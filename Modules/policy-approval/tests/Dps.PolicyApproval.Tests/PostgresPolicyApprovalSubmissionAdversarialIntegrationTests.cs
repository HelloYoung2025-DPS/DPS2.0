using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed partial class PostgresPolicyApprovalIntegrationTests
{
    private static readonly JsonSerializerOptions RawLifecycleJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    [Fact, Trait("Category", "Integration")]
    public async Task RawTransitionCredentialsCannotForgeEvidenceOrReuseOldAttemptAndLease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner);
        var (proposal, snapshot) = await IssueApprovedAsync(
            database,
            evaluationSigner,
            revocationSigner,
            authorityTopology,
            "raw-forged-recovery",
            cancellationToken);
        var request = FenceRequest(snapshot);
        var commandId = Guid.NewGuid();
        var firstIntent = SignSubmissionIntent(
            executorSigner,
            SubmissionIntent(snapshot, proposal, request, commandId: commandId));
        using (var executorClient = CreateSubmissionClient(
                   database,
                   authorityTopology,
                   fenceSigner,
                   executorSigner,
                   reconciliationSigner,
                   recoverySigner,
                   stateSigner))
        {
            var firstLease = await executorClient.AcquireAsync(
                request,
                SignFenceAuthorization(fenceSigner, request),
                firstIntent,
                cancellationToken);
            var pending = (await firstLease.BeginSubmissionAsync(firstIntent, cancellationToken)).PendingReceipt;
            var unknown = await firstLease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
            await firstLease.DisposeAsync();

            var forgedReconciliation = Reconciliation(firstIntent, pending);
            var reconciliationSha256 = ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(forgedReconciliation);
            var forgedReconciliationState = ForgedLifecycleState(
                firstIntent,
                ApprovalSubmissionStateV1.ReconciledNotSubmitted,
                unknown.StateSha256,
                reconciliationSha256);
            await using (var reconciliationConnection = new NpgsqlConnection(
                             database.SubmissionReconciliationOptions.ReconciliationConnectionString))
            {
                await reconciliationConnection.OpenAsync(cancellationToken);
                Assert.Equal(
                    "INSERTED",
                    await CallRawTransitionAsync(
                        reconciliationConnection,
                        database.SchemaName,
                        "reconcile_approval_submission",
                        forgedReconciliation,
                        reconciliationSha256,
                        forgedReconciliationState,
                        cancellationToken));
            }

            var nextAttemptId = Guid.NewGuid();
            var nextLeaseId = Guid.NewGuid();
            var nextAuthorizationSha256 = Sha256Hex("raw-forged-recovery-authorization:" + nextAttemptId);
            var nextNativeBindingSha256 = Sha256Hex("raw-forged-recovery-native:" + nextAttemptId);
            var forgedRecovery = Recovery(
                firstIntent,
                forgedReconciliation,
                nextAttemptId,
                nextLeaseId,
                nextAuthorizationSha256,
                nextNativeBindingSha256);

            await using (var recoveryConnection = new NpgsqlConnection(
                             database.SubmissionRecoveryOptions.RecoveryConnectionString))
            {
                await recoveryConnection.OpenAsync(cancellationToken);
                foreach (var invalidRecovery in new[]
                         {
                             forgedRecovery with { NextSubmissionAttemptId = firstIntent.SubmissionAttemptId },
                             forgedRecovery with { NextLeaseId = firstIntent.LeaseId }
                         })
                {
                    var invalidSha256 = Sha256Hex("invalid-raw-recovery:" + invalidRecovery.NextSubmissionAttemptId + ":" + invalidRecovery.NextLeaseId);
                    var invalidState = ForgedLifecycleState(
                        firstIntent,
                        ApprovalSubmissionStateV1.RecoveryAuthorized,
                        forgedReconciliationState.StateSha256,
                        invalidSha256);
                    var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                        CallRawTransitionAsync(
                            recoveryConnection,
                            database.SchemaName,
                            "recover_approval_submission",
                            invalidRecovery,
                            invalidSha256,
                            invalidState,
                            cancellationToken));
                    Assert.Equal("42501", exception.SqlState);
                }

                var recoverySha256 = ApprovalSubmissionLifecycleBinding.ComputeRecoverySha256(forgedRecovery);
                var forgedRecoveryState = ForgedLifecycleState(
                    firstIntent,
                    ApprovalSubmissionStateV1.RecoveryAuthorized,
                    forgedReconciliationState.StateSha256,
                    recoverySha256);
                Assert.Equal(
                    "INSERTED",
                    await CallRawTransitionAsync(
                        recoveryConnection,
                        database.SchemaName,
                        "recover_approval_submission",
                        forgedRecovery,
                        recoverySha256,
                        forgedRecoveryState,
                        cancellationToken));
            }

            var secondIntent = SignSubmissionIntent(
                executorSigner,
                SubmissionIntent(
                    snapshot,
                    proposal,
                    request,
                    submissionAttemptId: nextAttemptId,
                    commandId: commandId,
                    leaseId: nextLeaseId,
                    attempt: 2,
                    releaseBomGeneration: forgedRecovery.NextReleaseBomGeneration,
                    executionAuthorizationSha256: nextAuthorizationSha256,
                    nativeRequestBindingSha256: nextNativeBindingSha256));
            using var restarted = CreateSubmissionClient(
                database,
                authorityTopology,
                fenceSigner,
                executorSigner,
                reconciliationSigner,
                recoverySigner,
                stateSigner);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.AcquireAsync(
                request,
                SignFenceAuthorization(fenceSigner, request),
                secondIntent,
                cancellationToken));
        }

        await using var owner = new NpgsqlConnection(database.AdminConnectionString);
        await owner.OpenAsync(cancellationToken);
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM {database.SchemaName}.approval_submission_attempts",
            owner) { CommandTimeout = 5 };
        Assert.Equal(1L, await count.ExecuteScalarAsync(cancellationToken));
    }

    private static ApprovalSubmissionStateV1 ForgedLifecycleState(
        ApprovalSubmissionIntentV1 intent,
        string state,
        string predecessorStateSha256,
        string evidenceSha256)
    {
        var unsigned = new ApprovalSubmissionStateV1(
            ApprovalSubmissionStateV1.CurrentSchemaVersion,
            ApprovalSubmissionStateV1.CurrentContractId,
            ApprovalSubmissionStateV1.CurrentProducerModule,
            Guid.NewGuid(),
            intent.SubmissionAttemptId,
            intent.ApprovalId,
            intent.ProposalId,
            intent.CommandId,
            intent.LeaseId,
            intent.Attempt,
            intent.SoulId,
            intent.DeviceBindingId,
            intent.PlatformAccountId,
            intent.TraceId,
            intent.IdempotencyKey,
            intent.ReleaseBomSha256,
            intent.ReleaseBomGeneration,
            intent.NativeRequestBindingSha256,
            ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent),
            state,
            predecessorStateSha256,
            evidenceSha256,
            DateTimeOffset.UtcNow,
            "internal",
            new string('0', 64),
            Convert.ToBase64String(new byte[64]));
        return unsigned with
        {
            StateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(unsigned)
        };
    }

    private static async Task<string> CallRawTransitionAsync<TEnvelope>(
        NpgsqlConnection connection,
        string schemaName,
        string functionName,
        TEnvelope envelope,
        string envelopeSha256,
        ApprovalSubmissionStateV1 state,
        CancellationToken cancellationToken)
    {
        if (functionName is not ("reconcile_approval_submission" or "recover_approval_submission"))
            throw new ArgumentOutOfRangeException(nameof(functionName));
        await using var command = new NpgsqlCommand(
            $"SELECT {schemaName}.{functionName}(@envelope, @envelope_sha256, @state, @state_sha256)",
            connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue(
            "envelope",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(envelope, RawLifecycleJson));
        command.Parameters.AddWithValue("envelope_sha256", envelopeSha256);
        command.Parameters.AddWithValue(
            "state",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(state, RawLifecycleJson));
        command.Parameters.AddWithValue("state_sha256", state.StateSha256);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Raw lifecycle RPC returned no disposition.");
    }
}

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.ControlPlaneHost.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.PolicyApproval;

public enum PolicyApprovalSubmissionBeginDisposition
{
    Inserted,
    ExistingUnknownSubmission
}

public sealed record PolicyApprovalSubmissionBeginResult(
    PolicyApprovalSubmissionBeginDisposition Disposition,
    ApprovalSubmissionStateV1 PendingReceipt)
{
    public bool MaySubmit => Disposition == PolicyApprovalSubmissionBeginDisposition.Inserted;
}

public sealed record PolicyApprovalSubmissionSnapshot(
    ApprovalSubmissionIntentV1 Intent,
    ApprovalSubmissionStateV1 State,
    string PendingStateSha256)
{
    public bool RequiresReconciliation => State.State is
        ApprovalSubmissionStateV1.SubmissionPending or
        ApprovalSubmissionStateV1.UnknownSubmission;
}

public sealed class PolicyApprovalSubmissionCommitUncertainException : Exception
{
    public PolicyApprovalSubmissionCommitUncertainException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class PolicyApprovalSubmissionTerminalUncertainException : Exception
{
    public PolicyApprovalSubmissionTerminalUncertainException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class PolicyApprovalSubmissionConflictException : InvalidOperationException
{
    public PolicyApprovalSubmissionConflictException(string message) : base(message) { }
}

internal sealed class PostgresPolicyApprovalSubmissionAuthority : IDisposable
{
    private const string ZeroSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly string ZeroSignature = Convert.ToBase64String(new byte[64]);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _executorSignatureSync = new();
    private readonly object _transitionSignatureSync = new();
    private readonly object _stateSignatureSync = new();
    private readonly PolicyApprovalSubmissionAuthorityRuntime _runtime;
    private readonly ECDsa _executorAuthority = ECDsa.Create();
    private readonly ECDsa? _transitionAuthority;
    private readonly ECDsa? _reconciliationEvidenceAuthority;
    private readonly ECDsa? _recoveryEvidenceAuthority;
    private readonly ECDsa _stateSigner = ECDsa.Create();
    private readonly IActiveReleaseBindingReader? _activeReleaseBindingReader;

    private PostgresPolicyApprovalSubmissionAuthority(
        PolicyApprovalSubmissionAuthorityRuntime runtime,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorAuthorityPublicKey,
        ReadOnlySpan<byte> transitionAuthorityPublicKey,
        ReadOnlySpan<byte> reconciliationEvidencePublicKey,
        ReadOnlySpan<byte> recoveryEvidencePublicKey,
        ReadOnlySpan<byte> stateSigningPrivateKeyPkcs8,
        ECDsa? fenceAuthorizationAuthority,
        IActiveReleaseBindingReader? activeReleaseBindingReader)
    {
        _runtime = runtime;
        authorityTopology.Validate();
        if (runtime.Role == PolicyApprovalSubmissionDatabaseRole.Executor)
        {
            if (!transitionAuthorityPublicKey.IsEmpty
                || reconciliationEvidencePublicKey.IsEmpty
                || recoveryEvidencePublicKey.IsEmpty
                || fenceAuthorizationAuthority is null
                || activeReleaseBindingReader is not null)
                throw new ArgumentException("Executor composition requires read-only reconciliation/recovery evidence keys and accepts no transition authority or active release binding reader.");
        }
        else if (transitionAuthorityPublicKey.IsEmpty
                 || !reconciliationEvidencePublicKey.IsEmpty
                 || !recoveryEvidencePublicKey.IsEmpty
                 || fenceAuthorizationAuthority is not null)
        {
            throw new ArgumentException("Reconciliation and recovery compositions require only their exact transition authority.");
        }

        if (runtime.Role == PolicyApprovalSubmissionDatabaseRole.Recovery && activeReleaseBindingReader is null)
            throw new ArgumentException("Recovery composition requires the composition-fixed active release binding reader.", nameof(activeReleaseBindingReader));
        if (runtime.Role == PolicyApprovalSubmissionDatabaseRole.Reconciliation && activeReleaseBindingReader is not null)
            throw new ArgumentException("Reconciliation composition accepts no active release binding reader.", nameof(activeReleaseBindingReader));

        _activeReleaseBindingReader = activeReleaseBindingReader;

        _transitionAuthority = runtime.Role == PolicyApprovalSubmissionDatabaseRole.Executor
            ? null
            : ECDsa.Create();
        _reconciliationEvidenceAuthority = runtime.Role == PolicyApprovalSubmissionDatabaseRole.Executor
            ? ECDsa.Create()
            : null;
        _recoveryEvidenceAuthority = runtime.Role == PolicyApprovalSubmissionDatabaseRole.Executor
            ? ECDsa.Create()
            : null;
        try
        {
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(_executorAuthority, executorAuthorityPublicKey, nameof(executorAuthorityPublicKey));
            if (_transitionAuthority is not null)
                PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                    _transitionAuthority,
                    transitionAuthorityPublicKey,
                    nameof(transitionAuthorityPublicKey));
            if (_reconciliationEvidenceAuthority is not null)
                PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                    _reconciliationEvidenceAuthority,
                    reconciliationEvidencePublicKey,
                    nameof(reconciliationEvidencePublicKey));
            if (_recoveryEvidenceAuthority is not null)
                PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                    _recoveryEvidenceAuthority,
                    recoveryEvidencePublicKey,
                    nameof(recoveryEvidencePublicKey));
            ImportP256PrivateKey(_stateSigner, stateSigningPrivateKeyPkcs8, nameof(stateSigningPrivateKeyPkcs8));

            PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                _executorAuthority,
                authorityTopology.ExecutorSubmissionPublicKeySha256,
                "executor submission authority");
            PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                _stateSigner,
                authorityTopology.PolicyStateSigningPublicKeySha256,
                "policy submission-state signing authority");
            switch (runtime.Role)
            {
                case PolicyApprovalSubmissionDatabaseRole.Executor:
                    PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                        fenceAuthorizationAuthority!,
                        authorityTopology.FenceAuthorizationPublicKeySha256,
                        "fence authorization authority");
                    PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                        _reconciliationEvidenceAuthority!,
                        authorityTopology.ReconciliationPublicKeySha256,
                        "reconciliation evidence authority");
                    PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                        _recoveryEvidenceAuthority!,
                        authorityTopology.RecoveryPublicKeySha256,
                        "recovery evidence authority");
                    break;
                case PolicyApprovalSubmissionDatabaseRole.Reconciliation:
                    PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                        _transitionAuthority!,
                        authorityTopology.ReconciliationPublicKeySha256,
                        "independent reconciliation authority");
                    break;
                case PolicyApprovalSubmissionDatabaseRole.Recovery:
                    PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                        _transitionAuthority!,
                        authorityTopology.RecoveryPublicKeySha256,
                        "human recovery authority");
                    break;
                default:
                    throw new NotSupportedException($"Unsupported submission database role '{runtime.Role}'.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static PostgresPolicyApprovalSubmissionAuthority CreateExecutor(
        PolicyApprovalSubmissionAuthorityRuntime runtime,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorAuthorityPublicKey,
        ReadOnlySpan<byte> reconciliationEvidencePublicKey,
        ReadOnlySpan<byte> recoveryEvidencePublicKey,
        ReadOnlySpan<byte> stateSigningPrivateKeyPkcs8,
        ECDsa fenceAuthorizationAuthority)
    {
        RequireRuntimeRole(runtime, PolicyApprovalSubmissionDatabaseRole.Executor);
        return new PostgresPolicyApprovalSubmissionAuthority(
            runtime,
            authorityTopology,
            executorAuthorityPublicKey,
            ReadOnlySpan<byte>.Empty,
            reconciliationEvidencePublicKey,
            recoveryEvidencePublicKey,
            stateSigningPrivateKeyPkcs8,
            fenceAuthorizationAuthority,
            null);
    }

    internal static PostgresPolicyApprovalSubmissionAuthority CreateReconciliation(
        PolicyApprovalSubmissionAuthorityRuntime runtime,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorAuthorityPublicKey,
        ReadOnlySpan<byte> reconciliationAuthorityPublicKey,
        ReadOnlySpan<byte> stateSigningPrivateKeyPkcs8)
    {
        RequireRuntimeRole(runtime, PolicyApprovalSubmissionDatabaseRole.Reconciliation);
        return new PostgresPolicyApprovalSubmissionAuthority(
            runtime,
            authorityTopology,
            executorAuthorityPublicKey,
            reconciliationAuthorityPublicKey,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            stateSigningPrivateKeyPkcs8,
            null,
            null);
    }

    internal static PostgresPolicyApprovalSubmissionAuthority CreateRecovery(
        PolicyApprovalSubmissionAuthorityRuntime runtime,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorAuthorityPublicKey,
        ReadOnlySpan<byte> recoveryAuthorityPublicKey,
        ReadOnlySpan<byte> stateSigningPrivateKeyPkcs8,
        IActiveReleaseBindingReader activeReleaseBindingReader)
    {
        RequireRuntimeRole(runtime, PolicyApprovalSubmissionDatabaseRole.Recovery);
        ArgumentNullException.ThrowIfNull(activeReleaseBindingReader);
        return new PostgresPolicyApprovalSubmissionAuthority(
            runtime,
            authorityTopology,
            executorAuthorityPublicKey,
            recoveryAuthorityPublicKey,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            stateSigningPrivateKeyPkcs8,
            null,
            activeReleaseBindingReader);
    }

    internal void VerifyIntent(ApprovalSubmissionIntentV1 intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent.Validate();
        VerifySignature(_executorAuthority, _executorSignatureSync, ApprovalSubmissionLifecycleBinding.CanonicalIntentBytes(intent), intent.SignatureBase64, "submission intent");
    }

    internal void VerifyAcknowledgement(ApprovalSubmissionAcknowledgementV1 acknowledgement)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Executor);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        acknowledgement.Validate();
        VerifySignature(_executorAuthority, _executorSignatureSync, ApprovalSubmissionLifecycleBinding.CanonicalAcknowledgementBytes(acknowledgement), acknowledgement.SignatureBase64, "submission acknowledgement");
    }

    internal void VerifyReconciliation(ApprovalSubmissionReconciliationV1 reconciliation)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Reconciliation);
        VerifyReconciliationEvidence(reconciliation, _transitionAuthority!);
    }

    private void VerifyReconciliationEvidence(
        ApprovalSubmissionReconciliationV1 reconciliation,
        ECDsa authority)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        reconciliation.Validate();
        VerifySignature(authority, _transitionSignatureSync, ApprovalSubmissionLifecycleBinding.CanonicalReconciliationBytes(reconciliation), reconciliation.SignatureBase64, "independent reconciliation");
    }

    internal void VerifyRecovery(ApprovalSubmissionRecoveryV1 recovery)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Recovery);
        VerifyRecoveryEvidence(recovery, _transitionAuthority!);
    }

    private void VerifyRecoveryEvidence(
        ApprovalSubmissionRecoveryV1 recovery,
        ECDsa authority)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        recovery.Validate();
        VerifySignature(authority, _transitionSignatureSync, ApprovalSubmissionLifecycleBinding.CanonicalRecoveryBytes(recovery), recovery.SignatureBase64, "human recovery authorization");
    }

    internal static void ValidateIntentAgainstFence(
        ApprovalExecutionFenceRequestV1 request,
        ApprovalSubmissionIntentV1 intent)
    {
        var expectedFenceRequestSha256 = PolicyApprovalExecutionFenceBinding.ComputeRequestSha256(request);
        if (intent.ApprovalId != request.ApprovalId
            || intent.ProposalId != request.ProposalId
            || !string.Equals(intent.SoulId, request.SoulId, StringComparison.Ordinal)
            || !string.Equals(intent.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(intent.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(intent.TraceId, request.TraceId, StringComparison.Ordinal)
            || !string.Equals(intent.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
            || !FixedDigestEquals(intent.FenceRequestSha256, expectedFenceRequestSha256)
            || !FixedDigestEquals(intent.ApprovalSha256, request.ApprovalSha256)
            || intent.StatusRevision != request.ExpectedStatusRevision
            || intent.RuntimeRevision != request.ExpectedRuntimeRevision
            || !FixedDigestEquals(intent.RuntimeStateSha256, request.ExpectedRuntimeStateSha256)
            || !FixedDigestEquals(intent.ReleaseBomSha256, request.ExpectedReleaseBomSha256))
            throw new UnauthorizedAccessException("Submission intent is not bound to the exact approval execution fence request.");
    }

    internal async Task EnsureAcquirableWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ApprovalSubmissionIntentV1 intent,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Executor);
        await using var command = new NpgsqlCommand(
            $"""
            WITH exact_recovery AS
            (
                SELECT prior.intent_json::text AS prior_intent_json,
                       prior.pending_state_sha256,
                       COALESCE(quarantine.state_sha256, prior.pending_state_sha256) AS reconciliation_predecessor_sha256,
                       reconciliation.reconciliation_sha256,
                       reconciliation.reconciliation_json::text,
                       reconciliation.state_sha256 AS reconciliation_state_sha256,
                       reconciliation.state_json::text AS reconciliation_state_json,
                       recovery.recovery_sha256,
                       recovery.recovery_json::text,
                       recovery.state_sha256 AS recovery_state_sha256,
                       recovery.state_json::text AS recovery_state_json
                FROM {_runtime.SchemaName}.approval_submission_recoveries AS recovery
                JOIN {_runtime.SchemaName}.approval_submission_attempts AS prior
                  ON prior.submission_attempt_id = recovery.submission_attempt_id
                JOIN {_runtime.SchemaName}.approval_submission_reconciliations AS reconciliation
                  ON reconciliation.submission_attempt_id = prior.submission_attempt_id
                 AND reconciliation.reconciliation_id = recovery.reconciliation_id
                 AND reconciliation.reconciliation_sha256 = recovery.reconciliation_sha256
                LEFT JOIN {_runtime.SchemaName}.approval_submission_quarantines AS quarantine
                  ON quarantine.submission_attempt_id = prior.submission_attempt_id
                WHERE prior.approval_id = @approval_id
                  AND prior.proposal_id = @proposal_id
                  AND prior.command_id = @command_id
                  AND prior.soul_id = @soul_id
                  AND prior.device_binding_id = @device_binding_id
                  AND prior.platform_account_id = @platform_account_id
                  AND prior.trace_id = @trace_id
                  AND prior.idempotency_key = @idempotency_key
                  AND recovery.next_submission_attempt_id = @submission_attempt_id
                  AND recovery.next_lease_id = @lease_id
                  AND recovery.next_attempt = @attempt
                  AND recovery.next_release_bom_sha256 = @release_bom_sha256
                  AND recovery.next_release_bom_generation = @release_bom_generation
                  AND recovery.next_execution_authorization_sha256 = @execution_authorization_sha256
                  AND recovery.next_native_request_binding_sha256 = @native_request_binding_sha256
            )
            SELECT
                EXISTS (
                    SELECT 1 FROM {_runtime.SchemaName}.approval_submission_attempts AS attempt
                    WHERE attempt.submission_attempt_id = @submission_attempt_id),
                EXISTS (
                    SELECT 1 FROM {_runtime.SchemaName}.approval_submission_attempts AS attempt
                    WHERE attempt.approval_id = @approval_id OR attempt.command_id = @command_id),
                EXISTS (
                    SELECT 1
                    FROM {_runtime.SchemaName}.approval_submission_attempts AS attempt
                    JOIN {_runtime.SchemaName}.approval_submission_acknowledgements AS acknowledged USING (submission_attempt_id)
                    WHERE attempt.approval_id = @approval_id OR attempt.command_id = @command_id),
                exact_recovery.prior_intent_json,
                exact_recovery.pending_state_sha256,
                exact_recovery.reconciliation_predecessor_sha256,
                exact_recovery.reconciliation_sha256,
                exact_recovery.reconciliation_json,
                exact_recovery.reconciliation_state_sha256,
                exact_recovery.reconciliation_state_json,
                exact_recovery.recovery_sha256,
                exact_recovery.recovery_json,
                exact_recovery.recovery_state_sha256,
                exact_recovery.recovery_state_json,
                clock_timestamp()
            FROM (SELECT 1) AS singleton
            LEFT JOIN exact_recovery ON true
            """, connection, transaction) { CommandTimeout = 5 };
        AddIntentIdentityParameters(command, intent);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Submission lifecycle acquire guard returned no row.");
        var attemptAlreadyExists = reader.GetBoolean(0);
        var hasPrior = reader.GetBoolean(1);
        var acknowledged = reader.GetBoolean(2);
        var hasExactRecovery = !reader.IsDBNull(3);
        var databaseNow = reader.GetFieldValue<DateTimeOffset>(14).ToUniversalTime();
        if (attemptAlreadyExists
            || acknowledged
            || !hasPrior && intent.Attempt != 1
            || hasPrior && (intent.Attempt <= 1 || !hasExactRecovery))
            throw new UnauthorizedAccessException("The submission attempt already exists or its approval/command has no exact unused recovery authorization; acquire fails closed.");
        if (!hasPrior) return;

        var priorIntent = Deserialize<ApprovalSubmissionIntentV1>(reader.GetString(3), "prior submission intent");
        var pendingStateSha256 = reader.GetString(4);
        var reconciliationPredecessorSha256 = reader.GetString(5);
        var storedReconciliationSha256 = reader.GetString(6);
        var reconciliation = Deserialize<ApprovalSubmissionReconciliationV1>(
            reader.GetString(7),
            "stored submission reconciliation");
        var storedReconciliationStateSha256 = reader.GetString(8);
        var reconciliationState = Deserialize<ApprovalSubmissionStateV1>(
            reader.GetString(9),
            "stored reconciled submission state");
        var storedRecoverySha256 = reader.GetString(10);
        var recovery = Deserialize<ApprovalSubmissionRecoveryV1>(
            reader.GetString(11),
            "stored submission recovery authorization");
        var storedRecoveryStateSha256 = reader.GetString(12);
        var recoveryState = Deserialize<ApprovalSubmissionStateV1>(
            reader.GetString(13),
            "stored recovery-authorized submission state");
        await reader.DisposeAsync();

        VerifyIntent(priorIntent);
        VerifyReconciliationEvidence(reconciliation, _reconciliationEvidenceAuthority!);
        VerifyRecoveryEvidence(recovery, _recoveryEvidenceAuthority!);
        VerifyState(reconciliationState);
        VerifyState(recoveryState);

        var reconciliationSha256 = ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(reconciliation);
        var recoverySha256 = ApprovalSubmissionLifecycleBinding.ComputeRecoverySha256(recovery);
        var priorSnapshot = new PolicyApprovalSubmissionSnapshot(
            priorIntent,
            reconciliationState,
            pendingStateSha256);
        RequireReconciliationBinding(priorSnapshot, reconciliation);
        RequireRecoveryBinding(priorSnapshot, recovery);
        RequireStateBinding(
            priorIntent,
            reconciliationState,
            ApprovalSubmissionStateV1.ReconciledNotSubmitted,
            reconciliationPredecessorSha256,
            reconciliationSha256);
        RequireStateBinding(
            priorIntent,
            recoveryState,
            ApprovalSubmissionStateV1.RecoveryAuthorized,
            reconciliationState.StateSha256,
            recoverySha256);

        if (!FixedDigestEquals(storedReconciliationSha256, reconciliationSha256)
            || !FixedDigestEquals(storedReconciliationStateSha256, reconciliationState.StateSha256)
            || !FixedDigestEquals(storedRecoverySha256, recoverySha256)
            || !FixedDigestEquals(storedRecoveryStateSha256, recoveryState.StateSha256)
            || recovery.ReconciliationId != reconciliation.ReconciliationId
            || !FixedDigestEquals(recovery.ReconciliationSha256, reconciliationSha256)
            || recovery.NextSubmissionAttemptId != intent.SubmissionAttemptId
            || recovery.NextLeaseId != intent.LeaseId
            || recovery.NextAttempt != intent.Attempt
            || !FixedDigestEquals(recovery.NextReleaseBomSha256, intent.ReleaseBomSha256)
            || recovery.NextReleaseBomGeneration != intent.ReleaseBomGeneration
            || !FixedDigestEquals(recovery.NextExecutionAuthorizationSha256, intent.ExecutionAuthorizationSha256)
            || !FixedDigestEquals(recovery.NextNativeRequestBindingSha256, intent.NativeRequestBindingSha256)
            || recovery.ValidUntil <= databaseNow)
            throw new UnauthorizedAccessException("Stored reconciliation/recovery authorization is forged, stale, or not bound to the exact next submission intent.");
    }

    internal async Task<string> AppendPendingWithinFenceTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ApprovalExecutionFenceV1 fence,
        ApprovalSubmissionIntentV1 intent,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Executor);
        VerifyIntent(intent);
        var intentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent);
        var databaseNow = await ReadDatabaseNowAsync(connection, transaction, cancellationToken);
        var pending = CreateSignedState(intent, ApprovalSubmissionStateV1.SubmissionPending, null, intentSha256, databaseNow);
        var intentJson = Serialize(intent);
        var stateJson = Serialize(pending);
        await using var command = new NpgsqlCommand(
            $"SELECT {_runtime.SchemaName}.begin_approval_submission(@fence_id, @fence_valid_until, @intent, @intent_sha256, @state, @state_sha256)",
            connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("fence_id", fence.FenceId);
        command.Parameters.AddWithValue("fence_valid_until", fence.ValidUntil);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Jsonb, intentJson);
        command.Parameters.AddWithValue("intent_sha256", intentSha256);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Jsonb, stateJson);
        command.Parameters.AddWithValue("state_sha256", pending.StateSha256);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Submission begin function returned no disposition.");
    }

    internal async Task<PolicyApprovalSubmissionSnapshot> ReadSubmissionAsync(
        Guid submissionAttemptId,
        CancellationToken cancellationToken)
    {
        if (submissionAttemptId == Guid.Empty) throw new ArgumentException("Submission attempt ID cannot be empty.", nameof(submissionAttemptId));
        await using var connection = await OpenRoleAsync(cancellationToken);
        return await ReadSubmissionOnConnectionAsync(connection, null, submissionAttemptId, cancellationToken);
    }

    internal Task<PolicyApprovalSubmissionSnapshot> ReadSubmissionOnGuardConnectionAsync(
        NpgsqlConnection connection,
        Guid submissionAttemptId,
        CancellationToken cancellationToken)
        => ReadSubmissionOnConnectionAsync(connection, null, submissionAttemptId, cancellationToken);

    private async Task<PolicyApprovalSubmissionSnapshot> ReadSubmissionOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid submissionAttemptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (submissionAttemptId == Guid.Empty) throw new ArgumentException("Submission attempt ID cannot be empty.", nameof(submissionAttemptId));
        await using var command = new NpgsqlCommand(
            $"""
            SELECT attempt.intent_json::text,
                   COALESCE(
                       (SELECT acknowledged.state_json::text FROM {_runtime.SchemaName}.approval_submission_acknowledgements AS acknowledged WHERE acknowledged.submission_attempt_id = attempt.submission_attempt_id),
                       (SELECT recovery.state_json::text FROM {_runtime.SchemaName}.approval_submission_recoveries AS recovery WHERE recovery.submission_attempt_id = attempt.submission_attempt_id),
                       (SELECT reconciliation.state_json::text FROM {_runtime.SchemaName}.approval_submission_reconciliations AS reconciliation WHERE reconciliation.submission_attempt_id = attempt.submission_attempt_id),
                       (SELECT quarantine.state_json::text FROM {_runtime.SchemaName}.approval_submission_quarantines AS quarantine WHERE quarantine.submission_attempt_id = attempt.submission_attempt_id),
                       attempt.pending_state_json::text),
                   attempt.pending_state_sha256
            FROM {_runtime.SchemaName}.approval_submission_attempts AS attempt
            WHERE attempt.submission_attempt_id = @submission_attempt_id
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("submission_attempt_id", submissionAttemptId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new KeyNotFoundException("Submission attempt does not exist.");
        var intent = Deserialize<ApprovalSubmissionIntentV1>(reader.GetString(0), "submission intent");
        var state = Deserialize<ApprovalSubmissionStateV1>(reader.GetString(1), "submission state");
        var pendingStateSha256 = reader.GetString(2);
        VerifyIntent(intent);
        VerifyState(state);
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireSha256(
            pendingStateSha256,
            nameof(pendingStateSha256));
        if (state.SubmissionAttemptId != intent.SubmissionAttemptId
            || !FixedDigestEquals(state.SubmissionIntentSha256, ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent)))
            throw new InvalidDataException("Submission state readback is not bound to its exact intent.");
        if (state.State == ApprovalSubmissionStateV1.SubmissionPending
            && !FixedDigestEquals(state.StateSha256, pendingStateSha256))
            throw new InvalidDataException("Pending submission state readback is not bound to its stored commitment.");
        return new PolicyApprovalSubmissionSnapshot(intent, state, pendingStateSha256);
    }

    internal async Task<ApprovalSubmissionStateV1> AcknowledgeAsync(
        ApprovalSubmissionAcknowledgementV1 acknowledgement,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Executor);
        await using var connection = await OpenRoleAsync(cancellationToken);
        return await AcknowledgeOnGuardConnectionAsync(connection, acknowledgement, cancellationToken);
    }

    internal async Task<ApprovalSubmissionStateV1> AcknowledgeOnGuardConnectionAsync(
        NpgsqlConnection connection,
        ApprovalSubmissionAcknowledgementV1 acknowledgement,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Executor);
        ArgumentNullException.ThrowIfNull(connection);
        VerifyAcknowledgement(acknowledgement);
        var acknowledgementSha256 = ApprovalSubmissionLifecycleBinding.ComputeAcknowledgementSha256(acknowledgement);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var transactionDisposed = false;
        var terminalMayExist = false;
        try
        {
            await ConfigureTerminalTransactionAsync(connection, transaction, cancellationToken);
            var snapshot = await ReadSubmissionOnConnectionAsync(
                connection,
                transaction,
                acknowledgement.SubmissionAttemptId,
                cancellationToken);
            RequireAcknowledgementBinding(snapshot, acknowledgement);
            if (snapshot.State.State == ApprovalSubmissionStateV1.SubmissionAcknowledged)
            {
                if (!FixedDigestEquals(snapshot.State.EvidenceSha256, acknowledgementSha256))
                    throw new PolicyApprovalSubmissionConflictException("Submission attempt is acknowledged by another exact acknowledgement commitment.");
                terminalMayExist = true;
                await transaction.DisposeAsync();
                transactionDisposed = true;
                return snapshot.State;
            }
            if (snapshot.State.State is ApprovalSubmissionStateV1.ReconciledNotSubmitted or
                ApprovalSubmissionStateV1.ReconciledSubmitted or
                ApprovalSubmissionStateV1.RecoveryAuthorized)
                throw new UnauthorizedAccessException("A recovered submission attempt cannot later be acknowledged.");

            var databaseNow = await ReadDatabaseNowAsync(connection, transaction, cancellationToken);
            var state = CreateSignedState(snapshot.Intent, ApprovalSubmissionStateV1.SubmissionAcknowledged, snapshot.State.StateSha256, acknowledgementSha256, databaseNow);
            await using var command = LifecycleCommand(connection, transaction, "acknowledge_approval_submission", acknowledgement, acknowledgementSha256, state);
            _ = await command.ExecuteScalarAsync(cancellationToken);
            terminalMayExist = true;
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
            transactionDisposed = true;
            var persisted = await ReadSubmissionOnConnectionAsync(
                connection,
                null,
                acknowledgement.SubmissionAttemptId,
                cancellationToken);
            RequireStateBinding(
                snapshot.Intent,
                persisted.State,
                ApprovalSubmissionStateV1.SubmissionAcknowledged,
                snapshot.State.StateSha256,
                acknowledgementSha256);
            return persisted.State;
        }
        catch (Exception exception)
        {
            if (!transactionDisposed)
            {
                try
                {
                    await transaction.DisposeAsync();
                    transactionDisposed = true;
                }
                catch (Exception disposalException)
                {
                    throw new PolicyApprovalSubmissionTerminalUncertainException(
                        "ACK transaction disposal failed; terminal status cannot authorize a competing UNKNOWN transition.",
                        new AggregateException(exception, disposalException));
                }
            }
            if (terminalMayExist && exception is not PolicyApprovalSubmissionTerminalUncertainException)
                throw new PolicyApprovalSubmissionTerminalUncertainException(
                    "ACK commit was attempted or already existed, but its exact terminal lifecycle did not complete cleanly; competing UNKNOWN is forbidden.",
                    exception);
            throw;
        }
    }

    internal async Task<ApprovalSubmissionStateV1> QuarantineAsync(
        Guid submissionAttemptId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Executor);
        await using var connection = await OpenRoleAsync(cancellationToken);
        return await QuarantineOnGuardConnectionAsync(connection, submissionAttemptId, reasonCode, cancellationToken);
    }

    internal async Task<ApprovalSubmissionStateV1> QuarantineOnGuardConnectionAsync(
        NpgsqlConnection connection,
        Guid submissionAttemptId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Executor);
        ArgumentNullException.ThrowIfNull(connection);
        if (!IsAllowedQuarantineReason(reasonCode))
            throw new NotSupportedException($"Unsupported submission quarantine reason '{reasonCode}'.");
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var transactionDisposed = false;
        var terminalMayExist = false;
        try
        {
            await ConfigureTerminalTransactionAsync(connection, transaction, cancellationToken);
            var snapshot = await ReadSubmissionOnConnectionAsync(
                connection,
                transaction,
                submissionAttemptId,
                cancellationToken);
            var evidenceSha256 = ComputeQuarantineEvidenceSha256(
                submissionAttemptId,
                reasonCode,
                snapshot.PendingStateSha256);
            if (snapshot.State.State == ApprovalSubmissionStateV1.UnknownSubmission)
            {
                if (!FixedDigestEquals(snapshot.State.EvidenceSha256, evidenceSha256))
                    throw new PolicyApprovalSubmissionConflictException("Submission attempt is quarantined under another exact reason commitment.");
                RequireStateBinding(
                    snapshot.Intent,
                    snapshot.State,
                    ApprovalSubmissionStateV1.UnknownSubmission,
                    snapshot.PendingStateSha256,
                    evidenceSha256);
                terminalMayExist = true;
                await transaction.DisposeAsync();
                transactionDisposed = true;
                return snapshot.State;
            }
            if (snapshot.State.State != ApprovalSubmissionStateV1.SubmissionPending)
                throw new UnauthorizedAccessException("Only a durable pending attempt can become UNKNOWN_SUBMISSION.");
            var databaseNow = await ReadDatabaseNowAsync(connection, transaction, cancellationToken);
            var state = CreateSignedState(snapshot.Intent, ApprovalSubmissionStateV1.UnknownSubmission, snapshot.State.StateSha256, evidenceSha256, databaseNow);
            await using var command = new NpgsqlCommand(
                $"SELECT {_runtime.SchemaName}.quarantine_approval_submission(@quarantine_id, @submission_attempt_id, @reason_code, @evidence_sha256, @state, @state_sha256)",
                connection, transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("quarantine_id", Guid.NewGuid());
            command.Parameters.AddWithValue("submission_attempt_id", submissionAttemptId);
            command.Parameters.AddWithValue("reason_code", reasonCode);
            command.Parameters.AddWithValue("evidence_sha256", evidenceSha256);
            command.Parameters.AddWithValue("state", NpgsqlDbType.Jsonb, Serialize(state));
            command.Parameters.AddWithValue("state_sha256", state.StateSha256);
            _ = await command.ExecuteScalarAsync(cancellationToken);
            terminalMayExist = true;
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
            transactionDisposed = true;
            var persisted = await ReadSubmissionOnConnectionAsync(
                connection,
                null,
                submissionAttemptId,
                cancellationToken);
            RequireStateBinding(
                snapshot.Intent,
                persisted.State,
                ApprovalSubmissionStateV1.UnknownSubmission,
                snapshot.State.StateSha256,
                evidenceSha256);
            return persisted.State;
        }
        catch (Exception exception)
        {
            if (!transactionDisposed)
            {
                try
                {
                    await transaction.DisposeAsync();
                    transactionDisposed = true;
                }
                catch (Exception disposalException)
                {
                    throw new PolicyApprovalSubmissionTerminalUncertainException(
                        "UNKNOWN transaction disposal failed; terminal status cannot be proven cleanly.",
                        new AggregateException(exception, disposalException));
                }
            }
            if (terminalMayExist && exception is not PolicyApprovalSubmissionTerminalUncertainException)
                throw new PolicyApprovalSubmissionTerminalUncertainException(
                    "UNKNOWN commit was attempted or already existed, but its exact terminal lifecycle did not complete cleanly.",
                    exception);
            throw;
        }
    }

    internal static bool IsAllowedQuarantineReason(string reasonCode)
        => reasonCode is "NATIVE_SUBMISSION_UNCERTAIN" or "NATIVE_SUBMISSION_TIMEOUT" or
            "NATIVE_SUBMISSION_CANCELLED" or "NATIVE_SUBMISSION_NULL" or "NATIVE_SUBMISSION_ACK_INVALID" or
            "PROCESS_CRASH" or "AUTHORITY_TRANSITION_UNCERTAIN";

    internal async Task<ApprovalSubmissionStateV1> ReconcileAsync(
        ApprovalSubmissionReconciliationV1 reconciliation,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Reconciliation);
        VerifyReconciliation(reconciliation);
        var reconciliationSha256 = ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(reconciliation);
        var snapshot = await ReadSubmissionAsync(
            reconciliation.SubmissionAttemptId,
            cancellationToken);
        RequireReconciliationBinding(snapshot, reconciliation);
        if (snapshot.State.State is ApprovalSubmissionStateV1.ReconciledNotSubmitted or ApprovalSubmissionStateV1.ReconciledSubmitted)
        {
            if (FixedDigestEquals(snapshot.State.EvidenceSha256, reconciliationSha256)) return snapshot.State;
            throw new PolicyApprovalSubmissionConflictException("Submission attempt is reconciled by another exact reconciliation commitment.");
        }
        if (snapshot.State.State == ApprovalSubmissionStateV1.SubmissionAcknowledged)
            throw new UnauthorizedAccessException("An acknowledged submission cannot be reconciled as unknown.");
        if (snapshot.State.State == ApprovalSubmissionStateV1.RecoveryAuthorized)
            throw new UnauthorizedAccessException("A recovered old attempt cannot be reconciled again.");
        var stateName = reconciliation.Finding == ApprovalSubmissionReconciliationV1.ConfirmedNotSubmitted
            ? ApprovalSubmissionStateV1.ReconciledNotSubmitted
            : ApprovalSubmissionStateV1.ReconciledSubmitted;
        await using var connection = await OpenRoleAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var databaseNow = await ReadDatabaseNowAsync(connection, transaction, cancellationToken);
        var state = CreateSignedState(snapshot.Intent, stateName, snapshot.State.StateSha256, reconciliationSha256, databaseNow);
        await using var command = LifecycleCommand(connection, transaction, "reconcile_approval_submission", reconciliation, reconciliationSha256, state);
        _ = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var persisted = (await ReadSubmissionAsync(
            reconciliation.SubmissionAttemptId,
            cancellationToken)).State;
        if (persisted.State != stateName) throw new InvalidDataException("Reconciliation state did not survive exact readback.");
        return persisted;
    }

    internal async Task<ApprovalSubmissionStateV1> RecoverAsync(
        ApprovalSubmissionRecoveryV1 recovery,
        CancellationToken cancellationToken)
    {
        RequireRole(PolicyApprovalSubmissionDatabaseRole.Recovery);
        VerifyRecovery(recovery);
        var recoverySha256 = ApprovalSubmissionLifecycleBinding.ComputeRecoverySha256(recovery);
        var snapshot = await ReadSubmissionAsync(
            recovery.SubmissionAttemptId,
            cancellationToken);
        RequireRecoveryBinding(snapshot, recovery);
        if (snapshot.State.State == ApprovalSubmissionStateV1.RecoveryAuthorized)
        {
            if (FixedDigestEquals(snapshot.State.EvidenceSha256, recoverySha256)) return snapshot.State;
            throw new PolicyApprovalSubmissionConflictException("Submission attempt is recovered by another exact human authorization commitment.");
        }
        if (snapshot.State.State != ApprovalSubmissionStateV1.ReconciledNotSubmitted)
            throw new UnauthorizedAccessException("Recovery requires an independent CONFIRMED_NOT_SUBMITTED state.");
        await using var connection = await OpenRoleAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var databaseNow = await ReadDatabaseNowAsync(connection, transaction, cancellationToken);
        var state = CreateSignedState(snapshot.Intent, ApprovalSubmissionStateV1.RecoveryAuthorized, snapshot.State.StateSha256, recoverySha256, databaseNow);
        await using var command = LifecycleCommand(connection, transaction, "recover_approval_submission", recovery, recoverySha256, state);
        _ = await command.ExecuteScalarAsync(cancellationToken);
        RequireActiveReleaseBindingMatchesRecovery(recovery);
        await transaction.CommitAsync(cancellationToken);
        var persisted = (await ReadSubmissionAsync(
            recovery.SubmissionAttemptId,
            cancellationToken)).State;
        if (persisted.State != ApprovalSubmissionStateV1.RecoveryAuthorized)
            throw new InvalidDataException("Recovery authorization did not survive exact readback.");
        return persisted;
    }

    // Commit-time Release BOM fence (F2): the signed recovery envelope names
    // the next release BOM facts it was issued against, but the active binding
    // can change between control-plane-host's issuance fence commit and this
    // commit. Re-read the live active binding from the composition-fixed
    // control-plane-host reader at commit time and require the exact
    // (sha256, runtime generation) pair the envelope carries. An unreadable,
    // non-active, foreign-device, or mismatched binding fails closed: the
    // exception rolls the transaction back, so no recovery fact and no
    // partial write is ever persisted.
    private void RequireActiveReleaseBindingMatchesRecovery(ApprovalSubmissionRecoveryV1 recovery)
    {
        if (!_activeReleaseBindingReader!.TryReadActive(recovery.DeviceBindingId, out var binding)
            || binding is null
            || !string.Equals(binding.Status, "active", StringComparison.Ordinal)
            || !string.Equals(binding.DeviceBindingId, recovery.DeviceBindingId, StringComparison.Ordinal)
            || !FixedDigestEquals(binding.ReleaseBomSha256, recovery.NextReleaseBomSha256)
            || binding.Generation != recovery.NextReleaseBomGeneration)
            throw new UnauthorizedAccessException("Recovery next release BOM facts do not match the live active release binding; the recovery commit fails closed.");
    }

    private ApprovalSubmissionStateV1 CreateSignedState(
        ApprovalSubmissionIntentV1 intent,
        string state,
        string? predecessorStateSha256,
        string evidenceSha256,
        DateTimeOffset occurredAt)
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
            occurredAt,
            "internal",
            ZeroSha256,
            ZeroSignature);
        var stateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(unsigned);
        var withDigest = unsigned with { StateSha256 = stateSha256 };
        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalStateBytes(withDigest);
        byte[]? signature = null;
        try
        {
            lock (_stateSignatureSync)
                signature = _stateSigner.SignData(
                    canonical,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var signed = withDigest with { SignatureBase64 = Convert.ToBase64String(signature) };
            VerifyState(signed);
            return signed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private void VerifyState(ApprovalSubmissionStateV1 state)
    {
        state.Validate();
        var expected = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(state);
        if (!FixedDigestEquals(expected, state.StateSha256))
            throw new InvalidDataException("Policy submission state commitment is invalid.");
        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalStateBytes(state);
        try
        {
            VerifySignature(_stateSigner, _stateSignatureSync, canonical, state.SignatureBase64, "policy submission state");
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static void RequireAcknowledgementBinding(
        PolicyApprovalSubmissionSnapshot snapshot,
        ApprovalSubmissionAcknowledgementV1 acknowledgement)
    {
        var intent = snapshot.Intent;
        if (acknowledgement.ApprovalId != intent.ApprovalId || acknowledgement.ProposalId != intent.ProposalId
            || acknowledgement.CommandId != intent.CommandId || acknowledgement.LeaseId != intent.LeaseId
            || acknowledgement.Attempt != intent.Attempt
            || !string.Equals(acknowledgement.SoulId, intent.SoulId, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.DeviceBindingId, intent.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.PlatformAccountId, intent.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.TraceId, intent.TraceId, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.IdempotencyKey, intent.IdempotencyKey, StringComparison.Ordinal)
            || !FixedDigestEquals(acknowledgement.ReleaseBomSha256, intent.ReleaseBomSha256)
            || acknowledgement.ReleaseBomGeneration != intent.ReleaseBomGeneration
            || !FixedDigestEquals(acknowledgement.NativeRequestBindingSha256, intent.NativeRequestBindingSha256)
            || !FixedDigestEquals(acknowledgement.SubmissionIntentSha256, ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent))
            || !FixedDigestEquals(acknowledgement.PendingStateSha256, snapshot.PendingStateSha256))
            throw new UnauthorizedAccessException("Submission acknowledgement does not exactly consume the durable pending receipt.");
    }

    private static void RequireReconciliationBinding(
        PolicyApprovalSubmissionSnapshot snapshot,
        ApprovalSubmissionReconciliationV1 reconciliation)
    {
        var intent = snapshot.Intent;
        if (reconciliation.ApprovalId != intent.ApprovalId || reconciliation.ProposalId != intent.ProposalId
            || reconciliation.CommandId != intent.CommandId || reconciliation.LeaseId != intent.LeaseId
            || reconciliation.Attempt != intent.Attempt
            || !string.Equals(reconciliation.SoulId, intent.SoulId, StringComparison.Ordinal)
            || !string.Equals(reconciliation.DeviceBindingId, intent.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(reconciliation.PlatformAccountId, intent.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(reconciliation.TraceId, intent.TraceId, StringComparison.Ordinal)
            || !string.Equals(reconciliation.IdempotencyKey, intent.IdempotencyKey, StringComparison.Ordinal)
            || !FixedDigestEquals(reconciliation.SubmissionIntentSha256, ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent))
            || !FixedDigestEquals(reconciliation.PendingStateSha256, snapshot.PendingStateSha256))
            throw new UnauthorizedAccessException("Reconciliation receipt does not exactly bind the unknown submission attempt.");
    }

    private static void RequireRecoveryBinding(
        PolicyApprovalSubmissionSnapshot snapshot,
        ApprovalSubmissionRecoveryV1 recovery)
    {
        var intent = snapshot.Intent;
        if (recovery.ApprovalId != intent.ApprovalId || recovery.ProposalId != intent.ProposalId
            || recovery.CommandId != intent.CommandId || recovery.PreviousLeaseId != intent.LeaseId
            || recovery.PreviousAttempt != intent.Attempt
            || !string.Equals(recovery.SoulId, intent.SoulId, StringComparison.Ordinal)
            || !string.Equals(recovery.DeviceBindingId, intent.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(recovery.PlatformAccountId, intent.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(recovery.TraceId, intent.TraceId, StringComparison.Ordinal)
            || !string.Equals(recovery.IdempotencyKey, intent.IdempotencyKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Recovery authorization is not bound to the old submission attempt.");
    }

    private static void RequireStateBinding(
        ApprovalSubmissionIntentV1 intent,
        ApprovalSubmissionStateV1 state,
        string expectedState,
        string expectedPredecessorStateSha256,
        string expectedEvidenceSha256)
    {
        if (state.SubmissionAttemptId != intent.SubmissionAttemptId
            || state.ApprovalId != intent.ApprovalId
            || state.ProposalId != intent.ProposalId
            || state.CommandId != intent.CommandId
            || state.LeaseId != intent.LeaseId
            || state.Attempt != intent.Attempt
            || !string.Equals(state.SoulId, intent.SoulId, StringComparison.Ordinal)
            || !string.Equals(state.DeviceBindingId, intent.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(state.PlatformAccountId, intent.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(state.TraceId, intent.TraceId, StringComparison.Ordinal)
            || !string.Equals(state.IdempotencyKey, intent.IdempotencyKey, StringComparison.Ordinal)
            || !FixedDigestEquals(state.ReleaseBomSha256, intent.ReleaseBomSha256)
            || state.ReleaseBomGeneration != intent.ReleaseBomGeneration
            || !FixedDigestEquals(state.NativeRequestBindingSha256, intent.NativeRequestBindingSha256)
            || !FixedDigestEquals(
                state.SubmissionIntentSha256,
                ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent))
            || !string.Equals(state.State, expectedState, StringComparison.Ordinal)
            || state.PredecessorStateSha256 is null
            || !FixedDigestEquals(state.PredecessorStateSha256, expectedPredecessorStateSha256)
            || !FixedDigestEquals(state.EvidenceSha256, expectedEvidenceSha256))
            throw new UnauthorizedAccessException("Stored policy submission state is not bound to the exact signed transition chain.");
    }

    private NpgsqlCommand LifecycleCommand<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string function,
        T envelope,
        string envelopeSha256,
        ApprovalSubmissionStateV1 state)
    {
        var command = new NpgsqlCommand(
            $"SELECT {_runtime.SchemaName}.{function}(@envelope, @envelope_sha256, @state, @state_sha256)",
            connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("envelope", NpgsqlDbType.Jsonb, Serialize(envelope));
        command.Parameters.AddWithValue("envelope_sha256", envelopeSha256);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Jsonb, Serialize(state));
        command.Parameters.AddWithValue("state_sha256", state.StateSha256);
        return command;
    }

    private async Task<NpgsqlConnection> OpenRoleAsync(
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_runtime.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await PolicyApprovalSubmissionDatabaseRoleGuard.VerifyAsync(
                connection,
                _runtime.ExpectedRoleName,
                _runtime.SchemaName,
                _runtime.Role,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<DateTimeOffset> ReadDatabaseNowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction) { CommandTimeout = 5 };
        var value = (DateTime)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return its trusted clock."));
        return new DateTimeOffset(value, TimeSpan.Zero);
    }

    private static async Task ConfigureTerminalTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var transactionTimeout = new NpgsqlCommand(
            "SET LOCAL transaction_timeout = '3000ms'",
            connection,
            transaction) { CommandTimeout = 5 };
        await transactionTimeout.ExecuteNonQueryAsync(cancellationToken);
        await using var idleTimeout = new NpgsqlCommand(
            "SET LOCAL idle_in_transaction_session_timeout = '3000ms'",
            connection,
            transaction) { CommandTimeout = 5 };
        await idleTimeout.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddIntentIdentityParameters(NpgsqlCommand command, ApprovalSubmissionIntentV1 intent)
    {
        command.Parameters.AddWithValue("approval_id", intent.ApprovalId);
        command.Parameters.AddWithValue("proposal_id", intent.ProposalId);
        command.Parameters.AddWithValue("command_id", intent.CommandId);
        command.Parameters.AddWithValue("soul_id", intent.SoulId);
        command.Parameters.AddWithValue("device_binding_id", intent.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", intent.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", intent.TraceId);
        command.Parameters.AddWithValue("idempotency_key", intent.IdempotencyKey);
        command.Parameters.AddWithValue("submission_attempt_id", intent.SubmissionAttemptId);
        command.Parameters.AddWithValue("lease_id", intent.LeaseId);
        command.Parameters.AddWithValue("attempt", intent.Attempt);
        command.Parameters.AddWithValue("release_bom_sha256", intent.ReleaseBomSha256);
        command.Parameters.AddWithValue("release_bom_generation", intent.ReleaseBomGeneration);
        command.Parameters.AddWithValue("execution_authorization_sha256", intent.ExecutionAuthorizationSha256);
        command.Parameters.AddWithValue("native_request_binding_sha256", intent.NativeRequestBindingSha256);
    }

    private void RequireRole(PolicyApprovalSubmissionDatabaseRole expectedRole)
        => RequireRuntimeRole(_runtime, expectedRole);

    private static void RequireRuntimeRole(
        PolicyApprovalSubmissionAuthorityRuntime runtime,
        PolicyApprovalSubmissionDatabaseRole expectedRole)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Role != expectedRole)
            throw new UnauthorizedAccessException(
                $"Submission authority for '{runtime.Role}' cannot perform '{expectedRole}' operations.");
    }

    private static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var payload = value switch
        {
            ApprovalSubmissionIntentV1 intent => ApprovalSubmissionIntentV1Codec.Serialize(intent),
            ApprovalSubmissionAcknowledgementV1 acknowledgement => ApprovalSubmissionAcknowledgementV1Codec.Serialize(acknowledgement),
            ApprovalSubmissionReconciliationV1 reconciliation => ApprovalSubmissionReconciliationV1Codec.Serialize(reconciliation),
            ApprovalSubmissionRecoveryV1 recovery => ApprovalSubmissionRecoveryV1Codec.Serialize(recovery),
            ApprovalSubmissionStateV1 state => ApprovalSubmissionStateV1Codec.Serialize(state),
            _ => throw new NotSupportedException(
                $"Unsupported submission lifecycle contract type '{typeof(T).FullName}'.")
        };
        try
        {
            return StrictUtf8.GetString(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static T Deserialize<T>(string json, string name)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] payload;
        try
        {
            payload = StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"{name} readback is not strict UTF-8.", exception);
        }
        try
        {
            object value = typeof(T) switch
            {
                var type when type == typeof(ApprovalSubmissionIntentV1) => ApprovalSubmissionIntentV1Codec.DeserializeSemanticJsonb(payload),
                var type when type == typeof(ApprovalSubmissionAcknowledgementV1) => ApprovalSubmissionAcknowledgementV1Codec.DeserializeSemanticJsonb(payload),
                var type when type == typeof(ApprovalSubmissionReconciliationV1) => ApprovalSubmissionReconciliationV1Codec.DeserializeSemanticJsonb(payload),
                var type when type == typeof(ApprovalSubmissionRecoveryV1) => ApprovalSubmissionRecoveryV1Codec.DeserializeSemanticJsonb(payload),
                var type when type == typeof(ApprovalSubmissionStateV1) => ApprovalSubmissionStateV1Codec.DeserializeSemanticJsonb(payload),
                _ => throw new NotSupportedException(
                    $"Unsupported submission lifecycle contract type '{typeof(T).FullName}'.")
            };
            return (T)value;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            throw new InvalidDataException($"{name} readback is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void VerifySignature(ECDsa key, object sync, byte[] canonical, string signatureBase64, string name)
    {
        byte[]? signature = null;
        try
        {
            signature = PolicyEcdsaGuard.DecodeCanonicalP1363Signature(signatureBase64);
            bool valid;
            lock (sync)
                valid = key.VerifyData(canonical, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!valid) throw new UnauthorizedAccessException($"{name} signature verification failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static void ImportP256PrivateKey(ECDsa key, ReadOnlySpan<byte> privateKeyPkcs8, string parameterName)
    {
        if (privateKeyPkcs8.Length is < 1 or > 1024)
            throw new ArgumentException("Policy state signing key must be bounded PKCS#8 P-256 material.", parameterName);
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
        if (bytesRead != privateKeyPkcs8.Length || key.KeySize != 256)
            throw new ArgumentException("Policy state signing key must be exact P-256 PKCS#8 material.", parameterName);
        var parameters = key.ExportParameters(false);
        if (!string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
            throw new ArgumentException("Policy state signing key must use NIST P-256.", parameterName);
    }

    private static string ComputeQuarantineEvidenceSha256(Guid attemptId, string reasonCode, string pendingStateSha256)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "dps.policy-approval.unknown-submission/v1",
            submission_attempt_id = attemptId.ToString("D"),
            reason_code = reasonCode,
            pending_state_sha256 = pendingStateSha256
        });
        try { return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static bool FixedDigestEquals(string left, string right)
    {
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException) { return false; }
        finally
        {
            if (leftBytes is not null) CryptographicOperations.ZeroMemory(leftBytes);
            if (rightBytes is not null) CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    public void Dispose()
    {
        _executorAuthority.Dispose();
        _transitionAuthority?.Dispose();
        _reconciliationEvidenceAuthority?.Dispose();
        _recoveryEvidenceAuthority?.Dispose();
        _stateSigner.Dispose();
    }
}

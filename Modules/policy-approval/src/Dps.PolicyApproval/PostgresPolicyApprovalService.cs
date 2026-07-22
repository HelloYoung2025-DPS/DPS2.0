using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.PolicyApproval;

public enum PolicyApprovalMutationStage
{
    DecisionWritten,
    RateConsumed,
    StatusRevisionWritten,
    ReceiptWritten,
    OutboxWritten,
    QuarantineWritten,
    BeforeCommit
}

public enum PolicyApprovalAppendDisposition
{
    Inserted,
    DuplicateNoOp
}

public delegate ValueTask PolicyApprovalMutationFaultInjector(
    PolicyApprovalMutationStage stage,
    CancellationToken cancellationToken);

public sealed class PolicyApprovalIdempotencyConflictException : InvalidOperationException
{
    public PolicyApprovalIdempotencyConflictException()
        : base("The scoped idempotency key is bound to a different policy-approval mutation; hashes were quarantined.")
    {
    }
}

public sealed record PolicyApprovalReadRequest(
    Guid ApprovalId,
    Guid ProposalId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string ApprovalSha256)
{
    public void Validate()
    {
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireGuid(ApprovalId, nameof(ApprovalId));
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireGuid(ProposalId, nameof(ProposalId));
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireTraceId(TraceId);
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireIdempotencyKey(IdempotencyKey);
        PolicyCanonicalization.RequireStrictUtf8(TraceId, IdempotencyKey);
        PolicyCanonicalization.RequireSha256(ApprovalSha256, nameof(ApprovalSha256));
    }
}

public sealed class PolicyApprovalAuthoritativeSnapshot
{
    internal PolicyApprovalAuthoritativeSnapshot(
        ApprovalDecisionV1 approval,
        string canonicalSha256,
        string status,
        long statusRevision,
        DateTimeOffset validUntil,
        long runtimeRevision,
        string runtimeStateSha256,
        string releaseBomSha256)
    {
        Approval = PolicyCanonicalization.SnapshotDecision(approval);
        PolicyCanonicalization.RequireSha256(canonicalSha256, nameof(canonicalSha256));
        if (!FixedDigestEquals(canonicalSha256, PolicyApprovalDecisionCanonical.ComputeSha256(Approval)))
            throw new UnauthorizedAccessException("The stored approval snapshot digest does not match its immutable payload.");
        if (status is not (Active or Revoked)) throw new UnauthorizedAccessException("Unknown approval status fails closed.");
        if (statusRevision <= 0) throw new UnauthorizedAccessException("Approval status revision must be positive.");
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireUtc(validUntil, nameof(validUntil));
        if (validUntil <= Approval.OccurredAt)
            throw new UnauthorizedAccessException("Approval validity must end after the decision occurrence time.");
        if (runtimeRevision <= 0) throw new UnauthorizedAccessException("Approval runtime revision must be positive.");
        PolicyCanonicalization.RequireSha256(runtimeStateSha256, nameof(runtimeStateSha256));
        PolicyCanonicalization.RequireSha256(releaseBomSha256, nameof(releaseBomSha256));
        CanonicalSha256 = canonicalSha256;
        Status = status;
        StatusRevision = statusRevision;
        ValidUntil = validUntil;
        RuntimeRevision = runtimeRevision;
        RuntimeStateSha256 = runtimeStateSha256;
        ReleaseBomSha256 = releaseBomSha256;
    }

    public const string Active = "ACTIVE";
    public const string Revoked = "REVOKED";
    public ApprovalDecisionV1 Approval { get; }
    public string CanonicalSha256 { get; }
    public string Status { get; }
    public long StatusRevision { get; }
    public DateTimeOffset ValidUntil { get; }
    public long RuntimeRevision { get; }
    public string RuntimeStateSha256 { get; }
    public string ReleaseBomSha256 { get; }

    private static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}

public sealed record PolicyApprovalAppendResult(
    PolicyApprovalAppendDisposition Disposition,
    PolicyApprovalAuthoritativeSnapshot Snapshot);

public sealed record PolicyApprovalRevocationRequest(
    Guid ApprovalId,
    Guid ProposalId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string ApprovalSha256,
    long ExpectedStatusRevision)
{
    public void Validate()
    {
        new PolicyApprovalReadRequest(
            ApprovalId, ProposalId, SoulId, DeviceBindingId, PlatformAccountId,
            TraceId, IdempotencyKey, ApprovalSha256).Validate();
        if (ExpectedStatusRevision <= 0) throw new ArgumentOutOfRangeException(nameof(ExpectedStatusRevision));
    }
}

public sealed record PolicyApprovalRevocationEnvelope(
    string CallerModule,
    string AuthScope,
    Guid ApprovalId,
    string RevocationSha256,
    string ReleaseBomSha256,
    DateTimeOffset ValidUntil,
    string SignatureBase64);

public static class PolicyApprovalRevocationBinding
{
    public static string ComputeSha256(PolicyApprovalRevocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.revocation-request-sha256/v1");
            writer.Field(request.ApprovalId);
            writer.Field(request.ProposalId);
            writer.Field(request.SoulId);
            writer.Field(request.DeviceBindingId);
            writer.Field(request.PlatformAccountId);
            writer.Field(request.TraceId);
            writer.Field(request.IdempotencyKey);
            writer.Field(request.ApprovalSha256);
            writer.Field(request.ExpectedStatusRevision);
        });
    }
}

public sealed class EcdsaPolicyRevocationAuthorizer : IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _publicKey;

    public EcdsaPolicyRevocationAuthorizer(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        _publicKey = ECDsa.Create();
        try
        {
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                _publicKey, subjectPublicKeyInfo, nameof(subjectPublicKeyInfo));
        }
        catch
        {
            _publicKey.Dispose();
            throw;
        }
    }

    public void Verify(PolicyApprovalRevocationRequest request, PolicyApprovalRevocationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);
        request.Validate();
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireUtc(envelope.ValidUntil, nameof(envelope.ValidUntil));
        PolicyCanonicalization.RequireSha256(envelope.RevocationSha256, nameof(envelope.RevocationSha256));
        PolicyCanonicalization.RequireSha256(envelope.ReleaseBomSha256, nameof(envelope.ReleaseBomSha256));
        if (!string.Equals(envelope.CallerModule, "control-plane-host", StringComparison.Ordinal)
            || !string.Equals(envelope.AuthScope, "policy:revoke", StringComparison.Ordinal)
            || envelope.ApprovalId != request.ApprovalId
            || !FixedDigestEquals(envelope.RevocationSha256, PolicyApprovalRevocationBinding.ComputeSha256(request)))
            throw new UnauthorizedAccessException("Revocation envelope scope or commitment is invalid.");

        byte[]? signature = null;
        byte[]? canonical = null;
        try
        {
            signature = PolicyEcdsaGuard.DecodeCanonicalP1363Signature(envelope.SignatureBase64);
            canonical = CanonicalBytes(envelope);
            bool valid;
            lock (_sync)
                valid = _publicKey.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!valid) throw new UnauthorizedAccessException("Revocation signature verification failed.");
        }
        finally
        {
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (canonical is not null) CryptographicOperations.ZeroMemory(canonical);
        }
    }

    public static byte[] CanonicalBytes(PolicyApprovalRevocationEnvelope envelope)
        => PolicyCanonicalHash.Bytes(writer =>
        {
            writer.Field("dps.policy-approval.revocation-envelope/v1");
            writer.Field(envelope.CallerModule);
            writer.Field(envelope.AuthScope);
            writer.Field(envelope.ApprovalId);
            writer.Field(envelope.RevocationSha256);
            writer.Field(envelope.ReleaseBomSha256);
            writer.Field(envelope.ValidUntil);
        });

    private static bool FixedDigestEquals(string left, string right)
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

    public void Dispose() => _publicKey.Dispose();
}

public sealed class PolicyApprovalAuthoritativeClient
{
    private readonly PostgresPolicyApprovalOptions _options;
    private readonly string _connectionString;

    public PolicyApprovalAuthoritativeClient(PostgresPolicyApprovalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.BuildBoundedConnectionString();
        _options = options;
    }

    public PolicyApprovalAuthoritativeSnapshot Read(PolicyApprovalReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        PolicyApprovalDatabaseRoleGuard.VerifyRuntime(
            connection, _options.ExpectedRuntimeRoleName, _options.SchemaName);
        using var command = BuildReadCommand(connection, null, request);
        using var reader = command.ExecuteReader(CommandBehavior.SingleRow);
        if (!reader.Read()) throw new UnauthorizedAccessException("No exact authoritative approval snapshot exists.");
        return Materialize(reader, request);
    }

    public async Task<PolicyApprovalAuthoritativeSnapshot> ReadAsync(
        PolicyApprovalReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await PolicyApprovalDatabaseRoleGuard.VerifyRuntimeAsync(
            connection, _options.ExpectedRuntimeRoleName, _options.SchemaName, cancellationToken);
        await using var command = BuildReadCommand(connection, null, request);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new UnauthorizedAccessException("No exact authoritative approval snapshot exists.");
        return Materialize(reader, request);
    }

    internal NpgsqlCommand BuildReadCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PolicyApprovalReadRequest request)
    {
        var command = new NpgsqlCommand(
            $"""
            SELECT decision_json::text, decision_sha256, current_status.status,
                   current_status.revision, decision.valid_until,
                   decision.runtime_revision, decision.runtime_state_sha256,
                   decision.release_bom_sha256
            FROM {_options.SchemaName}.approval_decisions AS decision
            JOIN LATERAL
            (
                SELECT status, revision
                FROM {_options.SchemaName}.approval_status_revisions
                WHERE approval_id = decision.approval_id
                ORDER BY revision DESC
                LIMIT 1
            ) AS current_status ON true
            WHERE decision.approval_id = @approval_id
              AND decision.proposal_id = @proposal_id
              AND decision.soul_id = @soul_id
              AND decision.device_binding_id = @device_binding_id
              AND decision.platform_account_id = @platform_account_id
              AND decision.trace_id = @trace_id
              AND decision.idempotency_key = @idempotency_key
              AND decision.decision_sha256 = @decision_sha256
            """,
            connection,
            transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("approval_id", request.ApprovalId);
        command.Parameters.AddWithValue("proposal_id", request.ProposalId);
        command.Parameters.AddWithValue("soul_id", request.SoulId);
        command.Parameters.AddWithValue("device_binding_id", request.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", request.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", request.TraceId);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("decision_sha256", request.ApprovalSha256);
        return command;
    }

    internal static PolicyApprovalAuthoritativeSnapshot Materialize(
        NpgsqlDataReader reader,
        PolicyApprovalReadRequest request)
    {
        var storedJson = Encoding.UTF8.GetBytes(reader.GetString(0));
        ApprovalDecisionV1 decision;
        try
        {
            decision = ApprovalDecisionV1Codec.DeserializeSemanticJsonb(storedJson);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or JsonException
                or NotSupportedException)
        {
            throw new UnauthorizedAccessException(
                "Stored approval JSON is not an exact approval.decision/v1 payload.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(storedJson);
        }
        var snapshot = new PolicyApprovalAuthoritativeSnapshot(
            decision,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7));
        if (snapshot.Approval.ApprovalId != request.ApprovalId
            || snapshot.Approval.ProposalId != request.ProposalId
            || !string.Equals(snapshot.Approval.SoulId, request.SoulId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Approval.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Approval.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Approval.TraceId, request.TraceId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Approval.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Stored approval payload escaped the exact requested scope.");
        return snapshot;
    }
}

public sealed class PostgresPolicyApprovalService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly PostgresPolicyApprovalOptions _options;
    private readonly string _connectionString;
    private readonly byte[] _evaluationPublicKey;
    private readonly byte[] _promotionPublicKey;
    private readonly EcdsaPolicyRevocationAuthorizer _revocationAuthorizer;
    private readonly PolicyApprovalAuthoritativeClient _reader;
    private readonly PolicyApprovalMutationFaultInjector? _faultInjector;

    private PostgresPolicyApprovalService(
        PostgresPolicyApprovalOptions options,
        byte[] evaluationPublicKey,
        byte[] promotionPublicKey,
        EcdsaPolicyRevocationAuthorizer revocationAuthorizer,
        PolicyApprovalMutationFaultInjector? faultInjector)
    {
        _options = options;
        _connectionString = options.BuildBoundedConnectionString();
        _evaluationPublicKey = evaluationPublicKey;
        _promotionPublicKey = promotionPublicKey;
        _revocationAuthorizer = revocationAuthorizer;
        _reader = new PolicyApprovalAuthoritativeClient(options);
        _faultInjector = faultInjector;
    }

    public static PostgresPolicyApprovalService CreateProduction(
        PostgresPolicyApprovalOptions options,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> evaluationPublicKey,
        ReadOnlySpan<byte> promotionPublicKey,
        ReadOnlySpan<byte> revocationPublicKey,
        PolicyApprovalMutationFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorityTopology);
        options.Validate();
        authorityTopology.Validate();
        byte[]? ownedEvaluationPublicKey = null;
        byte[]? ownedPromotionPublicKey = null;
        EcdsaPolicyRevocationAuthorizer? revocationAuthorizer = null;
        try
        {
            using var evaluationValidator = ECDsa.Create();
            using var promotionValidator = ECDsa.Create();
            using var revocationValidator = ECDsa.Create();
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                evaluationValidator,
                evaluationPublicKey,
                nameof(evaluationPublicKey));
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                promotionValidator,
                promotionPublicKey,
                nameof(promotionPublicKey));
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                revocationValidator,
                revocationPublicKey,
                nameof(revocationPublicKey));
            PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                evaluationValidator,
                authorityTopology.EvaluationPublicKeySha256,
                "policy evaluation authority");
            PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                promotionValidator,
                authorityTopology.PromotionPublicKeySha256,
                "execution promotion authority");
            PolicyApprovalSubmissionCompositionGuard.RequireExpectedPublicKey(
                revocationValidator,
                authorityTopology.RevocationPublicKeySha256,
                "policy revocation authority");
            PolicyEcdsaGuard.RequireDistinctPublicKeys(
                evaluationValidator,
                nameof(evaluationPublicKey),
                promotionValidator,
                nameof(promotionPublicKey));
            PolicyEcdsaGuard.RequireDistinctPublicKeys(
                evaluationValidator,
                nameof(evaluationPublicKey),
                revocationValidator,
                nameof(revocationPublicKey));
            PolicyEcdsaGuard.RequireDistinctPublicKeys(
                promotionValidator,
                nameof(promotionPublicKey),
                revocationValidator,
                nameof(revocationPublicKey));
            ownedEvaluationPublicKey = evaluationPublicKey.ToArray();
            ownedPromotionPublicKey = promotionPublicKey.ToArray();
            revocationAuthorizer = new EcdsaPolicyRevocationAuthorizer(revocationPublicKey);
            return new PostgresPolicyApprovalService(
                options,
                ownedEvaluationPublicKey,
                ownedPromotionPublicKey,
                revocationAuthorizer,
                faultInjector);
        }
        catch
        {
            if (ownedEvaluationPublicKey is not null)
                CryptographicOperations.ZeroMemory(ownedEvaluationPublicKey);
            if (ownedPromotionPublicKey is not null)
                CryptographicOperations.ZeroMemory(ownedPromotionPublicKey);
            revocationAuthorizer?.Dispose();
            throw;
        }
    }

    public async Task<PolicyApprovalAppendResult> EvaluateAndAppendAsync(
        ActionProposalV1 proposal,
        PolicyEvaluationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        proposal = PolicyCanonicalization.SnapshotProposal(proposal);
        var proposalSha256 = PolicyAuthorizationBinding.ComputeProposalSha256(proposal);
        var commandSha256 = ComputeDecisionRequestSha256(proposal, envelope, proposalSha256);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await PolicyApprovalDatabaseRoleGuard.VerifyRuntimeAsync(
            connection, _options.ExpectedRuntimeRoleName, _options.SchemaName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, ScopeLock(proposal.SoulId, proposal.DeviceBindingId, proposal.PlatformAccountId, proposal.IdempotencyKey), cancellationToken);

        var existing = await ReadReceiptAsync(
            connection, transaction, proposal.SoulId, proposal.DeviceBindingId,
            proposal.PlatformAccountId, proposal.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!FixedDigestEquals(existing.Value.CommandSha256, commandSha256))
            {
                await InsertQuarantineAsync(
                    connection, transaction, proposal.SoulId, proposal.DeviceBindingId,
                    proposal.PlatformAccountId, proposal.IdempotencyKey, "decision",
                    existing.Value.CommandSha256, commandSha256, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await InjectAsync(PolicyApprovalMutationStage.QuarantineWritten, cancellationToken);
                throw new PolicyApprovalIdempotencyConflictException();
            }
            var duplicate = await ReadSnapshotAsync(
                connection,
                transaction,
                new PolicyApprovalReadRequest(
                    existing.Value.ApprovalId, proposal.ProposalId, proposal.SoulId,
                    proposal.DeviceBindingId, proposal.PlatformAccountId, proposal.TraceId,
                    proposal.IdempotencyKey, existing.Value.DecisionSha256),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PolicyApprovalAppendResult(PolicyApprovalAppendDisposition.DuplicateNoOp, duplicate);
        }

        // Policy-state INSERT uses the same database advisory lock via a migration trigger.
        // This closes the kill-switch/rate/authorization race between trusted read and commit.
        await AcquireLockAsync(
            connection,
            transaction,
            PolicyRuntimeLock(proposal.SoulId, proposal.DeviceBindingId, proposal.PlatformAccountId),
            cancellationToken);
        var stateSource = new PostgresPolicyRuntimeStateSource(_options, connection, transaction);
        using var trustProvider = new EcdsaPolicyTrustProvider(
            _evaluationPublicKey,
            _promotionPublicKey,
            stateSource);
        var evaluator = new DeterministicPolicyEvaluator(trustProvider);
        var evaluation = await evaluator.EvaluateVerifiedAsync(proposal, envelope, cancellationToken);
        var decision = evaluation.Decision;
        var decisionSha256 = PolicyApprovalDecisionCanonical.ComputeSha256(decision);
        var decisionPayload = ApprovalDecisionV1Codec.Serialize(decision);
        string decisionJson;
        try
        {
            decisionJson = Encoding.UTF8.GetString(decisionPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decisionPayload);
        }
        var approvalValidUntil = evaluation.Context.AuthorizationValidUntil <= evaluation.Context.RuntimeValidUntil
            ? evaluation.Context.AuthorizationValidUntil
            : evaluation.Context.RuntimeValidUntil;
        await InsertDecisionAsync(
            connection, transaction, decision, proposalSha256, decisionSha256,
            commandSha256, evaluation.Context.TrustEvidenceSha256,
            evaluation.Context.RuntimeRevision, evaluation.Context.RuntimeStateSha256,
            evaluation.Context.ReleaseBomSha256, approvalValidUntil, decisionJson, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.DecisionWritten, cancellationToken);
        if (decision.Decision == ApprovalDecisionV1.Approved)
        {
            if (evaluation.Context.RemainingRateBudget <= 0
                || evaluation.Context.RuntimeRevision <= 0)
                throw new UnauthorizedAccessException("An approved decision is missing an authoritative rate-budget reservation.");
            await InsertRateConsumptionAsync(
                connection,
                transaction,
                decision,
                evaluation.Context.RuntimeRevision,
                evaluation.Context.RuntimeStateSha256,
                cancellationToken);
            await InjectAsync(PolicyApprovalMutationStage.RateConsumed, cancellationToken);
        }
        var issuedReasonSha256 = PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.status-reason/v1");
            writer.Field(decision.ApprovalId);
            writer.Field(1L);
            writer.Field(PolicyApprovalAuthoritativeSnapshot.Active);
            writer.Field("ISSUED");
        });
        await InsertStatusAsync(
            connection, transaction, decision.ApprovalId, 1, PolicyApprovalAuthoritativeSnapshot.Active,
            "ISSUED", issuedReasonSha256, decision.TraceId, decision.IdempotencyKey, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.StatusRevisionWritten, cancellationToken);
        var resultJson = JsonSerializer.Serialize(new
        {
            approval_id = decision.ApprovalId,
            decision_sha256 = decisionSha256,
            status = PolicyApprovalAuthoritativeSnapshot.Active,
            status_revision = 1
        });
        await InsertReceiptAsync(
            connection, transaction, decision.SoulId, decision.DeviceBindingId,
            decision.PlatformAccountId, decision.IdempotencyKey, "decision", commandSha256,
            decision.ApprovalId, decisionSha256, 1, resultJson, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.ReceiptWritten, cancellationToken);
        await InsertOutboxAsync(
            connection, transaction, decision.ApprovalId, 1, decision.SoulId,
            decision.DeviceBindingId, decision.PlatformAccountId, decision.TraceId,
            decision.IdempotencyKey, "approval.decision/v1", decisionSha256,
            decisionJson, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.OutboxWritten, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.BeforeCommit, cancellationToken);
        await EnsureStillValidAsync(
            connection,
            transaction,
            evaluation.Context.AuthorizationValidUntil,
            evaluation.Context.RuntimeValidUntil,
            "Policy evaluation",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PolicyApprovalAppendResult(
            PolicyApprovalAppendDisposition.Inserted,
            new PolicyApprovalAuthoritativeSnapshot(
                decision,
                decisionSha256,
                PolicyApprovalAuthoritativeSnapshot.Active,
                1,
                approvalValidUntil,
                evaluation.Context.RuntimeRevision,
                evaluation.Context.RuntimeStateSha256,
                evaluation.Context.ReleaseBomSha256));
    }

    public async Task<PolicyApprovalAppendResult> RevokeAsync(
        PolicyApprovalRevocationRequest request,
        PolicyApprovalRevocationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);
        request.Validate();
        var commandSha256 = ComputeRevocationRequestSha256(request, envelope);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await PolicyApprovalDatabaseRoleGuard.VerifyRuntimeAsync(
            connection, _options.ExpectedRuntimeRoleName, _options.SchemaName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, ScopeLock(request.SoulId, request.DeviceBindingId, request.PlatformAccountId, request.IdempotencyKey), cancellationToken);

        var existing = await ReadReceiptAsync(
            connection, transaction, request.SoulId, request.DeviceBindingId,
            request.PlatformAccountId, request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!FixedDigestEquals(existing.Value.CommandSha256, commandSha256))
            {
                await InsertQuarantineAsync(
                    connection, transaction, request.SoulId, request.DeviceBindingId,
                    request.PlatformAccountId, request.IdempotencyKey, "revoke",
                    existing.Value.CommandSha256, commandSha256, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await InjectAsync(PolicyApprovalMutationStage.QuarantineWritten, cancellationToken);
                throw new PolicyApprovalIdempotencyConflictException();
            }
            var duplicate = await ReadSnapshotAsync(
                connection,
                transaction,
                new PolicyApprovalReadRequest(
                    request.ApprovalId, request.ProposalId, request.SoulId,
                    request.DeviceBindingId, request.PlatformAccountId,
                    await ReadDecisionTraceAsync(connection, transaction, request.ApprovalId, cancellationToken),
                    await ReadDecisionIdempotencyAsync(connection, transaction, request.ApprovalId, cancellationToken),
                    request.ApprovalSha256),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PolicyApprovalAppendResult(PolicyApprovalAppendDisposition.DuplicateNoOp, duplicate);
        }

        _revocationAuthorizer.Verify(request, envelope);
        await AcquireLockAsync(connection, transaction, ApprovalLock(request.ApprovalId), cancellationToken);
        await EnsureStillValidAsync(
            connection,
            transaction,
            envelope.ValidUntil,
            envelope.ValidUntil,
            "Revocation authorization",
            cancellationToken);
        var current = await ReadDecisionForRevocationAsync(connection, transaction, request, cancellationToken);
        if (!FixedDigestEquals(current.ReleaseBomSha256, envelope.ReleaseBomSha256))
            throw new UnauthorizedAccessException("Revocation Release BOM does not match the issued approval.");
        if (current.Status != PolicyApprovalAuthoritativeSnapshot.Active)
            throw new UnauthorizedAccessException("Only an ACTIVE approval may be revoked.");
        if (current.StatusRevision != request.ExpectedStatusRevision)
            throw new InvalidOperationException("The approval status revision is stale.");

        var nextRevision = checked(current.StatusRevision + 1);
        var reasonSha256 = PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.status-reason/v1");
            writer.Field(request.ApprovalId);
            writer.Field(nextRevision);
            writer.Field(PolicyApprovalAuthoritativeSnapshot.Revoked);
            writer.Field("CONTROL_PLANE_REVOKED");
            writer.Field(envelope.ReleaseBomSha256);
        });
        await InsertStatusAsync(
            connection, transaction, request.ApprovalId, nextRevision,
            PolicyApprovalAuthoritativeSnapshot.Revoked, "CONTROL_PLANE_REVOKED",
            reasonSha256, request.TraceId, request.IdempotencyKey, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.StatusRevisionWritten, cancellationToken);
        var statusJson = JsonSerializer.Serialize(new
        {
            approval_id = request.ApprovalId,
            decision_sha256 = request.ApprovalSha256,
            status = PolicyApprovalAuthoritativeSnapshot.Revoked,
            status_revision = nextRevision
        });
        await InsertReceiptAsync(
            connection, transaction, request.SoulId, request.DeviceBindingId,
            request.PlatformAccountId, request.IdempotencyKey, "revoke", commandSha256,
            request.ApprovalId, request.ApprovalSha256, nextRevision, statusJson, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.ReceiptWritten, cancellationToken);
        var statusPayloadSha256 = PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.status-payload-sha256/v1");
            writer.Field(request.ApprovalId);
            writer.Field(request.ApprovalSha256);
            writer.Field(nextRevision);
            writer.Field(PolicyApprovalAuthoritativeSnapshot.Revoked);
        });
        await InsertOutboxAsync(
            connection, transaction, request.ApprovalId, nextRevision,
            request.SoulId, request.DeviceBindingId, request.PlatformAccountId,
            request.TraceId, request.IdempotencyKey, "policy-approval.status/internal-v1",
            statusPayloadSha256, statusJson, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.OutboxWritten, cancellationToken);
        await InjectAsync(PolicyApprovalMutationStage.BeforeCommit, cancellationToken);
        await EnsureStillValidAsync(
            connection,
            transaction,
            envelope.ValidUntil,
            envelope.ValidUntil,
            "Revocation authorization",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var revokedSnapshot = await _reader.ReadAsync(
            new PolicyApprovalReadRequest(
                request.ApprovalId, request.ProposalId, request.SoulId,
                request.DeviceBindingId, request.PlatformAccountId,
                current.TraceId, current.DecisionIdempotencyKey, request.ApprovalSha256),
            cancellationToken);
        return new PolicyApprovalAppendResult(PolicyApprovalAppendDisposition.Inserted, revokedSnapshot);
    }

    public Task<PolicyApprovalAuthoritativeSnapshot> ReadAuthoritativeAsync(
        PolicyApprovalReadRequest request,
        CancellationToken cancellationToken = default)
        => _reader.ReadAsync(request, cancellationToken);

    public async Task<long> CountAsync(string tableName, CancellationToken cancellationToken = default)
    {
        var allowed = tableName switch
        {
            "decisions" => "approval_decisions",
            "statuses" => "approval_status_revisions",
            "rate" => "policy_rate_consumptions",
            "receipts" => "approval_idempotency_receipts",
            "outbox" => "approval_outbox",
            "quarantine" => "approval_idempotency_quarantine",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName))
        };
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await PolicyApprovalDatabaseRoleGuard.VerifyRuntimeAsync(
            connection, _options.ExpectedRuntimeRoleName, _options.SchemaName, cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {_options.SchemaName}.{allowed}", connection) { CommandTimeout = 5 };
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private async Task<PolicyApprovalAuthoritativeSnapshot> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PolicyApprovalReadRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        await using var command = _reader.BuildReadCommand(connection, transaction, request);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new UnauthorizedAccessException("No exact authoritative approval snapshot exists.");
        return PolicyApprovalAuthoritativeClient.Materialize(reader, request);
    }

    private static string ComputeDecisionRequestSha256(
        ActionProposalV1 proposal,
        PolicyEvaluationEnvelope envelope,
        string proposalSha256)
    {
        if (!string.Equals(envelope.CallerModule, "control-plane-host", StringComparison.Ordinal)
            || !string.Equals(envelope.AuthScope, "policy:evaluate", StringComparison.Ordinal)
            || envelope.RequestedMode is not (PolicyEvaluationEnvelope.Shadow or PolicyEvaluationEnvelope.Execute))
            throw new UnauthorizedAccessException("Unknown policy evaluation caller, scope, or mode fails closed.");
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireUtc(envelope.ValidUntil, nameof(envelope.ValidUntil));
        PolicyCanonicalization.RequireSha256(envelope.ProposalSha256, nameof(envelope.ProposalSha256));
        PolicyCanonicalization.RequireSha256(envelope.ReleaseBomSha256, nameof(envelope.ReleaseBomSha256));
        var signature = PolicyEcdsaGuard.DecodeCanonicalP1363Signature(envelope.SignatureBase64);
        try
        {
            return PolicyCanonicalHash.Compute(writer =>
            {
                writer.Field("dps.policy-approval.decision-command-sha256/v1");
                writer.Field(proposal.SoulId);
                writer.Field(proposal.DeviceBindingId);
                writer.Field(proposal.PlatformAccountId);
                writer.Field(proposal.IdempotencyKey);
                writer.Field(proposalSha256);
                writer.Field(envelope.CallerModule);
                writer.Field(envelope.AuthScope);
                writer.Field(envelope.ProposalId);
                writer.Field(envelope.ProposalSha256);
                writer.Field(envelope.ReleaseBomSha256);
                writer.Field(envelope.ValidUntil);
                writer.Field(envelope.RequestedMode);
                writer.NullableField(
                    envelope.ExecutionPromotion is null
                        ? null
                        : ActionExecutionPromotionV1Canonical.ComputeSignedSha256(envelope.ExecutionPromotion));
                writer.Field(envelope.SignatureBase64);
            });
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private static string ComputeRevocationRequestSha256(
        PolicyApprovalRevocationRequest request,
        PolicyApprovalRevocationEnvelope envelope)
    {
        if (!string.Equals(envelope.CallerModule, "control-plane-host", StringComparison.Ordinal)
            || !string.Equals(envelope.AuthScope, "policy:revoke", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Unknown revocation caller or scope fails closed.");
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireUtc(envelope.ValidUntil, nameof(envelope.ValidUntil));
        PolicyCanonicalization.RequireSha256(envelope.RevocationSha256, nameof(envelope.RevocationSha256));
        PolicyCanonicalization.RequireSha256(envelope.ReleaseBomSha256, nameof(envelope.ReleaseBomSha256));
        var signature = PolicyEcdsaGuard.DecodeCanonicalP1363Signature(envelope.SignatureBase64);
        try
        {
            return PolicyCanonicalHash.Compute(writer =>
            {
                writer.Field("dps.policy-approval.revoke-command-sha256/v1");
                writer.Field(PolicyApprovalRevocationBinding.ComputeSha256(request));
                writer.Field(envelope.CallerModule);
                writer.Field(envelope.AuthScope);
                writer.Field(envelope.ApprovalId);
                writer.Field(envelope.RevocationSha256);
                writer.Field(envelope.ReleaseBomSha256);
                writer.Field(envelope.ValidUntil);
                writer.Field(envelope.SignatureBase64);
            });
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private async Task InsertDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ApprovalDecisionV1 decision,
        string proposalSha256,
        string decisionSha256,
        string commandSha256,
        string trustEvidenceSha256,
        long runtimeRevision,
        string runtimeStateSha256,
        string releaseBomSha256,
        DateTimeOffset validUntil,
        string decisionJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO __TABLE__
            (approval_id, proposal_id, soul_id, device_binding_id, platform_account_id,
             trace_id, idempotency_key, occurred_at, decision, proposal_sha256,
             decision_sha256, command_sha256, trust_evidence_sha256,
             runtime_revision, runtime_state_sha256, release_bom_sha256,
             valid_until, decision_json)
            VALUES
            (@approval_id, @proposal_id, @soul_id, @device_binding_id, @platform_account_id,
             @trace_id, @idempotency_key, @occurred_at, @decision, @proposal_sha256,
             @decision_sha256, @command_sha256, @trust_evidence_sha256,
             @runtime_revision, @runtime_state_sha256, @release_bom_sha256,
             @valid_until, @decision_json)
            """,
            connection,
            transaction) { CommandTimeout = 5 };
        command.CommandText = command.CommandText.Replace(
            "__TABLE__",
            $"{_options.SchemaName}.approval_decisions",
            StringComparison.Ordinal);
        command.Parameters.AddWithValue("approval_id", decision.ApprovalId);
        command.Parameters.AddWithValue("proposal_id", decision.ProposalId);
        command.Parameters.AddWithValue("soul_id", decision.SoulId);
        command.Parameters.AddWithValue("device_binding_id", decision.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", decision.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", decision.TraceId);
        command.Parameters.AddWithValue("idempotency_key", decision.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", decision.OccurredAt);
        command.Parameters.AddWithValue("decision", decision.Decision);
        command.Parameters.AddWithValue("proposal_sha256", proposalSha256);
        command.Parameters.AddWithValue("decision_sha256", decisionSha256);
        command.Parameters.AddWithValue("command_sha256", commandSha256);
        command.Parameters.AddWithValue("trust_evidence_sha256", trustEvidenceSha256);
        command.Parameters.AddWithValue("runtime_revision", runtimeRevision);
        command.Parameters.AddWithValue("runtime_state_sha256", runtimeStateSha256);
        command.Parameters.AddWithValue("release_bom_sha256", releaseBomSha256);
        command.Parameters.AddWithValue("valid_until", validUntil);
        command.Parameters.AddWithValue("decision_json", NpgsqlDbType.Jsonb, decisionJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid approvalId, long revision,
        string status, string reasonCode, string reasonSha256, string traceId, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.approval_status_revisions
            (approval_id, revision, status, reason_code, reason_sha256, trace_id, idempotency_key)
            VALUES (@approval_id, @revision, @status, @reason_code, @reason_sha256, @trace_id, @idempotency_key)
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("approval_id", approvalId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("reason_code", reasonCode);
        command.Parameters.AddWithValue("reason_sha256", reasonSha256);
        command.Parameters.AddWithValue("trace_id", traceId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertRateConsumptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ApprovalDecisionV1 decision,
        long runtimeRevision,
        string runtimeStateSha256,
        CancellationToken cancellationToken)
    {
        PolicyCanonicalization.RequireSha256(runtimeStateSha256, nameof(runtimeStateSha256));
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.policy_rate_consumptions
            (approval_id, soul_id, device_binding_id, platform_account_id,
             runtime_revision, runtime_state_sha256, units)
            VALUES (@approval_id, @soul_id, @device_binding_id, @platform_account_id,
                    @runtime_revision, @runtime_state_sha256, 1)
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("approval_id", decision.ApprovalId);
        command.Parameters.AddWithValue("soul_id", decision.SoulId);
        command.Parameters.AddWithValue("device_binding_id", decision.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", decision.PlatformAccountId);
        command.Parameters.AddWithValue("runtime_revision", runtimeRevision);
        command.Parameters.AddWithValue("runtime_state_sha256", runtimeStateSha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string soulId,
        string deviceBindingId, string platformAccountId, string idempotencyKey,
        string mutationKind, string commandSha256, Guid approvalId, string decisionSha256,
        long statusRevision, string resultJson, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.approval_idempotency_receipts
            (soul_id, device_binding_id, platform_account_id, idempotency_key,
             mutation_kind, command_sha256, approval_id, decision_sha256,
             status_revision, result_json)
            VALUES
            (@soul_id, @device_binding_id, @platform_account_id, @idempotency_key,
             @mutation_kind, @command_sha256, @approval_id, @decision_sha256,
             @status_revision, @result_json)
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("mutation_kind", mutationKind);
        command.Parameters.AddWithValue("command_sha256", commandSha256);
        command.Parameters.AddWithValue("approval_id", approvalId);
        command.Parameters.AddWithValue("decision_sha256", decisionSha256);
        command.Parameters.AddWithValue("status_revision", statusRevision);
        command.Parameters.AddWithValue("result_json", NpgsqlDbType.Jsonb, resultJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid approvalId,
        long statusRevision, string soulId, string deviceBindingId, string platformAccountId,
        string traceId, string idempotencyKey, string topic, string payloadSha256,
        string payloadJson, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.approval_outbox
            (outbox_id, approval_id, status_revision, soul_id, device_binding_id,
             platform_account_id, trace_id, idempotency_key, topic, payload_sha256, payload_json)
            VALUES
            (@outbox_id, @approval_id, @status_revision, @soul_id, @device_binding_id,
             @platform_account_id, @trace_id, @idempotency_key, @topic, @payload_sha256, @payload_json)
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("outbox_id", Guid.NewGuid());
        command.Parameters.AddWithValue("approval_id", approvalId);
        command.Parameters.AddWithValue("status_revision", statusRevision);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        command.Parameters.AddWithValue("trace_id", traceId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("topic", topic);
        command.Parameters.AddWithValue("payload_sha256", payloadSha256);
        command.Parameters.AddWithValue("payload_json", NpgsqlDbType.Jsonb, payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string soulId,
        string deviceBindingId, string platformAccountId, string idempotencyKey,
        string mutationKind, string existingCommandSha256, string incomingCommandSha256,
        CancellationToken cancellationToken)
    {
        var scopeSha256 = PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.scope-sha256/v1");
            writer.Field(soulId);
            writer.Field(deviceBindingId);
            writer.Field(platformAccountId);
        });
        var idempotencySha256 = PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.idempotency-sha256/v1");
            writer.Field(idempotencyKey);
        });
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.approval_idempotency_quarantine
            (quarantine_id, scope_sha256, idempotency_sha256, mutation_kind,
             existing_command_sha256, incoming_command_sha256, reason)
            VALUES (@quarantine_id, @scope_sha256, @idempotency_sha256, @mutation_kind,
                    @existing_command_sha256, @incoming_command_sha256,
                    'scoped_idempotency_digest_conflict')
            ON CONFLICT (scope_sha256, idempotency_sha256, incoming_command_sha256) DO NOTHING
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("quarantine_id", Guid.NewGuid());
        command.Parameters.AddWithValue("scope_sha256", scopeSha256);
        command.Parameters.AddWithValue("idempotency_sha256", idempotencySha256);
        command.Parameters.AddWithValue("mutation_kind", mutationKind);
        command.Parameters.AddWithValue("existing_command_sha256", existingCommandSha256);
        command.Parameters.AddWithValue("incoming_command_sha256", incomingCommandSha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(string CommandSha256, Guid ApprovalId, string DecisionSha256)?> ReadReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string soulId,
        string deviceBindingId, string platformAccountId, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT command_sha256, approval_id, decision_sha256
            FROM {_options.SchemaName}.approval_idempotency_receipts
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
              AND idempotency_key = @idempotency_key
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetGuid(1), reader.GetString(2))
            : null;
    }

    private async Task<(string Status, long StatusRevision, string ReleaseBomSha256, string TraceId, string DecisionIdempotencyKey)> ReadDecisionForRevocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PolicyApprovalRevocationRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT current_status.status, current_status.revision,
                   decision.release_bom_sha256, decision.trace_id, decision.idempotency_key
            FROM {_options.SchemaName}.approval_decisions AS decision
            JOIN LATERAL
            (
                SELECT status, revision
                FROM {_options.SchemaName}.approval_status_revisions
                WHERE approval_id = decision.approval_id
                ORDER BY revision DESC
                LIMIT 1
            ) AS current_status ON true
            WHERE decision.approval_id = @approval_id
              AND decision.proposal_id = @proposal_id
              AND decision.soul_id = @soul_id
              AND decision.device_binding_id = @device_binding_id
              AND decision.platform_account_id = @platform_account_id
              AND decision.decision_sha256 = @decision_sha256
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("approval_id", request.ApprovalId);
        command.Parameters.AddWithValue("proposal_id", request.ProposalId);
        command.Parameters.AddWithValue("soul_id", request.SoulId);
        command.Parameters.AddWithValue("device_binding_id", request.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", request.PlatformAccountId);
        command.Parameters.AddWithValue("decision_sha256", request.ApprovalSha256);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new UnauthorizedAccessException("No exact approval exists for revocation.");
        return (reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private async Task<string> ReadDecisionTraceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid approvalId,
        CancellationToken cancellationToken)
        => await ReadDecisionScalarAsync(connection, transaction, approvalId, "trace_id", cancellationToken);

    private async Task<string> ReadDecisionIdempotencyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid approvalId,
        CancellationToken cancellationToken)
        => await ReadDecisionScalarAsync(connection, transaction, approvalId, "idempotency_key", cancellationToken);

    private async Task<string> ReadDecisionScalarAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid approvalId,
        string column, CancellationToken cancellationToken)
    {
        if (column is not ("trace_id" or "idempotency_key")) throw new ArgumentOutOfRangeException(nameof(column));
        await using var command = new NpgsqlCommand(
            $"SELECT {column} FROM {_options.SchemaName}.approval_decisions WHERE approval_id = @approval_id",
            connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("approval_id", approvalId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The referenced approval does not exist.");
    }

    private static async Task<DateTimeOffset> ReadDatabaseClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction) { CommandTimeout = 5 };
        var value = (DateTime)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL clock was unavailable."));
        return new DateTimeOffset(value, TimeSpan.Zero);
    }

    private static async Task EnsureStillValidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset authorizationValidUntil,
        DateTimeOffset runtimeValidUntil,
        string authorityName,
        CancellationToken cancellationToken)
    {
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireUtc(
            authorizationValidUntil, nameof(authorizationValidUntil));
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireUtc(
            runtimeValidUntil, nameof(runtimeValidUntil));
        var databaseNow = await ReadDatabaseClockAsync(connection, transaction, cancellationToken);
        if (databaseNow >= authorizationValidUntil || databaseNow >= runtimeValidUntil)
            throw new UnauthorizedAccessException($"{authorityName} expired according to PostgreSQL time.");
    }

    internal static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_value, 0))",
            connection,
            transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("lock_value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ScopeLock(string soulId, string deviceBindingId, string platformAccountId, string idempotencyKey)
        => PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.scoped-idempotency-lock/v1");
            writer.Field(soulId);
            writer.Field(deviceBindingId);
            writer.Field(platformAccountId);
            writer.Field(idempotencyKey);
        });

    internal static string PolicyRuntimeLock(string soulId, string deviceBindingId, string platformAccountId)
        => $"policy-runtime:{soulId}:{deviceBindingId}:{platformAccountId}";

    internal static string ApprovalLock(Guid approvalId)
        => "approval:" + approvalId.ToString("N");

    private static bool FixedDigestEquals(string left, string right)
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

    private ValueTask InjectAsync(PolicyApprovalMutationStage stage, CancellationToken cancellationToken)
        => _faultInjector is null ? ValueTask.CompletedTask : _faultInjector(stage, cancellationToken);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_evaluationPublicKey);
        CryptographicOperations.ZeroMemory(_promotionPublicKey);
        _revocationAuthorizer.Dispose();
    }
}

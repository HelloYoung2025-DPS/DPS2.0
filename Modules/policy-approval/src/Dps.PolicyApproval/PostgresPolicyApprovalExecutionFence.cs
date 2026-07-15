using System.Data;
using System.Security.Cryptography;
using Dps.PolicyApproval.Contracts;
using Npgsql;

namespace Dps.PolicyApproval;

public static class PolicyApprovalExecutionFenceBinding
{
    public static string ComputeRequestSha256(ApprovalExecutionFenceRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.execution-fence-request-sha256/v1");
            writer.Field(request.SchemaVersion);
            writer.Field(request.ContractId);
            writer.Field(request.ConsumerModule);
            writer.Field(request.ApprovalId);
            writer.Field(request.ProposalId);
            writer.Field(request.SoulId);
            writer.Field(request.DeviceBindingId);
            writer.Field(request.PlatformAccountId);
            writer.Field(request.TraceId);
            writer.Field(request.IdempotencyKey);
            writer.Field(request.ApprovalSha256);
            writer.Field(request.ExpectedStatusRevision);
            writer.Field(request.ExpectedRuntimeRevision);
            writer.Field(request.ExpectedRuntimeStateSha256);
            writer.Field(request.ExpectedReleaseBomSha256);
        });
    }

    public static byte[] CanonicalAuthorizationBytes(ApprovalExecutionFenceAuthorizationV1 authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.Validate();
        return PolicyCanonicalHash.Bytes(writer =>
        {
            writer.Field("dps.policy-approval.execution-fence-authorization/v1");
            writer.Field(authorization.CallerModule);
            writer.Field(authorization.AuthScope);
            writer.Field(authorization.FenceRequestSha256);
            writer.Field(authorization.ReleaseBomSha256);
            writer.Field(authorization.ValidUntil);
        });
    }
}

/// <summary>
/// Native execution truth is deliberately separate from submission transport
/// acknowledgement.  An ACK can never be promoted to action success.
/// </summary>
public static class PolicyApprovalNativeResultTruth
{
    public const string ConfirmedSuccess = "CONFIRMED_SUCCESS";
    public const string Failed = "FAILED";
    public const string UnknownOutcome = "UNKNOWN_OUTCOME";
}

public sealed class PolicyApprovalWaitingExternalException : InvalidOperationException
{
    internal PolicyApprovalWaitingExternalException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class PolicyApprovalExecutionFenceClient : IDisposable
{
    private static readonly TimeSpan LeaseDuration = ApprovalExecutionFenceV1.MaximumLifetime;
    private readonly object _signatureSync = new();
    private readonly PostgresPolicyApprovalOptions _runtimeOptions;
    private readonly PostgresPolicyApprovalSubmissionExecutorOptions _executorOptions;
    private readonly string _connectionString;
    private readonly ECDsa _authorizationPublicKey;
    private readonly PostgresPolicyApprovalSubmissionAuthority _submissionAuthority;

    private PolicyApprovalExecutionFenceClient(
        PostgresPolicyApprovalSubmissionExecutorOptions options,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> authorizationPublicKey,
        ReadOnlySpan<byte> executorSubmissionPublicKey,
        ReadOnlySpan<byte> reconciliationEvidencePublicKey,
        ReadOnlySpan<byte> recoveryEvidencePublicKey,
        ReadOnlySpan<byte> policyStateSigningPrivateKeyPkcs8)
    {
        _executorOptions = options;
        _runtimeOptions = options.ToPolicyRuntimeOptions();
        _connectionString = options.ToAuthorityRuntime().ConnectionString;
        _authorizationPublicKey = ECDsa.Create();
        try
        {
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                _authorizationPublicKey,
                authorizationPublicKey,
                nameof(authorizationPublicKey));
            _submissionAuthority = PostgresPolicyApprovalSubmissionAuthority.CreateExecutor(
                options.ToAuthorityRuntime(),
                authorityTopology,
                executorSubmissionPublicKey,
                reconciliationEvidencePublicKey,
                recoveryEvidencePublicKey,
                policyStateSigningPrivateKeyPkcs8,
                _authorizationPublicKey);
        }
        catch
        {
            _authorizationPublicKey.Dispose();
            throw;
        }
    }

    public static PolicyApprovalExecutionFenceClient CreateProduction(
        PostgresPolicyApprovalSubmissionExecutorOptions options,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> fenceAuthorizationPublicKey,
        ReadOnlySpan<byte> executorSubmissionPublicKey,
        ReadOnlySpan<byte> reconciliationEvidencePublicKey,
        ReadOnlySpan<byte> recoveryEvidencePublicKey,
        ReadOnlySpan<byte> policyStateSigningPrivateKeyPkcs8)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorityTopology);
        options.Validate();
        authorityTopology.Validate();
        return new PolicyApprovalExecutionFenceClient(
            options,
            authorityTopology,
            fenceAuthorizationPublicKey,
            executorSubmissionPublicKey,
            reconciliationEvidencePublicKey,
            recoveryEvidencePublicKey,
            policyStateSigningPrivateKeyPkcs8);
    }

    public async Task<PolicyApprovalExecutionFenceLease> AcquireAsync(
        ApprovalExecutionFenceRequestV1 request,
        ApprovalExecutionFenceAuthorizationV1 authorization,
        ApprovalSubmissionIntentV1 submissionIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(submissionIntent);
        request.Validate();
        VerifyAuthorization(request, authorization);
        _submissionAuthority.VerifyIntent(submissionIntent);
        PostgresPolicyApprovalSubmissionAuthority.ValidateIntentAgainstFence(request, submissionIntent);
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await PolicyApprovalSubmissionDatabaseRoleGuard.VerifyAsync(
                connection,
                _executorOptions.ExpectedExecutorRoleName,
                _executorOptions.SchemaName,
                PolicyApprovalSubmissionDatabaseRole.Executor,
                cancellationToken);
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            await ConfigureLeaseTimeoutsAsync(connection, transaction, cancellationToken);
            await PostgresPolicyApprovalService.AcquireLockAsync(
                connection,
                transaction,
                PostgresPolicyApprovalService.PolicyRuntimeLock(
                    request.SoulId,
                    request.DeviceBindingId,
                    request.PlatformAccountId),
                cancellationToken);
            await PostgresPolicyApprovalService.AcquireLockAsync(
                connection,
                transaction,
                PostgresPolicyApprovalService.ApprovalLock(request.ApprovalId),
                cancellationToken);
            await PostgresPolicyApprovalService.AcquireLockAsync(
                connection,
                transaction,
                SubmissionCommandLock(submissionIntent.CommandId),
                cancellationToken);
            await PostgresPolicyApprovalService.AcquireLockAsync(
                connection,
                transaction,
                SubmissionAttemptLock(submissionIntent.SubmissionAttemptId),
                cancellationToken);
            await _submissionAuthority.EnsureAcquirableWithinTransactionAsync(
                connection,
                transaction,
                submissionIntent,
                cancellationToken);

            var state = await ReadAndValidateStateAsync(
                _runtimeOptions,
                connection,
                transaction,
                request,
                authorization.ValidUntil,
                cancellationToken);
            var validUntil = new[]
            {
                authorization.ValidUntil,
                state.Snapshot.ValidUntil,
                state.RuntimeValidUntil,
                state.DatabaseNow.Add(LeaseDuration)
            }.Min();
            if (validUntil <= state.DatabaseNow)
                throw new UnauthorizedAccessException("The approval execution fence has no positive trusted lifetime.");

            var fence = new ApprovalExecutionFenceV1(
                ApprovalExecutionFenceV1.CurrentSchemaVersion,
                ApprovalExecutionFenceV1.CurrentContractId,
                ApprovalExecutionFenceV1.CurrentProducerModule,
                Guid.NewGuid(),
                request.ApprovalId,
                request.ProposalId,
                request.SoulId,
                request.DeviceBindingId,
                request.PlatformAccountId,
                request.TraceId,
                request.IdempotencyKey,
                request.ApprovalSha256,
                request.ExpectedStatusRevision,
                request.ExpectedRuntimeRevision,
                request.ExpectedRuntimeStateSha256,
                request.ExpectedReleaseBomSha256,
                state.DatabaseNow,
                validUntil,
                "internal");
            fence.Validate();
            var lease = new PolicyApprovalExecutionFenceLease(
                _runtimeOptions,
                connection,
                transaction,
                request,
                authorization.ValidUntil,
                fence,
                submissionIntent,
                _submissionAuthority);
            connection = null;
            transaction = null;
            return lease;
        }
        catch
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch { }
                await transaction.DisposeAsync();
            }
            if (connection is not null) await connection.DisposeAsync();
            throw;
        }
    }

    private void VerifyAuthorization(
        ApprovalExecutionFenceRequestV1 request,
        ApprovalExecutionFenceAuthorizationV1 authorization)
    {
        authorization.Validate();
        if (!FixedDigestEquals(
                authorization.FenceRequestSha256,
                PolicyApprovalExecutionFenceBinding.ComputeRequestSha256(request))
            || !FixedDigestEquals(
                authorization.ReleaseBomSha256,
                request.ExpectedReleaseBomSha256))
            throw new UnauthorizedAccessException("Fence authorization is not bound to the exact request and Release BOM.");

        byte[]? signature = null;
        byte[]? canonical = null;
        try
        {
            signature = PolicyEcdsaGuard.DecodeCanonicalP1363Signature(authorization.SignatureBase64);
            canonical = PolicyApprovalExecutionFenceBinding.CanonicalAuthorizationBytes(authorization);
            bool verified;
            lock (_signatureSync)
                verified = _authorizationPublicKey.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!verified) throw new UnauthorizedAccessException("Fence authorization signature verification failed.");
        }
        finally
        {
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (canonical is not null) CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static async Task ConfigureLeaseTimeoutsAsync(
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

    internal static string SubmissionCommandLock(Guid commandId)
        => "submission-command:" + commandId.ToString("N");

    internal static string SubmissionAttemptLock(Guid submissionAttemptId)
        => "submission-attempt:" + submissionAttemptId.ToString("N");

    internal static async Task<FenceDatabaseState> ReadAndValidateStateAsync(
        PostgresPolicyApprovalOptions options,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ApprovalExecutionFenceRequestV1 request,
        DateTimeOffset authorizationValidUntil,
        CancellationToken cancellationToken)
    {
        var reader = new PolicyApprovalAuthoritativeClient(options);
        await using var command = reader.BuildReadCommand(
            connection,
            transaction,
            new PolicyApprovalReadRequest(
                request.ApprovalId,
                request.ProposalId,
                request.SoulId,
                request.DeviceBindingId,
                request.PlatformAccountId,
                request.TraceId,
                request.IdempotencyKey,
                request.ApprovalSha256));
        await using var result = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await result.ReadAsync(cancellationToken))
            throw new UnauthorizedAccessException("No exact approval exists for the execution fence.");
        var snapshot = PolicyApprovalAuthoritativeClient.Materialize(result, new PolicyApprovalReadRequest(
            request.ApprovalId,
            request.ProposalId,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.ApprovalSha256));
        await result.DisposeAsync();

        if (snapshot.Approval.Decision != ApprovalDecisionV1.Approved
            || snapshot.Approval.ShadowOnly
            || snapshot.Status != PolicyApprovalAuthoritativeSnapshot.Active
            || snapshot.StatusRevision != request.ExpectedStatusRevision
            || snapshot.RuntimeRevision != request.ExpectedRuntimeRevision
            || !FixedDigestEquals(snapshot.RuntimeStateSha256, request.ExpectedRuntimeStateSha256)
            || !FixedDigestEquals(snapshot.ReleaseBomSha256, request.ExpectedReleaseBomSha256))
            throw new UnauthorizedAccessException("Approval execution fence expectations do not match the authoritative snapshot.");

        await using var runtime = new NpgsqlCommand(
            $"""
            SELECT state.state_status, state.kill_switch_enabled, state.execution_enabled,
                   state.release_bom_sha256, state.valid_until,
                   (SELECT max(latest.revision)
                      FROM {options.SchemaName}.policy_runtime_revisions AS latest
                     WHERE latest.soul_id = state.soul_id
                       AND latest.device_binding_id = state.device_binding_id
                       AND latest.platform_account_id = state.platform_account_id),
                   clock_timestamp()
              FROM {options.SchemaName}.policy_runtime_revisions AS state
             WHERE state.soul_id = @soul_id
               AND state.device_binding_id = @device_binding_id
               AND state.platform_account_id = @platform_account_id
               AND state.revision = @runtime_revision
               AND state.state_sha256 = @runtime_state_sha256
            """,
            connection,
            transaction) { CommandTimeout = 5 };
        runtime.Parameters.AddWithValue("soul_id", request.SoulId);
        runtime.Parameters.AddWithValue("device_binding_id", request.DeviceBindingId);
        runtime.Parameters.AddWithValue("platform_account_id", request.PlatformAccountId);
        runtime.Parameters.AddWithValue("runtime_revision", request.ExpectedRuntimeRevision);
        runtime.Parameters.AddWithValue("runtime_state_sha256", request.ExpectedRuntimeStateSha256);
        await using var runtimeResult = await runtime.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await runtimeResult.ReadAsync(cancellationToken))
            throw new UnauthorizedAccessException("The exact approval runtime generation does not exist.");
        var runtimeValidUntil = runtimeResult.GetFieldValue<DateTimeOffset>(4).ToUniversalTime();
        var latestRevision = runtimeResult.GetInt64(5);
        var databaseNow = runtimeResult.GetFieldValue<DateTimeOffset>(6).ToUniversalTime();
        if (runtimeResult.GetString(0) != PolicyRuntimeStateRevisionV1.Active
            || runtimeResult.GetBoolean(1)
            || !runtimeResult.GetBoolean(2)
            || !FixedDigestEquals(runtimeResult.GetString(3), request.ExpectedReleaseBomSha256)
            || latestRevision != request.ExpectedRuntimeRevision
            || databaseNow >= authorizationValidUntil
            || databaseNow >= snapshot.ValidUntil
            || databaseNow >= runtimeValidUntil)
            throw new UnauthorizedAccessException("Execution fence runtime generation is inactive, changed, killed, or expired.");
        return new FenceDatabaseState(snapshot, runtimeValidUntil, databaseNow);
    }

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

    public Task<PolicyApprovalSubmissionSnapshot> ReadSubmissionAsync(
        Guid submissionAttemptId,
        CancellationToken cancellationToken = default)
        => _submissionAuthority.ReadSubmissionAsync(submissionAttemptId, cancellationToken);

    public void Dispose()
    {
        _submissionAuthority.Dispose();
        _authorizationPublicKey.Dispose();
    }

    internal sealed record FenceDatabaseState(
        PolicyApprovalAuthoritativeSnapshot Snapshot,
        DateTimeOffset RuntimeValidUntil,
        DateTimeOffset DatabaseNow);
}

public sealed class PolicyApprovalExecutionFenceLease : IAsyncDisposable
{
    private static readonly TimeSpan TerminalOperationTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PostgresPolicyApprovalOptions _options;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ApprovalExecutionFenceRequestV1 _request;
    private readonly DateTimeOffset _authorizationValidUntil;
    private readonly ApprovalSubmissionIntentV1 _submissionIntent;
    private readonly PostgresPolicyApprovalSubmissionAuthority _submissionAuthority;
    private readonly string[] _sessionLockNames;
    private bool _disposed;
    private bool _connectionDestroyed;
    private bool _transactionCompleted;
    private bool _pending;
    private bool _acknowledged;
    private bool _quarantined;
    private bool _sessionLocksHeld;
    private bool _guardMustBeRetained;

    internal PolicyApprovalExecutionFenceLease(
        PostgresPolicyApprovalOptions options,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ApprovalExecutionFenceRequestV1 request,
        DateTimeOffset authorizationValidUntil,
        ApprovalExecutionFenceV1 fence,
        ApprovalSubmissionIntentV1 submissionIntent,
        PostgresPolicyApprovalSubmissionAuthority submissionAuthority)
    {
        _options = options;
        _connection = connection;
        _transaction = transaction;
        _request = request;
        _authorizationValidUntil = authorizationValidUntil;
        _submissionIntent = submissionIntent;
        _submissionAuthority = submissionAuthority;
        _sessionLockNames =
        [
            PostgresPolicyApprovalService.PolicyRuntimeLock(
                request.SoulId,
                request.DeviceBindingId,
                request.PlatformAccountId),
            PostgresPolicyApprovalService.ApprovalLock(request.ApprovalId),
            PolicyApprovalExecutionFenceClient.SubmissionCommandLock(submissionIntent.CommandId),
            PolicyApprovalExecutionFenceClient.SubmissionAttemptLock(submissionIntent.SubmissionAttemptId)
        ];
        Fence = fence;
    }

    public ApprovalExecutionFenceRequestV1 Request => _request;
    public ApprovalExecutionFenceV1 Fence { get; }
    public ApprovalSubmissionIntentV1 SubmissionIntent => _submissionIntent;
    public string NativeRequestBindingSha256 => _submissionIntent.NativeRequestBindingSha256;

    public async Task<ApprovalExecutionFenceV1> RevalidateForNativeDispatchAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_transactionCompleted || _pending)
                throw new UnauthorizedAccessException("A durable pending submission can only be reconciled; fence revalidation fails closed.");
            var state = await PolicyApprovalExecutionFenceClient.ReadAndValidateStateAsync(
                _options,
                _connection,
                _transaction,
                _request,
                _authorizationValidUntil,
                cancellationToken);
            if (state.DatabaseNow >= Fence.ValidUntil)
                throw new UnauthorizedAccessException("Approval execution fence lease expired before native dispatch.");
            return Fence;
        }
        finally { _gate.Release(); }
    }

    public async Task SubmitNativeOnceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending)
                throw new PolicyApprovalWaitingExternalException(
                    "WAITING_EXTERNAL: an existing durable submission is UNKNOWN_OUTCOME and cannot be retried by Policy Approval.");
            throw new PolicyApprovalWaitingExternalException(
                "WAITING_EXTERNAL: Policy Approval does not own or publish a native-stop contract. Native execution remains disabled until the signed Executor Gateway owner artifact, two-phase submitted-request commitment, and external authorities are composed and independently verified; no PENDING row or native side effect occurred.");
        }
        finally { _gate.Release(); }
    }

    internal async Task<PolicyApprovalSubmissionBeginResult> BeginSubmissionAsync(
        ApprovalSubmissionIntentV1 intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await BeginSubmissionCoreAsync(intent, retainSessionGuard: false, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task<ApprovalSubmissionStateV1> AcknowledgeSubmissionAsync(
        ApprovalSubmissionAcknowledgementV1 acknowledgement,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pending || !_transactionCompleted)
                throw new UnauthorizedAccessException("A durable SUBMISSION_PENDING receipt is required before acknowledgement.");
            var state = await _submissionAuthority.AcknowledgeAsync(acknowledgement, cancellationToken);
            _acknowledged = true;
            return state;
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask<ApprovalSubmissionStateV1> QuarantineUnknownSubmissionAsync(
        string resultCode,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pending || !_transactionCompleted)
                throw new UnauthorizedAccessException("No durable pending attempt exists to quarantine.");
            var state = await _submissionAuthority.QuarantineAsync(
                _submissionIntent.SubmissionAttemptId,
                resultCode,
                cancellationToken);
            _quarantined = true;
            return state;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed) return;
            if (_guardMustBeRetained)
                throw new InvalidOperationException(
                    "Native callback termination is unproven; the non-pooled session guard must remain held until this process is failed fast.");
            _disposed = true;
            if (!_transactionCompleted)
            {
                try { await _transaction.RollbackAsync(CancellationToken.None); }
                catch { }
                try { await _transaction.DisposeAsync(); }
                finally { await DestroyConnectionAsync(); }
            }
            if (_pending && !_acknowledged && !_quarantined)
                throw new InvalidOperationException("Durable SUBMISSION_PENDING has no acknowledged or explicit UNKNOWN_SUBMISSION state; recovery is mandatory.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PolicyApprovalSubmissionBeginResult> BeginSubmissionCoreAsync(
        ApprovalSubmissionIntentV1 intent,
        bool retainSessionGuard,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _submissionAuthority.VerifyIntent(intent);
        PostgresPolicyApprovalSubmissionAuthority.ValidateIntentAgainstFence(_request, intent);
        var expectedIntentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(_submissionIntent);
        if (!PostgresPolicyApprovalSubmissionAuthority.FixedDigestEquals(
                expectedIntentSha256,
                ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent)))
            throw new UnauthorizedAccessException("Submission intent differs from the acquire-bound intent.");

        if (_pending)
        {
            var existing = await _submissionAuthority.ReadSubmissionAsync(intent.SubmissionAttemptId, cancellationToken);
            return new PolicyApprovalSubmissionBeginResult(
                PolicyApprovalSubmissionBeginDisposition.ExistingUnknownSubmission,
                existing.State);
        }

        var state = await PolicyApprovalExecutionFenceClient.ReadAndValidateStateAsync(
            _options,
            _connection,
            _transaction,
            _request,
            _authorizationValidUntil,
            cancellationToken);
        if (state.DatabaseNow >= Fence.ValidUntil)
            throw new UnauthorizedAccessException("Approval execution fence expired before durable SUBMISSION_PENDING.");

        var disposition = await _submissionAuthority.AppendPendingWithinFenceTransactionAsync(
            _connection,
            _transaction,
            Fence,
            intent,
            cancellationToken);
        if (retainSessionGuard && string.Equals(disposition, "INSERTED", StringComparison.Ordinal))
            await AcquireSessionLocksAsync(cancellationToken);
        try
        {
            await _transaction.CommitAsync(cancellationToken);
            _transactionCompleted = true;
            _pending = true;
        }
        catch (Exception exception)
        {
            _transactionCompleted = true;
            _pending = true;
            await DestroyConnectionAsync();
            throw new PolicyApprovalSubmissionCommitUncertainException(
                "SUBMISSION_PENDING commit acknowledgement is uncertain; native submission is forbidden and reconciliation is required.",
                exception);
        }
        finally
        {
            try { await _transaction.DisposeAsync(); } catch { }
            if (!retainSessionGuard) await DestroyConnectionAsync();
        }

        var persisted = retainSessionGuard
            ? await _submissionAuthority.ReadSubmissionOnGuardConnectionAsync(
                _connection,
                intent.SubmissionAttemptId,
                cancellationToken)
            : await _submissionAuthority.ReadSubmissionAsync(intent.SubmissionAttemptId, cancellationToken);
        if (persisted.State.State != ApprovalSubmissionStateV1.SubmissionPending)
            throw new InvalidDataException("Durable begin did not read back the exact SUBMISSION_PENDING state.");
        var beginDisposition = string.Equals(disposition, "INSERTED", StringComparison.Ordinal)
            ? PolicyApprovalSubmissionBeginDisposition.Inserted
            : PolicyApprovalSubmissionBeginDisposition.ExistingUnknownSubmission;
        return new PolicyApprovalSubmissionBeginResult(beginDisposition, persisted.State);
    }

    private async Task AcquireSessionLocksAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var lockName in _sessionLockNames)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_lock(hashtextextended(@lock_value, 0))",
                    _connection,
                    _transaction) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("lock_value", lockName);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            _sessionLocksHeld = true;
        }
        catch
        {
            await DestroyConnectionAsync();
            throw;
        }
    }

    private async Task ReleaseSessionGuardAndDestroyConnectionAsync()
    {
        if (!_sessionLocksHeld)
        {
            await DestroyConnectionAsync();
            return;
        }
        try
        {
            using var cleanupCts = new CancellationTokenSource(TerminalOperationTimeout);
            for (var index = _sessionLockNames.Length - 1; index >= 0; index--)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@lock_value, 0))",
                    _connection) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("lock_value", _sessionLockNames[index]);
                var unlocked = (bool?)await command.ExecuteScalarAsync(cleanupCts.Token);
                if (unlocked != true)
                    throw new InvalidOperationException("PostgreSQL did not confirm reverse session advisory unlock.");
            }
            _sessionLocksHeld = false;
        }
        finally
        {
            await DestroyConnectionAsync();
        }
    }

    private async ValueTask DestroyConnectionAsync()
    {
        if (_connectionDestroyed) return;
        await _connection.DisposeAsync();
        _connectionDestroyed = true;
    }

    private static Task<T> StartIsolatedCallbackAsync<T>(Func<Task<T>> callbackStart)
    {
        ArgumentNullException.ThrowIfNull(callbackStart);
        return Task.Factory.StartNew(
                callbackStart,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default)
            .Unwrap();
    }

    private static async Task ConfigureGuardTransactionTimeoutsAsync(
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

    internal async Task DestroyRetainedGuardForTestAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!_guardMustBeRetained)
                throw new InvalidOperationException("No retained guard exists to simulate process death.");
            await DestroyConnectionAsync();
            _sessionLocksHeld = false;
            _guardMustBeRetained = false;
            _disposed = true;
        }
        finally { _gate.Release(); }
    }
}

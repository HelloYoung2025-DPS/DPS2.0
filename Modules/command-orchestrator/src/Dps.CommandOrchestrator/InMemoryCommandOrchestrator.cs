using System.Security.Cryptography;
using Dps.CommandOrchestrator.Contracts;
using Dps.OperationCompiler.Contracts;

namespace Dps.CommandOrchestrator;

public enum CommandState { Pending, Leased, Dispatched, Succeeded, Failed, ReconciliationRequired }
public enum EnqueueDisposition { Inserted, DuplicateNoOp, Quarantined }
public enum ReceiptDisposition { Applied, DuplicateNoOp, Quarantined }
public sealed record EnqueueResult(EnqueueDisposition Disposition, Guid? CommandId, string PayloadSha256);
public sealed record ReceiptResult(ReceiptDisposition Disposition, CommandState State);
public sealed record CommandSnapshot(Guid CommandId, string SoulId, string DeviceBindingId, string PlatformAccountId, CommandState State, int Attempt, Guid? LeaseId, DateTimeOffset? LeaseExpiresAt);

// Deterministic test model only. Product hosts must use the durable PostgreSQL path,
// whose dispatch boundary verifies the independent Policy Approval trust root.
internal sealed class InMemoryCommandOrchestrator : IDisposable
{
    private const int MaximumAttempts = 3;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _commands = [];
    private readonly Dictionary<string, Guid> _idempotency = new(StringComparer.Ordinal);
    private readonly AuthoritativeCommandReceiptVerifier _receiptVerifier;
    private int _quarantineCount;

    internal InMemoryCommandOrchestrator(ReadOnlySpan<byte> trustedExecutorGatewayReceiptPublicKeySpki) =>
        _receiptVerifier = new AuthoritativeCommandReceiptVerifier(trustedExecutorGatewayReceiptPublicKeySpki);

    public int QuarantineCount { get { lock (_sync) return _quarantineCount; } }

    public EnqueueResult Enqueue(CompiledOperationV1 operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = operation.ValidateAndSnapshot();
        var digest = CommandCanonicalEncoding.OperationDigest(snapshot);
        var key = CommandCanonicalEncoding.IdempotencyScopeKey(snapshot.SoulId, snapshot.DeviceBindingId, snapshot.PlatformAccountId, snapshot.IdempotencyKey);
        lock (_sync)
        {
            if (_idempotency.TryGetValue(key, out var existingId))
            {
                var existing = _commands[existingId];
                if (FixedEquals(existing.OperationDigest, digest)) return new EnqueueResult(EnqueueDisposition.DuplicateNoOp, existingId, digest);
                _quarantineCount++; return new EnqueueResult(EnqueueDisposition.Quarantined, null, digest);
            }
            var commandId = CommandCanonicalEncoding.CommandId(key, snapshot.OperationId);
            _commands.Add(commandId, new Entry(commandId, snapshot, digest)); _idempotency.Add(key, commandId);
            return new EnqueueResult(EnqueueDisposition.Inserted, commandId, digest);
        }
    }

    public CommandDispatchV1 AcquireLease(Guid commandId, string soulId, string deviceBindingId, string platformAccountId, string workerId, DateTimeOffset now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(duration)); CommandContractGuard.RequireUtc(now, nameof(now)); CommandContractGuard.RequireText(workerId, 128, nameof(workerId));
        lock (_sync)
        {
            var entry = GetScoped(commandId, soulId, deviceBindingId, platformAccountId); RecoverExpired(entry, now);
            if (entry.State != CommandState.Pending) throw new InvalidOperationException($"Command is not leaseable from state {entry.State}."); if (entry.Attempt >= MaximumAttempts) throw new InvalidOperationException("Maximum attempts reached.");
            entry.Attempt++; entry.State = CommandState.Leased; entry.LeaseId = CommandCanonicalEncoding.LeaseId(entry.CommandId, entry.Attempt, workerId); entry.LeaseOwner = workerId; entry.LeaseExpiresAt = now.Add(duration);
            var dispatch = new CommandDispatchV1(CommandDispatchV1.CurrentSchemaVersion, CommandDispatchV1.CurrentContractId, CommandDispatchV1.CurrentProducerModule,
                entry.CommandId, entry.Operation.OperationId, entry.Operation.ApprovalId, entry.Operation.ApprovalSha256, entry.Operation.SoulId, entry.Operation.DeviceBindingId, entry.Operation.PlatformAccountId,
                entry.Operation.TraceId, entry.Operation.IdempotencyKey, now, "internal", entry.Operation.ActionKind, entry.Operation.IsSideEffect, entry.Operation.PlatformAuthorizationId,
                entry.LeaseId.Value, workerId, entry.LeaseExpiresAt.Value, entry.Attempt,
                entry.Operation.Steps.Select(step => new CommandStepV1(step.StepId, step.StepKind, new Dictionary<string, string>(step.Arguments, StringComparer.Ordinal), step.RetrySafe, step.PostconditionKind)).ToArray());
            dispatch.Validate();
            entry.CommandSha256 = ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch);
            return dispatch;
        }
    }

    public void MarkDispatched(Guid commandId, Guid leaseId, ExecutionAuthorizationV1 issuedAuthorization, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuedAuthorization);
        issuedAuthorization.Validate();
        CommandContractGuard.RequireUtc(now, nameof(now));
        lock (_sync)
        {
            var entry = Get(commandId);
            if (entry.State != CommandState.Leased || entry.LeaseId != leaseId || entry.LeaseExpiresAt <= now)
                throw new UnauthorizedAccessException("Lease is missing, forged, expired, or out of order.");
            if (entry.CommandSha256 is null || issuedAuthorization.CommandId != entry.CommandId || issuedAuthorization.LeaseId != leaseId || issuedAuthorization.Attempt != entry.Attempt ||
                !string.Equals(issuedAuthorization.SoulId, entry.Operation.SoulId, StringComparison.Ordinal) ||
                !string.Equals(issuedAuthorization.DeviceBindingId, entry.Operation.DeviceBindingId, StringComparison.Ordinal) ||
                !string.Equals(issuedAuthorization.PlatformAccountId, entry.Operation.PlatformAccountId, StringComparison.Ordinal) ||
                !string.Equals(issuedAuthorization.TraceId, entry.Operation.TraceId, StringComparison.Ordinal) ||
                !string.Equals(issuedAuthorization.IdempotencyKey, entry.Operation.IdempotencyKey, StringComparison.Ordinal) ||
                !FixedEquals(entry.CommandSha256, issuedAuthorization.CommandSha256) ||
                issuedAuthorization.OccurredAt > now || issuedAuthorization.ValidUntil <= now || issuedAuthorization.ValidUntil > entry.LeaseExpiresAt)
                throw new UnauthorizedAccessException("Issued execution authorization is outside the exact command, lease, scope, or validity window.");
            entry.AuthorizationSha256 = ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(issuedAuthorization);
            entry.ReleaseBomSha256 = issuedAuthorization.ReleaseBomSha256;
            entry.ActiveReleaseBomGeneration = issuedAuthorization.ActiveReleaseBomGeneration;
            entry.ActiveReleaseBomTokenSha256 = issuedAuthorization.ActiveReleaseBomTokenSha256;
            entry.State = CommandState.Dispatched;
        }
    }

    public ReceiptResult RecordReceipt(SignedCommandReceiptV1 signedReceipt)
    {
        var verified = _receiptVerifier.Verify(signedReceipt);
        var receipt = verified.Receipt;
        var digest = CommandCanonicalEncoding.SignedReceiptDigest(verified.SignedReceipt);
        lock (_sync)
        {
            var entry = GetScoped(receipt.CommandId, receipt.SoulId, receipt.DeviceBindingId, receipt.PlatformAccountId);
            if (entry.SeenReceipts.TryGetValue(receipt.ReceiptId, out var seenDigest))
            {
                if (FixedEquals(seenDigest, digest)) return new ReceiptResult(ReceiptDisposition.DuplicateNoOp, entry.State);
                _quarantineCount++; return new ReceiptResult(ReceiptDisposition.Quarantined, entry.State);
            }
            if (!string.Equals(entry.Operation.TraceId, receipt.TraceId, StringComparison.Ordinal) ||
                !string.Equals(entry.Operation.IdempotencyKey, receipt.IdempotencyKey, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Receipt trace or idempotency identity does not match the command.");
            if (entry.State != CommandState.Dispatched) throw new InvalidOperationException("Receipt is out of order; command was not marked dispatched.");
            if (entry.LeaseId != receipt.LeaseId || entry.Attempt != receipt.Attempt) throw new UnauthorizedAccessException("Receipt belongs to a stale or forged lease attempt.");
            if (entry.CommandSha256 is null || !FixedEquals(entry.CommandSha256, signedReceipt.CommandSha256))
                throw new UnauthorizedAccessException("Signed receipt is not bound to the exact dispatched command payload.");
            if (entry.AuthorizationSha256 is null || entry.ReleaseBomSha256 is null || entry.ActiveReleaseBomGeneration is null || entry.ActiveReleaseBomTokenSha256 is null ||
                !FixedEquals(entry.AuthorizationSha256, signedReceipt.AuthorizationSha256) ||
                !FixedEquals(entry.ReleaseBomSha256, signedReceipt.ReleaseBomSha256) ||
                entry.ActiveReleaseBomGeneration.Value != signedReceipt.ActiveReleaseBomGeneration ||
                !FixedEquals(entry.ActiveReleaseBomTokenSha256, signedReceipt.ActiveReleaseBomTokenSha256))
                throw new UnauthorizedAccessException("Signed receipt is not bound to the issued authorization and exact active Release BOM generation/token.");
            entry.SeenReceipts.Add(receipt.ReceiptId, digest);
            entry.State = receipt.Outcome switch
            {
                CommandReceiptV1.Success => CommandState.Succeeded,
                CommandReceiptV1.UnknownOutcome => CommandState.ReconciliationRequired,
                CommandReceiptV1.Failed when receipt.RetryAllowed && entry.Attempt < MaximumAttempts && entry.Operation.Steps.All(step => step.RetrySafe) => CommandState.Pending,
                CommandReceiptV1.Failed => CommandState.Failed,
                _ => throw new NotSupportedException($"Unknown receipt outcome '{receipt.Outcome}'.")
            };
            ClearLease(entry); return new ReceiptResult(ReceiptDisposition.Applied, entry.State);
        }
    }

    public int RecoverExpiredLeases(DateTimeOffset now)
    {
        CommandContractGuard.RequireUtc(now, nameof(now)); lock (_sync) { var changed = 0; foreach (var entry in _commands.Values) if (RecoverExpired(entry, now)) changed++; return changed; }
    }

    public CommandSnapshot GetSnapshot(Guid commandId, string soulId, string deviceBindingId, string platformAccountId)
    {
        lock (_sync) { var entry = GetScoped(commandId, soulId, deviceBindingId, platformAccountId); return new CommandSnapshot(entry.CommandId, entry.Operation.SoulId, entry.Operation.DeviceBindingId, entry.Operation.PlatformAccountId, entry.State, entry.Attempt, entry.LeaseId, entry.LeaseExpiresAt); }
    }

    public void Dispose() => _receiptVerifier.Dispose();

    private static bool RecoverExpired(Entry entry, DateTimeOffset now)
    {
        if (entry.LeaseExpiresAt is null || entry.LeaseExpiresAt > now) return false;
        if (entry.State == CommandState.Leased) { entry.State = CommandState.Pending; ClearLease(entry); return true; }
        if (entry.State == CommandState.Dispatched) { entry.State = CommandState.ReconciliationRequired; ClearLease(entry); return true; }
        return false;
    }
    private Entry Get(Guid id) => _commands.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException("Unknown command.");
    private Entry GetScoped(Guid id, string soul, string device, string account) { var entry = Get(id); if (!string.Equals(entry.Operation.SoulId, soul, StringComparison.Ordinal) || !string.Equals(entry.Operation.DeviceBindingId, device, StringComparison.Ordinal) || !string.Equals(entry.Operation.PlatformAccountId, account, StringComparison.Ordinal)) throw new UnauthorizedAccessException("SOUL-ISO-001: command scope mismatch."); return entry; }
    private static void ClearLease(Entry entry) { entry.LeaseId = null; entry.LeaseOwner = null; entry.LeaseExpiresAt = null; entry.CommandSha256 = null; entry.AuthorizationSha256 = null; entry.ReleaseBomSha256 = null; entry.ActiveReleaseBomGeneration = null; entry.ActiveReleaseBomTokenSha256 = null; }
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private sealed class Entry(Guid commandId, CompiledOperationV1 operation, string operationDigest)
    {
        public Guid CommandId { get; } = commandId; public CompiledOperationV1 Operation { get; } = operation; public string OperationDigest { get; } = operationDigest;
        public CommandState State { get; set; } = CommandState.Pending; public int Attempt { get; set; } public Guid? LeaseId { get; set; } public string? LeaseOwner { get; set; } public DateTimeOffset? LeaseExpiresAt { get; set; } public string? CommandSha256 { get; set; } public string? AuthorizationSha256 { get; set; } public string? ReleaseBomSha256 { get; set; } public long? ActiveReleaseBomGeneration { get; set; } public string? ActiveReleaseBomTokenSha256 { get; set; } public Dictionary<Guid, string> SeenReceipts { get; } = [];
    }
}

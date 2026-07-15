using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.AuditMetrics.Contracts;
using Dps.CommandOrchestrator.Contracts;

namespace Dps.AuditMetrics;

public sealed record AuditRelayEnvelope(string CallerModule, string AuthScope, Guid ReceiptId, string ReceiptSha256, DateTimeOffset ExpiresAt, string ReleaseBomSha256, string SignatureBase64);
public sealed record VerifiedAuditRelayAuthorization(string CallerModule, string AuthScope, Guid ReceiptId, string ReceiptSha256, DateTimeOffset ValidUntil, string ReleaseBomSha256);
public interface IAuditRelayAuthorizationVerifier { VerifiedAuditRelayAuthorization Verify(CommandReceiptV1 receipt, AuditRelayEnvelope envelope, DateTimeOffset now); }
public enum AuditAppendDisposition { Inserted, DuplicateNoOp, Quarantined }
public sealed record AuditAppendResult(AuditAppendDisposition Disposition, Guid? AuditEventId);
internal sealed record VerifiedAuditCandidate(
    AuditEventV1 AuditEvent,
    Guid SourceReceiptId,
    string SourceReceiptSha256,
    string ReleaseBomSha256,
    string EventIntegritySha256,
    string RecordSha256);

public sealed class ImmutableAuditMetrics
{
    private readonly IAuditRelayAuthorizationVerifier _authorizationVerifier;
    private readonly object _sync = new(); private readonly Dictionary<Guid, (AuditEventV1 Event, string Digest)> _events = []; private int _quarantineCount;
    public ImmutableAuditMetrics(IAuditRelayAuthorizationVerifier authorizationVerifier) { ArgumentNullException.ThrowIfNull(authorizationVerifier); _authorizationVerifier = authorizationVerifier; }
    public int QuarantineCount { get { lock (_sync) return _quarantineCount; } }

    public AuditAppendResult AppendReceipt(CommandReceiptV1 receipt, AuditRelayEnvelope envelope, DateTimeOffset now)
    {
        var candidate = AuditReceiptProcessor.VerifyAndCreate(_authorizationVerifier, receipt, envelope, now);
        return AppendVerified(candidate);
    }

    private AuditAppendResult AppendVerified(VerifiedAuditCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_sync)
        {
            if (_events.TryGetValue(candidate.AuditEvent.AuditEventId, out var existing)) { if (AuditDigest.FixedEquals(existing.Digest, candidate.RecordSha256)) return new AuditAppendResult(AuditAppendDisposition.DuplicateNoOp, candidate.AuditEvent.AuditEventId); _quarantineCount++; return new AuditAppendResult(AuditAppendDisposition.Quarantined, null); }
            _events.Add(candidate.AuditEvent.AuditEventId, (candidate.AuditEvent, candidate.RecordSha256)); return new AuditAppendResult(AuditAppendDisposition.Inserted, candidate.AuditEvent.AuditEventId);
        }
    }

    public IReadOnlyList<AuditEventV1> ReadScope(string soulId, string deviceBindingId, string platformAccountId)
    {
        AuditContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId); lock (_sync) return _events.Values.Select(item => item.Event).Where(item => string.Equals(item.SoulId, soulId, StringComparison.Ordinal) && string.Equals(item.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) && string.Equals(item.PlatformAccountId, platformAccountId, StringComparison.Ordinal)).OrderBy(item => item.OccurredAt).ThenBy(item => item.AuditEventId).ToArray();
    }

    public IReadOnlyDictionary<string, long> OutcomeCounts()
    {
        lock (_sync) return _events.Values.Select(item => item.Event.Outcome).GroupBy(outcome => outcome, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);
    }

}

internal static class AuditReceiptProcessor
{
    public static VerifiedAuditCandidate VerifyAndCreate(
        IAuditRelayAuthorizationVerifier authorizationVerifier,
        CommandReceiptV1 receipt,
        AuditRelayEnvelope envelope,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(authorizationVerifier);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(envelope);
        receipt.Validate();
        AuditContractGuard.RequireUtc(now, nameof(now));

        var expectedReceiptSha256 = AuditRelayAuthorizationBinding.ComputeReceiptSha256(receipt);
        var authorization = authorizationVerifier.Verify(receipt, envelope, now);
        ArgumentNullException.ThrowIfNull(authorization);
        AuditContractGuard.RequireSha256(authorization.ReleaseBomSha256, nameof(authorization.ReleaseBomSha256));
        AuditContractGuard.RequireUtc(authorization.ValidUntil, nameof(authorization.ValidUntil));
        AuditContractGuard.RequireSha256(authorization.ReceiptSha256, nameof(authorization.ReceiptSha256));

        if (!string.Equals(authorization.CallerModule, "command-orchestrator", StringComparison.Ordinal)
            || !string.Equals(authorization.AuthScope, "audit:command-receipt", StringComparison.Ordinal)
            || authorization.ReceiptId != receipt.ReceiptId
            || authorization.ValidUntil < now
            || !AuditDigest.FixedEquals(authorization.ReceiptSha256, expectedReceiptSha256))
        {
            throw new UnauthorizedAccessException("Verified relay authorization does not match the exact receipt payload.");
        }

        var verification = receipt.Outcome switch
        {
            CommandReceiptV1.Success => "verified",
            CommandReceiptV1.Failed => "failed",
            CommandReceiptV1.UnknownOutcome => "unknown",
            _ => throw new NotSupportedException()
        };
        var auditEvent = new AuditEventV1(
            AuditEventV1.CurrentSchemaVersion,
            AuditEventV1.CurrentContractId,
            AuditEventV1.CurrentProducerModule,
            DeterministicGuid($"{receipt.ReceiptId:N}:audit.event/v1"),
            receipt.CommandId,
            receipt.SoulId,
            receipt.DeviceBindingId,
            receipt.PlatformAccountId,
            receipt.TraceId,
            receipt.IdempotencyKey,
            receipt.OccurredAt,
            "internal",
            "command.completed",
            receipt.Outcome,
            CommandReceiptV1.CurrentContractId,
            receipt.EvidenceDigest,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["result_code"] = receipt.ResultCode,
                ["verification_class"] = verification
            });
        auditEvent.Validate();
        var eventIntegritySha256 = AuditEventIntegrityBinding.ComputeSha256(auditEvent, expectedReceiptSha256);
        var recordSha256 = AuditPersistedRecordBinding.ComputeSha256(eventIntegritySha256, authorization.ReleaseBomSha256);
        return new VerifiedAuditCandidate(
            auditEvent,
            receipt.ReceiptId,
            expectedReceiptSha256,
            authorization.ReleaseBomSha256,
            eventIntegritySha256,
            recordSha256);
    }

    private static Guid DeterministicGuid(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

internal static class AuditDigest
{
    public static bool FixedEquals(string left, string right)
    {
        AuditContractGuard.RequireSha256(left, nameof(left));
        AuditContractGuard.RequireSha256(right, nameof(right));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }
}

public static class AuditRelayAuthorizationBinding
{
    public static byte[] CanonicalBytes(CommandReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt); receipt.Validate();
        return AuditCanonicalEncoding.CommandReceipt(receipt);
    }

    public static string ComputeReceiptSha256(CommandReceiptV1 receipt)
    {
        var canonical = CanonicalBytes(receipt);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }
}

public sealed class EcdsaAuditRelayAuthorizationVerifier : IAuditRelayAuthorizationVerifier, IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _publicKey;
    public string PublicKeySha256 { get; }

    public EcdsaAuditRelayAuthorizationVerifier(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        PublicKeySha256 = Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));
        _publicKey = ECDsa.Create();
        _publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
        if (bytesRead != subjectPublicKeyInfo.Length) throw new ArgumentException("Public key contains trailing bytes.", nameof(subjectPublicKeyInfo));
        var parameters = _publicKey.ExportParameters(includePrivateParameters: false);
        if (!string.Equals(parameters.Curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal)
            || parameters.Q.X is not { Length: 32 }
            || parameters.Q.Y is not { Length: 32 })
        {
            _publicKey.Dispose();
            throw new ArgumentException("Relay public key must use NIST P-256.", nameof(subjectPublicKeyInfo));
        }
    }

    public VerifiedAuditRelayAuthorization Verify(CommandReceiptV1 receipt, AuditRelayEnvelope envelope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(envelope); AuditContractGuard.RequireUtc(now, nameof(now)); AuditContractGuard.RequireUtc(envelope.ExpiresAt, nameof(envelope.ExpiresAt)); AuditContractGuard.RequireSha256(envelope.ReceiptSha256, nameof(envelope.ReceiptSha256)); AuditContractGuard.RequireSha256(envelope.ReleaseBomSha256, nameof(envelope.ReleaseBomSha256));
        if (envelope.ReceiptId != receipt.ReceiptId || envelope.ExpiresAt < now) throw new UnauthorizedAccessException("Relay envelope is stale or bound to another receipt.");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.ReceiptSha256), Convert.FromHexString(AuditRelayAuthorizationBinding.ComputeReceiptSha256(receipt)))) throw new UnauthorizedAccessException("Relay envelope receipt digest mismatch.");
        byte[] signature;
        try { signature = Convert.FromBase64String(envelope.SignatureBase64); }
        catch (FormatException exception) { throw new UnauthorizedAccessException("Relay signature is not valid Base64.", exception); }
        var payload = CanonicalBytes(envelope);
        bool verified;
        try { lock (_sync) verified = _publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
        }
        if (!verified) throw new UnauthorizedAccessException("Relay signature verification failed.");
        return new VerifiedAuditRelayAuthorization(envelope.CallerModule, envelope.AuthScope, envelope.ReceiptId, envelope.ReceiptSha256, envelope.ExpiresAt, envelope.ReleaseBomSha256);
    }

    public static byte[] CanonicalBytes(AuditRelayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return AuditCanonicalEncoding.RelayEnvelope(envelope);
    }

    public void Dispose() => _publicKey.Dispose();
}

internal static class AuditEventIntegrityBinding
{
    public static string ComputeSha256(AuditEventV1 auditEvent, string sourceReceiptSha256)
    {
        ArgumentNullException.ThrowIfNull(auditEvent); auditEvent.Validate(); AuditContractGuard.RequireSha256(sourceReceiptSha256, nameof(sourceReceiptSha256));
        var canonical = AuditCanonicalEncoding.AuditEvent(auditEvent, sourceReceiptSha256);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }
}

internal static class AuditPersistedRecordBinding
{
    public static string ComputeSha256(string eventIntegritySha256, string releaseBomSha256)
    {
        AuditContractGuard.RequireSha256(eventIntegritySha256, nameof(eventIntegritySha256));
        AuditContractGuard.RequireSha256(releaseBomSha256, nameof(releaseBomSha256));
        var canonical = AuditCanonicalEncoding.PersistedRecord(eventIntegritySha256, releaseBomSha256);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }
}

internal static class AuditCanonicalEncoding
{
    private const string CommandReceiptDomain = "dps.audit.command-receipt/v1";
    private const string RelayEnvelopeDomain = "dps.audit.relay-envelope/v1";
    private const string AuditEventDomain = "dps.audit.event-integrity/v1";
    private const string PersistedRecordDomain = "dps.audit.persisted-record/v1";
    private const string ScopeIdempotencyDomain = "dps.audit.scope-idempotency/v1";

    public static byte[] CommandReceipt(CommandReceiptV1 receipt) => Encode(writer =>
    {
        writer.Field(CommandReceiptDomain);
        writer.Field(receipt.SchemaVersion);
        writer.Field(receipt.ContractId);
        writer.Field(receipt.ProducerModule);
        writer.Field(receipt.ReceiptId);
        writer.Field(receipt.CommandId);
        writer.Field(receipt.LeaseId);
        writer.Field(receipt.Attempt);
        writer.Field(receipt.SoulId);
        writer.Field(receipt.DeviceBindingId);
        writer.Field(receipt.PlatformAccountId);
        writer.Field(receipt.TraceId);
        writer.Field(receipt.IdempotencyKey);
        writer.Field(receipt.OccurredAt);
        writer.Field(receipt.PrivacyClass);
        writer.Field(receipt.Outcome);
        writer.Field(receipt.NativeResultId);
        writer.Field(receipt.NativeResultVerified);
        writer.Field(receipt.PostconditionVerified);
        writer.Field(receipt.EvidenceDigest);
        writer.Field(receipt.RetryAllowed);
        writer.Field(receipt.ResultCode);
    });

    public static byte[] RelayEnvelope(AuditRelayEnvelope envelope) => Encode(writer =>
    {
        writer.Field(RelayEnvelopeDomain);
        writer.Field(envelope.CallerModule);
        writer.Field(envelope.AuthScope);
        writer.Field(envelope.ReceiptId);
        writer.Field(envelope.ReceiptSha256);
        writer.Field(envelope.ExpiresAt);
        writer.Field(envelope.ReleaseBomSha256);
    });

    public static byte[] AuditEvent(AuditEventV1 auditEvent, string sourceReceiptSha256) => Encode(writer =>
    {
        writer.Field(AuditEventDomain);
        writer.Field(auditEvent.SchemaVersion);
        writer.Field(auditEvent.ContractId);
        writer.Field(auditEvent.ProducerModule);
        writer.Field(auditEvent.AuditEventId);
        writer.Field(auditEvent.SubjectId);
        writer.Field(auditEvent.SoulId);
        writer.Field(auditEvent.DeviceBindingId);
        writer.Field(auditEvent.PlatformAccountId);
        writer.Field(auditEvent.TraceId);
        writer.Field(auditEvent.IdempotencyKey);
        writer.Field(auditEvent.OccurredAt);
        writer.Field(auditEvent.PrivacyClass);
        writer.Field(auditEvent.EventType);
        writer.Field(auditEvent.Outcome);
        writer.Field(auditEvent.SourceContractId);
        writer.Field(auditEvent.EvidenceDigest);
        writer.Field(sourceReceiptSha256);
        writer.Field(auditEvent.Labels.Count);
        foreach (var pair in auditEvent.Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.Field(pair.Key);
            writer.Field(pair.Value);
        }
    });

    public static byte[] PersistedRecord(string eventIntegritySha256, string releaseBomSha256) => Encode(writer =>
    {
        writer.Field(PersistedRecordDomain);
        writer.Field(eventIntegritySha256);
        writer.Field(releaseBomSha256);
    });

    public static byte[] ScopeIdempotency(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string idempotencyKey) => Encode(writer =>
    {
        writer.Field(ScopeIdempotencyDomain);
        writer.Field(soulId);
        writer.Field(deviceBindingId);
        writer.Field(platformAccountId);
        writer.Field(idempotencyKey);
    });

    private static byte[] Encode(Action<CanonicalFieldWriter> write)
    {
        using var writer = new CanonicalFieldWriter();
        write(writer);
        return writer.ToArray();
    }

    private sealed class CanonicalFieldWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public void Field(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
                _stream.Write(length);
                _stream.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public void Field(Guid value) => Field(value.ToString("N"));
        public void Field(Guid? value) => Field(value?.ToString("N") ?? string.Empty);
        public void Field(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
        public void Field(bool value) => Field(value ? "true" : "false");
        public void Field(DateTimeOffset value) => Field(value.ToString("O", CultureInfo.InvariantCulture));
        public byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}

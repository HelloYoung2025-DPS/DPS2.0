using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry.Contracts;

namespace Dps.MemoryEventLedger;

internal sealed record SoulResolutionBindingRequestV2(
    Guid EventId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt)
{
    public void Validate()
    {
        MemoryContractValidationV2.RequireNonEmpty(EventId, nameof(EventId));
        MemoryContractValidationV2.RequireSoulId(SoulId, nameof(SoulId));
        MemoryContractValidationV2.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        MemoryContractValidationV2.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        MemoryContractValidationV2.RequireTraceId(TraceId, nameof(TraceId));
        MemoryContractValidationV2.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        MemoryContractValidationV2.RequireUtc(OccurredAt, nameof(OccurredAt));
    }
}

public sealed class VerifiedSoulResolutionCapabilityV2
{
    internal VerifiedSoulResolutionCapabilityV2() { }
    public override string ToString() => $"{nameof(VerifiedSoulResolutionCapabilityV2)} {{ Seal = [REDACTED] }}";
}

public sealed class VerifiedObservationReceiptCapabilityV2
{
    internal VerifiedObservationReceiptCapabilityV2() { }
    public override string ToString() => $"{nameof(VerifiedObservationReceiptCapabilityV2)} {{ Seal = [REDACTED] }}";
}

public sealed class PreparedMemoryEventV2
{
    internal PreparedMemoryEventV2(MemoryEventV2 memoryEvent) => Event = memoryEvent;
    public MemoryEventV2 Event { get; }
    public override string ToString() => $"{nameof(PreparedMemoryEventV2)} {{ EventId = {Event.EventId}, Seal = [REDACTED] }}";
}

internal sealed record SoulResolutionAuthoritySnapshotV2(
    SoulResolved Resolution,
    byte[] CanonicalRawBytes,
    long ResolutionRevision,
    string Issuer,
    string Audience,
    string KeyRole,
    string KeyId,
    long TrustEpoch,
    long RevocationEpoch,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

internal interface ISoulResolutionAuthoritySourceV2
{
    Task<SoulResolutionAuthoritySnapshotV2> ReadCurrentAsync(
        SoulResolutionBindingRequestV2 request,
        CancellationToken cancellationToken);
}

internal sealed class FixedSoulResolutionAuthorityV2
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
    private readonly ConditionalWeakTable<VerifiedSoulResolutionCapabilityV2, SoulSeal> _seals = new();
    private readonly ISoulResolutionAuthoritySourceV2 _source;
    private readonly TimeProvider _clock;
    private readonly Guid _authorityInstanceId = Guid.NewGuid();

    internal FixedSoulResolutionAuthorityV2(ISoulResolutionAuthoritySourceV2 source, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        if (!source.GetType().IsSealed || source.GetType().IsPublic || source.GetType().IsNestedPublic)
            throw new UnauthorizedAccessException("Soul authority source must be an exact non-public sealed composition type.");
        _source = source;
        _clock = clock;
    }

    internal async Task<VerifiedSoulResolutionCapabilityV2> IssueAsync(
        SoulResolutionBindingRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var snapshot = await _source.ReadCurrentAsync(request, cancellationToken);
        var seal = ValidateSnapshot(request, snapshot, _clock.GetUtcNow());
        var capability = new VerifiedSoulResolutionCapabilityV2();
        _seals.Add(capability, seal);
        return capability;
    }

    internal async Task<SoulSeal> RevalidateAsync(
        VerifiedSoulResolutionCapabilityV2 capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (!_seals.TryGetValue(capability, out var original) || original.AuthorityInstanceId != _authorityInstanceId)
            throw new MemoryCapabilityException("Soul capability was not issued by this fixed authority instance.");

        if (original.ExpiresAt <= _clock.GetUtcNow())
            throw new MemoryCapabilityException("Soul capability is exactly expired or stale.");

        var current = await _source.ReadCurrentAsync(original.Request, cancellationToken);
        var revalidated = ValidateSnapshot(original.Request, current, _clock.GetUtcNow());
        if (revalidated.ResolutionRevision != original.ResolutionRevision ||
            revalidated.TrustEpoch != original.TrustEpoch || revalidated.RevocationEpoch != original.RevocationEpoch ||
            !MemoryContractValidationV2.FixedTimeEquals(revalidated.ResolutionSha256, original.ResolutionSha256))
            throw new MemoryCapabilityException("Soul resolution is stale, revoked, replaced, or no longer current.");
        return original;
    }

    private SoulSeal ValidateSnapshot(
        SoulResolutionBindingRequestV2 request,
        SoulResolutionAuthoritySnapshotV2 snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Resolution);
        ArgumentNullException.ThrowIfNull(snapshot.CanonicalRawBytes);
        snapshot.Resolution.Validate();
        if (!snapshot.IsCurrent) throw new MemoryCapabilityException("Soul resolution is not current.");
        if (snapshot.ResolutionRevision < 1 || snapshot.TrustEpoch < 1 || snapshot.RevocationEpoch < 0)
            throw new MemoryCapabilityException("Soul resolution revision or trust epochs are invalid.");
        if (snapshot.IssuedAt.Offset != TimeSpan.Zero || snapshot.ExpiresAt.Offset != TimeSpan.Zero ||
            snapshot.IssuedAt > now || snapshot.ExpiresAt <= now || snapshot.ExpiresAt <= snapshot.IssuedAt ||
            snapshot.ExpiresAt - snapshot.IssuedAt > MaximumLifetime)
            throw new MemoryCapabilityException("Soul resolution is future-issued, expired, equal-expiry, or outside its maximum lifetime.");
        MemoryContractValidationV2.RequireExact(snapshot.Issuer, IdentityAuthorityAuditV2.CurrentIssuer, nameof(snapshot.Issuer));
        MemoryContractValidationV2.RequireExact(snapshot.Audience, IdentityAuthorityAuditV2.CurrentAudience, nameof(snapshot.Audience));
        MemoryContractValidationV2.RequireExact(snapshot.KeyRole, IdentityAuthorityAuditV2.CurrentKeyRole, nameof(snapshot.KeyRole));
        MemoryContractValidationV2.RequireKeyId(snapshot.KeyId, nameof(snapshot.KeyId));
        if (!string.Equals(snapshot.Resolution.SoulId, request.SoulId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Resolution.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Resolution.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Resolution.TraceId, request.TraceId, StringComparison.Ordinal))
            throw new MemoryCapabilityException("Soul resolution scope does not exactly match the append request.");

        var canonical = SoulResolvedCanonicalizerV1.Serialize(snapshot.Resolution);
        if (!snapshot.CanonicalRawBytes.AsSpan().SequenceEqual(canonical))
            throw new MemoryCapabilityException("Soul authority raw bytes are not the exact canonical resolution bytes.");
        var resolutionSha256 = Convert.ToHexStringLower(SHA256.HashData(canonical));
        return new SoulSeal(
            _authorityInstanceId,
            request,
            resolutionSha256,
            snapshot.ResolutionRevision,
            snapshot.KeyId,
            snapshot.TrustEpoch,
            snapshot.RevocationEpoch,
            snapshot.IssuedAt,
            snapshot.ExpiresAt);
    }

    internal sealed record SoulSeal(
        Guid AuthorityInstanceId,
        SoulResolutionBindingRequestV2 Request,
        string ResolutionSha256,
        long ResolutionRevision,
        string KeyId,
        long TrustEpoch,
        long RevocationEpoch,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt)
    {
        internal IdentityAuthorityAuditV2 ToAudit() => new(
            ResolutionSha256, ResolutionRevision,
            IdentityAuthorityAuditV2.CurrentIssuer, IdentityAuthorityAuditV2.CurrentAudience,
            IdentityAuthorityAuditV2.CurrentKeyRole, KeyId, TrustEpoch, RevocationEpoch, IssuedAt, ExpiresAt);
    }
}

internal sealed record ResultAuthorityStateV2(string KeyId, long TrustEpoch, long RevocationEpoch, bool IsCurrent);

internal interface IResultAuthorityStateSourceV2
{
    ResultAuthorityStateV2 ReadCurrent();
}

internal sealed class FixedObservationReceiptAuthorityV2 : IDisposable
{
    private static readonly TimeSpan MaximumReceiptAge = TimeSpan.FromMinutes(5);
    private readonly ConditionalWeakTable<VerifiedObservationReceiptCapabilityV2, ObservationSeal> _seals = new();
    private readonly ECDsa _publicKey;
    private readonly TimeProvider _clock;
    private readonly IResultAuthorityStateSourceV2 _stateSource;
    private readonly Guid _authorityInstanceId = Guid.NewGuid();
    private readonly object _sync = new();

    internal FixedObservationReceiptAuthorityV2(
        ReadOnlySpan<byte> fixedPublicKeySpki,
        IResultAuthorityStateSourceV2 stateSource,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(stateSource);
        if (!stateSource.GetType().IsSealed || stateSource.GetType().IsPublic || stateSource.GetType().IsNestedPublic)
            throw new UnauthorizedAccessException("Result authority state source must be an exact non-public sealed composition type.");
        ArgumentNullException.ThrowIfNull(clock);
        _publicKey = ECDsa.Create();
        try
        {
            _publicKey.ImportSubjectPublicKeyInfo(fixedPublicKeySpki, out var read);
            var parameters = _publicKey.ExportParameters(false);
            if (read != fixedPublicKeySpki.Length || _publicKey.KeySize != 256 ||
                !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
                throw new ArgumentException("Observation receipt trust root must be exact NIST P-256 SPKI without trailing bytes.", nameof(fixedPublicKeySpki));
        }
        catch { _publicKey.Dispose(); throw; }
        _stateSource = stateSource;
        _clock = clock;
    }

    internal VerifiedObservationReceiptCapabilityV2 Issue(
        Guid eventId,
        ReadOnlyMemory<byte> signedReceiptRaw,
        IReadOnlyList<InterestSignalV2> signals)
    {
        MemoryContractValidationV2.RequireNonEmpty(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(signals);
        var seal = Verify(eventId, signedReceiptRaw, signals, _clock.GetUtcNow());
        var capability = new VerifiedObservationReceiptCapabilityV2();
        _seals.Add(capability, seal);
        return capability;
    }

    internal ObservationSeal Revalidate(VerifiedObservationReceiptCapabilityV2 capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (!_seals.TryGetValue(capability, out var original) || original.AuthorityInstanceId != _authorityInstanceId)
            throw new MemoryCapabilityException("Observation capability was not issued by this fixed authority instance.");
        var current = Verify(original.EventId, original.RawReceipt, original.Signals, _clock.GetUtcNow());
        if (!MemoryContractValidationV2.FixedTimeEquals(current.SignedReceiptSha256, original.SignedReceiptSha256) ||
            current.TrustEpoch != original.TrustEpoch || current.RevocationEpoch != original.RevocationEpoch)
            throw new MemoryCapabilityException("Observation receipt capability is stale, replaced, or revoked.");
        return original;
    }

    private ObservationSeal Verify(
        Guid eventId,
        ReadOnlyMemory<byte> signedReceiptRaw,
        IReadOnlyList<InterestSignalV2> signals,
        DateTimeOffset now)
    {
        var signedReceipt = ConsumedSignedCommandReceiptV1.ParseExact(signedReceiptRaw.Span);
        var state = _stateSource.ReadCurrent();
        ArgumentNullException.ThrowIfNull(state);
        MemoryContractValidationV2.RequireKeyId(state.KeyId, nameof(state.KeyId));
        if (!state.IsCurrent || state.TrustEpoch < 1 || state.RevocationEpoch < 0)
            throw new MemoryCapabilityException("Result authority key is revoked, unavailable, or has invalid trust epochs.");
        var signature = Convert.FromBase64String(signedReceipt.SignatureBase64);
        var canonicalSigned = signedReceipt.CanonicalSignaturePayload();
        try
        {
            bool verified;
            lock (_sync)
                verified = _publicKey.VerifyData(canonicalSigned, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!verified) throw new MemoryCapabilityException("Observation receipt signature is invalid for the fixed result authority.");
        }
        finally { CryptographicOperations.ZeroMemory(signature); CryptographicOperations.ZeroMemory(canonicalSigned); }

        var receipt = signedReceipt.Receipt;
        if (eventId != receipt.CommandId)
            throw new MemoryCapabilityException("Memory event_id must equal the exact signed command_id; a receipt cannot be replayed under another event identity.");
        if (receipt.Outcome != "SUCCESS" || !receipt.NativeResultVerified || !receipt.PostconditionVerified ||
            receipt.NativeResultId is null || receipt.RetryAllowed || signedReceipt.NativeEvidenceSha256 is null ||
            signedReceipt.PostconditionEvidenceSha256 is null || !string.Equals(receipt.ResultCode, "OBSERVATION_VERIFIED", StringComparison.Ordinal))
            throw new MemoryCapabilityException("Only exact SUCCESS observation receipts with native and postcondition proof may enter memory.");
        if (receipt.OccurredAt > now || now - receipt.OccurredAt >= MaximumReceiptAge)
            throw new MemoryCapabilityException("Observation receipt is future-issued, stale, or exactly expired.");

        var signalsSnapshot = signals.Select(static signal => signal with { }).ToArray();
        var signalsDigest = MemorySignalCanonicalizerV2.ComputeSha256(signalsSnapshot);
        if (!MemoryContractValidationV2.FixedTimeEquals(signalsDigest, signedReceipt.PostconditionEvidenceSha256))
            throw new MemoryCapabilityException("Signed postcondition evidence does not bind the exact canonical signal set.");
        var signedReceiptSha256 = Convert.ToHexStringLower(SHA256.HashData(signedReceiptRaw.Span));
        return new ObservationSeal(
            _authorityInstanceId, eventId, signedReceiptRaw.ToArray(), signalsSnapshot, signedReceiptSha256,
            signedReceipt.NativeEvidenceSha256, signalsDigest,
            receipt.ReceiptId, receipt.CommandId, receipt.SoulId, receipt.DeviceBindingId,
            receipt.PlatformAccountId, receipt.TraceId, receipt.IdempotencyKey, receipt.OccurredAt,
            state.KeyId, state.TrustEpoch, state.RevocationEpoch, receipt.OccurredAt, receipt.OccurredAt + MaximumReceiptAge);
    }

    public void Dispose() => _publicKey.Dispose();

    internal sealed record ObservationSeal(
        Guid AuthorityInstanceId,
        Guid EventId,
        byte[] RawReceipt,
        IReadOnlyList<InterestSignalV2> Signals,
        string SignedReceiptSha256,
        string ContentDigest,
        string SignalsDigest,
        Guid ReceiptId,
        Guid CommandId,
        string SoulId,
        string DeviceBindingId,
        string PlatformAccountId,
        string TraceId,
        string IdempotencyKey,
        DateTimeOffset OccurredAt,
        string KeyId,
        long TrustEpoch,
        long RevocationEpoch,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt)
    {
        internal ResultAuthorityAuditV2 ToAudit() => new(
            ResultAuthorityAuditV2.CurrentIssuer, ResultAuthorityAuditV2.CurrentAudience,
            ResultAuthorityAuditV2.CurrentKeyRole, KeyId, TrustEpoch, RevocationEpoch, IssuedAt, ExpiresAt);
    }
}

internal static class SoulResolvedCanonicalizerV1
{
    internal static byte[] Serialize(SoulResolved value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", value.SchemaVersion); writer.WriteString("contract_id", value.ContractId);
            writer.WriteString("producer_module", value.ProducerModule); writer.WriteString("soul_id", value.SoulId);
            writer.WriteString("device_binding_id", value.DeviceBindingId); writer.WriteString("platform_account_id", value.PlatformAccountId);
            writer.WriteString("trace_id", value.TraceId); writer.WriteString("idempotency_key", value.IdempotencyKey);
            writer.WriteString("occurred_at", value.OccurredAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("privacy_class", value.PrivacyClass); writer.WriteString("alias_kind", value.AliasKind);
            writer.WriteString("alias_digest", value.AliasDigest); writer.WriteString("alias_key_id", value.AliasKeyId); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

public sealed class MemoryCapabilityException : UnauthorizedAccessException
{
    public MemoryCapabilityException(string message) : base(message) { }
}

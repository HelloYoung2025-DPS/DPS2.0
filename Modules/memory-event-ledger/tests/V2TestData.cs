using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry.Contracts;

namespace Dps.MemoryEventLedger.Tests;

internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Set(DateTimeOffset value) => _now = value;
}

internal sealed class TestSoulAuthoritySource : ISoulResolutionAuthoritySourceV2
{
    internal Func<SoulResolutionBindingRequestV2, SoulResolutionAuthoritySnapshotV2>? Mutate { get; set; }
    internal long Revision { get; set; } = 7;
    internal long RevocationEpoch { get; set; } = 3;
    internal bool IsCurrent { get; set; } = true;
    internal DateTimeOffset Now { get; set; }

    public Task<SoulResolutionAuthoritySnapshotV2> ReadCurrentAsync(
        SoulResolutionBindingRequestV2 request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Mutate is not null) return Task.FromResult(Mutate(request));
        var resolution = new SoulResolved(
            request.SoulId, request.DeviceBindingId, request.PlatformAccountId, request.TraceId,
            "idem_" + new string('9', 64), Now.AddMinutes(-1), "email", new string('a', 64), "soul-test-key");
        return Task.FromResult(new SoulResolutionAuthoritySnapshotV2(
            resolution, SoulResolvedCanonicalizerV1.Serialize(resolution), Revision,
            IdentityAuthorityAuditV2.CurrentIssuer, IdentityAuthorityAuditV2.CurrentAudience,
            IdentityAuthorityAuditV2.CurrentKeyRole, "soul-test-key", 4, RevocationEpoch,
            Now.AddMinutes(-1), Now.AddMinutes(4), IsCurrent));
    }
}

internal sealed class TestResultAuthorityStateSource : IResultAuthorityStateSourceV2
{
    internal string KeyId { get; set; } = "executor-test-key";
    internal long TrustEpoch { get; set; } = 8;
    internal long RevocationEpoch { get; set; } = 2;
    internal bool IsCurrent { get; set; } = true;
    public ResultAuthorityStateV2 ReadCurrent() => new(KeyId, TrustEpoch, RevocationEpoch, IsCurrent);
}

internal static class V2TestData
{
    internal static readonly DateTimeOffset Now = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    internal static SoulResolutionBindingRequestV2 Request(char value = 'a', Guid? eventId = null) => new(
        eventId ?? Guid.Parse("10000000-0000-0000-0000-000000000001"),
        "soul_" + new string(value, 64), "db_" + new string(value, 32), "pa_" + new string(value, 32),
        "trace_" + new string(value, 32), "idem_" + new string(value, 64), Now.AddMinutes(-1));

    internal static MemoryAppendRequestV2 AppendRequest(
        ECDsa signer,
        SoulResolutionBindingRequestV2 request,
        IReadOnlyList<InterestSignalV2> signals,
        byte[]? raw = null) => new(
            MemoryAppendRequestV2.CurrentSchemaVersion, MemoryAppendRequestV2.CurrentContractId,
            MemoryAppendRequestV2.CurrentProducerModule, request.EventId, request.SoulId, request.DeviceBindingId,
            request.PlatformAccountId, request.TraceId, request.IdempotencyKey, request.OccurredAt, "personal",
            Convert.ToBase64String(raw ?? SignedReceipt(signer, request, signals, request.OccurredAt)), signals);

    internal static InterestSignalV2[] Signals(string topic = "coffee") =>
        [new(topic, 0.75m, new string('f', 64))];

    internal static (PostgresMemoryEventLedgerV2 Ledger, TestSoulAuthoritySource Source, TestResultAuthorityStateSource ResultState, TestTimeProvider Clock, ECDsa Signer) Ledger()
    {
        var clock = new TestTimeProvider(Now);
        var source = new TestSoulAuthoritySource { Now = Now };
        var soul = new FixedSoulResolutionAuthorityV2(source, clock);
        var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var resultState = new TestResultAuthorityStateSource();
        var result = new FixedObservationReceiptAuthorityV2(signer.ExportSubjectPublicKeyInfo(), resultState, clock);
        var options = new MemoryEventLedgerV2Options(
            "Host=unused;Username=admin", "Host=unused;Username=runtime", "memory_v2", "memory_admin", "memory_runtime",
            Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray());
        return (new PostgresMemoryEventLedgerV2(options, soul, result), source, resultState, clock, signer);
    }

    internal static byte[] SignedReceipt(
        ECDsa signer,
        SoulResolutionBindingRequestV2 request,
        IReadOnlyList<InterestSignalV2> signals,
        DateTimeOffset? occurredAt = null,
        string outcome = "SUCCESS",
        string resultCode = "OBSERVATION_VERIFIED",
        bool nativeVerified = true,
        bool postconditionVerified = true,
        bool retryAllowed = false,
        string? nativeEvidence = null,
        string? postconditionEvidence = null)
    {
        occurredAt ??= Now.AddMinutes(-1);
        nativeEvidence ??= new string('a', 64);
        postconditionEvidence ??= MemorySignalCanonicalizerV2.ComputeSha256(signals);
        var receipt = new ConsumedCommandReceiptV1(
            "1.0.0", "command.receipt/v1", "executor-gateway",
            request.EventId,
            request.EventId,
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            1, request.SoulId, request.DeviceBindingId, request.PlatformAccountId, request.TraceId, request.IdempotencyKey,
            occurredAt.Value, "internal", outcome,
            nativeVerified ? Guid.Parse("50000000-0000-0000-0000-000000000001") : null,
            nativeVerified, postconditionVerified, EvidenceDigest(nativeEvidence, postconditionEvidence), retryAllowed, resultCode);
        var unsigned = new ConsumedSignedCommandReceiptV1(
            "1.0.0", "command.receipt.signed/v1", "executor-gateway", "dps.executor-gateway.command-receipt/v1",
            "dps.canonical.uint32be-length-prefixed-utf8/v1", "sha256:dps.command-orchestrator.command-receipt/v1",
            "sha256:dps.command-orchestrator.command-dispatch/v1", "sha256:dps.command-orchestrator.command-receipt-evidence/v1",
            "ecdsa-p256-sha256", "ieee-p1363-fixed-field-concatenation", "base64", "executor-gateway", "command-orchestrator:receipt",
            receipt.ReceiptId, receipt.CommandId, receipt.LeaseId, receipt.Attempt, receipt.SoulId, receipt.DeviceBindingId,
            receipt.PlatformAccountId, receipt.TraceId, receipt.IdempotencyKey, receipt.OccurredAt, receipt.PrivacyClass,
            ReceiptDigest(receipt), new string('b', 64), new string('c', 64), new string('d', 64), 11, new string('e', 64),
            nativeEvidence, postconditionEvidence, receipt, Convert.ToBase64String(new byte[64]));
        var payload = unsigned.CanonicalSignaturePayload();
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        try { return (unsigned with { SignatureBase64 = Convert.ToBase64String(signature) }).CanonicalJson(); }
        finally { CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(signature); }
    }

    private static string ReceiptDigest(ConsumedCommandReceiptV1 receipt)
    {
        var bytes = Encode(writer =>
        {
            writer.Token("dps.command-orchestrator.command-receipt/v1"); writer.Field("schema_version", receipt.SchemaVersion);
            writer.Field("contract_id", receipt.ContractId); writer.Field("producer_module", receipt.ProducerModule); writer.Field("receipt_id", receipt.ReceiptId);
            writer.Field("command_id", receipt.CommandId); writer.Field("lease_id", receipt.LeaseId); writer.Field("attempt", receipt.Attempt);
            writer.Field("soul_id", receipt.SoulId); writer.Field("device_binding_id", receipt.DeviceBindingId); writer.Field("platform_account_id", receipt.PlatformAccountId);
            writer.Field("trace_id", receipt.TraceId); writer.Field("idempotency_key", receipt.IdempotencyKey); writer.Field("occurred_at", receipt.OccurredAt);
            writer.Field("privacy_class", receipt.PrivacyClass); writer.Field("outcome", receipt.Outcome); writer.NullableField("native_result_id", receipt.NativeResultId);
            writer.Field("native_result_verified", receipt.NativeResultVerified); writer.Field("postcondition_verified", receipt.PostconditionVerified);
            writer.Field("evidence_digest", receipt.EvidenceDigest); writer.Field("retry_allowed", receipt.RetryAllowed); writer.Field("result_code", receipt.ResultCode);
        });
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string EvidenceDigest(string? native, string? postcondition)
    {
        var bytes = Encode(writer => { writer.Token("dps.command-orchestrator.command-receipt-evidence/v1"); writer.NullableField("native_evidence_sha256", native); writer.NullableField("postcondition_evidence_sha256", postcondition); });
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Encode(Action<TestFieldWriter> action) { using var writer = new TestFieldWriter(); action(writer); return writer.ToArray(); }

    private sealed class TestFieldWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        internal void Field(string name, string value) { Token(name); Token(value); }
        internal void Field(string name, Guid value) => Field(name, value.ToString("N"));
        internal void Field(string name, int value) => Field(name, value.ToString(CultureInfo.InvariantCulture));
        internal void Field(string name, bool value) => Field(name, value ? "true" : "false");
        internal void Field(string name, DateTimeOffset value) => Field(name, value.ToString("O", CultureInfo.InvariantCulture));
        internal void NullableField(string name, string? value) { Field($"{name}.present", value is not null); if (value is not null) Field($"{name}.value", value); }
        internal void NullableField(string name, Guid? value) { Field($"{name}.present", value.HasValue); if (value.HasValue) Field($"{name}.value", value.Value); }
        internal void Token(string value) { var bytes = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length)); _stream.Write(length); _stream.Write(bytes); }
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}

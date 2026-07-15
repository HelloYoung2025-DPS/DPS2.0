using Dps.CommandOrchestrator.Contracts;
using System.Security.Cryptography;
using Xunit;

namespace Dps.AuditMetrics.Tests;

public sealed class ImmutableAuditMetricsTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void PostgresIdentifiersRejectTrailingNewlines()
    {
        Assert.Throws<ArgumentException>(() =>
            new AuditMetricsPostgresOptions("Host=unused", "audit_test\n", "runtime_role").Validate());
        Assert.Throws<ArgumentException>(() =>
            new AuditMetricsMigrationOptions(
                "Host=unused",
                "audit_test",
                "runtime_role\n").Validate());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DuplicateIsNoOpAndConflictingReceiptIsQuarantined()
    {
        var service = Service(); var receipt = Receipt(); var first = Append(service, receipt); var duplicate = Append(service, receipt);
        var conflictReceipt = receipt with { Outcome = CommandReceiptV1.Failed, NativeResultVerified = true, PostconditionVerified = false, EvidenceDigest = new string('b', 64), RetryAllowed = false, ResultCode = "POSTCONDITION_FAILED" };
        var conflict = Append(service, conflictReceipt);
        Assert.Equal(AuditAppendDisposition.Inserted, first.Disposition); Assert.Equal(AuditAppendDisposition.DuplicateNoOp, duplicate.Disposition); Assert.Equal(AuditAppendDisposition.Quarantined, conflict.Disposition); Assert.Equal(1, service.QuarantineCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IdempotencyAndSourceOnlyReceiptChangesWithSameReceiptIdAreQuarantined()
    {
        var service = Service();
        var receipt = Receipt();

        Assert.Equal(AuditAppendDisposition.Inserted, Append(service, receipt).Disposition);
        var idempotencyConflict = Append(service, receipt with { IdempotencyKey = "idem_" + new string('b', 64) });
        var sourceOnlyConflict = Append(service, receipt with { LeaseId = Guid.Parse("87000000-0000-0000-0000-000000000007") });

        Assert.Equal(AuditAppendDisposition.Quarantined, idempotencyConflict.Disposition);
        Assert.Equal(AuditAppendDisposition.Quarantined, sourceOnlyConflict.Disposition);
        Assert.Equal(2, service.QuarantineCount);
        Assert.Equal("idem_" + new string('2', 64), Assert.Single(service.ReadScope(Soul, Device, Account)).IdempotencyKey);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OutOfOrderArrivalIsPreservedAndReadChronologically()
    {
        var service = Service(); var later = Receipt() with { ReceiptId = Guid.Parse("81000000-0000-0000-0000-000000000001"), OccurredAt = Now.AddMinutes(2) }; var earlier = Receipt() with { ReceiptId = Guid.Parse("82000000-0000-0000-0000-000000000002"), OccurredAt = Now };
        Append(service, later); Append(service, earlier); var events = service.ReadScope(Soul, Device, Account); Assert.Equal(2, events.Count); Assert.True(events[0].OccurredAt < events[1].OccurredAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CrossSoulDeviceAndAccountQueriesCannotLeak()
    {
        var service = Service(); Append(service, Receipt()); Assert.Empty(service.ReadScope("soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Device, Account)); Assert.Empty(service.ReadScope(Soul, "db_" + new string('b', 32), Account)); Assert.Empty(service.ReadScope(Soul, Device, "pa_" + new string('c', 32))); Assert.Single(service.ReadScope(Soul, Device, Account));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UnauthorizedRelayAndRawPiiOrSecretResultCodesFailClosed()
    {
        var unauthorized = new ImmutableAuditMetrics(new FakeRelayVerifier(valid: false)); Assert.Throws<UnauthorizedAccessException>(() => Append(unauthorized, Receipt()));
        var service = Service(); Assert.Throws<ArgumentException>(() => Append(service, Receipt() with { ResultCode = "user@example.com" })); Assert.Throws<ArgumentException>(() => Append(service, Receipt() with { ResultCode = "password=abc" }));
        Assert.Throws<ArgumentException>(() => Append(service, Receipt() with { TraceId = "user@example.com" }));
        Assert.Throws<ArgumentException>(() => Append(service, Receipt() with { IdempotencyKey = "password=abc" }));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void OutcomeMappingIsPreservedAndUnknownMajorIsRejected()
    {
        var service = Service(); var failed = Receipt() with { Outcome = CommandReceiptV1.Failed, NativeResultVerified = true, PostconditionVerified = false, ResultCode = "POSTCONDITION_FAILED" }; Append(service, failed); var auditEvent = Assert.Single(service.ReadScope(Soul, Device, Account)); Assert.Equal("failed", auditEvent.Labels["verification_class"]); Assert.Equal(CommandReceiptV1.Failed, auditEvent.Outcome);
        Assert.Throws<NotSupportedException>(() => Append(Service(), Receipt() with { SchemaVersion = "2.0.0" }));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void RealEcdsaVerifierRejectsTamperingAndAcceptsExactEnvelope()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = signer.ExportSubjectPublicKeyInfo(); using var verifier = new EcdsaAuditRelayAuthorizationVerifier(publicKey); var service = new ImmutableAuditMetrics(verifier); var receipt = Receipt();
        var signed = SignEnvelope(signer, Envelope(receipt) with { SignatureBase64 = "" });
        Assert.Equal(AuditAppendDisposition.Inserted, service.AppendReceipt(receipt, signed, Now).Disposition);
        Assert.Throws<UnauthorizedAccessException>(() => service.AppendReceipt(receipt with { ReceiptId = Guid.NewGuid() }, signed, Now));
        Assert.Throws<UnauthorizedAccessException>(() => service.AppendReceipt(receipt with { EvidenceDigest = new string('e', 64) }, signed, Now));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void CanonicalBindingsMatchGoldenVectors()
    {
        var receipt = Receipt();
        Assert.Equal("78cafd8be104aa37aee99fa84e2421a6e1d92a1914f22c0b50c863ff056581d5", AuditRelayAuthorizationBinding.ComputeReceiptSha256(receipt));

        var canonicalEnvelope = EcdsaAuditRelayAuthorizationVerifier.CanonicalBytes(Envelope(receipt) with { SignatureBase64 = "" });
        try
        {
            Assert.Equal("f073f9082c84ff50c711ccb3fb0743421b12b36a499131e59f9712641b22baac", Convert.ToHexStringLower(SHA256.HashData(canonicalEnvelope)));
        }
        finally { CryptographicOperations.ZeroMemory(canonicalEnvelope); }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void DelimiterCollisionCannotReuseReceiptDigestOrSignature()
    {
        var receiptA = Receipt() with { TraceId = "a", IdempotencyKey = "b|c" };
        var receiptB = Receipt() with { TraceId = "a|b", IdempotencyKey = "c" };
        Assert.NotEqual(AuditRelayAuthorizationBinding.ComputeReceiptSha256(receiptA), AuditRelayAuthorizationBinding.ComputeReceiptSha256(receiptB));

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaAuditRelayAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        var signedForA = SignEnvelope(signer, Envelope(receiptA) with { SignatureBase64 = "" });

        Assert.Equal(receiptA.ReceiptId, verifier.Verify(receiptA, signedForA, Now).ReceiptId);
        Assert.Throws<UnauthorizedAccessException>(() => verifier.Verify(receiptB, signedForA, Now));
    }

    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; private const string Device = "db_11111111111111111111111111111111"; private const string Account = "pa_22222222222222222222222222222222"; private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static ImmutableAuditMetrics Service() => new(new FakeRelayVerifier(valid: true));
    private static AuditAppendResult Append(ImmutableAuditMetrics service, CommandReceiptV1 receipt) => service.AppendReceipt(receipt, Envelope(receipt), Now);
    private static AuditRelayEnvelope Envelope(CommandReceiptV1 receipt) => new("command-orchestrator", "audit:command-receipt", receipt.ReceiptId, AuditRelayAuthorizationBinding.ComputeReceiptSha256(receipt), Now.AddMinutes(1), new string('d', 64), "fake-signature");
    private static CommandReceiptV1 Receipt() => new(CommandReceiptV1.CurrentSchemaVersion, CommandReceiptV1.CurrentContractId, CommandReceiptV1.CurrentProducerModule, Guid.Parse("83000000-0000-0000-0000-000000000003"), Guid.Parse("84000000-0000-0000-0000-000000000004"), Guid.Parse("86000000-0000-0000-0000-000000000006"), 1, Soul, Device, Account, "trace_" + new string('1', 32), "idem_" + new string('2', 64), Now, "internal", CommandReceiptV1.Success, Guid.Parse("85000000-0000-0000-0000-000000000005"), true, true, new string('a', 64), false, "VERIFIED");

    private static AuditRelayEnvelope SignEnvelope(ECDsa signer, AuditRelayEnvelope unsigned)
    {
        var canonical = EcdsaAuditRelayAuthorizationVerifier.CanonicalBytes(unsigned);
        try { return unsigned with { SignatureBase64 = Convert.ToBase64String(signer.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) }; }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private sealed class FakeRelayVerifier(bool valid) : IAuditRelayAuthorizationVerifier
    {
        public VerifiedAuditRelayAuthorization Verify(CommandReceiptV1 receipt, AuditRelayEnvelope envelope, DateTimeOffset now)
        {
            if (!valid) throw new UnauthorizedAccessException("FAKE: relay signature rejected.");
            return new VerifiedAuditRelayAuthorization(envelope.CallerModule, envelope.AuthScope, envelope.ReceiptId, envelope.ReceiptSha256, envelope.ExpiresAt, envelope.ReleaseBomSha256);
        }
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Dps.PolicyApproval.Contracts;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed class ApprovalSubmissionLifecycleContractTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Device = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Account = "pa_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Trace = "trace_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Idempotency = "idem_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Signature = Convert.ToBase64String(new byte[64]);

    [Fact, Trait("Category", "Contract")]
    public void IntentCommitmentBindsAttemptScopeAuthorizationBomAndNativeRequest()
    {
        var value = Intent();
        var digest = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(value);
        Assert.Equal("b1ffec40cb4746f7b7c9ad540eee86c1109ed458013d1b54b40f2fb742cb24db", digest);
        Assert.NotEqual(digest, ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(value with { Attempt = 2 }));
        Assert.NotEqual(digest, ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(value with { ReleaseBomGeneration = 2 }));
        Assert.NotEqual(digest, ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(value with { NativeRequestBindingSha256 = new string('9', 64) }));
    }

    [Fact, Trait("Category", "Contract")]
    public void SignatureEncodingIsNotPartOfSemanticIntentCommitment()
    {
        var value = Intent();
        var alternateSignature = Convert.ToBase64String(Enumerable.Repeat((byte)1, 64).ToArray());
        Assert.Equal(
            ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(value),
            ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(value with { SignatureBase64 = alternateSignature }));
    }

    [Fact, Trait("Category", "Contract")]
    public void AcknowledgementCommitmentConsumesPendingAndSubmittedRequestProofs()
    {
        var value = Acknowledgement();
        var digest = ApprovalSubmissionLifecycleBinding.ComputeAcknowledgementSha256(value);
        Assert.NotEqual(digest, ApprovalSubmissionLifecycleBinding.ComputeAcknowledgementSha256(value with { PendingStateSha256 = new string('8', 64) }));
        Assert.NotEqual(digest, ApprovalSubmissionLifecycleBinding.ComputeAcknowledgementSha256(value with { SubmittedRequestSha256 = new string('7', 64) }));
    }

    [Fact, Trait("Category", "Contract")]
    public void PendingCannotHavePredecessorAndUnknownMustHaveOne()
    {
        Assert.Equal(
            "2aa3a61921d66ce1c09a3ff54bd8e6635811475748d2ae209ed286f43985290d",
            ApprovalSubmissionLifecycleBinding.ComputeStateSha256(State(ApprovalSubmissionStateV1.SubmissionPending)));
        Assert.Throws<ArgumentException>(() => (State(ApprovalSubmissionStateV1.SubmissionPending) with
        {
            PredecessorStateSha256 = new string('f', 64)
        }).Validate());
        Assert.Throws<ArgumentException>(() => State(ApprovalSubmissionStateV1.UnknownSubmission).Validate());
        (State(ApprovalSubmissionStateV1.UnknownSubmission) with
        {
            PredecessorStateSha256 = new string('f', 64)
        }).Validate();
    }

    [Fact, Trait("Category", "Contract")]
    public void UnknownStateAndRecoveryAttemptFailClosed()
    {
        Assert.Throws<NotSupportedException>(() => (State(ApprovalSubmissionStateV1.SubmissionPending) with { State = "RETRYABLE" }).Validate());
        var recovery = Recovery();
        Assert.Throws<ArgumentOutOfRangeException>(() => (recovery with { NextAttempt = 3 }).Validate());
        Assert.Throws<ArgumentException>(() => (recovery with { NextSubmissionAttemptId = recovery.SubmissionAttemptId }).Validate());
        Assert.Throws<ArgumentException>(() => (recovery with { NextLeaseId = recovery.PreviousLeaseId }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void AllLifecycleCodecsConsumeTheirSharedStrictCorpus()
    {
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.approval.submission.intent.v1.corpus.json",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "valid", "unknown-field", "missing-attempt-id", "attempt-zero",
                "bom-generation-zero", "uppercase-request-hash"
            },
            static payload => ApprovalSubmissionIntentV1Codec.Deserialize(payload));
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.approval.submission.acknowledgement.v1.corpus.json",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "valid", "unknown-field", "missing-pending-state", "attempt-four",
                "empty-native-submission", "submitted-request-short"
            },
            static payload => ApprovalSubmissionAcknowledgementV1Codec.Deserialize(payload));
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.approval.submission.reconciliation.v1.corpus.json",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "valid-not-submitted", "valid-submitted", "unknown-finding", "wrong-role",
                "missing-evidence", "unknown-field"
            },
            static payload => ApprovalSubmissionReconciliationV1Codec.Deserialize(payload));
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.approval.submission.recovery.v1.corpus.json",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "valid", "wrong-next-attempt", "missing-human-approval", "wrong-role",
                "same-attempt-zero", "unknown-field"
            },
            static payload => ApprovalSubmissionRecoveryV1Codec.Deserialize(payload));
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.approval.submission.state.v1.corpus.json",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "valid-pending", "valid-acknowledged", "valid-unknown", "unknown-state",
                "pending-with-predecessor", "ack-without-predecessor", "unknown-field"
            },
            static payload => ApprovalSubmissionStateV1Codec.Deserialize(payload));
    }

    [Fact, Trait("Category", "Contract")]
    public void LifecycleCodecRejectsNonCanonicalWireAndAcceptsSemanticJsonbOrderingOnly()
    {
        var value = Intent() with
        {
            SubmissionAttemptId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        };
        var payload = ApprovalSubmissionIntentV1Codec.Serialize(value);
        var json = Encoding.UTF8.GetString(payload);
        Assert.Contains("\"occurred_at\":\"2026-07-15T00:00:00Z\"", json, StringComparison.Ordinal);
        Assert.Equal(value, ApprovalSubmissionIntentV1Codec.Deserialize(payload));

        var whitespace = Encoding.UTF8.GetBytes("{\n" + json[1..]);
        Assert.ThrowsAny<Exception>(() => ApprovalSubmissionIntentV1Codec.Deserialize(whitespace));
        Assert.Equal(value, ApprovalSubmissionIntentV1Codec.DeserializeSemanticJsonb(whitespace));

        var duplicate = Encoding.UTF8.GetBytes(
            json[..^1] + ",\"signature_base64\":\"" + Signature + "\"}");
        Assert.ThrowsAny<Exception>(() => ApprovalSubmissionIntentV1Codec.DeserializeSemanticJsonb(duplicate));

        var uppercaseUuid = Encoding.UTF8.GetBytes(
            json.Replace(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
                StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => ApprovalSubmissionIntentV1Codec.DeserializeSemanticJsonb(uppercaseUuid));

        var offsetTime = Encoding.UTF8.GetBytes(
            json.Replace("2026-07-15T00:00:00Z", "2026-07-15T00:00:00+00:00", StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => ApprovalSubmissionIntentV1Codec.DeserializeSemanticJsonb(offsetTime));
        Assert.ThrowsAny<Exception>(() => ApprovalSubmissionIntentV1Codec.Deserialize([0xc3, 0x28]));
        Assert.ThrowsAny<Exception>(() => ApprovalSubmissionIntentV1Codec.Deserialize(
            new byte[ApprovalSubmissionIntentV1Codec.MaximumPayloadBytes + 1]));
    }

    private static void AssertCodecCorpus(
        string resourceName,
        IReadOnlySet<string> expectedCaseIds,
        Func<byte[], object> deserialize)
    {
        using var stream = typeof(ApprovalSubmissionIntentV1).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded contract corpus '{resourceName}' is missing.");
        var corpus = JsonNode.Parse(stream)?.AsObject()
            ?? throw new InvalidOperationException(
                $"Embedded contract corpus '{resourceName}' is invalid.");
        var basePayload = corpus["base"]?.AsObject()
            ?? throw new InvalidOperationException("Contract corpus base is missing.");
        var cases = corpus["cases"]?.AsArray()
            ?? throw new InvalidOperationException("Contract corpus cases are missing.");
        Assert.Equal(expectedCaseIds.Count, cases.Count);
        var observedCaseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caseNode in cases)
        {
            var contractCase = caseNode?.AsObject()
                ?? throw new InvalidOperationException("Contract corpus case is invalid.");
            var caseId = contractCase["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Contract corpus case ID is missing.");
            Assert.True(observedCaseIds.Add(caseId), $"Duplicate corpus case ID '{caseId}'.");
            var instance = basePayload.DeepClone().AsObject();
            foreach (var pair in contractCase["patch"]?.AsObject()
                         ?? throw new InvalidOperationException("Contract corpus patch is missing."))
            {
                instance[pair.Key] = pair.Value?.DeepClone();
            }
            foreach (var field in contractCase["remove"]?.AsArray()
                         ?? throw new InvalidOperationException("Contract corpus remove is missing."))
            {
                instance.Remove(field?.GetValue<string>()
                    ?? throw new InvalidOperationException("Contract corpus remove field is invalid."));
            }
            var payload = Encoding.UTF8.GetBytes(instance.ToJsonString());
            var expectedValid = contractCase["codecValid"]?.GetValue<bool>()
                ?? throw new InvalidOperationException("Contract corpus codecValid is missing.");
            if (expectedValid)
                _ = deserialize(payload);
            else
                Assert.ThrowsAny<Exception>(() => { _ = deserialize(payload); });
        }
        Assert.Equal(
            expectedCaseIds.Order(StringComparer.Ordinal),
            observedCaseIds.Order(StringComparer.Ordinal));
    }

    private static ApprovalSubmissionIntentV1 Intent() => new(
        ApprovalSubmissionIntentV1.CurrentSchemaVersion,
        ApprovalSubmissionIntentV1.CurrentContractId,
        ApprovalSubmissionIntentV1.CurrentProducerModule,
        ApprovalSubmissionIntentV1.CurrentAuthScope,
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new string('a', 64),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        1, Soul, Device, Account, Trace, Idempotency,
        new string('b', 64), new string('c', 64), 1, 1,
        new string('d', 64), new string('e', 64), 1,
        new string('f', 64), "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
        DateTimeOffset.Parse("2026-07-15T00:01:00Z"),
        "internal", Signature);

    private static ApprovalSubmissionAcknowledgementV1 Acknowledgement() => new(
        ApprovalSubmissionAcknowledgementV1.CurrentSchemaVersion,
        ApprovalSubmissionAcknowledgementV1.CurrentContractId,
        ApprovalSubmissionAcknowledgementV1.CurrentProducerModule,
        ApprovalSubmissionAcknowledgementV1.CurrentAuthScope,
        Guid.Parse("10101010-1010-1010-1010-101010101010"),
        Intent().SubmissionAttemptId,
        Intent().ApprovalId, Intent().ProposalId, Intent().CommandId, Intent().LeaseId, 1,
        Soul, Device, Account, Trace, Idempotency,
        Intent().ReleaseBomSha256, 1, Intent().NativeRequestBindingSha256,
        ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(Intent()),
        new string('1', 64), new string('2', 64),
        Guid.Parse("66666666-6666-6666-6666-666666666666"),
        Guid.Parse("77777777-7777-7777-7777-777777777777"),
        new string('3', 64),
        DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
        DateTimeOffset.Parse("2026-07-15T00:01:00Z"),
        "internal", Signature);

    private static ApprovalSubmissionStateV1 State(string state) => new(
        ApprovalSubmissionStateV1.CurrentSchemaVersion,
        ApprovalSubmissionStateV1.CurrentContractId,
        ApprovalSubmissionStateV1.CurrentProducerModule,
        Guid.Parse("88888888-8888-8888-8888-888888888888"),
        Intent().SubmissionAttemptId,
        Intent().ApprovalId, Intent().ProposalId, Intent().CommandId, Intent().LeaseId, 1,
        Soul, Device, Account, Trace, Idempotency,
        Intent().ReleaseBomSha256, 1, Intent().NativeRequestBindingSha256,
        ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(Intent()), state, null,
        new string('4', 64), DateTimeOffset.Parse("2026-07-15T00:00:00Z"), "internal",
        new string('5', 64), Signature);

    private static ApprovalSubmissionRecoveryV1 Recovery() => new(
        ApprovalSubmissionRecoveryV1.CurrentSchemaVersion,
        ApprovalSubmissionRecoveryV1.CurrentContractId,
        ApprovalSubmissionRecoveryV1.CurrentProducerModule,
        ApprovalSubmissionRecoveryV1.CurrentAuthScope,
        ApprovalSubmissionRecoveryV1.CurrentAuthorityRole,
        Guid.Parse("99999999-9999-9999-9999-999999999999"),
        Intent().SubmissionAttemptId,
        Guid.Parse("12121212-1212-1212-1212-121212121212"),
        new string('6', 64), Intent().ApprovalId, Intent().ProposalId, Intent().CommandId,
        Intent().LeaseId, 1,
        Guid.Parse("13131313-1313-1313-1313-131313131313"),
        Guid.Parse("14141414-1414-1414-1414-141414141414"), 2,
        Soul, Device, Account, Trace, Idempotency,
        new string('7', 64), 2, new string('8', 64), new string('9', 64),
        "human_" + new string('a', 64),
        DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
        DateTimeOffset.Parse("2026-07-15T00:04:00Z"),
        "internal", Signature);
}

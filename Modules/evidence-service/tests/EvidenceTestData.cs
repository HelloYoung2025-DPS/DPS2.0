using System.Security.Cryptography;
using System.Text;
using Dps.EvidenceService.Contracts;

namespace Dps.EvidenceService.Tests;

internal static class EvidenceTestData
{
    internal const string BaselineCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string InstructionSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string SoulId = "soul_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    internal static readonly DateTimeOffset FinishedAt = new(2026, 7, 14, 0, 1, 0, TimeSpan.Zero);

    internal const string RunnerKeyId = "runner-key-f2-evidence";
    internal const string EvidenceIssuerIdentity = "agent:evidence-auditor";

    internal static (TestEvidenceV1 Receipt, RawEvidenceArtifact[] Raw) Valid(
        Guid? evidenceId = null,
        string? rawText = null)
    {
        var raw = RawEvidenceArtifact.FromUtf8("raw-test-log", rawText ?? "deterministic raw test proof");
        var receipt = new TestEvidenceV1(
            TestEvidenceV1.CurrentSchemaVersion,
            TestEvidenceV1.CurrentContractId,
            TestEvidenceV1.CurrentProducerModule,
            SoulId,
            "db_" + new string('1', 32),
            "pa_" + new string('2', 32),
            "trace_" + new string('3', 32),
            "idem_" + new string('4', 64),
            FinishedAt,
            "personal",
            evidenceId ?? Guid.Parse("7d478f18-b6b8-4ad4-a58f-b58a4e8a2cb4"),
            "evidence-service.unit",
            "evidence-service",
            "unit",
            "local",
            true,
            "PASS",
            "REPOSITORY_STATIC_VERIFIED",
            BaselineCommit,
            "instruction-receipt:f2-evidence",
            InstructionSha,
            "agent:implementer",
            EvidenceIssuerIdentity,
            null,
            Sha256("dotnet test --filter-trait Category=Unit"),
            FinishedAt.AddMinutes(-1),
            FinishedAt,
            0,
            [new EvidenceArtifactV1("raw-test-log", raw.Sha256, raw.SizeBytes, "text/plain")],
            [],
            null);
        return (receipt, [raw]);
    }

    internal static EvidenceSubmissionCandidate Candidate(
        (TestEvidenceV1 Receipt, RawEvidenceArtifact[] Raw) value)
        => EvidenceSubmissionCandidate.Create(value.Receipt, value.Raw);

    internal static CryptographicRunnerAttestationVerifier Verifier(ECDsa runnerKey)
        => new(
        [
            new TrustedRunnerPolicy(
                RunnerKeyId,
                runnerKey.ExportSubjectPublicKeyInfoPem(),
                EvidenceIssuerIdentity,
                "INTEGRATION_VERIFIED")
        ]);

    internal static SignedRunnerAttestationV1 Sign(
        EvidenceSubmissionCandidate candidate,
        ECDsa runnerKey,
        RunnerAttestationFactsV1? facts = null)
    {
        facts ??= RunnerAttestationCanonicalizer.CreateFacts(
            candidate,
            RunnerKeyId,
            candidate.Receipt.FinishedAt.AddSeconds(1),
            baselineObjectVerified: true,
            instructionReceiptVerified: true,
            rawArtifactsObserved: true,
            roleSeparationVerified: true);
        var signature = runnerKey.SignData(
            RunnerAttestationCanonicalizer.GetSigningPayload(facts),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedRunnerAttestationV1(facts, Convert.ToBase64String(signature));
    }

    internal static string Sha256(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

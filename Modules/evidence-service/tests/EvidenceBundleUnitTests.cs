using System.Security.Cryptography;
using Dps.EvidenceService.Contracts;

namespace Dps.EvidenceService.Tests;

public sealed class EvidenceBundleUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Candidate_is_deterministic_and_does_not_retain_mutable_raw_input()
    {
        var original = "immutable proof"u8.ToArray();
        var raw = RawEvidenceArtifact.FromBytes("raw-test-log", original);
        var valid = EvidenceTestData.Valid();
        var receipt = valid.Receipt with
        {
            Artifacts = [new EvidenceArtifactV1("raw-test-log", raw.Sha256, raw.SizeBytes, "text/plain")]
        };

        var first = EvidenceSubmissionCandidate.Create(receipt, [raw]);
        original[0] = (byte)'X';
        var second = EvidenceSubmissionCandidate.Create(receipt, [raw]);

        Assert.Equal(first.ReceiptSha256, second.ReceiptSha256);
        Assert.Equal(first.ArtifactSetSha256, second.ArtifactSetSha256);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("FAIL")]
    [InlineData("SKIP")]
    [InlineData("PARTIAL")]
    [InlineData("NOT_RUN")]
    [InlineData("INFRA_ERROR")]
    [InlineData("NOT_APPLICABLE")]
    public void Required_non_pass_outcome_is_recordable_but_never_releases(string status)
    {
        var valid = EvidenceTestData.Valid();
        var receipt = valid.Receipt with { Status = status, ExitCode = 1, ReasonCode = "required_check_failed" };

        var candidate = EvidenceSubmissionCandidate.Create(receipt, valid.Raw);

        Assert.Equal(status, candidate.Receipt.Status);
        Assert.False(TestEvidenceReleaseEvaluator.SatisfiesRequiredGate(candidate.Receipt));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Signed_required_failure_is_persisted_as_failure_evidence()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = EvidenceTestData.Valid();
        var candidate = EvidenceSubmissionCandidate.Create(
            valid.Receipt with { Status = "NOT_RUN", ExitCode = null, ReasonCode = "test_command_not_run" },
            valid.Raw);
        var store = new InMemoryEvidenceStore();
        var service = new EvidenceSubmissionService(store, EvidenceTestData.Verifier(runnerKey));

        var result = await service.SubmitAsync(
            candidate,
            EvidenceTestData.Sign(candidate, runnerKey),
            TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStoreDisposition.Stored, result.Disposition);
        Assert.Equal("NOT_RUN", store.Read(
            candidate.Receipt.EvidenceId,
            candidate.Receipt.SoulId,
            candidate.Receipt.DeviceBindingId,
            candidate.Receipt.PlatformAccountId)!.Status);
        Assert.False(TestEvidenceReleaseEvaluator.SatisfiesRequiredGate(candidate.Receipt));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Required_pass_with_nonzero_exit_fails_closed()
    {
        var valid = EvidenceTestData.Valid();
        var receipt = valid.Receipt with { ExitCode = 7 };

        Assert.Throws<InvalidOperationException>(() => EvidenceSubmissionCandidate.Create(receipt, valid.Raw));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Artifact_hash_mismatch_is_rejected()
    {
        var valid = EvidenceTestData.Valid();
        var tampered = RawEvidenceArtifact.FromUtf8("raw-test-log", "different bytes");

        Assert.Throws<InvalidOperationException>(() => EvidenceSubmissionCandidate.Create(valid.Receipt, [tampered]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Self_issued_evidence_is_rejected_before_attestation()
    {
        var valid = EvidenceTestData.Valid();
        var receipt = valid.Receipt with { EvidenceIssuerIdentity = valid.Receipt.ImplementerIdentity };

        Assert.Throws<InvalidOperationException>(() => EvidenceSubmissionCandidate.Create(receipt, valid.Raw));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Attestation_cannot_be_reused_after_instruction_receipt_changes()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = EvidenceTestData.Candidate(EvidenceTestData.Valid());
        var attestation = EvidenceTestData.Sign(original, runnerKey);
        var changedData = EvidenceTestData.Valid();
        var changed = EvidenceSubmissionCandidate.Create(
            changedData.Receipt with { InstructionReceiptSha256 = new string('c', 64) },
            changedData.Raw);
        var service = new EvidenceSubmissionService(new InMemoryEvidenceStore(), EvidenceTestData.Verifier(runnerKey));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(changed, attestation, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Unknown_contract_major_is_rejected()
    {
        var valid = EvidenceTestData.Valid();
        var receipt = valid.Receipt with { SchemaVersion = "2.0.0" };

        Assert.Throws<NotSupportedException>(() => EvidenceSubmissionCandidate.Create(receipt, valid.Raw));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Mock_or_unit_evidence_cannot_escalate_to_integration()
    {
        var valid = EvidenceTestData.Valid();
        var receipt = valid.Receipt with
        {
            ExecutionEnvironment = "mock",
            VerificationLevel = "INTEGRATION_VERIFIED"
        };

        Assert.Throws<InvalidOperationException>(() => EvidenceSubmissionCandidate.Create(receipt, valid.Raw));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Forged_runner_signature_is_rejected()
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var candidate = EvidenceTestData.Candidate(EvidenceTestData.Valid());
        var forged = EvidenceTestData.Sign(candidate, attackerKey);
        var service = new EvidenceSubmissionService(new InMemoryEvidenceStore(), EvidenceTestData.Verifier(trustedKey));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(candidate, forged, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Missing_runner_verification_fact_is_rejected_even_with_valid_signature()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var candidate = EvidenceTestData.Candidate(EvidenceTestData.Valid());
        var facts = RunnerAttestationCanonicalizer.CreateFacts(
            candidate,
            EvidenceTestData.RunnerKeyId,
            candidate.Receipt.FinishedAt.AddSeconds(1),
            baselineObjectVerified: false,
            instructionReceiptVerified: true,
            rawArtifactsObserved: true,
            roleSeparationVerified: true);
        var signed = EvidenceTestData.Sign(candidate, runnerKey, facts);
        var service = new EvidenceSubmissionService(new InMemoryEvidenceStore(), EvidenceTestData.Verifier(runnerKey));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(candidate, signed, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Duplicate_is_noop_and_same_id_different_checksum_is_quarantined()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new InMemoryEvidenceStore();
        var service = new EvidenceSubmissionService(store, EvidenceTestData.Verifier(runnerKey));
        var original = EvidenceTestData.Candidate(EvidenceTestData.Valid());
        var originalAttestation = EvidenceTestData.Sign(original, runnerKey);

        var inserted = await service.SubmitAsync(original, originalAttestation, TestContext.Current.CancellationToken);
        var duplicate = await service.SubmitAsync(original, originalAttestation, TestContext.Current.CancellationToken);

        var changed = EvidenceTestData.Candidate(EvidenceTestData.Valid(original.Receipt.EvidenceId, "other valid raw proof"));
        var conflict = await service.SubmitAsync(changed, EvidenceTestData.Sign(changed, runnerKey), TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStoreDisposition.Stored, inserted.Disposition);
        Assert.Equal(EvidenceStoreDisposition.DuplicateNoOp, duplicate.Disposition);
        Assert.Equal(EvidenceStoreDisposition.Quarantined, conflict.Disposition);
        Assert.Equal(1, store.CountForSoul(EvidenceTestData.SoulId));
        Assert.Equal(1, store.QuarantineCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Soul_scoped_read_model_does_not_cross_souls()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new InMemoryEvidenceStore();
        var service = new EvidenceSubmissionService(store, EvidenceTestData.Verifier(runnerKey));
        var first = EvidenceTestData.Candidate(EvidenceTestData.Valid());
        var secondData = EvidenceTestData.Valid(Guid.Parse("f620ec58-477d-4a0e-90aa-44c2a8aba862"));
        var secondSoul = "soul_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var second = EvidenceSubmissionCandidate.Create(
            secondData.Receipt with
            {
                SoulId = secondSoul,
                DeviceBindingId = "db_" + new string('5', 32),
                PlatformAccountId = "pa_" + new string('6', 32),
                TraceId = "trace_" + new string('7', 32),
                IdempotencyKey = "idem_" + new string('8', 64)
            },
            secondData.Raw);

        await service.SubmitAsync(first, EvidenceTestData.Sign(first, runnerKey), TestContext.Current.CancellationToken);
        await service.SubmitAsync(second, EvidenceTestData.Sign(second, runnerKey), TestContext.Current.CancellationToken);

        Assert.Equal(1, store.CountForSoul(EvidenceTestData.SoulId));
        Assert.Equal(1, store.CountForSoul(secondSoul));
        Assert.Equal(
            EvidenceTestData.SoulId,
            store.Read(
                first.Receipt.EvidenceId,
                first.Receipt.SoulId,
                first.Receipt.DeviceBindingId,
                first.Receipt.PlatformAccountId)!.SoulId);
        Assert.Null(store.Read(
            first.Receipt.EvidenceId,
            second.Receipt.SoulId,
            second.Receipt.DeviceBindingId,
            second.Receipt.PlatformAccountId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Database_options_and_attestation_proofs_do_not_leak_secrets_in_to_string()
    {
        var options = new EvidenceStoreOptions("Host=db;Username=user;Password=secret-value", "evidence_test");
        var invalidSchema = new EvidenceStoreOptions("Password=other-secret", "schema Password=schema-secret");
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = new TrustedRunnerPolicy(
            EvidenceTestData.RunnerKeyId,
            runnerKey.ExportSubjectPublicKeyInfoPem(),
            EvidenceTestData.EvidenceIssuerIdentity,
            "INTEGRATION_VERIFIED");
        var signed = EvidenceTestData.Sign(EvidenceTestData.Candidate(EvidenceTestData.Valid()), runnerKey);

        Assert.DoesNotContain("secret-value", options.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret", invalidSchema.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("schema-secret", invalidSchema.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PUBLIC KEY", policy.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(signed.SignatureBase64, signed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Database_schema_identifier_rejects_trailing_newline()
    {
        Assert.Throws<ArgumentException>(() =>
            new EvidenceStoreOptions("Host=unused", "evidence_test\n").Validate());
    }
}

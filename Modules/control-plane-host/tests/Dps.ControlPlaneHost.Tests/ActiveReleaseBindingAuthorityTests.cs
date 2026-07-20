using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Xunit;

namespace Dps.ControlPlaneHost.Tests;

public sealed class ActiveReleaseBindingAuthorityTests
{
    private const string Device = "db_11111111111111111111111111111111";
    private const string OtherDevice = "db_22222222222222222222222222222222";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    private static string Token(string seed)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("token:" + seed)));

    private static string Sha256Hex(byte[] value)
        => Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed class TestSigner : IDisposable
    {
        public RSA Rsa { get; } = RSA.Create(2048);
        public string KeyId { get; }
        public string Identity { get; }

        public TestSigner(string keyId = "test-bom-key-v1", string identity = "test-release-controller")
        {
            KeyId = keyId;
            Identity = identity;
        }

        public ReleaseBomTrustKey TrustKey
        {
            get
            {
                var parameters = Rsa.ExportParameters(false);
                return new ReleaseBomTrustKey(
                    KeyId,
                    Identity,
                    Convert.ToHexStringLower(parameters.Modulus!),
                    65537);
            }
        }

        /// <summary>
        /// Builds a minimal legal signed Release BOM carrying the exact
        /// candidate_bom_validator._BOM_FIELDS top-level set. Deep subtrees
        /// (modules, evidence, ...) are placeholders because the activation
        /// authority only enforces the activation-safety subset.
        /// </summary>
        public byte[] SignBom(
            string bomId,
            long signerGeneration,
            string executionTokenBase64,
            string? previousStableBomSha256,
            Action<JsonObject>? mutateBeforeSign = null,
            string? algorithm = null,
            string? keyIdOverride = null)
        {
            var tokenBytes = Convert.FromBase64String(executionTokenBase64);
            var payload = new JsonObject
            {
                ["schema_version"] = "dps.release-bom/v1",
                ["bom_id"] = bomId,
                ["status"] = "SIGNED",
                ["integration_commit"] = new string('a', 40),
                ["created_at"] = "2026-07-14T00:00:00.0000001Z",
                ["release_bom_generation"] = signerGeneration,
                ["activation_token_sha256"] = Convert.ToHexStringLower(SHA256.HashData(tokenBytes)),
                ["modules"] = new JsonArray(),
                ["instruction_hashes"] = new JsonObject(),
                ["contracts"] = new JsonArray(),
                ["database_versions"] = new JsonObject(),
                ["dependency_dag_sha256"] = new string('b', 64),
                ["compatibility_matrix_sha256"] = new string('c', 64),
                ["feature_flags"] = new JsonObject(),
                ["kill_switches"] = new JsonArray(),
                ["ai_toolchain"] = new JsonObject(),
                ["evidence"] = new JsonArray(),
                ["risk"] = new JsonObject(),
                ["release_approval"] = new JsonObject(),
                ["rollout"] = new JsonObject(),
                ["rollback"] = new JsonObject(),
                ["previous_stable_bom"] = previousStableBomSha256 is null
                    ? null
                    : (JsonNode)("bom-previous-" + bomId),
                ["previous_stable_bom_sha256"] = previousStableBomSha256,
                ["native_stop_authorities"] = new JsonArray(),
                ["device_route_assignment_authorities"] = new JsonArray(),
                ["native_stop_challenge_authorities"] = new JsonArray()
            };
            mutateBeforeSign?.Invoke(payload);
            using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
            var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
            var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n").Concat(canonical).ToArray();
            var signature = Rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            payload["signature"] = new JsonObject
            {
                ["algorithm"] = algorithm ?? "rsa-pss-sha256",
                ["key_id"] = keyIdOverride ?? KeyId,
                ["value"] = Convert.ToBase64String(signature)
            };
            // The authority only accepts the canonical sorted compact wire,
            // so the fixture emits exactly that encoding.
            using var fullDocument = JsonDocument.Parse(payload.ToJsonString());
            return ReleaseBomCanonicalJson.Serialize(fullDocument.RootElement);
        }

        public void Dispose() => Rsa.Dispose();
    }

    private sealed class FrozenTruthStore(IReadOnlyList<ReleaseBindingTruthRecord> records)
        : IReleaseBindingTruthStore
    {
        public void Append(ReleaseBindingTruthRecord record) { }
        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll() => records;
    }

    private static ActiveReleaseBindingAuthority Authority(
        TestSigner signer,
        IReleaseBindingTruthStore? store = null)
        => new([signer.TrustKey], store ?? new InMemoryReleaseBindingTruthStore(), () => Now);

    private static (byte[] Bom, string Token) MakeBom(
        TestSigner signer,
        string bomId,
        long signerGeneration,
        byte[]? previousBom)
        => (signer.SignBom(
                bomId,
                signerGeneration,
                Token(bomId),
                previousBom is null ? null : Sha256Hex(previousBom)),
            Token(bomId));

    // ----- lifecycle -----

    [Fact, Trait("Category", "Unit")]
    public void ActivateExposesVerifiedActiveBinding()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        var receipt = authority.Activate(Device, bom, token);

        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.NotNull(binding);
        Assert.Equal(Sha256Hex(bom), binding!.ReleaseBomSha256);
        Assert.Equal(1, binding.Generation);
        Assert.Equal(1, binding.ReleaseBomGeneration);
        Assert.Equal(token, binding.ExecutionTokenBase64);
        Assert.Equal("active", binding.Status);
        Assert.Equal(signer.Identity, binding.SignerIdentity);
        Assert.Equal(signer.KeyId, binding.SignerKeyId);
        Assert.Equal("activation", receipt.ReceiptKind);
        Assert.Null(receipt.From);
        Assert.Equal(binding.ReleaseBomSha256, receipt.To.ReleaseBomSha256);
        Assert.Equal(1, receipt.Sequence);
        Assert.Equal(binding.ReceiptId, receipt.ReceiptId);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void SecondActivationIsMonotonicAndHidesPreviousToken()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);

        var receipt = authority.Activate(Device, second, secondToken);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(2, binding!.Generation);
        Assert.Equal(2, binding.ReleaseBomGeneration);
        Assert.Equal(secondToken, binding.ExecutionTokenBase64);
        Assert.NotEqual(firstToken, binding.ExecutionTokenBase64);
        Assert.NotNull(receipt.From);
        Assert.Equal("previous", receipt.From!.Status);
        Assert.Equal(Sha256Hex(first), receipt.From.ReleaseBomSha256);
        Assert.Equal("active", receipt.To.Status);
        Assert.Equal(2, receipt.Sequence);
    }

    [Fact, Trait("Category", "Unit")]
    public void RevokeFailsReaderClosedAndWritesVersionedReceipt()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out var active));

        var receipt = authority.Revoke(Device, active!.Generation);
        Assert.False(authority.TryReadActive(Device, out var afterRevoke));
        Assert.Null(afterRevoke);
        Assert.Equal("revocation", receipt.ReceiptKind);
        Assert.Equal("active", receipt.From!.Status);
        Assert.Equal("revoked", receipt.To.Status);
        Assert.Equal(active.ReleaseBomSha256, receipt.From.ReleaseBomSha256);
        Assert.Equal(active.Generation, receipt.To.Generation);
        Assert.Equal(2, receipt.Sequence);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackRestoresPreviousDigestWithNewGenerationAndSignerToken()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        authority.Activate(Device, second, secondToken);
        Assert.True(authority.TryReadActive(Device, out var abandoned));

        var receipt = authority.Rollback(Device, firstToken);
        Assert.True(authority.TryReadActive(Device, out var restored));
        Assert.Equal(Sha256Hex(first), restored!.ReleaseBomSha256);
        Assert.Equal(3, restored.Generation);
        // Signer ordinal legitimately reverts; the runtime ordinal advances.
        Assert.Equal(1, restored.ReleaseBomGeneration);
        Assert.Equal(firstToken, restored.ExecutionTokenBase64);
        Assert.NotEqual(abandoned!.ExecutionTokenBase64, restored.ExecutionTokenBase64);
        Assert.Equal("rollback", receipt.ReceiptKind);
        Assert.Equal("revoked", receipt.From!.Status);
        Assert.Equal(abandoned.ReleaseBomSha256, receipt.From.ReleaseBomSha256);
        Assert.Equal("active", receipt.To.Status);
        Assert.Equal(3, receipt.To.Generation);
        Assert.Equal(3, receipt.Sequence);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void ActivationOverRevokedRecordsRevokedFromAndNeverLaundersItToPrevious()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        var activated = authority.Activate(Device, second, secondToken);
        authority.Revoke(Device, activated.To.Generation);

        var (third, thirdToken) = MakeBom(signer, "bom-3", 3, second);
        var receipt = authority.Activate(Device, third, thirdToken);

        // The receipt tells the truth: the prior binding stays revoked, it is
        // not demoted to "previous".
        Assert.NotNull(receipt.From);
        Assert.Equal("revoked", receipt.From!.Status);
        Assert.Equal(2, receipt.From.Generation);
        // No rollback path survives across a revocation: neither the revoked
        // bom-2 nor the older bom-1 is reachable.
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device, secondToken));
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device, firstToken));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(3, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackAwayFromRevokedActiveRestoresTheTruePrevious()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        var activated = authority.Activate(Device, second, secondToken);
        authority.Revoke(Device, activated.To.Generation);

        var receipt = authority.Rollback(Device, firstToken);

        Assert.Equal("revoked", receipt.From!.Status);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(first), binding!.ReleaseBomSha256);
        Assert.Equal(3, binding.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void GenerationIsStrictlyMonotonicAcrossManyActivations()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        byte[]? previous = null;
        for (var round = 1; round <= 5; round++)
        {
            var (bom, token) = MakeBom(signer, "bom-" + round, round, previous);
            authority.Activate(Device, bom, token);
            previous = bom;
            Assert.True(authority.TryReadActive(Device, out var binding));
            Assert.Equal(round, binding!.Generation);
            Assert.Equal(round, binding.ReleaseBomGeneration);
        }
        var receipts = authority.ReadReceipts(Device);
        Assert.Equal(5, receipts.Count);
        Assert.Equal(
            Enumerable.Range(1, 5).Select(static value => (long)value),
            receipts.Select(static receipt => receipt.Sequence));
    }

    [Fact, Trait("Category", "Unit")]
    public void ReceiptSequenceIsSharedAcrossAllKinds()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        authority.Activate(Device, second, secondToken);
        authority.Rollback(Device, firstToken);
        var (third, thirdToken) = MakeBom(signer, "bom-3", 3, first);
        authority.Activate(Device, third, thirdToken);
        Assert.True(authority.TryReadActive(Device, out var latest));
        authority.Revoke(Device, latest!.Generation);

        var receipts = authority.ReadReceipts(Device);
        Assert.Equal(
            new[] { "activation", "activation", "rollback", "activation", "revocation" },
            receipts.Select(static receipt => receipt.ReceiptKind));
        Assert.Equal(
            new long[] { 1, 2, 3, 4, 5 },
            receipts.Select(static receipt => receipt.Sequence));
        Assert.All(receipts, static receipt =>
            Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownDeviceReadsFailClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);

        Assert.False(authority.TryReadActive(Device, out var binding));
        Assert.Null(binding);
        Assert.Empty(authority.ReadReceipts(Device));
        Assert.Throws<ArgumentException>(() => authority.Revoke("not-a-device", 1));
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Revoke(OtherDevice, 1));
    }

    // ----- signature and trust -----

    [Fact, Trait("Category", "Unit")]
    public void BadSignatureFailsClosedWithZeroStateResidue()
    {
        using var signer = new TestSigner();
        using var stranger = new TestSigner("stranger-key-v1", "stranger-controller");
        var authority = Authority(signer);
        // Signed by an untrusted RSA key but claiming the trusted key id.
        var forged = stranger.SignBom("bom-1", 1, Token("bom-1"), null, keyIdOverride: signer.KeyId);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, forged, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownKeyIdFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1", 1, Token("bom-1"), null, keyIdOverride: "unknown-key-v1");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownAlgorithmFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1", 1, Token("bom-1"), null, algorithm: "rsa-sha256");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void WrongPurposeKeyIsNeverTrusted()
    {
        using var signer = new TestSigner();
        var parameters = signer.Rsa.ExportParameters(false);
        var policy = new JsonObject
        {
            ["keys"] = new JsonArray(
                new JsonObject
                {
                    ["key_id"] = signer.KeyId,
                    ["identity"] = signer.Identity,
                    ["algorithm"] = "rsa-pss-sha256",
                    ["modulus_hex"] = Convert.ToHexStringLower(parameters.Modulus!),
                    ["exponent"] = 65537,
                    ["purposes"] = new JsonArray("artifact")
                })
        };
        using var document = JsonDocument.Parse(policy.ToJsonString());

        // The parser refuses to yield any bom key from a wrong-purpose policy.
        Assert.Throws<ActiveReleaseBindingException>(
            () => ReleaseBomTrustKey.FromTrustPolicy(document.RootElement));
    }

    [Fact, Trait("Category", "Unit")]
    public void TrustPolicyParserAcceptsBomPurposeKeys()
    {
        using var signer = new TestSigner();
        var parameters = signer.Rsa.ExportParameters(false);
        var policy = new JsonObject
        {
            ["keys"] = new JsonArray(
                new JsonObject
                {
                    ["key_id"] = signer.KeyId,
                    ["identity"] = signer.Identity,
                    ["algorithm"] = "rsa-pss-sha256",
                    ["modulus_hex"] = Convert.ToHexStringLower(parameters.Modulus!),
                    ["exponent"] = 65537,
                    ["purposes"] = new JsonArray("bom")
                })
        };
        using var document = JsonDocument.Parse(policy.ToJsonString());
        var keys = ReleaseBomTrustKey.FromTrustPolicy(document.RootElement);
        var authority = new ActiveReleaseBindingAuthority(
            keys, new InMemoryReleaseBindingTruthStore(), () => Now);

        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(signer.Identity, binding!.SignerIdentity);
    }

    [Fact, Trait("Category", "Unit")]
    public void TamperedPayloadReplayFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        var text = Encoding.UTF8.GetString(bom).Replace("bom-1", "bom-9");
        var tampered = Encoding.UTF8.GetBytes(text);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, tampered, token));
        Assert.False(authority.TryReadActive(Device, out _));
        // The untampered original still verifies afterwards.
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out _));
    }

    // ----- F1: strict BOM shape and signer-committed token binding -----

    [Fact, Trait("Category", "Unit")]
    public void MissingTopLevelFieldFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload.Remove("risk"));

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void ExtraTopLevelFieldFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["surprise"] = true);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void WrongSchemaVersionFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["schema_version"] = "dps.release-bom/v2");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void NonSignedStatusFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["status"] = "STABLE");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void NonPositiveSignerGenerationFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["release_bom_generation"] = 0);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void MalformedActivationTokenDigestFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload =>
                payload["activation_token_sha256"] = new string('C', 64));

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void TokenNotMatchingSignerCommitmentFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, _) = MakeBom(signer, "bom-1", 1, null);

        // A perfectly canonical 32-byte token that is not the committed one.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("some-other-token")));
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void NonCanonicalExecutionTokenFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);

        // Wrong size (33 bytes).
        var oversized = Convert.ToBase64String(new byte[33]);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, oversized));
        // Non-canonical re-encoding of the right token (padding bits abuse).
        var nonCanonical = token[..^2] + "//";
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, nonCanonical));
        // Not base64 at all.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, "!not-base64!"));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void SignerOrdinalAntiRollbackIsEnforced()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-5", 5, null);
        authority.Activate(Device, first, firstToken);

        // Same signer ordinal, different bytes: equivocation, fail-closed.
        var (conflict, conflictToken) = MakeBom(signer, "bom-5b", 5, first);
        var conflictError = Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, conflict, conflictToken));
        Assert.Contains("conflicting re-submission", conflictError.Message, StringComparison.Ordinal);
        // Lower signer ordinal: anti-rollback, fail-closed.
        var (older, olderToken) = MakeBom(signer, "bom-4", 4, first);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, older, olderToken));
        // Strictly higher ordinal proceeds.
        var (next, nextToken) = MakeBom(signer, "bom-6", 6, first);
        authority.Activate(Device, next, nextToken);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(6, binding!.ReleaseBomGeneration);
    }

    [Fact, Trait("Category", "Unit")]
    public void PreviousStableBomChainIsEnforced()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        // First activation must carry a null previous chain.
        var withBogusPrevious = signer.SignBom(
            "bom-1", 1, Token("bom-1"), new string('e', 64));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, withBogusPrevious, Token("bom-1")));

        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);

        // Second activation must chain to the current binding digest.
        var wrongChain = signer.SignBom("bom-2", 2, Token("bom-2"), new string('e', 64));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, wrongChain, Token("bom-2")));
        var nullChain = signer.SignBom("bom-2", 2, Token("bom-2"), null);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, nullChain, Token("bom-2")));
        // The correct chain proceeds.
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        authority.Activate(Device, second, secondToken);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(second), binding!.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void RevokeWrongGenerationFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Revoke(Device, 2));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal("active", binding!.Status);
        Assert.Single(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackWithoutPreviousFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device, token));
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(OtherDevice, token));
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackWithWrongTokenFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        authority.Activate(Device, second, secondToken);

        // The active BOM's token is not the previous binding's commitment.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Rollback(Device, secondToken));
        // An arbitrary canonical token is not either.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Rollback(Device, Token("unrelated")));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(second), binding!.ReleaseBomSha256);
    }

    // ----- F2: recoverable state -----

    [Fact, Trait("Category", "Unit")]
    public void RecoveryFromStoreRestoresBindingsTokensAndCounters()
    {
        using var signer = new TestSigner();
        var store = new InMemoryReleaseBindingTruthStore();
        var first = Authority(signer, store);
        var (bom1, token1) = MakeBom(signer, "bom-1", 1, null);
        first.Activate(Device, bom1, token1);
        var (bom2, token2) = MakeBom(signer, "bom-2", 2, bom1);
        first.Activate(Device, bom2, token2);

        var recovered = Authority(signer, store);
        Assert.True(recovered.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(bom2), binding!.ReleaseBomSha256);
        Assert.Equal(2, binding.Generation);
        Assert.Equal(token2, binding.ExecutionTokenBase64);
        Assert.Equal(2, recovered.ReadReceipts(Device).Count);

        // Counters continue: rollback still reaches bom-1, sequence goes 3,
        // and no receipt id repeats.
        var receipt = recovered.Rollback(Device, token1);
        Assert.Equal(3, receipt.Sequence);
        Assert.Equal(3, receipt.To.Generation);
        var ids = recovered.ReadReceipts(Device).Select(static value => value.ReceiptId).ToArray();
        Assert.Equal(3, ids.Length);
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        // Signer anti-rollback survives recovery: ordinal 2 is spent.
        var (stale, staleToken) = MakeBom(signer, "bom-2x", 2, bom1);
        Assert.Throws<ActiveReleaseBindingException>(
            () => recovered.Activate(Device, stale, staleToken));
    }

    [Fact, Trait("Category", "Unit")]
    public void RecoveryAfterRevokeKeepsReaderClosed()
    {
        using var signer = new TestSigner();
        var store = new InMemoryReleaseBindingTruthStore();
        var first = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        first.Activate(Device, bom, token);
        first.Revoke(Device, 1);

        var recovered = Authority(signer, store);
        Assert.False(recovered.TryReadActive(Device, out _));
        Assert.Equal(2, recovered.ReadReceipts(Device).Count);
        Assert.Throws<ActiveReleaseBindingException>(() => recovered.Rollback(Device, token));
    }

    [Fact, Trait("Category", "Unit")]
    public void ForkedOrRegressedJournalRefusesService()
    {
        using var signer = new TestSigner();
        var store = new InMemoryReleaseBindingTruthStore();
        var authority = Authority(signer, store);
        var (bom1, token1) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var (bom2, token2) = MakeBom(signer, "bom-2", 2, bom1);
        authority.Activate(Device, bom2, token2);
        var records = store.LoadAll();

        // Sequence gap (dropped first record).
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([records[1]])));
        // Duplicated record (replayed receipt identity).
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([records[0], records[0]])));
        // Tampered receipt payload digest.
        var tampered = records[1] with
        {
            Receipt = records[1].Receipt with { PayloadSha256 = new string('0', 64) }
        };
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([records[0], tampered])));
    }

    // ----- F3: idempotency and concurrency -----

    [Fact, Trait("Category", "Unit")]
    public void IdenticalActivateResubmissionReturnsOriginalReceiptWithoutStateChange()
    {
        using var signer = new TestSigner();
        var store = new InMemoryReleaseBindingTruthStore();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        var original = authority.Activate(Device, bom, token);

        var replay = authority.Activate(Device, bom, token);
        Assert.Equal(original, replay);
        Assert.Single(authority.ReadReceipts(Device));
        Assert.Single(store.LoadAll());
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(1, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void IdenticalRevokeAndRollbackResubmissionsAreIdempotent()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom1, token1) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var (bom2, token2) = MakeBom(signer, "bom-2", 2, bom1);
        authority.Activate(Device, bom2, token2);

        var rollback = authority.Rollback(Device, token1);
        var rollbackReplay = authority.Rollback(Device, token1);
        Assert.Equal(rollback, rollbackReplay);

        Assert.True(authority.TryReadActive(Device, out var active));
        var revoke = authority.Revoke(Device, active!.Generation);
        var revokeReplay = authority.Revoke(Device, active.Generation);
        Assert.Equal(revoke, revokeReplay);
        Assert.Equal(4, authority.ReadReceipts(Device).Count);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ConcurrentFirstActivationNeverForksGenerationOne()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bomA, tokenA) = MakeBom(signer, "bom-a", 1, null);
        var (bomB, tokenB) = MakeBom(signer, "bom-b", 1, null);

        using var barrier = new Barrier(2);
        var outcomes = new object?[2];
        void Run(int slot, byte[] bom, string token)
        {
            barrier.SignalAndWait();
            try
            {
                outcomes[slot] = authority.Activate(Device, bom, token);
            }
            catch (ActiveReleaseBindingException exception)
            {
                outcomes[slot] = exception;
            }
        }
        await Task.WhenAll(
            Task.Run(() => Run(0, bomA, tokenA), TestContext.Current.CancellationToken),
            Task.Run(() => Run(1, bomB, tokenB), TestContext.Current.CancellationToken));

        // Exactly one contender wins generation 1; the other fails closed on
        // the spent signer ordinal. Never two forked generation-1 bindings.
        var receipts = outcomes.OfType<ReleaseBindingReceiptV1>().ToArray();
        var failures = outcomes.OfType<ActiveReleaseBindingException>().ToArray();
        Assert.Single(receipts);
        Assert.Single(failures);
        Assert.Equal(1, receipts[0].To.Generation);
        Assert.Single(authority.ReadReceipts(Device));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(1, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ConcurrentIdenticalActivationsConvergeOnOneReceipt()
    {
        using var signer = new TestSigner();
        var store = new InMemoryReleaseBindingTruthStore();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);

        using var barrier = new Barrier(2);
        var receipts = new ReleaseBindingReceiptV1[2];
        void Run(int slot)
        {
            barrier.SignalAndWait();
            receipts[slot] = authority.Activate(Device, bom, token);
        }
        await Task.WhenAll(
            Task.Run(() => Run(0), TestContext.Current.CancellationToken),
            Task.Run(() => Run(1), TestContext.Current.CancellationToken));

        Assert.Equal(receipts[0], receipts[1]);
        Assert.Single(authority.ReadReceipts(Device));
        Assert.Single(store.LoadAll());
    }

    // ----- second adversarial review: F1-F3 regressions -----

    [Fact, Trait("Category", "Unit")]
    public void StaleRollbackReplayAfterLaterActivationNeverReportsStaleSuccess()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-a", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-b", 2, first);
        authority.Activate(Device, second, secondToken);
        var staleRollback = authority.Rollback(Device, firstToken);
        // A newer BOM supersedes the rolled-back binding (and legitimately
        // demotes it to previous again).
        var (third, thirdToken) = MakeBom(signer, "bom-c", 3, first);
        authority.Activate(Device, third, thirdToken);

        // The identical rollback request digest exists, but its recorded
        // postcondition is no longer the current truth: it is NOT treated
        // as a replay. Here a fresh, truthful rollback is still possible
        // (bom-a was re-demoted to previous), so the authority performs a
        // NEW transition instead of echoing the stale generation-3 receipt.
        var freshRollback = authority.Rollback(Device, firstToken);
        Assert.NotEqual(staleRollback, freshRollback);
        Assert.Equal(5, freshRollback.To.Generation);
        Assert.Equal(Sha256Hex(third), freshRollback.From!.ReleaseBomSha256);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(first), binding!.ReleaseBomSha256);
        Assert.Equal(5, binding.Generation);

        // Now exhaust every rollback path: revoke the active binding and
        // activate over the revocation (which drops the previous slot).
        authority.Revoke(Device, 5);
        var (fourth, fourthToken) = MakeBom(signer, "bom-d", 4, first);
        authority.Activate(Device, fourth, fourthToken);

        // The old rollback digest still hits the idempotency map, but its
        // postcondition is stale and no previous slot survives: fail-closed,
        // never a fake success while bom-d stays active.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Rollback(Device, firstToken));
        Assert.True(authority.TryReadActive(Device, out var final));
        Assert.Equal(Sha256Hex(fourth), final!.ReleaseBomSha256);
        Assert.Equal(6, final.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void ReencodedSignedBomVariantsAreRejected()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);

        // Same signed payload, fields re-ordered (compact but unsorted).
        var reordered = new JsonObject();
        foreach (var pair in JsonNode.Parse(bom)!.AsObject().ToArray().Reverse())
        {
            reordered[pair.Key] = pair.Value?.DeepClone();
        }
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(
            Device, Encoding.UTF8.GetBytes(reordered.ToJsonString()), token));

        // Same signed payload with whitespace (indented re-encoding).
        var indented = JsonNode.Parse(bom)!.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true });
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(
            Device, Encoding.UTF8.GetBytes(indented), token));

        // Signature value with an embedded newline: base64 decoding ignores
        // whitespace so the signature still verifies; only the canonical
        // base64 re-encoding check rejects it.
        var mutated = JsonNode.Parse(bom)!.AsObject();
        var signatureObject = mutated["signature"]!.AsObject();
        signatureObject["value"] =
            signatureObject["value"]!.GetValue<string>().Insert(10, "\n");
        using var mutatedDocument = JsonDocument.Parse(mutated.ToJsonString());
        var newlineVariant = ReleaseBomCanonicalJson.Serialize(mutatedDocument.RootElement);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, newlineVariant, token));

        // Zero state residue, and the true canonical wire still activates.
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void TamperedRecoveryBindingsRefuseService()
    {
        using var signer = new TestSigner();
        var store = new InMemoryReleaseBindingTruthStore();
        var authority = Authority(signer, store);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        authority.Activate(Device, second, secondToken);
        var records = store.LoadAll();
        var head = records[0];

        // 1. Current binding digest no longer matches the recorded BOM.
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with
            {
                CurrentBinding = head.CurrentBinding with { ReleaseBomSha256 = new string('f', 64) }
            }])));
        // 2. Signer identity swapped under the same signed BOM.
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with
            {
                CurrentBinding = head.CurrentBinding with { SignerIdentity = "stranger-controller" }
            }])));
        // 3. Execution token swapped for a self-consistent but uncommitted one.
        var evilToken = Token("evil");
        var evilTokenSha = Convert.ToHexStringLower(
            SHA256.HashData(Convert.FromBase64String(evilToken)));
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with
            {
                CurrentBinding = head.CurrentBinding with
                {
                    ExecutionTokenBase64 = evilToken,
                    ActivationTokenSha256 = evilTokenSha
                }
            }])));
        // 4. Previous binding dropped from an activation over an active one.
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([records[0], records[1] with { PreviousBinding = null }])));
        // 5. Recorded signed BOM bytes tampered.
        var tamperedBytes = head.SignedBomBytes!.ToArray();
        tamperedBytes[^20] ^= 0x01;
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with { SignedBomBytes = tamperedBytes }])));

        // The untampered journal still recovers.
        var recovered = Authority(signer, new FrozenTruthStore(records));
        Assert.True(recovered.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(second), binding!.ReleaseBomSha256);
    }
}

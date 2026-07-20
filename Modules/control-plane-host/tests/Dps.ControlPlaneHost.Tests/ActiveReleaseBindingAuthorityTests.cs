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

    private sealed class SequencedTokenSource(string seed) : IExecutionTokenSource
    {
        private int _next;

        public string NextToken()
        {
            _next++;
            var material = Encoding.UTF8.GetBytes(seed + ":" + _next);
            return Convert.ToHexStringLower(SHA256.HashData(material));
        }
    }

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

        public byte[] SignBom(string bomId, string? algorithm = null, string? keyIdOverride = null)
        {
            var payload = new JsonObject
            {
                ["bom_id"] = bomId,
                ["release_bom_generation"] = 1,
                ["modules"] = new JsonArray(
                    new JsonObject { ["module_id"] = "control-plane-host", ["sha256"] = new string('a', 64) })
            };
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
            return Encoding.UTF8.GetBytes(payload.ToJsonString());
        }

        public void Dispose() => Rsa.Dispose();
    }

    private static ActiveReleaseBindingAuthority Authority(
        TestSigner signer,
        string tokenSeed = "seed-a")
        => new([signer.TrustKey], new SequencedTokenSource(tokenSeed), () => Now);

    [Fact, Trait("Category", "Unit")]
    public void ActivateExposesVerifiedActiveBinding()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1");
        var receipt = authority.Activate(Device, bom);

        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.NotNull(binding);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bom)), binding!.ReleaseBomSha256);
        Assert.Equal(1, binding.Generation);
        Assert.Equal("active", binding.Status);
        Assert.Equal(signer.Identity, binding.SignerIdentity);
        Assert.Equal(signer.KeyId, binding.SignerKeyId);
        Assert.Matches("^[a-f0-9]{64}$", binding.ExecutionToken);
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
        authority.Activate(Device, signer.SignBom("bom-1"));
        Assert.True(authority.TryReadActive(Device, out var first));
        var firstToken = first!.ExecutionToken;

        var receipt = authority.Activate(Device, signer.SignBom("bom-2"));
        Assert.True(authority.TryReadActive(Device, out var second));
        Assert.Equal(2, second!.Generation);
        Assert.NotEqual(firstToken, second.ExecutionToken);
        Assert.NotEqual(firstToken, second.ExecutionToken);
        Assert.NotNull(receipt.From);
        Assert.Equal("previous", receipt.From!.Status);
        Assert.Equal(first.ReleaseBomSha256, receipt.From.ReleaseBomSha256);
        Assert.Equal("active", receipt.To.Status);
        Assert.Equal(2, receipt.Sequence);
        // Only the active token is ever readable; the demoted binding is gone
        // from the read surface entirely.
        Assert.True(authority.TryReadActive(Device, out var read));
        Assert.Equal(second.ExecutionToken, read!.ExecutionToken);
    }

    [Fact, Trait("Category", "Unit")]
    public void RevokeFailsReaderClosedAndWritesVersionedReceipt()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        authority.Activate(Device, signer.SignBom("bom-1"));
        Assert.True(authority.TryReadActive(Device, out var active));

        var receipt = authority.Revoke(Device, active!.Generation);
        Assert.False(authority.TryReadActive(Device, out var afterRevoke));
        Assert.Null(afterRevoke);
        Assert.Equal("revocation", receipt.ReceiptKind);
        Assert.Equal("active", receipt.From!.Status);
        Assert.Equal("revoked", receipt.To.Status);
        Assert.Equal(active.ReleaseBomSha256, receipt.From.ReleaseBomSha256);
        Assert.Equal(active.ReleaseBomSha256, receipt.To.ReleaseBomSha256);
        Assert.Equal(active.Generation, receipt.To.Generation);
        Assert.Equal(2, receipt.Sequence);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackRestoresPreviousDigestWithNewGenerationAndToken()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var firstBom = signer.SignBom("bom-1");
        authority.Activate(Device, firstBom);
        authority.Activate(Device, signer.SignBom("bom-2"));
        Assert.True(authority.TryReadActive(Device, out var abandoned));

        var receipt = authority.Rollback(Device);
        Assert.True(authority.TryReadActive(Device, out var restored));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(firstBom)), restored!.ReleaseBomSha256);
        Assert.Equal(3, restored.Generation);
        Assert.NotEqual(abandoned!.ExecutionToken, restored.ExecutionToken);
        Assert.Equal("rollback", receipt.ReceiptKind);
        Assert.Equal("revoked", receipt.From!.Status);
        Assert.Equal(abandoned.ReleaseBomSha256, receipt.From.ReleaseBomSha256);
        Assert.Equal(abandoned.Generation, receipt.From.Generation);
        Assert.Equal("active", receipt.To.Status);
        Assert.Equal(restored.ReleaseBomSha256, receipt.To.ReleaseBomSha256);
        Assert.Equal(3, receipt.To.Generation);
        Assert.Equal(3, receipt.Sequence);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void BadSignatureFailsClosedWithZeroStateResidue()
    {
        using var signer = new TestSigner();
        using var stranger = new TestSigner("stranger-key-v1", "stranger-controller");
        var authority = Authority(signer);
        // Signed by an untrusted RSA key but claiming the trusted key id.
        var forged = stranger.SignBom("bom-1", keyIdOverride: signer.KeyId);

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(Device, forged));
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownKeyIdFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1", keyIdOverride: "unknown-key-v1");

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(Device, bom));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownAlgorithmFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1", algorithm: "rsa-sha256");

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(Device, bom));
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
            keys, new SequencedTokenSource("seed-parser"), () => Now);

        authority.Activate(Device, signer.SignBom("bom-1"));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(signer.Identity, binding!.SignerIdentity);
    }

    [Fact, Trait("Category", "Unit")]
    public void TamperedPayloadReplayFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1");
        var text = Encoding.UTF8.GetString(bom).Replace("bom-1", "bom-9");
        var tampered = Encoding.UTF8.GetBytes(text);

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(Device, tampered));
        Assert.False(authority.TryReadActive(Device, out _));
        // The untampered original still verifies afterwards.
        authority.Activate(Device, bom);
        Assert.True(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void RevokeWrongGenerationFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        authority.Activate(Device, signer.SignBom("bom-1"));

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Revoke(Device, 2));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal("active", binding!.Status);
        Assert.Single(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void RepeatedRevokeFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        authority.Activate(Device, signer.SignBom("bom-1"));
        authority.Revoke(Device, 1);

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Revoke(Device, 1));
        Assert.Equal(2, authority.ReadReceipts(Device).Count);
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackWithoutPreviousFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        authority.Activate(Device, signer.SignBom("bom-1"));

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device));
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(OtherDevice));
    }

    [Fact, Trait("Category", "Unit")]
    public void RevokedBindingIsNeverARollbackTarget()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        authority.Activate(Device, signer.SignBom("bom-1"));
        authority.Activate(Device, signer.SignBom("bom-2"));
        authority.Rollback(Device);

        // bom-2 is now revoked and the previous slot is consumed: a second
        // rollback has no signed previous BOM left and must fail closed.
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(3, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void ActivationOverRevokedRecordsRevokedFromAndNeverLaundersItToPrevious()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        authority.Activate(Device, signer.SignBom("bom-1"));
        var second = authority.Activate(Device, signer.SignBom("bom-2"));
        authority.Revoke(Device, second.To.Generation);

        var receipt = authority.Activate(Device, signer.SignBom("bom-3"));

        // The receipt tells the truth: the prior binding stays revoked, it is
        // not demoted to "previous".
        Assert.NotNull(receipt.From);
        Assert.Equal("revoked", receipt.From!.Status);
        Assert.Equal(2, receipt.From.Generation);
        // No rollback path survives across a revocation: neither the revoked
        // bom-2 nor the older bom-1 is reachable.
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(3, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackAwayFromRevokedActiveRestoresTheTruePrevious()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var firstBom = signer.SignBom("bom-1");
        authority.Activate(Device, firstBom);
        var second = authority.Activate(Device, signer.SignBom("bom-2"));
        authority.Revoke(Device, second.To.Generation);

        var receipt = authority.Rollback(Device);

        Assert.Equal("revoked", receipt.From!.Status);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(firstBom)), binding!.ReleaseBomSha256);
        Assert.Equal(3, binding.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void GenerationIsStrictlyMonotonicAcrossManyActivations()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        for (var round = 1; round <= 5; round++)
        {
            authority.Activate(Device, signer.SignBom("bom-" + round));
            Assert.True(authority.TryReadActive(Device, out var binding));
            Assert.Equal(round, binding!.Generation);
        }
        var receipts = authority.ReadReceipts(Device);
        Assert.Equal(5, receipts.Count);
        Assert.Equal(Enumerable.Range(1, 5).Select(static value => (long)value),
            receipts.Select(static receipt => receipt.Sequence));
    }

    [Fact, Trait("Category", "Unit")]
    public void TokenComesOnlyFromTheInjectedSourceNeverFromInputs()
    {
        using var signer = new TestSigner();
        var bom = signer.SignBom("bom-1");
        var first = Authority(signer, "seed-a");
        var second = Authority(signer, "seed-b");
        first.Activate(Device, bom);
        second.Activate(Device, bom);

        Assert.True(first.TryReadActive(Device, out var bindingA));
        Assert.True(second.TryReadActive(Device, out var bindingB));
        // Identical device, identical BOM bytes, identical clock — the token
        // still differs because it is a pure function of the token source.
        Assert.NotEqual(bindingA!.ExecutionToken, bindingB!.ExecutionToken);
        Assert.Equal(bindingA.ReleaseBomSha256, bindingB.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void ReceiptSequenceIsSharedAcrossAllKinds()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        authority.Activate(Device, signer.SignBom("bom-1"));
        authority.Activate(Device, signer.SignBom("bom-2"));
        authority.Rollback(Device);
        authority.Activate(Device, signer.SignBom("bom-3"));
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
}

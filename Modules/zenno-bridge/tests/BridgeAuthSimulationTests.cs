using System.Security.Cryptography;
using Dps.ZennoBridge;
using Xunit;

namespace Dps.ZennoBridge.Tests;

public sealed class BridgeAuthSimulationTests
{
    [Fact]
    [Trait("Category", "SecuritySimulation")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Pinned_proof_accepts_once_then_rejects_replay_tampering_and_rogue_signer()
    {
        using var signingKey = RSA.Create(2048);
        var parameters = signingKey.ExportParameters(includePrivateParameters: false);
        var keyId = BridgeTrustConfiguration.ComputeKeyId(parameters.Modulus!, parameters.Exponent!);
        var trust = new BridgeTrustConfiguration(
            keyId,
            Convert.ToBase64String(parameters.Modulus!),
            Convert.ToBase64String(parameters.Exponent!),
            maximumClockSkewSeconds: 120);
        var verifier = new BridgePeerProofVerifier(trust);
        var directive = SignedDirective(signingKey, keyId, new string('a', 64));

        verifier.Verify(directive, directive.AuthNonce);
        Assert.Throws<BridgeProtocolException>(() => verifier.Verify(directive, directive.AuthNonce));

        var tampered = SignedDirective(signingKey, keyId, new string('b', 64));
        tampered.CommandId = "tampered-command";
        Assert.Throws<BridgeProtocolException>(() => verifier.Verify(tampered, tampered.AuthNonce));

        using var rogue = RSA.Create(2048);
        var rogueSigned = SignedDirective(rogue, keyId, new string('c', 64));
        Assert.Throws<BridgeProtocolException>(() => verifier.Verify(rogueSigned, rogueSigned.AuthNonce));
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Legacy_key_id_matches_modern_subject_public_key_info_fingerprint()
    {
        using var signingKey = RSA.Create(2048);
        var parameters = signingKey.ExportParameters(includePrivateParameters: false);
        var expected = "sha256_" + Convert.ToHexString(
            SHA256.HashData(signingKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        Assert.Equal(
            expected,
            BridgeTrustConfiguration.ComputeKeyId(parameters.Modulus!, parameters.Exponent!));
        Assert.Equal("rsa-pkcs1-sha256", BridgeTrustConfiguration.SignatureAlgorithm);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Oversized_loopback_response_is_rejected_before_deserialization()
    {
        using var declaredOversized = new MemoryStream(new byte[] { (byte)'{' });
        Assert.Throws<BridgeProtocolException>(() =>
            LoopbackBridgeClient.ReadDirective(
                declaredOversized,
                LoopbackBridgeClient.MaximumResponseBytes + 1L));

        using var chunkedOversized = new MemoryStream(
            Enumerable.Repeat((byte)' ', LoopbackBridgeClient.MaximumResponseBytes + 1).ToArray());
        Assert.Throws<BridgeProtocolException>(() =>
            LoopbackBridgeClient.ReadDirective(chunkedOversized, -1));
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Missing_trust_configuration_fails_before_network_io()
    {
        var exchange = new BridgeExchange
        {
            SchemaVersion = "1.0",
            ContractId = BridgeProtocolValidator.ExchangeContract,
            ProducerModule = BridgeProtocolValidator.ExchangeProducer,
            SoulId = "soul_" + new string('a', 64),
            DeviceBindingId = "db_" + new string('b', 32),
            PlatformAccountId = "pa_" + new string('c', 32),
            TraceId = "trace_" + new string('d', 32),
            IdempotencyKey = "idem_" + new string('e', 64),
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            PrivacyClass = "personal",
            AuthNonce = new string('d', 64),
            ExchangeKind = "POLL",
            CommandId = null,
            ActionKind = null,
            StepKind = null,
            Selector = null,
            Text = null,
            WaitMs = null,
            ExpectedPostcondition = null,
            NativeStatus = null,
            NativeDetail = null,
            PostconditionVerified = null
        };

        var error = Assert.Throws<BridgeProtocolException>(() => new LoopbackBridgeClient().Exchange(exchange));
        Assert.Contains("not configured", error.Message, StringComparison.Ordinal);
    }

    private static BridgeDirective SignedDirective(RSA signingKey, string claimedKeyId, string nonce)
    {
        var directive = new BridgeDirective
        {
            SchemaVersion = "1.0",
            ContractId = BridgeProtocolValidator.DirectiveContract,
            ProducerModule = BridgeProtocolValidator.DirectiveProducer,
            SoulId = "soul_" + new string('a', 64),
            DeviceBindingId = "db_" + new string('b', 32),
            PlatformAccountId = "pa_" + new string('c', 32),
            TraceId = "trace_" + new string('d', 32),
            IdempotencyKey = "idem_" + new string('e', 64),
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            PrivacyClass = "personal",
            AuthKeyId = claimedKeyId,
            AuthNonce = nonce,
            AuthIssuedAt = DateTimeOffset.UtcNow.ToString("O"),
            AuthBodySha256 = string.Empty,
            AuthProof = string.Empty,
            DirectiveKind = "COMMAND",
            CommandId = "command-1",
            ActionKind = "TAP",
            StepKind = "TAP_SELECTOR",
            Selector = "fixture-button",
            Text = null,
            WaitMs = null,
            ExpectedPostcondition = "fixture-open"
        };
        directive.AuthBodySha256 = BridgePeerProofVerifier.ComputeDirectiveBodySha256(directive);
        var statement = BridgePeerProofVerifier.CreateSigningStatement(
            directive.AuthKeyId,
            directive.AuthNonce,
            directive.AuthIssuedAt,
            directive.AuthBodySha256);
        directive.AuthProof = Convert.ToBase64String(signingKey.SignData(
            statement,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        return directive;
    }
}

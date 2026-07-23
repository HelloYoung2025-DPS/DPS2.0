using System.Reflection;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.FactoryReleaseController.Contracts;
using Xunit;

namespace Dps.FactoryReleaseController.Contracts.Tests;

public sealed class ReleaseBomCanonicalNumberContractTests
{
    private const string CorpusResource =
        "Dps.FactoryReleaseController.Contracts.Tests.release-bom.canonical-number.v1.corpus.json";
    private const string StringCorpusResource =
        "Dps.FactoryReleaseController.Contracts.Tests.release-bom.canonical-string.v1.corpus.json";
    private const string NumberCorpusSha256 =
        "14f115b4acb3b11e4cc97b4fd657eea6b112841b3ee7bdc6b293e9fae4add4d3";
    private const string StringCorpusSha256 =
        "a7a132a48170ce6495af87706faa722670d4ceb856620436b5906e78d1ee42f9";

    [Fact, Trait("Category", "Contract")]
    public void CanonicalNumberCorpusMatchesCandidateValidator()
    {
        var corpusBytes = LoadResource(CorpusResource);
        Assert.Equal(
            NumberCorpusSha256,
            Convert.ToHexStringLower(SHA256.HashData(corpusBytes)));
        using var corpus = JsonDocument.Parse(corpusBytes);
        Assert.Equal(
            "dps.release-bom-canonical-number-corpus/v1",
            corpus.RootElement.GetProperty("schema_version").GetString());
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(62, cases.Length);
        Assert.Equal(18, cases.Count(item => item.GetProperty("outcome").GetString() == "accept"));
        Assert.Equal(35, cases.Count(item => item.GetProperty("outcome").GetString() == "normalize"));
        Assert.Equal(9, cases.Count(item => item.GetProperty("outcome").GetString() == "reject"));
        Assert.All(cases, item => Assert.Contains(
            item.GetProperty("outcome").GetString(),
            new[] { "accept", "normalize", "reject" }));

        foreach (var item in cases)
        {
            var wire = item.GetProperty("wire").GetString()!;
            var outcome = item.GetProperty("outcome").GetString()!;
            if (outcome == "reject")
            {
                AssertCanonicalizationRejected(() => CanonicalizeNumber(wire));
                continue;
            }

            var canonical = item.GetProperty("canonical").GetString()!;
            Assert.Equal("{\"n\":" + canonical + "}", CanonicalizeNumber(wire));
            Assert.Equal(outcome == "accept", wire == canonical);
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void CanonicalStringCorpusMatchesCandidateValidator()
    {
        var corpusBytes = LoadResource(StringCorpusResource);
        Assert.Equal(
            StringCorpusSha256,
            Convert.ToHexStringLower(SHA256.HashData(corpusBytes)));
        using var corpus = JsonDocument.Parse(corpusBytes);
        Assert.Equal(
            "dps.release-bom-canonical-string-corpus/v1",
            corpus.RootElement.GetProperty("schema_version").GetString());
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(4, cases.Length);
        Assert.Equal(4, cases.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
        foreach (var item in cases)
        {
            var wire = Convert.FromBase64String(
                item.GetProperty("wire_base64").GetString()!);
            var expected = Convert.FromBase64String(
                item.GetProperty("canonical_base64").GetString()!);
            using var value = JsonDocument.Parse(wire);
            Assert.Equal(
                expected,
                ReleaseBomAuthorityTrustVerifierV1.CanonicalizeForContractTests(
                    value.RootElement));
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void CanonicalIntegerDigitLimitIsExactly4300()
    {
        var accepted = "1" + new string('0', 4_299);
        Assert.Equal("{\"n\":" + accepted + "}", CanonicalizeNumber(accepted));

        var rejected = "1" + new string('0', 4_300);
        using var document = JsonDocument.Parse("{\"n\":" + rejected + "}");
        Assert.Throws<InvalidDataException>(
            () => ReleaseBomAuthorityTrustVerifierV1.CanonicalizeForContractTests(
                document.RootElement));
    }

    [Fact, Trait("Category", "Contract")]
    public void PublicVerifierAcceptsMatchingRuntimeSignedBomAndReceipt()
    {
        using var fixture = new SignedTrustFixture();
        var bom = fixture.BuildCanonicalBom();
        var receipt = fixture.BuildReceipt(bom);

        var verified = ReleaseBomAuthorityTrustVerifierV1.Verify(
            bom, receipt, fixture.Anchors, fixture.VerificationTime);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(bom)),
            verified.ReleaseBomSha256);
        Assert.Equal("candidate-bom-001", verified.ReleaseBomId);
        Assert.Equal(2, verified.ReleaseBomGeneration);
    }

    [Theory, Trait("Category", "Contract")]
    [InlineData("STABLE")]
    [InlineData("DRAFT")]
    public void PublicVerifierRejectsFreshlySignedNonCandidateStatus(string status)
    {
        using var fixture = new SignedTrustFixture();
        var bom = fixture.BuildCanonicalBom(status);
        var receipt = fixture.BuildReceipt(bom);

        var exception = Assert.Throws<InvalidDataException>(
            () => ReleaseBomAuthorityTrustVerifierV1.Verify(
                bom, receipt, fixture.Anchors, fixture.VerificationTime));

        Assert.Contains("status", exception.Message);
    }

    [Fact, Trait("Category", "Contract")]
    public void TrustAnchorsRequireExponent65537OnlyForTheBomSigner()
    {
        using var profileBomSigner = RSA.Create(2048);
        using var profileReceiptSigner = RSA.Create(2048);
        var exponentThreeSpki = ExportSyntheticRsaSpki(exponent: 3);

        var exception = Assert.Throws<InvalidDataException>(
            () => new ReleaseBomAuthorityTrustAnchorsV1(
                "dps-deployed-release-anchor-v1",
                "wrong-bom-key",
                exponentThreeSpki,
                "receipt-key",
                profileReceiptSigner.ExportSubjectPublicKeyInfo()));
        Assert.Contains("exponent 65537", exception.Message);

        var anchors = new ReleaseBomAuthorityTrustAnchorsV1(
            "dps-deployed-release-anchor-v1",
            "bom-key",
            profileBomSigner.ExportSubjectPublicKeyInfo(),
            "receipt-key",
            exponentThreeSpki);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(exponentThreeSpki)),
            anchors.AuthorityReceiptSignerSpkiSha256);
    }

    [Fact, Trait("Category", "Contract")]
    public void PublicVerifierRejectsFreshlySignedNonCanonicalFloatAlias()
    {
        using var fixture = new SignedTrustFixture();
        var aliasBom = fixture.BuildFreshlySignedFloatAliasBom();
        var receipt = fixture.BuildReceipt(aliasBom);

        var exception = Assert.Throws<InvalidDataException>(
            () => ReleaseBomAuthorityTrustVerifierV1.Verify(
                aliasBom, receipt, fixture.Anchors, fixture.VerificationTime));
        Assert.Contains("canonical sorted compact wire", exception.Message);
    }

    [Fact, Trait("Category", "Contract")]
    public void PublicVerifierRejectsNonCanonicalReceiptRepresentationsWithTheSameSignature()
    {
        using var fixture = new SignedTrustFixture();
        var bom = fixture.BuildCanonicalBom();
        var canonicalReceipt = fixture.BuildReceipt(bom);
        var variants = new[]
        {
            canonicalReceipt.Concat(new byte[] { (byte)'\n' }).ToArray(),
            Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(canonicalReceipt)),
            ReverseTopLevelProperties(canonicalReceipt),
        };
        foreach (var variant in variants)
        {
            Assert.NotEqual(canonicalReceipt, variant);
            var exception = Assert.Throws<InvalidDataException>(
                () => ReleaseBomAuthorityTrustVerifierV1.Verify(
                    bom, variant, fixture.Anchors, fixture.VerificationTime));
            Assert.Contains("canonical sorted compact JSON wire", exception.Message);
        }

        var alias = JsonNode.Parse(canonicalReceipt)!.AsObject();
        var signature = alias["signature"]!.AsObject();
        signature["value"] = NonCanonicalBase64PadAlias(
            signature["value"]!.GetValue<string>());
        var canonicalAliasWire = SignedTrustFixture.CanonicalizeNode(alias);
        var aliasException = Assert.Throws<InvalidDataException>(
            () => ReleaseBomAuthorityTrustVerifierV1.Verify(
                bom,
                canonicalAliasWire,
                fixture.Anchors,
                fixture.VerificationTime));
        Assert.Contains("canonical base64", aliasException.Message);

        var representativeAlias = fixture.BuildReceiptWithRepresentativeAlias(bom);
        var representativeException = Assert.Throws<InvalidDataException>(
            () => ReleaseBomAuthorityTrustVerifierV1.Verify(
                bom,
                representativeAlias,
                fixture.Anchors,
                fixture.VerificationTime));
        Assert.Contains("canonical RSA representative", representativeException.Message);

        var shortRepresentative = fixture.BuildReceiptWithShortRepresentative(bom);
        var shortException = Assert.Throws<InvalidDataException>(
            () => ReleaseBomAuthorityTrustVerifierV1.Verify(
                bom,
                shortRepresentative,
                fixture.Anchors,
                fixture.VerificationTime));
        Assert.Contains("canonical RSA representative", shortException.Message);
    }

    private static string CanonicalizeNumber(string wire)
    {
        using var document = JsonDocument.Parse("{\"n\":" + wire + "}");
        return Encoding.UTF8.GetString(
            ReleaseBomAuthorityTrustVerifierV1.CanonicalizeForContractTests(
                document.RootElement));
    }

    private static byte[] LoadResource(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"embedded contract resource is missing: {resource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void AssertCanonicalizationRejected(Action action)
    {
        var exception = Record.Exception(action);
        Assert.NotNull(exception);
        Assert.True(
            exception is JsonException or InvalidDataException,
            $"Unexpected exception type: {exception.GetType().FullName}");
    }

    private static byte[] ReverseTopLevelProperties(byte[] canonical)
    {
        using var document = JsonDocument.Parse(canonical);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject().Reverse())
                property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string NonCanonicalBase64PadAlias(string value)
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var padding = value.Length - value.TrimEnd('=').Length;
        Assert.Contains(padding, new[] { 1, 2 });
        var index = value.Length - padding - 1;
        var replacement = alphabet[alphabet.IndexOf(value[index], StringComparison.Ordinal) ^ 1];
        var alias = value[..index] + replacement + value[(index + 1)..];
        Assert.Equal(Convert.FromBase64String(value), Convert.FromBase64String(alias));
        Assert.NotEqual(value, alias);
        return alias;
    }

    private static byte[] ExportSyntheticRsaSpki(int exponent)
    {
        var modulus = Enumerable.Repeat((byte)0xa5, 256).ToArray();
        modulus[0] = 0xc5;
        modulus[^1] = 0x9d;
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = modulus,
            Exponent = new BigInteger(exponent).ToByteArray(
                isUnsigned: true,
                isBigEndian: true),
        });
        return rsa.ExportSubjectPublicKeyInfo();
    }

    private sealed class SignedTrustFixture : IDisposable
    {
        private const string BomResource =
            "Dps.FactoryReleaseController.Contracts.Tests.r0c.bom.json";
        private const string ReceiptPayloadResource =
            "Dps.FactoryReleaseController.Contracts.Tests.r0c.receipt-payload.json";
        private const string BomKeyId = "test-release-bom-key";
        private const string ReceiptKeyId = "test-owner-receipt-key";
        private readonly RSA _bomSigner = RSA.Create(2048);
        private readonly RSA _receiptSigner = RSA.Create(2048);

        public DateTimeOffset VerificationTime { get; } =
            new(2026, 7, 14, 0, 0, 1, TimeSpan.Zero);

        public ReleaseBomAuthorityTrustAnchorsV1 Anchors =>
            new(
                "dps-deployed-release-anchor-v1",
                BomKeyId,
                _bomSigner.ExportSubjectPublicKeyInfo(),
                ReceiptKeyId,
                _receiptSigner.ExportSubjectPublicKeyInfo());

        public byte[] BuildCanonicalBom(string status = "SIGNED")
        {
            var payload = LoadNode(BomResource);
            payload.AsObject().Remove("signature");
            payload["status"] = status;
            payload["feature_flags"]!.AsObject()["shadow_ratio"] = 0.5;
            var canonicalPayload = Canonicalize(payload);
            var signature = _bomSigner.SignData(
                Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
                    .Concat(canonicalPayload).ToArray(),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            payload["signature"] = new JsonObject
            {
                ["algorithm"] = "rsa-pss-sha256",
                ["key_id"] = BomKeyId,
                ["value"] = Convert.ToBase64String(signature),
            };
            return Canonicalize(payload);
        }

        public byte[] BuildFreshlySignedFloatAliasBom()
        {
            var canonical = BuildCanonicalBom();
            var root = JsonNode.Parse(canonical)!.AsObject();
            var originalSignature = root["signature"]!["value"]!.GetValue<string>();
            root.Remove("signature");
            var canonicalPayload = Canonicalize(root);
            var payloadText = Encoding.UTF8.GetString(canonicalPayload);
            Assert.Equal(1, Count(payloadText, "0.5"));
            var aliasPayload = Encoding.UTF8.GetBytes(
                payloadText.Replace("0.5", "5e-1", StringComparison.Ordinal));
            var aliasSignature = Convert.ToBase64String(
                _bomSigner.SignData(
                    Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
                        .Concat(aliasPayload).ToArray(),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss));
            Assert.True(_bomSigner.VerifyData(
                Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
                    .Concat(aliasPayload).ToArray(),
                Convert.FromBase64String(aliasSignature),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss));

            var fullText = Encoding.UTF8.GetString(canonical);
            Assert.Equal(1, Count(fullText, originalSignature));
            return Encoding.UTF8.GetBytes(
                fullText
                    .Replace("0.5", "5e-1", StringComparison.Ordinal)
                    .Replace(
                        originalSignature, aliasSignature, StringComparison.Ordinal));
        }

        public byte[] BuildReceipt(byte[] exactBom)
        {
            var node = LoadNode(ReceiptPayloadResource);
            node["release_bom_sha256"] =
                Convert.ToHexStringLower(SHA256.HashData(exactBom));
            node["receipt_id"] = "native-stop-trust-" + new string('d', 32);
            node["idempotency_key"] = "idem_" + new string('e', 64);
            node["signature"] = new JsonObject
            {
                ["algorithm"] = "rsa-pss-sha256",
                ["key_id"] = ReceiptKeyId,
                ["value"] = "AA==",
            };
            var placeholder = JsonSerializer.Deserialize<
                ReleaseBomNativeStopAuthorityTrustReceiptV1>(node.ToJsonString())
                ?? throw new InvalidOperationException("receipt fixture decoded to null");
            var signingBytes =
                NativeStopAuthorityTrustProtocolV1.CanonicalReceiptSigningBytes(
                    placeholder);
            node["signature"]!["value"] = Convert.ToBase64String(
                _receiptSigner.SignData(
                    signingBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss));
            return Canonicalize(node);
        }

        public byte[] BuildReceiptWithRepresentativeAlias(byte[] exactBom)
        {
            var modulus = _receiptSigner.ExportParameters(false).Modulus!;
            for (var attempt = 0; attempt < 1_024; attempt++)
            {
                var receipt = BuildReceipt(exactBom);
                var node = JsonNode.Parse(receipt)!.AsObject();
                var signature = Convert.FromBase64String(
                    node["signature"]!["value"]!.GetValue<string>());
                var sum = new BigInteger(signature, isUnsigned: true, isBigEndian: true)
                    + new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
                var raw = sum.ToByteArray(isUnsigned: true, isBigEndian: true);
                if (raw.Length > modulus.Length)
                    continue;
                var alias = new byte[modulus.Length];
                raw.CopyTo(alias, alias.Length - raw.Length);
                Assert.NotEqual(signature, alias);
                node["signature"]!["value"] = Convert.ToBase64String(alias);
                return Canonicalize(node);
            }
            throw new InvalidOperationException(
                "unable to construct an RSA s+n representative alias");
        }

        public byte[] BuildReceiptWithShortRepresentative(byte[] exactBom)
        {
            for (var attempt = 0; attempt < 4_096; attempt++)
            {
                var receipt = BuildReceipt(exactBom);
                var node = JsonNode.Parse(receipt)!.AsObject();
                var signature = Convert.FromBase64String(
                    node["signature"]!["value"]!.GetValue<string>());
                if (signature[0] != 0)
                    continue;
                node["signature"]!["value"] =
                    Convert.ToBase64String(signature[1..]);
                return Canonicalize(node);
            }
            throw new InvalidOperationException(
                "unable to construct a short RSA I2OSP representative");
        }

        private static JsonNode LoadNode(string resource)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"embedded fixture is missing: {resource}");
            return JsonNode.Parse(stream)
                ?? throw new InvalidOperationException(
                    $"embedded fixture decoded to null: {resource}");
        }

        internal static byte[] CanonicalizeNode(JsonNode node)
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            return ReleaseBomAuthorityTrustVerifierV1.CanonicalizeForContractTests(
                document.RootElement);
        }

        private static byte[] Canonicalize(JsonNode node)
            => CanonicalizeNode(node);

        private static int Count(string source, string value)
            => (source.Length - source.Replace(
                value, string.Empty, StringComparison.Ordinal).Length)
                / value.Length;

        public void Dispose()
        {
            _bomSigner.Dispose();
            _receiptSigner.Dispose();
        }
    }
}

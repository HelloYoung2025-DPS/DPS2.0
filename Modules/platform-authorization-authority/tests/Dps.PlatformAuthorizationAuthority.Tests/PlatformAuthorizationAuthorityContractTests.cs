using System.Security.Cryptography;
using System.Text.Json;
using Dps.PlatformAuthorizationAuthority.Contracts;
using Xunit;

namespace Dps.PlatformAuthorizationAuthority.Tests;

public sealed class PlatformAuthorizationAuthorityContractTests
{
    private const string ContractCategory = "Contract";

    [Fact, Trait("Category", ContractCategory)]
    public void Strict_codec_regex_initializes_with_absolute_end_lookahead()
    {
        var evidence = PlatformAuthorizationAuthorityContractJson.DeserializeEvidenceStrict(ValidJson);
        Assert.Equal(SignedPlatformAuthorizationEvidenceV1.CurrentContractId, evidence.ContractId);
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Corpus_accepts_every_valid_case_and_rejects_every_invalid_case()
    {
        var rawCorpus = File.ReadAllText(CorpusPath);
        Assert.Contains("9223372036854775807", rawCorpus, StringComparison.Ordinal);
        Assert.Contains("9223372036854775808", rawCorpus, StringComparison.Ordinal);
        Assert.DoesNotContain("9223372036854776000", rawCorpus, StringComparison.Ordinal);
        using var corpus = JsonDocument.Parse(rawCorpus);
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var baseline = cases.Single(item => item.GetProperty("name").GetString() == "valid-z")
            .GetProperty("json").GetString()!;
        var validCount = 0;
        var invalidCount = 0;
        foreach (var item in cases)
        {
            var json = Materialize(item, baseline);
            if (item.GetProperty("valid").GetBoolean())
            {
                PlatformAuthorizationAuthorityContractJson.DeserializeEvidenceStrict(json);
                validCount++;
            }
            else
            {
                Assert.ThrowsAny<Exception>(() => PlatformAuthorizationAuthorityContractJson.DeserializeEvidenceStrict(json));
                invalidCount++;
            }
        }
        Assert.Equal(2, validCount);
        Assert.Equal(17, invalidCount);
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Strict_json_rejects_missing_unknown_and_duplicate_top_level_fields()
    {
        var json = ValidJson;
        Assert.ThrowsAny<Exception>(() => Parse(json.Replace("\"privacy_class\":\"sensitive\",", string.Empty, StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => Parse(InsertBeforeClose(json, ",\"unknown\":true")));
        Assert.ThrowsAny<Exception>(() => Parse(json.Replace(
            "\"schema_version\":\"1.0.0\"",
            "\"schema_version\":\"1.0.0\",\"schema_version\":\"1.0.0\"",
            StringComparison.Ordinal)));
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Strict_json_rejects_recursive_duplicate_properties_before_unknown_field_handling()
    {
        Assert.ThrowsAny<Exception>(() => Parse(InsertBeforeClose(
            ValidJson,
            ",\"unknown_object\":{\"nested\":1,\"nested\":2}")));
    }

    [Fact, Trait("Category", ContractCategory)]
    public void All_three_timestamps_require_exact_zero_offset_lexemes()
    {
        foreach (var field in new[] { "occurred_at", "issued_at", "expires_at" })
        {
            var original = Parse(ValidJson).GetType().GetProperty(ToPropertyName(field));
            Assert.NotNull(original);
        }
        Assert.ThrowsAny<Exception>(() => Parse(ValidJson.Replace("2026-07-15T00:00:01Z", "2026-07-15T00:00:01.12345678Z", StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => Parse(ValidJson.Replace("2026-07-15T00:00:00Z", "2026-07-15T01:00:00+01:00", StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => Parse(ValidJson.Replace("2026-07-15T00:15:00Z", "2026-07-15T00:15:00Z\\n", StringComparison.Ordinal)));
        foreach (var mutation in new[]
                 {
                     ("2026-07-15T00:00:01Z", "0000-07-15T00:00:01Z"),
                     ("2026-07-15T00:00:01Z", "2026-07-15T00:00:60Z"),
                     ("2026-07-15T00:00:00Z", "0000-07-15T00:00:00Z"),
                     ("2026-07-15T00:00:00Z", "2026-07-15T00:00:60Z"),
                     ("2026-07-15T00:15:00Z", "0000-07-15T00:15:00Z"),
                     ("2026-07-15T00:15:00Z", "2026-07-15T00:15:60Z")
                 })
            Assert.ThrowsAny<Exception>(() => Parse(ValidJson.Replace(mutation.Item1, mutation.Item2, StringComparison.Ordinal)));
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Platform_is_lowercase_normalized_and_rejects_space_cr_lf_and_extra_separator()
    {
        foreach (var invalid in new[] { "Fixture", "fixture app", "fixture\\rapp", "fixture\\napp", "fixture--app", "fixture-" })
        {
            Assert.ThrowsAny<Exception>(() => Parse(ReplaceJsonString(ValidJson, "platform", invalid)));
        }
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Alias_key_id_is_lowercase_and_rejects_space_cr_lf_and_oversize()
    {
        foreach (var invalid in new[] { "Tenant-hmac-v1", "tenant hmac", "tenant\\rkey", "tenant\\nkey", new string('a', 65) })
        {
            Assert.ThrowsAny<Exception>(() => Parse(ReplaceJsonString(ValidJson, "alias_key_id", invalid)));
        }
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Authorization_evidence_id_is_lowercase_prefixed_and_rejects_control_space_and_oversize()
    {
        foreach (var invalid in new[] { "Approval_fixture", "approval_", "approval_bad value", "approval_bad\\r", "approval_bad\\n", "approval_" + new string('a', 120) })
        {
            Assert.ThrowsAny<Exception>(() => Parse(ReplaceJsonString(ValidJson, "authorization_evidence_id", invalid)));
        }
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Signature_requires_canonical_base64_for_exact_64_byte_p256_p1363()
    {
        var evidence = Parse(ValidJson);
        Assert.Equal(64, Convert.FromBase64String(evidence.SignatureBase64).Length);
        foreach (var invalid in new[]
                 {
                     Convert.ToBase64String(new byte[63]),
                     Convert.ToBase64String(new byte[65]),
                     Convert.ToBase64String(new byte[64]) + "\n",
                     " " + Convert.ToBase64String(new byte[64])
                 })
        {
            Assert.ThrowsAny<Exception>(() => Parse(ReplaceJsonString(ValidJson, "signature_base64", invalid)));
        }
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Canonical_identifiers_and_versions_reject_trailing_newlines_and_uppercase_hex()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReplaceJsonString(ValidJson, "schema_version", "1.0.0\n")));
        Assert.ThrowsAny<Exception>(() => Parse(ReplaceJsonString(ValidJson, "soul_id", "soul_" + new string('A', 64))));
        Assert.ThrowsAny<Exception>(() => Parse(ReplaceJsonString(ValidJson, "trace_id", "trace_" + new string('a', 32) + "\n")));
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Schema_pins_exact_timestamp_patterns_lowercase_fields_and_int64_maxima()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var properties = schema.RootElement.GetProperty("properties");
        const string utcPattern = "^(?!0000)\\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\\d|3[01])T(?:[01]\\d|2[0-3]):[0-5]\\d:[0-5]\\d(?:\\.\\d{1,7})?(?:Z|\\+00:00)$(?![\\s\\S])";
        Assert.Equal(utcPattern, properties.GetProperty("occurred_at").GetProperty("pattern").GetString());
        Assert.Equal(utcPattern, properties.GetProperty("issued_at").GetProperty("pattern").GetString());
        Assert.Equal(utcPattern, properties.GetProperty("expires_at").GetProperty("pattern").GetString());
        Assert.StartsWith("^approval_[a-z0-9_-]", properties.GetProperty("authorization_evidence_id").GetProperty("pattern").GetString());
        Assert.StartsWith("^[a-z0-9]", properties.GetProperty("alias_key_id").GetProperty("pattern").GetString());
        foreach (var name in new[] { "alias_key_epoch", "authorization_revision", "release_generation" })
            Assert.Equal(long.MaxValue, properties.GetProperty(name).GetProperty("maximum").GetInt64());
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Canonicalizer_is_deterministic_and_length_prefixes_delimiter_collisions()
    {
        var baseline = Parse(ValidJson);
        var collisionA = baseline with { AuthorizationEvidenceId = "approval_ab", Platform = "c" };
        var collisionB = baseline with { AuthorizationEvidenceId = "approval_a", Platform = "bc" };
        var first = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(collisionA);
        var second = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(collisionB);
        var repeated = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(collisionA);
        try
        {
            Assert.Equal(first, repeated);
            Assert.NotEqual(first, second);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
            CryptographicOperations.ZeroMemory(repeated);
        }
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Evidence_validity_window_is_positive_ordered_and_never_exceeds_fifteen_minutes()
    {
        var baseline = Parse(ValidJson);
        Assert.Throws<ArgumentException>(() => (baseline with { ExpiresAt = baseline.IssuedAt.AddMinutes(15).AddTicks(1) }).Validate());
        Assert.Throws<ArgumentException>(() => (baseline with { ExpiresAt = baseline.IssuedAt }).Validate());
        Assert.Throws<ArgumentException>(() => (baseline with { OccurredAt = baseline.IssuedAt.AddMinutes(-1) }).Validate());
    }

    [Fact, Trait("Category", ContractCategory)]
    public void Fixed_trust_metadata_pin_matches_an_exact_p256_spki()
    {
        var spki = Convert.FromBase64String(PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiBase64);
        try
        {
            Assert.Equal(PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiSha256, Convert.ToHexStringLower(SHA256.HashData(spki)));
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(spki, out var bytesRead);
            Assert.Equal(spki.Length, bytesRead);
            Assert.Equal(256, algorithm.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(spki);
        }
    }

    private static string CorpusPath => Path.Combine(AppContext.BaseDirectory, "Contracts", "Corpus", "platform.account.authorization.evidence.v1.corpus.json");
    private static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "Contracts", "platform.account.authorization.evidence.v1.schema.json");
    private static string ValidJson
    {
        get
        {
            using var corpus = JsonDocument.Parse(File.ReadAllText(CorpusPath));
            return corpus.RootElement.GetProperty("cases")[0].GetProperty("json").GetString()!;
        }
    }

    private static SignedPlatformAuthorizationEvidenceV1 Parse(string json) =>
        PlatformAuthorizationAuthorityContractJson.DeserializeEvidenceStrict(json);

    private static string Materialize(JsonElement item, string baseline)
    {
        if (item.TryGetProperty("json", out var exact)) return exact.GetString()!;
        if (item.TryGetProperty("replace", out var replace))
        {
            var from = replace.GetProperty("from").GetString()!;
            var to = replace.GetProperty("to").GetString()!;
            var changed = baseline.Replace(from, to, StringComparison.Ordinal);
            Assert.NotEqual(baseline, changed);
            return changed;
        }
        return InsertBeforeClose(baseline, item.GetProperty("append_before_close").GetString()!);
    }

    private static string ReplaceJsonString(string json, string propertyName, string replacement)
    {
        using var document = JsonDocument.Parse(json);
        var original = document.RootElement.GetProperty(propertyName).GetString()!;
        return json.Replace(
            $"\"{propertyName}\":{JsonSerializer.Serialize(original)}",
            $"\"{propertyName}\":{JsonSerializer.Serialize(replacement)}",
            StringComparison.Ordinal);
    }

    private static string InsertBeforeClose(string json, string value) => json.Insert(json.LastIndexOf('}'), value);

    private static string ToPropertyName(string jsonName) => string.Concat(
        jsonName.Split('_').Select(static segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
}

using System.Text.Json;
using Dps.SoulRegistry.Contracts;
using Xunit;

namespace Dps.SoulRegistry.Tests;

public sealed class SoulResolvedContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void SerializedContractUsesExactSnakeCaseFieldsAndContainsNoRawAlias()
    {
        const string rawAlias = "never.serialize@example.test";
        var resolved = CreateContract();
        resolved.Validate();
        var json = JsonSerializer.Serialize(resolved);
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(static property => property.Name).ToArray();
        Assert.Equal(
            [
                "schema_version", "contract_id", "producer_module", "soul_id", "device_binding_id",
                "platform_account_id", "trace_id", "idempotency_key", "occurred_at", "privacy_class",
                "alias_kind", "alias_digest", "alias_key_id"
            ],
            names);
        Assert.DoesNotContain(rawAlias, json, StringComparison.Ordinal);
        Assert.Equal(SoulResolved.CurrentContractId, document.RootElement.GetProperty("contract_id").GetString());
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("99.1.0")]
    [InlineData("not-semver")]
    [Trait("Category", "Contract")]
    public void UnknownMajorOrInvalidVersionFailsClosed(string version)
    {
        Assert.Throws<NotSupportedException>(() => SoulResolvedValidation.RequireSupportedMajor(version, 1));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void CurrentAndPreviousMinorWithinMajorRemainAccepted()
    {
        SoulResolvedValidation.RequireSupportedMajor("1.0.0", 1);
        SoulResolvedValidation.RequireSupportedMajor("1.9.7", 1);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void InvalidOpaqueScopeOrDigestFailsClosed()
    {
        var invalid = new SoulResolved(
            "soul_invalid",
            "db_invalid",
            "pa_invalid",
            "trace_invalid",
            "idem_invalid",
            new DateTimeOffset(2026, 7, 14, 2, 0, 0, TimeSpan.Zero),
            "email",
            "not-a-digest",
            "key-v1");
        Assert.Throws<ArgumentException>(invalid.Validate);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void PublicOutputHasNoRawAliasOrVerificationProofProperty()
    {
        var propertyNames = typeof(SoulResolved).GetProperties().Select(static property => property.Name).ToArray();
        Assert.DoesNotContain("RawAlias", propertyNames);
        Assert.DoesNotContain("Email", propertyNames);
        Assert.DoesNotContain("Phone", propertyNames);
        Assert.DoesNotContain("EvidenceId", propertyNames);
    }

    private static SoulResolved CreateContract()
        => new(
            "soul_" + new string('a', 64),
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            "trace_33333333333333333333333333333333",
            "idem_4444444444444444444444444444444444444444444444444444444444444444",
            new DateTimeOffset(2026, 7, 14, 2, 0, 0, TimeSpan.Zero),
            "email",
            new string('b', 64),
            "key-v1");
}

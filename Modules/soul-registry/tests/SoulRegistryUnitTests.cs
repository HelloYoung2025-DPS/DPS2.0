using Xunit;

namespace Dps.SoulRegistry.Tests;

public sealed class SoulRegistryUnitTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 7, 14, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public void EmailCanonicalizationAndIdnAreDeterministic()
    {
        using var registry = CreateRegistry("key-v1", 0x11);
        var first = Assert.Single(registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.Email, "  USER@exämple.com "));
        var second = Assert.Single(registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.Email, "user@xn--exmple-cua.com"));
        Assert.Equal(first, second);
        Assert.Matches("^[a-f0-9]{64}\\z", first.Digest);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PhoneCanonicalizationIsE164AndDeterministic()
    {
        using var registry = CreateRegistry("key-v1", 0x22);
        var formatted = Assert.Single(registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.Phone, "+60 (12) 345-6789"));
        var compact = Assert.Single(registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.Phone, "+60123456789"));
        Assert.Equal(formatted, compact);
        Assert.Throws<ArgumentException>(() => registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.Phone, "0123456789"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TenantAndPlatformIdCaseRemainInDigestScope()
    {
        using var registry = CreateRegistry("key-v1", 0x33);
        var tenantA = Assert.Single(registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.PlatformId, "User-42"));
        var tenantB = Assert.Single(registry.ComputeAliasReferences("tenant-b", IdentityAliasKind.PlatformId, "User-42"));
        var caseVariant = Assert.Single(registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.PlatformId, "user-42"));
        Assert.NotEqual(tenantA.Digest, tenantB.Digest);
        Assert.NotEqual(tenantA.Digest, caseVariant.Digest);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void KeyringComputesCurrentAndPreviousDigestsWithoutRawOutput()
    {
        using var oldKey = new AliasHmacKey("key-v1", Enumerable.Repeat((byte)0x44, 32).ToArray());
        using var newKey = new AliasHmacKey("key-v2", Enumerable.Repeat((byte)0x55, 32).ToArray());
        var options = new SoulRegistryOptions("Host=unused", "soul_test", "key-v2", [oldKey, newKey]);
        using var registry = new PostgresSoulRegistry(options);
        var references = registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.Email, "person@example.test");
        Assert.Equal(2, references.Count);
        Assert.Equal(["key-v1", "key-v2"], references.Select(static item => item.KeyId).ToArray());
        Assert.All(references, static item => Assert.DoesNotContain("person", item.Digest, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SensitiveObjectsRedactRawAliasConnectionProofAndKeyBytes()
    {
        const string rawAlias = "secret.person@example.test";
        const string password = "do-not-print-password";
        const string evidence = "proof-must-not-print";
        using var key = new AliasHmacKey("key-v1", Enumerable.Repeat((byte)0x66, 32).ToArray());
        var options = new SoulRegistryOptions($"Host=unused;Password={password}", "soul_test", "key-v1", [key]);
        var request = new RegisterVerifiedAliasRequest(
            RegisterVerifiedAliasRequest.CurrentSchemaVersion,
            "tenant-a",
            IdentityAliasKind.Email,
            rawAlias,
            new AliasVerification(evidence, OccurredAt.AddMinutes(-1)),
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            "trace_33333333333333333333333333333333",
            "idem_4444444444444444444444444444444444444444444444444444444444444444",
            OccurredAt);

        var rendered = string.Join('\n', options, key, request, request.Verification);
        Assert.DoesNotContain(rawAlias, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(password, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(Enumerable.Repeat((byte)0x66, 32).ToArray()), rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InvalidAliasExceptionDoesNotEchoSensitiveInputAndDisposedKeyFailsClosed()
    {
        const string rawAlias = "not-an-email-secret";
        using var registry = CreateRegistry("key-v1", 0x77);
        var error = Assert.Throws<ArgumentException>(() =>
            registry.ComputeAliasReferences("tenant-a", IdentityAliasKind.Email, rawAlias));
        Assert.DoesNotContain(rawAlias, error.ToString(), StringComparison.Ordinal);

        var key = new AliasHmacKey("throwaway", Enumerable.Repeat((byte)0x88, 32).ToArray());
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.CopyKeyBytes());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void KeyAndSchemaIdentifiersRejectTerminalNewlines()
    {
        var keyBytes = Enumerable.Repeat((byte)0x89, 32).ToArray();
        Assert.Throws<ArgumentException>(() => new AliasHmacKey("key-v1\n", keyBytes));

        using var key = new AliasHmacKey("key-v1", keyBytes);
        var options = new SoulRegistryOptions("Host=unused", "soul_test\n", "key-v1", [key]);
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnverifiedProofFailsClosedBeforeDatabaseAccess()
    {
        using var registry = CreateRegistry("key-v1", 0x99);
        var request = new ResolveSoulRequest(
            ResolveSoulRequest.CurrentSchemaVersion,
            "tenant-a",
            IdentityAliasKind.Email,
            "unverified@example.test",
            new AliasVerification("unverified-proof", OccurredAt, Verified: false),
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            "trace_33333333333333333333333333333333",
            "idem_4444444444444444444444444444444444444444444444444444444444444444",
            OccurredAt);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.ResolveAsync(request, TestContext.Current.CancellationToken));
    }

    private static PostgresSoulRegistry CreateRegistry(string keyId, byte fill)
    {
        using var key = new AliasHmacKey(keyId, Enumerable.Repeat(fill, 32).ToArray());
        return new PostgresSoulRegistry(new SoulRegistryOptions("Host=unused", "soul_test", keyId, [key]));
    }
}

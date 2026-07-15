using System.Globalization;
using System.Text.Json;
using Dps.MemoryEventLedger.Contracts;
using Xunit;

namespace Dps.MemoryEventLedger.Tests;

public sealed class MemoryEventContractTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void DatabaseSchemaIdentifierRejectsTrailingNewline()
    {
        Assert.Throws<ArgumentException>(() =>
            new MemoryEventLedgerOptions("Host=unused", "memory_test\n").Validate());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LedgerOptionsNeverRenderConnectionSecrets()
    {
        const string password = "do-not-log-this-password";
        var options = new MemoryEventLedgerOptions(
            $"Host=localhost;Username=dps;Password={password}",
            "memory_test");

        var rendered = options.ToString();

        Assert.DoesNotContain(password, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=localhost", rendered, StringComparison.Ordinal);
        Assert.Contains("memory_test", rendered, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanonicalHash_IsStableAcrossSignalOrderAndCulture()
    {
        var first = TestData.Event(
            eventId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            signals:
            [
                new InterestSignalV1("旅行", 0.40m),
                new InterestSignalV1("coffee", 0.75m)
            ]);
        var reordered = first with
        {
            Observation = first.Observation with
            {
                InterestSignals = first.Observation.InterestSignals.Reverse().ToArray()
            }
        };

        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var firstHash = MemoryEventCanonicalizer.ComputeSha256(first);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var secondHash = MemoryEventCanonicalizer.ComputeSha256(reordered);

            Assert.Equal(firstHash, secondHash);
            Assert.Equal(MemoryEventCanonicalizer.Serialize(first), MemoryEventCanonicalizer.Serialize(reordered));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void UnknownMajor_FailsClosed()
    {
        var memoryEvent = TestData.Event() with { SchemaVersion = "2.0.0" };

        Assert.Throws<NotSupportedException>(memoryEvent.Validate);
        Assert.Throws<NotSupportedException>(() => MemoryEventCanonicalizer.ComputeSha256(memoryEvent));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void DefaultSerializerUsesTheOwnedSnakeCaseWireNames()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(TestData.Event()));
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("schema_version", out _));
        Assert.True(root.TryGetProperty("event_id", out _));
        Assert.True(root.TryGetProperty("observation", out var observation));
        Assert.True(observation.TryGetProperty("content_digest", out _));
        Assert.True(observation.GetProperty("interest_signals")[0].TryGetProperty("topic", out _));
        Assert.False(root.TryGetProperty("SchemaVersion", out _));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void UnverifiedOrInvalidScope_FailsClosed()
    {
        var unverified = TestData.Event() with
        {
            Observation = TestData.Event().Observation with { Verified = false }
        };
        var wrongSoul = TestData.Event() with { SoulId = "soul_not_a_digest" };

        Assert.Throws<InvalidOperationException>(unverified.Validate);
        Assert.Throws<ArgumentException>(wrongSoul.Validate);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void OpaqueEnvelopeIdentifiersRejectTrailingNewlines()
    {
        var memoryEvent = TestData.Event();

        Assert.Throws<ArgumentException>(() => (memoryEvent with { SoulId = memoryEvent.SoulId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (memoryEvent with { DeviceBindingId = memoryEvent.DeviceBindingId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (memoryEvent with { PlatformAccountId = memoryEvent.PlatformAccountId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (memoryEvent with { TraceId = memoryEvent.TraceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (memoryEvent with { IdempotencyKey = memoryEvent.IdempotencyKey + "\n" }).Validate());
    }
}

using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry.Contracts;

namespace Dps.MemoryEventLedger.Tests;

internal static class TestData
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    public static SoulResolved Soul(char digestCharacter, string suffix)
        => new(
            $"soul_{new string(digestCharacter, 64)}",
            $"db_{new string(digestCharacter, 32)}",
            $"pa_{new string(digestCharacter, 32)}",
            $"trace_{new string(digestCharacter, 32)}",
            $"idem_{new string(digestCharacter, 64)}",
            OccurredAt.AddMinutes(-1),
            "email",
            new string(digestCharacter, 64),
            "test-key-v1");

    public static MemoryEventV1 Event(
        Guid? eventId = null,
        SoulResolved? soul = null,
        string? contentDigest = null,
        IReadOnlyList<InterestSignalV1>? signals = null)
    {
        soul ??= Soul('a', "a");

        return new MemoryEventV1(
            MemoryEventV1.CurrentSchemaVersion,
            MemoryEventV1.CurrentContractId,
            MemoryEventV1.CurrentProducerModule,
            eventId ?? Guid.Parse("00000000-0000-0000-0000-000000000001"),
            soul.SoulId,
            soul.DeviceBindingId,
            soul.PlatformAccountId,
            soul.TraceId,
            $"idem_{(eventId ?? Guid.Parse("00000000-0000-0000-0000-000000000001")):N}{(eventId ?? Guid.Parse("00000000-0000-0000-0000-000000000001")):N}",
            OccurredAt,
            "personal",
            MemoryEventV1.ObservedContentEventType,
            new MemoryObservationV1(
                contentDigest ?? new string('0', 64),
                true,
                signals ?? [new InterestSignalV1("coffee", 0.80m)]));
    }
}

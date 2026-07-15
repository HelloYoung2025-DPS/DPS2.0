using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry.Contracts;
using Npgsql;
using Xunit;

namespace Dps.InterestReducer.Tests;

public sealed class PostgresInterestReducerIntegrationTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PersistedLedgerEventsReduceDeterministicallyWithoutCrossSoulLeakage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await IntegrationDatabase.CreateAsync(cancellationToken);
        var ledger = database.Ledger;
        var soulA = CreateSoul('a', "a");
        var soulB = CreateSoul('b', "b");
        var eventA1 = CreateEvent(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            soulA,
            AsOf.AddHours(-1),
            "gardening",
            0.8m);
        var eventA2 = CreateEvent(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            soulA,
            AsOf,
            "books",
            0.4m);
        var eventB = CreateEvent(
            Guid.Parse("20000000-0000-0000-0000-000000000003"),
            soulB,
            AsOf,
            "other-soul-private-topic",
            1m);

        Assert.Equal(AppendDisposition.Inserted, (await ledger.AppendAsync(soulA, eventA1, cancellationToken)).Disposition);
        Assert.Equal(AppendDisposition.DuplicateNoOp, (await ledger.AppendAsync(soulA, eventA1, cancellationToken)).Disposition);
        Assert.Equal(AppendDisposition.Inserted, (await ledger.AppendAsync(soulA, eventA2, cancellationToken)).Disposition);
        Assert.Equal(AppendDisposition.Inserted, (await ledger.AppendAsync(soulB, eventB, cancellationToken)).Disposition);

        var persistedSoulAEvents = await ledger.ReadSoulEventsAsync(soulA.SoulId, cancellationToken);
        var request = new InterestReductionRequest(
            soulA.SoulId,
            soulA.DeviceBindingId,
            soulA.PlatformAccountId,
            "trace_77777777777777777777777777777777",
            "idem_8888888888888888888888888888888888888888888888888888888888888888",
            AsOf,
            persistedSoulAEvents);
        var reducer = new InterestReducer();
        var first = reducer.Reduce(request, new InterestReducerOptions(3_600m));
        var second = reducer.Reduce(request with { Events = persistedSoulAEvents.Reverse().ToArray() }, new InterestReducerOptions(3_600m));

        Assert.Equal(2, first.SourceEventCount);
        Assert.Equal(2, first.Interests.Count);
        Assert.DoesNotContain(first.Interests, item => item.Topic == "other-soul-private-topic");
        Assert.Equal(0.4m, first.Interests.Single(item => item.Topic == "gardening").DecayedConfidence);
        Assert.Equal(
            InterestSnapshotCanonicalizer.Serialize(first),
            InterestSnapshotCanonicalizer.Serialize(second));
        Assert.Equal(2, await ledger.CountEventsAsync(soulA.SoulId, cancellationToken));
        Assert.Equal(1, await ledger.CountEventsAsync(soulB.SoulId, cancellationToken));
    }

    private static SoulResolved CreateSoul(char digestCharacter, string suffix)
    {
        return new SoulResolved(
            $"soul_{new string(digestCharacter, 64)}",
            $"db_{new string(digestCharacter, 32)}",
            $"pa_{new string(digestCharacter, 32)}",
            $"trace_{new string(digestCharacter, 32)}",
            $"idem_{new string(digestCharacter, 64)}",
            AsOf.AddHours(-2),
            "email",
            new string(digestCharacter, 64),
            "test-key-v1");
    }

    private static MemoryEventV1 CreateEvent(
        Guid eventId,
        SoulResolved soul,
        DateTimeOffset occurredAt,
        string topic,
        decimal confidence)
    {
        return new MemoryEventV1(
            MemoryEventV1.CurrentSchemaVersion,
            MemoryEventV1.CurrentContractId,
            MemoryEventV1.CurrentProducerModule,
            eventId,
            soul.SoulId,
            soul.DeviceBindingId,
            soul.PlatformAccountId,
            soul.TraceId,
            $"idem_{eventId:N}{eventId:N}",
            occurredAt,
            "personal",
            MemoryEventV1.ObservedContentEventType,
            new MemoryObservationV1(
                new string(eventId.ToString("N")[0], 64),
                true,
                [new InterestSignalV1(topic, confidence)]));
    }

    private sealed class IntegrationDatabase : IAsyncDisposable
    {
        private IntegrationDatabase(string connectionString, string schemaName)
        {
            ConnectionString = connectionString;
            SchemaName = schemaName;
            Ledger = new PostgresMemoryEventLedger(new MemoryEventLedgerOptions(connectionString, schemaName));
        }

        public string ConnectionString { get; }
        public string SchemaName { get; }
        public PostgresMemoryEventLedger Ledger { get; }

        public static async Task<IntegrationDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var connectionString = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES is required. The required PostgreSQL integration test fails rather than skipping.");
            }

            var database = new IntegrationDatabase(
                connectionString,
                $"dps_f2_interest_{Guid.NewGuid():N}");
            await database.Ledger.InitializeAsync(cancellationToken);
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {SchemaName} CASCADE", connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

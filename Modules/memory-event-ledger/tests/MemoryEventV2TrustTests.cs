using System.Security.Cryptography;
using System.Text.Json;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry.Contracts;
using Xunit;

namespace Dps.MemoryEventLedger.Tests;

public sealed class MemoryEventV2TrustTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FixedAuthoritiesPrepareExactCanonicalEvent()
    {
        var (ledger, _, _, _, signer) = V2TestData.Ledger();
        using (ledger) using (signer)
        {
            var request = V2TestData.Request(); var signals = V2TestData.Signals();
            var prepared = await ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals), TestContext.Current.CancellationToken);
            prepared.Event.Validate();
            var raw = System.Text.Encoding.UTF8.GetBytes(MemoryEventCanonicalizerV2.Serialize(prepared.Event));
            var parsed = MemoryEventCanonicalizerV2.DeserializeExact(raw);
            Assert.Equal(MemoryEventCanonicalizerV2.Serialize(prepared.Event), MemoryEventCanonicalizerV2.Serialize(parsed));
            Assert.Throws<UnauthorizedAccessException>(() => (prepared.Event with { EventId = Guid.NewGuid() }).Validate());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PublicV1BooleanAndSoulDtoCannotAppend()
    {
        var legacy = new PostgresMemoryEventLedger(new MemoryEventLedgerOptions("Host=unused", "memory_v1"));
        var soul = TestData.Soul('a', "forged");
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => legacy.AppendAsync(soul, TestData.Event(soul: soul), TestContext.Current.CancellationToken));
        Assert.Contains("quarantine-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task ForgedSoulRawBytesOrCrossScopeFailsClosed()
    {
        var (ledger, source, _, _, signer) = V2TestData.Ledger();
        using (ledger) using (signer)
        {
            var request = V2TestData.Request(); var signals = V2TestData.Signals();
            source.Mutate = current =>
            {
                var soul = new SoulResolved(current.SoulId, current.DeviceBindingId, current.PlatformAccountId, current.TraceId,
                    "idem_" + new string('9', 64), V2TestData.Now.AddMinutes(-1), "email", new string('a', 64), "soul-test-key");
                return new SoulResolutionAuthoritySnapshotV2(soul, [1, 2, 3], 7,
                    IdentityAuthorityAuditV2.CurrentIssuer, IdentityAuthorityAuditV2.CurrentAudience, IdentityAuthorityAuditV2.CurrentKeyRole,
                    "soul-test-key", 4, 3, V2TestData.Now.AddMinutes(-1), V2TestData.Now.AddMinutes(4), true);
            };
            await Assert.ThrowsAsync<MemoryCapabilityException>(() =>
                ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals), TestContext.Current.CancellationToken));

            source.Mutate = null;
            var other = V2TestData.Request('b', request.EventId);
            await Assert.ThrowsAsync<MemoryCapabilityException>(() =>
                ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals, V2TestData.SignedReceipt(signer, other, signals, other.OccurredAt)), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task RawSwapWrongKeyUnknownOutcomeAndActedCodeFailClosed()
    {
        var (ledger, _, _, _, signer) = V2TestData.Ledger();
        using (ledger) using (signer)
        {
            var request = V2TestData.Request(); var signals = V2TestData.Signals();
            var raw = V2TestData.SignedReceipt(signer, request, signals);
            raw[^2] = raw[^2] == (byte)'A' ? (byte)'B' : (byte)'A';
            await Assert.ThrowsAnyAsync<Exception>(() => ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals, raw), TestContext.Current.CancellationToken));

            using var wrongSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await Assert.ThrowsAsync<MemoryCapabilityException>(() =>
                ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals, V2TestData.SignedReceipt(wrongSigner, request, signals, request.OccurredAt)), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<MemoryCapabilityException>(() =>
                ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals, V2TestData.SignedReceipt(signer, request, signals, request.OccurredAt, outcome: "UNKNOWN_OUTCOME", nativeVerified: false, postconditionVerified: false, nativeEvidence: null, postconditionEvidence: null)), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<MemoryCapabilityException>(() =>
                ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals, V2TestData.SignedReceipt(signer, request, signals, request.OccurredAt, resultCode: "ACTED_VERIFIED")), TestContext.Current.CancellationToken));

            var replayEvent = request with { EventId = Guid.Parse("10000000-0000-0000-0000-000000000099") };
            await Assert.ThrowsAsync<MemoryCapabilityException>(() =>
                ledger.PrepareAsync(V2TestData.AppendRequest(
                    signer, replayEvent, signals, V2TestData.SignedReceipt(signer, request, signals, request.OccurredAt)), TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(-6)]
    [Trait("Category", "Contract")]
    public async Task EqualExpiryAndStaleReceiptFailClosed(int minutes)
    {
        var (ledger, _, _, _, signer) = V2TestData.Ledger();
        using (ledger) using (signer)
        {
            var request = V2TestData.Request(); var signals = V2TestData.Signals();
            await Assert.ThrowsAsync<MemoryCapabilityException>(() => ledger.PrepareAsync(V2TestData.AppendRequest(
                signer, request, signals, V2TestData.SignedReceipt(signer, request, signals, V2TestData.Now.AddMinutes(minutes))), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task RevokedSoulAndExpiredPreparedCapabilityFailClosed()
    {
        var (ledger, source, resultState, clock, signer) = V2TestData.Ledger();
        using (ledger) using (signer)
        {
            var request = V2TestData.Request(); var signals = V2TestData.Signals();
            source.IsCurrent = false;
            await Assert.ThrowsAsync<MemoryCapabilityException>(() =>
                ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals), TestContext.Current.CancellationToken));
            source.IsCurrent = true;
            var prepared = await ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals), TestContext.Current.CancellationToken);
            resultState.RevocationEpoch++;
            await Assert.ThrowsAsync<MemoryCapabilityException>(() => ledger.AppendAsync(prepared, TestContext.Current.CancellationToken));
            resultState.RevocationEpoch--;
            clock.Set(V2TestData.Now.AddMinutes(4)); source.Now = V2TestData.Now.AddMinutes(4);
            await Assert.ThrowsAsync<MemoryCapabilityException>(() => ledger.AppendAsync(prepared, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task RecomputedOrJsonRoundTrippedPreparedObjectCannotAuthorizeAppend()
    {
        var (ledger, _, _, _, signer) = V2TestData.Ledger();
        using (ledger) using (signer)
        {
            var request = V2TestData.Request(); var signals = V2TestData.Signals();
            var prepared = await ledger.PrepareAsync(V2TestData.AppendRequest(signer, request, signals), TestContext.Current.CancellationToken);
            var recomputed = new PreparedMemoryEventV2(prepared.Event);
            await Assert.ThrowsAsync<MemoryCapabilityException>(() => ledger.AppendAsync(recomputed, TestContext.Current.CancellationToken));
            Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<PreparedMemoryEventV2>(JsonSerializer.Serialize(prepared)));
            Assert.Empty(typeof(VerifiedSoulResolutionCapabilityV2).GetConstructors());
            Assert.Empty(typeof(VerifiedObservationReceiptCapabilityV2).GetConstructors());
            Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<VerifiedSoulResolutionCapabilityV2>("{}"));
            Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<VerifiedObservationReceiptCapabilityV2>("{}"));

            var exposedSignals = Assert.IsType<InterestSignalV2[]>(prepared.Event.Observation.InterestSignals);
            exposedSignals[0] = new InterestSignalV2("tampered", 0.99m, new string('a', 64));
            await Assert.ThrowsAsync<MemoryCapabilityException>(() => ledger.AppendAsync(prepared, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SignalAndJsonBoundsRejectControlDuplicatesCountDepthAndNonFinite()
    {
        Assert.Throws<ArgumentException>(() => new InterestSignalV2("bad\n", 0.5m, new string('a', 64)).Validate());
        var duplicate = new[] { new InterestSignalV2("x", 0.1m, new string('a', 64)), new InterestSignalV2("x", 0.2m, new string('b', 64)) };
        var digest = MemorySignalCanonicalizerV2.ComputeSha256(duplicate);
        Assert.Throws<InvalidOperationException>(() => new MemoryObservationV2(new string('a', 64), digest, new string('b', 64), Guid.NewGuid(), Guid.NewGuid(), duplicate).Validate());
        var excessive = Enumerable.Range(0, 33).Select(index => new InterestSignalV2($"t{index}", 0.5m, new string('a', 64))).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => MemorySignalCanonicalizerV2.ComputeSha256(excessive));
        Assert.ThrowsAny<JsonException>(() => MemoryContractValidationV2.ValidateJsonShape("{\"a\":NaN}"u8, 4, 8));
        Assert.ThrowsAny<JsonException>(() => MemoryContractValidationV2.ValidateJsonShape("{\"a\":1,\"a\":2}"u8, 4, 8));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ProductionCompositionRejectsCallerSelectedRoots()
    {
        var options = new MemoryEventLedgerV2Options("Host=a", "Host=b", "memory_v2", "memory_admin", "memory_runtime", new byte[32]);
        var exception = Assert.Throws<InvalidOperationException>(() => PostgresMemoryEventLedgerV2.CreateProduction(options));
        Assert.Contains("WAITING_EXTERNAL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void MigrationV2DeclaresAtomicAppendAclChainQuarantineAndPrivacyTombstones()
    {
        var assembly = typeof(PostgresMemoryEventLedgerV2).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), value => value.EndsWith("002_create_memory_event_ledger_v2.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        Assert.Contains("SECURITY DEFINER", sql, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", sql, StringComparison.Ordinal);
        Assert.Contains("runtime_authority_v2", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON ALL TABLES", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE OR TRUNCATE", sql, StringComparison.Ordinal);
        Assert.Contains("memory_events_v2", sql, StringComparison.Ordinal);
        Assert.Contains("outbox_v2", sql, StringComparison.Ordinal);
        Assert.Contains("quarantine_v2", sql, StringComparison.Ordinal);
        Assert.Contains("privacy_tombstones_v2", sql, StringComparison.Ordinal);
        Assert.Contains("correction_links_v2", sql, StringComparison.Ordinal);
        Assert.Contains("identity_resolution_sha256", sql, StringComparison.Ordinal);
        Assert.Contains("signed_receipt_sha256", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (event_id = command_id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (target_event_id, soul_id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (replacement_event_id, soul_id)", sql, StringComparison.Ordinal);
        Assert.Contains("identity_key_role", sql, StringComparison.Ordinal);
        Assert.Contains("result_key_role", sql, StringComparison.Ordinal);
    }
}

using System.Security.Cryptography;
using System.Text;
using Dps.GBrainProjector.Contracts;
using Dps.SoulMemoryAdapter.Contracts;
using Xunit;

namespace Dps.SoulMemoryAdapter.Tests;

public sealed class GBrainCompanyLoopbackIntegrationTests
{
    private const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const long FixtureNonce = 0;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset BindingTime = Now.AddMinutes(-10);

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task ExactWriteReadbackAndDuplicateDeliveryUseOneMutation()
    {
        var projection = CreateProjection(SoulA, 'a');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        using var client = CreateClient(simulator, scope);

        var first = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);
        var duplicate = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);

        Assert.Equal(SoulMemoryReadbackV1.VerifiedStatus, first.Status);
        Assert.Equal(projection.ProjectionChecksum, first.ReadbackChecksum);
        Assert.Equal(first, duplicate);
        Assert.Equal(1, simulator.PutCalls);
        Assert.True(simulator.ToolsListCalls >= 4);
        Assert.True(simulator.RequestsWithPreferredHeader > 0);
        Assert.True(simulator.RequestsWithNegotiatedHeader > 0);
        Assert.True(simulator.RequestsWithSessionHeader > 0);
        Assert.Equal(2, simulator.ChallengeCalls);
        Assert.Equal(2, simulator.ProtectedMetadataCalls);
        Assert.True(simulator.TokenAudienceAccepted);
        Assert.Equal(2, simulator.InitializedNotificationCalls);
        Assert.Equal(2, simulator.SessionCloseCalls);
        Assert.StartsWith("dps-", first.SourceId, StringComparison.Ordinal);
        Assert.False(first.SourceId.StartsWith("gs_", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task CommitThenDisconnectReconcilesByExactReadWithoutRetry()
    {
        var projection = CreateProjection(SoulA, 'b');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            DropAfterNextPutCommit = true
        };
        using var client = CreateClient(simulator, scope);

        var result = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);

        Assert.Equal(SoulMemoryReadbackV1.VerifiedStatus, result.Status);
        Assert.Equal(1, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task MalformedMutationResponseReconcilesByExactReadWithoutRetry()
    {
        var projection = CreateProjection(SoulA, '0');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            MalformedNextPutResult = true
        };
        using var client = CreateClient(simulator, scope);

        var result = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);

        Assert.Equal(SoulMemoryReadbackV1.VerifiedStatus, result.Status);
        Assert.Equal(1, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task CancellationAfterMutationCommitRemainsUnknownAndIsNeverRetried()
    {
        var projection = CreateProjection(SoulA, '1');
        var scope = ScopeFor(projection);
        using var cancellation = new CancellationTokenSource();
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            CancelAfterNextPutCommit = cancellation
        };
        using var client = CreateClient(simulator, scope);

        await Assert.ThrowsAsync<GBrainUnknownOutcomeException>(() =>
            client.WriteProjectionAsync(projection, cancellation.Token));
        Assert.Equal(1, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task ExactReconciliationReadFailureRemainsUnknownAndIsNeverRetried()
    {
        var projection = CreateProjection(SoulA, '2');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            DropNextProjectionReadAfterPut = true
        };
        using var client = CreateClient(simulator, scope);

        await Assert.ThrowsAsync<GBrainUnknownOutcomeException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(1, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task CapabilityDriftStopsBeforeAnyMutation()
    {
        var projection = CreateProjection(SoulA, 'c');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            DriftGetPageCapability = true
        };
        using var client = CreateClient(simulator, scope);

        await Assert.ThrowsAsync<GBrainCapabilityException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(0, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task MissingInitializeToolsCapabilityStopsBeforeAnyMutation()
    {
        var projection = CreateProjection(SoulA, '4');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            OmitInitializeToolsCapability = true
        };
        using var client = CreateClient(simulator, scope);

        await Assert.ThrowsAsync<GBrainProtocolException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(0, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task CrossSourceReadbackNeverBecomesVerified()
    {
        var projection = CreateProjection(SoulA, 'd');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            ProjectionReadSourceOverride = GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce)
        };
        using var client = CreateClient(simulator, scope);

        await Assert.ThrowsAsync<GBrainUnknownOutcomeException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(1, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task ActiveOnlyReadsRejectSoftDeletedProjectionAndBindingPages()
    {
        var projection = CreateProjection(SoulA, '5');
        var scope = ScopeFor(projection);
        await using (var simulator = new GBrainLoopbackSimulator(scope, Now))
        {
            using var client = CreateClient(simulator, scope);
            _ = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);
            simulator.MarkPageSoftDeleted(GBrainCompanyProtocolV1.ProjectionCurrentSlug);
            simulator.IgnoreDeletedFilter = true;

            await Assert.ThrowsAsync<GBrainReadbackMismatchException>(() =>
                client.ReadProjectionExactAsync(projection, TestContext.Current.CancellationToken));
        }

        await using (var simulator = new GBrainLoopbackSimulator(scope, Now))
        {
            simulator.MarkPageSoftDeleted(GBrainCompanyProtocolV1.SourceBindingSlug);
            simulator.IgnoreDeletedFilter = true;
            using var client = CreateClient(simulator, scope);

            await Assert.ThrowsAsync<GBrainReadbackMismatchException>(() =>
                client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
            Assert.Equal(0, simulator.PutCalls);
        }
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task DurableIdempotencyBindingRejectsSameKeyWithDifferentProjection()
    {
        var first = CreateProjection(SoulA, '6', Now.AddMinutes(-3), idempotencyHex: '6');
        var replacement = CreateProjection(SoulA, '7', Now.AddMinutes(-2), idempotencyHex: '7');
        var conflict = CreateProjection(SoulA, '8', Now.AddMinutes(-1), idempotencyHex: '6');
        var scope = ScopeFor(first);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        using var client = CreateClient(simulator, scope);
        _ = await client.WriteProjectionAsync(first, TestContext.Current.CancellationToken);
        _ = await client.WriteProjectionAsync(replacement, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.WriteProjectionAsync(conflict, TestContext.Current.CancellationToken));
        Assert.Equal(2, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task ConcurrentNewAndOldProjectionCannotLeaveTheSoulRegressed()
    {
        var older = CreateProjection(SoulA, '8', Now.AddMinutes(-2));
        var newer = CreateProjection(SoulA, '9', Now.AddMinutes(-1));
        var scope = ScopeFor(newer);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        var ledger = new FixedGBrainProjectionMutationJournal();
        var leases = new FixedGBrainSoulMutationLeaseProvider();
        using var client = CreateClient(simulator, scope, ledger, leases);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var newerTask = Task.Run(async () =>
        {
            await start.Task;
            return await client.WriteProjectionAsync(newer);
        });
        var olderTask = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                _ = await client.WriteProjectionAsync(older);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        start.SetResult(true);

        var newestReceipt = await newerTask;
        var olderFailure = await olderTask;
        var finalReceipt = await client.ReadProjectionExactAsync(newer, TestContext.Current.CancellationToken);

        Assert.Equal(newer.ProjectionChecksum, newestReceipt.ReadbackChecksum);
        Assert.Equal(newer.ProjectionChecksum, finalReceipt.ReadbackChecksum);
        Assert.True(olderFailure is null or InvalidOperationException);
        Assert.InRange(simulator.PutCalls, 1, 2);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task SoftDeleteAndRebuildReportOnlyWhatExactReadbackProves()
    {
        var projection = CreateProjection(SoulA, 'e');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        using var client = CreateClient(simulator, scope);
        _ = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);

        var deleteIntent = CreateDeleteIntent(projection, 'd', Now.AddSeconds(-30));
        var rebuildIntent = CreateRebuildIntent('f', Now);
        var deleted = await client.SoftDeleteProjectionAsync(deleteIntent, TestContext.Current.CancellationToken);
        var duplicateDelete = await client.SoftDeleteProjectionAsync(deleteIntent, TestContext.Current.CancellationToken);
        var rebuilt = await client.RebuildProjectionAsync(projection, rebuildIntent, TestContext.Current.CancellationToken);

        Assert.Equal(GBrainProjectionDeleteOutcome.SoftDeletedStatus, deleted.Status);
        Assert.True(deleted.ExactSoftDeleteReadback);
        Assert.False(deleted.HardErasureConfirmed);
        Assert.False(deleted.ChunksAndEmbeddingsErased);
        Assert.False(deleted.CachesErased);
        Assert.False(deleted.BackupsErased);
        Assert.Equal(GBrainProjectionDeleteOutcome.AlreadySoftDeletedStatus, duplicateDelete.Status);
        Assert.Equal(GBrainProjectionRebuildOutcome.RebuiltStatus, rebuilt.Status);
        Assert.True(rebuilt.RestoredFromSoftDelete);
        Assert.Equal(SoulMemoryReadbackV1.VerifiedStatus, rebuilt.VerifiedReadback.Status);
        Assert.Equal(1, simulator.DeleteCalls);
        Assert.Equal(1, simulator.RestoreCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task OldDeleteReplayAfterNewerRebuildCannotDeleteCurrentProjection()
    {
        var original = CreateProjection(SoulA, '1', Now.AddMinutes(-5));
        var newer = CreateProjection(SoulA, '2', Now.AddMinutes(-2));
        var scope = ScopeFor(original);
        var deleteIntent = CreateDeleteIntent(original, '3', Now.AddMinutes(-4));
        var rebuildIntent = CreateRebuildIntent('4', Now.AddMinutes(-1));
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        using var client = CreateClient(simulator, scope);
        _ = await client.WriteProjectionAsync(original, TestContext.Current.CancellationToken);
        _ = await client.SoftDeleteProjectionAsync(deleteIntent, TestContext.Current.CancellationToken);
        _ = await client.RebuildProjectionAsync(newer, rebuildIntent, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SoftDeleteProjectionAsync(deleteIntent, TestContext.Current.CancellationToken));
        var current = await client.ReadProjectionExactAsync(newer, TestContext.Current.CancellationToken);

        Assert.Equal(newer.ProjectionChecksum, current.ReadbackChecksum);
        Assert.Equal(1, simulator.DeleteCalls);
        Assert.Equal(1, simulator.RestoreCalls);
        Assert.Equal(2, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task UnknownWriteQuarantinesSoulUntilExactReplayReconcilesIt()
    {
        var older = CreateProjection(SoulA, '3', Now.AddMinutes(-3));
        var newer = CreateProjection(SoulA, '4', Now.AddMinutes(-1));
        var scope = ScopeFor(older);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            DropNextProjectionReadAfterPut = true
        };
        var journal = new FixedGBrainProjectionMutationJournal();
        var leases = new FixedGBrainSoulMutationLeaseProvider();
        using var client = CreateClient(simulator, scope, journal, leases);

        await Assert.ThrowsAsync<GBrainUnknownOutcomeException>(() =>
            client.WriteProjectionAsync(older, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GBrainMutationQuarantinedException>(() =>
            client.WriteProjectionAsync(newer, TestContext.Current.CancellationToken));
        Assert.Equal(1, simulator.PutCalls);

        var reconciled = await client.WriteProjectionAsync(older, TestContext.Current.CancellationToken);
        var advanced = await client.WriteProjectionAsync(newer, TestContext.Current.CancellationToken);

        Assert.Equal(older.ProjectionChecksum, reconciled.ReadbackChecksum);
        Assert.Equal(newer.ProjectionChecksum, advanced.ReadbackChecksum);
        Assert.Equal(2, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task LostMutationLeaseLeavesDispatchedWriteQuarantined()
    {
        var older = CreateProjection(SoulA, '5', Now.AddMinutes(-3));
        var newer = CreateProjection(SoulA, '6', Now.AddMinutes(-1));
        var scope = ScopeFor(older);
        var journal = new FixedGBrainProjectionMutationJournal();
        var leases = new FixedGBrainSoulMutationLeaseProvider();
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            AfterNextPutCommit = () => leases.LoseActiveLease(scope.SourceId)
        };
        using var client = CreateClient(simulator, scope, journal, leases);

        await Assert.ThrowsAsync<GBrainUnknownOutcomeException>(() =>
            client.WriteProjectionAsync(older, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GBrainMutationQuarantinedException>(() =>
            client.WriteProjectionAsync(newer, TestContext.Current.CancellationToken));

        Assert.Equal(1, simulator.PutCalls);
        var reconciled = await client.WriteProjectionAsync(older, TestContext.Current.CancellationToken);
        Assert.Equal(older.ProjectionChecksum, reconciled.ReadbackChecksum);
        Assert.Equal(1, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task PersonaCurrentUsesFixedExactGetAndNeverSemanticSearch()
    {
        var projection = CreateProjection(SoulA, 'f');
        var scope = ScopeFor(projection);
        var payload = "{\"display_name\":\"DPS fixture\",\"traits\":[\"curious\"]}";
        var persona = new GBrainPersonaCurrentDocument(
            GBrainCompanyProtocolV1.WireSchemaVersion,
            GBrainCompanyProtocolV1.PersonaCurrentContractId,
            scope.SourceId,
            scope.SoulId,
            scope.DeviceBindingId,
            scope.PlatformAccountId,
            scope.SourceBindingNonce,
            new string('9', 64),
            payload,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            Now.AddMinutes(-1),
            GBrainCompanyProtocolV1.DpsProvenance);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        simulator.SeedPersona(persona);
        using var client = CreateClient(simulator, scope);

        var result = await client.ReadPersonaCurrentExactAsync(
            scope,
            Now.AddMinutes(-2),
            TestContext.Current.CancellationToken);

        Assert.Equal(persona, result);
        Assert.Equal(1, simulator.PersonaGetCalls);
        Assert.Equal(0, simulator.SearchCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task SearchRevalidatesEveryCandidateByExactPageRead()
    {
        var projection = CreateProjection(SoulA, '8');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        using var client = CreateClient(simulator, scope);
        _ = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);

        var result = await client.SearchProjectionAsync(
            scope,
            "fixture interest",
            Now.AddMinutes(-2),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal(projection.ProjectionChecksum, result[0].Projection.ProjectionChecksum);

        simulator.TamperProjectionBodyOnRead = true;
        await Assert.ThrowsAsync<GBrainReadbackMismatchException>(() =>
            client.SearchProjectionAsync(
                scope,
                "fixture interest",
                Now.AddMinutes(-2),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task SearchCandidateRequiresAnExplicitExactNativeSource()
    {
        var projection = CreateProjection(SoulA, 'a');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        using var client = CreateClient(simulator, scope);
        _ = await client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken);

        simulator.OmitSearchSource = true;
        await Assert.ThrowsAsync<GBrainAuthorizationException>(() =>
            client.SearchProjectionAsync(
                scope,
                "fixture interest",
                Now.AddMinutes(-2),
                cancellationToken: TestContext.Current.CancellationToken));

        simulator.OmitSearchSource = false;
        simulator.SearchSourceOverride = GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce);
        await Assert.ThrowsAsync<GBrainAuthorizationException>(() =>
            client.SearchProjectionAsync(
                scope,
                "fixture interest",
                Now.AddMinutes(-2),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task TamperedBindingPageNonceIsRejectedBeforeAnyMutation()
    {
        var projection = CreateProjection(SoulA, 'b');
        var scope = ScopeFor(projection);
        var genuine = GBrainCompanyProvisioning.CreateSourceBindingMarkdown(scope);
        // The binding page is the only accepted immutable Source/Soul binding record;
        // it must carry the nonce witness, because a binding record without it cannot
        // pin the (soul, nonce) -> source_id pairing.
        Assert.Contains("\"source_binding_nonce\":0", genuine, StringComparison.Ordinal);
        var tampered = genuine.Replace(
            "\"source_binding_nonce\":0",
            "\"source_binding_nonce\":1",
            StringComparison.Ordinal);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now);
        simulator.SeedRawPage(GBrainCompanyProtocolV1.SourceBindingSlug, tampered);
        using var client = CreateClient(simulator, scope);

        await Assert.ThrowsAsync<GBrainAuthorizationException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(0, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task RedirectsAreNotFollowedAndCredentialsAreNotReplayed()
    {
        var projection = CreateProjection(SoulA, '7');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            RedirectDiscovery = true
        };
        using var client = CreateClient(simulator, scope);

        var exception = await Assert.ThrowsAsync<GBrainProtocolException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("loopback-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, simulator.TokenCalls);
        Assert.Equal(0, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task OversizedProtocolResponseFailsAtTheByteLimit()
    {
        var projection = CreateProjection(SoulA, '6');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            OversizeInitialize = true
        };
        var options = GBrainCompanyOptions.CreateLoopbackSimulation(
            simulator.Issuer,
            simulator.McpEndpoint,
            maximumResponseBytes: 4_096);
        using var client = new GBrainCompanySoulMemoryClient(
            options,
            new FixedGBrainCredentialSource(scope),
            new FixedGBrainProjectionMutationJournal(),
            new FixedGBrainSoulMutationLeaseProvider(),
            simulator.CreateHttpClient(),
            new FixedTimeProvider(Now),
            ownsHttpClient: true);

        await Assert.ThrowsAsync<GBrainProtocolException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(0, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task NonBooleanToolErrorFlagFailsClosedBeforeMutation()
    {
        var projection = CreateProjection(SoulA, '3');
        var scope = ScopeFor(projection);
        await using var simulator = new GBrainLoopbackSimulator(scope, Now)
        {
            MalformedWhoAmIErrorFlag = true
        };
        using var client = CreateClient(simulator, scope);

        await Assert.ThrowsAsync<GBrainProtocolException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(0, simulator.PutCalls);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public async Task CredentialScopeMismatchFailsBeforeAnyNetworkRequest()
    {
        var projection = CreateProjection(SoulA, '5');
        var requested = ScopeFor(projection);
        var otherProjection = CreateProjection(SoulB, '4');
        var other = ScopeFor(otherProjection);
        await using var simulator = new GBrainLoopbackSimulator(requested, Now);
        var options = GBrainCompanyOptions.CreateLoopbackSimulation(
            simulator.Issuer,
            simulator.McpEndpoint);
        using var client = new GBrainCompanySoulMemoryClient(
            options,
            new FixedGBrainCredentialSource(other),
            new FixedGBrainProjectionMutationJournal(),
            new FixedGBrainSoulMutationLeaseProvider(),
            simulator.CreateHttpClient(),
            new FixedTimeProvider(Now),
            ownsHttpClient: true);

        await Assert.ThrowsAsync<GBrainAuthorizationException>(() =>
            client.WriteProjectionAsync(projection, TestContext.Current.CancellationToken));
        Assert.Equal(0, simulator.RequestCount);
    }

    [Fact]
    [Trait("Category", "SecuritySimulation")]
    public void ProductionConfigurationRejectsPlaintextPathAndAuthorityDrift()
    {
        Assert.Throws<GBrainConfigurationException>(() =>
            GBrainCompanyOptions.CreateProduction(
                new Uri("http://company.example/"),
                new Uri("http://company.example/mcp"),
                GBrainCompanyProtocolV1.TestedServerVersion));
        Assert.Throws<GBrainConfigurationException>(() =>
            GBrainCompanyOptions.CreateProduction(
                new Uri("https://company.example/oauth"),
                new Uri("https://company.example/mcp"),
                GBrainCompanyProtocolV1.TestedServerVersion));
        Assert.Throws<GBrainConfigurationException>(() =>
            GBrainCompanyOptions.CreateProduction(
                new Uri("https://company.example/"),
                new Uri("https://other.example/mcp"),
                GBrainCompanyProtocolV1.TestedServerVersion));
    }

    private static GBrainCompanySoulMemoryClient CreateClient(
        GBrainLoopbackSimulator simulator,
        GBrainSoulScope scope,
        IGBrainProjectionMutationJournal? mutationJournal = null,
        IGBrainSoulMutationLeaseProvider? mutationLeaseProvider = null)
    {
        var options = GBrainCompanyOptions.CreateLoopbackSimulation(
            simulator.Issuer,
            simulator.McpEndpoint);
        return new GBrainCompanySoulMemoryClient(
            options,
            new FixedGBrainCredentialSource(scope),
            mutationJournal ?? new FixedGBrainProjectionMutationJournal(),
            mutationLeaseProvider ?? new FixedGBrainSoulMutationLeaseProvider(),
            simulator.CreateHttpClient(),
            new FixedTimeProvider(Now),
            ownsHttpClient: true);
    }

    private static GBrainSoulScope ScopeFor(GBrainProjectionV2 projection) =>
        new(
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            projection.SourceBindingNonce);

    private static GBrainProjectionDeleteIntent CreateDeleteIntent(
        GBrainProjectionV2 projection,
        char idempotencyHex,
        DateTimeOffset occurredAt) =>
        new(
            GBrainProjectionDeleteIntent.CurrentSchemaVersion,
            GBrainProjectionDeleteIntent.CurrentContractId,
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            "trace_abcdef1234567890abcdef1234567890",
            "idem_" + new string(idempotencyHex, 64),
            occurredAt,
            projection.ProjectionRevision,
            projection.ProjectionChecksum,
            projection.SourceBindingNonce);

    private static GBrainProjectionRebuildIntent CreateRebuildIntent(
        char idempotencyHex,
        DateTimeOffset occurredAt) =>
        new(
            GBrainProjectionRebuildIntent.CurrentSchemaVersion,
            GBrainProjectionRebuildIntent.CurrentContractId,
            "trace_fedcba0987654321fedcba0987654321",
            "idem_" + new string(idempotencyHex, 64),
            occurredAt);

    private static GBrainProjectionV2 CreateProjection(
        string soulId,
        char revisionHex,
        DateTimeOffset? occurredAt = null,
        char? idempotencyHex = null)
    {
        var binding = GBrainSourceBindingV1.Create(
            soulId,
            GBrainSourceIdCandidates.Compute(soulId, FixtureNonce),
            FixtureNonce,
            BindingTime);
        var withoutChecksum = new GBrainProjectionV2(
            GBrainProjectionV2.CurrentSchemaVersion,
            GBrainProjectionV2.CurrentContractId,
            GBrainProjectionV2.CurrentProducerModule,
            soulId,
            "db_" + new string(soulId[5], 32),
            "pa_" + new string(soulId[5], 32),
            "trace_1234567890abcdef1234567890abcdef",
            "idem_" + new string(idempotencyHex ?? revisionHex, 64),
            occurredAt ?? Now.AddMinutes(-1),
            "personal",
            binding.SourceId,
            binding.Algorithm,
            binding.Nonce,
            binding.SoulHash,
            binding.AllocatedAt,
            binding.BindingRevision,
            binding.BindingChecksum,
            new string(revisionHex, 64),
            new string('0', 64),
            GBrainProjectionV2.RenderedNotWrittenStatus,
            0,
            Array.Empty<ProjectionEventV1>(),
            Array.Empty<ProjectionInterestV1>());
        var projection = withoutChecksum with
        {
            ProjectionChecksum = GBrainProjectionV2Canonicalizer.ComputeSha256(withoutChecksum)
        };
        projection.Validate();
        return projection;
    }
}

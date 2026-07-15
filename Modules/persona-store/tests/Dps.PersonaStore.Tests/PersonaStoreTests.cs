using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Reflection;
using Dps.Binding.Contracts;
using Dps.PersonaStore.Contracts;
using Xunit;

namespace Dps.PersonaStore.Tests;

public sealed class PersonaStoreTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSoul = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BindingId = "db_cccccccccccccccccccccccccccccccc";
    private const string AccountId = "pa_dddddddddddddddddddddddddddddddd";
    private static readonly DateTimeOffset OccurredAt = new(2026, 7, 14, 3, 0, 0, TimeSpan.Zero);
    private static readonly string RequestHmacKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 32).ToArray());
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Unit")]
    public async Task DeterministicRevisionsAreIdempotentEvidenceBackedAndExactReadOnly()
    {
        var store = CreateStore();
        var command = Put(
            expectedRevision: 0,
            idempotencyKey: "persona-put-1",
            traits: new Dictionary<string, string> { ["tone"] = "calm", ["curiosity"] = "high" });

        var first = await store.PutAsync(command, TestCancellation);
        var duplicate = await store.PutAsync(command, TestCancellation);

        Assert.Equal(first, duplicate);
        Assert.Equal(["curiosity", "tone"], first.TraitKeys);
        Assert.Equal("calm", (await store.ExportHistoryV1Async(Export("persona-export-1"), TestCancellation)).Revisions[^1].Traits!["tone"]);
        Assert.Equal(first, await store.GetCurrentAsync(Soul, BindingId, AccountId, TestCancellation));
        await Assert.ThrowsAsync<PersonaIdempotencyConflictException>(async () =>
            await store.PutAsync(command with
            {
                Traits = new Dictionary<string, string> { ["tone"] = "direct" }
            }, TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ReturnedSnapshotsAndExportPayloadsCannotMutateAppendOnlyHistory()
    {
        var mutableTraits = new Dictionary<string, string> { ["tone"] = "calm" };
        var mutableEvidence = new List<string> { new string('a', 64) };
        var store = CreateStore();
        var first = await store.PutAsync(new PutPersonaCommand(
            Soul,
            BindingId,
            AccountId,
            0,
            mutableTraits,
            mutableEvidence,
            Trace("immutable"),
            Idem("immutable"),
            OccurredAt), TestCancellation);

        mutableTraits["tone"] = "direct";
        mutableEvidence[0] = new string('b', 64);
        Assert.False(first.TraitKeys is string[]);
        Assert.False(first.EvidenceSha256 is string[]);
        var keys = Assert.IsAssignableFrom<IList<string>>(first.TraitKeys);
        var evidence = Assert.IsAssignableFrom<IList<string>>(first.EvidenceSha256);
        Assert.Throws<NotSupportedException>(() => keys[0] = "email");
        Assert.Throws<NotSupportedException>(() => evidence[0] = new string('c', 64));

        var current = await store.GetCurrentAsync(Soul, BindingId, AccountId, TestCancellation);
        var history = Assert.Single(await store.ReadHistoryAsync(Soul, BindingId, AccountId, TestCancellation));
        var export = await store.ExportHistoryV1Async(Export("immutable-export"), TestCancellation);
        Assert.Equal(["tone"], current.TraitKeys);
        Assert.Equal([new string('a', 64)], history.EvidenceSha256);
        var exportedTraits = Assert.IsAssignableFrom<IDictionary<string, string>>(Assert.Single(export.Revisions).Traits!);
        Assert.Throws<NotSupportedException>(() => exportedTraits["tone"] = "direct");
        Assert.Equal("calm", export.Revisions[0].Traits!["tone"]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task CorrectionAndLivePrimaryLogicalDeletionAppendHistoryWithoutSemanticLookup()
    {
        var store = CreateStore();
        var first = await store.PutAsync(Put(0, "persona-first"), TestCancellation);
        var corrected = await store.PutAsync(Put(first.PersonaRevision, "persona-correction") with
        {
            Traits = new Dictionary<string, string> { ["tone"] = "warm" }
        }, TestCancellation);
        var retainedExportRequest = Export("persona-export-retained");
        var retainedHistory = await store.ExportHistoryV1Async(retainedExportRequest, TestCancellation);
        Assert.Equal("persona.history.export/v1", retainedHistory.ContractId);
        Assert.Equal("sensitive", retainedHistory.PrivacyClass);
        Assert.Equal("retained", retainedHistory.LivePrimaryPayloadState);
        Assert.Equal(retainedExportRequest.TraceId, retainedHistory.TraceId);
        Assert.Equal(retainedExportRequest.IdempotencyKey, retainedHistory.IdempotencyKey);
        Assert.Equal(retainedExportRequest.OccurredAt, retainedHistory.OccurredAt);
        Assert.Equal([1L, 2L], retainedHistory.Revisions.Select(static value => value.Revision.PersonaRevision));
        Assert.Equal(["calm", "warm"], retainedHistory.Revisions.Select(static value => value.Traits!["tone"]));
        Assert.All(retainedHistory.Revisions, static value => Assert.Equal("retained", value.LivePrimaryPayloadState));
        var deleted = await store.DeleteAsync(new DeletePersonaCommand(
            Soul,
            BindingId,
            AccountId,
            corrected.PersonaRevision,
            [new string('c', 64)],
            Trace("delete"),
            Idem("persona-delete"),
            OccurredAt.AddMinutes(2)), TestCancellation);

        Assert.Equal("deleted", deleted.Status);
        Assert.Equal([1L, 2L, 3L], (await store.ReadHistoryAsync(Soul, BindingId, AccountId, TestCancellation)).Select(static value => value.PersonaRevision));
        var deletedHistory = await store.ExportHistoryV1Async(Export("persona-export-deleted"), TestCancellation);
        Assert.Equal("live-primary-logically-deleted", deletedHistory.LivePrimaryPayloadState);
        Assert.Equal([1L, 2L, 3L], deletedHistory.Revisions.Select(static value => value.Revision.PersonaRevision));
        Assert.All(deletedHistory.Revisions, static value =>
        {
            Assert.Equal("live-primary-logically-deleted", value.LivePrimaryPayloadState);
            Assert.Null(value.Traits);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.PutAsync(Put(deleted.PersonaRevision, "persona-resurrection"), TestCancellation));
        Assert.DoesNotContain(typeof(IPersonaStore).GetMethods(), method =>
            method.Name.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Semantic", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Embedding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task CrossScopeStaleUnknownAndSensitiveInputsFailClosed()
    {
        var store = CreateStore();
        _ = await store.PutAsync(Put(0, "persona-scope"), TestCancellation);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.GetCurrentAsync(Soul, "db_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", AccountId, TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await store.GetCurrentAsync(OtherSoul, BindingId, AccountId, TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await store.ExportHistoryV1Async(Export("persona-export-other") with { SoulId = OtherSoul }, TestCancellation));
        await Assert.ThrowsAsync<PersonaRevisionConflictException>(async () =>
            await store.PutAsync(Put(0, "persona-stale"), TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PutAsync(Put(1, "persona-unknown") with
            {
                Traits = new Dictionary<string, string> { ["email"] = "person@example.com" }
            }, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PutAsync(Put(1, "persona-credential") with
            {
                Traits = new Dictionary<string, string> { ["tone"] = "api-key-value" }
            }, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PutAsync(Put(1, "invalid-email") with { IdempotencyKey = "person@example.com" }, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PutAsync(Put(1, "invalid-phone") with { IdempotencyKey = "60123456789" }, TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task InvalidOrMismatchedAuthoritativeBindingFenceFailsClosed()
    {
        var released = new BindingFenceClient { State = "released" };
        var inactiveStore = new InMemoryPersonaStore(released, Convert.FromBase64String(RequestHmacKey));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await inactiveStore.PutAsync(Put(0, "persona-inactive"), TestCancellation));

        var wrongScope = new BindingFenceClient { ReturnedSoulId = OtherSoul };
        var wrongScopeStore = new InMemoryPersonaStore(wrongScope, Convert.FromBase64String(RequestHmacKey));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await wrongScopeStore.PutAsync(Put(0, "persona-wrong-scope"), TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public void EqualLowEntropyTraitsUseIndependentNonEnumerableSoulCommitments()
    {
        var traits = new Dictionary<string, string> { ["tone"] = "calm" };
        var first = PersonaMutationCanonicalizer.HashTraits(traits, Enumerable.Repeat((byte)0x11, 32).ToArray());
        var second = PersonaMutationCanonicalizer.HashTraits(traits, Enumerable.Repeat((byte)0x22, 32).ToArray());

        Assert.Matches("^[a-f0-9]{64}$", first);
        Assert.Matches("^[a-f0-9]{64}$", second);
        Assert.NotEqual(first, second);
        Assert.NotEqual(PersonaMutationCanonicalizer.DeletedTraitsSha256, first);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task HistoryExportIdempotencyKeepsTheExactFirstSnapshotAndRejectsScopeOrRequestDrift()
    {
        var store = CreateStore();
        var first = await store.PutAsync(Put(0, "stable-export-seed"), TestCancellation);
        var exportCommand = Export("stable-export");
        var initial = await store.ExportHistoryV1Async(exportCommand, TestCancellation);
        var proofKey = Convert.FromBase64String(RequestHmacKey);
        try
        {
            var normalized = PersonaMutationCanonicalizer.Normalize(exportCommand);
            Assert.Throws<InvalidDataException>(() => PersonaMutationCanonicalizer.VerifyExportProof(
                initial with { ExportRequestHmacSha256 = new string('0', 64) }, normalized, proofKey));
            Assert.Throws<InvalidDataException>(() => PersonaMutationCanonicalizer.VerifyExportProof(
                initial with { SnapshotCursorHmacSha256 = new string('0', 64) }, normalized, proofKey));
            Assert.Throws<InvalidDataException>(() => PersonaMutationCanonicalizer.VerifyExportProof(
                initial with { ExportPayloadSha256 = new string('0', 64) }, normalized, proofKey));
            Assert.Throws<InvalidDataException>(() => PersonaMutationCanonicalizer.VerifyExportProof(
                initial with
                {
                    ExportReceiptHmacSha256 = new string('0', 64),
                    ExportReceiptId = "pexport_" + new string('0', 64)
                }, normalized, proofKey));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(proofKey);
        }
        var corrected = await store.PutAsync(Put(first.PersonaRevision, "stable-export-correction") with
        {
            Traits = new Dictionary<string, string> { ["tone"] = "warm" }
        }, TestCancellation);
        _ = await store.DeleteAsync(new DeletePersonaCommand(
            Soul, BindingId, AccountId, corrected.PersonaRevision, [new string('d', 64)],
            Trace("stable-export-delete"), Idem("stable-export-delete"), OccurredAt.AddMinutes(4)), TestCancellation);

        var replay = await store.ExportHistoryV1Async(exportCommand, TestCancellation);
        Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(replay));
        Assert.Equal(1, replay.SnapshotPersonaRevision);
        Assert.Equal("calm", Assert.Single(replay.Revisions).Traits!["tone"]);
        await Assert.ThrowsAsync<PersonaIdempotencyConflictException>(async () =>
            await store.ExportHistoryV1Async(exportCommand with { TraceId = Trace("changed-export-request") }, TestCancellation));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.ExportHistoryV1Async(Export("wrong-export-device") with
            {
                DeviceBindingId = "db_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
            }, TestCancellation));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.ExportHistoryV1Async(Export("wrong-export-account") with
            {
                PlatformAccountId = "pa_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
            }, TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task HistoryExportRecomputesEveryRetainedTraitCommitmentBeforeRelease()
    {
        var store = CreateStore();
        _ = await store.PutAsync(Put(0, "tampered-export-seed"), TestCancellation);
        var field = typeof(InMemoryPersonaStore).GetField("_historyTraits", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing in-memory retained-trait ledger.");
        var retained = Assert.IsType<Dictionary<string, Dictionary<long, IReadOnlyDictionary<string, string>>>>(field.GetValue(store));
        retained[Soul][1] = PersonaTraitVocabularyV1.ValidateAndFreeze(
            new Dictionary<string, string> { ["tone"] = "warm" });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ExportHistoryV1Async(Export("tampered-export"), TestCancellation));
    }

    [Fact, Trait("Category", "Contract")]
    public async Task ContractUsesCanonicalScopeDigestAndRejectsUnknownMajor()
    {
        var value = await CreateStore().PutAsync(Put(0, "persona-contract"), TestCancellation);
        value.Validate();
        var json = JsonSerializer.Serialize(value);
        Assert.Contains("\"traits_sha256\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("calm", json, StringComparison.Ordinal);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "2.0" }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void PostgreSqlOptionsRequireSeparateRolesAndBoundedTimeoutWithoutRenderingSecrets()
    {
        var migrator = "Host=localhost;Database=dps_test;Username=persona_migrator;Password=migrator-secret";
        var runtime = "Host=localhost;Database=dps_test;Username=persona_runtime;Password=runtime-secret";
        var options = new PostgresPersonaStoreOptions(migrator, runtime, "persona_test", RequestHmacKey, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("secret", options.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(typeof(PostgresPersonaStoreOptions).GetProperty("MigratorConnectionString"));
        Assert.Null(typeof(PostgresPersonaStoreOptions).GetProperty("RuntimeConnectionString"));
        Assert.Throws<ArgumentException>(() => new PostgresPersonaStoreOptions(migrator, migrator, "persona_test", RequestHmacKey));
        Assert.Throws<ArgumentException>(() => new PostgresPersonaStoreOptions(migrator, runtime, "persona_test", "not-base64"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresPersonaStoreOptions(migrator, runtime, "persona_test", RequestHmacKey, TimeSpan.FromSeconds(6)));
        Assert.Empty(typeof(PostgresPersonaStore).GetConstructors());
        var factory = Assert.Single(typeof(PostgresPersonaStore).GetMethods(), static method => method.Name == "CreateTrusted");
        Assert.Equal(typeof(IBindingMutationFenceClient), factory.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(SignedBindingCompositionAttestationV1), factory.GetParameters()[2].ParameterType);
        Assert.Equal(typeof(PersonaBindingCompositionExpectations), factory.GetParameters()[3].ParameterType);
        Assert.DoesNotContain(
            typeof(PostgresPersonaStore).Assembly.GetReferencedAssemblies(),
            static reference => reference.Name == "Dps.Binding");
        Assert.DoesNotContain(
            typeof(PostgresPersonaStore).GetMethods(),
            static method => method.IsPublic && method.Name.StartsWith("Count", StringComparison.Ordinal));
        var publicStores = typeof(PostgresPersonaStore).Assembly.GetExportedTypes()
            .Where(type => typeof(IPersonaStore).IsAssignableFrom(type) && !type.IsInterface)
            .ToArray();
        Assert.Equal([typeof(PostgresPersonaStore)], publicStores);
    }

    [Fact, Trait("Category", "Contract")]
    public void ProductionFactoryRequiresPinnedRootCompositionAndRejectsCallerFenceImplementationsBeforeDatabaseAccess()
    {
        var attestation = GoldenCompositionAttestation();
        PersonaBindingCompositionVerifier.VerifyPinnedRootSignature(attestation);
        Assert.Throws<UnauthorizedAccessException>(() =>
            PersonaBindingCompositionVerifier.VerifyPinnedRootSignature(attestation with { ReleaseBomSha256 = new string('9', 64) }));

        var migrator = "Host=localhost;Database=dps_test;Username=persona_migrator;Password=migrator-secret";
        var runtime = "Host=localhost;Database=dps_test;Username=persona_runtime;Password=runtime-secret";
        var options = new PostgresPersonaStoreOptions(migrator, runtime, "persona_test", RequestHmacKey);
        var expectations = new PersonaBindingCompositionExpectations(
            attestation.ReleaseBomSha256,
            attestation.Generation,
            attestation.BindingInstanceConfigurationSha256,
            attestation.BindingInstanceTrustEpoch);
        Assert.Throws<UnauthorizedAccessException>(() =>
            PostgresPersonaStore.CreateTrusted(options, new BindingFenceClient(), attestation, expectations));
    }

    [Fact, Trait("Category", "Contract")]
    public async Task StrictJsonAndFixedOpaqueIdentifiersMatchThePublicSchemaBoundary()
    {
        var value = await CreateStore().PutAsync(Put(0, "strict-json", new Dictionary<string, string>
        {
            ["curiosity"] = "high",
            ["tone"] = "calm"
        }) with { EvidenceSha256 = [new string('a', 64), new string('b', 64)] }, TestCancellation);
        var json = JsonSerializer.Serialize(value);
        var parsed = PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(json);
        Assert.Equal(json, JsonSerializer.Serialize(parsed));

        var unknown = JsonNode.Parse(json)!.AsObject();
        unknown["unexpected"] = true;
        Assert.ThrowsAny<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(unknown.ToJsonString()));

        var missing = JsonNode.Parse(json)!.AsObject();
        Assert.True(missing.Remove("occurred_at"));
        Assert.ThrowsAny<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(missing.ToJsonString()));

        var duplicate = json.Replace("\"status\":\"active\"", "\"status\":\"deleted\",\"status\":\"active\"", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(duplicate));
        Assert.ThrowsAny<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(json + " trailing"));
        Assert.ThrowsAny<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(json.Replace("{", "{/*forbidden*/", StringComparison.Ordinal)));
        Assert.ThrowsAny<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(json[..^1] + ",}"));
        Assert.ThrowsAny<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(json[..^1] + ",\"padding\":\"" + new string('x', 70_000) + "\"}"));

        Assert.Throws<ArgumentException>(() => (value with { DeviceBindingId = "db_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { PlatformAccountId = "pa_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { TraceId = "sk-live-not-a-trace-id" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { IdempotencyKey = "60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { DeviceBindingId = value.DeviceBindingId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { SoulId = value.SoulId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { PlatformAccountId = value.PlatformAccountId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { TraceId = value.TraceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { IdempotencyKey = value.IdempotencyKey + "\n" }).Validate());
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "01.0" }).Validate());
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "1.+0" }).Validate());
        Assert.Throws<JsonException>(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(
            json.Replace("2026-07-14T03:00:00+00:00", "2026-07-14T03:00:00.12345678Z", StringComparison.Ordinal)));

        var unsortedButUnique = value with
        {
            TraitKeys = ["tone", "curiosity"],
            EvidenceSha256 = [new string('b', 64), new string('a', 64)]
        };
        Assert.Throws<ArgumentException>(unsortedButUnique.Validate);
    }

    [Fact, Trait("Category", "Contract")]
    public void ProviderOwnedRawCorpusMakesTheStrictDotNetDecisionForAll27Cases()
    {
        string[] expectedCaseIds =
        [
            "persona.valid.active.minimal",
            "persona.valid.active.utc-seven-fraction-int64-max",
            "persona.valid.deleted.empty-traits",
            "persona.invalid.version.unknown-major",
            "persona.invalid.version.trailing-newline",
            "persona.invalid.occurred-at.nonzero-offset",
            "persona.invalid.occurred-at.before-range",
            "persona.invalid.occurred-at.after-range",
            "persona.invalid.occurred-at.eight-fraction-digits",
            "persona.invalid.revision.int64-overflow",
            "persona.invalid.active.empty-traits",
            "persona.invalid.deleted.nonempty-traits",
            "persona.invalid.traits.reversed",
            "persona.invalid.traits.duplicate",
            "persona.invalid.evidence.reversed",
            "persona.invalid.evidence.duplicate",
            "persona.invalid.evidence.over-64",
            "persona.invalid.soul.trailing-newline",
            "persona.invalid.device-binding.bad-length",
            "persona.invalid.platform-account.bad-hex",
            "persona.invalid.trace.trailing-newline",
            "persona.invalid.idempotency.bad-prefix",
            "persona.invalid.traits-hash.trailing-newline",
            "persona.invalid.evidence-hash.trailing-newline",
            "persona.invalid.unknown-field",
            "persona.invalid.contract-id.case-change",
            "persona.invalid.duplicate-json-property"
        ];
        var corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus", "persona.revision.v1.corpus.json");
        using var corpus = JsonDocument.Parse(File.ReadAllText(corpusPath));
        Assert.Equal("persona.revision/v1", corpus.RootElement.GetProperty("contract_id").GetString());
        Assert.Equal("persona-store", corpus.RootElement.GetProperty("owner_module").GetString());
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(expectedCaseIds.Length, cases.Length);
        Assert.Equal(expectedCaseIds, cases.Select(value => value.GetProperty("id").GetString()).ToArray());

        foreach (var testCase in cases)
        {
            var expectedValid = testCase.GetProperty("valid").GetBoolean();
            var expectedDecision = expectedValid ? "accept" : "reject";
            Assert.Equal(expectedDecision, testCase.GetProperty("expectations").GetProperty("dotnet_strict_codec").GetString());
            var rawJson = testCase.GetProperty("raw_json").GetString()!;
            var exception = Record.Exception(() => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(rawJson));
            Assert.Equal(expectedValid, exception is null);
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void ProviderOwnedHistoryExportCorpusMakesTheStrictDotNetDecisionForAll17Cases()
    {
        string[] expectedCaseIds =
        [
            "persona-history.valid.active.retained",
            "persona-history.valid.deleted.metadata-only",
            "persona-history.invalid.version.unknown-major",
            "persona-history.invalid.retained.missing-traits",
            "persona-history.invalid.retained.unknown-trait",
            "persona-history.invalid.retained.keys-mismatch",
            "persona-history.invalid.revisions.noncontiguous",
            "persona-history.invalid.deleted.no-final-tombstone",
            "persona-history.invalid.deleted.early-tombstone",
            "persona-history.invalid.soul.trailing-newline",
            "persona-history.invalid.nested-occurred-at.eight-fraction",
            "persona-history.invalid.unknown-field",
            "persona-history.invalid.privacy-not-sensitive",
            "persona-history.invalid.duplicate-json-property",
            "persona-history.invalid.nested-soul.scope-mismatch",
            "persona-history.invalid.nested-device.scope-mismatch",
            "persona-history.invalid.nested-account.scope-mismatch"
        ];
        var corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus", "persona.history.export.v1.corpus.json");
        using var corpus = JsonDocument.Parse(File.ReadAllText(corpusPath));
        Assert.Equal(PersonaHistoryExportV1.CurrentContractId, corpus.RootElement.GetProperty("contract_id").GetString());
        Assert.Equal("persona-store", corpus.RootElement.GetProperty("owner_module").GetString());
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(expectedCaseIds, cases.Select(value => value.GetProperty("id").GetString()).ToArray());

        foreach (var testCase in cases)
        {
            var expectedValid = testCase.GetProperty("valid").GetBoolean();
            Assert.Equal(expectedValid ? "accept" : "reject", testCase.GetProperty("expectations").GetProperty("dotnet_strict_codec").GetString());
            var exception = Record.Exception(() => PersonaContractJson.DeserializeStrict<PersonaHistoryExportV1>(testCase.GetProperty("raw_json").GetString()!));
            Assert.True(expectedValid == (exception is null), $"{testCase.GetProperty("id").GetString()}: {exception}");
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void EmbeddedMigrationStaticallyRevokesRawRuntimeMutationAndLimitsSecurityDefinerEntrypoints()
    {
        const string resourceName = "Dps.PersonaStore.Migrations.001_create_persona_store.sql";
        using var stream = typeof(PostgresPersonaStore).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing {resourceName}.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        Assert.Contains("REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON ALL SEQUENCES IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT INSERT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT UPDATE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT DELETE", sql, StringComparison.Ordinal);
        Assert.Equal(6, Regex.Matches(sql, @"(?m)^GRANT EXECUTE ON FUNCTION ").Count);
        Assert.Equal(7, Regex.Matches(sql, @"(?m)^SECURITY DEFINER$").Count);
        Assert.Equal(7, Regex.Matches(sql, @"(?m)^SET search_path = pg_catalog, __SCHEMA__$").Count);
        Assert.Contains("REVOKE SELECT (%I) ON TABLE __SCHEMA__.%I FROM __RUNTIME_ROLE__", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE SCHEMA IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("external_destruction_receipt_sha256 IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("'live-postgresql-primary-only'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("jsonb_object_length", sql, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.jsonb_object_keys", sql, StringComparison.Ordinal);
        Assert.Contains("target_result_document ->> 'soul_id' IS DISTINCT FROM target_soul_id", sql, StringComparison.Ordinal);
        Assert.Contains("result_json ->> 'soul_id' IS NOT DISTINCT FROM soul_id", sql, StringComparison.Ordinal);
        Assert.Contains("persona_request_hmac_key_attestations", sql, StringComparison.Ordinal);
        Assert.Contains("item.document -> 'revision' IS DISTINCT FROM revision.result_json", sql, StringComparison.Ordinal);
        Assert.Contains("expected_payload_sha256 := encode(sha256(convert_to(canonical_payload_json, 'UTF8')), 'hex')", sql, StringComparison.Ordinal);
        Assert.Contains("result_wire_bytes_value := octet_length(convert_to(canonical_result_json, 'UTF8'))", sql, StringComparison.Ordinal);
        Assert.Contains("expected_receipt_hmac_sha256", sql, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract")]
    public async Task HistoryExportEnforcesExactRevisionAndCompleteWireBoundaries()
    {
        var store = CreateStore();
        _ = await store.PutAsync(Put(0, "boundary-export-seed"), TestCancellation);
        var command = Export("boundary-export");
        var prototype = await store.ExportHistoryV1Async(command, TestCancellation);
        var revisions = Enumerable.Range(1, 10_000).Select(index =>
            prototype.Revisions[0] with
            {
                Revision = prototype.Revisions[0].Revision with { PersonaRevision = index }
            }).ToArray();
        var key = Convert.FromBase64String(RequestHmacKey);
        try
        {
            var exact = PersonaMutationCanonicalizer.CreateHistoryExport(
                PersonaMutationCanonicalizer.Normalize(command),
                PersonaHistoryExportItemV1.Retained,
                Array.AsReadOnly(revisions),
                key);
            Assert.Equal(10_000, exact.Revisions.Count);
            var tooMany = revisions.Append(revisions[^1] with
            {
                Revision = revisions[^1].Revision with { PersonaRevision = 10_001 }
            }).ToArray();
            Assert.Throws<ArgumentException>(() => PersonaMutationCanonicalizer.CreateHistoryExport(
                PersonaMutationCanonicalizer.Normalize(command with { IdempotencyKey = Idem("boundary-export-over") }),
                PersonaHistoryExportItemV1.Retained,
                Array.AsReadOnly(tooMany),
                key));

            var maximumEvidence = Array.AsReadOnly(Enumerable.Range(0, 64)
                .Select(index => Digest("boundary-evidence:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Order(StringComparer.Ordinal)
                .ToArray());
            var oversizedWire = revisions.Select(item => item with
            {
                Revision = item.Revision with { EvidenceSha256 = maximumEvidence }
            }).ToArray();
            Assert.Throws<ArgumentException>(() => PersonaMutationCanonicalizer.CreateHistoryExport(
                PersonaMutationCanonicalizer.Normalize(command with { IdempotencyKey = Idem("boundary-export-wire-over") }),
                PersonaHistoryExportItemV1.Retained,
                Array.AsReadOnly(oversizedWire),
                key));

            var sizingItems = revisions.Select(item => item with
            {
                Revision = item.Revision with { EvidenceSha256 = Array.AsReadOnly(new[] { maximumEvidence[0] }) }
            }).ToArray();
            var sizingExport = prototype with
            {
                SnapshotPersonaRevision = 10_000,
                Revisions = Array.AsReadOnly(sizingItems)
            };
            var targetWireBytes = 16 * 1024 * 1024;
            var remainingBytes = targetWireBytes - JsonSerializer.SerializeToUtf8Bytes(sizingExport).Length;
            Assert.True(remainingBytes > 0);
            var evidenceAdditions = remainingBytes / 67;
            var fractionalTimestampBytes = remainingBytes % 67;
            if (fractionalTimestampBytes == 1)
            {
                evidenceAdditions--;
                fractionalTimestampBytes += 67;
            }
            Assert.InRange(evidenceAdditions, 0, sizingItems.Length * 63);
            var additionsLeft = evidenceAdditions;
            for (var index = 0; index < sizingItems.Length && additionsLeft > 0; index++)
            {
                var addHere = Math.Min(63, additionsLeft);
                sizingItems[index] = sizingItems[index] with
                {
                    Revision = sizingItems[index].Revision with
                    {
                        EvidenceSha256 = Array.AsReadOnly(maximumEvidence.Take(addHere + 1).ToArray())
                    }
                };
                additionsLeft -= addHere;
            }
            Assert.Equal(0, additionsLeft);

            var timestampIndex = 0;
            while (fractionalTimestampBytes > 0)
            {
                var serializedFractionBytes = Math.Min(8, fractionalTimestampBytes);
                if (fractionalTimestampBytes - serializedFractionBytes == 1) serializedFractionBytes--;
                Assert.InRange(serializedFractionBytes, 2, 8);
                var tickDelta = serializedFractionBytes switch
                {
                    2 => 1_000_000L,
                    3 => 100_000L,
                    4 => 10_000L,
                    5 => 1_000L,
                    6 => 100L,
                    7 => 10L,
                    _ => 1L
                };
                sizingItems[timestampIndex] = sizingItems[timestampIndex] with
                {
                    Revision = sizingItems[timestampIndex].Revision with
                    {
                        OccurredAt = sizingItems[timestampIndex].Revision.OccurredAt.AddTicks(tickDelta)
                    }
                };
                timestampIndex++;
                fractionalTimestampBytes -= serializedFractionBytes;
            }

            var exactWireExport = sizingExport with { Revisions = Array.AsReadOnly(sizingItems) };
            var exactWire = JsonSerializer.SerializeToUtf8Bytes(exactWireExport);
            Assert.Equal(targetWireBytes, exactWire.Length);
            PersonaHistoryExportV1.ValidateWireByteCount(exactWire.Length);
            var oneByteOverWire = JsonSerializer.SerializeToUtf8Bytes(exactWireExport with
            {
                SchemaVersion = exactWireExport.SchemaVersion + "0"
            });
            Assert.Equal(targetWireBytes + 1, oneByteOverWire.Length);
            Assert.Throws<ArgumentException>(() => PersonaHistoryExportV1.ValidateWireByteCount(oneByteOverWire.Length));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }

    }

    private static PutPersonaCommand Put(
        long expectedRevision,
        string idempotencyKey,
        IReadOnlyDictionary<string, string>? traits = null) => new(
        Soul,
        BindingId,
        AccountId,
        expectedRevision,
        traits ?? new Dictionary<string, string> { ["tone"] = "calm" },
        [new string('a', 64)],
        Trace("persona"),
        Idem(idempotencyKey),
        OccurredAt.AddMinutes(expectedRevision));

    private static ExportPersonaHistoryCommand Export(string idempotencyKey) => new(
        Soul,
        BindingId,
        AccountId,
        Trace(idempotencyKey),
        Idem(idempotencyKey),
        OccurredAt.AddMinutes(10));

    private static string Trace(string label) => "trace_" + Digest("trace:" + label)[..32];
    private static string Idem(string label) => "idem_" + Digest("idempotency:" + label);
    private static string Digest(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static SignedBindingCompositionAttestationV1 GoldenCompositionAttestation() => new(
        "1.0.0",
        "binding.composition.attestation/v1",
        "binding",
        null,
        null,
        null,
        "trace_11111111111111111111111111111111",
        "idem_2222222222222222222222222222222222222222222222222222222222222222",
        new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
        "internal",
        "dps-binding-composition-root-2026-07",
        new string('a', 64),
        1,
        new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 14, 0, 5, 0, TimeSpan.Zero),
        new string('b', 64),
        1,
        new string('c', 64),
        new string('d', 64),
        new string('e', 64),
        new string('f', 64),
        2,
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        3,
        new string('4', 64),
        new string('5', 64),
        "WXdgYveURCxtxpi8MmMF2Lxsl4b9dtlRwXYvBYR4wGl/2XztFHFTxPNt3T/VFBSl9OZnrIUJuSH/M+KKu8DL5g==");
    private static InMemoryPersonaStore CreateStore() =>
        new(new BindingFenceClient(), Convert.FromBase64String(RequestHmacKey));

    private sealed class BindingFenceClient : IBindingMutationFenceClient
    {
        public string ReturnedSoulId { get; init; } = Soul;
        public string State { get; init; } = "held";

        public Task<IBindingMutationFenceLease> AcquireAsync(
            AcquireBindingMutationFenceCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.SoulId == OtherSoul) throw new KeyNotFoundException("Unknown binding.");
            IBindingMutationFenceLease lease = new BindingFenceLease(new BindingMutationFenceV1(
                "1.0.0",
                "identity.binding.mutation.fence/v1",
                "binding",
                ReturnedSoulId,
                command.DeviceBindingId,
                command.PlatformAccountId,
                command.TraceId,
                command.IdempotencyKey,
                command.OccurredAt,
                "sensitive",
                7,
                "bfence_" + new string('f', 64),
                1,
                State));
            return Task.FromResult(lease);
        }
    }

    private sealed class BindingFenceLease(BindingMutationFenceV1 receipt) : IBindingMutationFenceLease
    {
        public BindingMutationFenceV1 Receipt { get; } = receipt;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

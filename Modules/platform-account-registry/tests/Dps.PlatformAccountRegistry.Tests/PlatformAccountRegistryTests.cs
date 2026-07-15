using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.PlatformAccountRegistry.Contracts;
using Dps.PlatformAuthorizationAuthority.Contracts;
using Xunit;

namespace Dps.PlatformAccountRegistry.Tests;

public sealed class PlatformAccountRegistryTests
{
    private const string Soul = PlatformAuthorizationEvidenceTestFactory.SoulA;
    private const string Binding = PlatformAuthorizationEvidenceTestFactory.BindingA;
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 14, 4, 0, 0, TimeSpan.Zero);

    [Fact, Trait("Category", "Unit")]
    public void AliasCollisionAndConflictingIdempotencyFailClosed()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var registry = new InMemoryPlatformAccountRegistry(authority.Verifier);
        var first = registry.Authorize(authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime));

        Assert.True(registry.IsAuthorized(first.PlatformAccountId, Soul, Binding));
        Assert.Matches("^pa_[a-f0-9]{32}$", first.PlatformAccountId);
        Assert.Throws<InvalidOperationException>(() => registry.Authorize(
            authority.Authorize(Soul, Binding, 'a', "two", "account-2", BaseTime)));
        Assert.Throws<InvalidOperationException>(() => registry.Authorize(
            authority.Authorize(Soul, Binding, 'b', "two", "account-1", BaseTime)));
    }

    [Fact, Trait("Category", "Unit")]
    public void StatusMutationIsVersionedIdempotentAndScopeProtected()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var registry = new InMemoryPlatformAccountRegistry(authority.Verifier);
        var first = registry.Authorize(authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime));
        var mutation = authority.Status(first, 1, "revoked", "revoke", "account-2", BaseTime);
        var revoked = registry.ChangeStatus(mutation);

        Assert.Equal(revoked, registry.ChangeStatus(mutation));
        Assert.False(registry.IsAuthorized(first.PlatformAccountId, Soul, Binding));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Get(
            first.PlatformAccountId, PlatformAuthorizationEvidenceTestFactory.SoulB, Binding));
        Assert.Throws<InvalidOperationException>(() => registry.ChangeStatus(
            authority.Status(first, 1, "revoked", "again", "account-3", BaseTime)));
    }

    [Fact, Trait("Category", "Unit")]
    public void EffectiveBindingReservationFreezesAuthorizationRevisionUntilRelease()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var registry = new InMemoryPlatformAccountRegistry(authority.Verifier);
        var account = registry.Authorize(authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime));
        var reservationId = "bres_" + new string('1', 64);
        var held = registry.ReserveBinding(new ReservePlatformAccountBindingCommand(
            Soul, Binding, account.PlatformAccountId, account.AuthorizationRevision, reservationId,
            PlatformAuthorizationEvidenceTestFactory.Trace("reserve"), BaseTime));
        Assert.Equal("held", held.State);
        Assert.Equal(PlatformAccountBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, "held"), held.IdempotencyKey);
        var reservation = new PlatformAccountBindingReservationCommand(
            Soul, Binding, account.PlatformAccountId, account.AuthorizationRevision, reservationId,
            PlatformAuthorizationEvidenceTestFactory.Trace("confirm"), BaseTime);
        Assert.Equal("active", registry.ConfirmBinding(reservation).State);
        Assert.Throws<PlatformAccountBindingReservationConflictException>(() => registry.ChangeStatus(
            authority.Status(account, account.AuthorizationRevision, "suspended", "suspend", "status", BaseTime)));
        Assert.Equal("released", registry.ReleaseBinding(
            reservation with { TraceId = PlatformAuthorizationEvidenceTestFactory.Trace("release") }).State);
        Assert.Equal("suspended", registry.ChangeStatus(
            authority.Status(account, account.AuthorizationRevision, "suspended", "suspend", "status", BaseTime)).Status);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task PostgresBoundaryRejectsIdentifiersThatTheJsonSchemaCannotRepresent()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var registry = new PostgresPlatformAccountRegistry(
            new PlatformAccountRegistryOptions(
                "Host=127.0.0.1;Port=1;Database=unreachable",
                "dps_platform_account_test",
                PlatformAuthorizationEvidenceTestFactory.ReleaseBomSha256,
                PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration),
            authority.Verifier);
        var command = authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime);
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => registry.AuthorizeAsync(
            command with { Platform = "fixture..invalid" }, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AuthorizeAsync(
            command with { Platform = "é" }, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AuthorizeAsync(
            command with { AliasKeyId = "clé" }, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AuthorizeAsync(
            command with { PlatformAccountId = "pa_not-canonical" }, cancellationToken));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ProductionRegistryRejectsEvidenceOutsidePinnedRootBeforeDatabaseAccess()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var registry = new PostgresPlatformAccountRegistry(new PlatformAccountRegistryOptions(
            "Host=127.0.0.1;Port=1;Database=unreachable",
            "dps_platform_account_test",
            PlatformAuthorizationEvidenceTestFactory.ReleaseBomSha256,
            PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration));
        var command = authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime);

        await Assert.ThrowsAsync<PlatformAuthorizationEvidenceException>(() =>
            registry.AuthorizeAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact, Trait("Category", "Unit")]
    public void SignedEvidenceRejectsForgeryWrongScopeExpiryAndUnknownIssuer()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        using var attacker = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var command = authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime);
        var forged = attacker.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime);
        Assert.Throws<PlatformAuthorizationEvidenceException>(() =>
            authority.Verifier.VerifyAuthorizeScope(forged.AuthorizationEvidence, forged));

        Assert.Throws<PlatformAuthorizationEvidenceException>(() => authority.Verifier.VerifyAuthorizeScope(
            command.AuthorizationEvidence,
            command with { PlatformAccountId = PlatformAuthorizationEvidenceTestFactory.PlatformAccount("wrong") }));
        Assert.Throws<PlatformAuthorizationEvidenceException>(() => authority.Verifier.VerifySignatureAndIssuer(
            command.AuthorizationEvidence with { IssuerId = "unknown-platform-authority" }));

        var expiredOccurredAt = BaseTime.AddMinutes(-6);
        var expiredEvidence = authority.CreateEvidence(
            Soul,
            Binding,
            command.PlatformAccountId,
            command.TraceId,
            command.IdempotencyKey,
            expiredOccurredAt,
            "approval_expired",
            command.Platform,
            command.AliasDigest,
            command.AliasKeyId,
            command.AliasKeyEpoch,
            "authorized",
            1,
            BaseTime.AddMinutes(-8),
            BaseTime.AddMinutes(-5));
        var expiredCommand = command with { OccurredAt = expiredOccurredAt, AuthorizationEvidence = expiredEvidence };
        authority.Verifier.VerifyAuthorizeScope(expiredEvidence, expiredCommand);
        Assert.Throws<PlatformAuthorizationEvidenceException>(() => authority.Verifier.EnsureFresh(expiredEvidence));

        var wrongReleaseEvidence = authority.CreateEvidence(
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt,
            "approval_wrong_release",
            command.Platform,
            command.AliasDigest,
            command.AliasKeyId,
            command.AliasKeyEpoch,
            "authorized",
            1,
            releaseBomSha256: new string('d', 64),
            releaseGeneration: PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration + 1);
        Assert.Throws<PlatformAuthorizationEvidenceException>(() => authority.Verifier.VerifyAuthorizeScope(
            wrongReleaseEvidence,
            command with { AuthorizationEvidence = wrongReleaseEvidence }));
    }

    [Fact, Trait("Category", "Unit")]
    public void PinnedRootGoldenVectorVerifiesAndTamperingFails()
    {
        var golden = GoldenEvidence();
        PlatformAuthorizationEvidenceVerifier.VerifyPinnedRootSignature(golden);
        Assert.Throws<PlatformAuthorizationEvidenceException>(() =>
            PlatformAuthorizationEvidenceVerifier.VerifyPinnedRootSignature(
                golden with { ReleaseBomSha256 = new string('2', 64) }));
        Assert.Throws<PlatformAuthorizationEvidenceException>(() =>
            PlatformAuthorizationEvidenceVerifier.VerifyPinnedRootSignature(
                golden with { AuthorizationRevision = 2 }));
        Assert.Throws<PlatformAuthorizationEvidenceException>(() =>
            PlatformAuthorizationEvidenceVerifier.VerifyPinnedRootSignature(
                golden with { PlatformAccountId = "pa_" + new string('d', 32) }));
    }

    [Fact, Trait("Category", "Unit")]
    public void OptionsAreRedactedAndProviderInstanceIdentityBindsNonSecretConfiguration()
    {
        var first = new PostgresPlatformAccountRegistry(new PlatformAccountRegistryOptions(
            "Host=127.0.0.1;Database=dps;Username=user;Password=secret-one", "dps_accounts",
            PlatformAuthorizationEvidenceTestFactory.ReleaseBomSha256,
            PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration,
            7));
        var passwordOnly = new PostgresPlatformAccountRegistry(new PlatformAccountRegistryOptions(
            "Host=127.0.0.1;Database=dps;Username=user;Password=secret-two", "dps_accounts",
            PlatformAuthorizationEvidenceTestFactory.ReleaseBomSha256,
            PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration,
            7));
        var otherDatabase = new PostgresPlatformAccountRegistry(new PlatformAccountRegistryOptions(
            "Host=127.0.0.1;Database=dps_other;Username=user;Password=secret-one", "dps_accounts",
            PlatformAuthorizationEvidenceTestFactory.ReleaseBomSha256,
            PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration,
            8));
        var otherRelease = new PostgresPlatformAccountRegistry(new PlatformAccountRegistryOptions(
            "Host=127.0.0.1;Database=dps;Username=user;Password=secret-one", "dps_accounts",
            new string('d', 64),
            PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration + 1,
            7));
        var optionsText = new PlatformAccountRegistryOptions(
            "Host=127.0.0.1;Database=dps;Password=secret-one", "dps_accounts",
            PlatformAuthorizationEvidenceTestFactory.ReleaseBomSha256,
            PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration,
            7).ToString();

        Assert.Equal(first.BindingProviderInstanceConfigurationSha256, passwordOnly.BindingProviderInstanceConfigurationSha256);
        Assert.NotEqual(first.BindingProviderInstanceConfigurationSha256, otherDatabase.BindingProviderInstanceConfigurationSha256);
        Assert.NotEqual(first.BindingProviderInstanceConfigurationSha256, otherRelease.BindingProviderInstanceConfigurationSha256);
        Assert.Equal(7, first.BindingProviderInstanceTrustEpoch);
        Assert.Equal(8, otherDatabase.BindingProviderInstanceTrustEpoch);
        Assert.Contains("[REDACTED]", optionsText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-one", optionsText, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Unit")]
    public void ReleaseGenerationFenceRejectsRollbackAndSameGenerationEquivocation()
    {
        var currentBom = new string('a', 64);
        var nextBom = new string('b', 64);

        PostgresPlatformAccountRegistry.EnsureReleaseGenerationTransition(null, null, 1, currentBom);
        PostgresPlatformAccountRegistry.EnsureReleaseGenerationTransition(7, currentBom, 7, currentBom);
        PostgresPlatformAccountRegistry.EnsureReleaseGenerationTransition(7, currentBom, 8, nextBom);
        Assert.Throws<UnauthorizedAccessException>(() =>
            PostgresPlatformAccountRegistry.EnsureReleaseGenerationTransition(7, currentBom, 6, currentBom));
        Assert.Throws<UnauthorizedAccessException>(() =>
            PostgresPlatformAccountRegistry.EnsureReleaseGenerationTransition(7, currentBom, 7, nextBom));
    }

    [Fact, Trait("Category", "Contract")]
    public void ContractUsesCanonicalIdentityAndHidesRawAliases()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var account = new InMemoryPlatformAccountRegistry(authority.Verifier).Authorize(
            authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime));
        account.Validate();
        var json = JsonSerializer.Serialize(account);

        Assert.Contains("\"soul_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"alias_digest\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal);
        Assert.DoesNotContain("not_applicable", json, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => (account with { SchemaVersion = "1" }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { DeviceBindingId = Guid.NewGuid().ToString() }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { DeviceBindingId = Binding + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { PlatformAccountId = account.PlatformAccountId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { TraceId = account.TraceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { IdempotencyKey = account.IdempotencyKey + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { Platform = "user@example.com" }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { Platform = new string('a', 65) }).Validate());
        Assert.Throws<ArgumentException>(() => (account with { AliasKeyId = "key\ud800" }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void BindingReservationContractIsVersionedCanonicalAndStateExpiryIsClosed()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var registry = new InMemoryPlatformAccountRegistry(authority.Verifier);
        var account = registry.Authorize(authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime));
        var value = registry.ReserveBinding(new ReservePlatformAccountBindingCommand(
            Soul, Binding, account.PlatformAccountId, 1, "bres_" + new string('2', 64),
            PlatformAuthorizationEvidenceTestFactory.Trace("reserve"), BaseTime));
        value.Validate();
        var json = JsonSerializer.Serialize(value);
        Assert.Contains("\"reservation_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lease_expires_at\"", json, StringComparison.Ordinal);
        Assert.Matches("^idem_[a-f0-9]{64}$", value.IdempotencyKey);
        Assert.Throws<ArgumentException>(() => (value with { ReservationId = "caller-proof" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { State = "active" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { IdempotencyKey = "idem_" + new string('0', 64) }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void StrictJsonRejectsMissingUnknownDuplicateAndPrivacyViolations()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var account = new InMemoryPlatformAccountRegistry(authority.Verifier).Authorize(
            authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime));
        var json = JsonSerializer.Serialize(account);
        Assert.Equal(account, PlatformAccountContractJson.DeserializeStrict<PlatformAccountAuthorizedV1>(json));

        var missing = JsonNode.Parse(json)!.AsObject();
        missing.Remove("alias_key_epoch");
        Assert.Throws<JsonException>(() =>
            PlatformAccountContractJson.DeserializeStrict<PlatformAccountAuthorizedV1>(missing.ToJsonString()));
        var unknown = JsonNode.Parse(json)!.AsObject();
        unknown["raw_email"] = "user@example.com";
        Assert.Throws<JsonException>(() =>
            PlatformAccountContractJson.DeserializeStrict<PlatformAccountAuthorizedV1>(unknown.ToJsonString()));
        var insertion = json.IndexOf(",\"contract_id\"", StringComparison.Ordinal);
        var duplicate = json.Insert(insertion, ",\"schema_version\":\"1.0.0\"");
        Assert.Throws<JsonException>(() =>
            PlatformAccountContractJson.DeserializeStrict<PlatformAccountAuthorizedV1>(duplicate));
    }

    [Fact, Trait("Category", "Contract")]
    public void SignedEvidenceContractIsExactVersionedAndStrictlyDeserializable()
    {
        using var authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        var evidence = authority.Authorize(Soul, Binding, 'a', "one", "account-1", BaseTime).AuthorizationEvidence;
        evidence.Validate();
        var json = JsonSerializer.Serialize(evidence);
        Assert.Equal(evidence, PlatformAuthorizationAuthorityContractJson.DeserializeEvidenceStrict(json));
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NotSupportedException>(() => (evidence with { SchemaVersion = "1.0.1" }).Validate());
        Assert.Throws<ArgumentException>(() => (evidence with { ExpiresAt = evidence.IssuedAt.AddMinutes(16) }).Validate());
        Assert.Throws<ArgumentException>(() => (evidence with { SignatureBase64 = "not-base64" }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void SharedAuthorizedAccountBoundaryCorpusMatchesStrictConsumerRules()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Corpus",
            "platform.account.authorized.v1.corpus.json");
        var rawCorpus = File.ReadAllText(path);
        Assert.Contains("9223372036854775807", rawCorpus, StringComparison.Ordinal);
        Assert.Contains("9223372036854775808", rawCorpus, StringComparison.Ordinal);
        Assert.DoesNotContain("9223372036854776000", rawCorpus, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(rawCorpus);
        var valid = document.RootElement.GetProperty("valid").EnumerateArray().ToArray();
        var invalid = document.RootElement.GetProperty("invalid").EnumerateArray().ToArray();

        Assert.Equal(2, valid.Length);
        Assert.Equal(20, invalid.Length);
        foreach (var entry in valid)
        {
            var parsed = PlatformAccountContractJson.DeserializeStrict<PlatformAccountAuthorizedV1>(
                entry.GetProperty("payload").GetRawText());
            Assert.Equal(PlatformAccountAuthorizedV1.CurrentContractId, parsed.ContractId);
        }
        foreach (var entry in invalid)
        {
            Assert.ThrowsAny<Exception>(() =>
                PlatformAccountContractJson.DeserializeStrict<PlatformAccountAuthorizedV1>(
                    entry.GetProperty("payload").GetRawText()));
        }

        var schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Schemas",
            "platform.account.authorized.v1.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var properties = schema.RootElement.GetProperty("properties");
        foreach (var field in new[] { "platform", "alias_key_id", "authorization_evidence_id" })
        {
            Assert.EndsWith(
                "$(?![\\s\\S])",
                properties.GetProperty(field).GetProperty("pattern").GetString(),
                StringComparison.Ordinal);
        }
        Assert.StartsWith(
            "^[a-z0-9]",
            properties.GetProperty("alias_key_id").GetProperty("pattern").GetString(),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "^approval_[a-z0-9_-]",
            properties.GetProperty("authorization_evidence_id").GetProperty("pattern").GetString(),
            StringComparison.Ordinal);
    }

    private static SignedPlatformAuthorizationEvidenceV1 GoldenEvidence() => new(
        "1.0.0",
        "platform.account.authorization.evidence/v1",
        "platform-authorization-authority",
        "soul_" + new string('a', 64),
        "db_" + new string('b', 32),
        "pa_" + new string('c', 32),
        "trace_" + new string('d', 32),
        "idem_" + new string('e', 64),
        new DateTimeOffset(2026, 7, 14, 4, 0, 0, TimeSpan.Zero),
        "sensitive",
        "approval_golden_202607",
        "fixture",
        new string('f', 64),
        "tenant-hmac-v1",
        7,
        "authorized",
        1,
        PlatformAuthorizationEvidenceVerifier.PinnedIssuerId,
        PlatformAuthorizationEvidenceVerifier.PinnedIssuerKeyId,
        new string('1', 64),
        9,
        new DateTimeOffset(2026, 7, 14, 3, 59, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 14, 4, 9, 0, TimeSpan.Zero),
        "mG9buYucs8QGLBT4/x7kTNucA64lmHwa2QAw2a7NXrz0AuVAJTeIvly1sEzm1hwSnju+5fYcJjZgqu4pUNjU/w==");
}

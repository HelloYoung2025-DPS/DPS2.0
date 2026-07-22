using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Dps.ExecutorGateway;
using Dps.PolicyApproval;
using Npgsql;
using Xunit;

namespace Dps.ControlPlaneHost.ActiveBindingSameInstance.Tests;

/// <summary>
/// REAL_POSTGRESQL (18.4, DPS_TEST_POSTGRES) M1B/R0-C same-instance proof:
/// ONE shared <see cref="ActiveReleaseBindingAuthority"/> instance backed by
/// ONE <see cref="PostgresReleaseBindingTruthStore"/> is injected as the
/// composition-fixed <see cref="IActiveReleaseBindingReader"/> into BOTH
/// consumer code paths — the executor side (the real provider-backed
/// <see cref="ControlPlaneHostActiveReleaseBomReader"/>) and the policy side
/// (the real production <see cref="PolicyApprovalSubmissionRecoveryClient"/>
/// composition, whose commit-time
/// RequireActiveReleaseBindingMatchesRecovery re-read performs exactly the
/// TryReadActive call asserted here against the same instance).
///
/// Assertion vocabulary: "same running instance, same
/// generation/token/status — engineering/integration-level proof per
/// RebuildPlan §4.3 (PR#6 v2); not a production-topology claim (M4)".
///
/// Public-API boundary (no InternalsVisibleTo anywhere): the policy-side
/// in-RecoverAsync accept/reject wiring behind a durable
/// RECONCILED_NOT_SUBMITTED predecessor is covered by policy-approval's own
/// required Integration suite
/// (PostgresPolicyApprovalRecoveryReleaseBindingIntegrationTests); the
/// lease methods that create that predecessor are internal to
/// policy-approval, so this cross-module suite proves the same-instance
/// observation at the exact read both consumers issue, plus the real
/// production recovery composition binding that reader. All binding state
/// here is established exclusively through the production Activate /
/// Revoke / Rollback API — never by direct journal writes.
/// </summary>
public sealed class ActiveBindingSameInstanceIntegrationTests
{
    private const string Device = "db_11111111111111111111111111111111";
    private const string ForeignDevice = "db_99999999999999999999999999999999";
    private const string PolicySchema = "sameinst_policy_composition";
    private const string PolicyRecoveryRole = "sameinst_policy_recovery";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task ActivationIsObservedIdenticallyByExecutorAndPolicyConsumerPathsOnOneInstance()
    {
        await using var database = await SameInstanceReleaseBindingDatabase.CreateAsync(Token);
        using var signer = new SameInstanceBomSigner();
        var releaseBindingStore = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], releaseBindingStore, () => Now);
        var (bom, token) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom, token);

        // Executor consumer path: the real composition-fixed adapter over
        // the SAME running instance.
        var executorReader = new ControlPlaneHostActiveReleaseBomReader(authority);
        var executorObserved = await executorReader.ReadVerifiedActiveAsync(Device, Token);

        Assert.NotNull(executorObserved);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(bom), executorObserved!.ReleaseBomSha256);
        Assert.Equal(1, executorObserved.Generation);
        Assert.Equal(token, executorObserved.ExecutionTokenBase64);

        // Policy consumer path: the real production recovery composition
        // binds the store-issued coordination capability from the SAME CPH
        // instance; the capability is mandatory (null fails construction).
        using (var policyRecovery = CreatePolicyRecoveryClient(releaseBindingStore))
        {
            Assert.NotNull(policyRecovery);
        }
        Assert.Throws<ArgumentNullException>(() => CreatePolicyRecoveryClient(null!));

        // The commit-time policy re-read is exactly this TryReadActive call
        // against this instance; it must observe the same
        // generation/token/status as the executor path.
        Assert.True(authority.TryReadActive(Device, out var policyObserved));
        Assert.NotNull(policyObserved);
        Assert.Equal(executorObserved.ReleaseBomSha256, policyObserved!.ReleaseBomSha256);
        Assert.Equal(executorObserved.Generation, policyObserved.Generation);
        Assert.Equal(executorObserved.ExecutionTokenBase64, policyObserved.ExecutionTokenBase64);
        Assert.Equal("active", policyObserved.Status);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RevocationFailsBothConsumersClosedAndRollbackRestoresTheIdenticalTarget()
    {
        await using var database = await SameInstanceReleaseBindingDatabase.CreateAsync(Token);
        using var signer = new SameInstanceBomSigner();
        var releaseBindingStore = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], releaseBindingStore, () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        authority.Activate(Device, bom2, token2);
        var executorReader = new ControlPlaneHostActiveReleaseBomReader(authority);
        using var policyRecovery = CreatePolicyRecoveryClient(releaseBindingStore);
        Assert.True(authority.TryReadActive(Device, out var active));
        Assert.Equal(2, active!.Generation);

        // Revocation: the executor adapter goes fail-closed (null) and the
        // same instance reports no readable active binding to the policy
        // commit-time check either.
        authority.Revoke(Device, active.Generation);
        Assert.Null(await executorReader.ReadVerifiedActiveAsync(Device, Token));
        Assert.False(authority.TryReadActive(Device, out _));

        // Rollback: both consumer paths observe the identical restored
        // target — bom1 sha, runtime generation 3 (strictly monotonic,
        // rollback included), the bom1 execution token.
        authority.Rollback(Device, token1);
        var executorObserved = await executorReader.ReadVerifiedActiveAsync(Device, Token);
        Assert.NotNull(executorObserved);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(bom1), executorObserved!.ReleaseBomSha256);
        Assert.Equal(3, executorObserved.Generation);
        Assert.Equal(token1, executorObserved.ExecutionTokenBase64);
        Assert.True(authority.TryReadActive(Device, out var policyObserved));
        Assert.Equal(executorObserved.ReleaseBomSha256, policyObserved!.ReleaseBomSha256);
        Assert.Equal(executorObserved.Generation, policyObserved.Generation);
        Assert.Equal(executorObserved.ExecutionTokenBase64, policyObserved.ExecutionTokenBase64);
        Assert.Equal("active", policyObserved.Status);

        // A policy recovery envelope pinned to the stale pre-transition
        // facts (bom2 sha, generation 2) names exactly what this instance no
        // longer reports to either consumer — the commit-time pair check
        // (sha256, generation) cannot match it on this instance.
        Assert.False(
            string.Equals(policyObserved.ReleaseBomSha256, SameInstanceBomSigner.Sha256Hex(bom2), StringComparison.Ordinal)
            && policyObserved.Generation == 2);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RestartedAuthorityOverTheSameStorePresentsIdenticalTruthToBothConsumers()
    {
        await using var database = await SameInstanceReleaseBindingDatabase.CreateAsync(Token);
        using var signer = new SameInstanceBomSigner();
        var firstReleaseBindingStore = database.CreateStore();
        var first = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], firstReleaseBindingStore, () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        first.Activate(Device, bom1, token1);
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        first.Activate(Device, bom2, token2);
        Assert.True(first.TryReadActive(Device, out var active));
        first.Revoke(Device, active!.Generation);
        first.Rollback(Device, token1);
        Assert.True(first.TryReadActive(Device, out var beforeRestart));

        // Process restart: a SECOND authority over the same store replays
        // the journal with full re-verification; both consumer paths are
        // re-pointed at it and observe the same recovered
        // generation/token/sha (revocation/rollback truth survived).
        var restartedReleaseBindingStore = database.CreateStore();
        var restarted = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], restartedReleaseBindingStore, () => Now);
        var executorReader = new ControlPlaneHostActiveReleaseBomReader(restarted);
        using var policyRecovery = CreatePolicyRecoveryClient(restartedReleaseBindingStore);
        var executorObserved = await executorReader.ReadVerifiedActiveAsync(Device, Token);

        Assert.NotNull(executorObserved);
        Assert.Equal(beforeRestart!.ReleaseBomSha256, executorObserved!.ReleaseBomSha256);
        Assert.Equal(beforeRestart.Generation, executorObserved.Generation);
        Assert.Equal(beforeRestart.ExecutionTokenBase64, executorObserved.ExecutionTokenBase64);
        Assert.True(restarted.TryReadActive(Device, out var afterRestart));
        Assert.Equal(beforeRestart, afterRestart);
        Assert.Equal("active", afterRestart!.Status);
        Assert.Equal(first.ReadReceipts(Device), restarted.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ForeignDeviceBindingIsFailClosedForBothConsumerPaths()
    {
        await using var database = await SameInstanceReleaseBindingDatabase.CreateAsync(Token);
        using var signer = new SameInstanceBomSigner();
        var releaseBindingStore = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], releaseBindingStore, () => Now);
        var (bom, token) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom, token);
        var executorReader = new ControlPlaneHostActiveReleaseBomReader(authority);
        using var policyRecovery = CreatePolicyRecoveryClient(releaseBindingStore);

        // A second device_binding_id observes nothing on either consumer
        // path: the executor adapter reads null and the same instance
        // reports no active binding to the policy commit-time check.
        Assert.Null(await executorReader.ReadVerifiedActiveAsync(ForeignDevice, Token));
        Assert.False(authority.TryReadActive(ForeignDevice, out _));
        Assert.NotNull(await executorReader.ReadVerifiedActiveAsync(Device, Token));
        Assert.True(authority.TryReadActive(Device, out _));
    }

    /// <summary>
    /// Real production policy recovery composition over the same store-issued
    /// coordination capability, with a pairwise-distinct
    /// eight-authority topology generated per call. The options carry a
    /// well-formed bounded connection string; composition itself performs
    /// no PostgreSQL I/O (per-operation role verification happens only
    /// inside the real RecoverAsync path).
    /// </summary>
    private static PolicyApprovalSubmissionRecoveryClient CreatePolicyRecoveryClient(
        IActiveReleaseBindingRecoveryCoordinator releaseBindingRecoveryCoordinator)
    {
        using var evaluation = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var promotion = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocation = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fence = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliation = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recovery = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var state = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var topology = PolicyApprovalSubmissionAuthorityTopology.Create(
            evaluation.ExportSubjectPublicKeyInfo(),
            promotion.ExportSubjectPublicKeyInfo(),
            revocation.ExportSubjectPublicKeyInfo(),
            fence.ExportSubjectPublicKeyInfo(),
            executor.ExportSubjectPublicKeyInfo(),
            reconciliation.ExportSubjectPublicKeyInfo(),
            recovery.ExportSubjectPublicKeyInfo(),
            state.ExportSubjectPublicKeyInfo());
        var connectionString = new NpgsqlConnectionStringBuilder(
                SameInstanceReleaseBindingDatabase.RequireConnectionString())
        {
            Username = PolicyRecoveryRole,
            Password = "sameinst-composition-only",
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5
        }.ConnectionString;
        var statePrivateKey = state.ExportPkcs8PrivateKey();
        try
        {
            return PolicyApprovalSubmissionRecoveryClient.CreateProduction(
                new PostgresPolicyApprovalSubmissionRecoveryOptions(
                    connectionString,
                    PolicySchema,
                    PolicyRecoveryRole),
                topology,
                executor.ExportSubjectPublicKeyInfo(),
                recovery.ExportSubjectPublicKeyInfo(),
                statePrivateKey,
                releaseBindingRecoveryCoordinator);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statePrivateKey);
        }
    }

    /// <summary>
    /// Runtime-generated RSA-PSS release BOM signer emitting the exact
    /// canonical sorted compact wire the activation authority accepts
    /// (mirrors the control-plane-host activation authority fixture over
    /// public APIs only).
    /// </summary>
    private sealed class SameInstanceBomSigner : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);

        internal SameInstanceBomSigner(
            string keyId = "test-bom-key-v1",
            string identity = "test-release-controller")
        {
            KeyId = keyId;
            Identity = identity;
        }

        internal string KeyId { get; }
        internal string Identity { get; }

        internal ReleaseBomTrustKey TrustKey
        {
            get
            {
                var parameters = _rsa.ExportParameters(false);
                return new ReleaseBomTrustKey(
                    KeyId,
                    Identity,
                    Convert.ToHexStringLower(parameters.Modulus!),
                    65537);
            }
        }

        internal static string Sha256Hex(byte[] value)
            => Convert.ToHexStringLower(SHA256.HashData(value));

        internal (byte[] Bom, string Token) SignBom(
            string bomId,
            long signerGeneration,
            byte[]? previousBom)
        {
            var token = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes("token:" + bomId)));
            var tokenBytes = Convert.FromBase64String(token);
            var payload = new JsonObject
            {
                ["schema_version"] = "dps.release-bom/v1",
                ["bom_id"] = bomId,
                ["status"] = "SIGNED",
                ["integration_commit"] = new string('a', 40),
                ["created_at"] = "2026-07-14T00:00:00.0000001Z",
                ["release_bom_generation"] = signerGeneration,
                ["activation_token_sha256"] = Convert.ToHexStringLower(SHA256.HashData(tokenBytes)),
                ["modules"] = new JsonArray(),
                ["instruction_hashes"] = new JsonObject(),
                ["contracts"] = new JsonArray(),
                ["database_versions"] = new JsonObject(),
                ["dependency_dag_sha256"] = new string('b', 64),
                ["compatibility_matrix_sha256"] = new string('c', 64),
                ["feature_flags"] = new JsonObject(),
                ["kill_switches"] = new JsonArray(),
                ["ai_toolchain"] = new JsonObject(),
                ["evidence"] = new JsonArray(),
                ["risk"] = new JsonObject(),
                ["release_approval"] = new JsonObject(),
                ["rollout"] = new JsonObject(),
                ["rollback"] = new JsonObject(),
                ["previous_stable_bom"] = previousBom is null
                    ? null
                    : (JsonNode)("bom-previous-" + bomId),
                ["previous_stable_bom_sha256"] = previousBom is null
                    ? null
                    : Sha256Hex(previousBom),
                ["native_stop_authorities"] = new JsonArray(),
                ["device_route_assignment_authorities"] = new JsonArray(),
                ["native_stop_challenge_authorities"] = new JsonArray()
            };
            using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
            var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
            var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
                .Concat(canonical)
                .ToArray();
            var signature = _rsa.SignData(
                message,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            payload["signature"] = new JsonObject
            {
                ["algorithm"] = "rsa-pss-sha256",
                ["key_id"] = KeyId,
                ["value"] = Convert.ToBase64String(signature)
            };
            using var fullDocument = JsonDocument.Parse(payload.ToJsonString());
            return (ReleaseBomCanonicalJson.Serialize(fullDocument.RootElement), token);
        }

        public void Dispose() => _rsa.Dispose();
    }

    /// <summary>
    /// One disposable release binding truth database per test: a fresh
    /// least-privilege runtime login role and a fresh marked schema created
    /// by the production migrator; disposal drops both. Missing
    /// DPS_TEST_POSTGRES is a hard failure, never a skip.
    /// </summary>
    private sealed class SameInstanceReleaseBindingDatabase : IAsyncDisposable
    {
        private readonly string _migrationConnectionString;
        private readonly string _runtimeConnectionString;
        private readonly string _schemaName;
        private readonly string _runtimeRoleName;
        private readonly string _migrationRoleName;

        private SameInstanceReleaseBindingDatabase(
            string migrationConnectionString,
            string runtimeConnectionString,
            string schemaName,
            string runtimeRoleName,
            string migrationRoleName)
        {
            _migrationConnectionString = migrationConnectionString;
            _runtimeConnectionString = runtimeConnectionString;
            _schemaName = schemaName;
            _runtimeRoleName = runtimeRoleName;
            _migrationRoleName = migrationRoleName;
        }

        internal static string RequireConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES is required for REAL_POSTGRESQL same-instance Integration; missing infrastructure is not a skip or pass.");
            }

            return value;
        }

        internal static async Task<SameInstanceReleaseBindingDatabase> CreateAsync(
            CancellationToken cancellationToken)
        {
            var migrationConnectionString = RequireConnectionString();
            var migrationBuilder = new NpgsqlConnectionStringBuilder(migrationConnectionString);
            if (migrationBuilder.Port == 55434
                || string.Equals(
                    migrationBuilder.Database,
                    "dps_gbrain_company",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Same-instance Integration refuses the persistent GBrain Company database.");
            }

            if (string.IsNullOrWhiteSpace(migrationBuilder.Username))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES requires an explicit migration username.");
            }

            var suffix = Guid.NewGuid().ToString("N")[..20];
            var schemaName = "sameinst_it_" + suffix;
            var runtimeRoleName = "sameinst_rt_" + suffix;
            var migrationRoleName = migrationBuilder.Username;
            var passwordBytes = RandomNumberGenerator.GetBytes(32);
            string runtimePassword;
            try
            {
                runtimePassword = Convert.ToHexStringLower(passwordBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            try
            {
                await using (var admin = new NpgsqlConnection(migrationConnectionString))
                {
                    await admin.OpenAsync(cancellationToken);
                    await using (var version = new NpgsqlCommand("SHOW server_version_num", admin))
                    {
                        var actual = (string?)await version.ExecuteScalarAsync(cancellationToken);
                        if (!string.Equals(actual, "180004", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Same-instance Integration requires exact PostgreSQL 18.4; server_version_num was '{actual ?? "missing"}'.");
                        }
                    }

                    string createRoleSql;
                    await using (var formatRole = new NpgsqlCommand(
                        "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', @role_name, @password)",
                        admin))
                    {
                        formatRole.Parameters.AddWithValue("role_name", runtimeRoleName);
                        formatRole.Parameters.AddWithValue("password", runtimePassword);
                        createRoleSql = (string)(await formatRole.ExecuteScalarAsync(cancellationToken)
                            ?? throw new InvalidOperationException(
                                "PostgreSQL did not produce the controlled CREATE ROLE statement."));
                    }

                    await using var createRole = new NpgsqlCommand(createRoleSql, admin);
                    await createRole.ExecuteNonQueryAsync(cancellationToken);
                }

                var runtimeBuilder = new NpgsqlConnectionStringBuilder(migrationConnectionString)
                {
                    Username = runtimeRoleName,
                    Password = runtimePassword,
                    Pooling = false,
                    Options = string.Empty,
                    LogParameters = false,
                    IncludeErrorDetail = false,
                    PersistSecurityInfo = false
                };
                var migrator = new PostgresReleaseBindingTruthMigrator(
                    new PostgresReleaseBindingMigrationOptions(
                        migrationConnectionString,
                        schemaName,
                        runtimeRoleName));
                await migrator.InitializeAsync(cancellationToken);
                return new SameInstanceReleaseBindingDatabase(
                    migrationConnectionString,
                    runtimeBuilder.ConnectionString,
                    schemaName,
                    runtimeRoleName,
                    migrationRoleName);
            }
            catch
            {
                await CleanupAsync(
                    migrationConnectionString,
                    schemaName,
                    runtimeRoleName,
                    CancellationToken.None);
                throw;
            }
        }

        internal PostgresReleaseBindingTruthStore CreateStore()
            => new(new PostgresReleaseBindingTruthStoreOptions(
                _runtimeConnectionString,
                _schemaName,
                _runtimeRoleName,
                _migrationRoleName));

        public async ValueTask DisposeAsync()
            => await CleanupAsync(
                _migrationConnectionString,
                _schemaName,
                _runtimeRoleName,
                CancellationToken.None);

        private static async Task CleanupAsync(
            string migrationConnectionString,
            string schemaName,
            string runtimeRoleName,
            CancellationToken cancellationToken)
        {
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schemaName);
            var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRoleName);
            await using var admin = new NpgsqlConnection(migrationConnectionString);
            await admin.OpenAsync(cancellationToken);
            await using var cleanup = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE; DROP ROLE IF EXISTS {quotedRole}",
                admin);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

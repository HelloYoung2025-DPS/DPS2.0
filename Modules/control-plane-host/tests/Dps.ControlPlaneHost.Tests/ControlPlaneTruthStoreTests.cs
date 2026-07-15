using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using Xunit;

namespace Dps.ControlPlaneHost.Tests;

public sealed class ControlPlaneTruthStoreTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSoul = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Binding = "db_11111111111111111111111111111111";
    private const string OtherBinding = "db_22222222222222222222222222222222";
    private const string Account = "pa_33333333333333333333333333333333";
    private const string OtherAccount = "pa_44444444444444444444444444444444";
    private const string Trace = "trace_55555555555555555555555555555555";
    private const string OtherTrace = "trace_66666666666666666666666666666666";
    private const string Idempotency =
        "idem_7777777777777777777777777777777777777777777777777777777777777777";
    private const string OtherIdempotency =
        "idem_8888888888888888888888888888888888888888888888888888888888888888";
    private const string ActiveReleaseBom =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly string CanonicalZeroSignature =
        Convert.ToBase64String(new byte[64]);

    private static ModuleResultEnvelope Result(
        string contract = "identity.binding/v1",
        string producer = "binding",
        string status = "active",
        string idem = Idempotency)
        => new(
            "1.0.0",
            contract,
            producer,
            Soul,
            Binding,
            Account,
            Trace,
            idem,
            Now,
            new string('a', 64),
            status);

    [Fact, Trait("Category", "Unit")]
    public void AllowlistedResultIsIdempotentAndScopeProtected()
    {
        var store = new ControlPlaneTruthStore();
        var input = Result();
        var first = store.Ingest(input);
        var duplicate = store.Ingest(input);

        Assert.Equal(first, duplicate);
        Assert.Equal(
            first,
            store.Get(
                Soul,
                Binding,
                Account,
                "identity.binding/v1",
                Idempotency));
        Assert.Throws<KeyNotFoundException>(() =>
            store.Get(
                OtherSoul,
                Binding,
                Account,
                "identity.binding/v1",
                Idempotency));
        Assert.Throws<InvalidOperationException>(() =>
            store.Ingest(input with { SourcePayloadSha256 = new string('b', 64) }));
    }

    [Fact, Trait("Category", "Unit")]
    public void CanonicalEncodingMatchesDomainSeparatedGoldenVectors()
    {
        var result = Result();

        Assert.Equal(
            "20c5f662fd705a7453f6f384cda4353d91792565ec2b0cd0a757e446d9decf3f",
            ControlPlaneCanonicalEncoding.ComputeBusinessKeySha256(result));
        Assert.Equal(
            "2b7841a435b857b52171bfb64c20f8e553f7515ada5a7faa5a396d0e1eab553d",
            ControlPlaneCanonicalEncoding.ComputeReceiptPayloadSha256(result));
        Assert.Equal(
            "receipt_2b7841a435b857b52171bfb64c20f8e5",
            new ControlPlaneTruthStore().Ingest(result).ReceiptId);
    }

    [Fact, Trait("Category", "Unit")]
    public void ReceiptCanonicalHashCoversEveryEnvelopeField()
    {
        var baseline = Result();
        var baselineHash = ControlPlaneCanonicalEncoding.ComputeReceiptPayloadSha256(baseline);
        ModuleResultEnvelope[] mutations =
        [
            baseline with { SchemaVersion = "1.0.1" },
            baseline with { SourceContractId = "device.registered/v1" },
            baseline with { SourceProducerModule = "device-registry" },
            baseline with { SoulId = OtherSoul },
            baseline with { DeviceBindingId = OtherBinding },
            baseline with { PlatformAccountId = OtherAccount },
            baseline with { TraceId = OtherTrace },
            baseline with { IdempotencyKey = OtherIdempotency },
            baseline with { OccurredAt = Now.AddTicks(1) },
            baseline with { SourcePayloadSha256 = new string('b', 64) },
            baseline with { ResultStatus = "revoked" }
        ];

        var hashes = mutations
            .Select(ControlPlaneCanonicalEncoding.ComputeReceiptPayloadSha256)
            .ToArray();
        Assert.DoesNotContain(baselineHash, hashes);
        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact, Trait("Category", "Unit")]
    public void StrictCorrelationIdsEliminateDelimiterInjectionAndValidScopesRemainDistinct()
    {
        var store = new ControlPlaneTruthStore();
        Assert.Throws<ArgumentException>(() => store.Ingest(
            Result() with { TraceId = "trace_1234:binding:payload" }));
        Assert.Throws<ArgumentException>(() => store.Ingest(
            Result(idem: "idem_user@example.com")));

        var firstInput = Result();
        var secondInput = Result(idem: OtherIdempotency) with
        {
            TraceId = OtherTrace
        };
        var first = store.Ingest(firstInput);
        var second = store.Ingest(secondInput);

        Assert.NotEqual(
            ControlPlaneCanonicalEncoding.ComputeBusinessKeySha256(firstInput),
            ControlPlaneCanonicalEncoding.ComputeBusinessKeySha256(secondInput));
        Assert.NotEqual(first.ReceiptId, second.ReceiptId);
        Assert.Equal(
            first,
            store.Get(
                firstInput.SoulId,
                firstInput.DeviceBindingId,
                firstInput.PlatformAccountId,
                firstInput.SourceContractId,
                firstInput.IdempotencyKey));
        Assert.Equal(
            second,
            store.Get(
                secondInput.SoulId,
                secondInput.DeviceBindingId,
                secondInput.PlatformAccountId,
                secondInput.SourceContractId,
                secondInput.IdempotencyKey));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownProducerContractAndPreparedMemoryFailClosed()
    {
        var store = new ControlPlaneTruthStore();
        Assert.Throws<NotSupportedException>(() =>
            store.Ingest(Result("unknown.contract/v1", "unknown", "active")));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Ingest(Result(producer: "planner")));
        Assert.Throws<InvalidOperationException>(() =>
            store.Ingest(Result("soul.memory.readback/v1", "soul-memory-adapter", "prepared")));
        Assert.Equal(64, HostStartup.Run([], TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, HostStartup.Run(["--self-check"], TextWriter.Null, TextWriter.Null));
    }

    [Fact, Trait("Category", "Unit")]
    public void PostgreSqlOptionsRejectGbrainSharedRolesAndStartupRoleOptions()
    {
        Assert.Throws<InvalidOperationException>(() => new PostgresControlPlaneTruthStore(
            new PostgresControlPlaneTruthStoreOptions(
                "Host=127.0.0.1;Port=55434;Database=control_test;Username=control_rt",
                "control_test",
                "control_rt",
                "control_admin")));
        Assert.Throws<InvalidOperationException>(() => new PostgresControlPlaneTruthMigrator(
            new PostgresControlPlaneMigrationOptions(
                "Host=127.0.0.1;Port=5432;Database=dps_gbrain_company;Username=admin",
                "control_test",
                "control_rt")));
        Assert.Throws<UnauthorizedAccessException>(() => new PostgresControlPlaneTruthMigrator(
            new PostgresControlPlaneMigrationOptions(
                "Host=127.0.0.1;Port=5432;Database=control_test;Username=control_rt",
                "control_test",
                "control_rt")));
        Assert.Throws<UnauthorizedAccessException>(() => new PostgresControlPlaneTruthStore(
            new PostgresControlPlaneTruthStoreOptions(
                "Host=127.0.0.1;Port=5432;Database=control_test;Username=control_rt;Options=-c role=admin",
                "control_test",
                "control_rt",
                "control_admin")));
    }

    [Fact, Trait("Category", "Unit")]
    public void PostgreSqlOptionsRequireExplicitTargetNormalizeDiagnosticsAndRedactSecrets()
    {
        Assert.Throws<ArgumentException>(() => new PostgresControlPlaneTruthStoreOptions(
            "Host=127.0.0.1;Port=5432;Username=control_rt",
            "control_test",
            "control_rt",
            "control_admin").ValidatedConnectionString());
        Assert.Throws<ArgumentException>(() => new PostgresControlPlaneTruthStoreOptions(
            "Host=127.0.0.1;Database=control_test;Username=control_rt",
            "control_test",
            "control_rt",
            "control_admin").ValidatedConnectionString());

        const string secret = "fixture-password-never-log";
        var options = new PostgresControlPlaneTruthStoreOptions(
            "Host=127.0.0.1;Port=5432;Database=control_test;Username=control_rt;"
                + "Password=" + secret + ";Log Parameters=true;Include Error Detail=true;"
                + "Persist Security Info=true;Pooling=true;Timeout=30;Command Timeout=30",
            "control_test",
            "control_rt",
            "control_admin");
        var normalized = new NpgsqlConnectionStringBuilder(
            options.ValidatedConnectionString());

        Assert.Equal("127.0.0.1", normalized.Host);
        Assert.Equal(5432, normalized.Port);
        Assert.Equal("control_test", normalized.Database);
        Assert.Equal("control_rt", normalized.Username);
        Assert.False(normalized.LogParameters);
        Assert.False(normalized.IncludeErrorDetail);
        Assert.False(normalized.PersistSecurityInfo);
        Assert.False(normalized.Pooling);
        Assert.Equal(5, normalized.Timeout);
        Assert.Equal(5, normalized.CommandTimeout);
        Assert.Contains("[REDACTED]", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, options.ToString(), StringComparison.Ordinal);

        var migrationOptions = new PostgresControlPlaneMigrationOptions(
            "Host=127.0.0.1;Port=5432;Database=control_test;Username=control_admin;Password="
                + secret,
            "control_test",
            "control_rt");
        _ = migrationOptions.ValidatedConnectionString();
        Assert.Contains("[REDACTED]", migrationOptions.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, migrationOptions.ToString(), StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract")]
    public void ReceiptUsesCanonicalSnakeCaseContractAndRejectsUnknownMajorOrLooseIds()
    {
        var receipt = new ControlPlaneTruthStore().Ingest(Result());
        receipt.Validate();
        AssertReceiptCorpusCodec();
        var payload = ControlPlaneReceiptV1Codec.Serialize(receipt);
        var json = Encoding.UTF8.GetString(payload);
        Assert.Contains("\"source_payload_sha256\"", json, StringComparison.Ordinal);
        Assert.Equal(receipt, ControlPlaneReceiptV1Codec.Deserialize(payload));
        Assert.Throws<ArgumentException>(() => ControlPlaneReceiptV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(
                "\"occurred_at\":\"2026-07-14T00:00:00Z\"," ,
                string.Empty,
                StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ControlPlaneReceiptV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Insert(1, "\"unexpected\":true,"))));
        Assert.Throws<ArgumentException>(() => ControlPlaneReceiptV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(
                "\"schema_version\":\"1.0.0\"," ,
                "\"schema_version\":\"1.0.0\",\"schema_version\":\"1.0.0\"," ,
                StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ControlPlaneReceiptV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("Z\"", "+00:00\"", StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ControlPlaneReceiptV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json + "\n")));
        Assert.Throws<NotSupportedException>(() =>
            (receipt with { SchemaVersion = "2.0" }).Validate());
        Assert.Throws<NotSupportedException>(() =>
            (receipt with { SchemaVersion = "01" }).Validate());
        Assert.Throws<NotSupportedException>(() =>
            (receipt with { SchemaVersion = "1.evil" }).Validate());
        Assert.Throws<NotSupportedException>(() =>
            (receipt with { SchemaVersion = "1." }).Validate());
        Assert.Throws<NotSupportedException>(() =>
            (receipt with { SourceProducerModule = "soul-memory-adapter" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { DeviceBindingId = Guid.NewGuid().ToString() }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { PlatformAccountId = "pa_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { TraceId = "trace_bearer-token" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { IdempotencyKey = "idem_user@example.com" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { DeviceBindingId = Binding + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { PlatformAccountId = Account + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { TraceId = Trace + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { IdempotencyKey = Idempotency + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { SoulId = Soul + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { ReceiptId = receipt.ReceiptId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (receipt with { SourcePayloadSha256 = receipt.SourcePayloadSha256 + "\n" }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void EmbeddedMigrationUsesSecurityDefinerAtomicApiAndDeniesDirectRuntimeInsert()
    {
        var assembly = typeof(PostgresControlPlaneTruthMigrator).Assembly;
        var resource = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith(
                "001_create_control_plane_truth.sql",
                StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resource));
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        Assert.Contains("soul.memory.readback/v1", sql, StringComparison.Ordinal);
        Assert.Contains("result_status = 'verified'", sql, StringComparison.Ordinal);
        Assert.Contains("source_producer_module = 'soul-memory-adapter'", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE TRUNCATE", sql, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE OR REPLACE FUNCTION __SCHEMA__.commit_control_plane_atom(",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE OR REPLACE FUNCTION __SCHEMA__.append_control_plane_quarantine(",
            sql,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            sql.Split("SECURITY DEFINER", StringSplitOptions.None).Length - 1);
        Assert.Contains("SET search_path = pg_catalog", sql, StringComparison.Ordinal);
        Assert.Contains("IF session_user <> '__RUNTIME_ROLE__'", sql, StringComparison.Ordinal);
        Assert.Contains(
            "GRANT EXECUTE ON FUNCTION __SCHEMA__.commit_control_plane_atom(",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "GRANT EXECUTE ON FUNCTION __SCHEMA__.append_control_plane_quarantine(",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("octet_length(device_binding_id) = 35", sql, StringComparison.Ordinal);
        Assert.Contains("octet_length(platform_account_id) = 35", sql, StringComparison.Ordinal);
        Assert.Contains("octet_length(trace_id) = 38", sql, StringComparison.Ordinal);
        Assert.Contains("octet_length(idempotency_key) = 69", sql, StringComparison.Ordinal);
        Assert.Contains(
            "GRANT SELECT ON __SCHEMA__.runtime_truth TO __RUNTIME_ROLE__",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT SELECT, INSERT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT INSERT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT UPDATE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT DELETE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT TRUNCATE", sql, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract")]
    public void ProviderParserRejectsLeadingZeroMajorAndAcceptsFractionalUtc()
    {
        Assert.Throws<NotSupportedException>(() => ProviderResultAuthorization.Parse(
            Signed(BindingJson(schemaVersion: "01"))));
        Assert.Throws<NotSupportedException>(() => ProviderResultAuthorization.Parse(
            Signed(BindingJson(schemaVersion: "1.1.0"))));
        Assert.Throws<NotSupportedException>(() => ProviderResultAuthorization.Parse(
            Signed(PlatformJson("approval_x", schemaVersion: "1.1.0"))));

        var parsed = ProviderResultAuthorization.Parse(Signed(BindingJson(
            occurredAt: "2026-07-14T00:00:00.1234567Z")));

        Assert.Equal("1.0.0", parsed.Result.SchemaVersion);
        Assert.Equal(1_234_567, parsed.Result.OccurredAt.Ticks % TimeSpan.TicksPerSecond);
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(BindingJson(deviceBindingId: Binding + "\n"))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(BindingJson(platformAccountId: Account + "\n"))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(BindingJson(traceId: Trace + "\n"))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(BindingJson(idempotencyKey: Idempotency + "\n"))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(BindingJson(occurredAt: "2026-07-14T00:00:00Z\n"))));
    }

    [Fact, Trait("Category", "Contract")]
    public void ProviderSlugAcceptsDigitLeadingSegmentAndRejectsConsecutiveSeparators()
    {
        var parsed = ProviderResultAuthorization.Parse(Signed(DeviceJson(
            capabilities: ["2fa", "tap"])));
        Assert.Equal("registered", parsed.Result.ResultStatus);

        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(DeviceJson(capabilities: ["tap..double"]))));
    }

    [Fact, Trait("Category", "Contract")]
    public void ProviderOwnedSharedCorporaRemainConsumerCompatible()
    {
        AssertFlatProviderCorpus(
            "device.registered.v1.corpus.json",
            "device.registered/v1",
            ["valid-minimum-long-and-zulu", "valid-int64-max-seven-fractions-zero-offset"],
            [
                "invalid-version-termination", "invalid-id-trailing-newline",
                "invalid-nonzero-offset", "invalid-eight-fractional-digits",
                "invalid-int64-overflow", "invalid-year-zero", "invalid-leap-second",
                "invalid-int64-quoted-number"
            ]);
        AssertFlatProviderCorpus(
            "platform.account.authorized.v1.corpus.json",
            "platform.account.authorized/v1",
            [
                "valid-minimum-long-and-zulu",
                "valid-int64-max-seven-fractions-zero-offset"
            ],
            [
                "invalid-version-termination", "invalid-id-trailing-newline",
                "invalid-nonzero-offset", "invalid-eight-fractional-digits",
                "invalid-int64-overflow", "invalid-platform-trailing-newline",
                "invalid-platform-trailing-cr", "invalid-platform-leading-space",
                "invalid-platform-uppercase", "invalid-alias-key-trailing-newline",
                "invalid-alias-key-trailing-cr", "invalid-alias-key-space",
                "invalid-authorization-evidence-trailing-newline",
                "invalid-authorization-evidence-trailing-cr",
                "invalid-authorization-evidence-space", "invalid-extra-property",
                "invalid-year-zero", "invalid-leap-second",
                "invalid-alias-key-uppercase",
                "invalid-authorization-evidence-uppercase"
            ]);
        AssertFlatProviderCorpus(
            "identity.binding.v1.corpus.json",
            "identity.binding/v1",
            ["valid-minimum-long-and-zulu", "valid-int64-max-seven-fractions-zero-offset"],
            [
                "invalid-version-termination", "invalid-id-trailing-newline",
                "invalid-nonzero-offset", "invalid-eight-fractional-digits",
                "invalid-int64-overflow", "invalid-year-zero", "invalid-leap-second",
                "invalid-int64-quoted-number"
            ]);
        AssertPersonaProviderCorpus();
        AssertSoulMemoryReadbackCorpus();
        AssertGBrainSourceIdCorpus();
    }

    [Fact, Trait("Category", "Contract")]
    public void PlatformApprovalEvidenceRequiresNonemptyCanonicalSuffix()
    {
        var parsed = ProviderResultAuthorization.Parse(Signed(PlatformJson("approval_x")));
        Assert.Equal("authorized", parsed.Result.ResultStatus);
        Assert.Equal(
            "authorized",
            ProviderResultAuthorization.Parse(Signed(PlatformJson(
                "approval_x",
                platform: new string('a', 64)))).Result.ResultStatus);

        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PlatformJson("approval_"))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PlatformJson("approval_x", aliasKeyEpoch: 0))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PlatformJson("approval_x", platform: new string('a', 65)))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PlatformJson("approval_x", aliasKeyId: "Alias-key-v1"))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PlatformJson("approval_X"))));
        Assert.Equal(
            "authorized",
            ProviderResultAuthorization.Parse(Signed(PlatformJson(
                "approval_" + new string('a', 119),
                aliasKeyId: new string('a', 64)))).Result.ResultStatus);
    }

    [Fact, Trait("Category", "Contract")]
    public void DeletedPersonaRequiresEmptyTraitsAndArraysAreStrictlyOrdinal()
    {
        var deleted = ProviderResultAuthorization.Parse(Signed(PersonaJson(
            status: "deleted",
            traitKeys: [],
            evidence: [new string('e', 64), new string('f', 64)])));
        Assert.Equal("deleted", deleted.Result.ResultStatus);

        Assert.Throws<InvalidOperationException>(() => ProviderResultAuthorization.Parse(
            Signed(PersonaJson(
                status: "deleted",
                traitKeys: ["curiosity"],
                evidence: [new string('e', 64)]))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PersonaJson(
                status: "active",
                traitKeys: ["tone", "curiosity"],
                evidence: [new string('e', 64)]))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PersonaJson(
                status: "active",
                traitKeys: ["curiosity", "tone"],
                evidence: [new string('f', 64), new string('e', 64)]))));
        Assert.Throws<InvalidOperationException>(() => ProviderResultAuthorization.Parse(
            Signed(PersonaJson(
                status: "active",
                traitKeys: [],
                evidence: [new string('e', 64)]))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PersonaJson(
                status: "active",
                traitKeys: ["curiosity"],
                evidence: Enumerable.Range(0, 65)
                    .Select(static value => value.ToString("x64"))
                    .ToArray()))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(PersonaJson(
                status: "active",
                traitKeys: ["curiosity", "tone"],
                evidence: [new string('e', 64), new string('f', 64)],
                occurredAt: "2019-12-31T23:59:59Z"))));
    }

    [Fact, Trait("Category", "Contract")]
    public void SignedProviderEnvelopeRequiresCanonicalBoundedP256SignatureBase64()
    {
        var json = BindingJson();
        var parsed = ProviderResultAuthorization.Parse(Signed(json));
        Assert.Equal("identity.binding/v1", parsed.Result.SourceContractId);

        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(json, CanonicalZeroSignature.TrimEnd('='))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(json, Convert.ToBase64String(new byte[63]))));
        Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.Parse(
            Signed(json, new string('A', 8193))));

        var secp256k1Spki = Convert.FromHexString(
            "3056301006072a8648ce3d020106052b8104000a03420004"
            + "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"
            + "483ada7726a3c4655da4fbfc0e1108a8fd17b448a68554199c47d08ffb10d4b8");
        try
        {
            Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.ValidateTrustState(
                TrustStateForPublicKey(secp256k1Spki)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secp256k1Spki);
        }

        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var noncanonicalSpki = p256.ExportSubjectPublicKeyInfo()
            .Concat(new byte[] { 0x00 })
            .ToArray();
        try
        {
            Assert.Throws<ArgumentException>(() => ProviderResultAuthorization.ValidateTrustState(
                TrustStateForPublicKey(noncanonicalSpki)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(noncanonicalSpki);
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void ExecutionPromotionCodecIsStrictBoundedSnakeCaseAndShadowProposalIsNotAuthority()
    {
        AssertPromotionCorpusCodec();
        var promotion = Promotion();
        var payload = ActionExecutionPromotionV1Codec.Serialize(promotion);
        var json = Encoding.UTF8.GetString(payload);

        Assert.Contains("\"schema_version\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\"", json, StringComparison.Ordinal);
        Assert.Equal(promotion, ActionExecutionPromotionV1Codec.Deserialize(payload));
        Assert.Equal(
            "60436dfad7c0d18615afc7ff4fff90ba3ef01414742e8100275a6bb21b8b9579",
            ActionExecutionPromotionV1Canonical.ComputeSignedSha256(promotion));
        Assert.Equal(736, ActionExecutionPromotionV1Canonical.CanonicalBytes(promotion).Length);
        Assert.NotEqual(
            ActionExecutionPromotionV1Canonical.ComputeSignedSha256(promotion),
            ActionExecutionPromotionV1Canonical.ComputeSignedSha256(
                promotion with { ProposalSha256 = new string('c', 64) }));

        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(
                "\"schema_version\":\"1.0.0\",",
                "\"schema_version\":\"1.0.0\",\"schema_version\":\"1.0.0\",",
                StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(
                "\"auth_scope\":\"policy:promote\",",
                string.Empty,
                StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Insert(1, "\"unexpected\":true,"))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(
                promotion.PromotionId.ToString("D"),
                promotion.PromotionId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(
                promotion.PromotionId.ToString("D"),
                Guid.Empty.ToString("D"),
                StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("Z\"", "+00:00\"", StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(Binding, Binding + "\\n", StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json + "\n")));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace(
                "control-plane-host",
                "control-plane\\u002dhost",
                StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            [0x7b, 0x22, 0xff, 0x22, 0x7d]));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            new byte[ActionExecutionPromotionV1Codec.MaximumPayloadBytes + 1]));
        Assert.Throws<ArgumentException>(() => ActionExecutionPromotionV1Codec.Deserialize(
            Encoding.UTF8.GetBytes(
                "{\"schema_version\":\"1.0.0\",\"contract_id\":\"action.proposal/v1\"}")));
    }

    [Theory, Trait("Category", "Contract")]
    [InlineData("device_binding_id", "db_control_fixture")]
    [InlineData("platform_account_id", "pa_60123456789")]
    [InlineData("trace_id", "trace_bearer-token")]
    [InlineData("idempotency_key", "idem_user@example.com")]
    public void ProviderParserRejectsLooseTokenOrPhoneLikeCorrelationIds(
        string field,
        string invalidValue)
    {
        var json = field switch
        {
            "device_binding_id" => BindingJson(deviceBindingId: invalidValue),
            "platform_account_id" => BindingJson(platformAccountId: invalidValue),
            "trace_id" => BindingJson(traceId: invalidValue),
            "idempotency_key" => BindingJson(idempotencyKey: invalidValue),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Throws<ArgumentException>(() =>
            ProviderResultAuthorization.Parse(Signed(json)));
    }

    private static SignedProviderResultV1 Signed(
        string json,
        string? signatureBase64 = null)
        => new(
            ActiveReleaseBom,
            "provider-test-key:v1",
            Encoding.UTF8.GetBytes(json),
            signatureBase64 ?? CanonicalZeroSignature);

    private static SignedProviderResultV1 Signed(byte[] payload)
        => new(
            ActiveReleaseBom,
            "provider-test-key:v1",
            payload,
            CanonicalZeroSignature);

    private static ProviderTrustStateV1 TrustStateForPublicKey(byte[] publicKey)
        => new(
            1,
            "identity.binding/v1",
            "binding",
            ActiveReleaseBom,
            "provider-test-key:v1",
            Convert.ToBase64String(publicKey),
            Convert.ToHexStringLower(SHA256.HashData(publicKey)),
            "ACTIVE",
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static ActionExecutionPromotionV1 Promotion()
        => new(
            ActionExecutionPromotionV1.CurrentSchemaVersion,
            ActionExecutionPromotionV1.CurrentContractId,
            ActionExecutionPromotionV1.CurrentProducerModule,
            ActionExecutionPromotionV1.CurrentAuthScope,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Soul,
            Binding,
            Account,
            Trace,
            Idempotency,
            new string('a', 64),
            ActiveReleaseBom,
            7,
            Now,
            Now.AddMinutes(5),
            "internal",
            CanonicalZeroSignature);

    private static void AssertPromotionCorpusCodec()
    {
        var assembly = typeof(ActionExecutionPromotionV1).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith(
                "action.execution.promotion.v1.corpus.json",
                StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resourceName));
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        var cases = Assert.IsType<JsonArray>(root["cases"]);
        foreach (var caseNode in cases)
        {
            var contractCase = Assert.IsType<JsonObject>(caseNode);
            var instance = Assert.IsType<JsonObject>(baseline.DeepClone());
            var patch = Assert.IsType<JsonObject>(contractCase["patch"]);
            foreach (var pair in patch)
            {
                instance[pair.Key] = pair.Value?.DeepClone();
            }
            var remove = Assert.IsType<JsonArray>(contractCase["remove"]);
            foreach (var field in remove)
            {
                Assert.True(instance.Remove(
                    Assert.IsAssignableFrom<JsonValue>(field).GetValue<string>()));
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(instance);
            var codecValid = Assert.IsAssignableFrom<JsonValue>(contractCase["codecValid"])
                .GetValue<bool>();
            if (codecValid)
            {
                _ = ActionExecutionPromotionV1Codec.Deserialize(payload);
            }
            else
            {
                Assert.ThrowsAny<Exception>(() =>
                    ActionExecutionPromotionV1Codec.Deserialize(payload));
            }
        }
    }

    private static void AssertReceiptCorpusCodec()
    {
        var assembly = typeof(ControlPlaneReceiptV1).Assembly;
        Assert.Contains(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith(
                "control.plane.receipt.v1.schema.json",
                StringComparison.Ordinal));
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith(
                "control.plane.receipt.v1.corpus.json",
                StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resourceName));
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        var cases = Assert.IsType<JsonArray>(root["cases"]);
        foreach (var caseNode in cases)
        {
            var contractCase = Assert.IsType<JsonObject>(caseNode);
            var instance = Assert.IsType<JsonObject>(baseline.DeepClone());
            var patch = Assert.IsType<JsonObject>(contractCase["patch"]);
            foreach (var pair in patch)
            {
                instance[pair.Key] = pair.Value?.DeepClone();
            }
            var remove = Assert.IsType<JsonArray>(contractCase["remove"]);
            foreach (var field in remove)
            {
                Assert.True(instance.Remove(
                    Assert.IsAssignableFrom<JsonValue>(field).GetValue<string>()));
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(instance);
            var codecValid = Assert.IsAssignableFrom<JsonValue>(contractCase["codecValid"])
                .GetValue<bool>();
            if (codecValid)
            {
                _ = ControlPlaneReceiptV1Codec.Deserialize(payload);
            }
            else
            {
                Assert.ThrowsAny<Exception>(() =>
                    ControlPlaneReceiptV1Codec.Deserialize(payload));
            }
        }
    }

    private static void AssertFlatProviderCorpus(
        string resourceSuffix,
        string expectedContractId,
        string[] expectedValidCaseIds,
        string[] expectedInvalidCaseIds)
    {
        using var stream = OpenProviderCorpus(resourceSuffix);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.Equal(expectedContractId, root.GetProperty("contract_id").GetString());
        var valid = root.GetProperty("valid");
        var invalid = root.GetProperty("invalid");
        Assert.Equal(
            expectedValidCaseIds,
            valid.EnumerateArray()
                .Select(static corpusCase => corpusCase.GetProperty("id").GetString())
                .ToArray());
        Assert.Equal(
            expectedInvalidCaseIds,
            invalid.EnumerateArray()
                .Select(static corpusCase => corpusCase.GetProperty("id").GetString())
                .ToArray());
        foreach (var corpusCase in valid.EnumerateArray())
        {
            AssertConsumerCorpusCase(
                expectedContractId,
                Assert.IsType<string>(corpusCase.GetProperty("id").GetString()),
                Encoding.UTF8.GetBytes(corpusCase.GetProperty("payload").GetRawText()),
                expectedValid: true);
        }
        foreach (var corpusCase in invalid.EnumerateArray())
        {
            AssertConsumerCorpusCase(
                expectedContractId,
                Assert.IsType<string>(corpusCase.GetProperty("id").GetString()),
                Encoding.UTF8.GetBytes(corpusCase.GetProperty("payload").GetRawText()),
                expectedValid: false);
        }
    }

    private static void AssertSoulMemoryReadbackCorpus()
    {
        var root = LoadProviderCorpus("soul.memory.readback.v1.corpus.json");
        Assert.Equal("soul.memory.readback/v1", root["contractId"]?.GetValue<string>());
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        var cases = Assert.IsType<JsonArray>(root["cases"]);
        Assert.Equal(
            new[]
            {
                "valid", "valid-no-fraction", "additional-field", "missing-occurred-at",
                "wrong-revision-type", "duplicate-schema-version",
                "schema-version-terminal-newline", "projection-version-terminal-newline",
                "soul-terminal-newline", "device-terminal-newline",
                "account-terminal-newline", "trace-terminal-newline",
                "idempotency-terminal-newline", "source-terminal-newline",
                "wrong-but-well-formed-source", "nonzero-offset", "zero-offset-not-zulu",
                "eight-fractional-digits", "fractional-trailing-zero",
                "invalid-calendar-date", "uppercase-revision",
                "checksum-terminal-newline", "prepared-claims-readback",
                "verified-valid", "verified-checksum-mismatch"
            },
            cases.Select(static caseNode =>
                    Assert.IsType<JsonObject>(caseNode)["id"]?.GetValue<string>())
                .ToArray());
        var acceptedCases = 0;
        foreach (var caseNode in cases)
        {
            var corpusCase = Assert.IsType<JsonObject>(caseNode);
            var instance = ApplyPatchCorpusCase(baseline, corpusCase);
            var json = JsonSerializer.Serialize(instance);
            if (corpusCase["rawReplace"] is JsonObject rawReplace)
            {
                var oldValue = Assert.IsAssignableFrom<JsonValue>(rawReplace["old"]).GetValue<string>();
                var newValue = Assert.IsAssignableFrom<JsonValue>(rawReplace["new"]).GetValue<string>();
                Assert.Contains(oldValue, json, StringComparison.Ordinal);
                json = json.Replace(oldValue, newValue, StringComparison.Ordinal);
            }
            var expectedValid = Assert.IsAssignableFrom<JsonValue>(corpusCase["controlPlaneValid"])
                .GetValue<bool>();
            if (expectedValid)
            {
                acceptedCases++;
                Assert.Equal("verified-valid", corpusCase["id"]?.GetValue<string>());
            }
            AssertConsumerCorpusCase(
                "soul.memory.readback/v1",
                Assert.IsAssignableFrom<JsonValue>(corpusCase["id"]).GetValue<string>(),
                Encoding.UTF8.GetBytes(json),
                expectedValid);
        }
        Assert.Equal(1, acceptedCases);
    }

    private static void AssertPersonaProviderCorpus()
    {
        using var stream = OpenProviderCorpus("persona.revision.v1.corpus.json");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.Equal("persona.revision/v1", root.GetProperty("contract_id").GetString());
        var cases = root.GetProperty("cases");
        Assert.Equal(
            new[]
            {
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
            },
            cases.EnumerateArray()
                .Select(static corpusCase => corpusCase.GetProperty("id").GetString())
                .ToArray());
        foreach (var corpusCase in cases.EnumerateArray())
        {
            AssertConsumerCorpusCase(
                "persona.revision/v1",
                Assert.IsType<string>(corpusCase.GetProperty("id").GetString()),
                Encoding.UTF8.GetBytes(Assert.IsType<string>(
                    corpusCase.GetProperty("raw_json").GetString())),
                corpusCase.GetProperty("valid").GetBoolean());
        }
    }

    private static void AssertGBrainSourceIdCorpus()
    {
        var sourceCorpus = LoadProviderCorpus("gbrain.source-id.v1.corpus.json");
        Assert.Equal("gbrain.source-id/v1", sourceCorpus["contractId"]?.GetValue<string>());
        var sourceCases = Assert.IsType<JsonArray>(sourceCorpus["cases"]);
        Assert.Equal(
            new[]
            {
                "valid-a", "valid-b", "collision-left", "collision-right",
                "uppercase-soul", "terminal-newline", "short-soul"
            },
            sourceCases.Select(static caseNode =>
                    Assert.IsType<JsonObject>(caseNode)["id"]?.GetValue<string>())
                .ToArray());
        var readbackCorpus = LoadProviderCorpus("soul.memory.readback.v1.corpus.json");
        var baseline = Assert.IsType<JsonObject>(readbackCorpus["base"]);
        var collisionSources = new List<string>();
        foreach (var caseNode in sourceCases)
        {
            var sourceCase = Assert.IsType<JsonObject>(caseNode);
            var id = Assert.IsAssignableFrom<JsonValue>(sourceCase["id"]).GetValue<string>();
            var soulId = Assert.IsAssignableFrom<JsonValue>(sourceCase["soul_id"]).GetValue<string>();
            var expectedValid = Assert.IsAssignableFrom<JsonValue>(sourceCase["valid"]).GetValue<bool>();
            var payload = Assert.IsType<JsonObject>(baseline.DeepClone());
            payload["soul_id"] = soulId;
            payload["status"] = "verified";
            payload["readback_checksum"] = payload["projection_checksum"]?.DeepClone();
            if (expectedValid)
            {
                var sourceId = Assert.IsAssignableFrom<JsonValue>(sourceCase["expected_source_id"])
                    .GetValue<string>();
                Assert.Equal("dps-" + soulId.AsSpan(5, 28).ToString(), sourceId);
                payload["source_id"] = sourceId;
                if (sourceCase["collision_group"] is JsonValue collisionGroup
                    && collisionGroup.GetValue<string>() == "same-prefix-112-bit")
                {
                    collisionSources.Add(sourceId);
                }
            }
            AssertConsumerCorpusCase(
                "gbrain.source-id/v1",
                id,
                JsonSerializer.SerializeToUtf8Bytes(payload),
                expectedValid);
        }
        Assert.Equal(2, collisionSources.Count);
        Assert.Single(collisionSources.Distinct(StringComparer.Ordinal));
    }

    private static JsonObject ApplyPatchCorpusCase(
        JsonObject baseline,
        JsonObject corpusCase)
    {
        var instance = Assert.IsType<JsonObject>(baseline.DeepClone());
        var patch = Assert.IsType<JsonObject>(corpusCase["patch"]);
        foreach (var pair in patch)
        {
            instance[pair.Key] = pair.Value?.DeepClone();
        }
        foreach (var fieldNode in Assert.IsType<JsonArray>(corpusCase["remove"]))
        {
            Assert.True(instance.Remove(
                Assert.IsAssignableFrom<JsonValue>(fieldNode).GetValue<string>()));
        }
        return instance;
    }

    private static void AssertConsumerCorpusCase(
        string contractId,
        string caseId,
        byte[] payload,
        bool expectedValid)
    {
        var error = Record.Exception(() => ProviderResultAuthorization.Parse(Signed(payload)));
        if (expectedValid)
        {
            Assert.True(
                error is null,
                $"Provider corpus case {contractId}/{caseId} unexpectedly failed: {error}");
        }
        else
        {
            Assert.True(
                error is not null,
                $"Provider corpus case {contractId}/{caseId} unexpectedly passed.");
        }
    }

    private static JsonObject LoadProviderCorpus(string resourceSuffix)
    {
        using var stream = OpenProviderCorpus(resourceSuffix);
        return Assert.IsType<JsonObject>(JsonNode.Parse(stream));
    }

    private static Stream OpenProviderCorpus(string resourceSuffix)
    {
        var assembly = typeof(ControlPlaneTruthStoreTests).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));
        return Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resourceName));
    }

    private static string BindingJson(
        string schemaVersion = "1.0.0",
        string occurredAt = "2026-07-14T00:00:00Z",
        string deviceBindingId = Binding,
        string platformAccountId = Account,
        string traceId = Trace,
        string idempotencyKey = Idempotency)
    {
        var values = CommonProviderFields(
            schemaVersion,
            "identity.binding/v1",
            "binding",
            occurredAt,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey);
        values["privacy_class"] = "sensitive";
        values["device_id"] = "device_" + new string('1', 32);
        values["binding_revision"] = 1;
        values["status"] = "active";
        values["device_registration_revision"] = 1;
        values["account_authorization_revision"] = 1;
        return JsonSerializer.Serialize(values);
    }

    private static string DeviceJson(IReadOnlyList<string> capabilities)
    {
        var values = CommonProviderFields(
            "1.0.0",
            "device.registered/v1",
            "device-registry",
            "2026-07-14T00:00:00Z",
            Binding,
            Account,
            Trace,
            Idempotency);
        values["privacy_class"] = "sensitive";
        values["device_id"] = "device_" + new string('2', 32);
        values["fingerprint_hmac_sha256"] = new string('3', 64);
        values["fingerprint_key_id"] = "fpkey_" + new string('4', 32);
        values["fingerprint_key_epoch"] = 1;
        values["capability_revision"] = 1;
        values["capabilities"] = capabilities;
        values["status"] = "registered";
        return JsonSerializer.Serialize(values);
    }

    private static string PlatformJson(
        string approvalEvidenceId,
        int aliasKeyEpoch = 1,
        string schemaVersion = "1.0.0",
        string platform = "2fa.platform",
        string aliasKeyId = "alias-key-v1")
    {
        var values = CommonProviderFields(
            schemaVersion,
            "platform.account.authorized/v1",
            "platform-account-registry",
            "2026-07-14T00:00:00Z",
            Binding,
            Account,
            Trace,
            Idempotency);
        values["privacy_class"] = "sensitive";
        values["platform"] = platform;
        values["alias_digest"] = new string('4', 64);
        values["alias_key_id"] = aliasKeyId;
        values["alias_key_epoch"] = aliasKeyEpoch;
        values["authorization_evidence_id"] = approvalEvidenceId;
        values["authorization_revision"] = 1;
        values["status"] = "authorized";
        return JsonSerializer.Serialize(values);
    }

    private static string PersonaJson(
        string status,
        IReadOnlyList<string> traitKeys,
        IReadOnlyList<string> evidence,
        string occurredAt = "2026-07-14T00:00:00Z")
    {
        var values = CommonProviderFields(
            "1.0.0",
            "persona.revision/v1",
            "persona-store",
            occurredAt,
            Binding,
            Account,
            Trace,
            Idempotency);
        values["privacy_class"] = "personal";
        values["persona_revision"] = 1;
        values["traits_sha256"] = new string('5', 64);
        values["trait_keys"] = traitKeys;
        values["evidence_sha256"] = evidence;
        values["status"] = status;
        return JsonSerializer.Serialize(values);
    }

    private static Dictionary<string, object?> CommonProviderFields(
        string schemaVersion,
        string contractId,
        string producerModule,
        string occurredAt,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey)
        => new(StringComparer.Ordinal)
        {
            ["schema_version"] = schemaVersion,
            ["contract_id"] = contractId,
            ["producer_module"] = producerModule,
            ["soul_id"] = Soul,
            ["device_binding_id"] = deviceBindingId,
            ["platform_account_id"] = platformAccountId,
            ["trace_id"] = traceId,
            ["idempotency_key"] = idempotencyKey,
            ["occurred_at"] = occurredAt
        };
}

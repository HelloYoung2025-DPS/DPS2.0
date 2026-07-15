using Dps.CommandOrchestrator.Contracts;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Dps.CommandOrchestrator.Tests;

public sealed class ExecutionAuthorizationContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void PolicySignerPortIsVersionedKeyBoundAndRawIssuedAuthorizationEntryPointIsNotPublic()
    {
        Assert.Equal(
            "dps.policy-approval.execution-authorization-signer/v1",
            IPolicyExecutionAuthorizationSignerV1.CurrentProtocolId);
        Assert.Equal("policy-approval", IPolicyExecutionAuthorizationSignerV1.CurrentSignerModule);
        Assert.Equal("command-orchestrator", ExecutionAuthorizationV1.CurrentProducerModule);

        var publicIssue = typeof(PostgresCommandOrchestrator).GetMethod(
            nameof(PostgresCommandOrchestrator.IssueAndMarkDispatchedAsync));
        Assert.NotNull(publicIssue);
        Assert.True(publicIssue!.IsPublic);
        Assert.Null(typeof(PostgresCommandOrchestrator).GetMethod(
            "MarkDispatchedAsync",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        Assert.NotNull(typeof(PostgresCommandOrchestrator).GetMethod(
            "MarkDispatchedAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));

        using var authorizationAnchor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var receiptAnchor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorizationSpki = authorizationAnchor.ExportSubjectPublicKeyInfo();
        var receiptSpki = receiptAnchor.ExportSubjectPublicKeyInfo();
        var capability = RandomNumberGenerator.GetBytes(32);
        try
        {
            var keyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(authorizationSpki));
            var options = new PostgresCommandOrchestratorOptions(
                "Host=localhost;Database=dps_contract;Username=cmd_migrator;Password=x;Pooling=false",
                "Host=localhost;Database=dps_contract;Username=cmd_runtime;Password=y;Pooling=false",
                "command_orchestrator",
                "cmd_migrator",
                "cmd_runtime");
            using var valid = new PostgresCommandOrchestrator(
                options,
                new ContractSignerPort(
                    IPolicyExecutionAuthorizationSignerV1.CurrentProtocolId,
                    IPolicyExecutionAuthorizationSignerV1.CurrentSignerModule,
                    keyId),
                authorizationSpki,
                receiptSpki,
                capability);
            Assert.Throws<ArgumentException>(() => new PostgresCommandOrchestrator(
                options,
                new ContractSignerPort("dps.policy-approval.execution-authorization-signer/v2",
                    IPolicyExecutionAuthorizationSignerV1.CurrentSignerModule, keyId),
                authorizationSpki, receiptSpki, capability));
            Assert.Throws<ArgumentException>(() => new PostgresCommandOrchestrator(
                options,
                new ContractSignerPort(IPolicyExecutionAuthorizationSignerV1.CurrentProtocolId,
                    "command-orchestrator", keyId),
                authorizationSpki, receiptSpki, capability));
            Assert.Throws<ArgumentException>(() => new PostgresCommandOrchestrator(
                options,
                new ContractSignerPort(IPolicyExecutionAuthorizationSignerV1.CurrentProtocolId,
                    IPolicyExecutionAuthorizationSignerV1.CurrentSignerModule,
                    "sha256:" + new string('0', 64)),
                authorizationSpki, receiptSpki, capability));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authorizationSpki);
            CryptographicOperations.ZeroMemory(receiptSpki);
            CryptographicOperations.ZeroMemory(capability);
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Device = "db_11111111111111111111111111111111";
    private const string Account = "pa_22222222222222222222222222222222";
    private const string Trace = "trace_33333333333333333333333333333333";
    private const string Idempotency = "idem_4444444444444444444444444444444444444444444444444444444444444444";
    private const string OtherTrace = "trace_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherIdempotency = "idem_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    [Trait("Category", "Contract")]
    public void AuthorizationContractRejectsUnknownEncodingAlgorithmFormatAndSignatureShape()
    {
        var authorization = Authorization(Command());
        authorization.Validate();

        Assert.Throws<NotSupportedException>(() => (authorization with { SchemaVersion = "2.0.0" }).Validate());
        Assert.Throws<NotSupportedException>(() => (authorization with { SchemaVersion = "1.0.0\n" }).Validate());
        Assert.Throws<NotSupportedException>(() => (authorization with { CanonicalEncoding = "json-jcs" }).Validate());
        Assert.Throws<NotSupportedException>(() => (authorization with { SignatureAlgorithm = "rsa-pss-sha256" }).Validate());
        Assert.Throws<NotSupportedException>(() => (authorization with { SignatureFormat = "asn1-der" }).Validate());
        Assert.Throws<ArgumentException>(() => (authorization with { SignatureBase64 = Convert.ToBase64String(new byte[63]) }).Validate());
        Assert.Throws<ArgumentException>(() => (authorization with { SignatureBase64 = " " + authorization.SignatureBase64 }).Validate());
        Assert.Throws<EncoderFallbackException>(() => ExecutionAuthorizationProtocolV1.CanonicalCommandBytes(Command() with { LeaseOwner = "\uD800" }));
        Assert.Throws<EncoderFallbackException>(() => ExecutionAuthorizationProtocolV1.CanonicalCommandBytes(Command() with { LeaseOwner = "\uD801" }));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void OpaqueIdBaselinesRejectPiiTokenAndDelimiterShapesAndBindPayload()
    {
        var first = Command();
        var second = Command() with { TraceId = OtherTrace, IdempotencyKey = OtherIdempotency };
        Assert.NotEqual(ExecutionAuthorizationProtocolV1.ComputeCommandSha256(first), ExecutionAuthorizationProtocolV1.ComputeCommandSha256(second));
        Assert.NotEqual(ExecutionAuthorizationProtocolV1.ComputeCommandSha256(first), ExecutionAuthorizationProtocolV1.ComputeCommandSha256(first with { ApprovalSha256 = new string('e', 64) }));
        Assert.Throws<ArgumentException>(() => (first with { DeviceBindingId = "db_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { PlatformAccountId = "pa_user@example.com" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { TraceId = "trace|segment" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { IdempotencyKey = "Bearer secret-token" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { DeviceBindingId = Device + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { PlatformAccountId = Account + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { TraceId = Trace + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { IdempotencyKey = Idempotency + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (Authorization(first) with { DeviceBindingId = Device + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (Authorization(first) with { PlatformAccountId = Account + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (Authorization(first) with { TraceId = Trace + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (Authorization(first) with { IdempotencyKey = Idempotency + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { ApprovalSha256 = string.Empty }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void AuthorizationCanonicalBytesExcludeSignatureAndBindEveryScopeField()
    {
        var authorization = Authorization(Command());
        var canonical = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(authorization);
        var samePayloadDifferentSignature = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(authorization with { SignatureBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) });
        var differentTrace = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(authorization with { TraceId = OtherTrace });

        Assert.Equal(canonical, samePayloadDifferentSignature);
        Assert.NotEqual(Convert.ToHexString(canonical), Convert.ToHexString(differentTrace));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void MachineCanonicalSpecGoldenBytesDigestAndSignatureAreCrossLanguageStable()
    {
        using var spec = ReadEmbeddedJson("Dps.CommandOrchestrator.Contracts.execution.authorization.v1.canonical.json");
        var golden = spec.RootElement.GetProperty("goldenVector");
        var command = Command();
        var authorization = Authorization(command) with { SignatureBase64 = golden.GetProperty("signatureBase64").GetString()! };

        var commandBytes = ExecutionAuthorizationProtocolV1.CanonicalCommandBytes(command);
        var authorizationBytes = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(authorization);
        try
        {
            Assert.Equal(golden.GetProperty("commandCanonicalBase64").GetString(), Convert.ToBase64String(commandBytes));
            Assert.Equal(golden.GetProperty("commandSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(commandBytes)));
            Assert.Equal(golden.GetProperty("authorizationCanonicalBase64").GetString(), Convert.ToBase64String(authorizationBytes));
            Assert.Equal(golden.GetProperty("authorizationSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(authorizationBytes)));

            using var verifier = ECDsa.Create();
            var publicKey = Convert.FromBase64String(golden.GetProperty("signerPublicKeySpkiBase64").GetString()!);
            var signature = Convert.FromBase64String(golden.GetProperty("signatureBase64").GetString()!);
            try
            {
                verifier.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
                Assert.Equal(publicKey.Length, bytesRead);
                Assert.True(verifier.VerifyData(authorizationBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
            finally { CryptographicOperations.ZeroMemory(publicKey); CryptographicOperations.ZeroMemory(signature); }
        }
        finally { CryptographicOperations.ZeroMemory(commandBytes); CryptographicOperations.ZeroMemory(authorizationBytes); }

        using var schema = ReadEmbeddedJson("Dps.CommandOrchestrator.Contracts.execution.authorization.v1.schema.json");
        var binding = schema.RootElement.GetProperty("x-dps-canonical-spec");
        Assert.Equal("execution.authorization.v1.canonical.json", binding.GetProperty("resource").GetString());
        var canonicalSpecBytes = ReadEmbeddedBytes("Dps.CommandOrchestrator.Contracts.execution.authorization.v1.canonical.json");
        try { Assert.Equal(binding.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(canonicalSpecBytes))); }
        finally { CryptographicOperations.ZeroMemory(canonicalSpecBytes); }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SignedReceiptSchemaRequiresAndConstrainsThePublicEnvelopeScope()
    {
        using var schema = ReadEmbeddedJson("Dps.CommandOrchestrator.Contracts.command.receipt.signed.v1.schema.json");
        var root = schema.RootElement;
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var required = root.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var publicScope = new[]
        {
            "receipt_id", "command_id", "lease_id", "attempt", "soul_id", "device_binding_id",
            "platform_account_id", "trace_id", "idempotency_key", "occurred_at", "privacy_class"
        };
        Assert.All(publicScope, field => Assert.Contains(field, required));

        var properties = root.GetProperty("properties");
        Assert.Equal("^soul_[a-f0-9]{64}$(?![\\s\\S])", properties.GetProperty("soul_id").GetProperty("pattern").GetString());
        Assert.Equal("^db_[a-f0-9]{32}$(?![\\s\\S])", properties.GetProperty("device_binding_id").GetProperty("pattern").GetString());
        Assert.Equal("^pa_[a-f0-9]{32}$(?![\\s\\S])", properties.GetProperty("platform_account_id").GetProperty("pattern").GetString());
        Assert.Equal("^trace_[a-f0-9]{32}$(?![\\s\\S])", properties.GetProperty("trace_id").GetProperty("pattern").GetString());
        Assert.Equal("^idem_[a-f0-9]{64}$(?![\\s\\S])", properties.GetProperty("idempotency_key").GetProperty("pattern").GetString());
        Assert.Equal(1, properties.GetProperty("attempt").GetProperty("minimum").GetInt32());
        Assert.Equal(3, properties.GetProperty("attempt").GetProperty("maximum").GetInt32());
        Assert.Equal("internal", properties.GetProperty("privacy_class").GetProperty("const").GetString());
        Assert.Equal("date-time", properties.GetProperty("occurred_at").GetProperty("format").GetString());
        Assert.Equal("^[A-Za-z0-9+/]{86}==$(?![\\s\\S])", properties.GetProperty("signature_base64").GetProperty("pattern").GetString());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void RequiredSecurityContractTestIdsArePresent()
    {
        using var inventory = ReadEmbeddedJson("Dps.CommandOrchestrator.Tests.required-security-tests.v1.json", typeof(ExecutionAuthorizationContractTests).Assembly);
        Assert.Equal("dps.required-test-ids/v1", inventory.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("command-orchestrator.contract", inventory.RootElement.GetProperty("suiteId").GetString());
        var required = inventory.RootElement.GetProperty("requiredTestIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(required.Length, required.Distinct(StringComparer.Ordinal).Count());
        var actual = typeof(ExecutionAuthorizationContractTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null && method.DeclaringType is not null)
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(required, testId => Assert.Contains(testId, actual));
    }

    private static JsonDocument ReadEmbeddedJson(string resourceName)
    {
        return ReadEmbeddedJson(resourceName, typeof(ExecutionAuthorizationV1).Assembly);
    }

    private static JsonDocument ReadEmbeddedJson(string resourceName, Assembly assembly)
    {
        var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream);
    }

    private static byte[] ReadEmbeddedBytes(string resourceName)
    {
        using var stream = typeof(ExecutionAuthorizationV1).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static CommandDispatchV1 Command() => new(
        CommandDispatchV1.CurrentSchemaVersion, CommandDispatchV1.CurrentContractId, CommandDispatchV1.CurrentProducerModule,
        Guid.Parse("71000000-0000-0000-0000-000000000001"), Guid.Parse("72000000-0000-0000-0000-000000000002"), Guid.Parse("73000000-0000-0000-0000-000000000003"), new string('d', 64),
        Soul, Device, Account, Trace, Idempotency, Now.AddSeconds(-2), "internal", "observe", false, null,
        Guid.Parse("74000000-0000-0000-0000-000000000004"), "worker-a", Now.AddMinutes(1), 1,
        [new CommandStepV1(Guid.Parse("75000000-0000-0000-0000-000000000005"), "ui.observe", new Dictionary<string, string>(), true, "native-read-complete")]);

    private static ExecutionAuthorizationV1 Authorization(CommandDispatchV1 command) => new(
        ExecutionAuthorizationV1.CurrentSchemaVersion, ExecutionAuthorizationV1.CurrentContractId, ExecutionAuthorizationV1.CurrentProducerModule,
        ExecutionAuthorizationV1.CurrentSignatureDomain, ExecutionAuthorizationV1.CurrentCanonicalEncoding, ExecutionAuthorizationV1.CurrentCommandDigestAlgorithm,
        ExecutionAuthorizationV1.CurrentSignatureAlgorithm, ExecutionAuthorizationV1.CurrentSignatureFormat, ExecutionAuthorizationV1.CurrentSignatureEncoding,
        ExecutionAuthorizationV1.CurrentCallerModule, ExecutionAuthorizationV1.CurrentAuthScope, command.CommandId, command.LeaseId, command.Attempt,
        command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.TraceId, command.IdempotencyKey, Now.AddSeconds(-1), "internal",
        ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command), new string('a', 64), 7, new string('b', 64), Now.AddSeconds(30), false,
        Convert.ToBase64String(new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes]));

    private sealed record ContractSignerPort(
        string ProtocolId,
        string SignerModule,
        string KeyId) : IPolicyExecutionAuthorizationSignerV1
    {
        public ValueTask<ExecutionAuthorizationV1> SignAsync(
            ExecutionAuthorizationV1 unsignedAuthorization,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ExecutionAuthorizationV1>(
                new InvalidOperationException("Contract-only signer must never be invoked."));
    }
}

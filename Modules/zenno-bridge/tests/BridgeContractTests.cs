using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.ZennoBridge;
using Xunit;

namespace Dps.ZennoBridge.Tests;

public sealed class BridgeContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    [Trait("EvidenceKind", "CONTRACT")]
    public void Production_wire_output_satisfies_owned_schema_envelope()
    {
        var exchange = new BridgeExchange
        {
            SchemaVersion = "1.0",
            ContractId = BridgeProtocolValidator.ExchangeContract,
            ProducerModule = BridgeProtocolValidator.ExchangeProducer,
            SoulId = "soul_" + new string('a', 64),
            DeviceBindingId = "db_" + new string('b', 32),
            PlatformAccountId = "pa_" + new string('c', 32),
            TraceId = "trace_" + new string('d', 32),
            IdempotencyKey = "idem_" + new string('e', 64),
            OccurredAt = "2026-07-14T00:00:00Z",
            PrivacyClass = "personal",
            AuthNonce = new string('b', 64),
            ExchangeKind = "NATIVE_RESULT",
            CommandId = "command-contract-0001",
            ActionKind = "VERIFY",
            StepKind = "VERIFY_POSTCONDITION",
            Selector = "fixture:state",
            Text = null,
            WaitMs = null,
            ExpectedPostcondition = "fixture-visible",
            NativeStatus = "SUCCESS",
            NativeDetail = "verified",
            PostconditionVerified = true
        };

        BridgeProtocolValidator.ValidateExchange(exchange);

        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(BridgeExchange)).WriteObject(stream, exchange);
        var json = Encoding.UTF8.GetString(stream.ToArray());
        using var instance = JsonDocument.Parse(json);
        using var schema = JsonDocument.Parse(File.ReadAllText(FindSchema()));

        var required = schema.RootElement.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        var properties = schema.RootElement.GetProperty("properties");
        var produced = instance.RootElement.EnumerateObject()
            .Select(item => item.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(required.SetEquals(produced));
        Assert.Equal(properties.EnumerateObject().Count(), produced.Count);
        Assert.All(produced, name => Assert.True(properties.TryGetProperty(name, out _), name));
        Assert.Equal(
            properties.GetProperty("contract_id").GetProperty("const").GetString(),
            instance.RootElement.GetProperty("contract_id").GetString());
        Assert.Equal(
            properties.GetProperty("producer_module").GetProperty("const").GetString(),
            instance.RootElement.GetProperty("producer_module").GetString());
        Assert.Matches(
            new Regex(properties.GetProperty("soul_id").GetProperty("pattern").GetString()!),
            instance.RootElement.GetProperty("soul_id").GetString()!);
        Assert.Matches(
            new Regex(properties.GetProperty("device_binding_id").GetProperty("pattern").GetString()!),
            instance.RootElement.GetProperty("device_binding_id").GetString()!);
        Assert.Matches(
            new Regex(properties.GetProperty("platform_account_id").GetProperty("pattern").GetString()!),
            instance.RootElement.GetProperty("platform_account_id").GetString()!);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("EvidenceKind", "CONTRACT")]
    public void Runtime_rejects_offset_newline_pollution_and_mismatched_directive_truth()
    {
        var poll = ValidPoll();
        BridgeProtocolValidator.ValidateExchange(poll);

        Assert.Throws<BridgeProtocolException>(() =>
            BridgeProtocolValidator.ValidateExchange(ValidPollWith(occurredAt: "2026-07-14T08:00:00+08:00")));
        Assert.Throws<BridgeProtocolException>(() =>
            BridgeProtocolValidator.ValidateExchange(ValidPollWith(authNonce: new string('2', 64) + "\n")));
        Assert.Throws<BridgeProtocolException>(() =>
            BridgeProtocolValidator.ValidateExchange(ValidPollWith(commandId: "poll-must-not-carry-command")));

        var directive = ValidDirective();
        BridgeProtocolValidator.ValidateDirective(directive, poll);
        directive.StepKind = "TYPE_TEXT";
        Assert.Throws<BridgeProtocolException>(() =>
            BridgeProtocolValidator.ValidateDirective(directive, poll));
        directive = ValidDirective();
        directive.AuthProof += "\n";
        Assert.Throws<BridgeProtocolException>(() =>
            BridgeProtocolValidator.ValidateDirective(directive, poll));

        directive = ValidDirective();
        directive.IdempotencyKey = "idem_" + new string('f', 64);
        Assert.Throws<BridgeProtocolException>(() =>
            BridgeProtocolValidator.ValidateDirective(directive, poll));

        directive = ValidDirective();
        directive.PrivacyClass = "sensitive";
        Assert.Throws<BridgeProtocolException>(() =>
            BridgeProtocolValidator.ValidateDirective(directive, poll));

        foreach (var kind in new[] { "WAIT", "ACK" })
        {
            directive = ValidDirective();
            directive.DirectiveKind = kind;
            Assert.Throws<BridgeProtocolException>(() =>
                BridgeProtocolValidator.ValidateDirective(directive, poll));
        }
    }

    private static BridgeExchange ValidPollWith(
        string occurredAt = null,
        string authNonce = null,
        string commandId = null)
    {
        var poll = ValidPoll();
        poll.OccurredAt = occurredAt ?? poll.OccurredAt;
        poll.AuthNonce = authNonce ?? poll.AuthNonce;
        poll.CommandId = commandId;
        return poll;
    }

    private static BridgeExchange ValidPoll() => new()
    {
        SchemaVersion = "1.0",
        ContractId = BridgeProtocolValidator.ExchangeContract,
        ProducerModule = BridgeProtocolValidator.ExchangeProducer,
        SoulId = "soul_" + new string('a', 64),
        DeviceBindingId = "db_" + new string('b', 32),
        PlatformAccountId = "pa_" + new string('c', 32),
        TraceId = "trace_" + new string('d', 32),
        IdempotencyKey = "idem_" + new string('e', 64),
        OccurredAt = "2026-07-14T00:00:00Z",
        PrivacyClass = "personal",
        AuthNonce = new string('2', 64),
        ExchangeKind = "POLL",
        CommandId = null,
        ActionKind = null,
        StepKind = null,
        Selector = null,
        Text = null,
        WaitMs = null,
        ExpectedPostcondition = null,
        NativeStatus = null,
        NativeDetail = null,
        PostconditionVerified = null
    };

    private static BridgeDirective ValidDirective() => new()
    {
        SchemaVersion = "1.0",
        ContractId = BridgeProtocolValidator.DirectiveContract,
        ProducerModule = BridgeProtocolValidator.DirectiveProducer,
        SoulId = "soul_" + new string('a', 64),
        DeviceBindingId = "db_" + new string('b', 32),
        PlatformAccountId = "pa_" + new string('c', 32),
        TraceId = "trace_" + new string('d', 32),
        IdempotencyKey = "idem_" + new string('e', 64),
        OccurredAt = "2026-07-14T00:00:00Z",
        PrivacyClass = "personal",
        AuthKeyId = "sha256_" + new string('1', 64),
        AuthNonce = new string('2', 64),
        AuthIssuedAt = "2026-07-14T00:00:00Z",
        AuthBodySha256 = new string('3', 64),
        AuthProof = new string('A', 64),
        DirectiveKind = "COMMAND",
        CommandId = "command-1",
        ActionKind = "TAP",
        StepKind = "TAP_SELECTOR",
        Selector = "fixture:button",
        Text = null,
        WaitMs = null,
        ExpectedPostcondition = "fixture changed"
    };

    private static string FindSchema()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "Modules",
                "zenno-bridge",
                "contracts",
                "provided",
                "edge.bridge.exchange.v1.schema.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException("Owned edge.bridge.exchange/v1 schema was not found.");
    }
}

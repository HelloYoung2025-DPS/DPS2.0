using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeWorker;
using Xunit;

namespace Dps.WindowsEdgeWorker.Tests;

public sealed class WorkerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Duplicate_delivery_returns_recorded_receipt_without_second_side_effect()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeTransport(new NativeDispatchResult(true, NativeStatus.Success, true, "verified"));
        var processor = Processor(transport);
        var command = Command();

        var first = await processor.ProcessAsync(command, token);
        var duplicate = await processor.ProcessAsync(command, token);

        Assert.Equal("VERIFIED_SUCCESS", first.ResultStatus);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Newline_partition_collision_is_distinct_and_conflicting_duplicate_is_quarantined()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeTransport(new NativeDispatchResult(true, NativeStatus.Success, true, "verified"));
        var processor = Processor(transport);
        var first = WithHash(Command() with
        {
            Selector = "fixture:button\nsecondary",
            Text = "tail"
        });
        var conflicting = WithHash(Command() with
        {
            Selector = "fixture:button",
            Text = "secondary\ntail"
        });

        Assert.Equal(
            string.Join("\n", first.Selector, first.Text),
            string.Join("\n", conflicting.Selector, conflicting.Text));
        Assert.NotEqual(first.RequestSha256, conflicting.RequestSha256);
        Assert.Equal(
            "f5f4d6842826b7290aa739948169e25fdee7750a0c5e4659fbc850b9175d962f",
            first.RequestSha256);

        Assert.Equal("VERIFIED_SUCCESS", (await processor.ProcessAsync(first, token)).ResultStatus);
        var duplicate = await processor.ProcessAsync(first, token);
        var conflict = await processor.ProcessAsync(conflicting, token);

        Assert.True(duplicate.Duplicate);
        Assert.Equal("QUARANTINED", conflict.ResultStatus);
        Assert.False(conflict.RetryAllowed);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Request_hash_has_a_domain_separated_deterministic_golden_vector()
    {
        var command = Command();

        Assert.Equal(
            "2b712548afe5d1fd37e842ef420d277dd874ad76b4dcf11722e6165a20aacf0d",
            command.RequestSha256);
        Assert.Equal(command.RequestSha256, CommandHasher.Compute(command));
        Assert.NotEqual(
            CommandHasher.Compute(command with { Text = null }),
            CommandHasher.Compute(command with { Text = "<null>" }));
        Assert.NotEqual(
            CommandHasher.Compute(command with { Text = null }),
            CommandHasher.Compute(command with { Text = string.Empty }));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Request_hash_is_culture_independent_and_rejects_noncanonical_time_offsets()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var command = Command();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");

            Assert.Equal(command.RequestSha256, CommandHasher.Compute(command));
            Assert.Throws<InvalidDataException>(() => CommandHasher.Compute(command with
            {
                OccurredAt = command.OccurredAt!.Value.ToOffset(TimeSpan.FromHours(8))
            }));
            Assert.Throws<InvalidDataException>(() => CommandHasher.Compute(command with
            {
                LeaseExpiresAt = command.LeaseExpiresAt!.Value.ToOffset(TimeSpan.FromHours(8))
            }));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Request_hash_rejects_invalid_unicode()
    {
        Assert.Throws<EncoderFallbackException>(() =>
            CommandHasher.Compute(Command() with { Text = "\ud800" }));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Journal_payload_matches_owner_canonical_codec_and_golden_hash()
    {
        var write = WorkerJournalWrite.Create(
            Command(),
            "TERMINAL",
            "VERIFIED_SUCCESS",
            "verified");
        const string expected =
            "{\"detail\":\"verified\",\"entry_type\":\"VERIFIED_SUCCESS\",\"schema_version\":\"1.0\"}";

        Assert.Equal(expected, write.PayloadJson);
        Assert.Equal(expected, CanonicalJson.Canonicalize(write.PayloadJson));
        Assert.Equal(
            "527169419c376942fb39ea2c0d2dbb77ebd9e65aa8caa4850533df5748679a9c",
            write.PayloadSha256);
        Assert.Equal(WorkerTestHash.Sha256(expected), write.PayloadSha256);

        var escaped = WorkerJournalWrite.Create(
            Command(),
            "TERMINAL",
            "VERIFIED_SUCCESS",
            "line one\n\"quoted\"");
        Assert.Equal(escaped.PayloadJson, CanonicalJson.Canonicalize(escaped.PayloadJson));
        Assert.Equal(WorkerTestHash.Sha256(escaped.PayloadJson), escaped.PayloadSha256);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Journal_request_rejects_non_token_ids_and_noncanonical_payloads()
    {
        var command = Command();
        var context = WorkerJournalContext.FromCommand(command);
        var write = WorkerJournalWrite.Create(command, "TERMINAL", "VERIFIED_SUCCESS", "verified");

        Assert.Throws<InvalidDataException>(() =>
            WorkerJournalAppendRequest.Create(context with { CommandId = "command\n1" }, write));
        Assert.Throws<InvalidDataException>(() =>
            WorkerJournalAppendRequest.Create(context with { CommandId = "command/1" }, write));
        Assert.Throws<InvalidDataException>(() => WorkerExchangeCodec.DecodeCommand(
            JsonSerializer.Serialize(WithHash(command with { CommandId = "command\n1" }))));
        Assert.Throws<InvalidDataException>(() => WorkerExchangeCodec.DecodeCommand(
            JsonSerializer.Serialize(WithHash(command with { CommandId = "command/1" }))));
        Assert.Throws<InvalidDataException>(() =>
            WorkerJournalAppendRequest.Create(
                context,
                write with { EntryId = "worker/unsafe" }));
        Assert.Throws<InvalidDataException>(() =>
            WorkerJournalAppendRequest.Create(
                context,
                write with
                {
                    PayloadJson =
                        "{\"schema_version\":\"1.0\",\"entry_type\":\"VERIFIED_SUCCESS\",\"detail\":\"verified\"}",
                    PayloadSha256 = WorkerTestHash.Sha256(
                        "{\"schema_version\":\"1.0\",\"entry_type\":\"VERIFIED_SUCCESS\",\"detail\":\"verified\"}")
                }));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Worker_append_is_accepted_by_owner_store_and_raw_noncanonical_hash_fails()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "dps-worker-journal-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = await JournalStore.OpenAsync(
                Path.Combine(directory, "journal.jsonl"),
                token);
            var command = Command();
            var request = WorkerJournalAppendRequest.Create(
                WorkerJournalContext.FromCommand(command),
                WorkerJournalWrite.Create(command, "TERMINAL", "VERIFIED_SUCCESS", "verified"));

            var receipt = await store.AppendAsync(ToOwnerRequest(request), token);

            Assert.True(receipt.Durable);
            Assert.Equal(request.EntryId, receipt.EntryId);
            Assert.Equal(request.PayloadSha256, receipt.PayloadSha256);
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(
                ToOwnerRequest(request) with
                {
                    EntryId = request.EntryId + "_BAD_HASH",
                    PayloadSha256 = new string('0', 64)
                },
                token));

            const string rawNoncanonical =
                "{\"schema_version\":\"1.0\",\"entry_type\":\"VERIFIED_SUCCESS\",\"detail\":\"verified\"}";
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(
                ToOwnerRequest(request) with
                {
                    EntryId = request.EntryId + "_RAW_HASH",
                    PayloadJson = rawNoncanonical,
                    PayloadSha256 = WorkerTestHash.Sha256(rawNoncanonical)
                },
                token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Request_hash_binds_occurred_at_and_privacy_class()
    {
        var command = Command();

        Assert.NotEqual(
            command.RequestSha256,
            CommandHasher.Compute(command with { OccurredAt = command.OccurredAt!.Value.AddSeconds(1) }));
        Assert.NotEqual(
            command.RequestSha256,
            CommandHasher.Compute(command with { PrivacyClass = "sensitive" }));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Same_key_with_cross_soul_device_or_account_scope_is_quarantined()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeTransport(new NativeDispatchResult(true, NativeStatus.Success, true, "verified"));
        var store = new InMemoryCommandStateStore();
        var processor = Processor(transport, store);
        var first = Command();
        Assert.Equal("VERIFIED_SUCCESS", (await processor.ProcessAsync(first, token)).ResultStatus);
        var changedScopes = new[]
        {
            first with { SoulId = "soul_" + new string('b', 64) },
            first with { DeviceBindingId = "db_" + new string('f', 32) },
            first with { PlatformAccountId = "pa_" + new string('1', 32) }
        };
        foreach (var changed in changedScopes)
        {
            var receipt = await processor.ProcessAsync(WithHash(changed), token);
            Assert.Equal("QUARANTINED", receipt.ResultStatus);
        }

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_success_without_postcondition_is_failure_and_shadow_never_dispatches()
    {
        var token = TestContext.Current.CancellationToken;
        var native = new FakeTransport(new NativeDispatchResult(true, NativeStatus.Success, false, "native only"));
        var nativeReceipt = await Processor(native).ProcessAsync(Command(), token);
        Assert.Equal("FAILED", nativeReceipt.ResultStatus);
        Assert.True(nativeReceipt.DispatchAcknowledged);

        var shadowTransport = new FakeTransport(new NativeDispatchResult(true, NativeStatus.Success, true, "must not run"));
        var shadow = WithHash(Command() with
        {
            IdempotencyKey = "idem_" + new string('9', 64),
            CommandId = "command-shadow",
            Shadow = true
        });
        var shadowReceipt = await Processor(shadowTransport).ProcessAsync(shadow, token);
        Assert.Equal("SHADOWED", shadowReceipt.ResultStatus);
        Assert.False(shadowReceipt.DispatchAcknowledged);
        Assert.Equal(0, shadowTransport.Calls);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Worker_contract_is_owned_by_supervisor_and_defines_all_exchange_kinds_fail_closed()
    {
        var contractRoot = ContractRoot();
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.schema.json")));
        var properties = schema.RootElement.GetProperty("properties");
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            "edge.worker.exchange/v1",
            properties.GetProperty("contract_id").GetProperty("const").GetString());
        Assert.Equal(
            new[] { "windows-edge-supervisor", "windows-edge-worker" },
            properties.GetProperty("producer_module").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray());

        var conditionals = schema.RootElement.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "COMMAND", "RECEIPT", "HEALTH" },
            conditionals.Select(conditional => conditional.GetProperty("if").GetProperty("properties")
                .GetProperty("exchange_kind").GetProperty("const").GetString()).ToArray());
        Assert.Equal(
            "windows-edge-supervisor",
            ConditionalProperties(conditionals, "COMMAND").GetProperty("producer_module").GetProperty("const").GetString());
        Assert.Equal(
            "windows-edge-worker",
            ConditionalProperties(conditionals, "RECEIPT").GetProperty("producer_module").GetProperty("const").GetString());
        Assert.Equal("null", ConditionalProperties(conditionals, "HEALTH").GetProperty("request_sha256").GetProperty("type").GetString());
        Assert.Equal("boolean", ConditionalProperties(conditionals, "RECEIPT").GetProperty("duplicate").GetProperty("type").GetString());
        Assert.Equal("boolean", ConditionalProperties(conditionals, "RECEIPT").GetProperty("retry_allowed").GetProperty("type").GetString());

        var specFile = schema.RootElement.GetProperty("x-dps-request-sha256-spec").GetString();
        Assert.Equal("edge.worker.exchange.v1.request-sha256.json", specFile);
        using var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(contractRoot, specFile!)));
        Assert.Equal("windows-edge-supervisor", spec.RootElement.GetProperty("owner_module").GetString());
        Assert.Equal("COMPUTED_BY_THIS_SPEC", spec.RootElement.GetProperty("exchange_kind_semantics")
            .GetProperty("COMMAND").GetProperty("value").GetString());
        Assert.Equal("ORIGINAL_COMMAND_REQUEST_SHA256", spec.RootElement.GetProperty("exchange_kind_semantics")
            .GetProperty("RECEIPT").GetProperty("value").GetString());
        Assert.Equal(
            new[]
            {
                "ContractId", "ProducerModule", "SoulId", "DeviceBindingId", "PlatformAccountId",
                "CommandId", "TraceId", "IdempotencyKey", "OccurredAt", "PrivacyClass",
                "LeaseId", "LeaseExpiresAt",
                "ActionKind", "StepKind", "Selector", "Text", "WaitMs", "ExpectedPostcondition", "Shadow"
            },
            spec.RootElement.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("worker_property").GetString())
                .ToArray());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Worker_decoder_accepts_the_supervisor_production_wire_golden_vector()
    {
        var contractRoot = ContractRoot();
        var command = WorkerExchangeCodec.DecodeCommand(File.ReadAllBytes(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.command.golden.json")));

        Assert.Equal("1.0", command.SchemaVersion);
        Assert.Equal("COMMAND", command.ExchangeKind);
        Assert.Equal("personal", command.PrivacyClass);
        Assert.Equal("command-vector-1", command.CommandId);
        Assert.Equal("hello\n世界", command.Text);
        Assert.True(command.Shadow);
        Assert.Equal(
            "d7a8f4901c7d56f833b2ff24ea169bff565984a6417f165a4f25a5ff233d8d1e",
            command.RequestSha256);
        Assert.Equal(command.RequestSha256, CommandHasher.Compute(command));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Worker_decoder_rejects_unknown_duplicate_missing_overlong_and_non_command_wire_fields()
    {
        var json = File.ReadAllText(Path.Combine(
            ContractRoot(),
            "edge.worker.exchange.v1.command.golden.json"));
        var mutations = new[]
        {
            json.Replace("\n}", ",\n  \"unknown_field\": true\n}", StringComparison.Ordinal),
            json.Replace(
                "\"schema_version\": \"1.0\",",
                "\"schema_version\": \"1.0\", \"schema_version\": \"1.0\",",
                StringComparison.Ordinal),
            json.Replace("  \"schema_version\": \"1.0\",\n", string.Empty, StringComparison.Ordinal),
            json.Replace("\"exchange_kind\": \"COMMAND\"", "\"exchange_kind\": \"RECEIPT\"", StringComparison.Ordinal),
            json.Replace("\"exchange_kind\": \"COMMAND\"", "\"exchange_kind\": \"DRAIN\"", StringComparison.Ordinal),
            json.Replace("\"trace_id\": \"trace_dddddddddddddddddddddddddddddddd\"", "\"trace_id\": \"" + new string('x', 129) + "\"", StringComparison.Ordinal),
            json.Replace("\"command_id\": \"command-vector-1\"", "\"command_id\": null", StringComparison.Ordinal),
            json.Replace("2026-07-14T00:00:00+00:00", "2026-07-14T08:00:00+08:00", StringComparison.Ordinal),
            json.Replace("2026-07-14T00:05:00+00:00", "2026-07-14T08:05:00+08:00", StringComparison.Ordinal),
            json.Replace(
                "d7a8f4901c7d56f833b2ff24ea169bff565984a6417f165a4f25a5ff233d8d1e",
                new string('0', 64),
                StringComparison.Ordinal)
        };

        foreach (var mutation in mutations)
            Assert.Throws<InvalidDataException>(() => WorkerExchangeCodec.DecodeCommand(mutation));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Worker_production_codecs_interoperate_for_receipt_and_health_golden_wires()
    {
        var contractRoot = ContractRoot();
        var source = WorkerExchangeCodec.DecodeCommand(File.ReadAllBytes(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.command.golden.json")));
        var receipt = new CommandReceipt(
            source.CommandId,
            source.IdempotencyKey,
            "VERIFIED_SUCCESS",
            true,
            NativeStatus.Success,
            true,
            Duplicate: false,
            RetryAllowed: false,
            "fixture state changed");
        using var actualReceipt = JsonDocument.Parse(WorkerExchangeCodec.EncodeReceipt(
            source,
            receipt,
            DateTimeOffset.Parse("2026-07-14T00:00:01+00:00")));
        using var expectedReceipt = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.receipt.golden.json")));
        Assert.True(JsonElement.DeepEquals(expectedReceipt.RootElement, actualReceipt.RootElement));

        var healthReport = new WorkerHealthReport(
            "soul_" + new string('a', 64),
            "db_" + new string('b', 32),
            "pa_" + new string('c', 32),
            "trace_" + new string('f', 32),
            "idem_" + new string('a', 64),
            DateTimeOffset.Parse("2026-07-14T00:00:02+00:00"),
            "internal",
            "worker healthy");
        using var actualHealth = JsonDocument.Parse(WorkerExchangeCodec.EncodeHealth(healthReport));
        using var expectedHealth = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.health.golden.json")));
        Assert.True(JsonElement.DeepEquals(expectedHealth.RootElement, actualHealth.RootElement));

        Assert.Throws<InvalidDataException>(() => WorkerExchangeCodec.EncodeReceipt(
            source,
            receipt with { ResultStatus = "VERIFIED_SUCCESS", DispatchAcknowledged = false },
            DateTimeOffset.Parse("2026-07-14T00:00:01+00:00")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Restart_after_reservation_before_acceptance_resumes_and_dispatches_once()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var store = new InMemoryCommandStateStore();
        var interruptedEpoch = store.BeginProcessEpoch();
        var reservation = store.TryBegin(command.IdempotencyKey!, command.RequestSha256!, interruptedEpoch);
        Assert.Equal("NEW", reservation.Status);
        Assert.Equal(CommandExecutionPhase.Reserved, reservation.Phase);

        var transport = SuccessfulTransport();
        var receipt = await Processor(transport, store).ProcessAsync(command, token);

        Assert.Equal("VERIFIED_SUCCESS", receipt.ResultStatus);
        Assert.True(receipt.DispatchAcknowledged);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Restart_after_acceptance_before_transport_resumes_and_dispatches_once()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var store = new InMemoryCommandStateStore();
        var interruptedEpoch = store.BeginProcessEpoch();
        Assert.Equal("NEW", store.TryBegin(command.IdempotencyKey!, command.RequestSha256!, interruptedEpoch).Status);
        store.MarkAccepted(command.IdempotencyKey!, interruptedEpoch);

        var transport = SuccessfulTransport();
        var receipt = await Processor(transport, store).ProcessAsync(command, token);

        Assert.Equal("VERIFIED_SUCCESS", receipt.ResultStatus);
        Assert.True(receipt.DispatchAcknowledged);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Restart_after_transport_attempt_before_ack_is_unknown_with_ack_truth_unset()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var store = StateAt(command, CommandExecutionPhase.TransportAttempted);
        var transport = SuccessfulTransport("must not run");

        var receipt = await Processor(transport, store).ProcessAsync(command, token);

        Assert.Equal("UNKNOWN_OUTCOME", receipt.ResultStatus);
        Assert.Null(receipt.DispatchAcknowledged);
        Assert.False(receipt.RetryAllowed);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Restart_after_dispatch_ack_before_completion_is_unknown_with_ack_true()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var store = StateAt(command, CommandExecutionPhase.DispatchAcknowledged);
        var transport = SuccessfulTransport("must not run");

        var receipt = await Processor(transport, store).ProcessAsync(command, token);

        Assert.Equal("UNKNOWN_OUTCOME", receipt.ResultStatus);
        Assert.True(receipt.DispatchAcknowledged);
        Assert.False(receipt.RetryAllowed);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Same_epoch_duplicates_only_retry_before_transport_and_reconcile_after_attempt()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var store = new InMemoryCommandStateStore();
        var transport = SuccessfulTransport("must not run");
        var processor = Processor(transport, store);
        const long processorEpoch = 1;
        Assert.Equal("NEW", store.TryBegin(command.IdempotencyKey!, command.RequestSha256!, processorEpoch).Status);
        store.MarkAccepted(command.IdempotencyKey!, processorEpoch);

        var acceptedDuplicate = await processor.ProcessAsync(command, token);
        Assert.Equal("IN_PROGRESS", acceptedDuplicate.ResultStatus);
        Assert.False(acceptedDuplicate.DispatchAcknowledged);
        Assert.True(acceptedDuplicate.RetryAllowed);

        store.MarkTransportAttempted(command.IdempotencyKey!, processorEpoch);
        var attemptedDuplicate = await processor.ProcessAsync(command, token);
        Assert.Equal("UNKNOWN_OUTCOME", attemptedDuplicate.ResultStatus);
        Assert.Null(attemptedDuplicate.DispatchAcknowledged);
        Assert.False(attemptedDuplicate.RetryAllowed);

        store.MarkDispatchAcknowledged(command.IdempotencyKey!, processorEpoch);
        var acknowledgedDuplicate = await processor.ProcessAsync(command, token);
        Assert.Equal("UNKNOWN_OUTCOME", acknowledgedDuplicate.ResultStatus);
        Assert.True(acknowledgedDuplicate.DispatchAcknowledged);
        Assert.False(acknowledgedDuplicate.RetryAllowed);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Pre_dispatch_attempt_budget_survives_restart_and_never_exceeds_two()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var store = new InMemoryCommandStateStore();
        var interruptedEpoch = store.BeginProcessEpoch();
        Assert.Equal("NEW", store.TryBegin(command.IdempotencyKey!, command.RequestSha256!, interruptedEpoch).Status);
        store.MarkAccepted(command.IdempotencyKey!, interruptedEpoch);
        Assert.Equal(1, store.MarkTransportAttempted(command.IdempotencyKey!, interruptedEpoch));
        store.MarkPreDispatchRetry(command.IdempotencyKey!, interruptedEpoch);
        var transport = new FakeTransport(
            new NativeDispatchResult(false, NativeStatus.Failed, false, "still offline"),
            new NativeDispatchResult(true, NativeStatus.Success, true, "must not run"));

        var receipt = await Processor(transport, store).ProcessAsync(command, token);

        Assert.Equal("FAILED", receipt.ResultStatus);
        Assert.False(receipt.DispatchAcknowledged);
        Assert.False(receipt.RetryAllowed);
        Assert.Equal(1, transport.Calls);
        var completed = store.TryBegin(command.IdempotencyKey!, command.RequestSha256!, store.BeginProcessEpoch());
        Assert.Equal("DUPLICATE", completed.Status);
        Assert.Equal(CommandDispatchPolicy.MaximumAttempts, completed.DispatchAttemptCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Unknown_outcome_is_not_retried_and_survives_processor_recovery()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeTransport(
            new NativeDispatchResult(true, NativeStatus.UnknownOutcome, false, "lost after acknowledgement"));
        var store = new InMemoryCommandStateStore();
        var command = Command();
        var first = await Processor(transport, store).ProcessAsync(command, token);
        var recovered = await Processor(transport, store).ProcessAsync(command, token);

        Assert.Equal("UNKNOWN_OUTCOME", first.ResultStatus);
        Assert.True(first.DispatchAcknowledged);
        Assert.Equal("UNKNOWN_OUTCOME", recovered.ResultStatus);
        Assert.True(recovered.DispatchAcknowledged);
        Assert.False(recovered.RetryAllowed);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Ambiguous_transport_exception_is_unknown_and_never_redispatched()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeTransport(
            new TransportDispatchException("connection lost during write", dispatchAcknowledged: null));
        var store = new InMemoryCommandStateStore();
        var command = Command();

        var first = await Processor(transport, store).ProcessAsync(command, token);
        var duplicate = await Processor(transport, store).ProcessAsync(command, token);

        Assert.Equal("UNKNOWN_OUTCOME", first.ResultStatus);
        Assert.Null(first.DispatchAcknowledged);
        Assert.Equal("UNKNOWN_OUTCOME", duplicate.ResultStatus);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Only_pre_dispatch_failure_gets_one_bounded_retry()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeTransport(
            new TransportDispatchException("offline before write", dispatchAcknowledged: false),
            new NativeDispatchResult(true, NativeStatus.Success, true, "verified"));

        var receipt = await Processor(transport).ProcessAsync(Command(), token);

        Assert.Equal("VERIFIED_SUCCESS", receipt.ResultStatus);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Terminal_receipt_is_not_visible_until_the_final_Journal_append_is_durable()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var transport = SuccessfulTransport();
        var store = new InMemoryCommandStateStore();
        var journal = new InMemoryJournal(failOnceEntryType: "VERIFIED_SUCCESS");
        var processor = Processor(transport, store, journal);

        await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(command, token));

        var pending = store.GetDrainSnapshot();
        Assert.Equal(1, pending.CompletionPendingCount);
        Assert.False(pending.IsDrained);

        var duplicate = await processor.ProcessAsync(command, token);

        Assert.Equal("VERIFIED_SUCCESS", duplicate.ResultStatus);
        Assert.True(duplicate.Duplicate);
        Assert.False(duplicate.RetryAllowed);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(2, journal.Writes.Count(write => write.EntryType == "VERIFIED_SUCCESS"));
        Assert.Single(journal.States, state => state == "VERIFIED_SUCCESS");
        processor.StopIntake();
        Assert.True(processor.IsDrained);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Crash_after_final_Journal_append_replays_the_same_entry_before_completion()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        var transport = SuccessfulTransport();
        var innerStore = new InMemoryCommandStateStore();
        var store = new FailOnceFinalizeStore(innerStore);
        var journal = new InMemoryJournal();
        var processor = Processor(transport, store, journal);

        await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(command, token));
        Assert.Equal(1, innerStore.GetDrainSnapshot().CompletionPendingCount);

        var recoveredTransport = SuccessfulTransport("must not run");
        var recoveredProcessor = Processor(recoveredTransport, store, journal);
        Assert.Equal(1, await recoveredProcessor.ReconcilePreparedCompletionsAsync(token));
        var duplicate = await recoveredProcessor.ProcessAsync(command, token);

        Assert.Equal("VERIFIED_SUCCESS", duplicate.ResultStatus);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(0, recoveredTransport.Calls);
        var terminalWrites = journal.Writes.Where(write => write.EntryType == "VERIFIED_SUCCESS").ToArray();
        Assert.Equal(2, terminalWrites.Length);
        Assert.Equal(terminalWrites[0].EntryId, terminalWrites[1].EntryId);
        Assert.Equal(terminalWrites[0].PayloadSha256, terminalWrites[1].PayloadSha256);
        Assert.Single(journal.States, state => state == "VERIFIED_SUCCESS");
        Assert.True(innerStore.GetDrainSnapshot().IsDrained);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Drain_truth_includes_persisted_unfinished_uncertain_and_completion_pending_state()
    {
        var token = TestContext.Current.CancellationToken;
        var command = Command();

        var unfinishedStore = new InMemoryCommandStateStore();
        var unfinishedEpoch = unfinishedStore.BeginProcessEpoch();
        Assert.Equal("NEW", unfinishedStore.TryBegin(
            command.IdempotencyKey!, command.RequestSha256!, unfinishedEpoch).Status);
        unfinishedStore.MarkAccepted(command.IdempotencyKey!, unfinishedEpoch);
        var unfinishedProcessor = Processor(SuccessfulTransport("must not run"), unfinishedStore);
        unfinishedProcessor.StopIntake();
        Assert.False(unfinishedProcessor.IsDrained);
        Assert.Equal(1, unfinishedProcessor.GetDrainSnapshot().UnfinishedCount);

        var uncertainStore = StateAt(command, CommandExecutionPhase.TransportAttempted);
        var uncertainProcessor = Processor(SuccessfulTransport("must not run"), uncertainStore);
        uncertainProcessor.StopIntake();
        Assert.False(uncertainProcessor.IsDrained);
        Assert.Equal(1, uncertainProcessor.GetDrainSnapshot().UncertainCount);

        var pendingStore = new InMemoryCommandStateStore();
        var pendingEpoch = pendingStore.BeginProcessEpoch();
        Assert.Equal("NEW", pendingStore.TryBegin(
            command.IdempotencyKey!, command.RequestSha256!, pendingEpoch).Status);
        pendingStore.MarkAccepted(command.IdempotencyKey!, pendingEpoch);
        var preparedReceipt = new CommandReceipt(
            command.CommandId, command.IdempotencyKey, "SHADOWED", false, null, null,
            Duplicate: false, RetryAllowed: false, "prepared terminal audit");
        pendingStore.PrepareCompletion(
            command.IdempotencyKey!,
            pendingEpoch,
            WorkerJournalContext.FromCommand(command),
            preparedReceipt,
            WorkerJournalWrite.Create(command, "TERMINAL", "SHADOWED", preparedReceipt.Detail));
        var pendingProcessor = Processor(SuccessfulTransport("must not run"), pendingStore);
        pendingProcessor.StopIntake();
        Assert.False(pendingProcessor.IsDrained);
        Assert.Equal(1, pendingProcessor.GetDrainSnapshot().CompletionPendingCount);

        var completedProcessor = Processor(SuccessfulTransport());
        Assert.Equal("VERIFIED_SUCCESS", (await completedProcessor.ProcessAsync(command, token)).ResultStatus);
        Assert.False(completedProcessor.IsDrained);
        completedProcessor.StopIntake();
        Assert.True(completedProcessor.IsDrained);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Drain_truth_stays_false_for_the_entire_reconciliation_pass()
    {
        using var store = new BlockingClaimStateStore();
        var processor = Processor(SuccessfulTransport("must not run"), store);
        processor.StopIntake();
        Assert.True(processor.IsDrained);

        var reconciliation = Task.Run(
            () => processor.ReconcilePreparedCompletionsAsync(
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.True(store.WaitUntilClaimed(TimeSpan.FromSeconds(5)));
        Assert.False(processor.IsDrained);

        store.ReleaseClaim();
        Assert.Equal(0, await reconciliation);
        Assert.True(processor.IsDrained);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Production_mode_rejects_simulation_only_adapters()
    {
        var transport = SuccessfulTransport("must not run");

        var exception = Assert.Throws<InvalidOperationException>(() => Processor(
            transport,
            new InMemoryCommandStateStore(),
            new InMemoryJournal(),
            WorkerRuntimeMode.Production));

        Assert.Contains("durable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, transport.Calls);
    }

    private static CommandProcessor Processor(
        FakeTransport transport,
        ICommandStateStore? store = null,
        IWorkerJournal? journal = null,
        WorkerRuntimeMode runtimeMode = WorkerRuntimeMode.Simulation) =>
        new(
            transport,
            journal ?? new InMemoryJournal(),
            store ?? new InMemoryCommandStateStore(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-14T00:00:00Z")),
            runtimeMode);

    private static FakeTransport SuccessfulTransport(string detail = "verified") =>
        new(new NativeDispatchResult(true, NativeStatus.Success, true, detail));

    private static WorkerCommand Command()
    {
        var command = new WorkerCommand(
            "1.0",
            "edge.worker.exchange/v1",
            "windows-edge-supervisor",
            "soul_" + new string('a', 64),
            "db_" + new string('b', 32),
            "pa_" + new string('c', 32),
            "trace_" + new string('d', 32),
            "idem_" + new string('e', 64),
            DateTimeOffset.Parse("2026-07-14T00:00:00Z"),
            "personal",
            "COMMAND",
            "command-1",
            "lease-1",
            DateTimeOffset.Parse("2026-07-14T00:05:00Z"),
            string.Empty,
            "TAP",
            "TAP_SELECTOR",
            "fixture:button",
            null,
            null,
            "fixture state changed",
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        return WithHash(command);
    }

    private static WorkerCommand WithHash(WorkerCommand command) =>
        command with { RequestSha256 = CommandHasher.Compute(command) };

    private static InMemoryCommandStateStore StateAt(WorkerCommand command, CommandExecutionPhase phase)
    {
        var store = new InMemoryCommandStateStore();
        var interruptedEpoch = store.BeginProcessEpoch();
        Assert.Equal("NEW", store.TryBegin(command.IdempotencyKey!, command.RequestSha256!, interruptedEpoch).Status);
        store.MarkAccepted(command.IdempotencyKey!, interruptedEpoch);
        if (phase is CommandExecutionPhase.TransportAttempted or CommandExecutionPhase.DispatchAcknowledged)
            store.MarkTransportAttempted(command.IdempotencyKey!, interruptedEpoch);
        if (phase == CommandExecutionPhase.DispatchAcknowledged)
            store.MarkDispatchAcknowledged(command.IdempotencyKey!, interruptedEpoch);
        return store;
    }

    private static JsonElement ConditionalProperties(JsonElement[] conditionals, string kind) =>
        conditionals.Single(conditional => conditional.GetProperty("if").GetProperty("properties")
                .GetProperty("exchange_kind").GetProperty("const").GetString() == kind)
            .GetProperty("then").GetProperty("properties");

    private static JournalAppendRequest ToOwnerRequest(WorkerJournalAppendRequest request) => new(
        request.SchemaVersion,
        request.ContractId,
        request.ProducerModule,
        request.CommandId,
        request.EntryId,
        request.EntryType,
        request.TraceId,
        request.IdempotencyKey,
        request.PrivacyClass,
        request.SoulId,
        request.DeviceBindingId,
        request.PlatformAccountId,
        request.PayloadJson,
        request.PayloadSha256,
        request.OccurredAt);

    private static string ContractRoot() => Path.Combine(
        RepositoryRoot(),
        "Modules/windows-edge-supervisor/contracts/provided");

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null &&
               !(File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                 Directory.Exists(Path.Combine(current.FullName, "governance"))))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

internal sealed class FakeTransport(params object[] outcomes) : INativeTransport
{
    private readonly Queue<object> _outcomes = new(outcomes);
    public int Calls { get; private set; }

    public Task<NativeDispatchResult> DispatchAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        Calls++;
        var outcome = _outcomes.Dequeue();
        return outcome is Exception exception
            ? Task.FromException<NativeDispatchResult>(exception)
            : Task.FromResult((NativeDispatchResult)outcome);
    }
}

internal sealed class InMemoryJournal(string? failOnceEntryType = null) : IWorkerJournal
{
    private const string GenesisChecksum =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public List<string> States { get; } = [];
    public List<WorkerJournalAppendRequest> Writes { get; } = [];
    private readonly Dictionary<string, StoredJournalEntry> _entries = new(StringComparer.Ordinal);
    private bool _failed;
    private long _lastSequence;
    private string _lastChecksum = GenesisChecksum;

    public Task<WorkerJournalAppendReceipt> AppendAsync(
        WorkerJournalAppendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPayload = CanonicalJson.Canonicalize(request.PayloadJson);
        if (!string.Equals(canonicalPayload, request.PayloadJson, StringComparison.Ordinal))
            return Task.FromException<WorkerJournalAppendReceipt>(
                new InvalidDataException("worker Journal adapter received noncanonical payload_json"));
        var computedPayloadSha256 = WorkerTestHash.Sha256(canonicalPayload);
        if (!string.Equals(computedPayloadSha256, request.PayloadSha256, StringComparison.Ordinal))
            return Task.FromException<WorkerJournalAppendReceipt>(
                new InvalidDataException("worker Journal adapter received a mismatched payload hash"));

        Writes.Add(request);
        if (!_failed && request.EntryType == failOnceEntryType)
        {
            _failed = true;
            return Task.FromException<WorkerJournalAppendReceipt>(new IOException("injected Journal append failure"));
        }

        if (_entries.TryGetValue(request.EntryId, out var existing))
        {
            if (existing.Request != request)
                return Task.FromException<WorkerJournalAppendReceipt>(
                    new InvalidDataException("journal entry identity was reused with different scoped content"));
            return Task.FromResult(existing.Receipt with { Duplicate = true });
        }

        var sequence = checked(++_lastSequence);
        var previousChecksum = _lastChecksum;
        var entryChecksum = WorkerTestHash.Sha256(JsonSerializer.Serialize(new
        {
            domain = "dps.windows-edge-worker.test-journal-receipt/v1",
            sequence,
            previous_checksum = previousChecksum,
            request.CommandId,
            request.EntryId,
            request.EntryType,
            request.PayloadSha256
        }));
        var receipt = new WorkerJournalAppendReceipt(
            "1.0",
            "edge.journal.receipt/v1",
            "edge-local-journal",
            request.ProducerModule,
            request.CommandId,
            request.EntryId,
            request.EntryType,
            request.TraceId,
            request.IdempotencyKey,
            request.PrivacyClass,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            sequence,
            request.PayloadSha256,
            previousChecksum,
            entryChecksum,
            Durable: true,
            Duplicate: false);
        _entries.Add(request.EntryId, new StoredJournalEntry(request, receipt));
        _lastChecksum = entryChecksum;
        States.Add(request.EntryType);
        return Task.FromResult(receipt);
    }

    private sealed record StoredJournalEntry(
        WorkerJournalAppendRequest Request,
        WorkerJournalAppendReceipt Receipt);
}

internal sealed class FailOnceFinalizeStore(ICommandStateStore inner) : ICommandStateStore
{
    private bool _failed;

    public long BeginProcessEpoch() => inner.BeginProcessEpoch();
    public BeginResult TryBegin(string idempotencyKey, string requestSha256, long processEpoch) =>
        inner.TryBegin(idempotencyKey, requestSha256, processEpoch);
    public void MarkAccepted(string idempotencyKey, long processEpoch) =>
        inner.MarkAccepted(idempotencyKey, processEpoch);
    public int MarkTransportAttempted(string idempotencyKey, long processEpoch) =>
        inner.MarkTransportAttempted(idempotencyKey, processEpoch);
    public void MarkPreDispatchRetry(string idempotencyKey, long processEpoch) =>
        inner.MarkPreDispatchRetry(idempotencyKey, processEpoch);
    public void MarkDispatchAcknowledged(string idempotencyKey, long processEpoch) =>
        inner.MarkDispatchAcknowledged(idempotencyKey, processEpoch);
    public void PrepareCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalContext journalContext,
        CommandReceipt receipt,
        WorkerJournalWrite terminalWrite) =>
        inner.PrepareCompletion(idempotencyKey, processEpoch, journalContext, receipt, terminalWrite);
    public void FinalizeCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalAppendReceipt journalReceipt)
    {
        if (!_failed)
        {
            _failed = true;
            throw new IOException("injected crash after durable Journal append");
        }
        inner.FinalizeCompletion(idempotencyKey, processEpoch, journalReceipt);
    }
    public IReadOnlyList<PreparedCommandCompletion> ClaimPreparedCompletions(long processEpoch) =>
        inner.ClaimPreparedCompletions(processEpoch);
    public CommandDrainSnapshot GetDrainSnapshot() => inner.GetDrainSnapshot();
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class BlockingClaimStateStore : ICommandStateStore, IDisposable
{
    private readonly ManualResetEventSlim _claimEntered = new(false);
    private readonly ManualResetEventSlim _releaseClaim = new(false);

    public long BeginProcessEpoch() => 1;

    public IReadOnlyList<PreparedCommandCompletion> ClaimPreparedCompletions(long processEpoch)
    {
        if (processEpoch != 1)
            throw new InvalidOperationException("unexpected process epoch");
        _claimEntered.Set();
        _releaseClaim.Wait();
        return [];
    }

    public CommandDrainSnapshot GetDrainSnapshot() => new(0, 0, 0);

    public bool WaitUntilClaimed(TimeSpan timeout) => _claimEntered.Wait(timeout);

    public void ReleaseClaim() => _releaseClaim.Set();

    public BeginResult TryBegin(string idempotencyKey, string requestSha256, long processEpoch) =>
        throw new NotSupportedException();
    public void MarkAccepted(string idempotencyKey, long processEpoch) =>
        throw new NotSupportedException();
    public int MarkTransportAttempted(string idempotencyKey, long processEpoch) =>
        throw new NotSupportedException();
    public void MarkPreDispatchRetry(string idempotencyKey, long processEpoch) =>
        throw new NotSupportedException();
    public void MarkDispatchAcknowledged(string idempotencyKey, long processEpoch) =>
        throw new NotSupportedException();
    public void PrepareCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalContext journalContext,
        CommandReceipt receipt,
        WorkerJournalWrite terminalWrite) =>
        throw new NotSupportedException();
    public void FinalizeCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalAppendReceipt journalReceipt) =>
        throw new NotSupportedException();

    public void Dispose()
    {
        _releaseClaim.Set();
        _claimEntered.Dispose();
        _releaseClaim.Dispose();
    }
}

internal static class WorkerTestHash
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(value)));
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeSupervisor;
using Dps.WindowsEdgeSupervisor.Contracts;
using Xunit;

namespace Dps.WindowsEdgeSupervisor.Tests;

public sealed class SupervisorTests
{
    private static readonly string[] RequiredCapabilities = ["bridge-abi-v1", "journal-v1"];

    [Fact]
    [Trait("Category", "Unit")]
    public void Invalid_digest_is_rejected()
    {
        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        var invalid = fixture.Artifact(WorkerSlot.B, 1) with { Sha256 = new string('0', 64) };
        Assert.Throws<InvalidDataException>(() =>
            supervisor.StageCandidate(invalid, fixture.VerifiedWaitingCapability(invalid)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Cutover_requires_drain_and_exact_rollback()
    {
        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        var candidate = fixture.Artifact(WorkerSlot.B, 1);
        supervisor.StageCandidate(candidate, fixture.VerifiedWaitingCapability(candidate));
        var lease = supervisor.AcquireRoute("db_" + new string('b', 32));
        var firstDrain = supervisor.BeginDrain(fixture.DrainScope());
        var firstReceipt = fixture.SignedDrainReceipt(firstDrain);
        Assert.False(await supervisor.TryCutoverAsync(
            firstReceipt, TestContext.Current.CancellationToken));
        lease.Dispose();
        Assert.False(await supervisor.TryCutoverAsync(
            firstReceipt, TestContext.Current.CancellationToken));
        _ = await supervisor.PrepareDrainDirectiveAsync(TestContext.Current.CancellationToken);
        Assert.True(await supervisor.TryCutoverAsync(
            firstReceipt, TestContext.Current.CancellationToken));
        Assert.Equal(WorkerSlot.B, supervisor.ActiveSlot);
        var protectedRollbackArtifact = fixture.Artifact(WorkerSlot.A, 2);
        Assert.Throws<InvalidOperationException>(() => supervisor.StageCandidate(
            protectedRollbackArtifact,
            fixture.VerifiedWaitingCapability(protectedRollbackArtifact)));
        var rollbackDrain = supervisor.BeginDrain(fixture.DrainScope());
        _ = await supervisor.PrepareDrainDirectiveAsync(TestContext.Current.CancellationToken);
        Assert.True(await supervisor.TryRollbackAsync(
            fixture.SignedDrainReceipt(rollbackDrain), TestContext.Current.CancellationToken));
        Assert.Equal(WorkerSlot.A, supervisor.ActiveSlot);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task One_hundred_ab_switches_and_rollbacks_preserve_stable_device_routes()
    {
        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        for (var index = 1; index <= 100; index++)
        {
            var candidateSlot = supervisor.ActiveSlot == WorkerSlot.A ? WorkerSlot.B : WorkerSlot.A;
            var candidate = fixture.Artifact(candidateSlot, index);
            supervisor.StageCandidate(candidate, fixture.VerifiedWaitingCapability(candidate));
            using (var first = supervisor.AcquireRoute("db_" + new string('b', 32)))
            using (var second = supervisor.AcquireRoute("db_" + new string('b', 32)))
            {
                Assert.Equal(first.Snapshot.Slot, second.Snapshot.Slot);
                Assert.Equal(first.Snapshot.RoutingEpoch, second.Snapshot.RoutingEpoch);
            }

            var cutoverDrain = supervisor.BeginDrain(fixture.DrainScope());
            _ = await supervisor.PrepareDrainDirectiveAsync(TestContext.Current.CancellationToken);
            Assert.True(await supervisor.TryCutoverAsync(
                fixture.SignedDrainReceipt(cutoverDrain), TestContext.Current.CancellationToken));
            Assert.Equal(WorkerSlot.B, supervisor.ActiveSlot);
            var rollbackDrain = supervisor.BeginDrain(fixture.DrainScope());
            _ = await supervisor.PrepareDrainDirectiveAsync(TestContext.Current.CancellationToken);
            Assert.True(await supervisor.TryRollbackAsync(
                fixture.SignedDrainReceipt(rollbackDrain), TestContext.Current.CancellationToken));
            Assert.Equal(WorkerSlot.A, supervisor.ActiveSlot);
        }

        Assert.Equal(200, supervisor.RoutingEpoch);
        Assert.Equal(WorkerSlot.A, supervisor.ActiveSlot);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Symbolic_link_escape_and_tampered_shadow_evidence_are_rejected()
    {
        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        var candidate = fixture.Artifact(WorkerSlot.B, 1);
        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(
                Path.GetTempPath(),
                "dps-edge-outside-" + Guid.NewGuid().ToString("N"));
            File.Copy(candidate.BinaryPath, outside);
            var link = Path.Combine(candidate.VersionDirectory, "escaped-worker.bin");
            File.CreateSymbolicLink(link, outside);
            Assert.Throws<InvalidDataException>(() => supervisor.StageCandidate(
                candidate with { BinaryPath = link }, fixture.VerifiedWaitingCapability(candidate)));
            File.Delete(link);
        }

        File.AppendAllText(candidate.ShadowEvidencePath, " ");
        Assert.Throws<InvalidDataException>(() =>
            supervisor.StageCandidate(candidate, fixture.VerifiedWaitingCapability(candidate)));
        var tamperedShadowDigest = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(candidate.ShadowEvidencePath))).ToLowerInvariant();
        Assert.Throws<InvalidDataException>(() => supervisor.StageCandidate(
            candidate with { ShadowEvidenceSha256 = tamperedShadowDigest },
            fixture.VerifiedWaitingCapability(candidate)));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Public_contracts_bind_owner_and_reject_unknown_fields()
    {
        foreach (var contract in new[]
        {
            ("edge.worker.exchange.v1.schema.json", "edge.worker.exchange/v1", new[] { "windows-edge-supervisor", "windows-edge-worker" }),
            ("edge.worker.drain.receipt.v1.schema.json", "edge.worker.drain.receipt/v1", new[] { "windows-edge-worker" }),
            ("edge.bridge.directive.v1.schema.json", "edge.bridge.directive/v1", new[] { "windows-edge-supervisor" }),
            ("edge.capability.evidence.v1.schema.json", "edge.capability.evidence/v1", new[] { "windows-edge-supervisor" })
        })
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixture.RepositoryRoot(), "Modules/windows-edge-supervisor/contracts/provided", contract.Item1)));
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(contract.Item2, schema.RootElement.GetProperty("properties").GetProperty("contract_id").GetProperty("const").GetString());
            var producer = schema.RootElement.GetProperty("properties").GetProperty("producer_module");
            if (contract.Item3.Length == 1)
                Assert.Equal(contract.Item3[0], producer.GetProperty("const").GetString());
            else
                Assert.Equal(contract.Item3, producer.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
            foreach (var identity in new[] { "soul_id", "device_binding_id", "platform_account_id" })
            {
                Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty(identity).GetProperty("type").GetString());
            }
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Edge_worker_request_hash_spec_has_a_self_consistent_golden_vector()
    {
        var contractRoot = Path.Combine(
            Fixture.RepositoryRoot(),
            "Modules/windows-edge-supervisor/contracts/provided");
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.schema.json")));
        var specFile = schema.RootElement.GetProperty("x-dps-request-sha256-spec").GetString();
        Assert.Equal("edge.worker.exchange.v1.request-sha256.json", specFile);

        using var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(contractRoot, specFile!)));
        var root = spec.RootElement;
        Assert.Equal("edge.worker.exchange/v1", root.GetProperty("contract_id").GetString());
        Assert.Equal("windows-edge-supervisor", root.GetProperty("owner_module").GetString());
        Assert.Equal("SHA-256", root.GetProperty("hash_algorithm").GetString());
        Assert.Equal(
            "dps.windows-edge-worker.command-request-sha256/v1",
            root.GetProperty("domain").GetString());
        Assert.Equal(19, root.GetProperty("fields").GetArrayLength());
        Assert.Equal(
            Enumerable.Range(1, 19),
            root.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("ordinal").GetInt32()));
        var framing = root.GetProperty("framing");
        Assert.Equal(
            "uint32-big-endian-byte-length-then-strict-utf8",
            framing.GetProperty("domain").GetString());
        Assert.Equal("uint32-big-endian", framing.GetProperty("field_count").GetString());
        Assert.Equal(19, framing.GetProperty("field_count_value").GetInt32());
        Assert.Equal("00", framing.GetProperty("null_marker_hex").GetString());
        Assert.Equal("01", framing.GetProperty("present_marker_hex").GetString());

        var vector = root.GetProperty("golden_vectors")[0];
        var input = vector.GetProperty("input");
        var command = EdgeWorkerExchangeCodec.CreateCommand(new EdgeWorkerCommandRequest(
            input.GetProperty("soul_id").GetString()!,
            input.GetProperty("device_binding_id").GetString()!,
            input.GetProperty("platform_account_id").GetString()!,
            input.GetProperty("trace_id").GetString()!,
            input.GetProperty("idempotency_key").GetString()!,
            DateTimeOffset.Parse(input.GetProperty("occurred_at").GetString()!),
            input.GetProperty("privacy_class").GetString()!,
            input.GetProperty("command_id").GetString()!,
            input.GetProperty("lease_id").GetString()!,
            DateTimeOffset.Parse(input.GetProperty("lease_expires_at").GetString()!),
            input.GetProperty("action_kind").GetString()!,
            input.GetProperty("step_kind").GetString()!,
            input.GetProperty("selector").GetString(),
            input.GetProperty("text").GetString(),
            input.GetProperty("wait_ms").ValueKind == JsonValueKind.Null
                ? null
                : input.GetProperty("wait_ms").GetInt32(),
            input.GetProperty("expected_postcondition").GetString(),
            input.GetProperty("shadow").GetBoolean()));
        var canonical = EdgeWorkerRequestHasher.CanonicalizeCommand(command);
        Assert.Equal(vector.GetProperty("canonical_byte_length").GetInt32(), canonical.Length);
        Assert.Equal(
            vector.GetProperty("canonical_bytes_hex").GetString(),
            Convert.ToHexStringLower(canonical));
        Assert.Equal(
            vector.GetProperty("request_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
        Assert.Equal(vector.GetProperty("request_sha256").GetString(), command.RequestSha256);

        var encoded = EdgeWorkerExchangeCodec.EncodeCommand(command);
        using var actualWire = JsonDocument.Parse(encoded);
        using var expectedWire = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.command.golden.json")));
        Assert.True(JsonElement.DeepEquals(expectedWire.RootElement, actualWire.RootElement));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Edge_worker_exchange_defines_command_receipt_health_and_rejects_legacy_drain()
    {
        var path = Path.Combine(
            Fixture.RepositoryRoot(),
            "Modules/windows-edge-supervisor/contracts/provided/edge.worker.exchange.v1.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(path));
        var conditionals = schema.RootElement.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "COMMAND", "RECEIPT", "HEALTH" },
            conditionals.Select(ConditionalKind).ToArray());

        var command = ConditionalProperties(conditionals, "COMMAND");
        Assert.Equal("windows-edge-supervisor", command.GetProperty("producer_module").GetProperty("const").GetString());
        Assert.Equal("string", command.GetProperty("request_sha256").GetProperty("type").GetString());
        Assert.Equal("null", command.GetProperty("result_status").GetProperty("type").GetString());

        var receipt = ConditionalProperties(conditionals, "RECEIPT");
        Assert.Equal("windows-edge-worker", receipt.GetProperty("producer_module").GetProperty("const").GetString());
        Assert.Equal("string", receipt.GetProperty("request_sha256").GetProperty("type").GetString());
        Assert.Contains("IN_PROGRESS", receipt.GetProperty("result_status").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("boolean", receipt.GetProperty("duplicate").GetProperty("type").GetString());
        Assert.Equal("boolean", receipt.GetProperty("retry_allowed").GetProperty("type").GetString());

        var health = ConditionalProperties(conditionals, "HEALTH");
        Assert.Equal("windows-edge-worker", health.GetProperty("producer_module").GetProperty("const").GetString());
        Assert.Equal("null", health.GetProperty("request_sha256").GetProperty("type").GetString());
        Assert.Equal("HEALTHY", health.GetProperty("result_status").GetProperty("const").GetString());

        Assert.DoesNotContain("DRAIN", schema.RootElement.GetProperty("properties")
            .GetProperty("exchange_kind").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Production_codecs_interoperate_for_receipt_health_and_reject_free_text_drain()
    {
        var contractRoot = Path.Combine(
            Fixture.RepositoryRoot(),
            "Modules/windows-edge-supervisor/contracts/provided");

        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.CreateDrain(new EdgeWorkerDrainRequest(
            "soul_" + new string('a', 64),
            "db_" + new string('b', 32),
            "pa_" + new string('c', 32),
            "trace_" + new string('1', 32),
            "idem_" + new string('2', 64),
            DateTimeOffset.Parse("2026-07-14T00:00:03+00:00"),
            "internal",
            "stop intake and drain")));

        var sourceCommand = EdgeWorkerExchangeCodec.CreateCommand(GoldenCommandRequest());
        var receipt = EdgeWorkerExchangeCodec.DecodeReceipt(
            File.ReadAllBytes(Path.Combine(contractRoot, "edge.worker.exchange.v1.receipt.golden.json")),
            sourceCommand);
        Assert.Equal("VERIFIED_SUCCESS", receipt.ResultStatus);
        Assert.True(receipt.DispatchAcknowledged);
        Assert.True(receipt.PostconditionVerified);
        Assert.False(receipt.Duplicate);
        Assert.False(receipt.RetryAllowed);

        var health = EdgeWorkerExchangeCodec.DecodeHealth(File.ReadAllBytes(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.health.golden.json")));
        Assert.Equal("HEALTHY", health.ResultStatus);
        Assert.Null(health.RequestSha256);

        var invalidReceipt = File.ReadAllText(
            Path.Combine(contractRoot, "edge.worker.exchange.v1.receipt.golden.json"))
            .Replace("\"dispatch_acknowledged\": true", "\"dispatch_acknowledged\": false", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.DecodeReceipt(invalidReceipt, sourceCommand));
        var wrongSource = EdgeWorkerExchangeCodec.CreateCommand(GoldenCommandRequest() with
        {
            TraceId = "trace_" + new string('0', 32),
            IdempotencyKey = "idem_" + new string('0', 64),
            CommandId = "command-other"
        });
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.DecodeReceipt(
            File.ReadAllBytes(Path.Combine(contractRoot, "edge.worker.exchange.v1.receipt.golden.json")),
            wrongSource));
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.DecodeHealth(
            File.ReadAllBytes(Path.Combine(contractRoot, "edge.worker.exchange.v1.receipt.golden.json"))));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Production_command_encoder_rejects_tampering_and_wire_boundary_violations()
    {
        var request = GoldenCommandRequest();
        var command = EdgeWorkerExchangeCodec.CreateCommand(request);

        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.EncodeCommand(
            command with { RequestSha256 = new string('0', 64) }));
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.CreateCommand(
            request with { TraceId = new string('x', 129) }));
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.CreateCommand(
            request with { ActionKind = "TAP", StepKind = "TYPE_TEXT" }));
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.CreateCommand(
            request with { Text = "\ud800" }));
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.CreateCommand(
            request with { OccurredAt = DateTimeOffset.Parse("2026-07-14T08:00:00+08:00") }));
        Assert.Throws<InvalidDataException>(() => EdgeWorkerExchangeCodec.CreateCommand(
            request with { LeaseExpiresAt = DateTimeOffset.Parse("2026-07-14T08:05:00+08:00") }));

        var occurredAtChanged = EdgeWorkerExchangeCodec.CreateCommand(request with
        {
            OccurredAt = request.OccurredAt.AddSeconds(1)
        });
        var privacyChanged = EdgeWorkerExchangeCodec.CreateCommand(request with
        {
            PrivacyClass = "sensitive"
        });
        Assert.NotEqual(command.RequestSha256, occurredAtChanged.RequestSha256);
        Assert.NotEqual(command.RequestSha256, privacyChanged.RequestSha256);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Directive_auth_golden_uses_spki_key_id_and_pkcs1_sha256_end_to_end()
    {
        var contractRoot = Path.Combine(
            Fixture.RepositoryRoot(),
            "Modules/windows-edge-supervisor/contracts/provided");
        using var spec = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(contractRoot, "edge.bridge.directive.v1.auth.json")));
        var vector = spec.RootElement.GetProperty("golden_vectors")[0];
        Assert.Equal("RSA_PKCS1_SHA256", spec.RootElement.GetProperty("signature").GetProperty("algorithm").GetString());
        Assert.Equal("PKCS#1 v1.5", spec.RootElement.GetProperty("signature").GetProperty("rsa_padding").GetString());

        var wire = File.ReadAllBytes(Path.Combine(contractRoot, vector.GetProperty("wire_file").GetString()!));
        var directive = JsonSerializer.Deserialize<BridgeDirectiveV1>(wire)!;
        using var publicKey = RSA.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(
            vector.GetProperty("public_key_spki_base64").GetString()!), out _);
        Assert.Equal(
            vector.GetProperty("auth_key_id").GetString(),
            PinnedRsaTrustStore.ComputeKeyId(publicKey.ExportSubjectPublicKeyInfo()));
        Assert.Equal(
            vector.GetProperty("auth_body_sha256").GetString(),
            BridgeDirectiveAuthenticator.ComputeDirectiveBodySha256(directive));
        var statement = BridgeDirectiveAuthenticator.CreateSigningStatement(
            directive.AuthKeyId,
            directive.AuthNonce,
            directive.AuthIssuedAt,
            directive.AuthBodySha256);
        Assert.Equal(vector.GetProperty("signing_statement_hex").GetString(), Convert.ToHexStringLower(statement));
        var signature = Convert.FromBase64String(directive.AuthProof);
        Assert.True(publicKey.VerifyData(statement, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(publicKey.VerifyData(statement, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

        var trustRoot = Path.Combine(Path.GetTempPath(), "dps-directive-golden-trust", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(trustRoot);
        File.WriteAllText(Path.Combine(trustRoot, directive.AuthKeyId + ".pem"), publicKey.ExportSubjectPublicKeyInfoPem());
        using var trustStore = PinnedRsaTrustStore.LoadFromDirectory(trustRoot, [directive.AuthKeyId]);
        var decoded = BridgeDirectiveAuthenticator.DecodeAndVerify(
            wire,
            directive.AuthNonce,
            trustStore,
            DateTimeOffset.Parse("2026-07-15T00:00:01Z"),
            maximumClockSkewSeconds: 120);
        Assert.Equal("TYPE", decoded.ActionKind);

        var tampered = wire.ToArray();
        var index = Array.IndexOf(tampered, (byte)'h');
        Assert.True(index >= 0);
        tampered[index] = (byte)'H';
        Assert.Throws<InvalidDataException>(() => BridgeDirectiveAuthenticator.DecodeAndVerify(
            tampered,
            directive.AuthNonce,
            trustStore,
            DateTimeOffset.Parse("2026-07-15T00:00:01Z"),
            120));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Stage_and_cutover_require_capability_and_exact_durable_drain_truth()
    {
        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        var candidate = fixture.Artifact(WorkerSlot.B, 1);
        var blocked = Assert.Throws<InvalidOperationException>(() =>
            SupervisorSimulationAccess.PrepareCandidateLaunch(
                supervisor,
                candidate,
                fixture.VerifiedWaitingCapability(candidate)));
        Assert.Contains("verified PASS capability receipt", blocked.Message, StringComparison.Ordinal);
        supervisor.StageCandidate(candidate, fixture.VerifiedWaitingCapability(candidate));
        Assert.Throws<ArgumentException>(() => supervisor.AcquireRoute("db_NOT_LOWER_HEX"));

        var drain = supervisor.BeginDrain(fixture.DrainScope());
        fixture.DrainDirectiveSigningBroker.PauseNextSignature();
        var firstPreparation = supervisor.PrepareDrainDirectiveAsync(
            TestContext.Current.CancellationToken);
        await fixture.DrainDirectiveSigningBroker.WaitUntilSignatureRequestedAsync();
        var concurrentRetry = supervisor.PrepareDrainDirectiveAsync(
            TestContext.Current.CancellationToken);
        fixture.DrainDirectiveSigningBroker.ReleasePausedSignature();
        var preparedWires = await Task.WhenAll(firstPreparation, concurrentRetry);
        var preparedOnce = preparedWires[0];
        var preparedRetry = preparedWires[1];
        Assert.Equal(preparedOnce, preparedRetry);
        Assert.Equal(1, fixture.DrainDirectiveSigningBroker.SignCount);
        var resumedWithPreparedDirective = fixture.ResumeSupervisor();
        Assert.Equal(
            preparedOnce,
            await resumedWithPreparedDirective.PrepareDrainDirectiveAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.DrainDirectiveSigningBroker.SignCount);
        var signed = fixture.SignedDrainReceipt(drain);
        var envelope = WorkerDrainReceiptContractCodec.Deserialize(signed);
        fixture.JournalProvider.BeforeSignMutation = attestation => attestation with
        {
            JournalReceipt = attestation.JournalReceipt with { Durable = false }
        };
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        fixture.JournalProvider.BeforeSignMutation = null;
        Assert.False(await supervisor.TryCutoverAsync(
            JsonSerializer.SerializeToUtf8Bytes(
                envelope with { WorkerArtifactSha256 = new string('0', 64) }),
            TestContext.Current.CancellationToken));
        var staleButDurableReceipt = fixture.SignedDrainReceipt(
            drain,
            DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True(await supervisor.TryCutoverAsync(
            staleButDurableReceipt,
            TestContext.Current.CancellationToken));
        var proofPair = fixture.ReadLastDrainProofPair();
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(staleButDurableReceipt)),
            proofPair.WorkerWireSha256);
        Assert.Equal(fixture.JournalProvider.LastIssuedWireSha256, proofPair.JournalWireSha256);
        Assert.NotEqual(proofPair.WorkerWireSha256, proofPair.JournalWireSha256);

        _ = fixture.ResumeSupervisor();
        fixture.RemoveLastJournalProofDigestKeepingChecksumsValid();
        var halfPair = Assert.Throws<InvalidDataException>(() => fixture.ResumeSupervisor());
        Assert.Contains("proof digests as one pair", halfPair.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Durable_state_is_required_and_restart_rejects_tampering_or_inflight_truth()
    {
        var missing = Fixture.Create();
        Assert.Throws<InvalidOperationException>(() => missing.ResumeSupervisor());

        var initializePrepareCrash = Fixture.Create();
        initializePrepareCrash.StateAnchor.ThrowAfterPrepareOnce = true;
        Assert.Throws<InvalidOperationException>(() => initializePrepareCrash.Supervisor(
            initializePrepareCrash.Artifact(WorkerSlot.A, 0)));
        Assert.Null(initializePrepareCrash.StateAnchor.ReadSnapshot().Committed);
        Assert.NotNull(initializePrepareCrash.StateAnchor.ReadSnapshot().Prepared);
        Assert.False(initializePrepareCrash.StateStore.Exists);
        Assert.Throws<InvalidOperationException>(() => initializePrepareCrash.ResumeSupervisor());
        Assert.Equal(
            new SupervisorStateAnchorSnapshot(null, null),
            initializePrepareCrash.StateAnchor.ReadSnapshot());
        Assert.Equal(
            1,
            initializePrepareCrash.Supervisor(
                initializePrepareCrash.Artifact(WorkerSlot.A, 0)).DurableStateGeneration);

        var initializeCommitCrash = Fixture.Create();
        initializeCommitCrash.StateAnchor.ThrowBeforeCommitOnce = true;
        Assert.Throws<InvalidOperationException>(() => initializeCommitCrash.Supervisor(
            initializeCommitCrash.Artifact(WorkerSlot.A, 0)));
        Assert.True(initializeCommitCrash.StateStore.Exists);
        Assert.Null(initializeCommitCrash.StateAnchor.ReadSnapshot().Committed);
        Assert.NotNull(initializeCommitCrash.StateAnchor.ReadSnapshot().Prepared);
        Assert.Equal(1, initializeCommitCrash.ResumeSupervisor().DurableStateGeneration);
        Assert.Null(initializeCommitCrash.StateAnchor.ReadSnapshot().Prepared);

        var concurrentFixture = Fixture.Create();
        var concurrentSupervisor = concurrentFixture.Supervisor(
            concurrentFixture.Artifact(WorkerSlot.A, 0));
        var concurrentCandidate = concurrentFixture.Artifact(WorkerSlot.B, 10);
        concurrentFixture.StateAnchor.PauseNextPrepare();
        var concurrentAdvance = Task.Run(() => concurrentSupervisor.StageCandidate(
            concurrentCandidate,
            concurrentFixture.VerifiedWaitingCapability(concurrentCandidate)),
            TestContext.Current.CancellationToken);
        await concurrentFixture.StateAnchor.WaitUntilPreparePausedAsync();
        var concurrentResume = Task.Run(
            concurrentFixture.ResumeSupervisor,
            TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(concurrentResume.IsCompleted);
        }
        finally
        {
            concurrentFixture.StateAnchor.ReleasePausedPrepare();
        }
        await concurrentAdvance;
        var concurrentlyResumed = await concurrentResume;
        Assert.Equal(
            concurrentSupervisor.DurableStateGeneration,
            concurrentlyResumed.DurableStateGeneration);
        Assert.Null(concurrentFixture.StateAnchor.ReadSnapshot().Prepared);

        var mismatchedPrepareFixture = Fixture.Create();
        var mismatchedPrepareSupervisor = mismatchedPrepareFixture.Supervisor(
            mismatchedPrepareFixture.Artifact(WorkerSlot.A, 0));
        var mismatchedPrepareCandidate = mismatchedPrepareFixture.Artifact(WorkerSlot.B, 16);
        mismatchedPrepareFixture.StateAnchor.ReturnMismatchedPreparationOnce = true;
        var mismatchedPrepare = Assert.Throws<InvalidDataException>(
            () => mismatchedPrepareSupervisor.StageCandidate(
                mismatchedPrepareCandidate,
                mismatchedPrepareFixture.VerifiedWaitingCapability(mismatchedPrepareCandidate)));
        Assert.Contains("other than the exact requested", mismatchedPrepare.Message, StringComparison.Ordinal);
        Assert.NotNull(mismatchedPrepareFixture.StateAnchor.ReadSnapshot().Prepared);
        Assert.Equal(1, mismatchedPrepareFixture.ResumeSupervisor().DurableStateGeneration);

        var abaFixture = Fixture.Create();
        var abaSupervisor = abaFixture.Supervisor(abaFixture.Artifact(WorkerSlot.A, 0));
        var abaCandidate = abaFixture.Artifact(WorkerSlot.B, 11);
        var abaCapability = abaFixture.VerifiedWaitingCapability(abaCandidate);
        abaFixture.StateAnchor.ThrowAfterPrepareOnce = true;
        Assert.Throws<InvalidOperationException>(() => abaSupervisor.StageCandidate(
            abaCandidate,
            abaCapability));
        var stalePreparation = abaFixture.StateAnchor.ReadSnapshot().Prepared ??
            throw new InvalidOperationException("test stale preparation is missing");
        var abaRecovered = abaFixture.ResumeSupervisor();
        abaFixture.StateAnchor.PauseNextPrepare();
        var repeatedAdvance = Task.Run(() => abaRecovered.StageCandidate(
            abaCandidate,
            abaCapability),
            TestContext.Current.CancellationToken);
        await abaFixture.StateAnchor.WaitUntilPreparePausedAsync();
        try
        {
            var currentPreparation = abaFixture.StateAnchor.ReadSnapshot().Prepared ??
                throw new InvalidOperationException("test current preparation is missing");
            Assert.Equal(stalePreparation.Next, currentPreparation.Next);
            Assert.NotEqual(stalePreparation.Token, currentPreparation.Token);
            Assert.False(abaFixture.StateAnchor.TryCommit(stalePreparation));
        }
        finally
        {
            abaFixture.StateAnchor.ReleasePausedPrepare();
        }
        await repeatedAdvance;

        var rejectCommitFixture = Fixture.Create();
        var rejectCommitSupervisor = rejectCommitFixture.Supervisor(
            rejectCommitFixture.Artifact(WorkerSlot.A, 0));
        var rejectCommitCandidate = rejectCommitFixture.Artifact(WorkerSlot.B, 12);
        rejectCommitFixture.StateAnchor.RejectCommitOnce = true;
        Assert.Throws<InvalidOperationException>(() => rejectCommitSupervisor.StageCandidate(
            rejectCommitCandidate,
            rejectCommitFixture.VerifiedWaitingCapability(rejectCommitCandidate)));
        Assert.NotNull(rejectCommitFixture.StateAnchor.ReadSnapshot().Prepared);
        Assert.Equal(2, rejectCommitFixture.ResumeSupervisor().DurableStateGeneration);

        var afterCommitFixture = Fixture.Create();
        var afterCommitSupervisor = afterCommitFixture.Supervisor(
            afterCommitFixture.Artifact(WorkerSlot.A, 0));
        var afterCommitCandidate = afterCommitFixture.Artifact(WorkerSlot.B, 13);
        afterCommitFixture.StateAnchor.ThrowAfterCommitOnce = true;
        Assert.Throws<InvalidOperationException>(() => afterCommitSupervisor.StageCandidate(
            afterCommitCandidate,
            afterCommitFixture.VerifiedWaitingCapability(afterCommitCandidate)));
        Assert.Null(afterCommitFixture.StateAnchor.ReadSnapshot().Prepared);
        Assert.Equal(2, afterCommitFixture.ResumeSupervisor().DurableStateGeneration);

        var rejectAbortFixture = Fixture.Create();
        var rejectAbortSupervisor = rejectAbortFixture.Supervisor(
            rejectAbortFixture.Artifact(WorkerSlot.A, 0));
        var rejectAbortCandidate = rejectAbortFixture.Artifact(WorkerSlot.B, 14);
        rejectAbortFixture.StateAnchor.ThrowAfterPrepareOnce = true;
        Assert.Throws<InvalidOperationException>(() => rejectAbortSupervisor.StageCandidate(
            rejectAbortCandidate,
            rejectAbortFixture.VerifiedWaitingCapability(rejectAbortCandidate)));
        rejectAbortFixture.StateAnchor.RejectAbortOnce = true;
        Assert.Throws<InvalidOperationException>(() => rejectAbortFixture.ResumeSupervisor());
        Assert.NotNull(rejectAbortFixture.StateAnchor.ReadSnapshot().Prepared);
        Assert.Equal(1, rejectAbortFixture.ResumeSupervisor().DurableStateGeneration);

        var afterAbortFixture = Fixture.Create();
        var afterAbortSupervisor = afterAbortFixture.Supervisor(
            afterAbortFixture.Artifact(WorkerSlot.A, 0));
        var afterAbortCandidate = afterAbortFixture.Artifact(WorkerSlot.B, 15);
        afterAbortFixture.StateAnchor.ThrowAfterPrepareOnce = true;
        Assert.Throws<InvalidOperationException>(() => afterAbortSupervisor.StageCandidate(
            afterAbortCandidate,
            afterAbortFixture.VerifiedWaitingCapability(afterAbortCandidate)));
        afterAbortFixture.StateAnchor.ThrowAfterAbortOnce = true;
        Assert.Throws<InvalidOperationException>(() => afterAbortFixture.ResumeSupervisor());
        Assert.Null(afterAbortFixture.StateAnchor.ReadSnapshot().Prepared);
        Assert.Equal(1, afterAbortFixture.ResumeSupervisor().DurableStateGeneration);

        var thirdStateFixture = Fixture.Create();
        _ = thirdStateFixture.Supervisor(thirdStateFixture.Artifact(WorkerSlot.A, 0));
        thirdStateFixture.CreateValidThirdStateOutsideAnchorHeads();
        var thirdState = Assert.Throws<InvalidDataException>(
            () => thirdStateFixture.ResumeSupervisor());
        Assert.Contains("neither the committed nor the prepared", thirdState.Message, StringComparison.Ordinal);

        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        Assert.Throws<IOException>(() => new DurableSupervisorStateStore(
            fixture.StateStore.StatePath,
            fixture.StateAnchor));
        Assert.Equal(1, supervisor.DurableStateGeneration);
        var bootstrapSnapshot = File.ReadAllBytes(fixture.StateStore.StatePath);
        using (supervisor.AcquireRoute("db_" + new string('b', 32)))
        {
            Assert.Throws<InvalidOperationException>(() => fixture.ResumeSupervisor());
        }
        var resumed = fixture.ResumeSupervisor();
        Assert.Equal(WorkerSlot.A, resumed.ActiveSlot);
        Assert.True(resumed.DurableStateGeneration >= 3);

        var beforePrepareCrashGeneration = resumed.DurableStateGeneration;
        var prepareCrashCandidate = fixture.Artifact(WorkerSlot.B, 20);
        fixture.StateAnchor.ThrowAfterPrepareOnce = true;
        Assert.Throws<InvalidOperationException>(() => resumed.StageCandidate(
            prepareCrashCandidate,
            fixture.VerifiedWaitingCapability(prepareCrashCandidate)));
        Assert.NotNull(fixture.StateAnchor.ReadSnapshot().Prepared);
        var afterPrepareCrashRecovery = fixture.ResumeSupervisor();
        Assert.Equal(beforePrepareCrashGeneration, afterPrepareCrashRecovery.DurableStateGeneration);
        Assert.Null(fixture.StateAnchor.ReadSnapshot().Prepared);

        var commitCrashCandidate = fixture.Artifact(WorkerSlot.B, 21);
        fixture.StateAnchor.ThrowBeforeCommitOnce = true;
        Assert.Throws<InvalidOperationException>(() => afterPrepareCrashRecovery.StageCandidate(
            commitCrashCandidate,
            fixture.VerifiedWaitingCapability(commitCrashCandidate)));
        Assert.NotNull(fixture.StateAnchor.ReadSnapshot().Prepared);
        var afterCommitCrashRecovery = fixture.ResumeSupervisor();
        Assert.Equal(
            beforePrepareCrashGeneration + 1,
            afterCommitCrashRecovery.DurableStateGeneration);
        Assert.Null(fixture.StateAnchor.ReadSnapshot().Prepared);
        _ = afterCommitCrashRecovery.BeginDrain(fixture.DrainScope());
        Assert.Equal(
            beforePrepareCrashGeneration + 2,
            afterCommitCrashRecovery.DurableStateGeneration);

        var statePath = fixture.StateStore.StatePath;
        var currentSnapshot = File.ReadAllBytes(statePath);
        File.WriteAllBytes(statePath, bootstrapSnapshot);
        Assert.Throws<InvalidDataException>(() => fixture.ResumeSupervisor());
        File.WriteAllBytes(statePath, currentSnapshot);
        var state = File.ReadAllText(statePath);
        File.WriteAllText(statePath, state.Replace(
            "\"routing_epoch\":0",
            "\"routing_epoch\":1",
            StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => fixture.ResumeSupervisor());

        var directiveFixture = Fixture.Create();
        var directiveSupervisor = directiveFixture.Supervisor(
            directiveFixture.Artifact(WorkerSlot.A, 0));
        _ = directiveSupervisor.BeginDrain(directiveFixture.DrainScope());
        _ = await directiveSupervisor.PrepareDrainDirectiveAsync(
            TestContext.Current.CancellationToken);
        directiveFixture.TamperPreparedDirectiveSignatureKeepingChecksumsValid();
        Assert.Throws<CryptographicException>(() => directiveFixture.ResumeSupervisor());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task Drain_codec_rejects_forged_worker_journal_and_policy_truth()
    {
        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        var candidate = fixture.Artifact(WorkerSlot.B, 1);
        supervisor.StageCandidate(candidate, fixture.VerifiedWaitingCapability(candidate));
        var drain = supervisor.BeginDrain(fixture.DrainScope());
        _ = await supervisor.PrepareDrainDirectiveAsync(TestContext.Current.CancellationToken);
        var signed = fixture.SignedDrainReceipt(drain);
        var envelope = WorkerDrainReceiptContractCodec.Deserialize(signed);
        var workerWireSha256 = Convert.ToHexStringLower(SHA256.HashData(signed));
        var canonicalPayload = WorkerDrainReceiptContractCodec.CreateJournalPayload(
            envelope,
            workerWireSha256);
        Assert.Equal(
            "{\"drain_id\":\"" + envelope.DrainId +
            "\",\"intake_stopped\":true,\"journal_artifact_sha256\":\"" + envelope.JournalArtifactSha256 +
            "\",\"protected_policy_sha256\":\"" + envelope.ProtectedPolicySha256 +
            "\",\"release_bom_sha256\":\"" + envelope.ReleaseBomSha256 +
            "\",\"remaining_in_flight\":0,\"routing_epoch\":" + envelope.RoutingEpoch +
            ",\"schema_version\":\"1.0\",\"slot\":\"" + envelope.Slot +
            "\",\"worker_artifact_sha256\":\"" + envelope.WorkerArtifactSha256 +
            "\",\"worker_drained\":true,\"worker_receipt_wire_sha256\":\"" + workerWireSha256 +
            "\",\"worker_version\":\"" + envelope.WorkerVersion + "\"}",
            System.Text.Encoding.UTF8.GetString(canonicalPayload));
        fixture.JournalProvider.BeforeSignMutation = attestation => attestation with
        {
            ReleaseBomSha256 = new string('9', 64)
        };
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        fixture.JournalProvider.BeforeSignMutation = attestation => attestation with
        {
            RequestId = "drainreq_" + new string('0', 64)
        };
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        fixture.JournalProvider.BeforeSignMutation = attestation => attestation with
        {
            SignatureKeyId = envelope.WorkerKeyId
        };
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        fixture.JournalProvider.BeforeSignMutation = attestation => attestation with
        {
            SignatureKeyId = "sha256_" + new string('8', 64)
        };
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        fixture.JournalProvider.BeforeSignMutation = null;
        fixture.JournalProvider.AfterSignMutation = attestation => attestation with
        {
            Signature = Convert.ToBase64String(new byte[256])
        };
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        fixture.JournalProvider.AfterSignMutation = null;
        fixture.JournalProvider.BeforeSignMutation = null;
        fixture.JournalProvider.HangRequests = true;
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        Assert.True(fixture.JournalProvider.LastRequestCancellationToken.CanBeCanceled);
        Assert.True(fixture.JournalProvider.LastRequestCancellationToken.IsCancellationRequested);
        fixture.JournalProvider.HangRequests = false;
        fixture.JournalProvider.RejectRequests = true;
        Assert.False(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
        fixture.JournalProvider.RejectRequests = false;
        Assert.False(await supervisor.TryCutoverAsync(
            JsonSerializer.SerializeToUtf8Bytes(
                envelope with { ReleaseBomSha256 = new string('9', 64) }),
            TestContext.Current.CancellationToken));
        Assert.True(await supervisor.TryCutoverAsync(signed, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Windows_gate_configuration_is_bound_to_external_digest_bom_policy_host_and_trust()
    {
        var fixture = Fixture.Create();
        var candidate = fixture.Artifact(WorkerSlot.B, 1);
        var configuration = new WindowsGateConfiguration(
            "dps.windows-edge-supervisor-gate/v1",
            fixture.CapabilityTrustRoot,
            [fixture.CapabilityKeyId],
            fixture.CapabilityTrustStore.StoreFingerprint,
            fixture.Deployment.ReleaseBomSha256,
            fixture.Deployment.ProtectedPolicySha256,
            fixture.Deployment.HostId,
            candidate.Sha256,
            candidate.Version,
            candidate.Slot.ToString(),
            42,
            fixture.ZennoDroidStartedAt,
            fixture.BridgeKeyId,
            100,
            new string('1', 64),
            new string('2', 64),
            86400,
            0,
            100,
            86400,
            300,
            30,
            Path.Combine(fixture.Root, "capability.json"));
        var path = Path.Combine(fixture.Root, "windows-gate.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration);
        File.WriteAllBytes(path, bytes);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var binding = new WindowsGateProcessBinding(
            digest,
            fixture.Deployment.ReleaseBomSha256,
            fixture.Deployment.ProtectedPolicySha256,
            fixture.CapabilityTrustStore.StoreFingerprint,
            fixture.Deployment.HostId,
            fixture.BridgeKeyId);
        Assert.Equal(candidate.Sha256, WindowsGateConfigurationCodec.Load(path, binding).ExpectedWorkerArtifactSha256);
        Assert.Throws<InvalidDataException>(() => WindowsGateConfigurationCodec.Load(
            path,
            binding with { ConfigurationSha256 = new string('0', 64) }));
        Assert.Throws<InvalidDataException>(() => WindowsGateConfigurationCodec.Load(
            path,
            binding with { TrustStoreFingerprint = new string('0', 64) }));
        var nullKeyBytes = JsonSerializer.SerializeToUtf8Bytes(configuration with
        {
            AllowedWindowsEvidenceKeyIds = [null!]
        });
        var nullKeyPath = Path.Combine(fixture.Root, "windows-gate-null-key.json");
        File.WriteAllBytes(nullKeyPath, nullKeyBytes);
        Assert.Throws<InvalidDataException>(() => WindowsGateConfigurationCodec.Load(
            nullKeyPath,
            binding with
            {
                ConfigurationSha256 = Convert.ToHexStringLower(SHA256.HashData(nullKeyBytes))
            }));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Capability_codec_derives_attestation_truth_from_signature_and_fixed_trust()
    {
        var fixture = Fixture.Create();
        var candidate = fixture.Artifact(WorkerSlot.B, 1);
        var evidence = fixture.SignedCapabilityEvidence(candidate);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        var verification = CapabilityEvidenceCodec.DecodeAndVerify(
            bytes,
            fixture.CapabilityTrustStore,
            fixture.CapabilityExpectation(candidate));
        Assert.True(verification.AttestationVerified);
        Assert.Equal("WAITING_EXTERNAL", verification.Assessment.Status);
        Assert.Null(verification.Assessment.VerificationClaim);
        Assert.Equal(["worker-launch-runtime-abi-unavailable"], verification.Assessment.Missing);

        var selfClaimed = evidence with
        {
            AttestationSignature = Convert.ToBase64String(new byte[256])
        };
        Assert.Throws<InvalidDataException>(() => CapabilityEvidenceCodec.DecodeAndVerify(
            JsonSerializer.SerializeToUtf8Bytes(selfClaimed),
            fixture.CapabilityTrustStore,
            fixture.CapabilityExpectation(candidate)));
        var unknown = JsonSerializer.Serialize(evidence).Replace(
            "\"missing\":",
            "\"unexpected\":true,\"missing\":",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => CapabilityEvidenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetBytes(unknown),
            fixture.CapabilityTrustStore,
            fixture.CapabilityExpectation(candidate)));
        Assert.Throws<InvalidDataException>(() => CapabilityEvidenceCodec.DecodeAndVerify(
            JsonSerializer.SerializeToUtf8Bytes(evidence with { Missing = null! }),
            fixture.CapabilityTrustStore,
            fixture.CapabilityExpectation(candidate)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Capability_simulation_is_explicit_and_missing_local_environment_waits()
    {
        var waiting = new CapabilityProbe().Evaluate(CapabilityProbe.CaptureLocalPrerequisites());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("WAITING_EXTERNAL", waiting.Status);
            Assert.Null(waiting.VerificationClaim);
        }

        var time = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        var simulated = new CapabilitySnapshot(
            "SIMULATION", new string('a', 64), true, "7.5", "1.0", 42, 42, time, time, "4.8", "5", true, true, true,
            "1.0", 2, "edge.bridge.exchange/v1", 28741, 15000, "fail-closed-unknown-outcome-no-implicit-retry/v1", null, null, null,
            0, null, null, 86400, 0, 100, 86400);
        var assessment = new CapabilityProbe().Evaluate(simulated);
        Assert.Equal("WAITING_EXTERNAL", assessment.Status);
        Assert.Null(assessment.VerificationClaim);
        Assert.Contains("trusted-cryptographic-attestation", assessment.Missing);

    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Arbitrary_self_signed_keys_cannot_attest_artifacts_or_windows_evidence()
    {
        var fixture = Fixture.Create();
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        using var rogue = RSA.Create(2048);
        var rogueDirectiveBroker = new TestDrainDirectiveSigningBroker(
            rogue,
            PinnedRsaTrustStore.ComputeKeyId(rogue.ExportSubjectPublicKeyInfo()));
        Assert.Throws<InvalidOperationException>(() => fixture.Supervisor(
            fixture.Artifact(WorkerSlot.A, 99),
            rogueDirectiveBroker));
        var overlappingTrust = Assert.Throws<InvalidOperationException>(() =>
            fixture.ConstructWithOverlappingDirectiveAndWorkerTrust(
                fixture.Artifact(WorkerSlot.A, 98)));
        Assert.Contains("pairwise disjoint", overlappingTrust.Message, StringComparison.Ordinal);
        var candidate = fixture.Artifact(WorkerSlot.B, 1);
        var rogueArtifactSignature = Convert.ToBase64String(rogue.SignData(
            WorkerArtifactSigning.CreateStatement(
                candidate.Slot,
                candidate.Version,
                candidate.Sha256,
                candidate.HealthEvidenceSha256,
                candidate.ShadowEvidenceSha256,
                candidate.RuntimeManifestSha256,
                candidate.VersionDirectorySecuritySha256),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
        Assert.Throws<InvalidOperationException>(() => supervisor.StageCandidate(
            candidate with { SignatureBase64 = rogueArtifactSignature },
            fixture.VerifiedWaitingCapability(candidate)));

        var time = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        var claimedReal = new CapabilitySnapshot(
            "REAL_WINDOWS_ATTESTED", null, true, "7.6.2", "1.0", 42, 42, time, time, "4.8", "5", true, true, true,
            "37.0.0-14910828", 2, "edge.bridge.exchange/v1", 28741, 15000, "fail-closed-unknown-outcome-no-implicit-retry/v1",
            "WINDOWS_IDENTITY_AND_PINNED_RSA", fixture.BridgeKeyId, "RSA_PKCS1_SHA256",
            100, new string('1', 64), new string('2', 64), 86400, 0, 100, 86400);
        var statement = CapabilityProbe.CreateSigningStatement(claimedReal);
        claimedReal = claimedReal with
        {
            RawEvidenceSha256 = Convert.ToHexString(SHA256.HashData(statement)).ToLowerInvariant()
        };
        var rogueEvidenceSignature = Convert.ToBase64String(rogue.SignData(
            statement,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
        var assessment = new CapabilityProbe(fixture.CapabilityTrustStore).Evaluate(
            claimedReal,
            new SignedWindowsEvidence(fixture.CapabilityKeyId, "RSA_PSS_SHA256", rogueEvidenceSignature));
        Assert.Equal("WAITING_EXTERNAL", assessment.Status);
        Assert.Null(assessment.VerificationClaim);
        Assert.Contains("trusted-cryptographic-attestation", assessment.Missing);

        var privateTrustRoot = Path.Combine(fixture.Root, "private-trust-must-fail");
        Directory.CreateDirectory(privateTrustRoot);
        File.WriteAllText(Path.Combine(privateTrustRoot, fixture.KeyId + ".pem"), fixture.ExportPrivateKeyPemForNegativeTest());
        Assert.Throws<InvalidOperationException>(() =>
            PinnedRsaTrustStore.LoadFromDirectory(privateTrustRoot, [fixture.KeyId]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Append_only_evidence_log_reopens_exact_chain_and_rejects_tampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "dps-edge-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "supervisor-evidence.jsonl");
        EvidenceLogCheckpoint checkpoint;
        using (var log = new AppendOnlyEvidenceLog(root, path))
        {
            _ = log.Append("host.start", Encoding.UTF8.GetBytes("first"));
            checkpoint = log.Append("bridge.poll.wait", Encoding.UTF8.GetBytes("second"));
        }
        Assert.Equal(2, checkpoint.EntryCount);
        Assert.NotEqual(new string('0', 64), checkpoint.HeadSha256);
        Assert.Equal(64, checkpoint.FileIdentitySha256.Length);
        using (var reopened = new AppendOnlyEvidenceLog(root, path))
        {
            Assert.Equal(checkpoint, reopened.Checkpoint);
        }

        var tampered = File.ReadAllText(path).Replace(
            "\"sequence\":1",
            "\"sequence\":2",
            StringComparison.Ordinal);
        File.WriteAllText(path, tampered, new UTF8Encoding(false, true));
        Assert.Throws<InvalidDataException>(() => new AppendOnlyEvidenceLog(root, path));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Protected_path_detects_parent_replacement_and_intermediate_links()
    {
        var root = Path.Combine(Path.GetTempPath(), "dps-edge-path-" + Guid.NewGuid().ToString("N"));
        var version = Path.Combine(root, "v1");
        Directory.CreateDirectory(version);
        var binary = Path.Combine(version, "worker.exe");
        File.WriteAllText(binary, "signed-worker");
        var proof = SecurePathProof.CaptureFile(root, binary);
        Directory.Move(version, version + ".old");
        Directory.CreateDirectory(version);
        File.WriteAllText(binary, "signed-worker");
        Assert.Throws<InvalidOperationException>(proof.Revalidate);

        if (!OperatingSystem.IsWindows())
        {
            var external = Path.Combine(Path.GetTempPath(), "dps-edge-linked-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "worker.exe"), "signed-worker");
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), external);
            Assert.Throws<InvalidOperationException>(() =>
                SecurePathProof.CaptureFile(root, Path.Combine(root, "linked", "worker.exe")));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Signed_runtime_closure_rejects_same_size_rewrite_and_unsigned_extra_file()
    {
        var rewriteFixture = Fixture.Create();
        var rewriteArtifact = rewriteFixture.Artifact(WorkerSlot.A, 0);
        var proof = WorkerRuntimeClosureProof.Capture(rewriteFixture.Root, rewriteArtifact);
        var originalTimestamp = File.GetLastWriteTimeUtc(rewriteArtifact.BinaryPath);
        var rewritten = File.ReadAllBytes(rewriteArtifact.BinaryPath);
        rewritten[0] ^= 0xff;
        File.WriteAllBytes(rewriteArtifact.BinaryPath, rewritten);
        File.SetLastWriteTimeUtc(rewriteArtifact.BinaryPath, originalTimestamp);
        Assert.Throws<InvalidDataException>(() =>
            SupervisorSimulationAccess.LockForLaunch(proof));

        var extraFixture = Fixture.Create();
        var extraArtifact = extraFixture.Artifact(WorkerSlot.A, 0);
        File.WriteAllText(
            Path.Combine(extraArtifact.VersionDirectory, "unsigned.dll"),
            "unsigned-runtime-dependency");
        Assert.Throws<InvalidOperationException>(() =>
            WorkerRuntimeClosureProof.Capture(extraFixture.Root, extraArtifact));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Zenno_exchange_decoder_accepts_only_exact_v1_poll_truth_table()
    {
        var poll = new BridgeExchangeV1(
            "1.0",
            "edge.bridge.exchange/v1",
            "zenno-bridge",
            "soul_" + new string('a', 64),
            "db_" + new string('b', 32),
            "pa_" + new string('c', 32),
            "trace_" + new string('d', 32),
            "idem_" + new string('e', 64),
            "2026-07-15T00:00:00Z",
            "internal",
            new string('f', 64),
            "POLL",
            null, null, null, null, null, null, null, null, null, null);
        var wire = JsonSerializer.SerializeToUtf8Bytes(poll);
        Assert.Equal(poll, BridgeExchangeCodec.Decode(wire));

        using var document = JsonDocument.Parse(wire);
        var unknown = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        unknown["model_instruction"] = "run shell";
        Assert.Throws<InvalidDataException>(() =>
            BridgeExchangeCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(unknown)));
        var unknownMajor = poll with { ContractId = "edge.bridge.exchange/v2" };
        Assert.Throws<InvalidDataException>(() =>
            BridgeExchangeCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(unknownMajor)));
        var unknownAction = poll with
        {
            ExchangeKind = "NATIVE_RESULT",
            CommandId = "command-1",
            ActionKind = "SHELL",
            StepKind = "EXECUTE",
            NativeStatus = "SUCCESS",
            NativeDetail = "not accepted",
            PostconditionVerified = true
        };
        Assert.Throws<InvalidDataException>(() =>
            BridgeExchangeCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(unknownAction)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Windows_host_configuration_is_digest_bound_and_fixed_to_the_bridge_abi()
    {
        var root = Path.Combine(Path.GetTempPath(), "dps-edge-host-" + Guid.NewGuid().ToString("N"));
        var workers = Path.Combine(root, "workers");
        var evidence = Path.Combine(root, "evidence", "supervisor.jsonl");
        Directory.CreateDirectory(workers);
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        var hostId = "host_" + new string('a', 64);
        var keyId = "sha256_" + new string('b', 64);
        var configuration = new WindowsHostConfiguration(
            "dps.windows-edge-supervisor-host/v1",
            hostId,
            "127.0.0.1",
            28741,
            "/dps/edge/v1/exchange",
            15000,
            64 * 1024,
            ["S-1-5-18"],
            new string('c', 40),
            keyId,
            root,
            workers,
            evidence,
            new string('d', 64),
            new string('e', 64));
        var path = Path.Combine(root, "host.json");
        var wire = JsonSerializer.SerializeToUtf8Bytes(configuration);
        File.WriteAllBytes(path, wire);
        var binding = new WindowsHostProcessBinding(
            Convert.ToHexStringLower(SHA256.HashData(wire)),
            hostId,
            configuration.ReleaseBomSha256,
            configuration.ProtectedPolicySha256,
            keyId);
        var loaded = WindowsHostConfigurationCodec.Load(path, binding);
        Assert.Equal(configuration with { AllowedClientSids = loaded.AllowedClientSids }, loaded);
        Assert.Equal(configuration.AllowedClientSids, loaded.AllowedClientSids);

        var wrongPortWire = JsonSerializer.SerializeToUtf8Bytes(configuration with { ListenPort = 28742 });
        File.WriteAllBytes(path, wrongPortWire);
        Assert.Throws<InvalidDataException>(() => WindowsHostConfigurationCodec.Load(
            path,
            binding with { ConfigurationSha256 = Convert.ToHexStringLower(SHA256.HashData(wrongPortWire)) }));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unavailable_launch_abi_blocks_before_worker_process_or_runtime_channel()
    {
        var fixture = Fixture.Create();
        var artifact = fixture.Artifact(WorkerSlot.B, 1);
        var supervisor = fixture.Supervisor(fixture.Artifact(WorkerSlot.A, 0));
        using var processes = new SyntheticProcessController();
        var evidenceRoot = Path.Combine(fixture.Root, "coordinator-evidence");
        Directory.CreateDirectory(evidenceRoot);
        using var evidence = new AppendOnlyEvidenceLog(
            evidenceRoot,
            Path.Combine(evidenceRoot, "supervisor.jsonl"));
        var coordinator = new AbWorkerProcessCoordinator(
            supervisor,
            processes,
            new UnavailableWorkerRuntimeChannel(),
            evidence);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StageCandidateAsync(
                artifact,
                fixture.VerifiedWaitingCapability(artifact),
                TestContext.Current.CancellationToken));
        Assert.False(processes.Started);
        Assert.False(processes.Terminated);
        Assert.Equal(WorkerSlot.A, supervisor.ActiveSlot);
    }

    private sealed class SyntheticProcessController : IWorkerProcessController
    {
        private WorkerProcessIdentity? _identity;
        public bool Started { get; private set; }
        public bool Terminated { get; private set; }

        public WorkerProcessIdentity StartCandidate(CandidateLaunchAuthorization authorization)
        {
            Started = true;
            var artifact = authorization.Artifact;
            _identity = new WorkerProcessIdentity(
                artifact.Slot,
                123,
                DateTimeOffset.UtcNow,
                artifact.Version,
                artifact.Sha256,
                new string('f', 64));
            return _identity;
        }

        public WorkerProcessIdentity GetRequired(WorkerSlot slot) =>
            _identity is not null && _identity.Slot == slot
                ? _identity
                : throw new InvalidOperationException("synthetic Worker is not running");

        public void Revalidate(WorkerSlot slot) => _ = GetRequired(slot);

        public void Terminate(WorkerSlot slot)
        {
            if (_identity?.Slot == slot) _identity = null;
            Terminated = true;
        }

        public void Dispose() => _identity = null;
    }

    private static string ConditionalKind(JsonElement conditional) =>
        conditional.GetProperty("if").GetProperty("properties")
            .GetProperty("exchange_kind").GetProperty("const").GetString()!;

    private static JsonElement ConditionalProperties(JsonElement[] conditionals, string kind) =>
        conditionals.Single(conditional => ConditionalKind(conditional) == kind)
            .GetProperty("then").GetProperty("properties");

    private static EdgeWorkerCommandRequest GoldenCommandRequest() => new(
        "soul_" + new string('a', 64),
        "db_" + new string('b', 32),
        "pa_" + new string('c', 32),
        "trace_" + new string('d', 32),
        "idem_" + new string('e', 64),
        DateTimeOffset.Parse("2026-07-14T00:00:00+00:00"),
        "personal",
        "command-vector-1",
        "lease-vector-1",
        DateTimeOffset.Parse("2026-07-14T00:05:00+00:00"),
        "TYPE",
        "TYPE_TEXT",
        "fixture:input",
        "hello\n世界",
        null,
        "fixture contains text",
        true);

}

/// <summary>
/// Test-only reflection adapter for the non-public state-machine seam. Product
/// callers can reach mutation only through AbWorkerProcessCoordinator; these
/// tests deliberately label direct state transitions as simulation evidence.
/// </summary>
internal static class SupervisorSimulationAccess
{
    public static AbWorkerSupervisor Bootstrap(
        string approvedVersionRoot,
        WorkerArtifact initialArtifact,
        IEnumerable<string> requiredCapabilities,
        PinnedRsaTrustStore artifactTrustStore,
        PinnedRsaTrustStore drainDirectiveTrustStore,
        PinnedRsaTrustStore workerDrainTrustStore,
        PinnedRsaTrustStore journalDrainTrustStore,
        IDrainDirectiveSigningBroker drainDirectiveSigningBroker,
        IJournalDrainAttestationProvider journalDrainAttestationProvider,
        SupervisorDeploymentBinding deployment,
        DurableSupervisorStateStore stateStore) =>
        InvokeStatic<AbWorkerSupervisor>(
            "Bootstrap",
            approvedVersionRoot,
            initialArtifact,
            requiredCapabilities,
            artifactTrustStore,
            drainDirectiveTrustStore,
            workerDrainTrustStore,
            journalDrainTrustStore,
            drainDirectiveSigningBroker,
            journalDrainAttestationProvider,
            deployment,
            stateStore);

    public static AbWorkerSupervisor Resume(
        string approvedVersionRoot,
        IEnumerable<string> requiredCapabilities,
        PinnedRsaTrustStore artifactTrustStore,
        PinnedRsaTrustStore drainDirectiveTrustStore,
        PinnedRsaTrustStore workerDrainTrustStore,
        PinnedRsaTrustStore journalDrainTrustStore,
        IDrainDirectiveSigningBroker drainDirectiveSigningBroker,
        IJournalDrainAttestationProvider journalDrainAttestationProvider,
        SupervisorDeploymentBinding deployment,
        DurableSupervisorStateStore stateStore) =>
        InvokeStatic<AbWorkerSupervisor>(
            "Resume",
            approvedVersionRoot,
            requiredCapabilities,
            artifactTrustStore,
            drainDirectiveTrustStore,
            workerDrainTrustStore,
            journalDrainTrustStore,
            drainDirectiveSigningBroker,
            journalDrainAttestationProvider,
            deployment,
            stateStore);

    public static void StageCandidate(
        this AbWorkerSupervisor supervisor,
        WorkerArtifact candidate,
        CapabilityEvidenceVerification unusedCapability)
    {
        _ = unusedCapability;
        InvokeVoid(supervisor, "StageCandidateForSimulation", candidate);
    }

    public static CandidateLaunchAuthorization PrepareCandidateLaunch(
        AbWorkerSupervisor supervisor,
        WorkerArtifact candidate,
        CapabilityEvidenceVerification capability) =>
        Invoke<CandidateLaunchAuthorization>(
            supervisor,
            "PrepareCandidateLaunch",
            candidate,
            capability);

    public static IDisposable LockForLaunch(WorkerRuntimeClosureProof proof) =>
        Invoke<IDisposable>(proof, "LockForLaunch");

    public static RouteLease AcquireRoute(
        this AbWorkerSupervisor supervisor,
        string deviceBindingId) =>
        Invoke<RouteLease>(supervisor, "AcquireRoute", deviceBindingId);

    public static DrainExpectation BeginDrain(
        this AbWorkerSupervisor supervisor,
        DrainScope scope) =>
        Invoke<DrainExpectation>(supervisor, "BeginDrain", scope);

    public static Task<byte[]> PrepareDrainDirectiveAsync(
        this AbWorkerSupervisor supervisor,
        CancellationToken cancellationToken) =>
        Invoke<Task<byte[]>>(
            supervisor,
            "PrepareDrainDirectiveAsync",
            cancellationToken);

    public static Task<bool> TryCutoverAsync(
        this AbWorkerSupervisor supervisor,
        ReadOnlyMemory<byte> signedWorkerDrainReceipt,
        CancellationToken cancellationToken) =>
        Invoke<Task<bool>>(
            supervisor,
            "TryCutoverAsync",
            signedWorkerDrainReceipt,
            cancellationToken);

    public static Task<bool> TryRollbackAsync(
        this AbWorkerSupervisor supervisor,
        ReadOnlyMemory<byte> signedWorkerDrainReceipt,
        CancellationToken cancellationToken) =>
        Invoke<Task<bool>>(
            supervisor,
            "TryRollbackAsync",
            signedWorkerDrainReceipt,
            cancellationToken);

    private static void InvokeVoid(object target, string name, params object?[] arguments) =>
        _ = InvokeCore(target, name, arguments);

    private static T Invoke<T>(object target, string name, params object?[] arguments) =>
        (T)(InvokeCore(target, name, arguments) ??
            throw new InvalidOperationException("simulation method returned null"));

    private static object? InvokeCore(object target, string name, object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(target.GetType().FullName, name);
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw new InvalidOperationException("unreachable");
        }
    }

    private static T InvokeStatic<T>(string name, params object?[] arguments)
    {
        var method = typeof(AbWorkerSupervisor).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(typeof(AbWorkerSupervisor).FullName, name);
        try
        {
            return (T)(method.Invoke(null, arguments) ??
                throw new InvalidOperationException("simulation factory returned null"));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw new InvalidOperationException("unreachable");
        }
    }
}

internal sealed class Fixture
{
    private static readonly string[] RequiredCapabilities = ["bridge-abi-v1", "journal-v1"];

    private Fixture(string root)
    {
        Root = root;
        SigningKey = RSA.Create(2048);
        KeyId = PinnedRsaTrustStore.ComputeKeyId(SigningKey.ExportSubjectPublicKeyInfo());
        var trustRoot = Path.Combine(root, "trust-artifact");
        Directory.CreateDirectory(trustRoot);
        File.WriteAllText(Path.Combine(trustRoot, KeyId + ".pem"), SigningKey.ExportSubjectPublicKeyInfoPem());
        TrustStore = PinnedRsaTrustStore.LoadFromDirectory(trustRoot, [KeyId]);

        CapabilitySigningKey = RSA.Create(2048);
        CapabilityKeyId = PinnedRsaTrustStore.ComputeKeyId(
            CapabilitySigningKey.ExportSubjectPublicKeyInfo());
        CapabilityTrustRoot = Path.Combine(root, "trust-capability");
        Directory.CreateDirectory(CapabilityTrustRoot);
        File.WriteAllText(
            Path.Combine(CapabilityTrustRoot, CapabilityKeyId + ".pem"),
            CapabilitySigningKey.ExportSubjectPublicKeyInfoPem());
        CapabilityTrustStore = PinnedRsaTrustStore.LoadFromDirectory(
            CapabilityTrustRoot,
            [CapabilityKeyId]);

        BridgeSigningKey = RSA.Create(2048);
        BridgeKeyId = PinnedRsaTrustStore.ComputeKeyId(
            BridgeSigningKey.ExportSubjectPublicKeyInfo());

        DrainDirectiveSigningKey = RSA.Create(2048);
        DrainDirectiveKeyId = PinnedRsaTrustStore.ComputeKeyId(
            DrainDirectiveSigningKey.ExportSubjectPublicKeyInfo());
        var directiveTrustRoot = Path.Combine(root, "trust-drain-directive");
        Directory.CreateDirectory(directiveTrustRoot);
        File.WriteAllText(
            Path.Combine(directiveTrustRoot, DrainDirectiveKeyId + ".pem"),
            DrainDirectiveSigningKey.ExportSubjectPublicKeyInfoPem());
        DrainDirectiveTrustStore = PinnedRsaTrustStore.LoadFromDirectory(
            directiveTrustRoot,
            [DrainDirectiveKeyId]);
        DrainDirectiveSigningBroker = new TestDrainDirectiveSigningBroker(
            DrainDirectiveSigningKey,
            DrainDirectiveKeyId);

        WorkerDrainSigningKey = RSA.Create(2048);
        WorkerDrainKeyId = PinnedRsaTrustStore.ComputeKeyId(WorkerDrainSigningKey.ExportSubjectPublicKeyInfo());
        var workerTrustRoot = Path.Combine(root, "trust-worker-drain");
        Directory.CreateDirectory(workerTrustRoot);
        File.WriteAllText(
            Path.Combine(workerTrustRoot, WorkerDrainKeyId + ".pem"),
            WorkerDrainSigningKey.ExportSubjectPublicKeyInfoPem());
        WorkerDrainTrustStore = PinnedRsaTrustStore.LoadFromDirectory(workerTrustRoot, [WorkerDrainKeyId]);

        JournalSigningKey = RSA.Create(2048);
        JournalKeyId = PinnedRsaTrustStore.ComputeKeyId(JournalSigningKey.ExportSubjectPublicKeyInfo());
        var journalTrustRoot = Path.Combine(root, "trust-journal-drain");
        Directory.CreateDirectory(journalTrustRoot);
        File.WriteAllText(
            Path.Combine(journalTrustRoot, JournalKeyId + ".pem"),
            JournalSigningKey.ExportSubjectPublicKeyInfoPem());
        JournalDrainTrustStore = PinnedRsaTrustStore.LoadFromDirectory(journalTrustRoot, [JournalKeyId]);

        StateAnchor = new TestSupervisorStateAnchor();
        StateStore = new DurableSupervisorStateStore(
            Path.Combine(root, "state", "supervisor.json"),
            StateAnchor);
        Deployment = new SupervisorDeploymentBinding(
            "host_" + new string('f', 64),
            new string('1', 64),
            new string('2', 64),
            BridgeKeyId,
            new string('3', 64),
            TrustStore.StoreFingerprint,
            CapabilityTrustStore.StoreFingerprint,
            DrainDirectiveTrustStore.StoreFingerprint,
            DrainDirectiveKeyId,
            WorkerDrainTrustStore.StoreFingerprint,
            JournalDrainTrustStore.StoreFingerprint);
        JournalProvider = new TestJournalDrainAttestationProvider(
            JournalSigningKey,
            JournalKeyId);
    }

    public string Root { get; }
    public string KeyId { get; }
    public PinnedRsaTrustStore TrustStore { get; }
    public string CapabilityKeyId { get; }
    public string CapabilityTrustRoot { get; }
    public PinnedRsaTrustStore CapabilityTrustStore { get; }
    public string BridgeKeyId { get; }
    public string ZennoDroidStartedAt { get; } = "2026-07-15T00:00:00.0000000+00:00";
    public string DrainDirectiveKeyId { get; }
    public PinnedRsaTrustStore DrainDirectiveTrustStore { get; }
    public TestDrainDirectiveSigningBroker DrainDirectiveSigningBroker { get; }
    public string WorkerDrainKeyId { get; }
    public PinnedRsaTrustStore WorkerDrainTrustStore { get; }
    public string JournalKeyId { get; }
    public PinnedRsaTrustStore JournalDrainTrustStore { get; }
    public TestSupervisorStateAnchor StateAnchor { get; }
    public DurableSupervisorStateStore StateStore { get; }
    public SupervisorDeploymentBinding Deployment { get; }
    public TestJournalDrainAttestationProvider JournalProvider { get; }
    private RSA SigningKey { get; }
    private RSA CapabilitySigningKey { get; }
    private RSA BridgeSigningKey { get; }
    private RSA DrainDirectiveSigningKey { get; }
    private RSA WorkerDrainSigningKey { get; }
    private RSA JournalSigningKey { get; }

    public string ExportPrivateKeyPemForNegativeTest() => SigningKey.ExportPkcs8PrivateKeyPem();

    public AbWorkerSupervisor Supervisor(
        WorkerArtifact initialArtifact,
        IDrainDirectiveSigningBroker? drainDirectiveSigningBroker = null) =>
        SupervisorSimulationAccess.Bootstrap(
            Root,
            initialArtifact,
            RequiredCapabilities,
            TrustStore,
            DrainDirectiveTrustStore,
            WorkerDrainTrustStore,
            JournalDrainTrustStore,
            drainDirectiveSigningBroker ?? DrainDirectiveSigningBroker,
            JournalProvider,
            Deployment,
            StateStore);

    public AbWorkerSupervisor ResumeSupervisor() =>
        SupervisorSimulationAccess.Resume(
            Root,
            RequiredCapabilities,
            TrustStore,
            DrainDirectiveTrustStore,
            WorkerDrainTrustStore,
            JournalDrainTrustStore,
            DrainDirectiveSigningBroker,
            JournalProvider,
            Deployment,
            StateStore);

    public void ConstructWithOverlappingDirectiveAndWorkerTrust(WorkerArtifact initialArtifact)
    {
        var root = Path.Combine(Root, "overlapping-drain-trust");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, DrainDirectiveKeyId + ".pem"),
            DrainDirectiveSigningKey.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(
            Path.Combine(root, WorkerDrainKeyId + ".pem"),
            WorkerDrainSigningKey.ExportSubjectPublicKeyInfoPem());
        using var overlapping = PinnedRsaTrustStore.LoadFromDirectory(
            root,
            [DrainDirectiveKeyId, WorkerDrainKeyId]);
        _ = SupervisorSimulationAccess.Bootstrap(
            Root,
            initialArtifact,
            RequiredCapabilities,
            TrustStore,
            DrainDirectiveTrustStore,
            overlapping,
            JournalDrainTrustStore,
            DrainDirectiveSigningBroker,
            JournalProvider,
            Deployment with { WorkerDrainTrustFingerprint = overlapping.StoreFingerprint },
            StateStore);
    }

    public CapabilityVerificationExpectation CapabilityExpectation(WorkerArtifact candidate) => new(
        Deployment.HostId,
        Deployment.ReleaseBomSha256,
        Deployment.ProtectedPolicySha256,
        candidate.Sha256,
        candidate.Version,
        candidate.Slot.ToString(),
        42,
        ZennoDroidStartedAt,
        BridgeKeyId,
        100,
        new string('1', 64),
        new string('2', 64),
        86400,
        0,
        100,
        86400,
        300,
        30);

    public CapabilityEvidenceVerification VerifiedWaitingCapability(WorkerArtifact candidate)
    {
        var evidence = SignedCapabilityEvidence(candidate);
        return CapabilityEvidenceCodec.DecodeAndVerify(
            JsonSerializer.SerializeToUtf8Bytes(evidence),
            CapabilityTrustStore,
            CapabilityExpectation(candidate));
    }

    public CapabilityEvidenceV1 SignedCapabilityEvidence(WorkerArtifact candidate)
    {
        var issuedAt = DateTimeOffset.UtcNow;
        var issuedAtText = issuedAt.ToString("O");
        var evidence = new CapabilityEvidenceV1
        {
            SchemaVersion = "1.0",
            ContractId = "edge.capability.evidence/v1",
            ProducerModule = "windows-edge-supervisor",
            SoulId = "soul_" + new string('a', 64),
            DeviceBindingId = "db_" + new string('b', 32),
            PlatformAccountId = "pa_" + new string('c', 32),
            TraceId = "trace_" + new string('d', 32),
            IdempotencyKey = "idem_" + new string('e', 64),
            OccurredAt = issuedAtText,
            PrivacyClass = "internal",
            Status = "WAITING_EXTERNAL",
            RequestedLevel = "WINDOWS_VERIFIED",
            VerificationClaim = null,
            EvidenceKind = "REAL_WINDOWS_ATTESTED",
            RawEvidenceSha256 = null,
            AttestationKeyId = null,
            AttestationAlgorithm = null,
            AttestationSignature = null,
            HostId = Deployment.HostId,
            ReleaseBomSha256 = Deployment.ReleaseBomSha256,
            ProtectedPolicySha256 = Deployment.ProtectedPolicySha256,
            WorkerArtifactSha256 = candidate.Sha256,
            WorkerVersion = candidate.Version,
            WorkerSlot = candidate.Slot.ToString(),
            IssuedAt = issuedAtText,
            NotBefore = issuedAtText,
            ExpiresAt = issuedAt.AddMinutes(4).ToString("O"),
            IsWindows = true,
            PowerShellVersion = "7.6.2",
            ZennoDroidVersion = "fixture-1.0",
            ZennoDroidPidBefore = 42,
            ZennoDroidPidAfter = 42,
            ZennoDroidStartedAtBefore = ZennoDroidStartedAt,
            ZennoDroidStartedAtAfter = ZennoDroidStartedAt,
            DotNetFrameworkVersion = "4.8",
            CSharpVersion = "5",
            CodeDomSupported = true,
            GacSupported = true,
            DllLoadSupported = true,
            AdbVersion = "37.0.0-14910828",
            AuthorizedDeviceCount = 2,
            BridgeAbi = "edge.bridge.exchange/v1",
            LoopbackPort = 28741,
            TimeoutMs = 15000,
            ErrorSemantics = "fail-closed-unknown-outcome-no-implicit-retry/v1",
            PeerAuthMode = "WINDOWS_IDENTITY_AND_PINNED_RSA",
            PeerAuthKeyId = BridgeKeyId,
            PeerAuthAlgorithm = "RSA_PKCS1_SHA256",
            EvidenceLogEntryCount = 100,
            EvidenceLogHeadSha256 = new string('1', 64),
            EvidenceLogFileIdentitySha256 = new string('2', 64),
            ConnectionContinuitySeconds = 86400,
            ConnectionDrops = 0,
            AbSwitchCount = 100,
            SoakSeconds = 86400,
            Missing = ["worker-launch-runtime-abi-unavailable"]
        };
        var statement = CapabilityEvidenceCodec.CreateAttestationStatement(evidence);
        var signature = CapabilitySigningKey.SignData(
            statement,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        return evidence with
        {
            RawEvidenceSha256 = Convert.ToHexStringLower(SHA256.HashData(statement)),
            AttestationKeyId = CapabilityKeyId,
            AttestationAlgorithm = "RSA_PSS_SHA256",
            AttestationSignature = Convert.ToBase64String(signature)
        };
    }

    public DrainScope DrainScope() => new(
        "soul_" + new string('a', 64),
        "db_" + new string('b', 32),
        "pa_" + new string('c', 32),
        "trace_" + new string('d', 32),
        "idem_" + new string('e', 64),
        DateTimeOffset.UtcNow);

    public byte[] SignedDrainReceipt(
        DrainExpectation expectation,
        DateTimeOffset? receiptIssuedAt = null)
    {
        var issuedAt = receiptIssuedAt ?? DateTimeOffset.UtcNow;
        var claims = new WorkerDrainReceiptClaimsV1(
            WorkerDrainReceiptContractCodec.SchemaVersion,
            WorkerDrainReceiptContractCodec.ContractId,
            WorkerDrainReceiptContractCodec.ProducerModule,
            expectation.SoulId,
            expectation.DeviceBindingId,
            expectation.PlatformAccountId,
            expectation.TraceId,
            expectation.IdempotencyKey,
            expectation.OccurredAt,
            "internal",
            expectation.DrainId,
            expectation.Slot.ToString(),
            expectation.WorkerVersion,
            expectation.ArtifactSha256,
            Deployment.JournalArtifactSha256,
            Deployment.ReleaseBomSha256,
            Deployment.ProtectedPolicySha256,
            expectation.RoutingEpoch,
            true,
            true,
            0,
            issuedAt.ToString("O"),
            issuedAt.ToString("O"),
            issuedAt.AddMinutes(4).ToString("O"));
        var statement = WorkerDrainReceiptContractCodec.CreateSigningStatement(claims);
        var envelope = WorkerDrainReceiptContractCodec.AttachSignature(
            claims,
            WorkerDrainKeyId,
            Convert.ToBase64String(WorkerDrainSigningKey.SignData(
                statement,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)));
        var wire = WorkerDrainReceiptContractCodec.Serialize(envelope);
        JournalProvider.Register(expectation, envelope, wire);
        return wire;
    }

    public (string WorkerWireSha256, string JournalWireSha256) ReadLastDrainProofPair()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(StateStore.StatePath));
        var payload = document.RootElement.GetProperty("payload");
        return (
            payload.GetProperty("last_worker_drain_receipt_wire_sha256").GetString()!,
            payload.GetProperty("last_journal_drain_attestation_wire_sha256").GetString()!);
    }

    public void RemoveLastJournalProofDigestKeepingChecksumsValid()
    {
        RewriteStateKeepingChecksumsValid(payload =>
            payload["last_journal_drain_attestation_wire_sha256"] = null);
    }

    public void TamperPreparedDirectiveSignatureKeepingChecksumsValid()
    {
        RewriteStateKeepingChecksumsValid(payload =>
        {
            var prepared = payload["prepared_drain_directive"]?.AsObject() ??
                throw new InvalidOperationException("test prepared drain directive is missing");
            var wire = Convert.FromBase64String(
                prepared["wire_base64"]?.GetValue<string>() ??
                throw new InvalidOperationException("test prepared directive wire is missing"));
            var directive = JsonNode.Parse(wire)?.AsObject() ??
                throw new InvalidOperationException("test prepared directive JSON is missing");
            directive["signature"] = Convert.ToBase64String(new byte[256]);
            var tamperedWire = Encoding.UTF8.GetBytes(directive.ToJsonString());
            prepared["wire_base64"] = Convert.ToBase64String(tamperedWire);
            prepared["wire_sha256"] = Convert.ToHexStringLower(SHA256.HashData(tamperedWire));
        });
    }

    public void CreateValidThirdStateOutsideAnchorHeads()
    {
        RewriteStateKeepingChecksumsValid(
            payload => payload["routing_epoch"] = 999,
            updateAnchor: false);
        var committed = StateAnchor.ReadSnapshot().Committed ??
            throw new InvalidOperationException("test committed state anchor is missing");
        StateAnchor.InjectPreparedForNegativeTest(new SupervisorStateAnchor(
            committed.Generation + 1,
            new string('f', 64)));
    }

    private void RewriteStateKeepingChecksumsValid(
        Action<JsonObject> mutatePayload,
        bool updateAnchor = true)
    {
        var envelope = JsonNode.Parse(File.ReadAllBytes(StateStore.StatePath))?.AsObject() ??
            throw new InvalidOperationException("test Supervisor state is missing");
        var payload = envelope["payload"]?.AsObject() ??
            throw new InvalidOperationException("test Supervisor payload is missing");
        mutatePayload(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
        var generation = envelope["generation"]?.GetValue<long>() ??
            throw new InvalidOperationException("test Supervisor generation is missing");
        var previous = envelope["previous_state_sha256"]?.GetValue<string>() ??
            throw new InvalidOperationException("test Supervisor previous state digest is missing");
        var stateSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            "dps.windows-edge-supervisor-state-checksum/v1",
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            previous,
            payloadSha256))));
        envelope["payload_sha256"] = payloadSha256;
        envelope["state_sha256"] = stateSha256;
        File.WriteAllText(StateStore.StatePath, envelope.ToJsonString(), new UTF8Encoding(false));
        if (updateAnchor)
            StateAnchor.ReplaceForNegativeTest(new SupervisorStateAnchor(generation, stateSha256));
    }

    public static Fixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "dps-edge-supervisor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new Fixture(root);
    }

    public WorkerArtifact Artifact(WorkerSlot slot, int version)
    {
        var directory = Path.Combine(Root, $"worker-{version:D3}");
        Directory.CreateDirectory(directory);
        var binary = Path.Combine(directory, "worker.exe");
        File.WriteAllBytes(binary, BitConverter.GetBytes(version));
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))).ToLowerInvariant();
        var health = Path.Combine(directory, "health.evidence.json");
        var shadow = Path.Combine(directory, "shadow.evidence.json");
        File.WriteAllText(health, JsonSerializer.Serialize(new
        {
            Status = "PASS",
            ArtifactSha256 = digest,
            Capabilities = RequiredCapabilities,
            SideEffects = 0
        }));
        File.WriteAllText(shadow, JsonSerializer.Serialize(new
        {
            Status = "PASS",
            ArtifactSha256 = digest,
            Capabilities = RequiredCapabilities,
            SideEffects = 0
        }));
        var versionText = $"0.1.{version}";
        var healthDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(health))).ToLowerInvariant();
        var shadowDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shadow))).ToLowerInvariant();
        var manifest = Path.Combine(directory, "worker.runtime.manifest.json");
        var manifestBytes = WorkerRuntimeManifestCodec.Create(directory, [binary, health, shadow]);
        File.WriteAllBytes(manifest, manifestBytes);
        var manifestDigest = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        var directorySecurityDigest = WorkerRuntimeClosureProof.CaptureDirectorySecuritySha256(directory);
        var statement = WorkerArtifactSigning.CreateStatement(
            slot,
            versionText,
            digest,
            healthDigest,
            shadowDigest,
            manifestDigest,
            directorySecurityDigest);
        var signature = Convert.ToBase64String(SigningKey.SignData(statement, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return new WorkerArtifact(
            slot,
            versionText,
            directory,
            binary,
            digest,
            health,
            healthDigest,
            shadow,
            shadowDigest,
            manifest,
            manifestDigest,
            directorySecurityDigest,
            signature,
            KeyId);
    }

    public static string RepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null &&
               !(File.Exists(Path.Combine(current.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(current.FullName, "governance"))))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

internal sealed class TestDrainDirectiveSigningBroker : IDrainDirectiveSigningBroker
{
    private readonly RSA _signingKey;
    private PauseGate? _pause;
    private int _signCount;

    public TestDrainDirectiveSigningBroker(RSA signingKey, string keyId)
    {
        _signingKey = signingKey;
        KeyId = keyId;
    }

    public string KeyId { get; }
    public int SignCount => Volatile.Read(ref _signCount);

    public void PauseNextSignature()
    {
        if (Interlocked.CompareExchange(ref _pause, new PauseGate(), null) is not null)
            throw new InvalidOperationException("a test signature pause is already armed");
    }

    public Task WaitUntilSignatureRequestedAsync() =>
        (_pause ?? throw new InvalidOperationException("no test signature pause is armed"))
        .Started.Task;

    public void ReleasePausedSignature() =>
        (_pause ?? throw new InvalidOperationException("no test signature pause is armed"))
        .Release.TrySetResult(null);

    public async ValueTask<string> SignDrainDirectiveStatementAsync(
        ReadOnlyMemory<byte> canonicalStatement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.StartsWith(
            DrainDirectiveV1Codec.StatementDomain + "\n",
            Encoding.UTF8.GetString(canonicalStatement.Span),
            StringComparison.Ordinal);
        var pause = _pause;
        if (pause is not null)
        {
            pause.Started.TrySetResult(null);
            await pause.Release.Task.WaitAsync(cancellationToken);
            Interlocked.CompareExchange(ref _pause, null, pause);
        }
        Interlocked.Increment(ref _signCount);
        return Convert.ToBase64String(_signingKey.SignData(
            canonicalStatement.Span,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
    }

    private sealed class PauseGate
    {
        public TaskCompletionSource<object?> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class TestJournalDrainAttestationProvider : IJournalDrainAttestationProvider
{
    private readonly object _sync = new();
    private readonly RSA _journalSigningKey;
    private readonly string _journalKeyId;
    private readonly Dictionary<string, RegisteredDrain> _registered = new(StringComparer.Ordinal);

    public TestJournalDrainAttestationProvider(RSA journalSigningKey, string journalKeyId)
    {
        _journalSigningKey = journalSigningKey;
        _journalKeyId = journalKeyId;
    }

    public Func<JournalDrainAttestation, JournalDrainAttestation>? BeforeSignMutation { get; set; }
    public Func<JournalDrainAttestation, JournalDrainAttestation>? AfterSignMutation { get; set; }
    public bool RejectRequests { get; set; }
    public bool HangRequests { get; set; }
    public CancellationToken LastRequestCancellationToken { get; private set; }
    public string? LastIssuedWireSha256 { get; private set; }

    public void Register(
        DrainExpectation expectation,
        SignedWorkerDrainReceiptV1 receipt,
        byte[] exactWire)
    {
        var wireSha256 = Convert.ToHexStringLower(SHA256.HashData(exactWire));
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(
            WorkerDrainReceiptContractCodec.CreateJournalPayload(receipt, wireSha256)));
        var entryId = "worker-drain-" + expectation.DrainId["drain-".Length..];
        lock (_sync)
        {
            _registered[entryId] = new RegisteredDrain(
                expectation,
                receipt,
                wireSha256,
                payloadSha256);
        }
    }

    public Task<JournalDrainAttestation> IssueDrainAttestationAsync(
        JournalDrainAttestationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestCancellationToken = cancellationToken;
        if (HangRequests)
            return new TaskCompletionSource<JournalDrainAttestation>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        RegisteredDrain registered;
        lock (_sync)
        {
            if (RejectRequests || !_registered.TryGetValue(request.EntryId, out registered!))
                throw new JournalAttestationUnavailableException("test Journal proof is unavailable");
        }
        var receipt = registered.Receipt;
        var expectation = registered.Expectation;
        if (request.CommandId != expectation.DrainId ||
            request.WorkerArtifactSha256 != receipt.WorkerArtifactSha256 ||
            request.WorkerVersion != receipt.WorkerVersion ||
            request.WorkerSlot != receipt.Slot ||
            request.JournalArtifactSha256 != receipt.JournalArtifactSha256 ||
            request.ReleaseBomSha256 != receipt.ReleaseBomSha256 ||
            request.ProtectedPolicySha256 != receipt.ProtectedPolicySha256 ||
            request.RoutingEpoch != receipt.RoutingEpoch ||
            request.IntakeStopped != receipt.IntakeStopped ||
            request.WorkerDrained != receipt.WorkerDrained ||
            request.RemainingInFlight != receipt.RemainingInFlight ||
            request.WorkerReceiptWireSha256 != registered.WorkerWireSha256 ||
            request.ValidFor <= TimeSpan.Zero || request.ValidFor > TimeSpan.FromMinutes(5))
            throw new JournalConflictException("test Journal request does not locate the exact durable Worker entry");

        var ownerReceipt = new JournalDrainOwnerReceipt(
            "1.0",
            "edge.journal.receipt/v1",
            "edge-local-journal",
            "windows-edge-worker",
            expectation.SoulId,
            expectation.DeviceBindingId,
            expectation.PlatformAccountId,
            expectation.TraceId,
            expectation.IdempotencyKey,
            expectation.OccurredAt,
            "internal",
            expectation.DrainId,
            request.EntryId,
            "WORKER_DRAINED",
            registered.PayloadSha256,
            42,
            new string('a', 64),
            new string('b', 64),
            true,
            false);
        var ownerReceiptSha256 = Convert.ToHexStringLower(SHA256.HashData(
            JournalDrainAttestationCodec.EncodeOwnerReceipt(ownerReceipt)));
        var issuedAt = DateTimeOffset.UtcNow;
        var unsigned = new JournalDrainAttestation(
            SchemaVersion: JournalDrainAttestationCodec.SchemaVersion,
            ContractId: JournalDrainAttestationCodec.ContractId,
            ProducerModule: JournalDrainAttestationCodec.ProducerModule,
            RequestProducerModule: "windows-edge-supervisor",
            SoulId: expectation.SoulId,
            DeviceBindingId: expectation.DeviceBindingId,
            PlatformAccountId: expectation.PlatformAccountId,
            TraceId: expectation.TraceId,
            IdempotencyKey: expectation.IdempotencyKey,
            OccurredAt: expectation.OccurredAt,
            PrivacyClass: "internal",
            RequestId: request.RequestId,
            DrainId: expectation.DrainId,
            CommandId: expectation.DrainId,
            EntryId: request.EntryId,
            EntryType: "WORKER_DRAINED",
            EntrySequence: 42,
            EntryChecksum: new string('b', 64),
            EntryPayloadSha256: registered.PayloadSha256,
            JournalId: "journal_" + new string('c', 64),
            JournalFileSha256: new string('d', 64),
            JournalFileIdentitySha256: new string('e', 64),
            JournalHeadSequence: 42,
            JournalHeadChecksum: new string('b', 64),
            ChecksumEncoding: JournalChecksumEncoding.Name,
            RangeStartSequence: 42,
            RangeEndSequence: 42,
            RangeEntryCount: 1,
            EntrySetSha256: new string('f', 64),
            QuarantineState: "CLEAR",
            RecoveryState: "CLEAN",
            StateArtifactSetSha256: new string('0', 64),
            WorkerArtifactSha256: receipt.WorkerArtifactSha256,
            WorkerVersion: receipt.WorkerVersion,
            WorkerSlot: receipt.Slot,
            JournalArtifactSha256: receipt.JournalArtifactSha256,
            ReleaseBomSha256: receipt.ReleaseBomSha256,
            ProtectedPolicySha256: receipt.ProtectedPolicySha256,
            RoutingEpoch: receipt.RoutingEpoch,
            IntakeStopped: receipt.IntakeStopped,
            WorkerDrained: receipt.WorkerDrained,
            RemainingInFlight: receipt.RemainingInFlight,
            WorkerReceiptWireSha256: registered.WorkerWireSha256,
            JournalReceiptSha256: ownerReceiptSha256,
            JournalReceipt: ownerReceipt,
            IssuedAt: issuedAt.ToString("O"),
            ExpiresAt: issuedAt.Add(request.ValidFor).ToString("O"),
            Canonicalization: JournalDrainAttestationCodec.Canonicalization,
            SignatureKeyId: _journalKeyId,
            SignatureAlgorithm: JournalDrainAttestationCodec.SignatureAlgorithm,
            StatementSha256: new string('0', 64),
            Signature: Convert.ToBase64String(new byte[256]));
        var mutated = BeforeSignMutation?.Invoke(unsigned) ?? unsigned;
        var statement = JournalDrainAttestationCodec.EncodeStatement(mutated);
        var completed = mutated with
        {
            StatementSha256 = Convert.ToHexStringLower(SHA256.HashData(statement)),
            Signature = Convert.ToBase64String(_journalSigningKey.SignData(
                statement,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss))
        };
        completed = AfterSignMutation?.Invoke(completed) ?? completed;
        var exactWire = Encoding.UTF8.GetBytes(JournalDrainAttestationCodec.Serialize(completed));
        LastIssuedWireSha256 = Convert.ToHexStringLower(SHA256.HashData(exactWire));
        return Task.FromResult(completed);
    }

    private sealed record RegisteredDrain(
        DrainExpectation Expectation,
        SignedWorkerDrainReceiptV1 Receipt,
        string WorkerWireSha256,
        string PayloadSha256);
}

internal sealed class TestSupervisorStateAnchor : ISupervisorStateAnchor
{
    private readonly object _sync = new();
    private SupervisorStateAnchor? _committed;
    private SupervisorStatePreparation? _prepared;
    private ManualResetEventSlim? _prepareRelease;
    private TaskCompletionSource<bool>? _preparePaused;

    public bool ThrowAfterPrepareOnce { get; set; }
    public bool ThrowBeforeCommitOnce { get; set; }
    public bool ThrowAfterCommitOnce { get; set; }
    public bool ThrowAfterAbortOnce { get; set; }
    public bool RejectCommitOnce { get; set; }
    public bool RejectAbortOnce { get; set; }
    public bool ReturnMismatchedPreparationOnce { get; set; }

    public void PauseNextPrepare()
    {
        lock (_sync)
        {
            if (_prepareRelease is not null)
                throw new InvalidOperationException("test anchor prepare pause is already armed");
            _prepareRelease = new ManualResetEventSlim(initialState: false);
            _preparePaused = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task WaitUntilPreparePausedAsync()
    {
        lock (_sync)
            return (_preparePaused ?? throw new InvalidOperationException(
                "test anchor prepare pause is not armed")).Task;
    }

    public void ReleasePausedPrepare()
    {
        ManualResetEventSlim gate;
        lock (_sync)
            gate = _prepareRelease ?? throw new InvalidOperationException(
                "test anchor prepare pause is not armed");
        gate.Set();
    }

    public SupervisorStateAnchorSnapshot ReadSnapshot()
    {
        lock (_sync) return new SupervisorStateAnchorSnapshot(_committed, _prepared);
    }

    public SupervisorStatePreparation? TryPrepare(
        SupervisorStateAnchor? expectedCommitted,
        SupervisorStateAnchor next)
    {
        ManualResetEventSlim? gate;
        SupervisorStatePreparation preparation;
        lock (_sync)
        {
            var expectedGeneration = _committed is null ? 1 : _committed.Generation + 1;
            if (_prepared is not null || _committed != expectedCommitted ||
                next.Generation != expectedGeneration)
                return null;
            var preparedNext = next;
            if (ReturnMismatchedPreparationOnce)
            {
                ReturnMismatchedPreparationOnce = false;
                preparedNext = next with { StateSha256 = new string('e', 64) };
            }
            preparation = new SupervisorStatePreparation(
                "prep_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                preparedNext);
            _prepared = preparation;
            if (ThrowAfterPrepareOnce)
            {
                ThrowAfterPrepareOnce = false;
                throw new InvalidOperationException("injected crash after external anchor prepare");
            }
            gate = _prepareRelease;
            _preparePaused?.TrySetResult(true);
        }
        gate?.Wait();
        if (gate is not null)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_prepareRelease, gate))
                {
                    _prepareRelease = null;
                    _preparePaused = null;
                }
            }
            gate.Dispose();
        }
        return preparation;
    }

    public bool TryCommit(SupervisorStatePreparation prepared)
    {
        lock (_sync)
        {
            if (_prepared != prepared)
                return false;
            if (RejectCommitOnce)
            {
                RejectCommitOnce = false;
                return false;
            }
            if (ThrowBeforeCommitOnce)
            {
                ThrowBeforeCommitOnce = false;
                throw new InvalidOperationException("injected crash before external anchor commit");
            }
            _committed = prepared.Next;
            _prepared = null;
            if (ThrowAfterCommitOnce)
            {
                ThrowAfterCommitOnce = false;
                throw new InvalidOperationException("injected crash after external anchor commit");
            }
            return true;
        }
    }

    public bool TryAbort(SupervisorStatePreparation prepared)
    {
        lock (_sync)
        {
            if (_prepared != prepared)
                return false;
            if (RejectAbortOnce)
            {
                RejectAbortOnce = false;
                return false;
            }
            _prepared = null;
            if (ThrowAfterAbortOnce)
            {
                ThrowAfterAbortOnce = false;
                throw new InvalidOperationException("injected crash after external anchor abort");
            }
            return true;
        }
    }

    public void ReplaceForNegativeTest(SupervisorStateAnchor replacement)
    {
        lock (_sync) (_committed, _prepared) = (replacement, null);
    }

    public void InjectPreparedForNegativeTest(SupervisorStateAnchor next)
    {
        lock (_sync)
        {
            if (_prepared is not null)
                throw new InvalidOperationException("test state anchor already has a prepared head");
            _prepared = new SupervisorStatePreparation(
                "prep_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                next);
        }
    }
}

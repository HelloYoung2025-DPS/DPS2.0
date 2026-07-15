using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.EdgeLocalJournal;
using Xunit;

namespace Dps.EdgeLocalJournal.Tests;

public sealed class DrainAttestationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Owner_recomputes_durable_range_and_emits_one_verifiable_rich_Journal_signature()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await DrainFixture.CreateAsync();
        var attestation = await fixture.Store.IssueDrainAttestationAsync(fixture.Request, token);

        JournalDrainAttestationCodec.Verify(attestation, fixture.JournalKey, fixture.VerificationTime);
        Assert.Equal(fixture.Request.WorkerReceiptWireSha256, attestation.WorkerReceiptWireSha256);
        Assert.Equal(attestation.EntryChecksum, attestation.JournalReceipt.EntryChecksum);
        Assert.Equal(attestation.EntryPayloadSha256, attestation.JournalReceipt.PayloadSha256);
        Assert.Equal(attestation.EntrySequence, attestation.RangeEndSequence);
        Assert.Equal(attestation.JournalHeadSequence, attestation.EntrySequence);
        Assert.Equal(fixture.Request.JournalArtifactSha256, attestation.JournalArtifactSha256);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                JournalDrainAttestationCodec.EncodeOwnerReceipt(attestation.JournalReceipt))).ToLowerInvariant(),
            attestation.JournalReceiptSha256);
        Assert.Equal("windows-edge-supervisor", attestation.RequestProducerModule);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Caller_cannot_state_Journal_head_range_or_owner_receipt_and_mismatched_worker_hash_fails()
    {
        var token = TestContext.Current.CancellationToken;
        var requestProperties = typeof(JournalDrainAttestationRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("JournalHeadSequence", requestProperties);
        Assert.DoesNotContain("JournalHeadChecksum", requestProperties);
        Assert.DoesNotContain("RangeStartSequence", requestProperties);
        Assert.DoesNotContain("JournalReceipt", requestProperties);
        Assert.DoesNotContain("WorkerKeyId", requestProperties);
        Assert.DoesNotContain("WorkerSignature", requestProperties);
        Assert.DoesNotContain("WorkerIssuedAt", requestProperties);

        await using var fixture = await DrainFixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.IssueDrainAttestationAsync(
            fixture.Request with { WorkerReceiptWireSha256 = new string('0', 64) }, token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Missing_authority_and_invalid_Journal_validity_fail_closed()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await DrainFixture.CreateAsync();
        await using var noAuthority = await JournalStore.OpenAsync(fixture.Path, token);
        await Assert.ThrowsAsync<JournalAttestationUnavailableException>(
            () => noAuthority.IssueDrainAttestationAsync(fixture.Request, token));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.IssueDrainAttestationAsync(
            fixture.Request with { ValidFor = TimeSpan.Zero }, token));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task Drain_contract_declares_single_owner_signature_independent_worker_wire_and_narrow_ports()
    {
        var root = Path.Combine(TestDirectory.RepositoryRoot(), "Modules/edge-local-journal/contracts/provided");
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "edge.journal.drain.attestation.v1.schema.json")));
        using var auth = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "edge.journal.drain.attestation.v1.auth.json")));
        using var corpus = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "edge.journal.drain.attestation.v1.corpus.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.GetFullPath(Path.Combine(root, "..", "..", "module.yaml"))));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            "edge.journal.drain.attestation/v1",
            schema.RootElement.GetProperty("properties").GetProperty("contract_id").GetProperty("const").GetString());
        Assert.Equal(
            "windows-edge-supervisor",
            schema.RootElement.GetProperty("properties").GetProperty("request_producer_module").GetProperty("const").GetString());
        Assert.False(auth.RootElement.TryGetProperty("supervisor_compatibility", out _));
        var correlation = auth.RootElement.GetProperty("independent_worker_wire_correlation");
        Assert.Equal("worker_receipt_wire_sha256", correlation.GetProperty("opaque_field").GetString());
        Assert.Equal("windows-edge-supervisor", correlation.GetProperty("worker_contract_owner").GetString());
        var pss = auth.RootElement.GetProperty("rsa_pss_parameters");
        Assert.Equal("SHA-256", pss.GetProperty("message_hash").GetString());
        Assert.Equal("MGF1", pss.GetProperty("mask_generation_function").GetString());
        Assert.Equal("SHA-256", pss.GetProperty("mgf1_hash").GetString());
        Assert.Equal(32, pss.GetProperty("salt_length_bytes").GetInt32());
        Assert.Equal(1, pss.GetProperty("trailer_field").GetInt32());
        Assert.Equal(25, corpus.RootElement.GetProperty("cases").GetArrayLength());
        var inbound = manifest.RootElement.GetProperty("communication").GetProperty("inbound")
            .EnumerateArray().ToArray();
        var outbound = manifest.RootElement.GetProperty("communication").GetProperty("outbound")
            .EnumerateArray().ToArray();
        Assert.Contains(inbound, edge =>
            edge.GetProperty("peerModule").GetString() == "windows-edge-worker" &&
            edge.GetProperty("contractId").GetString() == "edge.journal.append" &&
            edge.GetProperty("transport").GetString() == "command");
        Assert.Contains(outbound, edge =>
            edge.GetProperty("peerModule").GetString() == "windows-edge-worker" &&
            edge.GetProperty("contractId").GetString() == "edge.journal.receipt" &&
            edge.GetProperty("transport").GetString() == "receipt");
        Assert.DoesNotContain(outbound, edge =>
            edge.GetProperty("peerModule").GetString() == "windows-edge-worker" &&
            edge.GetProperty("contractId").GetString() == "edge.journal.drain.attestation");
        Assert.Equal("Dps.EdgeLocalJournal.Contracts", typeof(JournalDrainAttestation).Assembly.GetName().Name);
        Assert.Equal("Dps.EdgeLocalJournal.Contracts", typeof(JournalAppendRequest).Assembly.GetName().Name);
        Assert.Equal("Dps.EdgeLocalJournal.Contracts", typeof(JournalDrainAttestationCodec).Assembly.GetName().Name);
        Assert.Equal("Dps.EdgeLocalJournal.Contracts", typeof(CanonicalJson).Assembly.GetName().Name);
        Assert.Equal("Dps.EdgeLocalJournal.Contracts", typeof(JournalChecksumEncoding).Assembly.GetName().Name);
        Assert.Equal("Dps.EdgeLocalJournal.Contracts", typeof(IJournalAppendClient).Assembly.GetName().Name);
        Assert.Equal(new[] { "AppendAsync" }, typeof(IJournalAppendClient).GetMethods().Select(method => method.Name));
        Assert.Equal(
            new[] { "Count", "IsQuarantined" },
            typeof(IJournalReadiness).GetProperties().Select(property => property.Name));
        Assert.Equal(new[] { "IssueDrainAttestationAsync" },
            typeof(IJournalDrainAttestationProvider).GetMethods().Select(method => method.Name));
        Assert.Equal(
            new[] { "GetQuarantineStatusAsync", "RecoverFromQuarantineAsync" },
            typeof(IJournalQuarantineAdministration).GetMethods().Select(method => method.Name));
        Assert.Null(typeof(JournalDrainAttestation).Assembly.GetType("Dps.EdgeLocalJournal.IEdgeLocalJournal"));
        Assert.Null(typeof(JournalDrainAttestation).Assembly.GetType("Dps.EdgeLocalJournal.JournalStore"));
        Assert.Null(typeof(JournalDrainAttestation).Assembly.GetType("Dps.EdgeLocalJournal.JournalDrainAttestationAuthority"));
        Assert.NotEqual(typeof(JournalStore).Assembly, typeof(IJournalAppendClient).Assembly);
        Assert.True(typeof(IJournalAppendClient).IsAssignableFrom(typeof(JournalStore)));
        Assert.True(typeof(IJournalReadiness).IsAssignableFrom(typeof(JournalStore)));
        Assert.True(typeof(IJournalDrainAttestationProvider).IsAssignableFrom(typeof(JournalStore)));
        Assert.True(typeof(IJournalQuarantineAdministration).IsAssignableFrom(typeof(JournalStore)));

        await using var fixture = await DrainFixture.CreateAsync();
        var attestation = await fixture.Store.IssueDrainAttestationAsync(
            fixture.Request,
            TestContext.Current.CancellationToken);
        using var wire = JsonDocument.Parse(JournalDrainAttestationCodec.Serialize(attestation));
        using var independentlyFramed = new MemoryStream();
        independentlyFramed.Write(Encoding.UTF8.GetBytes(
            auth.RootElement.GetProperty("rich_owner_statement").GetProperty("domain").GetString()!));
        independentlyFramed.WriteByte((byte)'\n');
        foreach (var field in auth.RootElement.GetProperty("rich_owner_statement").GetProperty("fields").EnumerateArray())
        {
            var value = wire.RootElement.GetProperty(field.GetString()!);
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw new InvalidDataException("Rich owner statement profile contains a non-scalar field.")
            };
            var bytes = Encoding.UTF8.GetBytes(text);
            independentlyFramed.Write(Encoding.ASCII.GetBytes(
                bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            independentlyFramed.Write(bytes);
            independentlyFramed.WriteByte((byte)';');
        }
        Assert.Equal(independentlyFramed.ToArray(), JournalDrainAttestationCodec.EncodeStatement(attestation));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task Drain_codec_rejects_unknown_duplicate_and_cryptographic_binding_changes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await DrainFixture.CreateAsync();
        var attestation = await fixture.Store.IssueDrainAttestationAsync(fixture.Request, token);
        var serialized = JournalDrainAttestationCodec.Serialize(attestation);
        var decoded = JournalDrainAttestationCodec.Deserialize(serialized);
        Assert.Equal(attestation.StatementSha256, decoded.StatementSha256);

        var unknown = serialized[..^1] + ",\"unknown\":true}";
        Assert.Throws<JsonException>(() => JournalDrainAttestationCodec.Deserialize(unknown));
        var duplicate = serialized[..^1] + ",\"contract_id\":\"edge.journal.drain.attestation/v1\"}";
        Assert.Throws<JsonException>(() => JournalDrainAttestationCodec.Deserialize(duplicate));
        Assert.Throws<InvalidDataException>(() => JournalDrainAttestationCodec.Serialize(
            attestation with { JournalFileIdentitySha256 = new string('0', 64) }));
        Assert.Throws<InvalidDataException>(() => JournalDrainAttestationCodec.Serialize(
            attestation with { WorkerReceiptWireSha256 = new string('0', 64) }));
        Assert.Throws<InvalidDataException>(() => JournalDrainAttestationCodec.Serialize(
            attestation with { JournalReceiptSha256 = new string('0', 64) }));
        Assert.DoesNotContain("worker_key_id", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("journal_signature", serialized, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LOCAL_PROCESS")]
    public async Task Restart_recomputes_identical_durable_truth_and_rich_proof_verifies()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await DrainFixture.CreateAsync();
        var first = await fixture.Store.IssueDrainAttestationAsync(fixture.Request, token);
        await fixture.Store.DisposeAsync();
        fixture.Store = await JournalStore.OpenWithAttestationAuthorityAsync(
            fixture.Path,
            fixture.Authority,
            token);
        var second = await fixture.Store.IssueDrainAttestationAsync(
            fixture.Request with { RequestId = "drainreq_" + new string('7', 64) }, token);

        JournalDrainAttestationCodec.Verify(first, fixture.JournalKey, fixture.VerificationTime);
        JournalDrainAttestationCodec.Verify(second, fixture.JournalKey, fixture.VerificationTime);
        Assert.Equal(first.JournalFileSha256, second.JournalFileSha256);
        Assert.Equal(first.JournalFileIdentitySha256, second.JournalFileIdentitySha256);
        Assert.Equal(first.JournalHeadChecksum, second.JournalHeadChecksum);
        Assert.Equal(first.EntrySetSha256, second.EntrySetSha256);
        Assert.Equal(first.JournalReceipt, second.JournalReceipt);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LOCAL_PROCESS")]
    public async Task Second_process_append_announced_during_signing_fails_closed()
    {
        var token = TestContext.Current.CancellationToken;
        using var innerKey = RSA.Create(2048);
        using var blockingKey = new BlockingRsa(innerKey);
        await using var fixture = await DrainFixture.CreateAsync(blockingKey);
        using var process = StartProbe("append", fixture.Path);
        Assert.Equal("READY", await process.StandardOutput.ReadLineAsync(token));

        blockingKey.Arm();
        var issue = fixture.Store.IssueDrainAttestationAsync(fixture.Request, token);
        Assert.True(blockingKey.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
        await process.StandardInput.WriteLineAsync("GO".AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        await WaitForAsync(
            () => Directory.EnumerateFiles(fixture.Directory, "journal.jsonl.append-intent.*.json").Any(),
            TimeSpan.FromSeconds(10),
            token);
        blockingKey.Release();

        await Assert.ThrowsAsync<JournalAttestationStateChangedException>(() => issue);
        Assert.True(await WaitForExitAsync(process, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LOCAL_PROCESS")]
    public async Task Second_process_identical_replacement_and_symlink_rebinding_fail_closed()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (var mode in new[] { "replace", "symlink" })
        {
            using var innerKey = RSA.Create(2048);
            using var blockingKey = new BlockingRsa(innerKey);
            await using var fixture = await DrainFixture.CreateAsync(blockingKey);
            using var process = StartProbe(mode, fixture.Path);
            Assert.Equal("READY", await process.StandardOutput.ReadLineAsync(token));
            blockingKey.Arm();
            var issue = fixture.Store.IssueDrainAttestationAsync(fixture.Request, token);
            Assert.True(blockingKey.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync("GO".AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            var outcome = await process.StandardOutput.ReadLineAsync(token);
            Assert.Contains(outcome, new[] { "REPLACED", "SYMLINKED", "REBIND_BLOCKED", "REBIND_PARTIAL" });
            blockingKey.Release();

            if (outcome == "REBIND_BLOCKED")
            {
                var attestation = await issue;
                JournalDrainAttestationCodec.Verify(attestation, fixture.JournalKey, fixture.VerificationTime);
            }
            else
            {
                var exception = await Record.ExceptionAsync(() => issue);
                Assert.True(
                    exception is JournalAttestationStateChangedException or JournalCorruptionException,
                    "replacement or link rebinding must fail closed, actual: " + exception?.GetType().FullName);
            }
            Assert.True(await WaitForExitAsync(process, TimeSpan.FromSeconds(10)));
            Assert.Equal(0, process.ExitCode);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LOCAL_PROCESS")]
    public async Task Killed_real_append_process_leaves_intent_and_blocks_current_and_restarted_attestation()
    {
        var token = TestContext.Current.CancellationToken;
        using var innerKey = RSA.Create(2048);
        using var blockingKey = new BlockingRsa(innerKey);
        await using var fixture = await DrainFixture.CreateAsync(blockingKey);
        using var process = StartProbe("append", fixture.Path);
        Assert.Equal("READY", await process.StandardOutput.ReadLineAsync(token));

        blockingKey.Arm();
        var issue = fixture.Store.IssueDrainAttestationAsync(fixture.Request, token);
        try
        {
            Assert.True(blockingKey.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync("GO".AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            await WaitForAsync(
                () => Directory.EnumerateFiles(fixture.Directory, "journal.jsonl.append-intent.*.json").Any(),
                TimeSpan.FromSeconds(10),
                token);
            process.Kill(entireProcessTree: true);
            Assert.True(await WaitForExitAsync(process, TimeSpan.FromSeconds(10)));
            Assert.NotEqual(0, process.ExitCode);
        }
        finally
        {
            blockingKey.Release();
        }

        await Assert.ThrowsAsync<JournalAttestationStateChangedException>(() => issue);
        await fixture.Store.DisposeAsync();
        fixture.Store = await JournalStore.OpenWithAttestationAuthorityAsync(
            fixture.Path,
            fixture.Authority,
            token);
        await Assert.ThrowsAsync<JournalAttestationStateChangedException>(
            () => fixture.Store.IssueDrainAttestationAsync(
                fixture.Request with { RequestId = "drainreq_" + new string('8', 64) }, token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LOCAL_PROCESS")]
    public async Task Second_process_gate_rebind_cannot_admit_real_append_across_writer_release()
    {
        var token = TestContext.Current.CancellationToken;
        using var innerKey = RSA.Create(2048);
        using var blockingKey = new BlockingVerifyRsa(innerKey);
        await using var fixture = await DrainFixture.CreateAsync(blockingKey);
        using var process = StartProbe("rebind-gate-append", fixture.Path);
        Assert.Equal("READY", await process.StandardOutput.ReadLineAsync(token));

        blockingKey.Arm();
        var issue = fixture.Store.IssueDrainAttestationAsync(fixture.Request, token);
        Assert.True(blockingKey.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
        await process.StandardInput.WriteLineAsync("GO".AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        var outcome = await process.StandardOutput.ReadLineAsync(token);
        Assert.Contains(outcome, new[] { "GATE_REBOUND", "GATE_REBIND_BLOCKED", "GATE_REBIND_PARTIAL" });
        if (outcome == "GATE_REBOUND")
        {
            await WaitForAsync(
                () => Directory.EnumerateFiles(fixture.Directory, "journal.jsonl.append-intent.*.json").Any(),
                TimeSpan.FromSeconds(10),
                token);
        }
        blockingKey.Release();

        if (outcome == "GATE_REBIND_BLOCKED")
        {
            var attestation = await issue;
            JournalDrainAttestationCodec.Verify(attestation, fixture.JournalKey, fixture.VerificationTime);
        }
        else
        {
            await Assert.ThrowsAsync<JournalAttestationStateChangedException>(() => issue);
        }
        Assert.True(await WaitForExitAsync(process, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, process.ExitCode);
    }

    private static Process StartProbe(string mode, string path)
    {
        var root = TestDirectory.RepositoryRoot();
        var executable = Path.Combine(
            root,
            "Modules/edge-local-journal/tests/process-probe/bin/Release/net10.0/",
            OperatingSystem.IsWindows()
                ? "Dps.EdgeLocalJournal.ProcessProbe.exe"
                : "Dps.EdgeLocalJournal.ProcessProbe");
        Assert.True(File.Exists(executable), "process probe was not built: " + executable);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(path);
        return Process.Start(start) ?? throw new InvalidOperationException("failed to start process probe");
    }

    private static async Task WaitForAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("condition was not reached");
            }
            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var source = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(source.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

internal sealed class DrainFixture : IAsyncDisposable
{
    private DrainFixture(
        string directory,
        string path,
        RSA journalKey,
        JournalDrainAttestationAuthority authority,
        JournalStore store,
        JournalDrainAttestationRequest request)
    {
        Directory = directory;
        Path = path;
        JournalKey = journalKey;
        Authority = authority;
        Store = store;
        Request = request;
    }

    public string Directory { get; }
    public string Path { get; }
    public RSA JournalKey { get; }
    public JournalDrainAttestationAuthority Authority { get; }
    public JournalStore Store { get; set; }
    public JournalDrainAttestationRequest Request { get; }
    public DateTimeOffset VerificationTime { get; } = DateTimeOffset.Parse("2026-07-15T00:00:02.0000000+00:00");

    public static async Task<DrainFixture> CreateAsync(
        RSA? journalKey = null,
        DateTimeOffset? authorityNow = null)
    {
        var directory = TestDirectory.Create();
        var path = System.IO.Path.Combine(directory, "journal.jsonl");
        journalKey ??= RSA.Create(2048);
        var time = new FixedTimeProvider(
            authorityNow ?? DateTimeOffset.Parse("2026-07-15T00:00:01.0000000+00:00"));
        var authority = new JournalDrainAttestationAuthority(journalKey, time, leaveOpen: true);
        var store = await JournalStore.OpenWithAttestationAuthorityAsync(path, authority);

        var drainId = "drain-" + new string('1', 64);
        var soulId = "soul_" + new string('a', 64);
        var deviceId = "db_" + new string('b', 32);
        var accountId = "pa_" + new string('c', 32);
        var traceId = "trace_" + new string('d', 32);
        var idempotencyKey = "idem_" + new string('e', 64);
        var occurredAt = "2026-07-15T00:00:00.0000000+00:00";
        var workerArtifact = new string('2', 64);
        var journalArtifact = new string('3', 64);
        var releaseBom = new string('4', 64);
        var policy = new string('5', 64);
        var workerReceiptWire = Encoding.UTF8.GetBytes(
            "{\"contract_id\":\"edge.worker.drain.receipt/v1\",\"fixture\":\"opaque-exact-wire\"}");
        var workerReceiptWireSha256 = Convert.ToHexString(
            SHA256.HashData(workerReceiptWire)).ToLowerInvariant();
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = "1.0",
            drain_id = drainId,
            slot = "B",
            worker_version = "1.2.3",
            worker_artifact_sha256 = workerArtifact,
            journal_artifact_sha256 = journalArtifact,
            release_bom_sha256 = releaseBom,
            protected_policy_sha256 = policy,
            routing_epoch = 7L,
            intake_stopped = true,
            worker_drained = true,
            remaining_in_flight = 0,
            worker_receipt_wire_sha256 = workerReceiptWireSha256
        });
        var canonicalPayload = CanonicalJson.Canonicalize(payload);
        var payloadSha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
        await store.AppendAsync(new JournalAppendRequest(
            "1.0",
            "edge.journal.append/v1",
            "windows-edge-worker",
            drainId,
            "worker-drain-" + new string('1', 64),
            "WORKER_DRAINED",
            traceId,
            idempotencyKey,
            "internal",
            soulId,
            deviceId,
            accountId,
            payload,
            payloadSha256,
            DateTimeOffset.Parse(occurredAt)));

        var request = new JournalDrainAttestationRequest(
            "drainreq_" + new string('6', 64),
            drainId,
            "worker-drain-" + new string('1', 64),
            workerArtifact,
            "1.2.3",
            "B",
            journalArtifact,
            releaseBom,
            policy,
            7,
            true,
            true,
            0,
            workerReceiptWireSha256,
            TimeSpan.FromMinutes(4));

        return new DrainFixture(directory, path, journalKey, authority, store, request);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Authority.Dispose();
        JournalKey.Dispose();
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}

internal sealed class BlockingRsa(RSA inner) : RSA
{
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private int _armed;
    private int _blocked;

    public override int KeySize
    {
        get => inner.KeySize;
        set => throw new NotSupportedException();
    }

    public override KeySizes[] LegalKeySizes => inner.LegalKeySizes;

    public void Arm() => Volatile.Write(ref _armed, 1);
    public bool WaitUntilBlocked(TimeSpan timeout) => _entered.Wait(timeout);
    public void Release() => _release.Set();

    public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => inner.Decrypt(data, padding);
    public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => inner.Encrypt(data, padding);
    public override RSAParameters ExportParameters(bool includePrivateParameters) => inner.ExportParameters(includePrivateParameters);
    public override void ImportParameters(RSAParameters parameters) => inner.ImportParameters(parameters);
    public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        inner.VerifyHash(hash, signature, hashAlgorithm, padding);

    public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (Volatile.Read(ref _armed) != 0 && Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("blocking RSA test release timed out");
            }
        }
        return inner.SignHash(hash, hashAlgorithm, padding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _entered.Dispose();
            _release.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class BlockingVerifyRsa(RSA inner) : RSA
{
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private int _armed;
    private int _blocked;

    public override int KeySize
    {
        get => inner.KeySize;
        set => throw new NotSupportedException();
    }

    public override KeySizes[] LegalKeySizes => inner.LegalKeySizes;

    public void Arm() => Volatile.Write(ref _armed, 1);
    public bool WaitUntilBlocked(TimeSpan timeout) => _entered.Wait(timeout);
    public void Release() => _release.Set();

    public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => inner.Decrypt(data, padding);
    public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => inner.Encrypt(data, padding);
    public override RSAParameters ExportParameters(bool includePrivateParameters) => inner.ExportParameters(includePrivateParameters);
    public override void ImportParameters(RSAParameters parameters) => inner.ImportParameters(parameters);
    public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        inner.SignHash(hash, hashAlgorithm, padding);

    public override bool VerifyHash(
        byte[] hash,
        byte[] signature,
        HashAlgorithmName hashAlgorithm,
        RSASignaturePadding padding)
    {
        if (Volatile.Read(ref _armed) != 0 && Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("blocking RSA verification release timed out");
            }
        }
        return inner.VerifyHash(hash, signature, hashAlgorithm, padding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _entered.Dispose();
            _release.Dispose();
        }
        base.Dispose(disposing);
    }
}

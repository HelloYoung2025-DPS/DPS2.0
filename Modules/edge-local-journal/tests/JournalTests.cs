using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dps.EdgeLocalJournal;
using Xunit;

namespace Dps.EdgeLocalJournal.Tests;

public sealed class JournalTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Canonical_duplicate_is_noop_and_cross_scope_duplicate_is_quarantined()
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Equal("{\"a\":1,\"b\":2}", CanonicalJson.Canonicalize("{\"b\":2,\"a\":1}"));
        var directory = TestDirectory.Create();
        await using var store = await JournalStore.OpenAsync(Path.Combine(directory, "journal.jsonl"), token);
        var request = Request("entry-1", "{\"b\":2,\"a\":1}");
        var first = await store.AppendAsync(request, token);
        var duplicate = await store.AppendAsync(request with { PayloadJson = "{\"a\":1,\"b\":2}" }, token);
        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.EntryChecksum, duplicate.EntryChecksum);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { EntryId = "entry-bad-hash", PayloadSha256 = new string('0', 64) }, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { TraceId = request.TraceId + "\n" }, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { CommandId = request.CommandId + "\n" }, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { EntryId = request.EntryId + "\n" }, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { EntryId = "entry/unsafe" }, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { OccurredAt = DateTimeOffset.Parse("2026-07-14T08:00:00+08:00") }, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { EntryType = "AB" }, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(request with { EntryType = "COMMAND_STATE\n" }, token));
        await Assert.ThrowsAsync<JsonException>(
            () => store.AppendAsync(Request("entry-duplicate-json", "{\"a\":1,\"a\":1}"), token));

        await Assert.ThrowsAsync<JournalConflictException>(
            () => store.AppendAsync(request with { SoulId = "soul_" + new string('b', 64) }, token));
        Assert.True(store.IsQuarantined);
        Assert.NotNull(await store.GetQuarantineStatusAsync(token));
        await Assert.ThrowsAsync<JournalQuarantinedException>(
            () => store.AppendAsync(Request("entry-after-conflict", "{}"), token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Length_prefixes_separate_fields_even_when_untrusted_values_contain_newlines()
    {
        var leftFields = new[] { "command\npart", "entry" };
        var rightFields = new[] { "command", "part\nentry" };
        Assert.Equal(string.Join("\n", leftFields), string.Join("\n", rightFields));
        Assert.NotEqual(
            JournalChecksumEncoding.ComputeSha256("dps.test/v1", leftFields),
            JournalChecksumEncoding.ComputeSha256("dps.test/v1", rightFields));
        Assert.NotEqual(
            JournalChecksumEncoding.ComputeSha256("dps.test/v1", leftFields),
            JournalChecksumEncoding.ComputeSha256("dps.test/v2", leftFields));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Every_identity_provenance_and_payload_conflict_quarantines_its_store()
    {
        var token = TestContext.Current.CancellationToken;
        Func<JournalAppendRequest, JournalAppendRequest>[] conflicts =
        [
            request => request with { SoulId = "soul_" + new string('b', 64) },
            request => request with { DeviceBindingId = "db_" + new string('b', 32) },
            request => request with { PlatformAccountId = "pa_" + new string('c', 32) },
            request => request with { IdempotencyKey = "idem_" + new string('d', 64) },
            request => request with { ProducerModule = "windows-edge-worker" },
            _ => Request("entry-1", "{\"changed\":true}")
        ];

        foreach (var mutate in conflicts)
        {
            var directory = TestDirectory.Create();
            await using var store = await JournalStore.OpenAsync(
                Path.Combine(directory, "journal.jsonl"),
                token);
            var request = Request("entry-1", "{}");
            await store.AppendAsync(request, token);
            await Assert.ThrowsAsync<JournalConflictException>(
                () => store.AppendAsync(mutate(request), token));
            Assert.True(store.IsQuarantined);
            Assert.NotNull(await store.GetQuarantineStatusAsync(token));
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Contract_binds_owner_common_fields_and_canonical_identity_patterns()
    {
        var rootDirectory = Path.Combine(TestDirectory.RepositoryRoot(), "Modules/edge-local-journal/contracts/provided");
        using var append = JsonDocument.Parse(File.ReadAllText(Path.Combine(rootDirectory, "edge.journal.append.v1.schema.json")));
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(rootDirectory, "edge.journal.receipt.v1.schema.json")));
        using var checksum = JsonDocument.Parse(File.ReadAllText(Path.Combine(rootDirectory, "edge.journal.checksum.v1.json")));
        Assert.False(append.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("edge.journal.append/v1", append.RootElement.GetProperty("properties").GetProperty("contract_id").GetProperty("const").GetString());
        Assert.Equal(
            new[] { "windows-edge-supervisor", "windows-edge-worker" },
            append.RootElement.GetProperty("properties").GetProperty("producer_module").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("edge.journal.receipt/v1", receipt.RootElement.GetProperty("properties").GetProperty("contract_id").GetProperty("const").GetString());
        Assert.Equal("edge-local-journal", receipt.RootElement.GetProperty("properties").GetProperty("producer_module").GetProperty("const").GetString());
        Assert.Equal("^soul_[a-f0-9]{64}$(?![\\s\\S])", append.RootElement.GetProperty("properties").GetProperty("soul_id").GetProperty("pattern").GetString());
        foreach (var identity in new[] { "soul_id", "device_binding_id", "platform_account_id" })
        {
            Assert.Equal("string", append.RootElement.GetProperty("properties").GetProperty(identity).GetProperty("type").GetString());
            Assert.Equal("string", receipt.RootElement.GetProperty("properties").GetProperty(identity).GetProperty("type").GetString());
        }
        Assert.Contains("command_id", append.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            JournalChecksumEncoding.Name,
            checksum.RootElement.GetProperty("encoding").GetString());
        Assert.Equal(
            "reject",
            checksum.RootElement.GetProperty("journal_line_discriminator").GetProperty("missing_or_unknown").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Two_instances_serialize_crash_recovery_and_scope_tampering_fails_closed()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = TestDirectory.Create();
        var path = Path.Combine(directory, "journal.jsonl");
        await using var firstStore = await JournalStore.OpenAsync(path, token);
        await using var secondStore = await JournalStore.OpenAsync(path, token);
        await Task.WhenAll(
            firstStore.AppendAsync(Request("entry-1", "{\"state\":\"ACCEPTED\"}"), token),
            secondStore.AppendAsync(Request("entry-2", "{\"state\":\"DISPATCHED\"}"), token));

        await File.AppendAllTextAsync(path, "{\"incomplete\":true", token);
        await using (var recovered = await JournalStore.OpenAsync(path, token))
        {
            Assert.Equal(2, recovered.Count);
            var receipt = await recovered.AppendAsync(Request("entry-3", "{\"state\":\"RECOVERED\"}"), token);
            Assert.Equal(3L, receipt.Sequence);
        }

        Assert.Single(Directory.GetFiles(directory, "*.crash-tail"));

        var scopeTamper = Path.Combine(directory, "scope-tamper.jsonl");
        File.Copy(path, scopeTamper);
        var committed = await File.ReadAllTextAsync(scopeTamper, token);
        await File.WriteAllTextAsync(scopeTamper, committed.Replace(
            "soul_" + new string('a', 64),
            "soul_" + new string('b', 64),
            StringComparison.Ordinal), token);
        await Assert.ThrowsAsync<JournalCorruptionException>(async () =>
        {
            await using var ignored = await JournalStore.OpenAsync(scopeTamper, token);
        });

        var unknownField = Path.Combine(directory, "unknown-field.jsonl");
        File.Copy(path, unknownField);
        var validLine = (await File.ReadAllLinesAsync(unknownField, token))[0];
        await File.WriteAllTextAsync(
            unknownField,
            validLine[..^1] + ",\"evil\":\"x\"}\n",
            token);
        await Assert.ThrowsAsync<JournalCorruptionException>(async () =>
        {
            await using var ignored = await JournalStore.OpenAsync(unknownField, token);
        });

        var duplicateField = Path.Combine(directory, "duplicate-field.jsonl");
        File.Copy(path, duplicateField);
        validLine = (await File.ReadAllLinesAsync(duplicateField, token))[0];
        await File.WriteAllTextAsync(
            duplicateField,
            validLine[..^1] + ",\"entry_id\":\"entry-1\"}\n",
            token);
        await Assert.ThrowsAsync<JournalCorruptionException>(async () =>
        {
            await using var ignored = await JournalStore.OpenAsync(duplicateField, token);
        });

        var legacyChecksumEncoding = Path.Combine(directory, "legacy-checksum-encoding.jsonl");
        File.Copy(path, legacyChecksumEncoding);
        validLine = (await File.ReadAllLinesAsync(legacyChecksumEncoding, token))[0];
        var legacyLine = validLine.Replace(
            "\"checksum_encoding\":\"dps.length-prefixed-utf8/v1\",",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(validLine, legacyLine);
        await File.WriteAllTextAsync(
            legacyChecksumEncoding,
            legacyLine + "\n",
            token);
        await Assert.ThrowsAsync<JournalCorruptionException>(async () =>
        {
            await using var ignored = await JournalStore.OpenAsync(legacyChecksumEncoding, token);
        });

        var oversized = Path.Combine(directory, "oversized.jsonl");
        await using (var sparse = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            sparse.SetLength(JournalStore.MaximumJournalFileBytes + 1);
        }
        await Assert.ThrowsAsync<JournalCorruptionException>(async () =>
        {
            await using var ignored = await JournalStore.OpenAsync(oversized, token);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Conflicting_duplicate_persists_quarantine_across_restart_until_exact_digest_release()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = TestDirectory.Create();
        var path = Path.Combine(directory, "journal.jsonl");
        string markerSha256;
        await using (var store = await JournalStore.OpenAsync(path, token))
        {
            var request = Request("entry-1", "{\"state\":\"ACCEPTED\"}");
            await store.AppendAsync(request, token);
            await Assert.ThrowsAsync<JournalConflictException>(
                () => store.AppendAsync(
                    request with { ProducerModule = "windows-edge-worker" },
                    token));
            var status = Assert.IsType<JournalQuarantineStatus>(
                await store.GetQuarantineStatusAsync(token));
            markerSha256 = status.MarkerSha256;
            Assert.Equal("conflicting_duplicate", status.Reason);
            Assert.Equal("entry-1", status.EntryId);
            await Assert.ThrowsAsync<JournalQuarantinedException>(
                () => store.AppendAsync(Request("entry-2", "{}"), token));
        }

        await using var recovered = await JournalStore.OpenAsync(path, token);
        Assert.True(recovered.IsQuarantined);
        await Assert.ThrowsAsync<JournalQuarantinedException>(
            () => recovered.AppendAsync(Request("entry-2", "{}"), token));
        await Assert.ThrowsAsync<JournalQuarantinedException>(
            () => recovered.RecoverFromQuarantineAsync(new string('0', 64), token));
        Assert.True(recovered.IsQuarantined);

        await recovered.RecoverFromQuarantineAsync(markerSha256, token);
        Assert.False(recovered.IsQuarantined);
        Assert.Null(await recovered.GetQuarantineStatusAsync(token));
        Assert.Single(Directory.GetFiles(directory, "*.released-quarantine.*.json"));
        var receipt = await recovered.AppendAsync(Request("entry-2", "{}"), token);
        Assert.Equal(2, receipt.Sequence);
    }

    private static JournalAppendRequest Request(string entryId, string payload)
    {
        var canonical = CanonicalJson.Canonicalize(payload);
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new JournalAppendRequest(
            "1.0",
            "edge.journal.append/v1",
            "windows-edge-supervisor",
            "command-1",
            entryId,
            "COMMAND_STATE",
            "trace_" + new string('1', 32),
            "idem_" + new string('2', 64),
            "personal",
            "soul_" + new string('a', 64),
            "db_" + new string('3', 32),
            "pa_" + new string('4', 32),
            payload,
            payloadSha256,
            DateTimeOffset.Parse("2026-07-14T00:00:00Z"));
    }
}

internal static class TestDirectory
{
    public static string Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "dps-edge-journal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

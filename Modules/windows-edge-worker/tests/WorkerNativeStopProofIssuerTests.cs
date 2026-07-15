using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Dps.ExecutorGateway.Contracts;
using Xunit;

namespace Dps.WindowsEdgeWorker.Tests;

public sealed class WorkerNativeStopProofIssuerTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 15, 10, 0, 2, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Legacy_v1_issuer_is_a_zero_authority_quarantine_tombstone()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        await using var store = DurableNativeStopProofStore.Open(runtime.Path);
        var collaborators = new CountingCollaborators(Request());
        var issuer = collaborators.CreateIssuer(store);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            issuer.IssueAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains("quarantine-only", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, collaborators.IdentityReads);
        Assert.Equal(0, collaborators.StopCalls);
        Assert.Equal(0, collaborators.SignCalls);
        Assert.Equal(0, collaborators.VerifyCalls);
        Assert.Null(store.InspectExisting(Request().SubmissionAttemptId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Quarantine_store_exposes_metadata_only_and_has_no_issuance_API()
    {
        Assert.False(typeof(WorkerNativeStopProofIssuer).IsPublic);
        Assert.False(typeof(DurableNativeStopProofStore).IsPublic);
        Assert.DoesNotContain(
            typeof(LegacyNativeStopProofV1Observation).GetProperties(),
            property => property.PropertyType == typeof(byte[]) ||
                property.Name.Contains("WireUtf8", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(DurableNativeStopProofStore).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name is "Prepare" or "AcquireIssuanceLeaseAsync");

        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var store = DurableNativeStopProofStore.Open(runtime.Path);
        Assert.Throws<ArgumentException>(() => store.InspectExisting(Guid.Empty));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Legacy_v1_wire_is_owner_decoded_but_only_quarantine_metadata_is_returned()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        var request = Request();
        var expected = WriteLegacyRecord(runtime.Path, request);

        using var store = DurableNativeStopProofStore.Open(runtime.Path);
        var observed = store.InspectExisting(request.SubmissionAttemptId);

        Assert.NotNull(observed);
        Assert.Equal(request.SubmissionAttemptId, observed.SubmissionAttemptId);
        Assert.Equal(expected.InputFingerprintSha256, observed.InputFingerprintSha256);
        Assert.Equal(expected.WireSha256, observed.WireSha256);
        Assert.Equal(expected.WireBytes, observed.ExactWireBytes);
        Assert.Equal("QUARANTINE_ONLY", observed.Disposition);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Manifest_declares_legacy_v1_quarantine_only_with_no_runtime_edge()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(root, "Modules", "windows-edge-worker", "module.yaml")));
        var manifest = document.RootElement;
        var declarations = manifest.GetProperty("contracts").GetProperty("consumed")
            .EnumerateArray()
            .Where(item =>
                item.GetProperty("contractId").GetString() == "native.stop.proof" &&
                item.GetProperty("major").GetInt32() == 1)
            .ToArray();

        var declaration = Assert.Single(declarations);
        Assert.Equal("quarantine-only", declaration.GetProperty("mode").GetString());
        Assert.Equal("deprecated", declaration.GetProperty("status").GetString());
        foreach (var direction in new[] { "inbound", "outbound" })
        {
            Assert.DoesNotContain(
                manifest.GetProperty("communication").GetProperty(direction).EnumerateArray(),
                edge => edge.GetProperty("contractId").GetString() == "native.stop.proof");
        }
        Assert.DoesNotContain(
            manifest.GetProperty("permissions").GetProperty("allowed").EnumerateArray(),
            permission => permission.GetString() is
                "worker:native-stop-proof-sign" or "native:no-later-write-stop");
        Assert.False(manifest.GetProperty("module").GetProperty("releaseEligible").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Repeated_legacy_requests_create_no_stop_call_signature_or_artifact()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        await using var store = DurableNativeStopProofStore.Open(runtime.Path);
        var collaborators = new CountingCollaborators(Request());
        var issuer = collaborators.CreateIssuer(store);

        for (var index = 0; index < 20; index++)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                issuer.IssueAsync(Request(), TestContext.Current.CancellationToken));
        }

        AssertZeroAuthority(collaborators, runtime.Path);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Concurrent_legacy_requests_create_no_stop_call_signature_or_artifact()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        await using var store = DurableNativeStopProofStore.Open(runtime.Path);
        var collaborators = new CountingCollaborators(Request());
        var issuer = collaborators.CreateIssuer(store);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                issuer.IssueAsync(Request(), TestContext.Current.CancellationToken));
        }));

        AssertZeroAuthority(collaborators, runtime.Path);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Cancellation_cannot_convert_quarantine_only_into_emission()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        await using var store = DurableNativeStopProofStore.Open(runtime.Path);
        var collaborators = new CountingCollaborators(Request());
        var issuer = collaborators.CreateIssuer(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            issuer.IssueAsync(Request(), cancellation.Token));

        Assert.Contains("Policy-owned v2", error.Message, StringComparison.Ordinal);
        AssertZeroAuthority(collaborators, runtime.Path);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Restart_repeats_the_same_bounded_quarantine_observation_without_wire_release()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        var request = Request();
        _ = WriteLegacyRecord(runtime.Path, request);
        LegacyNativeStopProofV1Observation first;
        using (var store = DurableNativeStopProofStore.Open(runtime.Path))
            first = store.InspectExisting(request.SubmissionAttemptId)!;
        using var recovered = DurableNativeStopProofStore.Open(runtime.Path);
        var second = recovered.InspectExisting(request.SubmissionAttemptId);

        Assert.Equal(first, second);
        Assert.Equal("QUARANTINE_ONLY", second!.Disposition);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Conflicting_legacy_inputs_cannot_create_or_mutate_quarantine_state()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        await using var store = DurableNativeStopProofStore.Open(runtime.Path);
        var request = Request();
        var collaborators = new CountingCollaborators(request);
        var issuer = collaborators.CreateIssuer(store);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            issuer.IssueAsync(request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            issuer.IssueAsync(
                request with { NativeRequestBindingSha256 = new string('f', 64) },
                TestContext.Current.CancellationToken));

        AssertZeroAuthority(collaborators, runtime.Path);
        Assert.Null(store.InspectExisting(request.SubmissionAttemptId));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Quarantine_store_retains_writer_fence_and_private_file_checks()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using (var first = DurableNativeStopProofStore.Open(runtime.Path))
            Assert.Throws<IOException>(() => DurableNativeStopProofStore.Open(runtime.Path));

        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(
            runtime.Path,
            "native-stop-proof-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{}", System.Text.Encoding.UTF8);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<UnauthorizedAccessException>(() =>
            DurableNativeStopProofStore.Open(runtime.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Malformed_legacy_wire_and_existing_conflict_marker_fail_closed_after_restart()
    {
        var request = Request();
        using (var malformedRuntime = new DrainReceiptTemporaryRuntimeDirectory())
        {
            var malformedPath = LegacyProofPath(malformedRuntime.Path, request.SubmissionAttemptId);
            WritePrivate(malformedPath, "{}"u8.ToArray());
            using var store = DurableNativeStopProofStore.Open(malformedRuntime.Path);
            Assert.ThrowsAny<Exception>(() => store.InspectExisting(request.SubmissionAttemptId));
        }

        using var quarantinedRuntime = new DrainReceiptTemporaryRuntimeDirectory();
        var markerPath = LegacyProofPath(quarantinedRuntime.Path, request.SubmissionAttemptId) +
            ".quarantine";
        WritePrivate(markerPath, "{}"u8.ToArray());
        using var quarantined = DurableNativeStopProofStore.Open(quarantinedRuntime.Path);
        Assert.Throws<NativeStopProofConflictException>(() =>
            quarantined.InspectExisting(request.SubmissionAttemptId));
    }

    private static void AssertZeroAuthority(
        CountingCollaborators collaborators,
        string runtimeDirectory)
    {
        Assert.Equal(0, collaborators.IdentityReads);
        Assert.Equal(0, collaborators.StopCalls);
        Assert.Equal(0, collaborators.SignCalls);
        Assert.Equal(0, collaborators.VerifyCalls);
        Assert.Empty(Directory.EnumerateFiles(
            runtimeDirectory,
            "native-stop-proof-*.json",
            SearchOption.TopDirectoryOnly));
    }

    private static WorkerNativeStopRequest Request() => new(
        Guid.Parse("7b000000-0000-0000-0000-00000000000b"),
        Guid.Parse("71000000-0000-0000-0000-000000000001"),
        Guid.Parse("72000000-0000-0000-0000-000000000002"),
        1,
        new string('1', 64),
        new string('2', 64),
        "soul_" + new string('3', 64),
        "db_" + new string('4', 32),
        "pa_" + new string('5', 32),
        "trace_" + new string('6', 32),
        "idem_" + new string('7', 64),
        new string('8', 64),
        17,
        new string('9', 64),
        "wi_" + new string('a', 32),
        23);

    private static (string InputFingerprintSha256, string WireSha256, int WireBytes)
        WriteLegacyRecord(string runtimeDirectory, WorkerNativeStopRequest request)
    {
        var wire = CreateLegacyOwnerWire(request);
        var wireSha256 = Sha256(wire);
        var fingerprint = new string('f', 64);
        var record = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "1.0",
            submission_attempt_id = request.SubmissionAttemptId.ToString("D"),
            input_fingerprint_sha256 = fingerprint,
            wire_base64 = Convert.ToBase64String(wire),
            wire_sha256 = wireSha256
        });
        WritePrivate(LegacyProofPath(runtimeDirectory, request.SubmissionAttemptId), record);
        return (fingerprint, wireSha256, wire.Length);
    }

    private static byte[] CreateLegacyOwnerWire(WorkerNativeStopRequest request)
    {
        var placeholder = Convert.ToBase64String(
            new byte[NativeAbortConfirmation.P1363SignatureSizeBytes]);
        var proof = new NativeAbortConfirmation(
            NativeAbortConfirmation.CurrentSchemaVersion,
            NativeAbortConfirmation.CurrentContractId,
            NativeAbortConfirmation.CurrentProducerModule,
            true,
            request.SubmissionAttemptId,
            request.CommandId,
            request.LeaseId,
            request.Attempt,
            request.NativeRequestBindingSha256,
            request.SubmittedRequestSha256,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.ActiveReleaseBomSha256,
            request.ActiveReleaseBomGeneration,
            request.ActiveReleaseBomTokenSha256,
            request.WorkerInstanceId,
            request.WorkerGeneration,
            NativeAbortConfirmation.TransportAborted,
            new string('0', 64),
            OccurredAt,
            NativeAbortConfirmation.CurrentPrivacyClass,
            NativeAbortConfirmation.CurrentAuthScope,
            "legacy-v1-quarantine-fixture",
            placeholder);
        proof = proof with
        {
            EvidenceSha256 = NativeStopProofProtocolV1.ComputeEvidenceSha256(proof)
        };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signingBytes = NativeStopProofProtocolV1.CanonicalSigningBytes(proof);
        try
        {
            var signature = key.SignData(
                signingBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            try
            {
                proof = proof with { SignatureBase64 = Convert.ToBase64String(signature) };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
        }
        return System.Text.Encoding.UTF8.GetBytes(
            ExecutorGatewayContractJson.SerializeNativeStopProof(proof));
    }

    private static string LegacyProofPath(string runtimeDirectory, Guid submissionAttemptId) =>
        Path.Combine(
            runtimeDirectory,
            "native-stop-proof-" + submissionAttemptId.ToString("N") + ".json");

    private static void WritePrivate(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("legacy quarantine fixture path has no directory");
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "Modules")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("repository root was not found");
    }

    private sealed class CountingCollaborators
    {
        private readonly WorkerRuntimeIdentitySnapshot _snapshot;
        private int _identityReads;
        private int _stopCalls;
        private int _signCalls;
        private int _verifyCalls;

        public CountingCollaborators(WorkerNativeStopRequest request)
        {
            _snapshot = new WorkerRuntimeIdentitySnapshot(
                request.ActiveReleaseBomSha256,
                request.ActiveReleaseBomGeneration,
                request.ActiveReleaseBomTokenSha256,
                request.WorkerInstanceId,
                request.WorkerGeneration,
                "legacy-v1-quarantine-key");
        }

        public int IdentityReads => Volatile.Read(ref _identityReads);
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public int SignCalls => Volatile.Read(ref _signCalls);
        public int VerifyCalls => Volatile.Read(ref _verifyCalls);

        public WorkerNativeStopProofIssuer CreateIssuer(DurableNativeStopProofStore store) => new(
            new StopController(this),
            new IdentityProvider(this),
            new SigningAuthority(this),
            store,
            TimeProvider.System);

        private sealed class StopController(CountingCollaborators owner) :
            IWorkerNativeNoLaterWriteController
        {
            public Task<WorkerNativeStopKind> StopAndVerifyNoLaterWriteAsync(
                WorkerNativeStopRequest request,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._stopCalls);
                return Task.FromResult(WorkerNativeStopKind.NativeTransportAborted);
            }
        }

        private sealed class IdentityProvider(CountingCollaborators owner) :
            IWorkerRuntimeIdentityProvider
        {
            public WorkerRuntimeIdentitySnapshot ReadCurrent()
            {
                Interlocked.Increment(ref owner._identityReads);
                return owner._snapshot;
            }
        }

        private sealed class SigningAuthority(CountingCollaborators owner) :
            IWorkerNativeStopProofSigningAuthority
        {
            public string KeyId => "legacy-v1-quarantine-key";

            public ValueTask<byte[]> SignAsync(
                ReadOnlyMemory<byte> canonicalSigningBytes,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._signCalls);
                return ValueTask.FromResult(new byte[64]);
            }

            public bool Verify(
                ReadOnlySpan<byte> canonicalSigningBytes,
                ReadOnlySpan<byte> p1363Signature)
            {
                Interlocked.Increment(ref owner._verifyCalls);
                return true;
            }
        }
    }
}

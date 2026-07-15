using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dps.ExecutorGateway.Contracts;

namespace Dps.WindowsEdgeWorker;

internal sealed record PersistedNativeStopProof(
    Guid SubmissionAttemptId,
    string InputFingerprintSha256,
    byte[] ExactWireUtf8,
    string WireSha256);

internal sealed record LegacyNativeStopProofV1Observation(
    Guid SubmissionAttemptId,
    string InputFingerprintSha256,
    string WireSha256,
    int ExactWireBytes,
    string Disposition);

internal sealed class NativeStopProofConflictException(string message) :
    InvalidOperationException(message);

internal sealed class DurableNativeStopProofStore : IDisposable, IAsyncDisposable
{
    private const int MaximumWireBytes = 64 * 1024;
    private const int MaximumRecordBytes = 128 * 1024;
    private const int MaximumArtifacts = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly string _runtimeDirectory;
    private readonly string _writerLeasePath;
    private readonly FileStream _writerLease;
    private readonly RuntimeFileIdentity? _writerLeaseIdentity;
    private bool _disposed;

    private DurableNativeStopProofStore(
        string runtimeDirectory,
        string writerLeasePath,
        FileStream writerLease,
        RuntimeFileIdentity? writerLeaseIdentity)
    {
        _runtimeDirectory = runtimeDirectory;
        _writerLeasePath = writerLeasePath;
        _writerLease = writerLease;
        _writerLeaseIdentity = writerLeaseIdentity;
    }

    public static DurableNativeStopProofStore Open(string runtimeDirectory)
    {
        var directory = SecureRuntimeFileSystem.PrepareDirectory(runtimeDirectory);
        var writerLeasePath = Path.Combine(directory, "native-stop-proofs.writer.lock");
        SecureRuntimeFileSystem.VerifyExistingFile(writerLeasePath);
        FileStream? writerLease = null;
        try
        {
            writerLease = SecureRuntimeFileSystem.OpenOrCreatePrivateFile(
                writerLeasePath,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.WriteThrough);
            var store = new DurableNativeStopProofStore(
                directory,
                writerLeasePath,
                writerLease,
                SecureRuntimeFileSystem.CaptureOpenFileIdentity(
                    writerLease,
                    writerLeasePath));
            store.VerifyArtifactBudget();
            return store;
        }
        catch
        {
            writerLease?.Dispose();
            throw;
        }
    }

    public LegacyNativeStopProofV1Observation? InspectExisting(Guid submissionAttemptId)
    {
        lock (_sync)
        {
            EnsureUsable();
            RequireNonEmptyGuid(submissionAttemptId, nameof(submissionAttemptId));
            var existing = ReadInternal(submissionAttemptId);
            return existing is null
                ? null
                : new LegacyNativeStopProofV1Observation(
                    existing.SubmissionAttemptId,
                    existing.InputFingerprintSha256,
                    existing.WireSha256,
                    existing.ExactWireUtf8.Length,
                    "QUARANTINE_ONLY");
        }
    }

    private PersistedNativeStopProof? ReadInternal(Guid submissionAttemptId)
    {
        var quarantinePath = QuarantinePath(submissionAttemptId);
        SecureRuntimeFileSystem.VerifyExistingFile(quarantinePath);
        if (File.Exists(quarantinePath))
            throw new NativeStopProofConflictException(
                "submission_attempt_id is durably quarantined after conflicting stop-proof input");

        var path = ProofPath(submissionAttemptId);
        SecureRuntimeFileSystem.VerifyExistingFile(path);
        if (!File.Exists(path))
            return null;
        var bytes = ReadPrivateFile(path, MaximumRecordBytes);
        ProofRecord record;
        try
        {
            record = JsonSerializer.Deserialize<ProofRecord>(bytes, SerializerOptions) ??
                throw new InvalidDataException("native stop-proof record is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("native stop-proof record JSON is invalid", exception);
        }
        if (record.SchemaVersion != "1.0" ||
            record.SubmissionAttemptId != submissionAttemptId.ToString("D"))
            throw new InvalidDataException("native stop-proof record identity is invalid");
        RequireLowerSha256(record.InputFingerprintSha256, nameof(record.InputFingerprintSha256));
        RequireLowerSha256(record.WireSha256, nameof(record.WireSha256));
        var wire = DecodeCanonicalBase64(record.WireBase64);
        ValidateWire(wire, submissionAttemptId);
        if (Sha256(wire) != record.WireSha256)
            throw new InvalidDataException("native stop-proof exact wire digest is invalid");
        return new PersistedNativeStopProof(
            submissionAttemptId,
            record.InputFingerprintSha256,
            wire,
            record.WireSha256);
    }

    private void VerifyArtifactBudget()
    {
        SecureRuntimeFileSystem.VerifyOpenFileIdentity(
            _writerLease,
            _writerLeasePath,
            _writerLeaseIdentity);
        var paths = Directory.EnumerateFileSystemEntries(
                _runtimeDirectory,
                "native-stop-proof-*",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumArtifacts + 1)
            .ToArray();
        if (paths.Length > MaximumArtifacts)
            throw new IOException("native stop-proof artifact count exceeds its hard limit");
        foreach (var path in paths)
            SecureRuntimeFileSystem.VerifyExistingFile(path);
    }

    private static byte[] ReadPrivateFile(string path, int maximumBytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
            BufferSize = 4096
        };
        using var stream = new FileStream(path, options);
        var identity = SecureRuntimeFileSystem.CaptureOpenFileIdentity(stream, path);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
            throw new InvalidDataException("native stop-proof artifact size is invalid");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("native stop-proof artifact grew during bounded read");
        SecureRuntimeFileSystem.VerifyOpenFileIdentity(stream, path, identity);
        return bytes;
    }

    private static byte[] DecodeCanonicalBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (Convert.ToBase64String(bytes) != value)
                throw new InvalidDataException("native stop-proof wire is not canonical Base64");
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "native stop-proof wire is not canonical Base64",
                exception);
        }
    }

    private static void ValidateWire(
        ReadOnlySpan<byte> exactWireUtf8,
        Guid submissionAttemptId)
    {
        if (exactWireUtf8.IsEmpty || exactWireUtf8.Length > MaximumWireBytes)
            throw new ArgumentOutOfRangeException(nameof(exactWireUtf8));
        string json;
        try
        {
            json = StrictUtf8.GetString(exactWireUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "native stop-proof wire is not strict UTF-8",
                exception);
        }
        var proof = ExecutorGatewayContractJson.DeserializeNativeStopProof(json);
        if (proof.SubmissionAttemptId != submissionAttemptId ||
            ExecutorGatewayContractJson.SerializeNativeStopProof(proof) != json)
            throw new InvalidDataException(
                "native stop-proof wire is not exact owner-canonical JSON for its attempt");
    }

    private string ProofPath(Guid submissionAttemptId) => Path.Combine(
        _runtimeDirectory,
        "native-stop-proof-" + submissionAttemptId.ToString("N") + ".json");

    private string QuarantinePath(Guid submissionAttemptId) =>
        ProofPath(submissionAttemptId) + ".quarantine";

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SecureRuntimeFileSystem.VerifyOpenFileIdentity(
            _writerLease,
            _writerLeasePath,
            _writerLeaseIdentity);
    }

    private static void RequireNonEmptyGuid(Guid value, string parameter)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("UUID cannot be empty", parameter);
    }

    private static void RequireLowerSha256(string? value, string parameter)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new ArgumentException(
                parameter + " must be 64 lowercase hexadecimal characters");
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writerLease.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record ProofRecord(
        string SchemaVersion,
        string SubmissionAttemptId,
        string InputFingerprintSha256,
        string WireBase64,
        string WireSha256);

}

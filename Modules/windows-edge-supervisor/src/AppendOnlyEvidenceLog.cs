using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Dps.WindowsEdgeSupervisor;

public sealed record EvidenceLogCheckpoint(
    long EntryCount,
    string HeadSha256,
    string FileIdentitySha256);

public sealed record EvidenceLogEntryV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("sequence"), JsonRequired] long Sequence,
    [property: JsonPropertyName("occurred_at"), JsonRequired] string OccurredAt,
    [property: JsonPropertyName("event_type"), JsonRequired] string EventType,
    [property: JsonPropertyName("payload_sha256"), JsonRequired] string PayloadSha256,
    [property: JsonPropertyName("previous_entry_sha256"), JsonRequired] string PreviousEntrySha256,
    [property: JsonPropertyName("entry_sha256"), JsonRequired] string EntrySha256);

/// <summary>
/// Single-writer, append-only evidence journal. The open file handle is kept for
/// the lifetime of the host and denies replacement on Windows. Every entry is
/// chained to its predecessor and synchronously flushed before it is returned.
/// Payload content is never stored: callers provide bytes and only their digest
/// enters the journal.
/// </summary>
public sealed class AppendOnlyEvidenceLog : IDisposable
{
    private const string SchemaVersion = "dps.edge-supervisor-evidence-log/v1";
    private const string HashDomain = "dps.edge-supervisor-evidence-entry/v1";
    private const long MaximumExistingBytes = 128L * 1024 * 1024;
    private static readonly string ZeroSha256 = new('0', 64);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex EventTypePattern = new(
        "^[a-z][a-z0-9.-]{0,63}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CanonicalUtcPattern = new(
        "^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-5][0-9]:[0-5][0-9](?:\\.[0-9]+)?(?:Z|\\+00:00)\\z",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly SecurePathProof _parentProof;
    private readonly FileStream _stream;
    private readonly string _stableFileIdentitySha256;
    private long _entryCount;
    private string _headSha256 = ZeroSha256;
    private DateTimeOffset? _lastOccurredAt;
    private bool _disposed;

    public AppendOnlyEvidenceLog(string approvedRuntimeRoot, string evidenceLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedRuntimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceLogPath);
        if (!Path.IsPathFullyQualified(approvedRuntimeRoot) || !Path.IsPathFullyQualified(evidenceLogPath))
            throw new ArgumentException("evidence paths must be absolute");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRuntimeRoot));
        _path = Path.GetFullPath(evidenceLogPath);
        var relative = Path.GetRelativePath(root, _path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("evidence log is outside the approved runtime root");
        var parent = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("evidence log has no parent directory");
        var parentBeforeOpen = SecurePathProof.CaptureDirectory(root, parent);
        RejectLinkIfPresent(_path);

        _stream = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough | FileOptions.SequentialScan);
        try
        {
            RejectLinkIfPresent(_path);
            _parentProof = SecurePathProof.CaptureDirectory(root, parent);
            parentBeforeOpen.Revalidate();
            _stableFileIdentitySha256 = StableFileIdentity.FromOpenHandle(_stream, _path);
            RevalidateOpenPathIdentity();
            LoadAndValidateExistingChain();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public EvidenceLogCheckpoint Checkpoint
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                RevalidateOpenPathIdentity();
                return new EvidenceLogCheckpoint(
                    _entryCount,
                    _headSha256,
                    _stableFileIdentitySha256);
            }
        }
    }

    public EvidenceLogCheckpoint Append(string eventType, ReadOnlySpan<byte> payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || !EventTypePattern.IsMatch(eventType))
            throw new ArgumentException("evidence event type is not canonical", nameof(eventType));
        if (payload.Length > 1024 * 1024)
            throw new InvalidDataException("evidence payload exceeds the hashing limit");
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload));

        lock (_sync)
        {
            ThrowIfDisposed();
            RevalidateOpenPathIdentity();
            var now = DateTimeOffset.UtcNow;
            if (_lastOccurredAt is not null && now < _lastOccurredAt.Value)
                throw new InvalidOperationException("system clock moved backwards while appending evidence");
            var occurredAt = now.ToString("O", CultureInfo.InvariantCulture);
            var sequence = checked(_entryCount + 1);
            var entrySha256 = ComputeEntrySha256(
                sequence,
                occurredAt,
                eventType,
                payloadSha256,
                _headSha256);
            var entry = new EvidenceLogEntryV1(
                SchemaVersion,
                sequence,
                occurredAt,
                eventType,
                payloadSha256,
                _headSha256,
                entrySha256);
            var encoded = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            if (encoded.Length > 8192)
                throw new InvalidDataException("evidence log entry exceeds the line-size limit");
            _stream.Seek(0, SeekOrigin.End);
            _stream.Write(encoded);
            _stream.WriteByte((byte)'\n');
            _stream.Flush(flushToDisk: true);
            _entryCount = sequence;
            _headSha256 = entrySha256;
            _lastOccurredAt = now;
            RevalidateOpenPathIdentity();
            return new EvidenceLogCheckpoint(
                _entryCount,
                _headSha256,
                _stableFileIdentitySha256);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }

    public static string ComputeEntrySha256(
        long sequence,
        string occurredAt,
        string eventType,
        string payloadSha256,
        string previousEntrySha256)
    {
        if (sequence <= 0)
            throw new InvalidDataException("evidence sequence must be positive");
        if (!TryParseCanonicalUtc(occurredAt, out _))
            throw new InvalidDataException("evidence timestamp is not canonical UTC");
        if (!EventTypePattern.IsMatch(eventType))
            throw new InvalidDataException("evidence event type is not canonical");
        RequireSha256(payloadSha256, "payload_sha256");
        RequireSha256(previousEntrySha256, "previous_entry_sha256");
        var statement = string.Join(
            "\n",
            HashDomain,
            sequence.ToString(CultureInfo.InvariantCulture),
            occurredAt,
            eventType,
            payloadSha256,
            previousEntrySha256);
        return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(statement)));
    }

    private void LoadAndValidateExistingChain()
    {
        if (_stream.Length > MaximumExistingBytes)
            throw new InvalidDataException("existing evidence log exceeds the validation limit");
        _stream.Position = 0;
        var bytes = new byte[checked((int)_stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = _stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException("evidence log ended during validation");
            offset += read;
        }
        if (bytes.Length == 0)
        {
            _stream.Position = 0;
            return;
        }
        if (bytes[^1] != (byte)'\n')
            throw new InvalidDataException("evidence log has a torn final entry");

        var text = StrictUtf8.GetString(bytes);
        var lines = text.Split('\n');
        var expectedSequence = 1L;
        var expectedPrevious = ZeroSha256;
        DateTimeOffset? priorOccurredAt = null;
        foreach (var line in lines[..^1])
        {
            if (line.Length == 0 || StrictUtf8.GetByteCount(line) > 8192)
                throw new InvalidDataException("evidence log contains an empty or oversized entry");
            EvidenceLogEntryV1 entry;
            try
            {
                entry = JsonSerializer.Deserialize<EvidenceLogEntryV1>(line, JsonOptions) ??
                    throw new InvalidDataException("evidence log entry is null");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("evidence log entry JSON is invalid", exception);
            }
            ValidateEntry(entry, expectedSequence, expectedPrevious, priorOccurredAt);
            expectedSequence++;
            expectedPrevious = entry.EntrySha256;
            _ = TryParseCanonicalUtc(entry.OccurredAt, out var parsed);
            priorOccurredAt = parsed;
        }
        _entryCount = expectedSequence - 1;
        _headSha256 = expectedPrevious;
        _lastOccurredAt = priorOccurredAt;
        _stream.Position = _stream.Length;
    }

    private static void ValidateEntry(
        EvidenceLogEntryV1 entry,
        long expectedSequence,
        string expectedPrevious,
        DateTimeOffset? priorOccurredAt)
    {
        if (entry.SchemaVersion != SchemaVersion || entry.Sequence != expectedSequence ||
            entry.PreviousEntrySha256 != expectedPrevious)
            throw new InvalidDataException("evidence log chain identity or sequence is invalid");
        if (!TryParseCanonicalUtc(entry.OccurredAt, out var occurredAt) ||
            priorOccurredAt is not null && occurredAt < priorOccurredAt.Value)
            throw new InvalidDataException("evidence log timestamps are invalid or non-monotonic");
        RequireSha256(entry.PayloadSha256, "payload_sha256");
        RequireSha256(entry.PreviousEntrySha256, "previous_entry_sha256");
        RequireSha256(entry.EntrySha256, "entry_sha256");
        var computed = ComputeEntrySha256(
            entry.Sequence,
            entry.OccurredAt,
            entry.EventType,
            entry.PayloadSha256,
            entry.PreviousEntrySha256);
        if (computed != entry.EntrySha256)
            throw new InvalidDataException("evidence log entry digest does not match its content");
    }

    private void RevalidateOpenPathIdentity()
    {
        _parentProof.Revalidate();
        RejectLinkIfPresent(_path);
        var current = StableFileIdentity.FromPath(_path);
        if (current != _stableFileIdentitySha256)
            throw new InvalidOperationException("evidence log path was replaced while the host was running");
    }

    private static void RejectLinkIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        var info = new FileInfo(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            !string.IsNullOrEmpty(info.LinkTarget) ||
            info.ResolveLinkTarget(returnFinalTarget: false) is not null)
            throw new InvalidOperationException("evidence log cannot be a link or reparse point");
    }

    private static bool TryParseCanonicalUtc(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        return CanonicalUtcPattern.IsMatch(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed) &&
            parsed.Offset == TimeSpan.Zero;
    }

    private static void RequireSha256(string? value, string field)
    {
        if (value is null || value.Length != 64 ||
            !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException(field + " is not canonical lowercase SHA-256");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static class StableFileIdentity
    {
        public static string FromPath(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1,
                FileOptions.None);
            return FromOpenHandle(stream, path);
        }

        public static string FromOpenHandle(FileStream stream, string path)
        {
            string native;
            if (OperatingSystem.IsWindows())
            {
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "unable to read evidence-log file identity");
                if (information.NumberOfLinks != 1)
                    throw new InvalidOperationException("hard-linked evidence logs are forbidden");
                var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
                native = information.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture) +
                    ":" + fileIndex.ToString("x16", CultureInfo.InvariantCulture);
            }
            else
            {
                var info = new FileInfo(path);
                info.Refresh();
                native = "portable-synthetic:" + Path.GetFullPath(path) + ":" +
                    info.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(native)));
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public NativeFileTime CreationTime;
            public NativeFileTime LastAccessTime;
            public NativeFileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}

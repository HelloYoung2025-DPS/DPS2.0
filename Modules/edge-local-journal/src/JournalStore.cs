using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace Dps.EdgeLocalJournal;

public sealed partial class JournalStore :
    IJournalAppendClient,
    IJournalReadiness,
    IJournalDrainAttestationProvider,
    IJournalQuarantineAdministration,
    IAsyncDisposable
{
    public const long MaximumJournalFileBytes = 64L * 1024 * 1024;
    public const int MaximumJournalRecordBytes = 4 * 1024 * 1024;
    public const int MaximumCanonicalPayloadBytes = 1024 * 1024;

    private const string GenesisChecksum = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string ChecksumEncoding = JournalChecksumEncoding.Name;
    private const string IdentityChecksumDomain = "dps.edge-local-journal.identity-sha256/v1";
    private const string EntryChecksumDomain = "dps.edge-local-journal.entry-sha256/v1";
    private const int MaximumQuarantineMarkerBytes = 16384;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly ConcurrentDictionary<string, JournalPathCoordination> PathCoordinations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> JournalLineFields = new(
        new[]
        {
            "schema_version", "contract_id", "producer_module", "command_id",
            "entry_id", "entry_type", "trace_id", "idempotency_key",
            "privacy_class", "soul_id", "device_binding_id", "platform_account_id",
            "occurred_at", "sequence", "previous_checksum", "payload_json",
            "payload_sha256", "checksum_encoding", "identity_sha256", "entry_checksum"
        },
        StringComparer.Ordinal);
    private static readonly HashSet<string> QuarantineMarkerFields = new(
        new[]
        {
            "schema_version", "reason", "entry_id", "existing_identity_sha256",
            "conflicting_identity_sha256", "detected_at", "journal_head_sequence",
            "journal_head_checksum"
        },
        StringComparer.Ordinal);

    private readonly string _path;
    private readonly string _quarantinePath;
    private readonly JournalPathCoordination _coordination;
    private readonly SemaphoreSlim _appendLock;
    private readonly JournalDrainAttestationAuthority? _attestationAuthority;
    private readonly Dictionary<string, JournalLine> _byEntryId = new(StringComparer.Ordinal);
    private long _lastSequence;
    private string _lastChecksum = GenesisChecksum;
    private bool _quarantined;
    private bool _disposed;

    private JournalStore(string path, JournalDrainAttestationAuthority? attestationAuthority)
    {
        _path = path;
        _quarantinePath = path + ".quarantine.json";
        _coordination = PathCoordinations.GetOrAdd(path, static _ => new JournalPathCoordination());
        _appendLock = _coordination.Lock;
        _attestationAuthority = attestationAuthority;
    }

    public int Count => _byEntryId.Count;

    public bool IsQuarantined => _quarantined || File.Exists(_quarantinePath);

    public static async Task<JournalStore> OpenAsync(string path, CancellationToken cancellationToken = default)
        => await OpenCoreAsync(path, null, cancellationToken).ConfigureAwait(false);

    public static async Task<JournalStore> OpenWithAttestationAuthorityAsync(
        string path,
        JournalDrainAttestationAuthority attestationAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestationAuthority);
        return await OpenCoreAsync(path, attestationAuthority, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JournalStore> OpenCoreAsync(
        string path,
        JournalDrainAttestationAuthority? attestationAuthority,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var store = new JournalStore(fullPath, attestationAuthority);
        await store._appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var writerLease = await store.AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
            await store.ReloadAsync(cancellationToken).ConfigureAwait(false);
            store._quarantined = File.Exists(store._quarantinePath);
        }
        finally
        {
            store._appendLock.Release();
        }

        return store;
    }

    public async Task<JournalReceipt> AppendAsync(
        JournalAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_quarantined || File.Exists(_quarantinePath))
        {
            _quarantined = true;
            throw new JournalQuarantinedException(
                "journal intake is stopped by a persistent quarantine marker");
        }

        ValidateRequest(request);
        var canonicalPayload = CanonicalJson.Canonicalize(request.PayloadJson);
        ValidateCanonicalPayloadSize(canonicalPayload);
        var payloadSha256 = Sha256(canonicalPayload);
        if (!string.Equals(payloadSha256, request.PayloadSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("payload_sha256 does not match the canonical payload");
        }

        await using var appendIntent = await CreateAppendIntentAsync(request, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _coordination.PendingAppends);
        var lockTaken = false;
        try
        {
            await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            await using var writerLease = await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
            writerLease.AssertStillBound();
            if (_quarantined || File.Exists(_quarantinePath))
            {
                _quarantined = true;
                throw new JournalQuarantinedException(
                    "journal intake is stopped by a persistent quarantine marker");
            }

            _quarantined = false;
            await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            if (_byEntryId.TryGetValue(request.EntryId, out var existing))
            {
                var duplicateIdentity = ComputeIdentityChecksum(request, canonicalPayload, payloadSha256);
                if (!StringComparer.Ordinal.Equals(existing.IdentitySha256, duplicateIdentity))
                {
                    await PersistConflictQuarantineAsync(
                        existing,
                        duplicateIdentity,
                        CancellationToken.None).ConfigureAwait(false);
                    throw new JournalConflictException(
                        $"entry_id {request.EntryId} conflicts with the committed identity; journal intake is quarantined");
                }

                return ToReceipt(existing, duplicate: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sequence = checked(_lastSequence + 1);
            var occurredAt = request.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            var identitySha256 = ComputeIdentityChecksum(request, canonicalPayload, payloadSha256);
            var checksum = ComputeEntryChecksum(
                sequence,
                _lastChecksum,
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
                occurredAt,
                payloadSha256,
                identitySha256);

            var line = new JournalLine
            {
                SchemaVersion = "1.0",
                ContractId = request.ContractId,
                ProducerModule = request.ProducerModule,
                CommandId = request.CommandId,
                EntryId = request.EntryId,
                EntryType = request.EntryType,
                TraceId = request.TraceId,
                IdempotencyKey = request.IdempotencyKey,
                PrivacyClass = request.PrivacyClass,
                SoulId = request.SoulId,
                DeviceBindingId = request.DeviceBindingId,
                PlatformAccountId = request.PlatformAccountId,
                OccurredAt = occurredAt,
                Sequence = sequence,
                PreviousChecksum = _lastChecksum,
                PayloadJson = canonicalPayload,
                PayloadSha256 = payloadSha256,
                ChecksumEncoding = ChecksumEncoding,
                IdentitySha256 = identitySha256,
                EntryChecksum = checksum
            };

            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(line, SerializerOptions) + "\n");
            if (bytes.Length > MaximumJournalRecordBytes)
            {
                throw new IOException("serialized journal record exceeds the hard byte limit");
            }

            var currentLength = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (currentLength > MaximumJournalFileBytes - bytes.Length)
            {
                throw new IOException("journal file reached its hard byte limit; intake remains stopped until rotation");
            }

            await using (var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            writerLease.AssertStillBound();

            _byEntryId.Add(line.EntryId, line);
            _lastSequence = line.Sequence;
            _lastChecksum = line.EntryChecksum;
            return ToReceipt(line, duplicate: false);
        }
        finally
        {
            if (lockTaken)
            {
                _appendLock.Release();
            }
            Interlocked.Decrement(ref _coordination.PendingAppends);
        }
    }

    public async Task<JournalQuarantineStatus?> GetQuarantineStatusAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var writerLease = await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadQuarantineMarkerAsync(cancellationToken).ConfigureAwait(false);
            _quarantined = state is not null;
            if (state is null)
            {
                return null;
            }

            return new JournalQuarantineStatus(
                state.Value.MarkerSha256,
                state.Value.Marker.Reason,
                state.Value.Marker.EntryId,
                state.Value.Marker.DetectedAt,
                state.Value.Marker.JournalHeadSequence,
                state.Value.Marker.JournalHeadChecksum);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task RecoverFromQuarantineAsync(
        string expectedMarkerSha256,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsLowerHex(expectedMarkerSha256, 64))
        {
            throw new ArgumentException(
                "expected quarantine marker digest must be exactly 64 lowercase hexadecimal characters",
                nameof(expectedMarkerSha256));
        }

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var writerLease = await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadQuarantineMarkerAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("journal does not have a quarantine marker to recover");
            if (!StringComparer.Ordinal.Equals(state.MarkerSha256, expectedMarkerSha256))
            {
                throw new JournalQuarantinedException(
                    "quarantine marker digest does not match the explicitly reviewed marker");
            }

            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (_lastSequence != state.Marker.JournalHeadSequence ||
                !StringComparer.Ordinal.Equals(_lastChecksum, state.Marker.JournalHeadChecksum))
            {
                throw new JournalCorruptionException(
                    "journal head changed while quarantined; recovery remains blocked");
            }

            var archivePath = _path + ".released-quarantine." + state.MarkerSha256 + ".json";
            if (File.Exists(archivePath))
            {
                throw new JournalQuarantinedException(
                    "the quarantine release evidence path already exists; recovery remains blocked");
            }

            File.Move(_quarantinePath, archivePath);
            _quarantined = false;
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private async Task PersistConflictQuarantineAsync(
        JournalLine existing,
        string conflictingIdentitySha256,
        CancellationToken cancellationToken)
    {
        _quarantined = true;
        if (File.Exists(_quarantinePath))
        {
            return;
        }

        var marker = new JournalQuarantineMarker
        {
            SchemaVersion = "1.0",
            Reason = "conflicting_duplicate",
            EntryId = existing.EntryId,
            ExistingIdentitySha256 = existing.IdentitySha256,
            ConflictingIdentitySha256 = conflictingIdentitySha256,
            DetectedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            JournalHeadSequence = _lastSequence,
            JournalHeadChecksum = _lastChecksum
        };
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(marker, SerializerOptions) + "\n");
        if (bytes.Length > MaximumQuarantineMarkerBytes)
        {
            throw new InvalidOperationException("quarantine marker exceeds its internal size limit");
        }

        await using var stream = new FileStream(
            _quarantinePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task<(JournalQuarantineMarker Marker, string MarkerSha256)?> ReadQuarantineMarkerAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_quarantinePath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(_quarantinePath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaximumQuarantineMarkerBytes)
        {
            throw new JournalCorruptionException("quarantine marker has an invalid byte length");
        }

        string serialized;
        try
        {
            serialized = Utf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JournalCorruptionException(
                "quarantine marker is not strict UTF-8: " + exception.Message);
        }

        JournalQuarantineMarker? marker;
        try
        {
            ValidateExactObjectFields(serialized, QuarantineMarkerFields, "quarantine marker");
            marker = JsonSerializer.Deserialize<JournalQuarantineMarker>(serialized, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new JournalCorruptionException("malformed quarantine marker: " + exception.Message);
        }

        if (marker is null)
        {
            throw new JournalCorruptionException("quarantine marker deserialized to null");
        }

        ValidateQuarantineMarker(marker);
        return (marker, Sha256(bytes));
    }

    private static void ValidateQuarantineMarker(JournalQuarantineMarker marker)
    {
        if (marker.SchemaVersion != "1.0" || marker.Reason != "conflicting_duplicate")
        {
            throw new JournalCorruptionException("unknown quarantine marker identity or reason");
        }

        if (!IsCanonicalJournalId(marker.EntryId, 160) ||
            !IsLowerHex(marker.ExistingIdentitySha256, 64) ||
            !IsLowerHex(marker.ConflictingIdentitySha256, 64) ||
            StringComparer.Ordinal.Equals(
                marker.ExistingIdentitySha256,
                marker.ConflictingIdentitySha256) ||
            marker.JournalHeadSequence < 1 ||
            !IsLowerHex(marker.JournalHeadChecksum, 64))
        {
            throw new JournalCorruptionException("quarantine marker fields are invalid");
        }

        if (!DateTimeOffset.TryParseExact(
                marker.DetectedAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var detectedAt) ||
            detectedAt.Offset != TimeSpan.Zero)
        {
            throw new JournalCorruptionException("quarantine marker detected_at is not canonical UTC");
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var fileLength = new FileInfo(_path).Length;
        if (fileLength > MaximumJournalFileBytes)
        {
            throw new JournalCorruptionException("journal file exceeds the hard recovery byte limit");
        }

        var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return;
        }

        var committedLength = bytes.Length;
        if (bytes[^1] != (byte)'\n')
        {
            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
            committedLength = lastNewline + 1;
            var tail = bytes.AsSpan(committedLength).ToArray();
            var tailHash = Sha256(tail)[..12];
            var crashTailPath = _path + "." + tailHash + ".crash-tail";
            await File.WriteAllBytesAsync(crashTailPath, tail, cancellationToken).ConfigureAwait(false);
            await using var truncate = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read);
            truncate.SetLength(committedLength);
            truncate.Flush(flushToDisk: true);
        }

        if (committedLength == 0)
        {
            return;
        }

        string text;
        try
        {
            text = Utf8NoBom.GetString(bytes, 0, committedLength);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JournalCorruptionException("committed journal is not strict UTF-8: " + exception.Message);
        }

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length - 1; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serialized = lines[index];
            if (serialized.Length == 0)
            {
                throw new JournalCorruptionException("committed journal contains an empty record");
            }
            if (Utf8NoBom.GetByteCount(serialized) > MaximumJournalRecordBytes)
            {
                throw new JournalCorruptionException("committed journal record exceeds the hard byte limit");
            }
            JournalLine? line;
            try
            {
                ValidateSerializedLineShape(serialized);
                line = JsonSerializer.Deserialize<JournalLine>(serialized, SerializerOptions);
            }
            catch (JsonException exception)
            {
                throw new JournalCorruptionException("malformed committed journal line: " + exception.Message);
            }

            if (line is null)
            {
                throw new JournalCorruptionException("committed journal line deserialized to null");
            }

            try
            {
                ValidateRecoveredLine(line);
            }
            catch (JournalCorruptionException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new JournalCorruptionException("invalid committed journal line: " + exception.Message);
            }
            _byEntryId.Add(line.EntryId, line);
            _lastSequence = line.Sequence;
            _lastChecksum = line.EntryChecksum;
        }
    }

    private static void ValidateSerializedLineShape(string serialized)
        => ValidateExactObjectFields(serialized, JournalLineFields, "committed journal line");

    private static void ValidateExactObjectFields(
        string serialized,
        IReadOnlySet<string> expectedFields,
        string objectName)
    {
        using var document = JsonDocument.Parse(serialized);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JournalCorruptionException(objectName + " must be a JSON object");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!expectedFields.Contains(property.Name))
            {
                throw new JournalCorruptionException(objectName + " contains unknown field: " + property.Name);
            }
            if (!seen.Add(property.Name))
            {
                throw new JournalCorruptionException(objectName + " contains duplicate field: " + property.Name);
            }
        }
        if (!seen.SetEquals(expectedFields))
        {
            throw new JournalCorruptionException(objectName + " is missing a required field");
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        _byEntryId.Clear();
        _lastSequence = 0;
        _lastChecksum = GenesisChecksum;
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateRecoveredLine(JournalLine line)
    {
        if (line.SchemaVersion != "1.0" ||
            line.ContractId != "edge.journal.append/v1" ||
            line.ProducerModule is not ("windows-edge-supervisor" or "windows-edge-worker") ||
            line.ChecksumEncoding != ChecksumEncoding)
        {
            throw new JournalCorruptionException("unknown journal schema or checksum encoding");
        }

        if (line.Sequence != _lastSequence + 1 || line.PreviousChecksum != _lastChecksum)
        {
            throw new JournalCorruptionException("journal sequence or previous checksum mismatch");
        }

        var canonical = CanonicalJson.Canonicalize(line.PayloadJson);
        ValidateCanonicalPayloadSize(canonical);
        var payloadHash = Sha256(canonical);
        if (payloadHash != line.PayloadSha256)
        {
            throw new JournalCorruptionException("journal payload checksum mismatch");
        }

        var expected = ComputeEntryChecksum(
            line.Sequence,
            line.PreviousChecksum,
            line.SchemaVersion,
            line.ContractId,
            line.ProducerModule,
            line.CommandId,
            line.EntryId,
            line.EntryType,
            line.TraceId,
            line.IdempotencyKey,
            line.PrivacyClass,
            line.SoulId,
            line.DeviceBindingId,
            line.PlatformAccountId,
            line.OccurredAt,
            line.PayloadSha256,
            line.IdentitySha256);
        if (expected != line.EntryChecksum)
        {
            throw new JournalCorruptionException("journal entry checksum mismatch");
        }

        if (_byEntryId.ContainsKey(line.EntryId))
        {
            throw new JournalCorruptionException("duplicate entry_id exists in committed journal");
        }

        var recoveredRequest = new JournalAppendRequest(
            line.SchemaVersion,
            line.ContractId,
            line.ProducerModule,
            line.CommandId,
            line.EntryId,
            line.EntryType,
            line.TraceId,
            line.IdempotencyKey,
            line.PrivacyClass,
            line.SoulId,
            line.DeviceBindingId,
            line.PlatformAccountId,
            line.PayloadJson,
            line.PayloadSha256,
            DateTimeOffset.ParseExact(
                line.OccurredAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None));
        ValidateRequest(recoveredRequest);
        var identity = ComputeIdentityChecksum(recoveredRequest, canonical, payloadHash);
        if (identity != line.IdentitySha256)
        {
            throw new JournalCorruptionException("journal scope or identity checksum mismatch");
        }
    }

    private static void ValidateRequest(JournalAppendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != "1.0" ||
            request.ContractId != "edge.journal.append/v1" ||
            request.ProducerModule is not ("windows-edge-supervisor" or "windows-edge-worker"))
        {
            throw new ArgumentException("unknown journal contract identity");
        }

        if (!IsCanonicalJournalId(request.EntryId, 160))
        {
            throw new ArgumentException(
                "entry_id must be a 1-to-160-character ASCII token using letters, digits, dot, underscore, colon, or hyphen");
        }

        if (string.IsNullOrWhiteSpace(request.EntryType) ||
            request.EntryType.Length is < 3 or > 64 ||
            request.EntryType[0] is < 'A' or > 'Z' ||
            !request.EntryType.All(character => character == '_' || char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("entry_type must be an uppercase identifier");
        }

        if (request.EntryType != request.EntryType.ToUpperInvariant())
        {
            throw new ArgumentException("entry_type must be uppercase");
        }

        if (request.OccurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("occurred_at must use an explicit zero UTC offset");
        }

        if (request.PayloadJson is null || request.PayloadJson.Length > 262144)
        {
            throw new ArgumentException("payload_json is required and limited to 262144 characters");
        }

        if (!IsLowerHex(request.PayloadSha256, 64))
        {
            throw new ArgumentException("payload_sha256 must be exactly 64 lowercase hexadecimal characters");
        }

        if (!IsPrefixedLowerHex(request.TraceId, "trace_", 32))
        {
            throw new ArgumentException("trace_id must be a canonical opaque identifier");
        }

        if (!IsPrefixedLowerHex(request.IdempotencyKey, "idem_", 64))
        {
            throw new ArgumentException("idempotency_key must be a canonical opaque identifier");
        }

        if (request.PrivacyClass is not ("internal" or "personal" or "sensitive"))
        {
            throw new ArgumentException("privacy_class is invalid");
        }

        if (!IsSoulId(request.SoulId) ||
            !IsDeviceBindingId(request.DeviceBindingId) ||
            !IsPlatformAccountId(request.PlatformAccountId))
        {
            throw new ArgumentException("identity scope does not use canonical DPS identifiers");
        }

        if (!IsCanonicalJournalId(request.CommandId, 128))
        {
            throw new ArgumentException(
                "command_id must be a 1-to-128-character ASCII token using letters, digits, dot, underscore, colon, or hyphen");
        }
    }

    private static JournalReceipt ToReceipt(JournalLine line, bool duplicate) => new(
        "1.0",
        "edge.journal.receipt/v1",
        "edge-local-journal",
        line.ProducerModule,
        line.CommandId,
        line.EntryId,
        line.EntryType,
        line.TraceId,
        line.IdempotencyKey,
        line.PrivacyClass,
        line.SoulId,
        line.DeviceBindingId,
        line.PlatformAccountId,
        line.OccurredAt,
        line.Sequence,
        line.PayloadSha256,
        line.PreviousChecksum,
        line.EntryChecksum,
        Durable: true,
        Duplicate: duplicate);

    private static string ComputeEntryChecksum(
        long sequence,
        string previousChecksum,
        string schemaVersion,
        string contractId,
        string producerModule,
        string commandId,
        string entryId,
        string entryType,
        string traceId,
        string idempotencyKey,
        string privacyClass,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string occurredAt,
        string payloadSha256,
        string identitySha256) => JournalChecksumEncoding.ComputeSha256(
            EntryChecksumDomain,
            sequence.ToString(CultureInfo.InvariantCulture),
            previousChecksum,
            schemaVersion,
            contractId,
            producerModule,
            commandId,
            entryId,
            entryType,
            traceId,
            idempotencyKey,
            privacyClass,
            soulId,
            deviceBindingId,
            platformAccountId,
            occurredAt,
            payloadSha256,
            ChecksumEncoding,
            identitySha256);

    private static string ComputeIdentityChecksum(
        JournalAppendRequest request,
        string canonicalPayload,
        string payloadSha256) => JournalChecksumEncoding.ComputeSha256(
            IdentityChecksumDomain,
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
            request.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            payloadSha256,
            canonicalPayload);

    private static bool IsCanonicalJournalId(string value, int maximumLength)
    {
        if (value is null || value.Length is < 1 || value.Length > maximumLength ||
            !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(
            character => IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsSoulId(string value) =>
        value is not null &&
        value.Length == 69 &&
        value.StartsWith("soul_", StringComparison.Ordinal) &&
        value.AsSpan(5).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDeviceBindingId(string value) =>
        IsPrefixedLowerHex(value, "db_", 32);

    private static bool IsPlatformAccountId(string value) =>
        IsPrefixedLowerHex(value, "pa_", 32);

    private static bool IsPrefixedLowerHex(string value, string prefix, int bodyLength)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + bodyLength)
        {
            return false;
        }

        return value.AsSpan(prefix.Length).ToString().All(
            character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsLowerHex(string value, int length) =>
        value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateCanonicalPayloadSize(string canonicalPayload)
    {
        if (Utf8NoBom.GetByteCount(canonicalPayload) > MaximumCanonicalPayloadBytes)
        {
            throw new ArgumentException("canonical payload exceeds the hard UTF-8 byte limit");
        }
    }

    private static string Sha256(string value) => Sha256(Utf8NoBom.GetBytes(value));

    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class JournalLine
    {
        [JsonPropertyName("schema_version")]
        public required string SchemaVersion { get; init; }

        [JsonPropertyName("contract_id")]
        public required string ContractId { get; init; }

        [JsonPropertyName("producer_module")]
        public required string ProducerModule { get; init; }

        [JsonPropertyName("command_id")]
        public required string CommandId { get; init; }

        [JsonPropertyName("entry_id")]
        public required string EntryId { get; init; }

        [JsonPropertyName("entry_type")]
        public required string EntryType { get; init; }

        [JsonPropertyName("trace_id")]
        public required string TraceId { get; init; }

        [JsonPropertyName("idempotency_key")]
        public required string IdempotencyKey { get; init; }

        [JsonPropertyName("privacy_class")]
        public required string PrivacyClass { get; init; }

        [JsonPropertyName("soul_id")]
        public required string SoulId { get; init; }

        [JsonPropertyName("device_binding_id")]
        public required string DeviceBindingId { get; init; }

        [JsonPropertyName("platform_account_id")]
        public required string PlatformAccountId { get; init; }

        [JsonPropertyName("occurred_at")]
        public required string OccurredAt { get; init; }

        [JsonPropertyName("sequence")]
        public long Sequence { get; init; }

        [JsonPropertyName("previous_checksum")]
        public required string PreviousChecksum { get; init; }

        [JsonPropertyName("payload_json")]
        public required string PayloadJson { get; init; }

        [JsonPropertyName("payload_sha256")]
        public required string PayloadSha256 { get; init; }

        [JsonPropertyName("checksum_encoding")]
        public required string ChecksumEncoding { get; init; }

        [JsonPropertyName("identity_sha256")]
        public required string IdentitySha256 { get; init; }

        [JsonPropertyName("entry_checksum")]
        public required string EntryChecksum { get; init; }
    }

    private sealed class JournalQuarantineMarker
    {
        [JsonPropertyName("schema_version")]
        public required string SchemaVersion { get; init; }

        [JsonPropertyName("reason")]
        public required string Reason { get; init; }

        [JsonPropertyName("entry_id")]
        public required string EntryId { get; init; }

        [JsonPropertyName("existing_identity_sha256")]
        public required string ExistingIdentitySha256 { get; init; }

        [JsonPropertyName("conflicting_identity_sha256")]
        public required string ConflictingIdentitySha256 { get; init; }

        [JsonPropertyName("detected_at")]
        public required string DetectedAt { get; init; }

        [JsonPropertyName("journal_head_sequence")]
        public long JournalHeadSequence { get; init; }

        [JsonPropertyName("journal_head_checksum")]
        public required string JournalHeadChecksum { get; init; }
    }

    private sealed class JournalPathCoordination
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public int PendingAppends;
    }
}

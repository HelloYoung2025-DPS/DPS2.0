using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.EdgeLocalJournal;

#if !EDGE_LOCAL_JOURNAL_CONTRACTS
public sealed class JournalDrainAttestationAuthority : IDisposable
{
    private readonly RSA _privateKey;
    private readonly bool _leaveOpen;
    private bool _disposed;

    public JournalDrainAttestationAuthority(
        RSA externallyInjectedPrivateKey,
        TimeProvider? timeProvider = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(externallyInjectedPrivateKey);
        if (externallyInjectedPrivateKey.KeySize < 2048)
        {
            throw new CryptographicException("Journal drain attestation requires an RSA key of at least 2048 bits.");
        }

        _privateKey = externallyInjectedPrivateKey;
        _leaveOpen = leaveOpen;
        TimeProvider = timeProvider ?? TimeProvider.System;
        KeyId = JournalDrainAttestationCodec.ComputeKeyId(externallyInjectedPrivateKey);

        var probe = Encoding.ASCII.GetBytes("dps.edge-local-journal.attestation-authority-self-test/v1");
        byte[] signature;
        try
        {
            signature = externallyInjectedPrivateKey.SignData(
                probe,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }
        catch (Exception exception) when (exception is CryptographicException or NotSupportedException)
        {
            throw new CryptographicException(
                "The externally injected Journal attestation key does not provide private RSA-PSS signing authority.",
                exception);
        }

        try
        {
            if (!externallyInjectedPrivateKey.VerifyData(
                    probe,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
            {
                throw new CryptographicException("The Journal attestation authority self-test failed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

    }

    public string KeyId { get; }

    internal TimeProvider TimeProvider { get; }

    internal string Sign(byte[] statement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var signature = _privateKey.SignData(
            statement,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        try
        {
            return Convert.ToBase64String(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    internal bool Verify(byte[] statement, string canonicalBase64Signature)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var signature = Convert.FromBase64String(canonicalBase64Signature);
        try
        {
            return _privateKey.VerifyData(
                statement,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _privateKey.Dispose();
        }
    }
}
#endif

#if EDGE_LOCAL_JOURNAL_CONTRACTS
public static class JournalDrainAttestationCodec
{
    public const string SchemaVersion = "1.0";
    public const string ContractId = "edge.journal.drain.attestation/v1";
    public const string ProducerModule = "edge-local-journal";
    public const string Canonicalization = "dps.utf8-byte-length-framing/v1";
    public const string SignatureAlgorithm = "RSA_PSS_SHA256";
    public const string StatementDomain = "dps.edge-local-journal.drain-attestation-envelope/v1";
    public const string OwnerReceiptDomain = "dps.edge-local-journal.owner-receipt/v1";
    public const int MaximumWireBytes = 64 * 1024;
    public static readonly TimeSpan MaximumValidity = TimeSpan.FromMinutes(5);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 16
    };
    private static readonly HashSet<string> ExactFields = new(
        new[]
        {
            "schema_version", "contract_id", "producer_module", "request_producer_module",
            "soul_id", "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "privacy_class", "request_id", "drain_id",
            "command_id", "entry_id", "entry_type", "entry_sequence", "entry_checksum",
            "entry_payload_sha256", "journal_id", "journal_file_sha256", "journal_file_identity_sha256",
            "journal_head_sequence", "journal_head_checksum", "checksum_encoding",
            "range_start_sequence", "range_end_sequence", "range_entry_count",
            "entry_set_sha256", "quarantine_state", "recovery_state",
            "state_artifact_set_sha256", "worker_artifact_sha256", "worker_version",
            "worker_slot", "journal_artifact_sha256", "release_bom_sha256",
            "protected_policy_sha256", "routing_epoch", "intake_stopped", "worker_drained",
            "remaining_in_flight", "worker_receipt_wire_sha256", "journal_receipt_sha256",
            "journal_receipt", "issued_at", "expires_at",
            "canonicalization", "signature_key_id", "signature_algorithm",
            "statement_sha256", "signature"
        },
        StringComparer.Ordinal);

    public static string Serialize(JournalDrainAttestation value)
    {
        Validate(value);
        return JsonSerializer.Serialize(value, StrictJson);
    }

    public static JournalDrainAttestation Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictUtf8.GetByteCount(json) > MaximumWireBytes)
        {
            throw new JsonException("Journal drain attestation exceeds the 64-KiB wire limit.");
        }

        using (var document = JsonDocument.Parse(json, new JsonDocumentOptions
               {
                   AllowTrailingCommas = false,
                   CommentHandling = JsonCommentHandling.Disallow,
                   MaxDepth = 16
               }))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Journal drain attestation must be a JSON object.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!ExactFields.Contains(property.Name))
                {
                    throw new JsonException("Unknown Journal drain attestation field: " + property.Name);
                }
                if (!seen.Add(property.Name))
                {
                    throw new JsonException("Duplicate Journal drain attestation field: " + property.Name);
                }
            }

            if (!seen.SetEquals(ExactFields))
            {
                throw new JsonException("Journal drain attestation is missing a required field.");
            }
        }

        var value = JsonSerializer.Deserialize<JournalDrainAttestation>(json, StrictJson)
            ?? throw new JsonException("Journal drain attestation deserialized to null.");
        Validate(value);
        return value;
    }

    public static void Verify(
        JournalDrainAttestation value,
        RSA externallyPinnedPublicKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(externallyPinnedPublicKey);
        Validate(value);
        if (externallyPinnedPublicKey.KeySize < 2048 ||
            !string.Equals(ComputeKeyId(externallyPinnedPublicKey), value.SignatureKeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException("Journal drain attestation key ID does not match the pinned public key.");
        }

        var statement = EncodeStatement(value);
        var signature = Convert.FromBase64String(value.Signature);
        try
        {
            if (!externallyPinnedPublicKey.VerifyData(
                    statement,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
            {
                throw new CryptographicException("Journal drain attestation signature is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        var issuedAt = ParseCanonicalUtc(value.IssuedAt, nameof(value.IssuedAt));
        var expiresAt = ParseCanonicalUtc(value.ExpiresAt, nameof(value.ExpiresAt));
        var utcNow = now.ToUniversalTime();
        if (utcNow < issuedAt || utcNow >= expiresAt)
        {
            throw new CryptographicException("Journal drain attestation is not currently valid.");
        }
    }

    public static byte[] EncodeStatement(JournalDrainAttestation value)
    {
        ValidateStatementFields(value);
        var fields = new[]
        {
            value.SchemaVersion,
            value.ContractId,
            value.ProducerModule,
            value.RequestProducerModule,
            value.SoulId,
            value.DeviceBindingId,
            value.PlatformAccountId,
            value.TraceId,
            value.IdempotencyKey,
            value.OccurredAt,
            value.PrivacyClass,
            value.RequestId,
            value.DrainId,
            value.CommandId,
            value.EntryId,
            value.EntryType,
            value.EntrySequence.ToString(CultureInfo.InvariantCulture),
            value.EntryChecksum,
            value.EntryPayloadSha256,
            value.JournalId,
            value.JournalFileSha256,
            value.JournalFileIdentitySha256,
            value.JournalHeadSequence.ToString(CultureInfo.InvariantCulture),
            value.JournalHeadChecksum,
            value.ChecksumEncoding,
            value.RangeStartSequence.ToString(CultureInfo.InvariantCulture),
            value.RangeEndSequence.ToString(CultureInfo.InvariantCulture),
            value.RangeEntryCount.ToString(CultureInfo.InvariantCulture),
            value.EntrySetSha256,
            value.QuarantineState,
            value.RecoveryState,
            value.StateArtifactSetSha256,
            value.WorkerArtifactSha256,
            value.WorkerVersion,
            value.WorkerSlot,
            value.JournalArtifactSha256,
            value.ReleaseBomSha256,
            value.ProtectedPolicySha256,
            value.RoutingEpoch.ToString(CultureInfo.InvariantCulture),
            value.IntakeStopped ? "true" : "false",
            value.WorkerDrained ? "true" : "false",
            value.RemainingInFlight.ToString(CultureInfo.InvariantCulture),
            value.WorkerReceiptWireSha256,
            value.JournalReceiptSha256,
            value.IssuedAt,
            value.ExpiresAt,
            value.Canonicalization,
            value.SignatureKeyId,
            value.SignatureAlgorithm
        };

        using var stream = new MemoryStream();
        var domain = StrictUtf8.GetBytes(StatementDomain);
        stream.Write(domain);
        stream.WriteByte((byte)'\n');
        foreach (var field in fields)
        {
            var bytes = StrictUtf8.GetBytes(field);
            var prefix = StrictUtf8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
            stream.Write(prefix);
            stream.Write(bytes);
            stream.WriteByte((byte)';');
        }

        return stream.ToArray();
    }

    public static byte[] EncodeOwnerReceipt(JournalDrainOwnerReceipt receipt)
    {
        ValidateOwnerReceipt(receipt);
        return EncodeOwnerReceiptUnchecked(receipt);
    }

    internal static byte[] EncodeOwnerReceiptUnchecked(JournalDrainOwnerReceipt receipt) =>
        EncodeUtf8LengthFramed(
            OwnerReceiptDomain,
            receipt.SchemaVersion,
            receipt.ContractId,
            receipt.ProducerModule,
            receipt.RequestProducerModule,
            receipt.SoulId,
            receipt.DeviceBindingId,
            receipt.PlatformAccountId,
            receipt.TraceId,
            receipt.IdempotencyKey,
            receipt.OccurredAt,
            receipt.PrivacyClass,
            receipt.CommandId,
            receipt.EntryId,
            receipt.EntryType,
            receipt.PayloadSha256,
            receipt.Sequence.ToString(CultureInfo.InvariantCulture),
            receipt.PreviousChecksum,
            receipt.EntryChecksum,
            receipt.Durable ? "true" : "false",
            receipt.Duplicate ? "true" : "false");

    private static byte[] EncodeUtf8LengthFramed(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        var domainBytes = StrictUtf8.GetBytes(domain);
        stream.Write(domainBytes);
        stream.WriteByte((byte)'\n');
        foreach (var field in fields)
        {
            var bytes = StrictUtf8.GetBytes(field);
            var prefix = StrictUtf8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
            stream.Write(prefix);
            stream.Write(bytes);
            stream.WriteByte((byte)';');
        }
        return stream.ToArray();
    }

    public static string ComputeKeyId(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var subjectPublicKeyInfo = key.ExportSubjectPublicKeyInfo();
        try
        {
            return "sha256_" + Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
        }
    }

    private static void Validate(JournalDrainAttestation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateStatementFields(value);
        RequireSha256(value.StatementSha256, nameof(value.StatementSha256));
        var expectedStatementSha256 = Convert.ToHexString(SHA256.HashData(EncodeStatement(value))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedStatementSha256),
                Convert.FromHexString(value.StatementSha256)))
        {
            throw new InvalidDataException("Journal drain attestation statement_sha256 is invalid.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(value.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Journal drain attestation signature is not canonical Base64.", exception);
        }
        try
        {
            if (signature.Length < 256 || signature.Length > 1536 ||
                !string.Equals(Convert.ToBase64String(signature), value.Signature, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Journal drain attestation signature is not canonical Base64 RSA data.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static void ValidateStatementFields(JournalDrainAttestation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != SchemaVersion || value.ContractId != ContractId ||
            value.ProducerModule != ProducerModule || value.Canonicalization != Canonicalization ||
            value.SignatureAlgorithm != SignatureAlgorithm ||
            value.RequestProducerModule != "windows-edge-supervisor")
        {
            throw new InvalidDataException("Unknown Journal drain attestation identity, producer, or cryptographic profile.");
        }
        if (!IsPrefixedLowerHex(value.SoulId, "soul_", 64) ||
            !IsPrefixedLowerHex(value.DeviceBindingId, "db_", 32) ||
            !IsPrefixedLowerHex(value.PlatformAccountId, "pa_", 32) ||
            !IsPrefixedLowerHex(value.TraceId, "trace_", 32) ||
            !IsPrefixedLowerHex(value.IdempotencyKey, "idem_", 64) ||
            !IsPrefixedLowerHex(value.RequestId, "drainreq_", 64) ||
            !IsPrefixedLowerHex(value.JournalId, "journal_", 64) ||
            !IsPrefixedLowerHex(value.SignatureKeyId, "sha256_", 64))
        {
            throw new InvalidDataException("Journal drain attestation contains a non-canonical opaque identifier.");
        }
        if (!value.DrainId.StartsWith("drain-", StringComparison.Ordinal) ||
            !IsLowerHex(value.DrainId.AsSpan(6), 64) || value.DrainId != value.CommandId ||
            !IsAsciiToken(value.CommandId, 128) || !IsAsciiToken(value.EntryId, 160) ||
            value.EntryType != "WORKER_DRAINED")
        {
            throw new InvalidDataException("Journal drain attestation does not bind one canonical drain command and terminal entry.");
        }
        if (value.PrivacyClass != "internal" || value.ChecksumEncoding != "dps.length-prefixed-utf8/v1" ||
            value.QuarantineState is not ("CLEAR" or "RELEASED") ||
            value.RecoveryState is not ("CLEAN" or "CRASH_TAIL_ISOLATED") ||
            value.WorkerSlot is not ("A" or "B") || !IsAsciiVersion(value.WorkerVersion) ||
            !value.IntakeStopped || !value.WorkerDrained || value.RemainingInFlight != 0)
        {
            throw new InvalidDataException("Journal drain attestation contains an invalid state or deployment binding.");
        }
        foreach (var digest in new[]
                 {
                     value.EntryChecksum, value.EntryPayloadSha256, value.JournalFileSha256,
                     value.JournalFileIdentitySha256,
                     value.JournalHeadChecksum, value.EntrySetSha256, value.StateArtifactSetSha256,
                     value.WorkerArtifactSha256, value.JournalArtifactSha256,
                     value.ReleaseBomSha256, value.ProtectedPolicySha256,
                     value.WorkerReceiptWireSha256, value.JournalReceiptSha256
                 })
        {
            RequireSha256(digest, "digest");
        }
        if (value.EntrySequence < 1 || value.JournalHeadSequence < value.EntrySequence ||
            value.RangeStartSequence < 1 || value.RangeEndSequence < value.RangeStartSequence ||
            value.RangeEndSequence > value.JournalHeadSequence || value.EntrySequence != value.RangeEndSequence ||
            value.RangeEntryCount < 1 || value.RangeEntryCount > value.RangeEndSequence - value.RangeStartSequence + 1 ||
            value.RoutingEpoch < 0 || value.RemainingInFlight < 0)
        {
            throw new InvalidDataException("Journal drain attestation sequence, range, or routing epoch is invalid.");
        }

        _ = ParseCanonicalUtc(value.OccurredAt, nameof(value.OccurredAt));
        var issuedAt = ParseCanonicalUtc(value.IssuedAt, nameof(value.IssuedAt));
        var expiresAt = ParseCanonicalUtc(value.ExpiresAt, nameof(value.ExpiresAt));
        if (expiresAt <= issuedAt || expiresAt - issuedAt > MaximumValidity)
        {
            throw new InvalidDataException("Journal drain attestation validity window is invalid.");
        }

        ValidateOwnerReceipt(value.JournalReceipt);
        if (value.JournalReceipt.SoulId != value.SoulId ||
            value.JournalReceipt.DeviceBindingId != value.DeviceBindingId ||
            value.JournalReceipt.PlatformAccountId != value.PlatformAccountId ||
            value.JournalReceipt.TraceId != value.TraceId ||
            value.JournalReceipt.IdempotencyKey != value.IdempotencyKey ||
            value.JournalReceipt.OccurredAt != value.OccurredAt ||
            value.JournalReceipt.PrivacyClass != value.PrivacyClass ||
            value.JournalReceipt.CommandId != value.CommandId ||
            value.JournalReceipt.EntryId != value.EntryId ||
            value.JournalReceipt.EntryType != value.EntryType ||
            value.JournalReceipt.PayloadSha256 != value.EntryPayloadSha256 ||
            value.JournalReceipt.Sequence != value.EntrySequence ||
            value.JournalReceipt.EntryChecksum != value.EntryChecksum)
        {
            throw new InvalidDataException("Journal owner receipt is not the exact selected durable terminal entry.");
        }

        var journalReceiptSha256 = Convert.ToHexString(
            SHA256.HashData(EncodeOwnerReceiptUnchecked(value.JournalReceipt))).ToLowerInvariant();
        if (journalReceiptSha256 != value.JournalReceiptSha256)
        {
            throw new InvalidDataException("Journal owner receipt digest is invalid.");
        }
    }

    private static void ValidateOwnerReceipt(JournalDrainOwnerReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaVersion != "1.0" || receipt.ContractId != "edge.journal.receipt/v1" ||
            receipt.ProducerModule != ProducerModule || receipt.RequestProducerModule != "windows-edge-worker" ||
            receipt.PrivacyClass != "internal" || receipt.EntryType != "WORKER_DRAINED" ||
            !receipt.Durable || receipt.Sequence < 1)
        {
            throw new InvalidDataException("Journal owner receipt identity or durable truth is invalid.");
        }
        if (!IsPrefixedLowerHex(receipt.SoulId, "soul_", 64) ||
            !IsPrefixedLowerHex(receipt.DeviceBindingId, "db_", 32) ||
            !IsPrefixedLowerHex(receipt.PlatformAccountId, "pa_", 32) ||
            !IsPrefixedLowerHex(receipt.TraceId, "trace_", 32) ||
            !IsPrefixedLowerHex(receipt.IdempotencyKey, "idem_", 64) ||
            !IsAsciiToken(receipt.CommandId, 128) || !IsAsciiToken(receipt.EntryId, 160))
        {
            throw new InvalidDataException("Journal owner receipt scope or token is invalid.");
        }
        _ = ParseCanonicalUtc(receipt.OccurredAt, nameof(receipt.OccurredAt));
        RequireSha256(receipt.PayloadSha256, nameof(receipt.PayloadSha256));
        RequireSha256(receipt.PreviousChecksum, nameof(receipt.PreviousChecksum));
        RequireSha256(receipt.EntryChecksum, nameof(receipt.EntryChecksum));
    }

    private static DateTimeOffset ParseCanonicalUtc(string value, string name)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) || parsed.Offset != TimeSpan.Zero ||
            !string.Equals(parsed.ToString("O", CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(name + " is not exact round-trip UTC text.");
        }

        return parsed;
    }

    private static void RequireSha256(string value, string name)
    {
        if (!IsLowerHex(value.AsSpan(), 64))
        {
            throw new InvalidDataException(name + " must be exactly 64 lowercase hexadecimal characters.");
        }
    }

    private static bool IsPrefixedLowerHex(string value, string prefix, int bodyLength) =>
        value is not null && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length == prefix.Length + bodyLength && IsLowerHex(value.AsSpan(prefix.Length), bodyLength);

    private static bool IsLowerHex(ReadOnlySpan<char> value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAsciiToken(string value, int maximumLength)
    {
        if (value is null || value.Length is < 1 || value.Length > maximumLength || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }
        return value.All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or ':' or '-');
    }

    private static bool IsAsciiVersion(string value) =>
        value is not null && value.Length is >= 1 and <= 64 && IsAsciiAlphaNumeric(value[0]) &&
        value.All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '+' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
#endif

#if !EDGE_LOCAL_JOURNAL_CONTRACTS
public sealed partial class JournalStore
{
    private const string JournalIdDomain = "dps.edge-local-journal.journal-id-sha256/v1";
    private const string DrainEntrySetDomain = "dps.edge-local-journal.drain-entry-set-sha256/v1";
    private const string StateArtifactSetDomain = "dps.edge-local-journal.state-artifact-set-sha256/v1";
    private const int MaximumStateArtifactCount = 1024;
    private const long MaximumStateArtifactBytes = MaximumJournalFileBytes * 4;

    public async Task<JournalDrainAttestation> IssueDrainAttestationAsync(
        JournalDrainAttestationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var authority = _attestationAuthority ?? throw new JournalAttestationUnavailableException(
            "Journal drain attestation is unavailable because no external private-key authority was injected.");
        ValidateDrainAttestationRequest(request);

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JournalWriterLease? writerLease = await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
            JournalWriterLease? finalizationGate = null;
            try
            {
                writerLease.AssertStillBound();
                EnsureNoPendingAppend();
                EnsureNoAppendIntentArtifacts();
                await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
                await using var journalSnapshot = OpenStableJournalSnapshot(_path);
                var beforeReload = await CaptureDurableAttestationFilesAsync(
                    journalSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
                if (beforeReload.QuarantineState == "QUARANTINED")
                {
                    throw new JournalQuarantinedException("A quarantined journal cannot issue a drain attestation.");
                }

                await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
                var stableFiles = await CaptureDurableAttestationFilesAsync(
                    journalSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
                if (beforeReload != stableFiles)
                {
                    throw new JournalAttestationStateChangedException(
                        "Journal descriptor, length, content, or recovery state changed while preparing the drain attestation.");
                }
                EnsureNoPendingAppend();
                EnsureNoAppendIntentArtifacts();

                var state = BuildDrainAttestationState(request, stableFiles);
                ValidateWorkerDrainPayload(state.Terminal, request);
                var issuedAt = authority.TimeProvider.GetUtcNow().ToUniversalTime();
                DateTimeOffset expiresAt;
                try
                {
                    expiresAt = issuedAt.Add(request.ValidFor);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new ArgumentException("Journal drain attestation validity overflows UTC time.", nameof(request), exception);
                }
                var ownerReceipt = new JournalDrainOwnerReceipt(
                    "1.0",
                    "edge.journal.receipt/v1",
                    JournalDrainAttestationCodec.ProducerModule,
                    "windows-edge-worker",
                    state.Terminal.SoulId,
                    state.Terminal.DeviceBindingId,
                    state.Terminal.PlatformAccountId,
                    state.Terminal.TraceId,
                    state.Terminal.IdempotencyKey,
                    state.Terminal.OccurredAt,
                    state.Terminal.PrivacyClass,
                    state.Terminal.CommandId,
                    state.Terminal.EntryId,
                    state.Terminal.EntryType,
                    state.Terminal.PayloadSha256,
                    state.Terminal.Sequence,
                    state.Terminal.PreviousChecksum,
                    state.Terminal.EntryChecksum,
                    true,
                    false);
                var placeholder = new string('0', 64);
                var placeholderSignature = Convert.ToBase64String(new byte[256]);
                var unsigned = new JournalDrainAttestation(
                    JournalDrainAttestationCodec.SchemaVersion,
                    JournalDrainAttestationCodec.ContractId,
                    JournalDrainAttestationCodec.ProducerModule,
                    "windows-edge-supervisor",
                    state.Terminal.SoulId,
                    state.Terminal.DeviceBindingId,
                    state.Terminal.PlatformAccountId,
                    state.Terminal.TraceId,
                    state.Terminal.IdempotencyKey,
                    state.Terminal.OccurredAt,
                    state.Terminal.PrivacyClass,
                    request.RequestId,
                    state.Terminal.CommandId,
                    state.Terminal.CommandId,
                    state.Terminal.EntryId,
                    state.Terminal.EntryType,
                    state.Terminal.Sequence,
                    state.Terminal.EntryChecksum,
                    state.Terminal.PayloadSha256,
                    ComputeJournalId(),
                    stableFiles.JournalFileSha256,
                    stableFiles.JournalFileIdentitySha256,
                    state.HeadSequence,
                    state.HeadChecksum,
                    JournalChecksumEncoding.Name,
                    state.RangeStartSequence,
                    state.RangeEndSequence,
                    state.RangeEntryCount,
                    state.EntrySetSha256,
                    stableFiles.QuarantineState,
                    stableFiles.RecoveryState,
                    stableFiles.StateArtifactSetSha256,
                    request.WorkerArtifactSha256,
                    request.WorkerVersion,
                    request.WorkerSlot,
                    request.JournalArtifactSha256,
                    request.ReleaseBomSha256,
                    request.ProtectedPolicySha256,
                    request.RoutingEpoch,
                    request.IntakeStopped,
                    request.WorkerDrained,
                    request.RemainingInFlight,
                    request.WorkerReceiptWireSha256,
                    placeholder,
                    ownerReceipt,
                    issuedAt.ToString("O", CultureInfo.InvariantCulture),
                    expiresAt.ToString("O", CultureInfo.InvariantCulture),
                    JournalDrainAttestationCodec.Canonicalization,
                    authority.KeyId,
                    JournalDrainAttestationCodec.SignatureAlgorithm,
                    placeholder,
                    placeholderSignature);

                var journalReceiptSha256 = Convert.ToHexString(SHA256.HashData(
                    JournalDrainAttestationCodec.EncodeOwnerReceiptUnchecked(ownerReceipt))).ToLowerInvariant();
                unsigned = unsigned with { JournalReceiptSha256 = journalReceiptSha256 };
                var statement = JournalDrainAttestationCodec.EncodeStatement(unsigned);
                var statementSha256 = Convert.ToHexString(SHA256.HashData(statement)).ToLowerInvariant();
                var signature = authority.Sign(statement);
                var result = unsigned with { StatementSha256 = statementSha256, Signature = signature };

                finalizationGate = await AcquireAppendIntentGateAsync(CancellationToken.None).ConfigureAwait(false);
                finalizationGate.AssertStillBound();
                EnsureNoPendingAppend();
                EnsureNoAppendIntentArtifacts();
                var afterSignature = await CaptureDurableAttestationFilesAsync(
                    journalSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
                if (stableFiles != afterSignature)
                {
                    throw new JournalAttestationStateChangedException(
                        "Journal descriptor, length, content, or recovery state changed while the drain attestation was being signed.");
                }
                await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
                var confirmedFiles = await CaptureDurableAttestationFilesAsync(
                    journalSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
                var confirmedState = BuildDrainAttestationState(request, confirmedFiles);
                if (confirmedFiles != stableFiles || !DrainAttestationStatesEqual(confirmedState, state))
                {
                    throw new JournalAttestationStateChangedException(
                        "Journal authoritative head or selected drain range changed before the attestation could be returned.");
                }
                if (authority.TimeProvider.GetUtcNow().ToUniversalTime() >= expiresAt)
                {
                    throw new JournalAttestationStateChangedException(
                        "Journal drain attestation expired before issuance completed.");
                }

                if (!authority.Verify(statement, result.Signature))
                {
                    throw new CryptographicException("Journal drain attestation authority produced an invalid signature.");
                }
                _ = JournalDrainAttestationCodec.Serialize(result);
                writerLease.AssertStillBound();
                finalizationGate.AssertStillBound();
                await writerLease.DisposeAsync().ConfigureAwait(false);
                writerLease = null;
                finalizationGate.AssertStillBound();
                return result;
            }
            finally
            {
                if (writerLease is not null)
                {
                    await writerLease.DisposeAsync().ConfigureAwait(false);
                }
                if (finalizationGate is not null)
                {
                    await finalizationGate.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _appendLock.Release();
        }
    }

    private void EnsureNoPendingAppend()
    {
        if (Volatile.Read(ref _coordination.PendingAppends) != 0)
        {
            throw new JournalAttestationStateChangedException(
                "A concurrent append is pending; Journal drain attestation issuance fails closed.");
        }
    }

    private DrainAttestationState BuildDrainAttestationState(
        JournalDrainAttestationRequest request,
        DurableAttestationFiles files)
    {
        if (!_byEntryId.TryGetValue(request.EntryId, out var terminal) ||
            terminal.CommandId != request.CommandId || terminal.EntryType != "WORKER_DRAINED" ||
            terminal.ProducerModule != "windows-edge-worker" || terminal.PrivacyClass != "internal")
        {
            throw new InvalidDataException(
                "Journal drain attestation requires the exact durable windows-edge-worker WORKER_DRAINED entry.");
        }
        var expectedEntryId = "worker-drain-" + terminal.CommandId["drain-".Length..];
        if (terminal.EntryId != expectedEntryId)
        {
            throw new InvalidDataException("The durable WORKER_DRAINED entry_id is not canonical for its drain_id.");
        }

        var commandEntries = _byEntryId.Values
            .Where(line => line.CommandId == terminal.CommandId)
            .OrderBy(line => line.Sequence)
            .ToArray();
        if (commandEntries.Length == 0 || commandEntries[^1].EntryId != terminal.EntryId)
        {
            throw new InvalidDataException("The selected WORKER_DRAINED entry is not the final entry for its command.");
        }
        if (commandEntries.Any(line =>
                line.SoulId != terminal.SoulId ||
                line.DeviceBindingId != terminal.DeviceBindingId ||
                line.PlatformAccountId != terminal.PlatformAccountId ||
                line.TraceId != terminal.TraceId ||
                line.IdempotencyKey != terminal.IdempotencyKey ||
                line.PrivacyClass != terminal.PrivacyClass))
        {
            throw new JournalCorruptionException(
                "One command_id spans multiple identity or request scopes; drain attestation is ambiguous.");
        }

        var digestFields = new List<string>
        {
            terminal.SoulId,
            terminal.DeviceBindingId,
            terminal.PlatformAccountId,
            terminal.TraceId,
            terminal.IdempotencyKey,
            terminal.CommandId,
            commandEntries.Length.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var line in commandEntries)
        {
            digestFields.Add(line.Sequence.ToString(CultureInfo.InvariantCulture));
            digestFields.Add(line.ProducerModule);
            digestFields.Add(line.EntryId);
            digestFields.Add(line.EntryType);
            digestFields.Add(line.OccurredAt);
            digestFields.Add(line.PayloadSha256);
            digestFields.Add(line.IdentitySha256);
            digestFields.Add(line.EntryChecksum);
        }

        return new DrainAttestationState(
            terminal,
            _lastSequence,
            _lastChecksum,
            commandEntries[0].Sequence,
            commandEntries[^1].Sequence,
            commandEntries.LongLength,
            JournalChecksumEncoding.ComputeSha256(DrainEntrySetDomain, digestFields.ToArray()),
            files.JournalFileSha256,
            files.StateArtifactSetSha256);
    }

    private static void ValidateWorkerDrainPayload(
        JournalLine terminal,
        JournalDrainAttestationRequest request)
    {
        using var document = JsonDocument.Parse(terminal.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("WORKER_DRAINED payload must be a JSON object.");
        }
        var expected = new HashSet<string>(
            new[]
            {
                "schema_version", "drain_id", "slot", "worker_version",
                "worker_artifact_sha256", "journal_artifact_sha256", "release_bom_sha256",
                "protected_policy_sha256", "routing_epoch", "intake_stopped", "worker_drained",
                "remaining_in_flight", "worker_receipt_wire_sha256"
            },
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException("WORKER_DRAINED payload contains an unknown or duplicate field.");
            }
        }
        if (!seen.SetEquals(expected))
        {
            throw new InvalidDataException("WORKER_DRAINED payload is missing a required field.");
        }

        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetString() != "1.0" ||
            root.GetProperty("drain_id").GetString() != request.CommandId ||
            root.GetProperty("slot").GetString() != request.WorkerSlot ||
            root.GetProperty("worker_version").GetString() != request.WorkerVersion ||
            root.GetProperty("worker_artifact_sha256").GetString() != request.WorkerArtifactSha256 ||
            root.GetProperty("journal_artifact_sha256").GetString() != request.JournalArtifactSha256 ||
            root.GetProperty("release_bom_sha256").GetString() != request.ReleaseBomSha256 ||
            root.GetProperty("protected_policy_sha256").GetString() != request.ProtectedPolicySha256 ||
            !root.GetProperty("routing_epoch").TryGetInt64(out var routingEpoch) || routingEpoch != request.RoutingEpoch ||
            root.GetProperty("intake_stopped").ValueKind is not JsonValueKind.True || !request.IntakeStopped ||
            root.GetProperty("worker_drained").ValueKind is not JsonValueKind.True || !request.WorkerDrained ||
            !root.GetProperty("remaining_in_flight").TryGetInt32(out var remaining) || remaining != 0 ||
            remaining != request.RemainingInFlight ||
            root.GetProperty("worker_receipt_wire_sha256").GetString() != request.WorkerReceiptWireSha256)
        {
            throw new InvalidDataException(
                "WORKER_DRAINED payload does not exactly bind the persisted Worker receipt wire and active deployment.");
        }
    }

    private string ComputeJournalId()
    {
        var normalizedPath = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
        return "journal_" + JournalChecksumEncoding.ComputeSha256(JournalIdDomain, normalizedPath);
    }

    private async Task<DurableAttestationFiles> CaptureDurableAttestationFilesAsync(
        JournalReadSnapshot journalSnapshot,
        CancellationToken cancellationToken)
    {
        var journalFile = await journalSnapshot.HashAsync(
            MaximumJournalFileBytes,
            cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(_path)!;
        var fileName = Path.GetFileName(_path);
        var statePaths = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(_quarantinePath))
        {
            statePaths.Add(_quarantinePath);
        }
        foreach (var path in Directory.EnumerateFiles(directory, fileName + ".released-quarantine.*.json"))
        {
            statePaths.Add(path);
        }
        foreach (var path in Directory.EnumerateFiles(directory, fileName + ".*.crash-tail"))
        {
            statePaths.Add(path);
        }
        foreach (var path in Directory.EnumerateFiles(directory, fileName + ".append-intent.*.json"))
        {
            statePaths.Add(path);
        }
        if (statePaths.Count > MaximumStateArtifactCount)
        {
            throw new JournalCorruptionException("Journal state has too many recovery artifacts to attest safely.");
        }

        var artifacts = new List<StateArtifact>(statePaths.Count);
        long totalBytes = 0;
        foreach (var path in statePaths.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var artifact = await HashFileAsync(
                path,
                MaximumJournalFileBytes,
                allowMissing: false,
                cancellationToken).ConfigureAwait(false);
            totalBytes = checked(totalBytes + artifact.Length);
            if (totalBytes > MaximumStateArtifactBytes)
            {
                throw new JournalCorruptionException("Journal recovery artifacts exceed the attestation byte budget.");
            }
            artifacts.Add(new StateArtifact(Path.GetFileName(path), artifact.Length, artifact.Sha256));
        }

        var fields = new List<string> { artifacts.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var artifact in artifacts)
        {
            fields.Add(artifact.Name);
            fields.Add(artifact.Length.ToString(CultureInfo.InvariantCulture));
            fields.Add(artifact.Sha256);
        }
        var stateDigest = JournalChecksumEncoding.ComputeSha256(StateArtifactSetDomain, fields.ToArray());
        var hasReleasedQuarantine = artifacts.Any(item =>
            item.Name.StartsWith(fileName + ".released-quarantine.", StringComparison.Ordinal) &&
            item.Name.EndsWith(".json", StringComparison.Ordinal));
        var hasCrashTail = artifacts.Any(item => item.Name.EndsWith(".crash-tail", StringComparison.Ordinal));
        return new DurableAttestationFiles(
            journalFile.Length,
            journalFile.Sha256,
            journalFile.IdentitySha256,
            File.Exists(_quarantinePath) ? "QUARANTINED" : hasReleasedQuarantine ? "RELEASED" : "CLEAR",
            hasCrashTail ? "CRASH_TAIL_ISOLATED" : "CLEAN",
            stateDigest);
    }

    private static async Task<FileHash> HashFileAsync(
        string path,
        long maximumBytes,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            if (!allowMissing)
            {
                throw new JournalAttestationStateChangedException("A Journal state artifact disappeared during attestation.");
            }
            return new FileHash(
                0,
                Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(),
                JournalChecksumEncoding.ComputeSha256(FileIdentityDomain, "missing", "0", "0"));
        }

        EnsurePathIsNotLinkOrDirectory(path, allowMissing: false);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        var identity = ReadOpenFileIdentity(stream.SafeFileHandle);
        EnsurePathStillNamesOpenFile(path, stream.SafeFileHandle, identity);
        var length = stream.Length;
        if (length < 0 || length > maximumBytes)
        {
            throw new JournalCorruptionException("Journal state file exceeds the attestation byte limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            if (stream.Length != length)
            {
                throw new JournalAttestationStateChangedException("Journal state file changed while it was being hashed.");
            }
            EnsurePathStillNamesOpenFile(path, stream.SafeFileHandle, identity);
            return new FileHash(
                length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                HashFileIdentity(identity));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void ValidateDrainAttestationRequest(JournalDrainAttestationRequest request)
    {
        if (!IsPrefixedLowerHex(request.RequestId, "drainreq_", 64) ||
            request.CommandId is null || !request.CommandId.StartsWith("drain-", StringComparison.Ordinal) ||
            !IsLowerHex(request.CommandId.AsSpan(6).ToString(), 64) ||
            !IsCanonicalJournalId(request.CommandId, 128) ||
            !IsCanonicalJournalId(request.EntryId, 160) ||
            !IsLowerHex(request.WorkerArtifactSha256, 64) ||
            !IsLowerHex(request.JournalArtifactSha256, 64) ||
            !IsLowerHex(request.ReleaseBomSha256, 64) ||
            !IsLowerHex(request.ProtectedPolicySha256, 64) ||
            !IsLowerHex(request.WorkerReceiptWireSha256, 64) ||
            request.WorkerSlot is not ("A" or "B") ||
            !IsAsciiVersion(request.WorkerVersion) ||
            request.RoutingEpoch < 0 || !request.IntakeStopped || !request.WorkerDrained ||
            request.RemainingInFlight != 0 || request.ValidFor <= TimeSpan.Zero ||
            request.ValidFor > JournalDrainAttestationCodec.MaximumValidity)
        {
            throw new ArgumentException("Journal drain attestation request is invalid.", nameof(request));
        }
    }

    private static bool IsAsciiVersion(string value) =>
        value is not null && value.Length is >= 1 and <= 64 && IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-');

    private static bool DrainAttestationStatesEqual(
        DrainAttestationState left,
        DrainAttestationState right) =>
        left.HeadSequence == right.HeadSequence &&
        left.HeadChecksum == right.HeadChecksum &&
        left.RangeStartSequence == right.RangeStartSequence &&
        left.RangeEndSequence == right.RangeEndSequence &&
        left.RangeEntryCount == right.RangeEntryCount &&
        left.EntrySetSha256 == right.EntrySetSha256 &&
        left.JournalFileSha256 == right.JournalFileSha256 &&
        left.StateArtifactSetSha256 == right.StateArtifactSetSha256 &&
        left.Terminal.SchemaVersion == right.Terminal.SchemaVersion &&
        left.Terminal.ContractId == right.Terminal.ContractId &&
        left.Terminal.ProducerModule == right.Terminal.ProducerModule &&
        left.Terminal.CommandId == right.Terminal.CommandId &&
        left.Terminal.EntryId == right.Terminal.EntryId &&
        left.Terminal.EntryType == right.Terminal.EntryType &&
        left.Terminal.TraceId == right.Terminal.TraceId &&
        left.Terminal.IdempotencyKey == right.Terminal.IdempotencyKey &&
        left.Terminal.PrivacyClass == right.Terminal.PrivacyClass &&
        left.Terminal.SoulId == right.Terminal.SoulId &&
        left.Terminal.DeviceBindingId == right.Terminal.DeviceBindingId &&
        left.Terminal.PlatformAccountId == right.Terminal.PlatformAccountId &&
        left.Terminal.OccurredAt == right.Terminal.OccurredAt &&
        left.Terminal.Sequence == right.Terminal.Sequence &&
        left.Terminal.PayloadSha256 == right.Terminal.PayloadSha256 &&
        left.Terminal.IdentitySha256 == right.Terminal.IdentitySha256 &&
        left.Terminal.EntryChecksum == right.Terminal.EntryChecksum;

    private sealed record FileHash(long Length, string Sha256, string IdentitySha256);

    private sealed record StateArtifact(string Name, long Length, string Sha256);

    private sealed record DurableAttestationFiles(
        long JournalFileLength,
        string JournalFileSha256,
        string JournalFileIdentitySha256,
        string QuarantineState,
        string RecoveryState,
        string StateArtifactSetSha256);

    private sealed record DrainAttestationState(
        JournalLine Terminal,
        long HeadSequence,
        string HeadChecksum,
        long RangeStartSequence,
        long RangeEndSequence,
        long RangeEntryCount,
        string EntrySetSha256,
        string JournalFileSha256,
        string StateArtifactSetSha256);
}
#endif

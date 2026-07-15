using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.MemoryEventLedger.Contracts;

public sealed record InterestSignalV2(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("evidence_sha256")] string EvidenceSha256)
{
    public void Validate()
    {
        MemoryContractValidationV2.RequireBoundedText(Topic, 128, 512, nameof(Topic));
        if (Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Confidence), "Confidence must be finite and between zero and one.");
        }

        MemoryContractValidationV2.RequireSha256(EvidenceSha256, nameof(EvidenceSha256));
    }
}

public sealed record MemoryObservationV2(
    [property: JsonPropertyName("content_digest")] string ContentDigest,
    [property: JsonPropertyName("signals_digest")] string SignalsDigest,
    [property: JsonPropertyName("signed_receipt_sha256")] string SignedReceiptSha256,
    [property: JsonPropertyName("receipt_id")] Guid ReceiptId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("interest_signals")] IReadOnlyList<InterestSignalV2> InterestSignals)
{
    public const int MaximumInterestSignals = 32;

    public void Validate()
    {
        MemoryContractValidationV2.RequireSha256(ContentDigest, nameof(ContentDigest));
        MemoryContractValidationV2.RequireSha256(SignalsDigest, nameof(SignalsDigest));
        MemoryContractValidationV2.RequireSha256(SignedReceiptSha256, nameof(SignedReceiptSha256));
        MemoryContractValidationV2.RequireNonEmpty(ReceiptId, nameof(ReceiptId));
        MemoryContractValidationV2.RequireNonEmpty(CommandId, nameof(CommandId));
        ArgumentNullException.ThrowIfNull(InterestSignals);
        if (InterestSignals.Count > MaximumInterestSignals)
        {
            throw new ArgumentOutOfRangeException(nameof(InterestSignals), $"At most {MaximumInterestSignals} interest signals are accepted.");
        }

        var topics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signal in InterestSignals)
        {
            ArgumentNullException.ThrowIfNull(signal);
            signal.Validate();
            if (!topics.Add(signal.Topic))
            {
                throw new InvalidOperationException("Interest signal topics must be unique.");
            }
        }

        var expectedSignalsDigest = MemorySignalCanonicalizerV2.ComputeSha256(InterestSignals);
        if (!MemoryContractValidationV2.FixedTimeEquals(SignalsDigest, expectedSignalsDigest))
        {
            throw new UnauthorizedAccessException("signals_digest does not bind the exact canonical interest signal set.");
        }
    }
}

public sealed record IdentityAuthorityAuditV2(
    [property: JsonPropertyName("resolution_sha256")] string ResolutionSha256,
    [property: JsonPropertyName("resolution_revision")] long ResolutionRevision,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("key_role")] string KeyRole,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("trust_epoch")] long TrustEpoch,
    [property: JsonPropertyName("revocation_epoch")] long RevocationEpoch,
    [property: JsonPropertyName("issued_at"), JsonConverter(typeof(MemoryCanonicalUtcJsonConverter))] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at"), JsonConverter(typeof(MemoryCanonicalUtcJsonConverter))] DateTimeOffset ExpiresAt)
{
    public const string CurrentIssuer = "soul-registry";
    public const string CurrentAudience = "memory-event-ledger";
    public const string CurrentKeyRole = "soul-resolution-current";

    public void Validate()
    {
        MemoryContractValidationV2.RequireSha256(ResolutionSha256, nameof(ResolutionSha256));
        if (ResolutionRevision < 1) throw new ArgumentOutOfRangeException(nameof(ResolutionRevision));
        MemoryContractValidationV2.RequireExact(Issuer, CurrentIssuer, nameof(Issuer));
        MemoryContractValidationV2.RequireExact(Audience, CurrentAudience, nameof(Audience));
        MemoryContractValidationV2.RequireExact(KeyRole, CurrentKeyRole, nameof(KeyRole));
        MemoryContractValidationV2.RequireKeyId(KeyId, nameof(KeyId));
        if (TrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(TrustEpoch));
        if (RevocationEpoch < 0) throw new ArgumentOutOfRangeException(nameof(RevocationEpoch));
        MemoryContractValidationV2.RequireUtc(IssuedAt, nameof(IssuedAt));
        MemoryContractValidationV2.RequireUtc(ExpiresAt, nameof(ExpiresAt));
        if (ExpiresAt <= IssuedAt) throw new ArgumentException("Identity authority expiry must be after issuance.", nameof(ExpiresAt));
    }
}

public sealed record ResultAuthorityAuditV2(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("key_role")] string KeyRole,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("trust_epoch")] long TrustEpoch,
    [property: JsonPropertyName("revocation_epoch")] long RevocationEpoch,
    [property: JsonPropertyName("issued_at"), JsonConverter(typeof(MemoryCanonicalUtcJsonConverter))] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at"), JsonConverter(typeof(MemoryCanonicalUtcJsonConverter))] DateTimeOffset ExpiresAt)
{
    public const string CurrentIssuer = "executor-gateway";
    public const string CurrentAudience = "memory-event-ledger";
    public const string CurrentKeyRole = "verified-observation-receipt";

    public void Validate()
    {
        MemoryContractValidationV2.RequireExact(Issuer, CurrentIssuer, nameof(Issuer));
        MemoryContractValidationV2.RequireExact(Audience, CurrentAudience, nameof(Audience));
        MemoryContractValidationV2.RequireExact(KeyRole, CurrentKeyRole, nameof(KeyRole));
        MemoryContractValidationV2.RequireKeyId(KeyId, nameof(KeyId));
        if (TrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(TrustEpoch));
        if (RevocationEpoch < 0) throw new ArgumentOutOfRangeException(nameof(RevocationEpoch));
        MemoryContractValidationV2.RequireUtc(IssuedAt, nameof(IssuedAt));
        MemoryContractValidationV2.RequireUtc(ExpiresAt, nameof(ExpiresAt));
        if (ExpiresAt <= IssuedAt) throw new ArgumentException("Result authority expiry must be after issuance.", nameof(ExpiresAt));
    }
}

public sealed record MemoryEventV2(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(MemoryCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("observation")] MemoryObservationV2 Observation,
    [property: JsonPropertyName("identity_authority")] IdentityAuthorityAuditV2 IdentityAuthority,
    [property: JsonPropertyName("result_authority")] ResultAuthorityAuditV2 ResultAuthority)
{
    public const string CurrentSchemaVersion = "2.0.0";
    public const string CurrentContractId = "memory.event/v2";
    public const string CurrentProducerModule = "memory-event-ledger";
    public const string ObservedContentEventType = "content.observed";

    public void Validate()
    {
        MemoryContractValidationV2.RequireMajor(SchemaVersion, 2);
        MemoryContractValidationV2.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        MemoryContractValidationV2.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        MemoryContractValidationV2.RequireNonEmpty(EventId, nameof(EventId));
        MemoryContractValidationV2.RequireSoulId(SoulId, nameof(SoulId));
        MemoryContractValidationV2.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        MemoryContractValidationV2.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        MemoryContractValidationV2.RequireTraceId(TraceId, nameof(TraceId));
        MemoryContractValidationV2.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        MemoryContractValidationV2.RequireUtc(OccurredAt, nameof(OccurredAt));
        MemoryContractValidationV2.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        MemoryContractValidationV2.RequireExact(EventType, ObservedContentEventType, nameof(EventType));
        ArgumentNullException.ThrowIfNull(Observation);
        Observation.Validate();
        if (EventId != Observation.CommandId)
            throw new UnauthorizedAccessException("event_id must equal the signed command_id for memory.event/v2.");
        ArgumentNullException.ThrowIfNull(IdentityAuthority);
        IdentityAuthority.Validate();
        ArgumentNullException.ThrowIfNull(ResultAuthority);
        ResultAuthority.Validate();
    }
}

public static class MemorySignalCanonicalizerV2
{
    private const string Domain = "dps.memory-event-ledger.interest-signals/v2";

    public static string ComputeSha256(IReadOnlyList<InterestSignalV2> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (signals.Count > MemoryObservationV2.MaximumInterestSignals)
            throw new ArgumentOutOfRangeException(nameof(signals));

        using var stream = new MemoryStream();
        WriteToken(stream, Domain);
        foreach (var signal in signals.OrderBy(static item => item.Topic, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(signal);
            signal.Validate();
            WriteToken(stream, signal.Topic);
            WriteToken(stream, signal.Confidence.ToString("0.############################", CultureInfo.InvariantCulture));
            WriteToken(stream, signal.EvidenceSha256);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteToken(Stream stream, string value)
    {
        var bytes = MemoryContractValidationV2.StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public static class MemoryEventCanonicalizerV2
{
    public const int MaximumCanonicalBytes = 65_536;
    private static readonly JsonSerializerOptions StrictSerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public static string Serialize(MemoryEventV2 memoryEvent)
    {
        ArgumentNullException.ThrowIfNull(memoryEvent);
        memoryEvent.Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", memoryEvent.SchemaVersion);
            writer.WriteString("contract_id", memoryEvent.ContractId);
            writer.WriteString("producer_module", memoryEvent.ProducerModule);
            writer.WriteString("event_id", memoryEvent.EventId);
            writer.WriteString("soul_id", memoryEvent.SoulId);
            writer.WriteString("device_binding_id", memoryEvent.DeviceBindingId);
            writer.WriteString("platform_account_id", memoryEvent.PlatformAccountId);
            writer.WriteString("trace_id", memoryEvent.TraceId);
            writer.WriteString("idempotency_key", memoryEvent.IdempotencyKey);
            writer.WriteString("occurred_at", MemoryCanonicalUtcJsonConverter.Format(memoryEvent.OccurredAt));
            writer.WriteString("privacy_class", memoryEvent.PrivacyClass);
            writer.WriteString("event_type", memoryEvent.EventType);
            writer.WritePropertyName("observation");
            writer.WriteStartObject();
            writer.WriteString("content_digest", memoryEvent.Observation.ContentDigest);
            writer.WriteString("signals_digest", memoryEvent.Observation.SignalsDigest);
            writer.WriteString("signed_receipt_sha256", memoryEvent.Observation.SignedReceiptSha256);
            writer.WriteString("receipt_id", memoryEvent.Observation.ReceiptId);
            writer.WriteString("command_id", memoryEvent.Observation.CommandId);
            writer.WritePropertyName("interest_signals");
            writer.WriteStartArray();
            foreach (var signal in memoryEvent.Observation.InterestSignals.OrderBy(static item => item.Topic, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("topic", signal.Topic);
                writer.WriteNumber("confidence", signal.Confidence);
                writer.WriteString("evidence_sha256", signal.EvidenceSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            WriteIdentityAuthority(writer, memoryEvent.IdentityAuthority);
            WriteResultAuthority(writer, memoryEvent.ResultAuthority);
            writer.WriteEndObject();
        }

        if (stream.Length > MaximumCanonicalBytes)
            throw new ArgumentOutOfRangeException(nameof(memoryEvent), $"Canonical event exceeds {MaximumCanonicalBytes} bytes.");
        return MemoryContractValidationV2.StrictUtf8.GetString(stream.ToArray());
    }

    public static string ComputeSha256(MemoryEventV2 memoryEvent) =>
        Convert.ToHexStringLower(SHA256.HashData(MemoryContractValidationV2.StrictUtf8.GetBytes(Serialize(memoryEvent))));

    public static MemoryEventV2 DeserializeExact(ReadOnlySpan<byte> raw)
    {
        if (raw.Length is 0 or > MaximumCanonicalBytes)
            throw new JsonException("MemoryEvent v2 raw bytes are empty or exceed the bounded size.");
        MemoryContractValidationV2.ValidateJsonShape(raw, maximumDepth: 12, maximumPropertyCount: 128);
        var parsed = JsonSerializer.Deserialize<MemoryEventV2>(raw, StrictSerializerOptions)
            ?? throw new JsonException("MemoryEvent v2 is null.");
        var canonical = MemoryContractValidationV2.StrictUtf8.GetBytes(Serialize(parsed));
        try
        {
            if (!raw.SequenceEqual(canonical))
                throw new JsonException("MemoryEvent v2 bytes are not the exact canonical encoding.");
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
        return parsed;
    }

    private static void WriteIdentityAuthority(Utf8JsonWriter writer, IdentityAuthorityAuditV2 value)
    {
        writer.WritePropertyName("identity_authority"); writer.WriteStartObject();
        writer.WriteString("resolution_sha256", value.ResolutionSha256);
        writer.WriteNumber("resolution_revision", value.ResolutionRevision);
        writer.WriteString("issuer", value.Issuer); writer.WriteString("audience", value.Audience);
        writer.WriteString("key_role", value.KeyRole); writer.WriteString("key_id", value.KeyId);
        writer.WriteNumber("trust_epoch", value.TrustEpoch); writer.WriteNumber("revocation_epoch", value.RevocationEpoch);
        writer.WriteString("issued_at", MemoryCanonicalUtcJsonConverter.Format(value.IssuedAt));
        writer.WriteString("expires_at", MemoryCanonicalUtcJsonConverter.Format(value.ExpiresAt)); writer.WriteEndObject();
    }

    private static void WriteResultAuthority(Utf8JsonWriter writer, ResultAuthorityAuditV2 value)
    {
        writer.WritePropertyName("result_authority"); writer.WriteStartObject();
        writer.WriteString("issuer", value.Issuer); writer.WriteString("audience", value.Audience);
        writer.WriteString("key_role", value.KeyRole); writer.WriteString("key_id", value.KeyId);
        writer.WriteNumber("trust_epoch", value.TrustEpoch); writer.WriteNumber("revocation_epoch", value.RevocationEpoch);
        writer.WriteString("issued_at", MemoryCanonicalUtcJsonConverter.Format(value.IssuedAt));
        writer.WriteString("expires_at", MemoryCanonicalUtcJsonConverter.Format(value.ExpiresAt)); writer.WriteEndObject();
    }
}

public static class MemoryContractValidationV2
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void RequireMajor(string value, int expected)
    {
        RequireBoundedText(value, 32, 32, nameof(value));
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3 || parts[0] != expected.ToString(CultureInfo.InvariantCulture) ||
            parts.Any(static part => part.Length == 0 || part.Any(static character => character is < '0' or > '9')))
            throw new NotSupportedException($"Unsupported schema major '{value}'.");
    }

    public static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new NotSupportedException($"Unsupported {name} '{actual}'.");
    }

    public static void RequireNonEmpty(Guid value, string name)
    {
        if (value == Guid.Empty) throw new ArgumentException($"{name} cannot be empty.", name);
    }

    public static void RequireSoulId(string value, string name) => RequirePrefixedLowerHex(value, "soul_", 64, name);
    public static void RequireOpaqueId(string value, string prefix, string name) => RequirePrefixedLowerHex(value, prefix, 32, name);
    public static void RequireTraceId(string value, string name) => RequirePrefixedLowerHex(value, "trace_", 32, name);
    public static void RequireIdempotencyKey(string value, string name) => RequirePrefixedLowerHex(value, "idem_", 64, name);

    public static void RequireSha256(string value, string name)
    {
        if (value is null || value.Length != 64 || value.AsSpan().ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException($"{name} must be lowercase SHA-256.", name);
    }

    public static void RequireKeyId(string value, string name)
    {
        RequireBoundedText(value, 64, 64, name);
        if (!char.IsAsciiLetterOrDigit(value[0]) || value.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException($"{name} is not a canonical key id.", name);
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name);
    }

    public static void RequireBoundedText(string value, int maxCharacters, int maxUtf8Bytes, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxCharacters || !value.IsNormalized(NormalizationForm.FormC) ||
            value.Any(static character => char.IsControl(character)) || StrictUtf8.GetByteCount(value) > maxUtf8Bytes)
            throw new ArgumentException($"{name} is empty, non-canonical, contains control characters, or exceeds its bound.", name);
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left); var rightBytes = Convert.FromHexString(right);
            try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
            finally { CryptographicOperations.ZeroMemory(leftBytes); CryptographicOperations.ZeroMemory(rightBytes); }
        }
        catch (FormatException) { return false; }
    }

    public static void ValidateJsonShape(ReadOnlySpan<byte> raw, int maximumDepth, int maximumPropertyCount)
    {
        var options = new JsonReaderOptions { MaxDepth = maximumDepth, CommentHandling = JsonCommentHandling.Disallow };
        var reader = new Utf8JsonReader(raw, options);
        var propertyCount = 0;
        var namesByDepth = new Dictionary<int, HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (++propertyCount > maximumPropertyCount) throw new JsonException("JSON property count exceeds the bounded maximum.");
                var name = reader.GetString() ?? throw new JsonException("JSON property name is null.");
                if (!namesByDepth.TryGetValue(reader.CurrentDepth, out var names)) namesByDepth[reader.CurrentDepth] = names = new(StringComparer.Ordinal);
                if (!names.Add(name)) throw new JsonException("Duplicate JSON property names are forbidden.");
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                namesByDepth[reader.CurrentDepth + 1] = new(StringComparer.Ordinal);
            }
            else if (reader.TokenType == JsonTokenType.Number && !reader.TryGetDecimal(out _))
            {
                throw new JsonException("Non-finite or out-of-range JSON numbers are forbidden.");
            }
        }
    }

    private static void RequirePrefixedLowerHex(string value, string prefix, int bodyLength, string name)
    {
        if (value is null || value.Length != prefix.Length + bodyLength || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException($"{name} is not a canonical opaque identifier.", name);
    }
}

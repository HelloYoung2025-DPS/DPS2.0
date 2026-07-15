using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.PlatformAccountRegistry.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformAccountAuthorizedV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("platform"), JsonRequired] string Platform,
    [property: JsonPropertyName("alias_digest"), JsonRequired] string AliasDigest,
    [property: JsonPropertyName("alias_key_id"), JsonRequired] string AliasKeyId,
    [property: JsonPropertyName("authorization_evidence_id"), JsonRequired] string AuthorizationEvidenceId,
    [property: JsonPropertyName("authorization_revision"), JsonRequired] long AuthorizationRevision,
    [property: JsonPropertyName("status"), JsonRequired] string Status,
    [property: JsonPropertyName("alias_key_epoch"), JsonRequired] long AliasKeyEpoch)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "platform.account.authorized/v1";
    public const string CurrentProducerModule = "platform-account-registry";

    public void Validate()
    {
        AccountContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        AccountContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        AccountContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        AccountContractValidation.RequireSoulId(SoulId);
        AccountContractValidation.RequireDeviceBindingId(DeviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(PlatformAccountId);
        AccountContractValidation.RequireTraceId(TraceId);
        AccountContractValidation.RequireIdempotencyKey(IdempotencyKey);
        AccountContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        AccountContractValidation.RequireExact(PrivacyClass, "sensitive", nameof(PrivacyClass));
        AccountContractValidation.RequireIdentifier(Platform, nameof(Platform));
        AccountContractValidation.RequireSha256(AliasDigest, nameof(AliasDigest));
        AccountContractValidation.RequireKeyId(AliasKeyId, nameof(AliasKeyId));
        if (AliasKeyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(AliasKeyEpoch));
        AccountContractValidation.RequireApprovalId(AuthorizationEvidenceId);
        if (AuthorizationRevision < 1) throw new ArgumentOutOfRangeException(nameof(AuthorizationRevision));
        AccountContractValidation.RequireStatus(Status, nameof(Status));
    }
}

public static class PlatformAccountContractJson
{
    private const int MaximumUtf8Bytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ExactUtcText = new(
        @"^(?!0000-)\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|\+00:00)$(?![\s\S])",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T DeserializeStrict<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var byteCount = StrictUtf8.GetByteCount(json);
        if (byteCount > MaximumUtf8Bytes)
            throw new JsonException($"The contract exceeds the {MaximumUtf8Bytes}-byte limit.");

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        RejectDuplicateProperties(document.RootElement);
        RequireExactUtc(document.RootElement, "occurred_at", nullable: false);
        if (typeof(T) == typeof(PlatformAccountBindingReservationV1))
            RequireExactUtc(document.RootElement, "lease_expires_at", nullable: true);
        var value = JsonSerializer.Deserialize<T>(json, StrictOptions)
            ?? throw new JsonException("The contract payload was null.");
        ValidateKnownContract(value);
        return value;
    }

    private static void RequireExactUtc(JsonElement root, string propertyName, bool nullable)
    {
        if (!root.TryGetProperty(propertyName, out var property)) return;
        if (nullable && property.ValueKind == JsonValueKind.Null) return;
        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } text ||
            !ExactUtcText.IsMatch(text))
        {
            throw new JsonException($"'{propertyName}' must be exact zero-offset UTC with at most seven fractional digits.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException($"Duplicate JSON property '{property.Name}' is forbidden.");
                RejectDuplicateProperties(property.Value);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Array) return;
        foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void ValidateKnownContract<T>(T value)
    {
        switch (value)
        {
            case PlatformAccountAuthorizedV1 authorized:
                authorized.Validate();
                break;
            case PlatformAccountBindingReservationV1 reservation:
                reservation.Validate();
                break;
            default:
                throw new NotSupportedException($"No strict validator is registered for {typeof(T).FullName}.");
        }
    }
}

public static class AccountContractValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported {name} '{actual}'.");
    }

    public static void RequireText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException($"{name} must contain between 1 and {maximum} characters.", name);
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsSurrogate(character))
                throw new ArgumentException($"{name} contains a forbidden control or surrogate character.", name);
        }
        _ = StrictUtf8.GetByteCount(value);
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException($"{name} must be a non-default UTC timestamp.", name);
    }

    public static void RequireSoulId(string value) => RequirePrefixedHex(value, "soul_", 64, nameof(value));
    public static void RequireDeviceBindingId(string value) => RequirePrefixedHex(value, "db_", 32, nameof(value));
    public static void RequirePlatformAccountId(string value) => RequirePrefixedHex(value, "pa_", 32, nameof(value));
    public static void RequireTraceId(string value) => RequirePrefixedHex(value, "trace_", 32, nameof(value));
    public static void RequireIdempotencyKey(string value) => RequirePrefixedHex(value, "idem_", 64, nameof(value));
    public static void RequireSha256(string value, string name) => RequirePrefixedHex(value, string.Empty, 64, name);

    public static void RequireIdentifier(string value, string name)
    {
        RequireText(value, 64, name);
        var previousWasSeparator = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (IsAsciiLower(character) || IsAsciiDigit(character))
            {
                previousWasSeparator = false;
                continue;
            }
            var isSeparator = character is '.' or '_' or '-';
            if (!isSeparator || index == 0 || index == value.Length - 1 || previousWasSeparator)
                throw new ArgumentException($"{name} must be a normalized lowercase ASCII identifier.", name);
            previousWasSeparator = true;
        }
    }

    public static void RequireKeyId(string value, string name)
    {
        RequireText(value, 64, name);
        if (!IsAsciiLowerOrDigit(value[0]) || value.Any(static character =>
                !IsAsciiLowerOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new ArgumentException($"Invalid {name}.", name);
    }

    public static void RequireApprovalId(string value)
    {
        RequireText(value, 128, nameof(value));
        if (!value.StartsWith("approval_", StringComparison.Ordinal) || value.Length <= 9 ||
            value.AsSpan(9).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_-"))
            throw new ArgumentException("Invalid authorization_evidence_id.", nameof(value));
    }

    public static void RequireStatus(string value, string name)
    {
        if (value is not ("authorized" or "revoked" or "suspended"))
            throw new ArgumentOutOfRangeException(name);
    }

    public static void RequirePrefixedHex(string value, string prefix, int hexLength, string name)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + hexLength ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException($"{name} is not a canonical lowercase hexadecimal identifier.", name);
    }

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';
    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
    private static bool IsAsciiLowerOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

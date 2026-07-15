using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.PlatformAuthorizationAuthority.Contracts;

public static class PlatformAuthorizationAuthorityContractJson
{
    private const int MaximumUtf8Bytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ExactUtcTimestamp = new(
        @"^(?!0000)\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|\+00:00)$(?![\s\S])",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static SignedPlatformAuthorizationEvidenceV1 DeserializeEvidenceStrict(string json)
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
        RequireExactUtcTimestamp(document.RootElement, "occurred_at");
        RequireExactUtcTimestamp(document.RootElement, "issued_at");
        RequireExactUtcTimestamp(document.RootElement, "expires_at");
        var evidence = JsonSerializer.Deserialize<SignedPlatformAuthorizationEvidenceV1>(json, StrictOptions)
            ?? throw new JsonException("The contract payload was null.");
        evidence.Validate();
        return evidence;
    }

    public static byte[] SerializeEvidenceStrict(SignedPlatformAuthorizationEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, StrictOptions);
        if (bytes.Length > MaximumUtf8Bytes)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw new JsonException($"The contract exceeds the {MaximumUtf8Bytes}-byte limit.");
        }
        return bytes;
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

    private static void RequireExactUtcTimestamp(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            throw new JsonException($"'{propertyName}' must be a JSON string.");
        var value = property.GetString();
        if (value is null || !ExactUtcTimestamp.IsMatch(value))
            throw new JsonException($"'{propertyName}' must be an exact zero-offset UTC timestamp with at most seven fractional digits.");
    }
}

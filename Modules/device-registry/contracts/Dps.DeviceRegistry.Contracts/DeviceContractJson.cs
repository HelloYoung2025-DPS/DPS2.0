using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.DeviceRegistry.Contracts;

public static class DeviceContractJson
{
    private const int MaximumUtf8Bytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ExactUtcText = new(
        @"^(?!0000-)\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|\+00:00)$(?![\s\S])",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 64,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T DeserializeStrict<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictUtf8.GetByteCount(json) > MaximumUtf8Bytes)
            throw new JsonException($"The contract exceeds the {MaximumUtf8Bytes}-byte limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        RejectDuplicateProperties(document.RootElement);
        RequireExactUtc(document.RootElement, "occurred_at", nullable: false);
        if (typeof(T) == typeof(DeviceBindingReservationV1))
            RequireExactUtc(document.RootElement, "lease_expires_at", nullable: true);
        var value = JsonSerializer.Deserialize<T>(json, StrictOptions)
            ?? throw new JsonException("The contract payload was null.");
        switch (value)
        {
            case DeviceRegisteredV1 registered:
                registered.Validate();
                break;
            case DeviceBindingReservationV1 reservation:
                reservation.Validate();
                break;
            default:
                throw new NotSupportedException($"No strict validator is registered for {typeof(T).FullName}.");
        }
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
}

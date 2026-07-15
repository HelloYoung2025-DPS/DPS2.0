using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.Binding.Contracts;

public static class BindingContractJson
{
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
        BindingContractValidation.RequireText(json, 1_048_576, nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        RejectDuplicateProperties(document.RootElement);
        ValidateExactUtcText<T>(document.RootElement);
        var value = JsonSerializer.Deserialize<T>(json, StrictOptions)
            ?? throw new JsonException($"The {typeof(T).Name} payload is null.");
        ValidateSupported(value);
        return value;
    }

    private static void ValidateExactUtcText<T>(JsonElement root)
    {
        if (typeof(T) == typeof(SignedBindingCompositionAttestationV1))
        {
            RequireExactUtc(root, "occurred_at");
            RequireExactUtc(root, "issued_at");
            RequireExactUtc(root, "expires_at");
            return;
        }
        if (typeof(T) == typeof(IdentityBindingV1) || typeof(T) == typeof(BindingMutationFenceV1))
            RequireExactUtc(root, "occurred_at");
    }

    private static void RequireExactUtc(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)) return;
        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } text ||
            !ExactUtcText.IsMatch(text))
        {
            throw new JsonException($"'{propertyName}' must be exact zero-offset UTC with at most seven fractional digits.");
        }
    }

    private static void ValidateSupported<T>(T value)
    {
        switch (value)
        {
            case IdentityBindingV1 binding:
                binding.Validate();
                return;
            case BindingMutationFenceV1 fence:
                fence.Validate();
                return;
            case SignedBindingCompositionAttestationV1 attestation:
                attestation.ValidateShape();
                return;
            default:
                throw new NotSupportedException($"{typeof(T).FullName} is not a binding public contract type.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException($"Duplicate JSON property '{property.Name}' is forbidden.");
                RejectDuplicateProperties(property.Value);
            }
            return;
        }
        if (value.ValueKind != JsonValueKind.Array) return;
        foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

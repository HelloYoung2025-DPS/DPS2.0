using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dps.PolicyApproval.Contracts;

internal static class PolicyApprovalContractJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] CanonicalUtcFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    internal static void RequirePayload(
        ReadOnlySpan<byte> payloadUtf8,
        int maximumPayloadBytes,
        string contractName)
    {
        if (payloadUtf8.Length is < 2 || payloadUtf8.Length > maximumPayloadBytes)
        {
            throw new ArgumentException(
                $"{contractName} payload is outside its byte budget.",
                nameof(payloadUtf8));
        }
        try
        {
            _ = StrictUtf8.GetCharCount(payloadUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                $"{contractName} payload is not strict UTF-8.",
                nameof(payloadUtf8),
                exception);
        }
    }

    internal static IReadOnlyDictionary<string, JsonElement> ReadExactFields(
        JsonElement root,
        IReadOnlySet<string> allowed,
        IReadOnlySet<string> required,
        string contractName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"{contractName} payload must be one JSON object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new ArgumentException(
                    $"Unknown {contractName} field '{property.Name}'.");
            if (!fields.TryAdd(property.Name, property.Value))
                throw new ArgumentException(
                    $"Duplicate {contractName} field '{property.Name}'.");
        }
        if (required.Any(field => !fields.ContainsKey(field)))
            throw new ArgumentException($"{contractName} payload has missing fields.");
        return fields;
    }

    internal static string ReadString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException(
                $"{contractName} field '{name}' must be a string.");
        return value.GetString()
            ?? throw new ArgumentException($"{contractName} field '{name}' is null.");
    }

    internal static string? ReadNullableString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = fields[name];
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException(
                $"{contractName} field '{name}' must be a string or null.");
        return value.GetString()
            ?? throw new ArgumentException($"{contractName} field '{name}' is invalid.");
    }

    internal static bool ReadBoolean(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = fields[name];
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException(
                $"{contractName} field '{name}' must be a boolean.")
        };
    }

    internal static Guid ReadAbsoluteGuid(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = ReadString(fields, name, contractName);
        if (value.Length != 36
            || !Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{contractName} field '{name}' is not a lowercase non-zero D UUID.");
        }
        return parsed;
    }

    internal static long ReadPositiveInt64(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed < 1)
        {
            throw new ArgumentException(
                $"{contractName} field '{name}' must be a positive Int64.");
        }
        return parsed;
    }

    internal static DateTimeOffset ReadCanonicalUtc(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = ReadString(fields, name, contractName);
        if (!value.EndsWith('Z')
            || !DateTimeOffset.TryParseExact(
                value,
                CanonicalUtcFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || !string.Equals(FormatCanonicalUtc(parsed), value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{contractName} field '{name}' must be canonical Zulu UTC.");
        }
        return parsed;
    }

    internal static IReadOnlyDictionary<string, string> ReadStringMap(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                $"{contractName} field '{name}' must be an object.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!result.TryAdd(
                    property.Name,
                    property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                            ?? throw new ArgumentException(
                                $"{contractName} map '{name}' contains null.")
                        : throw new ArgumentException(
                            $"{contractName} map '{name}' values must be strings.")))
            {
                throw new ArgumentException(
                    $"{contractName} map '{name}' contains duplicate key '{property.Name}'.");
            }
        }
        return result;
    }

    internal static IReadOnlyList<string> ReadStringList(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException(
                $"{contractName} field '{name}' must be an array.");
        var result = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                throw new ArgumentException(
                    $"{contractName} array '{name}' values must be strings.");
            result.Add(element.GetString()
                ?? throw new ArgumentException(
                    $"{contractName} array '{name}' contains null."));
        }
        return result;
    }

    internal static string FormatCanonicalUtc(DateTimeOffset value)
    {
        ApprovalContractGuard.RequireUtc(value, nameof(value));
        return value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }

    internal static void RequireCanonicalWire(
        ReadOnlySpan<byte> payloadUtf8,
        byte[] canonicalPayload,
        string contractName)
    {
        try
        {
            if (!payloadUtf8.SequenceEqual(canonicalPayload))
                throw new ArgumentException(
                    $"{contractName} payload is not the canonical snake_case wire.",
                    nameof(payloadUtf8));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalPayload);
        }
    }
}

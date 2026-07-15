using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Dps.ExecutorGateway.Contracts;

public static class ExecutorGatewayContractJson
{
    private static readonly JsonSerializerOptions StrictOptions = CreateOptions();

    public static string SerializeNativeSubmissionAck(NativeSubmissionAck acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        acknowledgement.Validate();
        return JsonSerializer.Serialize(acknowledgement, StrictOptions);
    }

    public static NativeSubmissionAck DeserializeNativeSubmissionAck(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Native submission acknowledgement JSON is required.", nameof(json));
        var acknowledgement = JsonSerializer.Deserialize<NativeSubmissionAck>(json, StrictOptions)
            ?? throw new JsonException("Native submission acknowledgement JSON is null.");
        acknowledgement.Validate();
        return acknowledgement;
    }

    public static string SerializeNativeStopProof(NativeAbortConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        confirmation.Validate();
        return JsonSerializer.Serialize(confirmation, StrictOptions);
    }

    public static NativeAbortConfirmation DeserializeNativeStopProof(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Native stop proof JSON is required.", nameof(json));
        var confirmation = JsonSerializer.Deserialize<NativeAbortConfirmation>(json, StrictOptions)
            ?? throw new JsonException("Native stop proof JSON is null.");
        confirmation.Validate();
        return confirmation;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new CanonicalNonEmptyGuidConverter());
        options.Converters.Add(new CanonicalUtcDateTimeOffsetConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class CanonicalNonEmptyGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("UUID must be a canonical lowercase string.");
            var raw = reader.GetString();
            if (raw is null || !Guid.TryParseExact(raw, "D", out var value) || value == Guid.Empty ||
                !string.Equals(value.ToString("D"), raw, StringComparison.Ordinal))
                throw new JsonException("UUID must be canonical lowercase D format and non-empty.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        {
            if (value == Guid.Empty) throw new JsonException("UUID cannot be empty.");
            writer.WriteStringValue(value.ToString("D"));
        }
    }

    private sealed class CanonicalUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("occurred_at must be canonical UTC text.");
            var raw = reader.GetString();
            if (raw is null || !DateTimeOffset.TryParseExact(
                    raw,
                    Format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var value) || value.Offset != TimeSpan.Zero)
                throw new JsonException("occurred_at must use canonical UTC yyyy-MM-ddTHH:mm:ss.fffffffZ format.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero) throw new JsonException("occurred_at must be UTC.");
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dps.EdgeLocalJournal;

public static class JournalChecksumEncoding
{
    public const string Name = "dps.length-prefixed-utf8/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ComputeSha256(string domain, params string[] fields) =>
        Convert.ToHexString(SHA256.HashData(Encode(domain, fields))).ToLowerInvariant();

    public static byte[] Encode(string domain, params string[] fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(fields);
        using var stream = new MemoryStream();
        WriteComponent(stream, domain);
        WriteInt32(stream, fields.Length);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            WriteComponent(stream, field);
        }

        return stream.ToArray();
    }

    private static void WriteComponent(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

public static class CanonicalJson
{
    public static string Canonicalize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException("duplicate JSON object property: " + property.Name);
                    }
                }
                foreach (var property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

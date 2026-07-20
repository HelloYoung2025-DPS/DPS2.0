using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.ControlPlaneHost.Contracts;

namespace Dps.ControlPlaneHost;

/// <summary>
/// Raised whenever active release binding material fails a fail-closed check.
/// No state mutation ever precedes this exception.
/// </summary>
public sealed class ActiveReleaseBindingException : Exception
{
    public ActiveReleaseBindingException(string message) : base(message) { }
    public ActiveReleaseBindingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One trusted Release BOM signing key parsed from the deployed release trust
/// policy: purpose "bom", algorithm "rsa-pss-sha256" only.
/// </summary>
public sealed record ReleaseBomTrustKey(
    string KeyId,
    string Identity,
    string ModulusHex,
    int Exponent)
{
    /// <summary>
    /// Parses the deployed release trust policy document (the JSON shape of
    /// governance/policies/deployed-release-trust-policy.v1.json) and keeps
    /// only keys whose purposes include "bom" with algorithm
    /// "rsa-pss-sha256". Fails closed when no such key exists.
    /// </summary>
    public static IReadOnlyList<ReleaseBomTrustKey> FromTrustPolicy(JsonElement policy)
    {
        if (policy.ValueKind != JsonValueKind.Object
            || !policy.TryGetProperty("keys", out var keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            throw new ActiveReleaseBindingException("release trust policy has no keys array");
        }
        var parsed = new List<ReleaseBomTrustKey>();
        foreach (var key in keys.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object
                || !key.TryGetProperty("purposes", out var purposes)
                || purposes.ValueKind != JsonValueKind.Array
                || !purposes.EnumerateArray().Any(static value =>
                    value.ValueKind == JsonValueKind.String && value.GetString() == "bom"))
            {
                continue;
            }
            if (key.GetProperty("algorithm").GetString() != "rsa-pss-sha256")
            {
                throw new ActiveReleaseBindingException("bom key algorithm must be rsa-pss-sha256");
            }
            parsed.Add(new ReleaseBomTrustKey(
                key.GetProperty("key_id").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key_id is missing"),
                key.GetProperty("identity").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key identity is missing"),
                key.GetProperty("modulus_hex").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key modulus is missing"),
                key.GetProperty("exponent").GetInt32()));
        }
        if (parsed.Count == 0)
        {
            throw new ActiveReleaseBindingException("release trust policy pins no bom signing key");
        }
        return parsed;
    }
}

/// <summary>
/// Source of opaque 256-bit execution tokens (lowercase 64-hex). Tokens must
/// be a pure function of nothing observable: never derived from BOM bytes,
/// device ids, generations, or clock values.
/// </summary>
public interface IExecutionTokenSource
{
    string NextToken();
}

/// <summary>
/// Proposed production token source backed by the platform CSPRNG. Not wired
/// into any composition root yet (runtime assembly is a later milestone).
/// </summary>
public sealed class CryptoRandomExecutionTokenSource : IExecutionTokenSource
{
    public string NextToken()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// Deterministic in-process authority for the per-device active Release BOM
/// binding (active.release.binding/v1). Verifies the out-of-repo RSA-PSS
/// signature on every candidate BOM against the injected trust policy keys,
/// keeps a monotonic generation and an opaque execution token per device,
/// and emits a versioned release.binding.receipt/v1 for every activation,
/// revocation, and rollback. No IO, no ambient time, fail-closed everywhere.
/// </summary>
public sealed class ActiveReleaseBindingAuthority : IActiveReleaseBindingReader
{
    private const string SchemaVersion = "1.0.0";
    private static readonly byte[] SignatureDomain =
        Encoding.ASCII.GetBytes("dps-release-bom/v1\n");

    private sealed class DeviceState
    {
        public ActiveReleaseBindingV1? Active;
        public ActiveReleaseBindingV1? Previous;
        public long Generation;
        public long Sequence;
        public readonly List<ReleaseBindingReceiptV1> Receipts = [];
    }

    private readonly IReadOnlyDictionary<string, ReleaseBomTrustKey> _keys;
    private readonly IExecutionTokenSource _tokens;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);

    public ActiveReleaseBindingAuthority(
        IReadOnlyList<ReleaseBomTrustKey> bomKeys,
        IExecutionTokenSource tokenSource,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(bomKeys);
        ArgumentNullException.ThrowIfNull(tokenSource);
        ArgumentNullException.ThrowIfNull(utcNow);
        if (bomKeys.Count == 0)
        {
            throw new ActiveReleaseBindingException("at least one bom trust key is required");
        }
        var keys = new Dictionary<string, ReleaseBomTrustKey>(StringComparer.Ordinal);
        foreach (var key in bomKeys)
        {
            if (!keys.TryAdd(key.KeyId, key))
            {
                throw new ActiveReleaseBindingException("duplicate bom trust key id");
            }
        }
        _keys = keys;
        _tokens = tokenSource;
        _utcNow = utcNow;
    }

    public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding)
    {
        binding = null;
        if (deviceBindingId is null
            || !_devices.TryGetValue(deviceBindingId, out var state)
            || state.Active is not { Status: "active" } active)
        {
            return false;
        }
        binding = active;
        return true;
    }

    public IReadOnlyList<ReleaseBindingReceiptV1> ReadReceipts(string deviceBindingId)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        return _devices.TryGetValue(deviceBindingId, out var state)
            ? state.Receipts.AsReadOnly()
            : [];
    }

    public ReleaseBindingReceiptV1 Activate(string deviceBindingId, ReadOnlySpan<byte> signedBomBytes)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        var (signatureBytes, key) = VerifySignedBom(signedBomBytes);
        var bomSha256 = Convert.ToHexStringLower(SHA256.HashData(signedBomBytes));
        var signatureSha256 = Convert.ToHexStringLower(SHA256.HashData(signatureBytes));

        // Signature verified: mutation may begin. Everything below is
        // constructed and validated before any state is replaced.
        var state = _devices.TryGetValue(deviceBindingId, out var existing)
            ? existing
            : new DeviceState();
        var generation = checked(state.Generation + 1);
        var now = RequireUtc(_utcNow());
        var binding = new ActiveReleaseBindingV1(
            SchemaVersion,
            "active.release.binding/v1",
            "control-plane-host",
            deviceBindingId,
            bomSha256,
            generation,
            RequireToken(_tokens.NextToken()),
            "active",
            key.Identity,
            key.KeyId,
            signatureSha256,
            now,
            NextReceiptId(deviceBindingId, state.Sequence + 1));
        binding.Validate();

        var demoted = state.Active is null
            ? null
            : state.Active with { Status = "previous" };
        var receipt = BuildReceipt(
            "activation",
            deviceBindingId,
            state,
            from: demoted is null
                ? null
                : new ReleaseBindingEndpointV1(demoted.ReleaseBomSha256, demoted.Generation, "previous"),
            to: new ReleaseBindingEndpointV1(bomSha256, generation, "active"),
            actorIdentity: key.Identity,
            occurredAt: now);

        state.Generation = generation;
        state.Previous = demoted;
        state.Active = binding;
        state.Sequence = receipt.Sequence;
        state.Receipts.Add(receipt);
        _devices[deviceBindingId] = state;
        return receipt;
    }

    public ReleaseBindingReceiptV1 Revoke(string deviceBindingId, long generation)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        if (!_devices.TryGetValue(deviceBindingId, out var state)
            || state.Active is not { Status: "active" } active)
        {
            throw new ActiveReleaseBindingException("no active release binding to revoke");
        }
        if (generation != active.Generation)
        {
            throw new ActiveReleaseBindingException("revocation generation does not match the active binding");
        }
        var now = RequireUtc(_utcNow());
        var receipt = BuildReceipt(
            "revocation",
            deviceBindingId,
            state,
            from: new ReleaseBindingEndpointV1(active.ReleaseBomSha256, active.Generation, "active"),
            to: new ReleaseBindingEndpointV1(active.ReleaseBomSha256, active.Generation, "revoked"),
            actorIdentity: "control-plane-host",
            occurredAt: now);

        state.Active = active with { Status = "revoked" };
        state.Sequence = receipt.Sequence;
        state.Receipts.Add(receipt);
        return receipt;
    }

    public ReleaseBindingReceiptV1 Rollback(string deviceBindingId)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        if (!_devices.TryGetValue(deviceBindingId, out var state)
            || state.Previous is not { Status: "previous" } previous)
        {
            throw new ActiveReleaseBindingException("no previous signed release binding to roll back to");
        }
        var abandoned = state.Active
            ?? throw new ActiveReleaseBindingException("rollback requires a current binding to abandon");
        var generation = checked(state.Generation + 1);
        var now = RequireUtc(_utcNow());
        var binding = new ActiveReleaseBindingV1(
            SchemaVersion,
            "active.release.binding/v1",
            "control-plane-host",
            deviceBindingId,
            previous.ReleaseBomSha256,
            generation,
            RequireToken(_tokens.NextToken()),
            "active",
            previous.SignerIdentity,
            previous.SignerKeyId,
            previous.BomSignatureSha256,
            now,
            NextReceiptId(deviceBindingId, state.Sequence + 1));
        binding.Validate();
        var receipt = BuildReceipt(
            "rollback",
            deviceBindingId,
            state,
            from: new ReleaseBindingEndpointV1(abandoned.ReleaseBomSha256, abandoned.Generation, "revoked"),
            to: new ReleaseBindingEndpointV1(previous.ReleaseBomSha256, generation, "active"),
            actorIdentity: "control-plane-host",
            occurredAt: now);

        state.Generation = generation;
        state.Active = binding;
        state.Previous = null;
        state.Sequence = receipt.Sequence;
        state.Receipts.Add(receipt);
        return receipt;
    }

    private ReleaseBindingReceiptV1 BuildReceipt(
        string kind,
        string deviceBindingId,
        DeviceState state,
        ReleaseBindingEndpointV1? from,
        ReleaseBindingEndpointV1 to,
        string actorIdentity,
        DateTimeOffset occurredAt)
    {
        var sequence = checked(state.Sequence + 1);
        var unhashed = new ReleaseBindingReceiptV1(
            SchemaVersion,
            "release.binding.receipt/v1",
            "control-plane-host",
            kind,
            deviceBindingId,
            from,
            to,
            sequence,
            actorIdentity,
            occurredAt,
            new string('0', 64),
            NextReceiptId(deviceBindingId, sequence));
        var receipt = unhashed with { PayloadSha256 = unhashed.ComputePayloadSha256() };
        receipt.Validate();
        return receipt;
    }

    private static string NextReceiptId(string deviceBindingId, long sequence)
    {
        var material = Encoding.UTF8.GetBytes(
            "dps.release.binding.receipt/v1\n"
            + deviceBindingId
            + "\n"
            + sequence.ToString(CultureInfo.InvariantCulture));
        return "receipt_" + Convert.ToHexStringLower(SHA256.HashData(material))[..32];
    }

    private static string RequireToken(string token)
    {
        ControlContractValidation.RequireSha256(token, nameof(token));
        return token;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        ControlContractValidation.RequireUtc(value, nameof(value));
        return value;
    }

    private (byte[] SignatureBytes, ReleaseBomTrustKey Key) VerifySignedBom(
        ReadOnlySpan<byte> signedBomBytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                signedBomBytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
        }
        catch (JsonException exception)
        {
            throw new ActiveReleaseBindingException("signed release BOM is not valid JSON", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ActiveReleaseBindingException("signed release BOM must be one JSON object");
            }
            if (!root.TryGetProperty("signature", out var signature)
                || signature.ValueKind != JsonValueKind.Object)
            {
                throw new ActiveReleaseBindingException("signed release BOM has no signature object");
            }
            string ReadSignatureField(string name)
            {
                if (!signature.TryGetProperty(name, out var value)
                    || value.ValueKind != JsonValueKind.String)
                {
                    throw new ActiveReleaseBindingException($"BOM signature field '{name}' is missing");
                }
                return value.GetString()!;
            }
            var fieldCount = signature.EnumerateObject().Count();
            if (fieldCount != 3)
            {
                throw new ActiveReleaseBindingException("BOM signature must have exactly algorithm, key_id, value");
            }
            if (ReadSignatureField("algorithm") != "rsa-pss-sha256")
            {
                throw new ActiveReleaseBindingException("only rsa-pss-sha256 BOM signatures are supported");
            }
            if (!_keys.TryGetValue(ReadSignatureField("key_id"), out var key))
            {
                throw new ActiveReleaseBindingException("BOM signature key is not trusted for bom");
            }
            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(ReadSignatureField("value"));
            }
            catch (FormatException exception)
            {
                throw new ActiveReleaseBindingException("BOM signature value is not valid base64", exception);
            }
            var payloadCanonical = ReleaseBomCanonicalJson.SerializeObjectWithout(root, "signature");
            var message = new byte[SignatureDomain.Length + payloadCanonical.Length];
            SignatureDomain.CopyTo(message, 0);
            payloadCanonical.CopyTo(message, SignatureDomain.Length);
            using var rsa = RSA.Create(new RSAParameters
            {
                Modulus = Convert.FromHexString(key.ModulusHex),
                Exponent = ExponentBytes(key.Exponent)
            });
            if (!rsa.VerifyData(message, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                throw new ActiveReleaseBindingException("bom signature verification failed");
            }
            return (signatureBytes, key);
        }
    }

    private static byte[] ExponentBytes(int exponent)
    {
        if (exponent < 3 || exponent % 2 == 0)
        {
            throw new ActiveReleaseBindingException("bom key exponent is invalid");
        }
        var bytes = new List<byte>(4);
        var value = exponent;
        while (value > 0)
        {
            bytes.Insert(0, (byte)(value & 0xFF));
            value >>= 8;
        }
        return [.. bytes];
    }
}

/// <summary>
/// Canonical JSON identical to the python reference
/// Tools/ci/candidate_bom_validator.py::canonical_bytes — json.dumps with
/// sort_keys=True, separators=(",", ":"), ensure_ascii=False, UTF-8 encoded.
/// Only null, bool, integer, string, array, and object values are accepted;
/// non-integer numbers fail closed because the reference wire never carries
/// them for signed Release BOM payloads.
/// </summary>
public static class ReleaseBomCanonicalJson
{
    public static byte[] SerializeObjectWithout(JsonElement root, string excludedProperty)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ActiveReleaseBindingException("canonical JSON root must be an object");
        }
        var builder = new StringBuilder(4096);
        WriteObject(builder, root, excludedProperty);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] Serialize(JsonElement value)
    {
        var builder = new StringBuilder(4096);
        WriteValue(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteObject(StringBuilder builder, JsonElement value, string? excludedProperty)
    {
        builder.Append('{');
        var first = true;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new ActiveReleaseBindingException("canonical JSON object has a duplicate key");
            }
        }
        foreach (var name in names)
        {
            if (excludedProperty is not null && name == excludedProperty)
            {
                continue;
            }
            if (!first)
            {
                builder.Append(',');
            }
            first = false;
            WriteString(builder, name);
            builder.Append(':');
            WriteValue(builder, value.GetProperty(name));
        }
        builder.Append('}');
    }

    private static void WriteValue(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Number:
                if (!value.TryGetInt64(out var integer)
                    || value.GetRawText().AsSpan().ContainsAny(".eE"))
                {
                    throw new ActiveReleaseBindingException("canonical JSON only carries integers");
                }
                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.String:
                WriteString(builder, value.GetString()!);
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }
                    firstItem = false;
                    WriteValue(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.Object:
                WriteObject(builder, value, null);
                break;
            default:
                throw new ActiveReleaseBindingException("canonical JSON value kind is unsupported");
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u")
                            .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }
}

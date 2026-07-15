using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.ControlPlaneHost.Contracts;

namespace Dps.ControlPlaneHost;

internal sealed record ModuleResultEnvelope(string SchemaVersion, string SourceContractId, string SourceProducerModule, string SoulId, string DeviceBindingId, string PlatformAccountId, string TraceId, string IdempotencyKey, DateTimeOffset OccurredAt, string SourcePayloadSha256, string ResultStatus);

internal static class ControlPlaneResultPolicy
{
    private static readonly IReadOnlyDictionary<string, (string Producer, HashSet<string> Statuses)> Rules =
        new Dictionary<string, (string, HashSet<string>)>(StringComparer.Ordinal)
        {
            ["device.registered/v1"] = ("device-registry", new HashSet<string>(["registered", "retired"], StringComparer.Ordinal)),
            ["platform.account.authorized/v1"] = ("platform-account-registry", new HashSet<string>(["authorized", "revoked", "suspended"], StringComparer.Ordinal)),
            ["identity.binding/v1"] = ("binding", new HashSet<string>(["active", "revoked"], StringComparer.Ordinal)),
            ["persona.revision/v1"] = ("persona-store", new HashSet<string>(["active", "deleted"], StringComparer.Ordinal)),
            ["soul.memory.readback/v1"] = ("soul-memory-adapter", new HashSet<string>(["verified"], StringComparer.Ordinal))
        };

    internal static void Validate(ModuleResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ControlContractValidation.RequireMajor(result.SchemaVersion, 1);
        ControlContractValidation.RequireSoulId(result.SoulId);
        ControlContractValidation.RequireDeviceBindingId(result.DeviceBindingId);
        ControlContractValidation.RequirePlatformAccountId(result.PlatformAccountId);
        ControlContractValidation.RequireTraceId(result.TraceId);
        ControlContractValidation.RequireIdempotencyKey(result.IdempotencyKey);
        ControlContractValidation.RequireUtc(result.OccurredAt, nameof(result.OccurredAt));
        ControlContractValidation.RequireSha256(result.SourcePayloadSha256, nameof(result.SourcePayloadSha256));

        if (!Rules.TryGetValue(result.SourceContractId, out var rule))
        {
            throw new NotSupportedException("Unknown source contract.");
        }

        if (!string.Equals(rule.Producer, result.SourceProducerModule, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Source producer does not own this contract.");
        }

        ControlContractValidation.RequireSourceOwnerPair(
            result.SourceContractId,
            result.SourceProducerModule);

        if (!rule.Statuses.Contains(result.ResultStatus))
        {
            throw new InvalidOperationException("Source result is not eligible runtime truth.");
        }
    }

    internal static ControlPlaneReceiptV1 CreateReceipt(ModuleResultEnvelope result, string payloadSha256)
    {
        Validate(result);
        ControlContractValidation.RequireSha256(payloadSha256, nameof(payloadSha256));
        var receipt = new ControlPlaneReceiptV1(
            "1.0.0",
            "control.plane.receipt/v1",
            "control-plane-host",
            result.SoulId,
            result.DeviceBindingId,
            result.PlatformAccountId,
            result.TraceId,
            result.IdempotencyKey,
            result.OccurredAt,
            "sensitive",
            "receipt_" + payloadSha256[..32],
            result.SourceContractId,
            result.SourceProducerModule,
            result.SourcePayloadSha256,
            "accepted");
        receipt.Validate();
        return receipt;
    }
}

internal static class ControlPlaneCanonicalEncoding
{
    private const string BusinessKeyDomain =
        "dps.control-plane-host.runtime-truth-business-key/v1";
    private const string ReceiptPayloadDomain =
        "dps.control-plane-host.runtime-truth-receipt-payload/v1";

    internal static string ComputeBusinessKeySha256(ModuleResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ComputeBusinessKeySha256(
            result.SoulId,
            result.DeviceBindingId,
            result.PlatformAccountId,
            result.SourceContractId,
            result.IdempotencyKey);
    }

    internal static string ComputeBusinessKeySha256(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string sourceContractId,
        string idempotencyKey)
        => ComputeSha256(
            BusinessKeyDomain,
            soulId,
            deviceBindingId,
            platformAccountId,
            sourceContractId,
            idempotencyKey);

    internal static string ComputeReceiptPayloadSha256(ModuleResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ComputeSha256(
            ReceiptPayloadDomain,
            result.SchemaVersion,
            result.SourceContractId,
            result.SourceProducerModule,
            result.SoulId,
            result.DeviceBindingId,
            result.PlatformAccountId,
            result.TraceId,
            result.IdempotencyKey,
            result.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            result.SourcePayloadSha256,
            result.ResultStatus);
    }

    internal static string ComputeDomainSha256(string domain, params string[] fields)
        => ComputeSha256(domain, fields);

    private static string ComputeSha256(string domain, params string[] fields)
    {
        var canonicalBytes = Encode(domain, fields);
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(canonicalBytes, digest);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    // Wire format: length-prefixed UTF-8 domain, uint32 field count, then each
    // fixed-order field as uint32 big-endian byte length followed by UTF-8 bytes.
    private static byte[] Encode(string domain, IReadOnlyList<string> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(fields);

        var encoded = new byte[fields.Count + 1][];
        try
        {
            encoded[0] = Encoding.UTF8.GetBytes(domain);
            var outputLength = checked(4 + encoded[0].Length + 4);
            for (var index = 0; index < fields.Count; index++)
            {
                ArgumentNullException.ThrowIfNull(fields[index]);
                encoded[index + 1] = Encoding.UTF8.GetBytes(fields[index]);
                outputLength = checked(outputLength + 4 + encoded[index + 1].Length);
            }

            var output = GC.AllocateUninitializedArray<byte>(outputLength);
            var offset = 0;
            WriteLengthPrefixed(output, ref offset, encoded[0]);
            BinaryPrimitives.WriteUInt32BigEndian(
                output.AsSpan(offset, sizeof(uint)),
                checked((uint)fields.Count));
            offset += sizeof(uint);
            for (var index = 1; index < encoded.Length; index++)
            {
                WriteLengthPrefixed(output, ref offset, encoded[index]);
            }

            return output;
        }
        finally
        {
            foreach (var value in encoded)
            {
                if (value is not null)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
        }
    }

    private static void WriteLengthPrefixed(byte[] destination, ref int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(
            destination.AsSpan(offset, sizeof(uint)),
            checked((uint)value.Length));
        offset += sizeof(uint);
        value.AsSpan().CopyTo(destination.AsSpan(offset, value.Length));
        offset += value.Length;
    }
}

internal sealed class ControlPlaneTruthStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string PayloadSha256, ControlPlaneReceiptV1 Receipt)> _receipts = new(StringComparer.Ordinal);

    public ControlPlaneReceiptV1 Ingest(ModuleResultEnvelope result)
    {
        ControlPlaneResultPolicy.Validate(result);
        var key = ControlPlaneCanonicalEncoding.ComputeBusinessKeySha256(result);
        var payloadSha256 = ControlPlaneCanonicalEncoding.ComputeReceiptPayloadSha256(result);
        lock (_gate)
        {
            if (_receipts.TryGetValue(key, out var prior))
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(prior.PayloadSha256),
                        Convert.FromHexString(payloadSha256)))
                {
                    throw new InvalidOperationException("Conflicting idempotency payload.");
                }

                return prior.Receipt;
            }

            var receipt = ControlPlaneResultPolicy.CreateReceipt(result, payloadSha256);
            _receipts.Add(key, (payloadSha256, receipt));
            return receipt;
        }
    }

    public ControlPlaneReceiptV1 Get(string soulId, string bindingId, string accountId, string sourceContractId, string idempotencyKey)
    {
        ControlContractValidation.RequireSoulId(soulId); ControlContractValidation.RequireDeviceBindingId(bindingId); ControlContractValidation.RequirePlatformAccountId(accountId); ControlContractValidation.RequireText(sourceContractId, 96, nameof(sourceContractId)); ControlContractValidation.RequireText(idempotencyKey, 256, nameof(idempotencyKey)); var key = ControlPlaneCanonicalEncoding.ComputeBusinessKeySha256(soulId, bindingId, accountId, sourceContractId, idempotencyKey); lock (_gate) return _receipts.TryGetValue(key, out var value) ? value.Receipt : throw new KeyNotFoundException("Unknown runtime truth receipt.");
    }
}

public static class HostStartup
{
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args); ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(error);
        if (args.Count == 1 && args[0] == "--self-check") { output.WriteLine("control-plane-host: proposed self-check PASS"); return 0; }
        error.WriteLine("control-plane-host accepts only --self-check until a signed release config exists."); return 64;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Dps.ExecutorGateway.Contracts;

public sealed record LegacyNativeStopProofV1QuarantineMetadata(
    string ContractId,
    int Major,
    string Mode,
    string Disposition,
    string WireSha256,
    int WireBytes);

public static class LegacyNativeStopProofV1QuarantineClassifier
{
    public const int MaximumWireBytes = 16_384;
    public const string QuarantineMode = "quarantine-only";
    public const string QuarantineDisposition = "QUARANTINE";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static LegacyNativeStopProofV1QuarantineMetadata Classify(ReadOnlySpan<byte> wire)
    {
        if (wire.IsEmpty || wire.Length > MaximumWireBytes)
            throw new InvalidDataException(
                $"Legacy native stop proof wire must contain between 1 and {MaximumWireBytes} bytes.");

        string json;
        try { json = StrictUtf8.GetString(wire); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Legacy native stop proof wire must be strict UTF-8.", exception);
        }

        _ = ExecutorGatewayContractJson.DeserializeNativeStopProof(json);
        return new LegacyNativeStopProofV1QuarantineMetadata(
            NativeAbortConfirmation.CurrentContractId,
            1,
            QuarantineMode,
            QuarantineDisposition,
            Convert.ToHexStringLower(SHA256.HashData(wire)),
            wire.Length);
    }
}

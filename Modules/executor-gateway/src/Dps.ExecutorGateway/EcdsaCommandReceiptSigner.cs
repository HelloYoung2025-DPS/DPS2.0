using System.Security.Cryptography;
using Dps.CommandOrchestrator.Contracts;

namespace Dps.ExecutorGateway;

public sealed class EcdsaCommandReceiptSigner : IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _privateKey;

    public EcdsaCommandReceiptSigner(ReadOnlySpan<byte> privateKeyPkcs8)
    {
        _privateKey = ECDsa.Create();
        try
        {
            _privateKey.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
            if (bytesRead != privateKeyPkcs8.Length)
                throw new ArgumentException("Receipt signing key contains trailing bytes.", nameof(privateKeyPkcs8));
            var parameters = _privateKey.ExportParameters(true);
            if (_privateKey.KeySize != 256 || parameters.D is null || !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
                throw new ArgumentException("Receipt signing requires a NIST P-256 private key.", nameof(privateKeyPkcs8));
        }
        catch
        {
            _privateKey.Dispose();
            throw;
        }
    }

    public SignedCommandReceiptV1 Sign(
        CommandReceiptV1 receipt,
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        string releaseBomSha256,
        long activeReleaseBomGeneration,
        string activeReleaseBomTokenSha256,
        string? nativeEvidenceSha256,
        string? postconditionEvidenceSha256)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authorization);
        receipt.Validate();
        command.Validate();
        authorization.Validate();
        if (receipt.CommandId != command.CommandId || receipt.LeaseId != command.LeaseId || receipt.Attempt != command.Attempt ||
            !string.Equals(receipt.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(receipt.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(receipt.TraceId, command.TraceId, StringComparison.Ordinal) ||
            !string.Equals(receipt.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Receipt signer refuses a receipt outside the exact command and lease scope.");
        if (authorization.CommandId != command.CommandId || authorization.LeaseId != command.LeaseId || authorization.Attempt != command.Attempt ||
            !string.Equals(authorization.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(authorization.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(authorization.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(authorization.TraceId, command.TraceId, StringComparison.Ordinal) ||
            !string.Equals(authorization.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(authorization.CommandSha256, ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command)) ||
            !FixedDigestEquals(authorization.ReleaseBomSha256, releaseBomSha256) ||
            authorization.ActiveReleaseBomGeneration != activeReleaseBomGeneration ||
            !FixedDigestEquals(authorization.ActiveReleaseBomTokenSha256, activeReleaseBomTokenSha256))
            throw new UnauthorizedAccessException("Receipt signer refuses authorization or BOM claims outside the exact execution snapshot.");

        var unsigned = new SignedCommandReceiptV1(
            SignedCommandReceiptV1.CurrentSchemaVersion,
            SignedCommandReceiptV1.CurrentContractId,
            SignedCommandReceiptV1.CurrentProducerModule,
            SignedCommandReceiptV1.CurrentSignatureDomain,
            SignedCommandReceiptV1.CurrentCanonicalEncoding,
            SignedCommandReceiptV1.CurrentReceiptDigestAlgorithm,
            SignedCommandReceiptV1.CurrentCommandDigestAlgorithm,
            SignedCommandReceiptV1.CurrentEvidenceDigestAlgorithm,
            SignedCommandReceiptV1.CurrentSignatureAlgorithm,
            SignedCommandReceiptV1.CurrentSignatureFormat,
            SignedCommandReceiptV1.CurrentSignatureEncoding,
            SignedCommandReceiptV1.CurrentSignerModule,
            SignedCommandReceiptV1.CurrentAuthScope,
            receipt.ReceiptId,
            receipt.CommandId,
            receipt.LeaseId,
            receipt.Attempt,
            receipt.SoulId,
            receipt.DeviceBindingId,
            receipt.PlatformAccountId,
            receipt.TraceId,
            receipt.IdempotencyKey,
            receipt.OccurredAt,
            receipt.PrivacyClass,
            CommandReceiptProtocolV1.ComputeReceiptSha256(receipt),
            ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command),
            ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization),
            releaseBomSha256,
            activeReleaseBomGeneration,
            activeReleaseBomTokenSha256,
            nativeEvidenceSha256,
            postconditionEvidenceSha256,
            receipt,
            Convert.ToBase64String(new byte[CommandReceiptProtocolV1.P1363SignatureSizeBytes]));
        unsigned.ValidatePayload();
        var payload = CommandReceiptProtocolV1.CanonicalSignedReceiptBytes(unsigned);
        byte[] signature;
        try
        {
            lock (_sync)
                signature = _privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
        try
        {
            var signed = unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
            signed.Validate();
            return signed;
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    public byte[] ExportSubjectPublicKeyInfo()
    {
        lock (_sync) return _privateKey.ExportSubjectPublicKeyInfo();
    }

    public void Dispose() => _privateKey.Dispose();

    private static bool FixedDigestEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}

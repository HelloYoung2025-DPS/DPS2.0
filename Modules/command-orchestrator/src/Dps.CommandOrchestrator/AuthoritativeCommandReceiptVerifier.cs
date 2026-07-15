using System.Security.Cryptography;
using Dps.CommandOrchestrator.Contracts;

namespace Dps.CommandOrchestrator;

internal sealed record VerifiedCommandReceipt(SignedCommandReceiptV1 SignedReceipt, CommandReceiptV1 Receipt);

internal sealed class AuthoritativeCommandReceiptVerifier : IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _publicKey;

    internal string TrustAnchorSha256 { get; }

    public AuthoritativeCommandReceiptVerifier(ReadOnlySpan<byte> trustedExecutorGatewaySubjectPublicKeyInfo)
    {
        _publicKey = ECDsa.Create();
        try
        {
            _publicKey.ImportSubjectPublicKeyInfo(trustedExecutorGatewaySubjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != trustedExecutorGatewaySubjectPublicKeyInfo.Length)
                throw new ArgumentException("Executor Gateway receipt trust anchor contains trailing bytes.", nameof(trustedExecutorGatewaySubjectPublicKeyInfo));
            var parameters = _publicKey.ExportParameters(false);
            if (_publicKey.KeySize != 256 || !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
                throw new ArgumentException("Executor Gateway receipt trust anchor must be NIST P-256.", nameof(trustedExecutorGatewaySubjectPublicKeyInfo));
            TrustAnchorSha256 = Fingerprint(_publicKey);
        }
        catch
        {
            _publicKey.Dispose();
            throw;
        }
    }

    public VerifiedCommandReceipt Verify(SignedCommandReceiptV1 signedReceipt)
    {
        ArgumentNullException.ThrowIfNull(signedReceipt);
        signedReceipt.Validate();
        var signature = Convert.FromBase64String(signedReceipt.SignatureBase64);
        var payload = CommandReceiptProtocolV1.CanonicalSignedReceiptBytes(signedReceipt);
        bool verified;
        try
        {
            lock (_sync)
                verified = _publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(payload);
        }
        if (!verified) throw new UnauthorizedAccessException("Executor Gateway command receipt signature verification failed.");
        return new VerifiedCommandReceipt(signedReceipt, signedReceipt.Receipt);
    }

    public void Dispose() => _publicKey.Dispose();

    private static string Fingerprint(ECDsa publicKey)
    {
        var normalized = publicKey.ExportSubjectPublicKeyInfo();
        try { return Convert.ToHexStringLower(SHA256.HashData(normalized)); }
        finally { CryptographicOperations.ZeroMemory(normalized); }
    }
}

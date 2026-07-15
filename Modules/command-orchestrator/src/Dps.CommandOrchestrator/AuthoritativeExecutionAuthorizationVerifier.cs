using System.Security.Cryptography;
using Dps.CommandOrchestrator.Contracts;

namespace Dps.CommandOrchestrator;

internal sealed class AuthoritativeExecutionAuthorizationVerifier : IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _publicKey;

    internal string TrustAnchorSha256 { get; }

    public AuthoritativeExecutionAuthorizationVerifier(
        ReadOnlySpan<byte> trustedPolicyApprovalSubjectPublicKeyInfo)
    {
        _publicKey = ECDsa.Create();
        try
        {
            _publicKey.ImportSubjectPublicKeyInfo(
                trustedPolicyApprovalSubjectPublicKeyInfo,
                out var bytesRead);
            if (bytesRead != trustedPolicyApprovalSubjectPublicKeyInfo.Length)
                throw new ArgumentException(
                    "Policy Approval authorization trust anchor contains trailing bytes.",
                    nameof(trustedPolicyApprovalSubjectPublicKeyInfo));
            var parameters = _publicKey.ExportParameters(false);
            if (_publicKey.KeySize != 256
                || !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Policy Approval authorization trust anchor must be NIST P-256.",
                    nameof(trustedPolicyApprovalSubjectPublicKeyInfo));
            }
            TrustAnchorSha256 = Fingerprint(_publicKey);
        }
        catch
        {
            _publicKey.Dispose();
            throw;
        }
    }

    public ExecutionAuthorizationV1 Verify(ExecutionAuthorizationV1 authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.Validate();
        var signature = Convert.FromBase64String(authorization.SignatureBase64);
        var payload = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(authorization);
        bool verified;
        try
        {
            lock (_sync)
            {
                verified = _publicKey.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(payload);
        }
        if (!verified)
            throw new UnauthorizedAccessException(
                "Policy Approval execution authorization signature verification failed.");
        return authorization;
    }

    public void Dispose() => _publicKey.Dispose();

    private static string Fingerprint(ECDsa publicKey)
    {
        var normalized = publicKey.ExportSubjectPublicKeyInfo();
        try { return Convert.ToHexStringLower(SHA256.HashData(normalized)); }
        finally { CryptographicOperations.ZeroMemory(normalized); }
    }
}

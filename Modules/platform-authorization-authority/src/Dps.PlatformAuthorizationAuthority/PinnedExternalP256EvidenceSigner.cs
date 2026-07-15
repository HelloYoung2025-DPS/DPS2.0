using System.Security.Cryptography;
using Dps.PlatformAuthorizationAuthority.Contracts;

namespace Dps.PlatformAuthorizationAuthority;

internal interface IPlatformAuthorizationEvidenceSigner
{
    ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken);
    ValueTask VerifyAsync(ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> signature, CancellationToken cancellationToken);
}

internal sealed class PinnedExternalP256EvidenceSigner : IPlatformAuthorizationEvidenceSigner
{
    private readonly IExternalP256SignatureProvider _provider;
    private readonly string _providerId;

    internal PinnedExternalP256EvidenceSigner(IExternalP256SignatureProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        PlatformAuthorizationContractValidation.RequireIdentifier(provider.ProviderId, nameof(provider.ProviderId));
        PlatformAuthorizationContractValidation.RequireExact(
            provider.IssuerKeyId,
            PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId,
            nameof(provider.IssuerKeyId));
        _providerId = provider.ProviderId;
    }

    public async ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        if (canonicalPayload.IsEmpty) throw new ArgumentException("Canonical payload is required.", nameof(canonicalPayload));
        await VerifyProviderTrustRootAsync(cancellationToken).ConfigureAwait(false);
        var signature = await _provider.SignSha256P1363Async(canonicalPayload, cancellationToken).ConfigureAwait(false);
        if (signature is null)
            throw new PlatformAuthorizationIssuanceException("The external P-256 signer returned no signature.");
        try
        {
            VerifyPinnedSignature(canonicalPayload.Span, signature);
            return signature;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(signature);
            throw;
        }
    }

    public ValueTask VerifyAsync(
        ReadOnlyMemory<byte> canonicalPayload,
        ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyPinnedSignature(canonicalPayload.Span, signature.Span);
        return ValueTask.CompletedTask;
    }

    private async ValueTask VerifyProviderTrustRootAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_provider.ProviderId, _providerId, StringComparison.Ordinal) ||
            !string.Equals(_provider.IssuerKeyId, PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId, StringComparison.Ordinal))
            throw new PlatformAuthorizationIssuanceException("The external signer identity changed after composition.");

        var providerSpki = await _provider.ExportSubjectPublicKeyInfoAsync(cancellationToken).ConfigureAwait(false);
        if (providerSpki is null)
            throw new PlatformAuthorizationIssuanceException("The external P-256 signer returned no public key.");
        var pinnedSpki = Convert.FromBase64String(PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiBase64);
        var expectedPin = Convert.FromHexString(PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiSha256);
        try
        {
            VerifyP256Spki(providerSpki);
            var actualPin = SHA256.HashData(providerSpki);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actualPin, expectedPin) ||
                    providerSpki.Length != pinnedSpki.Length ||
                    !CryptographicOperations.FixedTimeEquals(providerSpki, pinnedSpki))
                    throw new PlatformAuthorizationIssuanceException("The external signer does not hold the compiled platform-authorization root.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualPin);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(providerSpki);
            CryptographicOperations.ZeroMemory(pinnedSpki);
            CryptographicOperations.ZeroMemory(expectedPin);
        }
    }

    private static void VerifyPinnedSignature(ReadOnlySpan<byte> canonicalPayload, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
            throw new PlatformAuthorizationIssuanceException("The external signer must return an exact 64-byte P-256 P1363 signature.");
        var pinnedSpki = Convert.FromBase64String(PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiBase64);
        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(pinnedSpki, out var bytesRead);
            if (bytesRead != pinnedSpki.Length || algorithm.KeySize != 256 ||
                !algorithm.VerifyData(
                    canonicalPayload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new PlatformAuthorizationIssuanceException("The external signer returned a signature that the compiled root cannot verify.");
        }
        catch (CryptographicException exception)
        {
            throw new PlatformAuthorizationIssuanceException("The external P-256 signature could not be verified: " + exception.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinnedSpki);
        }
    }

    private static void VerifyP256Spki(ReadOnlySpan<byte> publicSpki)
    {
        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(publicSpki, out var bytesRead);
            if (bytesRead != publicSpki.Length || algorithm.KeySize != 256)
                throw new PlatformAuthorizationIssuanceException("The external signer public key is not an exact P-256 SPKI.");
        }
        catch (CryptographicException exception)
        {
            throw new PlatformAuthorizationIssuanceException("The external signer public key is invalid: " + exception.Message);
        }
    }
}

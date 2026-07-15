using System.Security.Cryptography;
using System.Text;
using Dps.PlatformAccountRegistry.Contracts;
using Dps.PlatformAuthorizationAuthority.Contracts;

namespace Dps.PlatformAccountRegistry;

public sealed class PlatformAuthorizationEvidenceException : UnauthorizedAccessException
{
    public PlatformAuthorizationEvidenceException(string message) : base(message) { }
    public PlatformAuthorizationEvidenceException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal sealed class PlatformAuthorizationEvidenceVerifier
{
    internal const string PinnedIssuerId = PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerId;
    internal const string PinnedIssuerKeyId = PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId;
    internal const string PinnedRootSpkiSha256 = PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiSha256;
    private readonly byte[] _publicSpki;
    private readonly string _issuerId;
    private readonly string _issuerKeyId;
    private readonly string _activeReleaseBomSha256;
    private readonly long _activeReleaseGeneration;
    private readonly TimeProvider _timeProvider;

    private PlatformAuthorizationEvidenceVerifier(
        byte[] publicSpki,
        string issuerId,
        string issuerKeyId,
        string activeReleaseBomSha256,
        long activeReleaseGeneration,
        TimeProvider timeProvider)
    {
        _publicSpki = publicSpki.ToArray();
        _issuerId = issuerId;
        _issuerKeyId = issuerKeyId;
        AccountContractValidation.RequireSha256(activeReleaseBomSha256, nameof(activeReleaseBomSha256));
        if (activeReleaseGeneration < 1) throw new ArgumentOutOfRangeException(nameof(activeReleaseGeneration));
        _activeReleaseBomSha256 = activeReleaseBomSha256;
        _activeReleaseGeneration = activeReleaseGeneration;
        _timeProvider = timeProvider;
        VerifyP256Spki(_publicSpki);
    }

    internal static PlatformAuthorizationEvidenceVerifier CreatePinned(
        string activeReleaseBomSha256,
        long activeReleaseGeneration,
        TimeProvider? timeProvider = null)
    {
        var publicSpki = Convert.FromBase64String(PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiBase64);
        try
        {
            var actualPin = Convert.ToHexStringLower(SHA256.HashData(publicSpki));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualPin), Encoding.ASCII.GetBytes(PinnedRootSpkiSha256)))
                throw new InvalidOperationException("The compiled platform-authorization trust root does not match its pin.");
            return new PlatformAuthorizationEvidenceVerifier(
                publicSpki,
                PinnedIssuerId,
                PinnedIssuerKeyId,
                activeReleaseBomSha256,
                activeReleaseGeneration,
                timeProvider ?? TimeProvider.System);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicSpki);
        }
    }

    internal static PlatformAuthorizationEvidenceVerifier CreateForTests(
        byte[] publicSpki,
        string issuerId,
        string issuerKeyId,
        string activeReleaseBomSha256,
        long activeReleaseGeneration,
        TimeProvider timeProvider) =>
        new(publicSpki, issuerId, issuerKeyId, activeReleaseBomSha256, activeReleaseGeneration, timeProvider);

    internal void VerifySignatureAndIssuer(SignedPlatformAuthorizationEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        try
        {
            evidence.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException)
        {
            throw new PlatformAuthorizationEvidenceException(
                "The authorization evidence envelope is structurally invalid.",
                exception);
        }
        if (!string.Equals(evidence.IssuerId, _issuerId, StringComparison.Ordinal) ||
            !string.Equals(evidence.IssuerKeyId, _issuerKeyId, StringComparison.Ordinal))
            throw new PlatformAuthorizationEvidenceException("The authorization evidence issuer is not trusted.");

        var canonical = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(evidence);
        var signature = Convert.FromBase64String(evidence.SignatureBase64);
        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(_publicSpki, out var bytesRead);
            if (bytesRead != _publicSpki.Length || !algorithm.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new PlatformAuthorizationEvidenceException("The authorization evidence signature is invalid.");
        }
        catch (CryptographicException exception)
        {
            throw new PlatformAuthorizationEvidenceException(
                "The authorization evidence signature could not be verified: " + exception.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(signature);
        }

        if (!FixedTimeHexEquals(evidence.ReleaseBomSha256, _activeReleaseBomSha256) ||
            evidence.ReleaseGeneration != _activeReleaseGeneration)
        {
            throw new PlatformAuthorizationEvidenceException(
                "The authorization evidence does not target the active signed Release BOM generation.");
        }
    }

    internal void VerifyAuthorizeScope(
        SignedPlatformAuthorizationEvidenceV1 evidence,
        AuthorizePlatformAccountCommand command)
    {
        VerifySignatureAndIssuer(evidence);
        if (!Matches(evidence.SoulId, command.SoulId) ||
            !Matches(evidence.DeviceBindingId, command.DeviceBindingId) ||
            !Matches(evidence.PlatformAccountId, command.PlatformAccountId) ||
            !Matches(evidence.Platform, command.Platform) ||
            !FixedTimeHexEquals(evidence.AliasDigest, command.AliasDigest) ||
            !Matches(evidence.AliasKeyId, command.AliasKeyId) ||
            evidence.AliasKeyEpoch != command.AliasKeyEpoch ||
            !Matches(evidence.TargetStatus, "authorized") ||
            evidence.AuthorizationRevision != 1 ||
            !Matches(evidence.TraceId, command.TraceId) ||
            !Matches(evidence.IdempotencyKey, command.IdempotencyKey) ||
            evidence.OccurredAt != command.OccurredAt)
            throw new PlatformAuthorizationEvidenceException("The authorization evidence does not match the authorize command scope.");
    }

    internal void VerifyStatusScope(
        SignedPlatformAuthorizationEvidenceV1 evidence,
        ChangePlatformAccountStatusCommand command,
        PlatformAccountAuthorizedV1 current)
    {
        VerifySignatureAndIssuer(evidence);
        if (!Matches(evidence.SoulId, command.SoulId) ||
            !Matches(evidence.DeviceBindingId, command.DeviceBindingId) ||
            !Matches(evidence.PlatformAccountId, command.PlatformAccountId) ||
            !Matches(evidence.Platform, current.Platform) ||
            !FixedTimeHexEquals(evidence.AliasDigest, current.AliasDigest) ||
            !Matches(evidence.AliasKeyId, current.AliasKeyId) ||
            evidence.AliasKeyEpoch != current.AliasKeyEpoch ||
            !Matches(evidence.TargetStatus, command.Status) ||
            evidence.AuthorizationRevision != checked(command.ExpectedRevision + 1) ||
            !Matches(evidence.TraceId, command.TraceId) ||
            !Matches(evidence.IdempotencyKey, command.IdempotencyKey) ||
            evidence.OccurredAt != command.OccurredAt)
            throw new PlatformAuthorizationEvidenceException("The authorization evidence does not match the status command scope.");
    }

    internal void EnsureFresh(SignedPlatformAuthorizationEvidenceV1 evidence)
    {
        var now = _timeProvider.GetUtcNow();
        if (now < evidence.IssuedAt || now > evidence.ExpiresAt)
            throw new PlatformAuthorizationEvidenceException("The authorization evidence is not currently valid.");
    }

    internal CancellationTokenSource CreateFreshnessDeadline(
        SignedPlatformAuthorizationEvidenceV1 evidence,
        CancellationToken cancellationToken)
    {
        EnsureFresh(evidence);
        var remaining = evidence.ExpiresAt - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            throw new PlatformAuthorizationEvidenceException("The authorization evidence expired before mutation work began.");
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        return deadline;
    }

    internal static string ComputeEvidenceSha256(SignedPlatformAuthorizationEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        var serialized = PlatformAuthorizationAuthorityContractJson.SerializeEvidenceStrict(evidence);
        try { return Convert.ToHexStringLower(SHA256.HashData(serialized)); }
        finally { CryptographicOperations.ZeroMemory(serialized); }
    }

    internal static void VerifyPinnedRootSignature(SignedPlatformAuthorizationEvidenceV1 evidence) =>
        CreatePinned(evidence.ReleaseBomSha256, evidence.ReleaseGeneration).VerifySignatureAndIssuer(evidence);

    private static bool Matches(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    private static bool FixedTimeHexEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64) return false;
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void VerifyP256Spki(byte[] publicSpki)
    {
        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(publicSpki, out var bytesRead);
            if (bytesRead != publicSpki.Length || algorithm.KeySize != 256)
                throw new ArgumentException("The authorization evidence key must be an exact P-256 SPKI.", nameof(publicSpki));
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("The authorization evidence key is invalid.", nameof(publicSpki), exception);
        }
    }
}

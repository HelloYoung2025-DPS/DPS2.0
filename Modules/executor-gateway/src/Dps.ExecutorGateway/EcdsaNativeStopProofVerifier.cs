using System.Security.Cryptography;
using Dps.ExecutorGateway.Contracts;

namespace Dps.ExecutorGateway;

public sealed record VerifiedNativeAbortConfirmation(NativeAbortConfirmation Confirmation);

public interface INativeStopProofVerifier
{
    VerifiedNativeAbortConfirmation Verify(
        NativeAbortConfirmation confirmation,
        NativeStopRequest expected,
        DateTimeOffset observedAt);
}

public sealed class EcdsaNativeStopProofVerifier : INativeStopProofVerifier, IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _publicKey;
    private readonly string _expectedKeyId;

    public EcdsaNativeStopProofVerifier(
        ReadOnlySpan<byte> subjectPublicKeyInfo,
        string expectedKeyId)
    {
        NativeStopProofProtocolV1.RequireKeyId(expectedKeyId);
        _expectedKeyId = expectedKeyId;
        _publicKey = ECDsa.Create();
        try
        {
            _publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length)
                throw new ArgumentException("Native stop proof public key contains trailing bytes.", nameof(subjectPublicKeyInfo));
            var parameters = _publicKey.ExportParameters(false);
            if (_publicKey.KeySize != 256 ||
                !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
                throw new ArgumentException("Native stop proof requires a NIST P-256 public key.", nameof(subjectPublicKeyInfo));
        }
        catch
        {
            _publicKey.Dispose();
            throw;
        }
    }

    public VerifiedNativeAbortConfirmation Verify(
        NativeAbortConfirmation confirmation,
        NativeStopRequest expected,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        confirmation.Validate();
        NativeContractGuard.RequireUtc(observedAt, nameof(observedAt));
        if (confirmation.SubmissionAttemptId != expected.SubmissionAttemptId ||
            confirmation.CommandId != expected.CommandId ||
            confirmation.LeaseId != expected.LeaseId ||
            confirmation.Attempt != expected.Attempt ||
            !FixedDigestEquals(confirmation.NativeRequestBindingSha256, expected.NativeRequestBindingSha256) ||
            !FixedDigestEquals(confirmation.SubmittedRequestSha256, expected.SubmittedRequestSha256) ||
            !string.Equals(confirmation.SoulId, expected.SoulId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.DeviceBindingId, expected.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.PlatformAccountId, expected.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.TraceId, expected.TraceId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.IdempotencyKey, expected.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(confirmation.ActiveReleaseBomSha256, expected.ActiveReleaseBomSha256) ||
            confirmation.ActiveReleaseBomGeneration != expected.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(confirmation.ActiveReleaseBomTokenSha256, expected.ActiveReleaseBomTokenSha256) ||
            !string.Equals(confirmation.WorkerInstanceId, expected.WorkerInstanceId, StringComparison.Ordinal) ||
            confirmation.WorkerGeneration != expected.WorkerGeneration ||
            confirmation.OccurredAt > observedAt)
            throw new UnauthorizedAccessException(
                "Native stop proof does not bind the exact submission attempt, worker incarnation, request, device, and active BOM.");
        if (!string.Equals(confirmation.KeyId, _expectedKeyId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Native stop proof key is not authorized by the active composition.");

        var signature = Convert.FromBase64String(confirmation.SignatureBase64);
        var payload = NativeStopProofProtocolV1.CanonicalSigningBytes(confirmation);
        bool verified;
        try
        {
            lock (_sync)
                verified = _publicKey.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
        }
        if (!verified)
            throw new UnauthorizedAccessException("Native stop proof signature is invalid.");
        return new VerifiedNativeAbortConfirmation(confirmation);
    }

    private static bool FixedDigestEquals(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(expected));

    public void Dispose() => _publicKey.Dispose();
}

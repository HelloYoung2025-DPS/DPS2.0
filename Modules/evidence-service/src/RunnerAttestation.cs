using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.EvidenceService;

public sealed record RunnerAttestationFactsV1(
    string SchemaVersion,
    string RunnerKeyId,
    string Algorithm,
    DateTimeOffset IssuedAt,
    string ReceiptSha256,
    string ArtifactSetSha256,
    string SourceReceiptSetSha256,
    string ObservedCommandSha256,
    int? ObservedExitCode,
    bool BaselineObjectVerified,
    bool InstructionReceiptVerified,
    bool RawArtifactsObserved,
    bool RoleSeparationVerified)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentAlgorithm = "ECDSA_P256_SHA256_P1363";
}

public sealed record SignedRunnerAttestationV1(
    RunnerAttestationFactsV1 Facts,
    string SignatureBase64)
{
    public override string ToString()
        => $"SignedRunnerAttestationV1 {{ RunnerKeyId = {Facts?.RunnerKeyId ?? "[MISSING]"}, SignatureBase64 = [REDACTED] }}";
}

/// <summary>
/// Immutable production trust configuration. It contains only a public key and
/// an allowed issuer identity; signing private keys stay outside this assembly.
/// </summary>
public sealed class TrustedRunnerPolicy
{
    public TrustedRunnerPolicy(
        string runnerKeyId,
        string publicKeyPem,
        string allowedEvidenceIssuerIdentity,
        string maximumVerificationLevel)
    {
        if (string.IsNullOrWhiteSpace(runnerKeyId) || runnerKeyId.Length > 128)
        {
            throw new ArgumentException("Runner key id is required.", nameof(runnerKeyId));
        }

        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new ArgumentException("Runner public key is required.", nameof(publicKeyPem));
        }

        if (string.IsNullOrWhiteSpace(allowedEvidenceIssuerIdentity))
        {
            throw new ArgumentException("Allowed evidence issuer identity is required.", nameof(allowedEvidenceIssuerIdentity));
        }

        _ = VerificationLevelRank(maximumVerificationLevel);
        RunnerKeyId = runnerKeyId;
        PublicKeyPem = publicKeyPem;
        AllowedEvidenceIssuerIdentity = allowedEvidenceIssuerIdentity;
        MaximumVerificationLevel = maximumVerificationLevel;

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (key.KeySize != 256)
        {
            throw new ArgumentException("Runner public key must use the P-256 curve.", nameof(publicKeyPem));
        }
    }

    public string RunnerKeyId { get; }

    public string PublicKeyPem { get; }

    public string AllowedEvidenceIssuerIdentity { get; }

    public string MaximumVerificationLevel { get; }

    public override string ToString()
        => $"TrustedRunnerPolicy {{ RunnerKeyId = {RunnerKeyId}, PublicKeyPem = [REDACTED], AllowedEvidenceIssuerIdentity = {AllowedEvidenceIssuerIdentity}, MaximumVerificationLevel = {MaximumVerificationLevel} }}";

    internal static int VerificationLevelRank(string value) => value switch
    {
        "REPOSITORY_STATIC_VERIFIED" => 0,
        "CONTRACT_VERIFIED" => 1,
        "INTEGRATION_VERIFIED" => 2,
        "WINDOWS_VERIFIED" => 3,
        "DEVICE_VERIFIED" => 4,
        "CANARY_VERIFIED" => 5,
        "SCALE_VERIFIED" => 6,
        _ => throw new NotSupportedException($"Unknown verification level '{value}'.")
    };
}

public static class RunnerAttestationCanonicalizer
{
    public static RunnerAttestationFactsV1 CreateFacts(
        EvidenceSubmissionCandidate candidate,
        string runnerKeyId,
        DateTimeOffset issuedAt,
        bool baselineObjectVerified,
        bool instructionReceiptVerified,
        bool rawArtifactsObserved,
        bool roleSeparationVerified)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new RunnerAttestationFactsV1(
            RunnerAttestationFactsV1.CurrentSchemaVersion,
            runnerKeyId,
            RunnerAttestationFactsV1.CurrentAlgorithm,
            issuedAt,
            candidate.ReceiptSha256,
            candidate.ArtifactSetSha256,
            candidate.SourceReceiptSetSha256,
            candidate.Receipt.CommandSha256,
            candidate.Receipt.ExitCode,
            baselineObjectVerified,
            instructionReceiptVerified,
            rawArtifactsObserved,
            roleSeparationVerified);
    }

    public static byte[] GetSigningPayload(RunnerAttestationFactsV1 facts)
    {
        ValidateFactsShape(facts);
        var canonical = string.Join(
            "\n",
            facts.SchemaVersion,
            facts.RunnerKeyId,
            facts.Algorithm,
            facts.IssuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            facts.ReceiptSha256,
            facts.ArtifactSetSha256,
            facts.SourceReceiptSetSha256,
            facts.ObservedCommandSha256,
            facts.ObservedExitCode?.ToString(CultureInfo.InvariantCulture) ?? "null",
            facts.BaselineObjectVerified ? "true" : "false",
            facts.InstructionReceiptVerified ? "true" : "false",
            facts.RawArtifactsObserved ? "true" : "false",
            facts.RoleSeparationVerified ? "true" : "false");
        return Encoding.UTF8.GetBytes(canonical);
    }

    public static string ComputeSha256(SignedRunnerAttestationV1 attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        var payload = GetSigningPayload(attestation.Facts);
        var signature = DecodeSignature(attestation.SignatureBase64);
        var combined = new byte[payload.Length + 1 + signature.Length];
        payload.CopyTo(combined, 0);
        combined[payload.Length] = (byte)'\n';
        signature.CopyTo(combined, payload.Length + 1);
        return Convert.ToHexStringLower(SHA256.HashData(combined));
    }

    internal static byte[] DecodeSignature(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            throw new ArgumentException("Runner signature is required.", nameof(value));
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Runner signature must be valid base64.", nameof(value), exception);
        }
    }

    private static void ValidateFactsShape(RunnerAttestationFactsV1 facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!string.Equals(facts.SchemaVersion, RunnerAttestationFactsV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(facts.Algorithm, RunnerAttestationFactsV1.CurrentAlgorithm, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Unknown runner attestation version or algorithm.");
        }

        if (string.IsNullOrWhiteSpace(facts.RunnerKeyId) || facts.RunnerKeyId.Length > 128)
        {
            throw new ArgumentException("Runner key id is required.", nameof(facts.RunnerKeyId));
        }

        if (facts.IssuedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Runner attestation time must be UTC.", nameof(facts.IssuedAt));
        }

        TestEvidenceServiceValidation.RequireSha256(facts.ReceiptSha256, nameof(facts.ReceiptSha256));
        TestEvidenceServiceValidation.RequireSha256(facts.ArtifactSetSha256, nameof(facts.ArtifactSetSha256));
        TestEvidenceServiceValidation.RequireSha256(facts.SourceReceiptSetSha256, nameof(facts.SourceReceiptSetSha256));
        TestEvidenceServiceValidation.RequireSha256(facts.ObservedCommandSha256, nameof(facts.ObservedCommandSha256));
    }
}

public sealed class CryptographicRunnerAttestationVerifier
{
    private readonly IReadOnlyDictionary<string, TrustedRunnerPolicy> _policies;

    public CryptographicRunnerAttestationVerifier(IEnumerable<TrustedRunnerPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var byKey = new Dictionary<string, TrustedRunnerPolicy>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            if (!byKey.TryAdd(policy.RunnerKeyId, policy))
            {
                throw new ArgumentException("Runner key ids must be unique.", nameof(policies));
            }
        }

        if (byKey.Count == 0)
        {
            throw new ArgumentException("At least one trusted runner policy is required.", nameof(policies));
        }

        _policies = byKey;
    }

    internal void Verify(
        EvidenceSubmissionCandidate candidate,
        SignedRunnerAttestationV1 attestation)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(attestation);
        var facts = attestation.Facts ?? throw new ArgumentException("Runner attestation facts are required.", nameof(attestation));
        var payload = RunnerAttestationCanonicalizer.GetSigningPayload(facts);

        if (!_policies.TryGetValue(facts.RunnerKeyId, out var policy))
        {
            throw new InvalidOperationException("Runner attestation key is not trusted.");
        }

        var receipt = candidate.Receipt;
        if (!string.Equals(receipt.EvidenceIssuerIdentity, policy.AllowedEvidenceIssuerIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Evidence issuer is not allowed by the runner trust policy.");
        }

        if (TrustedRunnerPolicy.VerificationLevelRank(receipt.VerificationLevel) >
            TrustedRunnerPolicy.VerificationLevelRank(policy.MaximumVerificationLevel))
        {
            throw new InvalidOperationException("Runner key is not trusted for the requested verification level.");
        }

        if (!string.Equals(facts.ReceiptSha256, candidate.ReceiptSha256, StringComparison.Ordinal) ||
            !string.Equals(facts.ArtifactSetSha256, candidate.ArtifactSetSha256, StringComparison.Ordinal) ||
            !string.Equals(facts.SourceReceiptSetSha256, candidate.SourceReceiptSetSha256, StringComparison.Ordinal) ||
            !string.Equals(facts.ObservedCommandSha256, receipt.CommandSha256, StringComparison.Ordinal) ||
            facts.ObservedExitCode != receipt.ExitCode)
        {
            throw new InvalidOperationException("Runner attestation does not bind the submitted receipt, artifacts, command, and exit code.");
        }

        if (!facts.BaselineObjectVerified ||
            !facts.InstructionReceiptVerified ||
            !facts.RawArtifactsObserved ||
            !facts.RoleSeparationVerified)
        {
            throw new InvalidOperationException("Runner did not attest all required verification facts.");
        }

        if (facts.IssuedAt < receipt.FinishedAt || facts.IssuedAt > receipt.FinishedAt.AddMinutes(5))
        {
            throw new InvalidOperationException("Runner attestation is stale or predates the observed command.");
        }

        var signature = RunnerAttestationCanonicalizer.DecodeSignature(attestation.SignatureBase64);
        using var key = ECDsa.Create();
        key.ImportFromPem(policy.PublicKeyPem);
        if (!key.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new InvalidOperationException("Runner attestation signature is invalid.");
        }
    }
}

internal static class TestEvidenceServiceValidation
{
    internal static void RequireSha256(string? value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.AsSpan().ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"{parameterName} must be 64 lowercase hexadecimal characters.", parameterName);
        }
    }
}

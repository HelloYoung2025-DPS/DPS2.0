using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dps.Binding.Contracts;
using Dps.PersonaStore.Contracts;

namespace Dps.PersonaStore;

public sealed record PersonaBindingCompositionExpectations(
    string ReleaseBomSha256,
    long Generation,
    string BindingInstanceConfigurationSha256,
    long BindingInstanceTrustEpoch)
{
    internal void Validate()
    {
        PersonaContractValidation.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        PersonaContractValidation.RequireSha256(BindingInstanceConfigurationSha256, nameof(BindingInstanceConfigurationSha256));
        if (BindingInstanceTrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(BindingInstanceTrustEpoch));
    }
}

internal sealed record PersonaBindingTrustContext(
    string AttestationSha256,
    string ReleaseBomSha256,
    long CompositionGeneration,
    long BindingInstanceTrustEpoch)
{
    internal static PersonaBindingTrustContext TestOnly { get; } = new(
        new string('0', 64),
        new string('0', 64),
        1,
        1);
}

internal static class PersonaBindingCompositionVerifier
{
    private const string SignatureDomain = "DPS:BINDING:PROVIDER-COMPOSITION:V1";
    private const string ExpectedFenceClientAssembly = "Dps.Binding";
    private const string ExpectedFenceClientType = "Dps.Binding.PostgresBindingMutationFenceClient";
    private const string ProductionRootSubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwPJkfb9fTamUG9VEj51KsN+FEy/HOxdlRDwpZ+NLBGcxYGWPadGuo4GZMwqNM5GS7jjr2ipgd3fw50zTweZfJA==";
    private const string ProductionRootSubjectPublicKeyInfoSha256 =
        "3a322ec109ce8f0a6ef2616fd65c9bffe821c1599d73f29b7c45762df006b85f";
    private static readonly TimeSpan MaximumAttestationLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static PersonaBindingTrustContext VerifyProduction(
        SignedBindingCompositionAttestationV1 attestation,
        PersonaBindingCompositionExpectations expectations,
        IBindingMutationFenceClient bindingFenceClient)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(bindingFenceClient);
        expectations.Validate();
        attestation.ValidateShape();
        VerifyPinnedRootSignature(attestation);
        EnsureExactFenceClientIdentity(bindingFenceClient.GetType());

        var now = DateTimeOffset.UtcNow;
        if (attestation.IssuedAt > now + MaximumClockSkew || attestation.ExpiresAt <= now ||
            attestation.ExpiresAt - attestation.IssuedAt > MaximumAttestationLifetime)
            throw new UnauthorizedAccessException("The signed Binding composition attestation is not currently valid.");
        EnsureDigestMatches(attestation.ReleaseBomSha256, expectations.ReleaseBomSha256, "Release BOM");
        if (attestation.Generation != expectations.Generation)
            throw new UnauthorizedAccessException("The Binding composition generation is not the deployment-pinned generation.");
        EnsureDigestMatches(
            attestation.BindingInstanceConfigurationSha256,
            expectations.BindingInstanceConfigurationSha256,
            "Binding instance configuration");
        if (attestation.BindingInstanceTrustEpoch != expectations.BindingInstanceTrustEpoch)
            throw new UnauthorizedAccessException("The Binding instance trust epoch is not the deployment-pinned epoch.");
        EnsureDigestMatches(
            attestation.BindingArtifactSha256,
            ComputeAssemblyArtifactSha256(bindingFenceClient.GetType().Assembly),
            "Binding implementation artifact");
        EnsureDigestMatches(
            attestation.BindingContractsArtifactSha256,
            ComputeAssemblyArtifactSha256(typeof(SignedBindingCompositionAttestationV1).Assembly),
            "Binding contracts artifact");
        var hostAssembly = Assembly.GetEntryAssembly()
            ?? throw new UnauthorizedAccessException("The composition host has no verifiable entry assembly.");
        EnsureDigestMatches(
            attestation.CompositionHostArtifactSha256,
            ComputeAssemblyArtifactSha256(hostAssembly),
            "composition host artifact");

        return new PersonaBindingTrustContext(
            ComputeExactAttestationSha256(attestation),
            attestation.ReleaseBomSha256,
            attestation.Generation,
            attestation.BindingInstanceTrustEpoch);
    }

    internal static void VerifyPinnedRootSignature(SignedBindingCompositionAttestationV1 attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        attestation.ValidateShape();
        var root = Convert.FromBase64String(ProductionRootSubjectPublicKeyInfoBase64);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(root),
                    Convert.FromHexString(ProductionRootSubjectPublicKeyInfoSha256)))
                throw new UnauthorizedAccessException("The pinned Binding composition trust anchor is corrupt.");

            var signature = Convert.FromBase64String(attestation.SignatureBase64);
            try
            {
                using var verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(root, out var bytesRead);
                if (bytesRead != root.Length || verifier.KeySize != 256 ||
                    !verifier.VerifyData(
                        Canonicalize(attestation),
                        signature,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    throw new UnauthorizedAccessException("The Binding composition attestation signature is invalid.");
            }
            finally { CryptographicOperations.ZeroMemory(signature); }
        }
        catch (CryptographicException exception)
        {
            throw new UnauthorizedAccessException("The Binding composition trust anchor or signature is invalid.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(root); }
    }

    internal static string ComputeExactAttestationSha256(SignedBindingCompositionAttestationV1 attestation)
    {
        var canonical = Canonicalize(attestation);
        try
        {
            using var stream = new MemoryStream();
            stream.Write(canonical);
            WriteField(stream, attestation.SignatureBase64);
            var bytes = stream.ToArray();
            try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static byte[] Canonicalize(SignedBindingCompositionAttestationV1 attestation)
    {
        attestation.ValidateShape();
        using var stream = new MemoryStream();
        WriteField(stream, SignatureDomain);
        WriteField(stream, attestation.SchemaVersion);
        WriteField(stream, attestation.ContractId);
        WriteField(stream, attestation.ProducerModule);
        WriteField(stream, attestation.SoulId ?? "<null>");
        WriteField(stream, attestation.DeviceBindingId ?? "<null>");
        WriteField(stream, attestation.PlatformAccountId ?? "<null>");
        WriteField(stream, attestation.TraceId);
        WriteField(stream, attestation.IdempotencyKey);
        WriteField(stream, attestation.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WriteField(stream, attestation.PrivacyClass);
        WriteField(stream, attestation.RootKeyId);
        WriteField(stream, attestation.ReleaseBomSha256);
        WriteField(stream, attestation.Generation.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, attestation.IssuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WriteField(stream, attestation.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WriteField(stream, "binding");
        WriteField(stream, attestation.BindingInstanceConfigurationSha256);
        WriteField(stream, attestation.BindingInstanceTrustEpoch.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, attestation.BindingArtifactSha256);
        WriteField(stream, attestation.BindingContractsArtifactSha256);
        WriteField(stream, "composition-host");
        WriteField(stream, attestation.CompositionHostArtifactSha256);
        WriteField(stream, "device-registry");
        WriteField(stream, attestation.DeviceRegistryInstanceConfigurationSha256);
        WriteField(stream, attestation.DeviceRegistryInstanceTrustEpoch.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, attestation.DeviceRegistryArtifactSha256);
        WriteField(stream, attestation.DeviceRegistryContractsArtifactSha256);
        WriteField(stream, "platform-account-registry");
        WriteField(stream, attestation.PlatformAccountRegistryInstanceConfigurationSha256);
        WriteField(stream, attestation.PlatformAccountRegistryInstanceTrustEpoch.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, attestation.PlatformAccountRegistryArtifactSha256);
        WriteField(stream, attestation.PlatformAccountRegistryContractsArtifactSha256);
        return stream.ToArray();
    }

    private static void EnsureExactFenceClientIdentity(Type implementationType)
    {
        if (!implementationType.IsSealed || implementationType.IsPublic || implementationType.IsNestedPublic ||
            !string.Equals(implementationType.Assembly.GetName().Name, ExpectedFenceClientAssembly, StringComparison.Ordinal) ||
            !string.Equals(implementationType.FullName, ExpectedFenceClientType, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Persona production requires Binding's exact non-public sealed PostgreSQL mutation-fence client.");
    }

    private static string ComputeAssemblyArtifactSha256(Assembly assembly)
    {
        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new UnauthorizedAccessException("An unverifiable assembly artifact cannot enter Persona production composition.");
        using var stream = File.OpenRead(location);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void EnsureDigestMatches(string signedDigest, string actualDigest, string description)
    {
        var signed = Convert.FromHexString(signedDigest);
        var actual = Convert.FromHexString(actualDigest);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(signed, actual))
                throw new UnauthorizedAccessException($"The {description} does not match the signed Binding composition attestation.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signed);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static void WriteField(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

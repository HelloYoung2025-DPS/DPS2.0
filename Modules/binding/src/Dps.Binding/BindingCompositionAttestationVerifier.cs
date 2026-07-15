using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.Binding.Contracts;
using Dps.DeviceRegistry.Contracts;
using Dps.PlatformAccountRegistry.Contracts;
using Npgsql;

namespace Dps.Binding;

internal static class BindingCompositionAttestationVerifier
{
    private const string SignatureDomain = "DPS:BINDING:PROVIDER-COMPOSITION:V1";
    private const string ProductionRootSubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwPJkfb9fTamUG9VEj51KsN+FEy/HOxdlRDwpZ+NLBGcxYGWPadGuo4GZMwqNM5GS7jjr2ipgd3fw50zTweZfJA==";
    private const string ProductionRootSubjectPublicKeyInfoSha256 =
        "3a322ec109ce8f0a6ef2616fd65c9bffe821c1599d73f29b7c45762df006b85f";
    private const string DeviceRegistryAssemblyName = "Dps.DeviceRegistry";
    private const string DeviceRegistryImplementationType =
        "Dps.DeviceRegistry.PostgresDeviceBindingReservationClient";
    private const string PlatformAccountRegistryAssemblyName = "Dps.PlatformAccountRegistry";
    private const string PlatformAccountRegistryImplementationType =
        "Dps.PlatformAccountRegistry.PostgresPlatformAccountBindingReservationClient";
    private static readonly TimeSpan MaximumAttestationLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SecretConnectionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password",
        "Passfile",
        "SSL Password"
    };

    internal static void VerifyProduction(
        SignedBindingCompositionAttestationV1 attestation,
        PostgresBindingRegistryOptions options,
        IDeviceBindingReservationClient deviceClient,
        IPlatformAccountBindingReservationClient accountClient)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deviceClient);
        ArgumentNullException.ThrowIfNull(accountClient);
        var root = GetVerifiedProductionRoot();
        EnsureExactProductionIdentity(
            deviceClient.GetType(),
            DeviceRegistryAssemblyName,
            DeviceRegistryImplementationType,
            "device-registry");
        EnsureExactProductionIdentity(
            accountClient.GetType(),
            PlatformAccountRegistryAssemblyName,
            PlatformAccountRegistryImplementationType,
            "platform-account-registry");
        Verify(
            attestation,
            deviceClient,
            accountClient,
            ComputeBindingInstanceConfigurationSha256(options),
            options.TrustEpoch,
            root,
            DateTimeOffset.UtcNow);
    }

    internal static void VerifyPinnedRootSignature(SignedBindingCompositionAttestationV1 attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        attestation.ValidateShape();
        VerifySignature(attestation, GetVerifiedProductionRoot());
    }

    internal static void Verify(
        SignedBindingCompositionAttestationV1 attestation,
        IDeviceBindingReservationClient deviceClient,
        IPlatformAccountBindingReservationClient accountClient,
        string bindingInstanceConfigurationSha256,
        long bindingInstanceTrustEpoch,
        ReadOnlySpan<byte> rootSubjectPublicKeyInfo,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(deviceClient);
        ArgumentNullException.ThrowIfNull(accountClient);
        attestation.ValidateShape();
        BindingContractValidation.RequireUtc(now, nameof(now));
        if (attestation.IssuedAt > now + MaximumClockSkew || attestation.ExpiresAt <= now)
            throw new UnauthorizedAccessException("The signed binding composition attestation is not currently valid.");
        if (attestation.ExpiresAt - attestation.IssuedAt > MaximumAttestationLifetime)
            throw new UnauthorizedAccessException("The signed binding composition attestation lifetime exceeds fifteen minutes.");

        EnsureSealedImplementation(deviceClient.GetType(), "device-registry");
        EnsureSealedImplementation(accountClient.GetType(), "platform-account-registry");
        EnsureDigestMatches(
            attestation.BindingInstanceConfigurationSha256,
            bindingInstanceConfigurationSha256,
            "binding instance configuration");
        EnsureTrustEpoch(attestation.BindingInstanceTrustEpoch, bindingInstanceTrustEpoch, "binding");
        EnsureDigestMatches(
            attestation.BindingArtifactSha256,
            ComputeImplementationArtifactSha256(typeof(PostgresBindingRegistry)),
            "binding implementation artifact");
        EnsureDigestMatches(
            attestation.BindingContractsArtifactSha256,
            ComputeImplementationArtifactSha256(typeof(SignedBindingCompositionAttestationV1)),
            "binding contracts artifact");
        var hostAssembly = System.Reflection.Assembly.GetEntryAssembly()
            ?? throw new UnauthorizedAccessException("The composition host has no verifiable entry assembly.");
        EnsureDigestMatches(
            attestation.CompositionHostArtifactSha256,
            ComputeAssemblyArtifactSha256(hostAssembly),
            "composition host artifact");
        EnsureDigestMatches(
            attestation.DeviceRegistryInstanceConfigurationSha256,
            deviceClient.InstanceConfigurationSha256,
            "device-registry instance configuration");
        EnsureTrustEpoch(attestation.DeviceRegistryInstanceTrustEpoch, deviceClient.InstanceTrustEpoch, "device-registry");
        EnsureDigestMatches(
            attestation.DeviceRegistryArtifactSha256,
            ComputeImplementationArtifactSha256(deviceClient.GetType()),
            "device-registry implementation artifact");
        EnsureDigestMatches(
            attestation.DeviceRegistryContractsArtifactSha256,
            ComputeImplementationArtifactSha256(typeof(IDeviceBindingReservationClient)),
            "device-registry contracts artifact");
        EnsureDigestMatches(
            attestation.PlatformAccountRegistryInstanceConfigurationSha256,
            accountClient.InstanceConfigurationSha256,
            "platform-account-registry instance configuration");
        EnsureTrustEpoch(attestation.PlatformAccountRegistryInstanceTrustEpoch, accountClient.InstanceTrustEpoch, "platform-account-registry");
        EnsureDigestMatches(
            attestation.PlatformAccountRegistryArtifactSha256,
            ComputeImplementationArtifactSha256(accountClient.GetType()),
            "platform-account-registry implementation artifact");
        EnsureDigestMatches(
            attestation.PlatformAccountRegistryContractsArtifactSha256,
            ComputeImplementationArtifactSha256(typeof(IPlatformAccountBindingReservationClient)),
            "platform-account-registry contracts artifact");

        VerifySignature(attestation, rootSubjectPublicKeyInfo);
    }

    private static byte[] GetVerifiedProductionRoot()
    {
        var root = Convert.FromBase64String(ProductionRootSubjectPublicKeyInfoBase64);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(root),
                Convert.FromHexString(ProductionRootSubjectPublicKeyInfoSha256)))
            throw new UnauthorizedAccessException("The pinned binding composition trust anchor is corrupt.");
        return root;
    }

    private static void VerifySignature(
        SignedBindingCompositionAttestationV1 attestation,
        ReadOnlySpan<byte> rootSubjectPublicKeyInfo)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(attestation.SignatureBase64);
        }
        catch (FormatException exception)
        {
            throw new UnauthorizedAccessException("The binding composition signature is malformed.", exception);
        }
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(rootSubjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != rootSubjectPublicKeyInfo.Length || verifier.KeySize != 256)
                throw new UnauthorizedAccessException("The binding composition root key is not one exact P-256 SPKI value.");
            if (!verifier.VerifyData(
                    Canonicalize(attestation),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new UnauthorizedAccessException("The binding composition attestation signature is invalid.");
        }
        catch (CryptographicException exception)
        {
            throw new UnauthorizedAccessException("The binding composition trust anchor or signature is invalid.", exception);
        }
    }

    internal static byte[] Canonicalize(SignedBindingCompositionAttestationV1 attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
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

    internal static string ComputeImplementationArtifactSha256(Type implementationType)
        => ComputeAssemblyArtifactSha256(implementationType.Assembly);

    internal static string ComputeAssemblyArtifactSha256(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new UnauthorizedAccessException("A provider client without a verifiable assembly artifact cannot enter production composition.");
        using var stream = File.OpenRead(location);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string ComputeBindingInstanceConfigurationSha256(PostgresBindingRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, "DPS:BINDING:INSTANCE-CONFIGURATION:V1");
        AppendHashField(hash, "binding");
        foreach (var key in builder.Keys.Cast<string>().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (SecretConnectionKeys.Contains(key)) continue;
            AppendHashField(hash, key.ToLowerInvariant());
            AppendHashField(hash, Convert.ToString(builder[key], CultureInfo.InvariantCulture) ?? string.Empty);
        }
        AppendHashField(hash, "schema");
        AppendHashField(hash, options.SchemaName);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string ComputeCompositionDescriptorSha256(SignedBindingCompositionAttestationV1 attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        attestation.ValidateShape();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, "DPS:BINDING:COMPOSITION-DESCRIPTOR:V1");
        AppendHashField(hash, attestation.ContractId);
        AppendHashField(hash, attestation.RootKeyId);
        AppendHashField(hash, attestation.ReleaseBomSha256);
        AppendHashField(hash, attestation.Generation.ToString(CultureInfo.InvariantCulture));
        AppendHashField(hash, attestation.BindingInstanceConfigurationSha256);
        AppendHashField(hash, attestation.BindingInstanceTrustEpoch.ToString(CultureInfo.InvariantCulture));
        AppendHashField(hash, attestation.BindingArtifactSha256);
        AppendHashField(hash, attestation.BindingContractsArtifactSha256);
        AppendHashField(hash, attestation.CompositionHostArtifactSha256);
        AppendHashField(hash, attestation.DeviceRegistryInstanceConfigurationSha256);
        AppendHashField(hash, attestation.DeviceRegistryInstanceTrustEpoch.ToString(CultureInfo.InvariantCulture));
        AppendHashField(hash, attestation.DeviceRegistryArtifactSha256);
        AppendHashField(hash, attestation.DeviceRegistryContractsArtifactSha256);
        AppendHashField(hash, attestation.PlatformAccountRegistryInstanceConfigurationSha256);
        AppendHashField(hash, attestation.PlatformAccountRegistryInstanceTrustEpoch.ToString(CultureInfo.InvariantCulture));
        AppendHashField(hash, attestation.PlatformAccountRegistryArtifactSha256);
        AppendHashField(hash, attestation.PlatformAccountRegistryContractsArtifactSha256);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void EnsureSealedImplementation(Type implementationType, string moduleId)
    {
        if (!implementationType.IsSealed || implementationType.IsPublic || implementationType.IsNestedPublic)
            throw new UnauthorizedAccessException($"The {moduleId} production client implementation must be non-public and sealed.");
    }

    private static void EnsureExactProductionIdentity(
        Type implementationType,
        string expectedAssemblyName,
        string expectedTypeName,
        string moduleId)
    {
        EnsureSealedImplementation(implementationType, moduleId);
        if (!string.Equals(implementationType.Assembly.GetName().Name, expectedAssemblyName, StringComparison.Ordinal) ||
            !string.Equals(implementationType.FullName, expectedTypeName, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"The {moduleId} client is not the exact implementation identity authorized by binding composition policy.");
        }
    }

    private static void EnsureDigestMatches(string signedDigest, string actualDigest, string moduleId)
    {
        var signed = Convert.FromHexString(signedDigest);
        var actual = Convert.FromHexString(actualDigest);
        if (!CryptographicOperations.FixedTimeEquals(signed, actual))
            throw new UnauthorizedAccessException($"The {moduleId} does not match the signed Release BOM attestation.");
    }

    private static void EnsureTrustEpoch(long signedEpoch, long actualEpoch, string moduleId)
    {
        if (signedEpoch != actualEpoch)
            throw new UnauthorizedAccessException($"The {moduleId} instance trust epoch does not match the signed Release BOM attestation.");
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteField(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }
}

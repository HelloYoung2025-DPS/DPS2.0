using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.WindowsEdgeSupervisor;

public sealed record WindowsHostConfiguration(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("host_id"), JsonRequired] string HostId,
    [property: JsonPropertyName("listen_host"), JsonRequired] string ListenHost,
    [property: JsonPropertyName("listen_port"), JsonRequired] int ListenPort,
    [property: JsonPropertyName("exchange_path"), JsonRequired] string ExchangePath,
    [property: JsonPropertyName("request_timeout_ms"), JsonRequired] int RequestTimeoutMs,
    [property: JsonPropertyName("maximum_request_bytes"), JsonRequired] int MaximumRequestBytes,
    [property: JsonPropertyName("allowed_client_sids"), JsonRequired] string[] AllowedClientSids,
    [property: JsonPropertyName("server_certificate_thumbprint"), JsonRequired] string ServerCertificateThumbprint,
    [property: JsonPropertyName("expected_server_key_id"), JsonRequired] string ExpectedServerKeyId,
    [property: JsonPropertyName("approved_runtime_root"), JsonRequired] string ApprovedRuntimeRoot,
    [property: JsonPropertyName("approved_worker_root"), JsonRequired] string ApprovedWorkerRoot,
    [property: JsonPropertyName("evidence_log_path"), JsonRequired] string EvidenceLogPath,
    [property: JsonPropertyName("release_bom_sha256"), JsonRequired] string ReleaseBomSha256,
    [property: JsonPropertyName("protected_policy_sha256"), JsonRequired] string ProtectedPolicySha256);

public sealed record WindowsHostProcessBinding(
    string ConfigurationSha256,
    string HostId,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    string ServerKeyId);

public static class WindowsHostConfigurationCodec
{
    public const string FixedHost = "127.0.0.1";
    public const int FixedPort = 28741;
    public const string FixedExchangePath = "/dps/edge/v1/exchange";
    public const int FixedTimeoutMs = 15000;
    public const int FixedMaximumRequestBytes = 64 * 1024;
    private const int MaximumConfigurationBytes = 32768;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Regex SidPattern = new(
        "^S-1-[0-9]+(?:-[0-9]+){1,15}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static WindowsHostConfiguration Load(
        string configurationPath,
        WindowsHostProcessBinding processBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentNullException.ThrowIfNull(processBinding);
        var fullPath = Path.GetFullPath(configurationPath);
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length is 0 or > MaximumConfigurationBytes)
            throw new InvalidDataException("Windows host configuration size is outside the allowed range");
        RequireSha256(processBinding.ConfigurationSha256, "process configuration digest");
        RequirePrefixedHex(processBinding.HostId, "host_", 64, "process host_id");
        RequireSha256(processBinding.ReleaseBomSha256, "process Release BOM digest");
        RequireSha256(processBinding.ProtectedPolicySha256, "process protected policy digest");
        RequirePrefixedHex(processBinding.ServerKeyId, "sha256_", 64, "process server key id");
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != processBinding.ConfigurationSha256)
            throw new InvalidDataException("Windows host configuration does not match its externally bound digest");

        WindowsHostConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<WindowsHostConfiguration>(bytes, JsonOptions) ??
                throw new InvalidDataException("Windows host configuration is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Windows host configuration JSON is invalid", exception);
        }

        if (configuration.SchemaVersion != "dps.windows-edge-supervisor-host/v1")
            throw new InvalidDataException("unknown Windows host configuration version");
        if (configuration.HostId != processBinding.HostId ||
            configuration.ReleaseBomSha256 != processBinding.ReleaseBomSha256 ||
            configuration.ProtectedPolicySha256 != processBinding.ProtectedPolicySha256 ||
            configuration.ExpectedServerKeyId != processBinding.ServerKeyId)
            throw new InvalidDataException("Windows host configuration does not match its protected process binding");
        if (configuration.ListenHost != FixedHost ||
            configuration.ListenPort != FixedPort ||
            configuration.ExchangePath != FixedExchangePath ||
            configuration.RequestTimeoutMs != FixedTimeoutMs ||
            configuration.MaximumRequestBytes != FixedMaximumRequestBytes)
            throw new InvalidDataException("Windows host endpoint, ABI size, or timeout is not the fixed Zenno bridge value");
        RequirePrefixedHex(configuration.HostId, "host_", 64, "host_id");
        RequireSha256(configuration.ReleaseBomSha256, "Release BOM digest");
        RequireSha256(configuration.ProtectedPolicySha256, "protected policy digest");
        RequirePrefixedHex(configuration.ExpectedServerKeyId, "sha256_", 64, "server key id");
        if (!LowerHexRegex(40).IsMatch(configuration.ServerCertificateThumbprint))
            throw new InvalidDataException("server certificate thumbprint must be a lowercase SHA-1 certificate thumbprint");
        if (configuration.AllowedClientSids is null ||
            configuration.AllowedClientSids.Length is < 1 or > 16 ||
            configuration.AllowedClientSids.Distinct(StringComparer.Ordinal).Count() != configuration.AllowedClientSids.Length ||
            configuration.AllowedClientSids.Any(sid => sid is null || !SidPattern.IsMatch(sid)))
            throw new InvalidDataException("allowed Windows client SID set is missing, duplicated, or invalid");
        ValidateAbsolutePath(configuration.ApprovedRuntimeRoot, "approved_runtime_root");
        ValidateAbsolutePath(configuration.ApprovedWorkerRoot, "approved_worker_root");
        ValidateAbsolutePath(configuration.EvidenceLogPath, "evidence_log_path");
        EnsureWithin(configuration.ApprovedRuntimeRoot, configuration.ApprovedWorkerRoot, "approved_worker_root");
        EnsureWithin(configuration.ApprovedRuntimeRoot, configuration.EvidenceLogPath, "evidence_log_path");
        return configuration;
    }

    private static void EnsureWithin(string rootPath, string targetPath, string field)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var target = Path.GetFullPath(targetPath);
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException(field + " must be inside approved_runtime_root");
    }

    private static void ValidateAbsolutePath(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
            Path.GetFullPath(value) != value)
            throw new InvalidDataException(field + " must be an absolute canonical path");
    }

    private static void RequireSha256(string? value, string field)
    {
        if (value is null || !LowerHexRegex(64).IsMatch(value))
            throw new InvalidDataException(field + " is not canonical lowercase SHA-256");
    }

    private static void RequirePrefixedHex(string? value, string prefix, int bodyLength, string field)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !LowerHexRegex(bodyLength).IsMatch(value[prefix.Length..]))
            throw new InvalidDataException(field + " is not canonical");
    }

    private static Regex LowerHexRegex(int length) => new(
        "^[a-f0-9]{" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}\\z",
        RegexOptions.CultureInvariant);

}

public sealed class WindowsCertificateServerIdentity : IDisposable
{
    private readonly object _signingSync = new();
    private readonly X509Certificate2 _certificate;
    private readonly RSA _privateKey;

    private WindowsCertificateServerIdentity(X509Certificate2 certificate, RSA privateKey, string keyId)
    {
        _certificate = certificate;
        _privateKey = privateKey;
        KeyId = keyId;
    }

    public string KeyId { get; }

    public static WindowsCertificateServerIdentity Load(WindowsHostConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the Windows server certificate identity can only load on Windows");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates
            .Where(certificate => string.Equals(
                NormalizeThumbprint(certificate.Thumbprint),
                configuration.ServerCertificateThumbprint,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new CryptographicException("the pinned LocalMachine/My server certificate was missing or ambiguous");
        var selected = matches[0];
        foreach (var extra in matches.Skip(1)) extra.Dispose();
        var now = DateTimeOffset.UtcNow;
        if (now < selected.NotBefore.ToUniversalTime() || now >= selected.NotAfter.ToUniversalTime())
        {
            selected.Dispose();
            throw new CryptographicException("the pinned server certificate is not currently valid");
        }
        var key = selected.GetRSAPrivateKey();
        if (key is null || key.KeySize < 2048)
        {
            key?.Dispose();
            selected.Dispose();
            throw new CryptographicException("the pinned server certificate has no acceptable RSA private key");
        }
        var keyId = PinnedRsaTrustStore.ComputeKeyId(key.ExportSubjectPublicKeyInfo());
        if (keyId != configuration.ExpectedServerKeyId)
        {
            key.Dispose();
            selected.Dispose();
            throw new CryptographicException("the pinned server certificate SPKI does not match the protected server identity");
        }
        var keyUsage = selected.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        if (keyUsage is not null && (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
        {
            key.Dispose();
            selected.Dispose();
            throw new CryptographicException("the pinned server certificate does not permit digital signatures");
        }
        return new WindowsCertificateServerIdentity(selected, key, keyId);
    }

    public BridgeDirectiveV1 CreateSignedDirective(
        BridgeDirectiveRequest request,
        string nonce,
        string issuedAt)
    {
        lock (_signingSync)
            return BridgeDirectiveAuthenticator.CreateSigned(request, nonce, issuedAt, _privateKey);
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        _certificate.Dispose();
    }

    private static string NormalizeThumbprint(string? value) =>
        (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}

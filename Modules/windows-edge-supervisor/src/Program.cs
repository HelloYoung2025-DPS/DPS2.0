using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using Dps.WindowsEdgeSupervisor;

if (args.Contains("--host", StringComparer.Ordinal))
{
    if (args.Contains("--windows-gate", StringComparer.Ordinal))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "FAIL",
            verification_claim = (string?)null,
            reason = "exactly one Supervisor process mode is allowed"
        }));
        return 2;
    }
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "WAITING_EXTERNAL",
            requested_level = "WINDOWS_VERIFIED",
            verification_claim = (string?)null,
            missing = new[] { "windows-host" },
            reason = "the production loopback host is Windows-only and did not bind a socket"
        }));
        return 2;
    }
    return await RunWindowsHostAsync(args).ConfigureAwait(false);
}

if (args.Contains("--windows-gate", StringComparer.Ordinal))
{
    var configurationPath = ArgumentValue(args, "--config");
    if (configurationPath is null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "WAITING_EXTERNAL",
            requested_level = "WINDOWS_VERIFIED",
            verification_claim = (string?)null,
            missing = new[] { "declarative-gate-configuration" },
            reason = "--config must name a fixed trust-root and signed capability-evidence configuration"
        }));
        return 2;
    }

    try
    {
        var processBinding = new WindowsGateProcessBinding(
            RequiredEnvironment("DPS_EDGE_WINDOWS_GATE_CONFIG_SHA256"),
            RequiredEnvironment("DPS_EDGE_RELEASE_BOM_SHA256"),
            RequiredEnvironment("DPS_EDGE_PROTECTED_POLICY_SHA256"),
            RequiredEnvironment("DPS_EDGE_WINDOWS_EVIDENCE_TRUST_STORE_FINGERPRINT"),
            RequiredEnvironment("DPS_EDGE_HOST_ID"),
            RequiredEnvironment("DPS_EDGE_SERVER_KEY_ID"));
        var configuration = WindowsGateConfigurationCodec.Load(configurationPath, processBinding);
        using var trustStore = PinnedRsaTrustStore.LoadFromDirectory(
            configuration.ApprovedWindowsEvidenceTrustRoot,
            configuration.AllowedWindowsEvidenceKeyIds);
        if (trustStore.StoreFingerprint != configuration.ApprovedWindowsEvidenceTrustStoreFingerprint)
            throw new InvalidDataException("loaded Windows evidence trust store does not match the protected fingerprint");
        var expectation = new CapabilityVerificationExpectation(
            configuration.ExpectedHostId,
            configuration.ReleaseBomSha256,
            configuration.ProtectedPolicySha256,
            configuration.ExpectedWorkerArtifactSha256,
            configuration.ExpectedWorkerVersion,
            configuration.ExpectedWorkerSlot,
            configuration.ExpectedZennoDroidPid,
            configuration.ExpectedZennoDroidStartedAt,
            configuration.ExpectedPeerAuthKeyId,
            configuration.ExpectedEvidenceLogEntryCount,
            configuration.ExpectedEvidenceLogHeadSha256,
            configuration.ExpectedEvidenceLogFileIdentitySha256,
            configuration.MinimumConnectionContinuitySeconds,
            configuration.MaximumConnectionDrops,
            configuration.MinimumAbSwitchCount,
            configuration.MinimumSoakSeconds,
            configuration.MaximumEvidenceAgeSeconds,
            configuration.MaximumClockSkewSeconds);
        var verification = CapabilityEvidenceCodec.DecodeAndVerify(
            File.ReadAllBytes(configuration.CapabilityEvidencePath),
            trustStore,
            expectation);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = verification.Assessment.Status,
            requested_level = "WINDOWS_VERIFIED",
            verification_claim = verification.Assessment.VerificationClaim,
            attestation_verified = verification.AttestationVerified,
            trust_store_fingerprint = trustStore.StoreFingerprint,
            missing = verification.Assessment.Missing,
            reason = verification.Assessment.Reason
        }));
        return verification.Assessment.Status == "PASS" ? 0 : 2;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or CryptographicException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "FAIL",
            requested_level = "WINDOWS_VERIFIED",
            verification_claim = (string?)null,
            missing = Array.Empty<string>(),
            reason = exception.Message
        }));
        return 2;
    }
}

Console.WriteLine("Windows Edge Supervisor requires a signed configuration and explicit host command.");
return 2;

static string? ArgumentValue(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    if (index < 0 || index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
        return null;
    return arguments[index + 1];
}

static string RequiredEnvironment(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidDataException("missing process-bound Windows gate value: " + name);
    return value;
}

[SupportedOSPlatform("windows")]
static async Task<int> RunWindowsHostAsync(string[] arguments)
{
    var configurationPath = ArgumentValue(arguments, "--config");
    if (configurationPath is null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "WAITING_EXTERNAL",
            requested_level = "WINDOWS_VERIFIED",
            verification_claim = (string?)null,
            missing = new[] { "declarative-host-configuration" },
            reason = "--config must name the externally digest-bound Windows host configuration"
        }));
        return 2;
    }

    try
    {
        var binding = new WindowsHostProcessBinding(
            RequiredEnvironment("DPS_EDGE_HOST_CONFIG_SHA256"),
            RequiredEnvironment("DPS_EDGE_HOST_ID"),
            RequiredEnvironment("DPS_EDGE_RELEASE_BOM_SHA256"),
            RequiredEnvironment("DPS_EDGE_PROTECTED_POLICY_SHA256"),
            RequiredEnvironment("DPS_EDGE_SERVER_KEY_ID"));
        var configuration = WindowsHostConfigurationCodec.Load(configurationPath, binding);
        using var serverIdentity = WindowsCertificateServerIdentity.Load(configuration);
        using var evidenceLog = new AppendOnlyEvidenceLog(
            configuration.ApprovedRuntimeRoot,
            configuration.EvidenceLogPath);
        using var processController = new FixedWindowsWorkerProcessController(
            configuration.ApprovedWorkerRoot);
        _ = processController;
        using var host = new BridgeLoopbackHost(configuration, serverIdentity, evidenceLog);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "STARTING",
                verification_claim = (string?)null,
                endpoint = "http://127.0.0.1:28741/dps/edge/v1/exchange",
                behavior = "authenticated-signed-wait-health-only",
                release_eligible = false
            }));
            await host.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
    catch (Exception exception) when (exception is
        IOException or UnauthorizedAccessException or InvalidDataException or
        ArgumentException or InvalidOperationException or CryptographicException or
        PlatformNotSupportedException or System.Net.HttpListenerException or Win32Exception)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "FAIL",
            requested_level = "WINDOWS_VERIFIED",
            verification_claim = (string?)null,
            reason = exception.Message
        }));
        return 2;
    }
}

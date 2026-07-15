using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dps.WindowsEdgeSupervisor;

public sealed record CapabilitySnapshot(
    string EvidenceKind,
    string? RawEvidenceSha256,
    bool IsWindows,
    string? PowerShellVersion,
    string? ZennoDroidVersion,
    int? ZennoDroidPidBefore,
    int? ZennoDroidPidAfter,
    DateTimeOffset? ZennoDroidStartedAtBefore,
    DateTimeOffset? ZennoDroidStartedAtAfter,
    string? DotNetFrameworkVersion,
    string? CSharpVersion,
    bool? CodeDomSupported,
    bool? GacSupported,
    bool? DllLoadSupported,
    string? AdbVersion,
    int AuthorizedDeviceCount,
    string? BridgeAbi,
    int? LoopbackPort,
    int? TimeoutMs,
    string? ErrorSemantics,
    string? PeerAuthMode,
    string? PeerAuthKeyId,
    string? PeerAuthAlgorithm,
    int EvidenceLogEntryCount,
    string? EvidenceLogHeadSha256,
    string? EvidenceLogFileIdentitySha256,
    int ConnectionContinuitySeconds,
    int ConnectionDrops,
    int AbSwitchCount,
    int SoakSeconds);

public sealed class CapabilityAssessment
{
    internal CapabilityAssessment(
        string status,
        string? verificationClaim,
        IReadOnlyList<string> missing,
        string reason)
    {
        Status = status;
        VerificationClaim = verificationClaim;
        Missing = missing;
        Reason = reason;
    }

    public string Status { get; }
    public string? VerificationClaim { get; }
    public IReadOnlyList<string> Missing { get; }
    public string Reason { get; }
}

public sealed record SignedWindowsEvidence(
    string SigningKeyId,
    string Algorithm,
    string SignatureBase64);

public sealed class CapabilityProbe
{
    private readonly PinnedRsaTrustStore? _windowsEvidenceTrustStore;

    public CapabilityProbe(PinnedRsaTrustStore? windowsEvidenceTrustStore = null)
    {
        _windowsEvidenceTrustStore = windowsEvidenceTrustStore;
    }

    public CapabilityAssessment Evaluate(
        CapabilitySnapshot snapshot,
        SignedWindowsEvidence? signedEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var signingStatement = CreateSigningStatement(snapshot);
        var computedEvidenceSha256 = Convert.ToHexString(SHA256.HashData(signingStatement)).ToLowerInvariant();
        var hasTrustedAttestation =
            signedEvidence is not null &&
            _windowsEvidenceTrustStore is not null &&
            signedEvidence.Algorithm == "RSA_PSS_SHA256" &&
            string.Equals(computedEvidenceSha256, snapshot.RawEvidenceSha256, StringComparison.Ordinal) &&
            _windowsEvidenceTrustStore.VerifyPssSha256Base64(
                signedEvidence.SigningKeyId,
                signingStatement,
                signedEvidence.SignatureBase64);
        return EvaluateTrustedAttestation(snapshot, hasTrustedAttestation);
    }

    internal static CapabilityAssessment EvaluateTrustedAttestation(
        CapabilitySnapshot snapshot,
        bool hasTrustedAttestation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ZennoDroidPidBefore is not null &&
            snapshot.ZennoDroidPidAfter is not null &&
            snapshot.ZennoDroidPidBefore != snapshot.ZennoDroidPidAfter)
        {
            return Failed("ZennoDroid PID changed during A/B upgrade");
        }

        if (snapshot.ZennoDroidStartedAtBefore is not null &&
            snapshot.ZennoDroidStartedAtAfter is not null &&
            snapshot.ZennoDroidStartedAtBefore != snapshot.ZennoDroidStartedAtAfter)
        {
            return Failed("ZennoDroid start time changed during A/B upgrade");
        }

        if (snapshot.ConnectionDrops != 0)
        {
            return Failed("bridge connection continuity was interrupted");
        }

        var missing = new List<string>();
        Require(snapshot.EvidenceKind == "REAL_WINDOWS_ATTESTED", "real-windows-evidence-kind", missing);
        RequireText(snapshot.RawEvidenceSha256, "raw-evidence-sha256", missing);
        Require(hasTrustedAttestation, "trusted-cryptographic-attestation", missing);
        // A cryptographically authenticated remote Windows measurement can be
        // verified by a non-Windows Control Plane. Local probes remain unable
        // to claim Windows because CaptureLocalPrerequisites sets IsWindows
        // from the actual host and has no trusted attestation.
        Require(snapshot.IsWindows, "windows", missing);
        Require(snapshot.PowerShellVersion == "7.6.2", "powershell-7.6.2", missing);
        RequireText(snapshot.ZennoDroidVersion, "zennodroid-version", missing);
        Require(snapshot.ZennoDroidPidBefore is > 0 && snapshot.ZennoDroidPidAfter is > 0, "zennodroid-pid", missing);
        Require(snapshot.ZennoDroidStartedAtBefore is not null && snapshot.ZennoDroidStartedAtAfter is not null, "zennodroid-start-time", missing);
        RequireText(snapshot.DotNetFrameworkVersion, "dotnet-framework", missing);
        RequireText(snapshot.CSharpVersion, "csharp", missing);
        Require(snapshot.CodeDomSupported == true, "codedom", missing);
        Require(snapshot.GacSupported == true, "gac", missing);
        Require(snapshot.DllLoadSupported == true, "dll-load", missing);
        Require(snapshot.AdbVersion == "37.0.0-14910828", "adb-37.0.0-14910828", missing);
        Require(snapshot.AuthorizedDeviceCount > 0, "authorized-adb-device", missing);
        Require(snapshot.BridgeAbi == "edge.bridge.exchange/v1", "bridge-abi-v1", missing);
        Require(snapshot.LoopbackPort == WindowsHostConfigurationCodec.FixedPort, "loopback-port-28741", missing);
        Require(snapshot.TimeoutMs == WindowsHostConfigurationCodec.FixedTimeoutMs, "timeout-15000", missing);
        Require(
            snapshot.ErrorSemantics == "fail-closed-unknown-outcome-no-implicit-retry/v1",
            "fail-closed-error-semantics-v1",
            missing);
        Require(snapshot.PeerAuthMode == "WINDOWS_IDENTITY_AND_PINNED_RSA", "peer-auth-mode", missing);
        Require(IsPinnedKeyId(snapshot.PeerAuthKeyId), "peer-auth-key-id", missing);
        Require(snapshot.PeerAuthAlgorithm == "RSA_PKCS1_SHA256", "peer-auth-algorithm", missing);
        Require(snapshot.EvidenceLogEntryCount >= 100, "independent-evidence-log-100", missing);
        Require(IsNonZeroLowerSha256(snapshot.EvidenceLogHeadSha256), "independent-evidence-log-head", missing);
        Require(IsNonZeroLowerSha256(snapshot.EvidenceLogFileIdentitySha256), "independent-evidence-log-file-identity", missing);
        Require(snapshot.ConnectionContinuitySeconds >= 86400, "connection-continuity-24h", missing);
        Require(snapshot.AbSwitchCount >= 100, "ab-switches-100", missing);
        Require(snapshot.SoakSeconds >= 86400, "soak-24h", missing);
        // The current Worker executable requires a launch/runtime ABI that has
        // not yet been promoted to a protected Supervisor contract. Until the
        // Windows Worker and Supervisor agree on that fixed ABI, no capability
        // evidence may authorize staging or a route transition.
        Require(false, "worker-launch-runtime-abi-unavailable", missing);

        return new CapabilityAssessment(
            "WAITING_EXTERNAL",
            null,
            missing,
            "external Windows/ZennoDroid evidence or the protected Worker runtime ABI is incomplete");
    }

    public static byte[] CreateSigningStatement(CapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var canonicalEvidence = JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.EvidenceKind,
            snapshot.IsWindows,
            snapshot.PowerShellVersion,
            snapshot.ZennoDroidVersion,
            snapshot.ZennoDroidPidBefore,
            snapshot.ZennoDroidPidAfter,
            snapshot.ZennoDroidStartedAtBefore,
            snapshot.ZennoDroidStartedAtAfter,
            snapshot.DotNetFrameworkVersion,
            snapshot.CSharpVersion,
            snapshot.CodeDomSupported,
            snapshot.GacSupported,
            snapshot.DllLoadSupported,
            snapshot.AdbVersion,
            snapshot.AuthorizedDeviceCount,
            snapshot.BridgeAbi,
            snapshot.LoopbackPort,
            snapshot.TimeoutMs,
            snapshot.ErrorSemantics,
            snapshot.PeerAuthMode,
            snapshot.PeerAuthKeyId,
            snapshot.PeerAuthAlgorithm,
            snapshot.EvidenceLogEntryCount,
            snapshot.EvidenceLogHeadSha256,
            snapshot.EvidenceLogFileIdentitySha256,
            snapshot.ConnectionContinuitySeconds,
            snapshot.ConnectionDrops,
            snapshot.AbSwitchCount,
            snapshot.SoakSeconds
        });
        var domain = Encoding.UTF8.GetBytes("dps.windows-edge-capability-evidence/v1\n");
        var statement = new byte[domain.Length + canonicalEvidence.Length];
        domain.CopyTo(statement, 0);
        canonicalEvidence.CopyTo(statement, domain.Length);
        return statement;
    }

    public static CapabilitySnapshot CaptureLocalPrerequisites()
    {
        var isWindows = OperatingSystem.IsWindows();
        var powerShell = FindExecutable(isWindows ? "pwsh.exe" : "pwsh") is null ? null : "present-unprobed";
        var adb = FindExecutable(isWindows ? "adb.exe" : "adb") is null ? null : "present-unprobed";
        Process? zenno = null;
        if (isWindows)
        {
            zenno = Process.GetProcesses().FirstOrDefault(
                process => process.ProcessName.Contains("ZennoDroid", StringComparison.OrdinalIgnoreCase));
        }

        return new CapabilitySnapshot(
            "UNATTESTED_LOCAL_PROBE",
            null,
            isWindows,
            powerShell,
            zenno?.MainModule?.FileVersionInfo.FileVersion,
            zenno?.Id,
            null,
            zenno is null ? null : new DateTimeOffset(zenno.StartTime.ToUniversalTime()),
            null,
            null,
            null,
            null,
            null,
            null,
            adb,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            null,
            null,
            0,
            0,
            0,
            0);
    }

    private static CapabilityAssessment Failed(string reason) =>
        new("FAIL", null, Array.Empty<string>(), reason);

    private static void Require(bool condition, string name, ICollection<string> missing)
    {
        if (!condition) missing.Add(name);
    }

    private static void RequireText(string? value, string name, ICollection<string> missing) =>
        Require(!string.IsNullOrWhiteSpace(value), name, missing);

    private static bool IsPinnedKeyId(string? value) =>
        value is not null &&
        value.Length == 71 &&
        value.StartsWith("sha256_", StringComparison.Ordinal) &&
        value.AsSpan(7).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsNonZeroLowerSha256(string? value) =>
        value is not null && value.Length == 64 &&
        value.Any(character => character != '0') &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? FindExecutable(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.WindowsEdgeSupervisor;

public sealed record CapabilityEvidenceV1
{
    [JsonPropertyName("schema_version"), JsonRequired] public required string SchemaVersion { get; init; }
    [JsonPropertyName("contract_id"), JsonRequired] public required string ContractId { get; init; }
    [JsonPropertyName("producer_module"), JsonRequired] public required string ProducerModule { get; init; }
    [JsonPropertyName("soul_id"), JsonRequired] public required string SoulId { get; init; }
    [JsonPropertyName("device_binding_id"), JsonRequired] public required string DeviceBindingId { get; init; }
    [JsonPropertyName("platform_account_id"), JsonRequired] public required string PlatformAccountId { get; init; }
    [JsonPropertyName("trace_id"), JsonRequired] public required string TraceId { get; init; }
    [JsonPropertyName("idempotency_key"), JsonRequired] public required string IdempotencyKey { get; init; }
    [JsonPropertyName("occurred_at"), JsonRequired] public required string OccurredAt { get; init; }
    [JsonPropertyName("privacy_class"), JsonRequired] public required string PrivacyClass { get; init; }
    [JsonPropertyName("status"), JsonRequired] public required string Status { get; init; }
    [JsonPropertyName("requested_level"), JsonRequired] public required string RequestedLevel { get; init; }
    [JsonPropertyName("verification_claim"), JsonRequired] public required string? VerificationClaim { get; init; }
    [JsonPropertyName("evidence_kind"), JsonRequired] public required string EvidenceKind { get; init; }
    [JsonPropertyName("raw_evidence_sha256"), JsonRequired] public required string? RawEvidenceSha256 { get; init; }
    [JsonPropertyName("attestation_key_id"), JsonRequired] public required string? AttestationKeyId { get; init; }
    [JsonPropertyName("attestation_algorithm"), JsonRequired] public required string? AttestationAlgorithm { get; init; }
    [JsonPropertyName("attestation_signature"), JsonRequired] public required string? AttestationSignature { get; init; }
    [JsonPropertyName("host_id"), JsonRequired] public required string HostId { get; init; }
    [JsonPropertyName("release_bom_sha256"), JsonRequired] public required string ReleaseBomSha256 { get; init; }
    [JsonPropertyName("protected_policy_sha256"), JsonRequired] public required string ProtectedPolicySha256 { get; init; }
    [JsonPropertyName("worker_artifact_sha256"), JsonRequired] public required string WorkerArtifactSha256 { get; init; }
    [JsonPropertyName("worker_version"), JsonRequired] public required string WorkerVersion { get; init; }
    [JsonPropertyName("worker_slot"), JsonRequired] public required string WorkerSlot { get; init; }
    [JsonPropertyName("issued_at"), JsonRequired] public required string IssuedAt { get; init; }
    [JsonPropertyName("not_before"), JsonRequired] public required string NotBefore { get; init; }
    [JsonPropertyName("expires_at"), JsonRequired] public required string ExpiresAt { get; init; }
    [JsonPropertyName("is_windows"), JsonRequired] public required bool IsWindows { get; init; }
    [JsonPropertyName("powershell_version"), JsonRequired] public required string? PowerShellVersion { get; init; }
    [JsonPropertyName("zennodroid_version"), JsonRequired] public required string? ZennoDroidVersion { get; init; }
    [JsonPropertyName("zennodroid_pid_before"), JsonRequired] public required int? ZennoDroidPidBefore { get; init; }
    [JsonPropertyName("zennodroid_pid_after"), JsonRequired] public required int? ZennoDroidPidAfter { get; init; }
    [JsonPropertyName("zennodroid_started_at_before"), JsonRequired] public required string? ZennoDroidStartedAtBefore { get; init; }
    [JsonPropertyName("zennodroid_started_at_after"), JsonRequired] public required string? ZennoDroidStartedAtAfter { get; init; }
    [JsonPropertyName("dotnet_framework_version"), JsonRequired] public required string? DotNetFrameworkVersion { get; init; }
    [JsonPropertyName("csharp_version"), JsonRequired] public required string? CSharpVersion { get; init; }
    [JsonPropertyName("codedom_supported"), JsonRequired] public required bool? CodeDomSupported { get; init; }
    [JsonPropertyName("gac_supported"), JsonRequired] public required bool? GacSupported { get; init; }
    [JsonPropertyName("dll_load_supported"), JsonRequired] public required bool? DllLoadSupported { get; init; }
    [JsonPropertyName("adb_version"), JsonRequired] public required string? AdbVersion { get; init; }
    [JsonPropertyName("authorized_device_count"), JsonRequired] public required int AuthorizedDeviceCount { get; init; }
    [JsonPropertyName("bridge_abi"), JsonRequired] public required string? BridgeAbi { get; init; }
    [JsonPropertyName("loopback_port"), JsonRequired] public required int? LoopbackPort { get; init; }
    [JsonPropertyName("timeout_ms"), JsonRequired] public required int? TimeoutMs { get; init; }
    [JsonPropertyName("error_semantics"), JsonRequired] public required string? ErrorSemantics { get; init; }
    [JsonPropertyName("peer_auth_mode"), JsonRequired] public required string? PeerAuthMode { get; init; }
    [JsonPropertyName("peer_auth_key_id"), JsonRequired] public required string? PeerAuthKeyId { get; init; }
    [JsonPropertyName("peer_auth_algorithm"), JsonRequired] public required string? PeerAuthAlgorithm { get; init; }
    [JsonPropertyName("evidence_log_entry_count"), JsonRequired] public required int EvidenceLogEntryCount { get; init; }
    [JsonPropertyName("evidence_log_head_sha256"), JsonRequired] public required string? EvidenceLogHeadSha256 { get; init; }
    [JsonPropertyName("evidence_log_file_identity_sha256"), JsonRequired] public required string? EvidenceLogFileIdentitySha256 { get; init; }
    [JsonPropertyName("connection_continuity_seconds"), JsonRequired] public required int ConnectionContinuitySeconds { get; init; }
    [JsonPropertyName("connection_drops"), JsonRequired] public required int ConnectionDrops { get; init; }
    [JsonPropertyName("ab_switch_count"), JsonRequired] public required int AbSwitchCount { get; init; }
    [JsonPropertyName("soak_seconds"), JsonRequired] public required int SoakSeconds { get; init; }
    [JsonPropertyName("missing"), JsonRequired] public required string[] Missing { get; init; }
}

public sealed class CapabilityEvidenceVerification
{
    internal CapabilityEvidenceVerification(
        CapabilityEvidenceV1 evidence,
        CapabilityAssessment assessment,
        bool attestationVerified,
        string wireSha256,
        string trustStoreFingerprint)
    {
        Evidence = evidence;
        Assessment = assessment;
        AttestationVerified = attestationVerified;
        WireSha256 = wireSha256;
        TrustStoreFingerprint = trustStoreFingerprint;
    }

    public CapabilityEvidenceV1 Evidence { get; }
    public CapabilityAssessment Assessment { get; }
    public bool AttestationVerified { get; }
    public string WireSha256 { get; }
    public string TrustStoreFingerprint { get; }
}

public sealed record CapabilityVerificationExpectation(
    string HostId,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    string WorkerArtifactSha256,
    string WorkerVersion,
    string WorkerSlot,
    int ExpectedZennoDroidPid,
    string ExpectedZennoDroidStartedAt,
    string ExpectedPeerAuthKeyId,
    int ExpectedEvidenceLogEntryCount,
    string ExpectedEvidenceLogHeadSha256,
    string ExpectedEvidenceLogFileIdentitySha256,
    int MinimumConnectionContinuitySeconds,
    int MaximumConnectionDrops,
    int MinimumAbSwitchCount,
    int MinimumSoakSeconds,
    int MaximumEvidenceAgeSeconds,
    int MaximumClockSkewSeconds);

public static partial class CapabilityEvidenceCodec
{
    public const string AttestationAlgorithm = "RSA_PSS_SHA256";
    private const int MaximumWireBytes = 131072;
    private const string AttestationDomain = "dps.windows-edge-capability-evidence-attestation/v1";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    internal static readonly JsonSerializerOptions StrictJson = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static CapabilityEvidenceVerification DecodeAndVerify(
        ReadOnlySpan<byte> utf8Json,
        PinnedRsaTrustStore trustStore,
        CapabilityVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentNullException.ThrowIfNull(expectation);
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumWireBytes)
            throw new InvalidDataException("capability evidence wire size is outside the contract range");
        CapabilityEvidenceV1 evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<CapabilityEvidenceV1>(utf8Json, StrictJson) ??
                throw new InvalidDataException("capability evidence payload is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("capability evidence JSON is invalid", exception);
        }

        ValidateCanonicalScopeAndSnapshot(evidence);
        ValidateDeploymentAndFreshness(evidence, expectation, DateTimeOffset.UtcNow);
        var statement = CreateAttestationStatement(evidence);
        var statementSha256 = Convert.ToHexStringLower(SHA256.HashData(statement));
        var attestationVerified = false;
        if (evidence.EvidenceKind == "REAL_WINDOWS_ATTESTED")
        {
            RequireLowerSha256(evidence.RawEvidenceSha256, "raw_evidence_sha256");
            RequireKeyId(evidence.AttestationKeyId, "attestation_key_id");
            if (evidence.AttestationAlgorithm != AttestationAlgorithm)
                throw new InvalidDataException("capability evidence attestation algorithm is not RSA-PSS SHA-256");
            RequireCanonicalBase64(evidence.AttestationSignature, 64, 2048, "attestation_signature");
            attestationVerified =
                string.Equals(statementSha256, evidence.RawEvidenceSha256, StringComparison.Ordinal) &&
                trustStore.VerifyPssSha256Base64(
                    evidence.AttestationKeyId!,
                    statement,
                    evidence.AttestationSignature!);
            if (!attestationVerified)
                throw new InvalidDataException("capability evidence attestation is invalid or is not signed by a pinned key");
        }
        else if (evidence.RawEvidenceSha256 is not null || evidence.AttestationKeyId is not null ||
                 evidence.AttestationAlgorithm is not null || evidence.AttestationSignature is not null)
        {
            throw new InvalidDataException("unattested and simulated evidence cannot carry attestation fields");
        }

        var snapshot = ToSnapshot(evidence);
        var assessment = CapabilityProbe.EvaluateTrustedAttestation(snapshot, attestationVerified);
        if (!string.Equals(evidence.Status, assessment.Status, StringComparison.Ordinal) ||
            !string.Equals(evidence.VerificationClaim, assessment.VerificationClaim, StringComparison.Ordinal) ||
            !evidence.Missing.SequenceEqual(assessment.Missing, StringComparer.Ordinal))
            throw new InvalidDataException("capability evidence reported assessment does not match verified runtime truth");
        return new CapabilityEvidenceVerification(
            evidence,
            assessment,
            attestationVerified,
            Convert.ToHexStringLower(SHA256.HashData(utf8Json)),
            trustStore.StoreFingerprint);
    }

    public static byte[] EncodeVerified(
        CapabilityEvidenceV1 evidence,
        PinnedRsaTrustStore trustStore,
        CapabilityVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evidence, StrictJson);
        _ = DecodeAndVerify(payload, trustStore, expectation);
        return payload;
    }

    public static byte[] CreateAttestationStatement(CapabilityEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateCanonicalScopeAndSnapshot(evidence);
        var canonical = new StringBuilder(2048);
        AppendAttestationField(canonical, evidence.SchemaVersion);
        AppendAttestationField(canonical, evidence.ContractId);
        AppendAttestationField(canonical, evidence.ProducerModule);
        AppendAttestationField(canonical, evidence.SoulId);
        AppendAttestationField(canonical, evidence.DeviceBindingId);
        AppendAttestationField(canonical, evidence.PlatformAccountId);
        AppendAttestationField(canonical, evidence.TraceId);
        AppendAttestationField(canonical, evidence.IdempotencyKey);
        AppendAttestationField(canonical, evidence.OccurredAt);
        AppendAttestationField(canonical, evidence.PrivacyClass);
        AppendAttestationField(canonical, evidence.HostId);
        AppendAttestationField(canonical, evidence.ReleaseBomSha256);
        AppendAttestationField(canonical, evidence.ProtectedPolicySha256);
        AppendAttestationField(canonical, evidence.WorkerArtifactSha256);
        AppendAttestationField(canonical, evidence.WorkerVersion);
        AppendAttestationField(canonical, evidence.WorkerSlot);
        AppendAttestationField(canonical, evidence.IssuedAt);
        AppendAttestationField(canonical, evidence.NotBefore);
        AppendAttestationField(canonical, evidence.ExpiresAt);
        AppendAttestationField(canonical, evidence.EvidenceKind);
        AppendAttestationField(canonical, evidence.IsWindows ? "true" : "false");
        AppendAttestationField(canonical, evidence.PowerShellVersion);
        AppendAttestationField(canonical, evidence.ZennoDroidVersion);
        AppendAttestationField(canonical, Invariant(evidence.ZennoDroidPidBefore));
        AppendAttestationField(canonical, Invariant(evidence.ZennoDroidPidAfter));
        AppendAttestationField(canonical, evidence.ZennoDroidStartedAtBefore);
        AppendAttestationField(canonical, evidence.ZennoDroidStartedAtAfter);
        AppendAttestationField(canonical, evidence.DotNetFrameworkVersion);
        AppendAttestationField(canonical, evidence.CSharpVersion);
        AppendAttestationField(canonical, Invariant(evidence.CodeDomSupported));
        AppendAttestationField(canonical, Invariant(evidence.GacSupported));
        AppendAttestationField(canonical, Invariant(evidence.DllLoadSupported));
        AppendAttestationField(canonical, evidence.AdbVersion);
        AppendAttestationField(canonical, evidence.AuthorizedDeviceCount.ToString(CultureInfo.InvariantCulture));
        AppendAttestationField(canonical, evidence.BridgeAbi);
        AppendAttestationField(canonical, Invariant(evidence.LoopbackPort));
        AppendAttestationField(canonical, Invariant(evidence.TimeoutMs));
        AppendAttestationField(canonical, evidence.ErrorSemantics);
        AppendAttestationField(canonical, evidence.PeerAuthMode);
        AppendAttestationField(canonical, evidence.PeerAuthKeyId);
        AppendAttestationField(canonical, evidence.PeerAuthAlgorithm);
        AppendAttestationField(canonical, evidence.EvidenceLogEntryCount.ToString(CultureInfo.InvariantCulture));
        AppendAttestationField(canonical, evidence.EvidenceLogHeadSha256);
        AppendAttestationField(canonical, evidence.EvidenceLogFileIdentitySha256);
        AppendAttestationField(canonical, evidence.ConnectionContinuitySeconds.ToString(CultureInfo.InvariantCulture));
        AppendAttestationField(canonical, evidence.ConnectionDrops.ToString(CultureInfo.InvariantCulture));
        AppendAttestationField(canonical, evidence.AbSwitchCount.ToString(CultureInfo.InvariantCulture));
        AppendAttestationField(canonical, evidence.SoakSeconds.ToString(CultureInfo.InvariantCulture));
        return StrictUtf8.GetBytes(AttestationDomain + "\n" + canonical);
    }

    private static string? Invariant(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static string? Invariant(bool? value) =>
        value is null ? null : value.Value ? "true" : "false";

    private static void AppendAttestationField(StringBuilder output, string? value)
    {
        if (value is null)
        {
            output.Append("-1:;");
            return;
        }

        output.Append(StrictUtf8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
        output.Append(':');
        output.Append(value);
        output.Append(';');
    }

    private static CapabilitySnapshot ToSnapshot(CapabilityEvidenceV1 evidence) => new(
        evidence.EvidenceKind,
        evidence.RawEvidenceSha256,
        evidence.IsWindows,
        evidence.PowerShellVersion,
        evidence.ZennoDroidVersion,
        evidence.ZennoDroidPidBefore,
        evidence.ZennoDroidPidAfter,
        ParseOptionalCanonicalUtc(evidence.ZennoDroidStartedAtBefore, "zennodroid_started_at_before"),
        ParseOptionalCanonicalUtc(evidence.ZennoDroidStartedAtAfter, "zennodroid_started_at_after"),
        evidence.DotNetFrameworkVersion,
        evidence.CSharpVersion,
        evidence.CodeDomSupported,
        evidence.GacSupported,
        evidence.DllLoadSupported,
        evidence.AdbVersion,
        evidence.AuthorizedDeviceCount,
        evidence.BridgeAbi,
        evidence.LoopbackPort,
        evidence.TimeoutMs,
        evidence.ErrorSemantics,
        evidence.PeerAuthMode,
        evidence.PeerAuthKeyId,
        evidence.PeerAuthAlgorithm,
        evidence.EvidenceLogEntryCount,
        evidence.EvidenceLogHeadSha256,
        evidence.EvidenceLogFileIdentitySha256,
        evidence.ConnectionContinuitySeconds,
        evidence.ConnectionDrops,
        evidence.AbSwitchCount,
        evidence.SoakSeconds);

    private static void ValidateCanonicalScopeAndSnapshot(CapabilityEvidenceV1 evidence)
    {
        if (evidence.SchemaVersion != "1.0" ||
            evidence.ContractId != "edge.capability.evidence/v1" ||
            evidence.ProducerModule != "windows-edge-supervisor")
            throw new InvalidDataException("unknown capability evidence contract identity");
        RequirePrefixedLowerHex(evidence.SoulId, "soul_", 64, "soul_id");
        RequirePrefixedLowerHex(evidence.DeviceBindingId, "db_", 32, "device_binding_id");
        RequirePrefixedLowerHex(evidence.PlatformAccountId, "pa_", 32, "platform_account_id");
        RequirePrefixedLowerHex(evidence.TraceId, "trace_", 32, "trace_id");
        RequirePrefixedLowerHex(evidence.IdempotencyKey, "idem_", 64, "idempotency_key");
        RequirePrefixedLowerHex(evidence.HostId, "host_", 64, "host_id");
        RequireLowerSha256(evidence.ReleaseBomSha256, "release_bom_sha256");
        RequireLowerSha256(evidence.ProtectedPolicySha256, "protected_policy_sha256");
        RequireLowerSha256(evidence.WorkerArtifactSha256, "worker_artifact_sha256");
        RequireText(evidence.WorkerVersion, 1, 64, "worker_version");
        if (evidence.WorkerSlot is not ("A" or "B"))
            throw new InvalidDataException("worker_slot is invalid");
        _ = ParseCanonicalUtc(evidence.OccurredAt, "occurred_at");
        _ = ParseCanonicalUtc(evidence.IssuedAt, "issued_at");
        _ = ParseCanonicalUtc(evidence.NotBefore, "not_before");
        _ = ParseCanonicalUtc(evidence.ExpiresAt, "expires_at");
        if (evidence.PrivacyClass != "internal" || evidence.RequestedLevel != "WINDOWS_VERIFIED")
            throw new InvalidDataException("capability evidence privacy class or requested level is invalid");
        if (evidence.Status is not ("PASS" or "FAIL" or "WAITING_EXTERNAL"))
            throw new InvalidDataException("unknown capability evidence status");
        if (evidence.VerificationClaim is not (null or "WINDOWS_VERIFIED"))
            throw new InvalidDataException("unknown capability evidence verification claim");
        if (evidence.EvidenceKind is not ("UNATTESTED_LOCAL_PROBE" or "SIMULATION" or "REAL_WINDOWS_ATTESTED"))
            throw new InvalidDataException("unknown capability evidence kind");
        RequireOptionalText(evidence.PowerShellVersion, 64, "powershell_version");
        RequireOptionalText(evidence.ZennoDroidVersion, 256, "zennodroid_version");
        RequireOptionalText(evidence.DotNetFrameworkVersion, 128, "dotnet_framework_version");
        RequireOptionalText(evidence.CSharpVersion, 64, "csharp_version");
        RequireOptionalText(evidence.AdbVersion, 128, "adb_version");
        RequireOptionalText(evidence.BridgeAbi, 256, "bridge_abi");
        RequireOptionalText(evidence.ErrorSemantics, 1024, "error_semantics");
        RequireOptionalText(evidence.PeerAuthMode, 64, "peer_auth_mode");
        if (evidence.PeerAuthKeyId is not null) RequireKeyId(evidence.PeerAuthKeyId, "peer_auth_key_id");
        if (evidence.PeerAuthAlgorithm is not null && evidence.PeerAuthAlgorithm != BridgeDirectiveAuthenticator.SignatureAlgorithm)
            throw new InvalidDataException("unknown peer_auth_algorithm");
        if (evidence.EvidenceLogEntryCount < 0)
            throw new InvalidDataException("evidence log entry count cannot be negative");
        if (evidence.EvidenceLogHeadSha256 is not null)
            RequireLowerSha256(evidence.EvidenceLogHeadSha256, "evidence_log_head_sha256");
        if (evidence.EvidenceLogFileIdentitySha256 is not null)
            RequireLowerSha256(evidence.EvidenceLogFileIdentitySha256, "evidence_log_file_identity_sha256");
        if ((evidence.EvidenceLogHeadSha256 is null) != (evidence.EvidenceLogFileIdentitySha256 is null) ||
            evidence.EvidenceLogEntryCount == 0 != (evidence.EvidenceLogHeadSha256 is null))
            throw new InvalidDataException("evidence log count, head, and file identity must be present as one proof");
        _ = ParseOptionalCanonicalUtc(evidence.ZennoDroidStartedAtBefore, "zennodroid_started_at_before");
        _ = ParseOptionalCanonicalUtc(evidence.ZennoDroidStartedAtAfter, "zennodroid_started_at_after");
        if (evidence.AuthorizedDeviceCount < 0 || evidence.ConnectionContinuitySeconds < 0 ||
            evidence.ConnectionDrops < 0 || evidence.AbSwitchCount < 0 || evidence.SoakSeconds < 0)
            throw new InvalidDataException("capability evidence counters cannot be negative");
        if (evidence.LoopbackPort is not null and (< 1024 or > 65535) ||
            evidence.TimeoutMs is not null and (< 1 or > 300000))
            throw new InvalidDataException("capability evidence port or timeout is outside the contract range");
        if (evidence.Missing is null)
            throw new InvalidDataException("capability evidence missing list cannot be null");
        if (evidence.Missing.Length > 64 || evidence.Missing.Distinct(StringComparer.Ordinal).Count() != evidence.Missing.Length)
            throw new InvalidDataException("capability evidence missing list is oversized or contains duplicates");
        foreach (var item in evidence.Missing) RequireText(item, 1, 128, "missing");
    }

    private static void ValidateDeploymentAndFreshness(
        CapabilityEvidenceV1 evidence,
        CapabilityVerificationExpectation expectation,
        DateTimeOffset now)
    {
        RequirePrefixedLowerHex(expectation.HostId, "host_", 64, "expected host_id");
        RequireLowerSha256(expectation.ReleaseBomSha256, "expected release_bom_sha256");
        RequireLowerSha256(expectation.ProtectedPolicySha256, "expected protected_policy_sha256");
        RequireLowerSha256(expectation.WorkerArtifactSha256, "expected worker_artifact_sha256");
        RequireText(expectation.WorkerVersion, 1, 64, "expected worker_version");
        if (expectation.WorkerSlot is not ("A" or "B") ||
            expectation.ExpectedZennoDroidPid <= 0 ||
            expectation.ExpectedEvidenceLogEntryCount < 100 ||
            expectation.MinimumConnectionContinuitySeconds < 86400 ||
            expectation.MaximumConnectionDrops != 0 ||
            expectation.MinimumAbSwitchCount < 100 ||
            expectation.MinimumSoakSeconds < 86400 ||
            expectation.MaximumEvidenceAgeSeconds is < 1 or > 900 ||
            expectation.MaximumClockSkewSeconds is < 0 or > 120)
            throw new InvalidDataException("capability verification expectation is invalid");
        _ = ParseCanonicalUtc(expectation.ExpectedZennoDroidStartedAt, "expected zennodroid_started_at");
        RequireKeyId(expectation.ExpectedPeerAuthKeyId, "expected peer_auth_key_id");
        RequireLowerSha256(expectation.ExpectedEvidenceLogHeadSha256, "expected evidence_log_head_sha256");
        RequireLowerSha256(
            expectation.ExpectedEvidenceLogFileIdentitySha256,
            "expected evidence_log_file_identity_sha256");
        if (evidence.HostId != expectation.HostId ||
            evidence.ReleaseBomSha256 != expectation.ReleaseBomSha256 ||
            evidence.ProtectedPolicySha256 != expectation.ProtectedPolicySha256 ||
            evidence.WorkerArtifactSha256 != expectation.WorkerArtifactSha256 ||
            evidence.WorkerVersion != expectation.WorkerVersion ||
            evidence.WorkerSlot != expectation.WorkerSlot ||
            evidence.ZennoDroidPidBefore != expectation.ExpectedZennoDroidPid ||
            evidence.ZennoDroidPidAfter != expectation.ExpectedZennoDroidPid ||
            evidence.ZennoDroidStartedAtBefore != expectation.ExpectedZennoDroidStartedAt ||
            evidence.ZennoDroidStartedAtAfter != expectation.ExpectedZennoDroidStartedAt ||
            evidence.PeerAuthKeyId != expectation.ExpectedPeerAuthKeyId ||
            evidence.EvidenceLogEntryCount != expectation.ExpectedEvidenceLogEntryCount ||
            evidence.EvidenceLogHeadSha256 != expectation.ExpectedEvidenceLogHeadSha256 ||
            evidence.EvidenceLogFileIdentitySha256 != expectation.ExpectedEvidenceLogFileIdentitySha256 ||
            evidence.ConnectionContinuitySeconds < expectation.MinimumConnectionContinuitySeconds ||
            evidence.ConnectionDrops > expectation.MaximumConnectionDrops ||
            evidence.AbSwitchCount < expectation.MinimumAbSwitchCount ||
            evidence.SoakSeconds < expectation.MinimumSoakSeconds)
            throw new InvalidDataException(
                "capability evidence does not match the externally protected host, bridge, log root, continuity, BOM, policy, and worker binding");

        var issuedAt = ParseCanonicalUtc(evidence.IssuedAt, "issued_at");
        var notBefore = ParseCanonicalUtc(evidence.NotBefore, "not_before");
        var expiresAt = ParseCanonicalUtc(evidence.ExpiresAt, "expires_at");
        var utcNow = now.ToUniversalTime();
        var maximumAge = TimeSpan.FromSeconds(expectation.MaximumEvidenceAgeSeconds);
        var skew = TimeSpan.FromSeconds(expectation.MaximumClockSkewSeconds);
        if (notBefore > issuedAt || expiresAt <= notBefore || expiresAt - notBefore > maximumAge ||
            issuedAt > utcNow + skew || utcNow - issuedAt > maximumAge + skew ||
            utcNow + skew < notBefore || utcNow >= expiresAt)
            throw new InvalidDataException("capability evidence is stale or outside its signed validity window");
    }

    private static DateTimeOffset? ParseOptionalCanonicalUtc(string? value, string field) =>
        value is null ? null : ParseCanonicalUtc(value, field);

    internal static DateTimeOffset ParseCanonicalUtc(string value, string field)
    {
        if (!CanonicalUtcRegex().IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException($"{field} must be a canonical zero-offset UTC timestamp");
        return parsed;
    }

    private static void RequireKeyId(string? value, string field) =>
        RequirePrefixedLowerHex(value, "sha256_", 64, field);

    private static void RequireLowerSha256(string? value, string field) =>
        RequireLowerHex(value, 64, field);

    private static void RequirePrefixedLowerHex(string? value, string prefix, int bodyLength, string field)
    {
        if (value is null || value.Length != prefix.Length + bodyLength || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"{field} is not canonical");
        RequireLowerHex(value[prefix.Length..], bodyLength, field);
    }

    private static void RequireLowerHex(string? value, int length, string field)
    {
        if (value is null || value.Length != length ||
            !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException($"{field} is not canonical lowercase hex");
    }

    private static void RequireCanonicalBase64(string? value, int minimum, int maximum, string field)
    {
        RequireText(value, minimum, maximum, field);
        try
        {
            var bytes = Convert.FromBase64String(value!);
            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
                throw new InvalidDataException($"{field} is not canonical Base64");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{field} is not canonical Base64", exception);
        }
    }

    private static void RequireOptionalText(string? value, int maximum, string field)
    {
        if (value is null) return;
        RequireText(value, 1, maximum, field);
    }

    private static void RequireText(string? value, int minimum, int maximum, string field)
    {
        if (value is null || value.Length < minimum || value.Length > maximum)
            throw new InvalidDataException($"{field} length is outside the contract range");
        _ = StrictUtf8.GetByteCount(value);
    }

    [GeneratedRegex(
        "^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-5][0-9]:[0-5][0-9](?:\\.[0-9]+)?(?:Z|\\+00:00)\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalUtcRegex();
}

public sealed record WindowsGateConfiguration(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("approved_windows_evidence_trust_root"), JsonRequired] string ApprovedWindowsEvidenceTrustRoot,
    [property: JsonPropertyName("allowed_windows_evidence_key_ids"), JsonRequired] string[] AllowedWindowsEvidenceKeyIds,
    [property: JsonPropertyName("approved_windows_evidence_trust_store_fingerprint"), JsonRequired] string ApprovedWindowsEvidenceTrustStoreFingerprint,
    [property: JsonPropertyName("release_bom_sha256"), JsonRequired] string ReleaseBomSha256,
    [property: JsonPropertyName("protected_policy_sha256"), JsonRequired] string ProtectedPolicySha256,
    [property: JsonPropertyName("expected_host_id"), JsonRequired] string ExpectedHostId,
    [property: JsonPropertyName("expected_worker_artifact_sha256"), JsonRequired] string ExpectedWorkerArtifactSha256,
    [property: JsonPropertyName("expected_worker_version"), JsonRequired] string ExpectedWorkerVersion,
    [property: JsonPropertyName("expected_worker_slot"), JsonRequired] string ExpectedWorkerSlot,
    [property: JsonPropertyName("expected_zennodroid_pid"), JsonRequired] int ExpectedZennoDroidPid,
    [property: JsonPropertyName("expected_zennodroid_started_at"), JsonRequired] string ExpectedZennoDroidStartedAt,
    [property: JsonPropertyName("expected_peer_auth_key_id"), JsonRequired] string ExpectedPeerAuthKeyId,
    [property: JsonPropertyName("expected_evidence_log_entry_count"), JsonRequired] int ExpectedEvidenceLogEntryCount,
    [property: JsonPropertyName("expected_evidence_log_head_sha256"), JsonRequired] string ExpectedEvidenceLogHeadSha256,
    [property: JsonPropertyName("expected_evidence_log_file_identity_sha256"), JsonRequired] string ExpectedEvidenceLogFileIdentitySha256,
    [property: JsonPropertyName("minimum_connection_continuity_seconds"), JsonRequired] int MinimumConnectionContinuitySeconds,
    [property: JsonPropertyName("maximum_connection_drops"), JsonRequired] int MaximumConnectionDrops,
    [property: JsonPropertyName("minimum_ab_switch_count"), JsonRequired] int MinimumAbSwitchCount,
    [property: JsonPropertyName("minimum_soak_seconds"), JsonRequired] int MinimumSoakSeconds,
    [property: JsonPropertyName("maximum_evidence_age_seconds"), JsonRequired] int MaximumEvidenceAgeSeconds,
    [property: JsonPropertyName("maximum_clock_skew_seconds"), JsonRequired] int MaximumClockSkewSeconds,
    [property: JsonPropertyName("capability_evidence_path"), JsonRequired] string CapabilityEvidencePath);

public sealed record WindowsGateProcessBinding(
    string ConfigurationSha256,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    string TrustStoreFingerprint,
    string HostId,
    string ServerKeyId);

public static class WindowsGateConfigurationCodec
{
    private const int MaximumConfigurationBytes = 32768;

    public static WindowsGateConfiguration Load(
        string configurationPath,
        WindowsGateProcessBinding processBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentNullException.ThrowIfNull(processBinding);
        var bytes = File.ReadAllBytes(Path.GetFullPath(configurationPath));
        if (bytes.Length == 0 || bytes.Length > MaximumConfigurationBytes)
            throw new InvalidDataException("Windows gate configuration size is outside the allowed range");
        RequireLowerSha256(processBinding.ConfigurationSha256, "process-bound configuration SHA-256");
        RequireLowerSha256(processBinding.ReleaseBomSha256, "process-bound Release BOM SHA-256");
        RequireLowerSha256(processBinding.ProtectedPolicySha256, "process-bound protected policy SHA-256");
        RequireLowerSha256(processBinding.TrustStoreFingerprint, "process-bound trust-store fingerprint");
        RequirePrefixedLowerHex(processBinding.HostId, "host_", 64, "process-bound host_id");
        RequirePrefixedLowerHex(processBinding.ServerKeyId, "sha256_", 64, "process-bound server key id");
        var actualConfigurationSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (actualConfigurationSha256 != processBinding.ConfigurationSha256)
            throw new InvalidDataException("Windows gate configuration does not match the process-bound digest");
        WindowsGateConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<WindowsGateConfiguration>(bytes, CapabilityEvidenceCodec.StrictJson) ??
                throw new InvalidDataException("Windows gate configuration is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Windows gate configuration JSON is invalid", exception);
        }
        if (configuration.SchemaVersion != "dps.windows-edge-supervisor-gate/v1")
            throw new InvalidDataException("unknown Windows gate configuration version");
        if (!Path.IsPathFullyQualified(configuration.ApprovedWindowsEvidenceTrustRoot) ||
            !Path.IsPathFullyQualified(configuration.CapabilityEvidencePath))
            throw new InvalidDataException("Windows gate trust root and evidence path must be absolute");
        if (configuration.AllowedWindowsEvidenceKeyIds is null)
            throw new InvalidDataException("Windows gate allowed key IDs cannot be null");
        if (configuration.AllowedWindowsEvidenceKeyIds.Length == 0 ||
            configuration.AllowedWindowsEvidenceKeyIds.Any(static keyId => keyId is null) ||
            configuration.AllowedWindowsEvidenceKeyIds.Distinct(StringComparer.Ordinal).Count() !=
            configuration.AllowedWindowsEvidenceKeyIds.Length)
            throw new InvalidDataException("Windows gate must pin at least one unique evidence key id");
        foreach (var keyId in configuration.AllowedWindowsEvidenceKeyIds)
        {
            if (keyId is null || keyId.Length != 71 || !keyId.StartsWith("sha256_", StringComparison.Ordinal) ||
                !keyId.AsSpan(7).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidDataException("Windows gate contains a noncanonical pinned key id");
        }
        RequireLowerSha256(configuration.ApprovedWindowsEvidenceTrustStoreFingerprint, "configured trust-store fingerprint");
        RequireLowerSha256(configuration.ReleaseBomSha256, "configured Release BOM SHA-256");
        RequireLowerSha256(configuration.ProtectedPolicySha256, "configured protected policy SHA-256");
        RequirePrefixedLowerHex(configuration.ExpectedHostId, "host_", 64, "configured host_id");
        RequireLowerSha256(configuration.ExpectedWorkerArtifactSha256, "configured worker artifact SHA-256");
        RequirePrefixedLowerHex(configuration.ExpectedPeerAuthKeyId, "sha256_", 64, "configured peer auth key id");
        RequireLowerSha256(configuration.ExpectedEvidenceLogHeadSha256, "configured evidence log head");
        RequireLowerSha256(
            configuration.ExpectedEvidenceLogFileIdentitySha256,
            "configured evidence log file identity");
        _ = CapabilityEvidenceCodec.ParseCanonicalUtc(
            configuration.ExpectedZennoDroidStartedAt,
            "configured expected_zennodroid_started_at");
        if (string.IsNullOrWhiteSpace(configuration.ExpectedWorkerVersion) ||
            configuration.ExpectedWorkerVersion.Length > 64 ||
            configuration.ExpectedWorkerSlot is not ("A" or "B") ||
            configuration.ExpectedZennoDroidPid <= 0 ||
            configuration.ExpectedEvidenceLogEntryCount < 100 ||
            configuration.MinimumConnectionContinuitySeconds < 86400 ||
            configuration.MaximumConnectionDrops != 0 ||
            configuration.MinimumAbSwitchCount < 100 ||
            configuration.MinimumSoakSeconds < 86400 ||
            configuration.MaximumEvidenceAgeSeconds is < 1 or > 900 ||
            configuration.MaximumClockSkewSeconds is < 0 or > 120)
            throw new InvalidDataException("Windows gate worker or freshness binding is invalid");
        if (configuration.ReleaseBomSha256 != processBinding.ReleaseBomSha256 ||
            configuration.ProtectedPolicySha256 != processBinding.ProtectedPolicySha256 ||
            configuration.ApprovedWindowsEvidenceTrustStoreFingerprint != processBinding.TrustStoreFingerprint ||
            configuration.ExpectedHostId != processBinding.HostId ||
            configuration.ExpectedPeerAuthKeyId != processBinding.ServerKeyId)
            throw new InvalidDataException("Windows gate configuration does not match the externally protected process binding");
        return configuration;
    }

    private static void RequireLowerSha256(string? value, string field)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new InvalidDataException(field + " is not canonical lowercase SHA-256");
    }

    private static void RequirePrefixedLowerHex(string? value, string prefix, int bodyLength, string field)
    {
        if (value is null || value.Length != prefix.Length + bodyLength ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.AsSpan(prefix.Length).ToString().Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new InvalidDataException(field + " is not canonical");
    }
}

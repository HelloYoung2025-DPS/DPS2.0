using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.FactoryReleaseController.Contracts;

public sealed record ReleaseBomSignatureV1(
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("value")] string Value);

public sealed record NativeStopAuthorityV1(
    [property: JsonPropertyName("authority_id")] string AuthorityId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("worker_module_id")] string WorkerModuleId,
    [property: JsonPropertyName("worker_artifact_id")] string WorkerArtifactId,
    [property: JsonPropertyName("worker_artifact_sha256")] string WorkerArtifactSha256,
    [property: JsonPropertyName("worker_version")] string WorkerVersion,
    [property: JsonPropertyName("worker_slot")] string WorkerSlot,
    [property: JsonPropertyName("worker_instance_id")] string WorkerInstanceId,
    [property: JsonPropertyName("worker_generation")] long WorkerGeneration,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("p256_spki_sha256")] string P256SpkiSha256,
    [property: JsonPropertyName("signature_algorithm")] string SignatureAlgorithm,
    [property: JsonPropertyName("signature_format")] string SignatureFormat,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("native_stop_contract_id")] string NativeStopContractId,
    [property: JsonPropertyName("policy_id")] string PolicyId,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("activation_token_sha256")] string ActivationTokenSha256,
    [property: JsonPropertyName("rotation_epoch")] long RotationEpoch,
    [property: JsonPropertyName("valid_from")] string ValidFrom,
    [property: JsonPropertyName("valid_until")] string ValidUntil,
    [property: JsonPropertyName("revoked")] bool Revoked,
    [property: JsonPropertyName("worker_authority_sha256")] string WorkerAuthoritySha256);

public sealed record DeviceRouteAssignmentAuthorityV1(
    [property: JsonPropertyName("route_authority_id")] string RouteAuthorityId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("supervisor_module_id")] string SupervisorModuleId,
    [property: JsonPropertyName("supervisor_artifact_id")] string SupervisorArtifactId,
    [property: JsonPropertyName("supervisor_artifact_sha256")] string SupervisorArtifactSha256,
    [property: JsonPropertyName("supervisor_version")] string SupervisorVersion,
    [property: JsonPropertyName("supervisor_instance_id")] string SupervisorInstanceId,
    [property: JsonPropertyName("supervisor_generation")] long SupervisorGeneration,
    [property: JsonPropertyName("route_signer_key_id")] string RouteSignerKeyId,
    [property: JsonPropertyName("route_signer_p256_spki_sha256")] string RouteSignerP256SpkiSha256,
    [property: JsonPropertyName("signature_algorithm")] string SignatureAlgorithm,
    [property: JsonPropertyName("signature_format")] string SignatureFormat,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("policy_id")] string PolicyId,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("activation_token_sha256")] string ActivationTokenSha256,
    [property: JsonPropertyName("rotation_epoch")] long RotationEpoch,
    [property: JsonPropertyName("valid_from")] string ValidFrom,
    [property: JsonPropertyName("valid_until")] string ValidUntil,
    [property: JsonPropertyName("revoked")] bool Revoked,
    [property: JsonPropertyName("route_authority_sha256")] string RouteAuthoritySha256);

public sealed record NativeStopChallengeAuthorityV1(
    [property: JsonPropertyName("authority_id")] string AuthorityId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("policy_module_id")] string PolicyModuleId,
    [property: JsonPropertyName("policy_artifact_id")] string PolicyArtifactId,
    [property: JsonPropertyName("policy_artifact_sha256")] string PolicyArtifactSha256,
    [property: JsonPropertyName("policy_version")] string PolicyVersion,
    [property: JsonPropertyName("policy_instance_id")] string PolicyInstanceId,
    [property: JsonPropertyName("policy_generation")] long PolicyGeneration,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("p256_spki_sha256")] string P256SpkiSha256,
    [property: JsonPropertyName("signature_algorithm")] string SignatureAlgorithm,
    [property: JsonPropertyName("signature_format")] string SignatureFormat,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("native_stop_challenge_contract_id")] string NativeStopChallengeContractId,
    [property: JsonPropertyName("policy_id")] string PolicyId,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("activation_token_sha256")] string ActivationTokenSha256,
    [property: JsonPropertyName("rotation_epoch")] long RotationEpoch,
    [property: JsonPropertyName("valid_from")] string ValidFrom,
    [property: JsonPropertyName("valid_until")] string ValidUntil,
    [property: JsonPropertyName("revoked")] bool Revoked,
    [property: JsonPropertyName("challenge_authority_sha256")] string ChallengeAuthoritySha256);

public sealed record ReleaseBomNativeStopAuthorityTrustReceiptV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("soul_id")] string? SoulId,
    [property: JsonPropertyName("device_binding_id")] string? DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string? PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at")] string OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("receipt_id")] string ReceiptId,
    [property: JsonPropertyName("release_bom_id")] string ReleaseBomId,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("integration_commit")] string IntegrationCommit,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("activation_token_sha256")] string ActivationTokenSha256,
    [property: JsonPropertyName("trust_policy_id")] string TrustPolicyId,
    [property: JsonPropertyName("native_stop_authorities_sha256")] string NativeStopAuthoritiesSha256,
    [property: JsonPropertyName("device_route_assignment_authorities_sha256")] string DeviceRouteAssignmentAuthoritiesSha256,
    [property: JsonPropertyName("native_stop_challenge_authorities_sha256")] string NativeStopChallengeAuthoritiesSha256,
    [property: JsonPropertyName("authority_sets_sha256")] string AuthoritySetsSha256,
    [property: JsonPropertyName("native_stop_authorities")] IReadOnlyList<NativeStopAuthorityV1> NativeStopAuthorities,
    [property: JsonPropertyName("device_route_assignment_authorities")] IReadOnlyList<DeviceRouteAssignmentAuthorityV1> DeviceRouteAssignmentAuthorities,
    [property: JsonPropertyName("native_stop_challenge_authorities")] IReadOnlyList<NativeStopChallengeAuthorityV1> NativeStopChallengeAuthorities,
    [property: JsonPropertyName("signature")] ReleaseBomSignatureV1 Signature);

public sealed record ReleaseBomNativeStopAuthorityTrustPayloadV1(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    string? SoulId,
    string? DeviceBindingId,
    string? PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string OccurredAt,
    string PrivacyClass,
    string ReceiptId,
    string ReleaseBomId,
    string ReleaseBomSha256,
    string IntegrationCommit,
    long ReleaseBomGeneration,
    string ActivationTokenSha256,
    string TrustPolicyId,
    string NativeStopAuthoritiesSha256,
    string DeviceRouteAssignmentAuthoritiesSha256,
    string NativeStopChallengeAuthoritiesSha256,
    string AuthoritySetsSha256,
    IReadOnlyList<NativeStopAuthorityV1> NativeStopAuthorities,
    IReadOnlyList<DeviceRouteAssignmentAuthorityV1> DeviceRouteAssignmentAuthorities,
    IReadOnlyList<NativeStopChallengeAuthorityV1> NativeStopChallengeAuthorities);

public static partial class NativeStopAuthorityTrustProtocolV1
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "release.bom.native.stop.authority.trust/v1";
    public const string CurrentProducerModule = "factory-release-controller";
    public const string WorkerProducerModule = "windows-edge-worker";
    public const string WorkerArtifactId = "dps.windows-edge-worker";
    public const string CurrentAuthScope = "policy-approval:native-stop-proof:v2:commit-unknown";
    public const string CurrentNativeStopContractId = "native.stop.proof/v2";
    public const string CurrentPolicyId = "RESULT-VERIFY-001";
    public const string WorkerSignatureAlgorithm = "ECDSA_P256_SHA256";
    public const string WorkerSignatureFormat = "IEEE_P1363_FIXED_FIELD";
    public const string WorkerAuthorityHashDomain = "dps.native-stop-worker-authority-sha256/v2";
    public const string AuthoritiesHashDomain = "dps.native-stop-authorities-sha256/v1";
    public const string RouteAuthorityHashDomain = "dps.device-route-assignment-authority-sha256/v1";
    public const string RouteAuthoritiesHashDomain = "dps.device-route-assignment-authorities-sha256/v1";
    public const string AuthoritySetsHashDomain = "dps.release-bom-authority-sets-sha256/v1";
    public const string SupervisorProducerModule = "factory-release-controller";
    public const string SupervisorModuleId = "windows-edge-supervisor";
    public const string SupervisorArtifactId = "dps.windows-edge-supervisor";
    public const string RouteAuthScope = "windows-edge-supervisor:device-route-assignment:issue";
    public const string RoutePolicyId = "SOUL-ISO-001";
    public const string RouteSignatureAlgorithm = "ECDSA_P256_SHA256";
    public const string RouteSignatureFormat = "IEEE_P1363_FIXED_FIELD_LOW_S";
    public const string ChallengeAuthorityHashDomain = "dps.native-stop-challenge-authority-sha256/v1";
    public const string ChallengeAuthoritiesHashDomain = "dps.native-stop-challenge-authorities-sha256/v1";
    public const string ChallengeProducerModule = "policy-approval";
    public const string ChallengePolicyArtifactId = "dps.policy-approval";
    public const string ChallengeAuthScope = "policy-approval:native-stop-challenge:v1:issue";
    public const string ChallengeContractId = "native.stop.challenge/v1";
    public const string ChallengePolicyId = "NATIVE-STOP-CHALLENGE-001";
    public const string ReceiptSigningDomain = "dps-release-bom-native.stop.authority.trust/v1";
    public const int MaximumReceiptBytes = 4 * 1024 * 1024;
    public const int MaximumAuthorities = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 48,
    };

    private static readonly ReadOnlyCollection<string> ReceiptFields = Array.AsReadOnly([
        "schema_version", "contract_id", "producer_module", "soul_id",
        "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
        "occurred_at", "privacy_class", "receipt_id", "release_bom_id",
        "release_bom_sha256", "integration_commit", "release_bom_generation",
        "activation_token_sha256", "trust_policy_id", "native_stop_authorities_sha256",
        "device_route_assignment_authorities_sha256", "authority_sets_sha256",
        "native_stop_challenge_authorities_sha256", "native_stop_authorities",
        "device_route_assignment_authorities", "native_stop_challenge_authorities",
        "signature",
    ]);

    private static readonly ReadOnlyCollection<string> AuthorityFields = Array.AsReadOnly([
        "authority_id", "producer_module", "worker_module_id", "worker_artifact_id",
        "worker_artifact_sha256", "worker_version", "worker_slot", "worker_instance_id",
        "worker_generation", "key_id", "p256_spki_sha256", "signature_algorithm",
        "signature_format", "auth_scope", "native_stop_contract_id", "policy_id", "release_bom_generation",
        "activation_token_sha256", "rotation_epoch", "valid_from", "valid_until",
        "revoked", "worker_authority_sha256",
    ]);

    private static readonly ReadOnlyCollection<string> RouteAuthorityFields = Array.AsReadOnly([
        "route_authority_id", "producer_module", "supervisor_module_id",
        "supervisor_artifact_id", "supervisor_artifact_sha256", "supervisor_version",
        "supervisor_instance_id", "supervisor_generation", "route_signer_key_id",
        "route_signer_p256_spki_sha256", "signature_algorithm", "signature_format",
        "auth_scope", "policy_id", "release_bom_generation", "activation_token_sha256",
        "rotation_epoch", "valid_from", "valid_until", "revoked", "route_authority_sha256",
    ]);

    private static readonly ReadOnlyCollection<string> ChallengeAuthorityFields = Array.AsReadOnly([
        "authority_id", "producer_module", "policy_module_id", "policy_artifact_id",
        "policy_artifact_sha256", "policy_version", "policy_instance_id",
        "policy_generation", "key_id", "p256_spki_sha256", "signature_algorithm",
        "signature_format", "auth_scope", "native_stop_challenge_contract_id",
        "policy_id", "release_bom_generation", "activation_token_sha256",
        "rotation_epoch", "valid_from", "valid_until", "revoked",
        "challenge_authority_sha256",
    ]);

    private static readonly ReadOnlyCollection<string> SignatureFields =
        Array.AsReadOnly(["algorithm", "key_id", "value"]);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{7,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerOpaqueIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^wi_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerInstancePattern();

    [GeneratedRegex("^si_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SupervisorInstancePattern();

    [GeneratedRegex("^pi_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex PolicyInstancePattern();

    [GeneratedRegex("^p256_spki_[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RouteSignerKeyIdPattern();

    [GeneratedRegex("^trace_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TracePattern();

    [GeneratedRegex("^idem_[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyPattern();

    [GeneratedRegex("^native-stop-trust-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReceiptIdPattern();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneralIdPattern();

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();

    public static ReleaseBomNativeStopAuthorityTrustReceiptV1 DeserializeStrict(
        ReadOnlySpan<byte> exactUtf8Json)
    {
        if (exactUtf8Json.IsEmpty || exactUtf8Json.Length > MaximumReceiptBytes)
            throw new InvalidDataException("Native stop trust receipt exceeds its exact byte limits.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(exactUtf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 48,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Native stop trust receipt is not strict JSON.", exception);
        }

        using (document)
        {
            RequireExactObject(document.RootElement, ReceiptFields, "receipt");
            if (document.RootElement.GetProperty("native_stop_authorities").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("native_stop_authorities must be an array.");
            foreach (var authority in document.RootElement.GetProperty("native_stop_authorities").EnumerateArray())
                RequireExactObject(authority, AuthorityFields, "authority");
            if (document.RootElement.GetProperty("device_route_assignment_authorities").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("device_route_assignment_authorities must be an array.");
            foreach (var authority in document.RootElement.GetProperty("device_route_assignment_authorities").EnumerateArray())
                RequireExactObject(authority, RouteAuthorityFields, "route authority");
            if (document.RootElement.GetProperty("native_stop_challenge_authorities").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("native_stop_challenge_authorities must be an array.");
            foreach (var authority in document.RootElement.GetProperty("native_stop_challenge_authorities").EnumerateArray())
                RequireExactObject(authority, ChallengeAuthorityFields, "challenge authority");
            RequireExactObject(document.RootElement.GetProperty("signature"), SignatureFields, "signature");
            RejectDuplicateMembers(document.RootElement);
            if (!exactUtf8Json.SequenceEqual(
                    ReleaseBomAuthorityTrustVerifierV1.CanonicalizeExactJson(
                        document.RootElement)))
            {
                throw new InvalidDataException(
                    "Native stop trust receipt must be the canonical sorted compact JSON wire.");
            }
        }

        ReleaseBomNativeStopAuthorityTrustReceiptV1? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<ReleaseBomNativeStopAuthorityTrustReceiptV1>(
                exactUtf8Json, StrictJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Native stop trust receipt cannot be decoded.", exception);
        }
        if (receipt is null)
            throw new InvalidDataException("Native stop trust receipt decoded to null.");
        Validate(receipt);
        return receipt;
    }

    public static void Validate(ReleaseBomNativeStopAuthorityTrustReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RequireExact(receipt.SchemaVersion, CurrentSchemaVersion, nameof(receipt.SchemaVersion));
        RequireExact(receipt.ContractId, CurrentContractId, nameof(receipt.ContractId));
        RequireExact(receipt.ProducerModule, CurrentProducerModule, nameof(receipt.ProducerModule));
        if (receipt.SoulId is not null || receipt.DeviceBindingId is not null ||
            receipt.PlatformAccountId is not null)
            throw new InvalidDataException("A global BOM trust receipt cannot authorize a Soul, device, or account route.");
        if (!TracePattern().IsMatch(receipt.TraceId) ||
            !IdempotencyPattern().IsMatch(receipt.IdempotencyKey) ||
            !ReceiptIdPattern().IsMatch(receipt.ReceiptId) ||
            !GeneralIdPattern().IsMatch(receipt.ReleaseBomId) ||
            !CommitPattern().IsMatch(receipt.IntegrationCommit) ||
            !GeneralIdPattern().IsMatch(receipt.TrustPolicyId))
            throw new InvalidDataException("Native stop trust receipt identity is not canonical.");
        RequireSha256(receipt.ReleaseBomSha256, nameof(receipt.ReleaseBomSha256));
        RequireSha256(receipt.ActivationTokenSha256, nameof(receipt.ActivationTokenSha256));
        RequireSha256(receipt.NativeStopAuthoritiesSha256, nameof(receipt.NativeStopAuthoritiesSha256));
        RequireSha256(receipt.DeviceRouteAssignmentAuthoritiesSha256, nameof(receipt.DeviceRouteAssignmentAuthoritiesSha256));
        RequireSha256(receipt.NativeStopChallengeAuthoritiesSha256, nameof(receipt.NativeStopChallengeAuthoritiesSha256));
        RequireSha256(receipt.AuthoritySetsSha256, nameof(receipt.AuthoritySetsSha256));
        RequirePositive(receipt.ReleaseBomGeneration, nameof(receipt.ReleaseBomGeneration));
        _ = ParseCanonicalUtc(receipt.OccurredAt, nameof(receipt.OccurredAt));
        RequireExact(receipt.PrivacyClass, "internal", nameof(receipt.PrivacyClass));
        if (receipt.NativeStopAuthorities is null || receipt.NativeStopAuthorities.Count is < 1 or > MaximumAuthorities)
            throw new InvalidDataException("Native stop authority count is outside the contract limit.");
        if (receipt.DeviceRouteAssignmentAuthorities is null ||
            receipt.DeviceRouteAssignmentAuthorities.Count is < 1 or > MaximumAuthorities)
            throw new InvalidDataException("Device route authority count is outside the contract limit.");
        if (receipt.NativeStopChallengeAuthorities is null ||
            receipt.NativeStopChallengeAuthorities.Count is < 1 or > MaximumAuthorities)
            throw new InvalidDataException("Native stop challenge authority count is outside the contract limit.");

        var authorityIds = new HashSet<string>(StringComparer.Ordinal);
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        var spkiHashes = new HashSet<string>(StringComparer.Ordinal);
        NativeStopAuthorityV1? prior = null;
        foreach (var authority in receipt.NativeStopAuthorities)
        {
            ValidateAuthority(authority, receipt.ReleaseBomGeneration, receipt.ActivationTokenSha256);
            if (!authorityIds.Add(authority.AuthorityId) || !keyIds.Add(authority.KeyId) ||
                !spkiHashes.Add(authority.P256SpkiSha256))
                throw new InvalidDataException("Authority id, Worker key id, and P-256 SPKI must be one-to-one.");
            if (prior is not null && CompareAuthorities(prior, authority) >= 0)
                throw new InvalidDataException("Native stop authorities are not in canonical order.");
            prior = authority;
        }
        if (!FixedShaEquals(receipt.NativeStopAuthoritiesSha256, ComputeAuthoritiesSha256(receipt.NativeStopAuthorities)))
            throw new InvalidDataException("Native stop authority set digest is invalid.");
        var routeAuthorityIds = new HashSet<string>(StringComparer.Ordinal);
        var routeKeyIds = new HashSet<string>(StringComparer.Ordinal);
        var routeSpkiHashes = new HashSet<string>(StringComparer.Ordinal);
        DeviceRouteAssignmentAuthorityV1? priorRoute = null;
        foreach (var authority in receipt.DeviceRouteAssignmentAuthorities)
        {
            ValidateRouteAuthority(authority, receipt.ReleaseBomGeneration, receipt.ActivationTokenSha256);
            if (!routeAuthorityIds.Add(authority.RouteAuthorityId) ||
                !routeKeyIds.Add(authority.RouteSignerKeyId) ||
                !routeSpkiHashes.Add(authority.RouteSignerP256SpkiSha256))
                throw new InvalidDataException("Route authority id, key id, and P-256 SPKI must be one-to-one.");
            if (keyIds.Contains(authority.RouteSignerKeyId) ||
                spkiHashes.Contains(authority.RouteSignerP256SpkiSha256))
                throw new InvalidDataException("Route and native stop P-256 keys must be pairwise distinct.");
            if (priorRoute is not null && CompareRouteAuthorities(priorRoute, authority) >= 0)
                throw new InvalidDataException("Device route authorities are not in canonical order.");
            priorRoute = authority;
        }
        if (!FixedShaEquals(
                receipt.DeviceRouteAssignmentAuthoritiesSha256,
                ComputeRouteAuthoritiesSha256(receipt.DeviceRouteAssignmentAuthorities)))
            throw new InvalidDataException("Device route authority set digest is invalid.");
        var challengeAuthorityIds = new HashSet<string>(StringComparer.Ordinal);
        var challengeKeyIds = new HashSet<string>(StringComparer.Ordinal);
        var challengeSpkiHashes = new HashSet<string>(StringComparer.Ordinal);
        NativeStopChallengeAuthorityV1? priorChallenge = null;
        foreach (var authority in receipt.NativeStopChallengeAuthorities)
        {
            ValidateChallengeAuthority(authority, receipt.ReleaseBomGeneration, receipt.ActivationTokenSha256);
            if (!challengeAuthorityIds.Add(authority.AuthorityId) ||
                !challengeKeyIds.Add(authority.KeyId) ||
                !challengeSpkiHashes.Add(authority.P256SpkiSha256))
                throw new InvalidDataException("Challenge authority id, key id, and P-256 SPKI must be one-to-one.");
            if (keyIds.Contains(authority.KeyId) || routeKeyIds.Contains(authority.KeyId) ||
                spkiHashes.Contains(authority.P256SpkiSha256) ||
                routeSpkiHashes.Contains(authority.P256SpkiSha256))
                throw new InvalidDataException("Challenge, route, and native stop P-256 keys must be pairwise distinct.");
            if (priorChallenge is not null && CompareChallengeAuthorities(priorChallenge, authority) >= 0)
                throw new InvalidDataException("Challenge authorities are not in canonical order.");
            priorChallenge = authority;
        }
        if (!FixedShaEquals(
                receipt.NativeStopChallengeAuthoritiesSha256,
                ComputeChallengeAuthoritiesSha256(receipt.NativeStopChallengeAuthorities)))
            throw new InvalidDataException("Challenge authority set digest is invalid.");
        if (!FixedShaEquals(
                receipt.AuthoritySetsSha256,
                ComputeAuthoritySetsSha256(
                    receipt.NativeStopAuthoritiesSha256,
                    receipt.DeviceRouteAssignmentAuthoritiesSha256,
                    receipt.NativeStopChallengeAuthoritiesSha256)))
            throw new InvalidDataException("Combined authority-set digest is invalid.");
        if (receipt.Signature is null)
            throw new InvalidDataException("Native stop trust receipt signature is absent.");
        RequireExact(receipt.Signature.Algorithm, "rsa-pss-sha256", nameof(receipt.Signature.Algorithm));
        if (!KeyIdPattern().IsMatch(receipt.Signature.KeyId) || string.IsNullOrWhiteSpace(receipt.Signature.Value))
            throw new InvalidDataException("Native stop trust receipt signature shape is invalid.");
        if (keyIds.Contains(receipt.Signature.KeyId) || routeKeyIds.Contains(receipt.Signature.KeyId) ||
            challengeKeyIds.Contains(receipt.Signature.KeyId))
            throw new InvalidDataException("Receipt signer key cannot be a runtime authority key.");
    }

    public static string ComputeWorkerAuthoritySha256(NativeStopAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        using var writer = new CanonicalWriter();
        writer.Field(WorkerAuthorityHashDomain);
        writer.Field(authority.AuthorityId);
        writer.Field(authority.ProducerModule);
        writer.Field(authority.WorkerModuleId);
        writer.Field(authority.WorkerArtifactId);
        writer.Field(authority.WorkerArtifactSha256);
        writer.Field(authority.WorkerVersion);
        writer.Field(authority.WorkerSlot);
        writer.Field(authority.WorkerInstanceId);
        writer.Field(authority.WorkerGeneration);
        writer.Field(authority.KeyId);
        writer.Field(authority.P256SpkiSha256);
        writer.Field(authority.SignatureAlgorithm);
        writer.Field(authority.SignatureFormat);
        writer.Field(authority.AuthScope);
        writer.Field(authority.NativeStopContractId);
        writer.Field(authority.PolicyId);
        writer.Field(authority.ReleaseBomGeneration);
        writer.Field(authority.ActivationTokenSha256);
        writer.Field(authority.RotationEpoch);
        writer.Field(authority.ValidFrom);
        writer.Field(authority.ValidUntil);
        writer.Field(authority.Revoked ? "true" : "false");
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    public static string ComputeAuthoritiesSha256(IReadOnlyList<NativeStopAuthorityV1> authorities)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        using var writer = new CanonicalWriter();
        writer.Field(AuthoritiesHashDomain);
        writer.Field(authorities.Count);
        foreach (var authority in authorities)
            writer.Field(authority.WorkerAuthoritySha256);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    public static string ComputeRouteAuthoritySha256(DeviceRouteAssignmentAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        using var writer = new CanonicalWriter();
        writer.Field(RouteAuthorityHashDomain);
        writer.Field(authority.RouteAuthorityId);
        writer.Field(authority.ProducerModule);
        writer.Field(authority.SupervisorModuleId);
        writer.Field(authority.SupervisorArtifactId);
        writer.Field(authority.SupervisorArtifactSha256);
        writer.Field(authority.SupervisorVersion);
        writer.Field(authority.SupervisorInstanceId);
        writer.Field(authority.SupervisorGeneration);
        writer.Field(authority.RouteSignerKeyId);
        writer.Field(authority.RouteSignerP256SpkiSha256);
        writer.Field(authority.SignatureAlgorithm);
        writer.Field(authority.SignatureFormat);
        writer.Field(authority.AuthScope);
        writer.Field(authority.PolicyId);
        writer.Field(authority.ReleaseBomGeneration);
        writer.Field(authority.ActivationTokenSha256);
        writer.Field(authority.RotationEpoch);
        writer.Field(authority.ValidFrom);
        writer.Field(authority.ValidUntil);
        writer.Field(authority.Revoked ? "true" : "false");
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    public static string ComputeRouteAuthoritiesSha256(
        IReadOnlyList<DeviceRouteAssignmentAuthorityV1> authorities)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        using var writer = new CanonicalWriter();
        writer.Field(RouteAuthoritiesHashDomain);
        writer.Field(authorities.Count);
        foreach (var authority in authorities)
            writer.Field(authority.RouteAuthoritySha256);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    public static string ComputeChallengeAuthoritySha256(NativeStopChallengeAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        using var writer = new CanonicalWriter();
        writer.Field(ChallengeAuthorityHashDomain);
        writer.Field(authority.AuthorityId);
        writer.Field(authority.ProducerModule);
        writer.Field(authority.PolicyModuleId);
        writer.Field(authority.PolicyArtifactId);
        writer.Field(authority.PolicyArtifactSha256);
        writer.Field(authority.PolicyVersion);
        writer.Field(authority.PolicyInstanceId);
        writer.Field(authority.PolicyGeneration);
        writer.Field(authority.KeyId);
        writer.Field(authority.P256SpkiSha256);
        writer.Field(authority.SignatureAlgorithm);
        writer.Field(authority.SignatureFormat);
        writer.Field(authority.AuthScope);
        writer.Field(authority.NativeStopChallengeContractId);
        writer.Field(authority.PolicyId);
        writer.Field(authority.ReleaseBomGeneration);
        writer.Field(authority.ActivationTokenSha256);
        writer.Field(authority.RotationEpoch);
        writer.Field(authority.ValidFrom);
        writer.Field(authority.ValidUntil);
        writer.Field(authority.Revoked ? "true" : "false");
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    public static string ComputeChallengeAuthoritiesSha256(
        IReadOnlyList<NativeStopChallengeAuthorityV1> authorities)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        using var writer = new CanonicalWriter();
        writer.Field(ChallengeAuthoritiesHashDomain);
        writer.Field(authorities.Count);
        foreach (var authority in authorities)
            writer.Field(authority.ChallengeAuthoritySha256);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    public static string ComputeAuthoritySetsSha256(
        string nativeStopAuthoritiesSha256,
        string deviceRouteAssignmentAuthoritiesSha256,
        string nativeStopChallengeAuthoritiesSha256)
    {
        RequireSha256(nativeStopAuthoritiesSha256, nameof(nativeStopAuthoritiesSha256));
        RequireSha256(deviceRouteAssignmentAuthoritiesSha256, nameof(deviceRouteAssignmentAuthoritiesSha256));
        RequireSha256(nativeStopChallengeAuthoritiesSha256, nameof(nativeStopChallengeAuthoritiesSha256));
        using var writer = new CanonicalWriter();
        writer.Field(AuthoritySetsHashDomain);
        writer.Field(nativeStopAuthoritiesSha256);
        writer.Field(deviceRouteAssignmentAuthoritiesSha256);
        writer.Field(nativeStopChallengeAuthoritiesSha256);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    public static byte[] CanonicalReceiptSigningBytes(
        ReleaseBomNativeStopAuthorityTrustReceiptV1 receipt)
    {
        Validate(receipt);
        return CanonicalReceiptSigningBytes(new ReleaseBomNativeStopAuthorityTrustPayloadV1(
            receipt.SchemaVersion, receipt.ContractId, receipt.ProducerModule,
            receipt.SoulId, receipt.DeviceBindingId, receipt.PlatformAccountId,
            receipt.TraceId, receipt.IdempotencyKey, receipt.OccurredAt,
            receipt.PrivacyClass, receipt.ReceiptId, receipt.ReleaseBomId,
            receipt.ReleaseBomSha256, receipt.IntegrationCommit,
            receipt.ReleaseBomGeneration, receipt.ActivationTokenSha256,
            receipt.TrustPolicyId, receipt.NativeStopAuthoritiesSha256,
            receipt.DeviceRouteAssignmentAuthoritiesSha256,
            receipt.NativeStopChallengeAuthoritiesSha256, receipt.AuthoritySetsSha256,
            receipt.NativeStopAuthorities, receipt.DeviceRouteAssignmentAuthorities,
            receipt.NativeStopChallengeAuthorities));
    }

    public static byte[] CanonicalReceiptSigningBytes(
        ReleaseBomNativeStopAuthorityTrustPayloadV1 payload)
    {
        ValidatePayload(payload);
        using var writer = new CanonicalWriter();
        writer.Field(ReceiptSigningDomain);
        writer.Field(payload.SchemaVersion);
        writer.Field(payload.ContractId);
        writer.Field(payload.ProducerModule);
        writer.Field(payload.SoulId ?? string.Empty);
        writer.Field(payload.DeviceBindingId ?? string.Empty);
        writer.Field(payload.PlatformAccountId ?? string.Empty);
        writer.Field(payload.TraceId);
        writer.Field(payload.IdempotencyKey);
        writer.Field(payload.OccurredAt);
        writer.Field(payload.PrivacyClass);
        writer.Field(payload.ReceiptId);
        writer.Field(payload.ReleaseBomId);
        writer.Field(payload.ReleaseBomSha256);
        writer.Field(payload.IntegrationCommit);
        writer.Field(payload.ReleaseBomGeneration);
        writer.Field(payload.ActivationTokenSha256);
        writer.Field(payload.TrustPolicyId);
        writer.Field(payload.NativeStopAuthoritiesSha256);
        writer.Field(payload.DeviceRouteAssignmentAuthoritiesSha256);
        writer.Field(payload.NativeStopChallengeAuthoritiesSha256);
        writer.Field(payload.AuthoritySetsSha256);
        return writer.ToArray();
    }

    public static void RequirePinnedP256Spki(
        ReadOnlySpan<byte> rawSubjectPublicKeyInfo,
        NativeStopAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (rawSubjectPublicKeyInfo.IsEmpty || rawSubjectPublicKeyInfo.Length > 2048)
            throw new InvalidDataException("Worker P-256 SPKI is absent or oversized.");
        using var verifier = ECDsa.Create();
        byte[] canonicalSpki;
        try
        {
            verifier.ImportSubjectPublicKeyInfo(rawSubjectPublicKeyInfo, out var bytesRead);
            var parameters = verifier.ExportParameters(false);
            canonicalSpki = verifier.ExportSubjectPublicKeyInfo();
            if (bytesRead != rawSubjectPublicKeyInfo.Length || verifier.KeySize != 256 ||
                !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal) ||
                !rawSubjectPublicKeyInfo.SequenceEqual(canonicalSpki))
                throw new InvalidDataException("Worker key bundle entry is not an exact P-256 SPKI.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Worker key bundle entry is not a valid P-256 SPKI.", exception);
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(canonicalSpki));
        if (!FixedShaEquals(digest, authority.P256SpkiSha256))
            throw new InvalidDataException("Worker key bundle SPKI does not match the signed BOM authority pin.");
    }

    public static void RequirePinnedRouteP256Spki(
        ReadOnlySpan<byte> rawSubjectPublicKeyInfo,
        DeviceRouteAssignmentAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (rawSubjectPublicKeyInfo.IsEmpty || rawSubjectPublicKeyInfo.Length > 2048)
            throw new InvalidDataException("Route signer P-256 SPKI is absent or oversized.");
        using var verifier = ECDsa.Create();
        byte[] canonicalSpki;
        try
        {
            verifier.ImportSubjectPublicKeyInfo(rawSubjectPublicKeyInfo, out var bytesRead);
            var parameters = verifier.ExportParameters(false);
            canonicalSpki = verifier.ExportSubjectPublicKeyInfo();
            if (bytesRead != rawSubjectPublicKeyInfo.Length || verifier.KeySize != 256 ||
                !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal) ||
                !rawSubjectPublicKeyInfo.SequenceEqual(canonicalSpki))
                throw new InvalidDataException("Route key bundle entry is not an exact canonical P-256 SPKI.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Route key bundle entry is not a valid P-256 SPKI.", exception);
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(canonicalSpki));
        if (!FixedShaEquals(digest, authority.RouteSignerP256SpkiSha256) ||
            !string.Equals(authority.RouteSignerKeyId, "p256_spki_" + digest, StringComparison.Ordinal))
            throw new InvalidDataException("Route key bundle SPKI does not match the signed BOM route authority pin.");
    }

    public static void RequirePinnedRouteP256Spki(
        ReadOnlySpan<byte> rawSubjectPublicKeyInfo,
        VerifiedDeviceRouteAssignmentAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        RequirePinnedRouteP256Spki(rawSubjectPublicKeyInfo, authority.Authority);
    }

    public static void RequirePinnedP256Spki(
        ReadOnlySpan<byte> rawSubjectPublicKeyInfo,
        VerifiedNativeStopAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        RequirePinnedP256Spki(rawSubjectPublicKeyInfo, authority.Authority);
    }

    public static void RequirePinnedChallengeP256Spki(
        ReadOnlySpan<byte> rawSubjectPublicKeyInfo,
        NativeStopChallengeAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (rawSubjectPublicKeyInfo.IsEmpty || rawSubjectPublicKeyInfo.Length > 2048)
            throw new InvalidDataException("Challenge signer P-256 SPKI is absent or oversized.");
        using var verifier = ECDsa.Create();
        byte[] canonicalSpki;
        try
        {
            verifier.ImportSubjectPublicKeyInfo(rawSubjectPublicKeyInfo, out var bytesRead);
            var parameters = verifier.ExportParameters(false);
            canonicalSpki = verifier.ExportSubjectPublicKeyInfo();
            if (bytesRead != rawSubjectPublicKeyInfo.Length || verifier.KeySize != 256 ||
                !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal) || !rawSubjectPublicKeyInfo.SequenceEqual(canonicalSpki))
                throw new InvalidDataException("Challenge key is not exact canonical P-256 SPKI.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Challenge key is not valid P-256 SPKI.", exception);
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(canonicalSpki));
        if (!FixedShaEquals(digest, authority.P256SpkiSha256))
            throw new InvalidDataException("Challenge key SPKI does not match its signed BOM authority pin.");
    }

    public static void RequirePinnedChallengeP256Spki(
        ReadOnlySpan<byte> rawSubjectPublicKeyInfo,
        VerifiedNativeStopChallengeAuthorityV1 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        RequirePinnedChallengeP256Spki(rawSubjectPublicKeyInfo, authority.Authority);
    }

    private static void ValidatePayload(ReleaseBomNativeStopAuthorityTrustPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Validate(new ReleaseBomNativeStopAuthorityTrustReceiptV1(
            payload.SchemaVersion, payload.ContractId, payload.ProducerModule,
            payload.SoulId, payload.DeviceBindingId, payload.PlatformAccountId,
            payload.TraceId, payload.IdempotencyKey, payload.OccurredAt,
            payload.PrivacyClass, payload.ReceiptId, payload.ReleaseBomId,
            payload.ReleaseBomSha256, payload.IntegrationCommit,
            payload.ReleaseBomGeneration, payload.ActivationTokenSha256,
            payload.TrustPolicyId, payload.NativeStopAuthoritiesSha256,
            payload.DeviceRouteAssignmentAuthoritiesSha256,
            payload.NativeStopChallengeAuthoritiesSha256, payload.AuthoritySetsSha256,
            payload.NativeStopAuthorities, payload.DeviceRouteAssignmentAuthorities,
            payload.NativeStopChallengeAuthorities,
            new ReleaseBomSignatureV1("rsa-pss-sha256", "external-trust-signer", "unsigned")));
    }

    private static void ValidateAuthority(
        NativeStopAuthorityV1 authority,
        long expectedBomGeneration,
        string expectedActivationTokenSha256)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!LowerOpaqueIdPattern().IsMatch(authority.AuthorityId) ||
            !KeyIdPattern().IsMatch(authority.KeyId) ||
            !SemVerPattern().IsMatch(authority.WorkerVersion) ||
            !WorkerInstancePattern().IsMatch(authority.WorkerInstanceId))
            throw new InvalidDataException("Native stop authority identifier or Worker version is invalid.");
        RequireExact(authority.ProducerModule, WorkerProducerModule, nameof(authority.ProducerModule));
        RequireExact(authority.WorkerModuleId, WorkerProducerModule, nameof(authority.WorkerModuleId));
        RequireExact(authority.WorkerArtifactId, WorkerArtifactId, nameof(authority.WorkerArtifactId));
        RequireExact(authority.SignatureAlgorithm, WorkerSignatureAlgorithm, nameof(authority.SignatureAlgorithm));
        RequireExact(authority.SignatureFormat, WorkerSignatureFormat, nameof(authority.SignatureFormat));
        RequireExact(authority.AuthScope, CurrentAuthScope, nameof(authority.AuthScope));
        RequireExact(authority.NativeStopContractId, CurrentNativeStopContractId, nameof(authority.NativeStopContractId));
        RequireExact(authority.PolicyId, CurrentPolicyId, nameof(authority.PolicyId));
        if (authority.WorkerSlot is not ("A" or "B"))
            throw new InvalidDataException("Worker slot must be A or B.");
        RequireSha256(authority.WorkerArtifactSha256, nameof(authority.WorkerArtifactSha256));
        RequireSha256(authority.P256SpkiSha256, nameof(authority.P256SpkiSha256));
        RequireSha256(authority.ActivationTokenSha256, nameof(authority.ActivationTokenSha256));
        RequireSha256(authority.WorkerAuthoritySha256, nameof(authority.WorkerAuthoritySha256));
        RequirePositive(authority.WorkerGeneration, nameof(authority.WorkerGeneration));
        RequirePositive(authority.ReleaseBomGeneration, nameof(authority.ReleaseBomGeneration));
        RequirePositive(authority.RotationEpoch, nameof(authority.RotationEpoch));
        if (authority.ReleaseBomGeneration != expectedBomGeneration ||
            !FixedShaEquals(authority.ActivationTokenSha256, expectedActivationTokenSha256))
            throw new InvalidDataException("Authority does not bind the receipt BOM generation and activation token.");
        if (authority.Revoked)
            throw new InvalidDataException("A revoked Worker key cannot authorize native stop proof.");
        var validFrom = ParseCanonicalUtc(authority.ValidFrom, nameof(authority.ValidFrom));
        var validUntil = ParseCanonicalUtc(authority.ValidUntil, nameof(authority.ValidUntil));
        if (validFrom >= validUntil)
            throw new InvalidDataException("Authority validity window is empty or reversed.");
        if (validUntil - validFrom > TimeSpan.FromDays(31))
            throw new InvalidDataException("Authority validity exceeds the 31-day policy.");
        if (!FixedShaEquals(authority.WorkerAuthoritySha256, ComputeWorkerAuthoritySha256(authority)))
            throw new InvalidDataException("Worker authority digest is invalid.");
    }

    private static void ValidateRouteAuthority(
        DeviceRouteAssignmentAuthorityV1 authority,
        long expectedBomGeneration,
        string expectedActivationTokenSha256)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!LowerOpaqueIdPattern().IsMatch(authority.RouteAuthorityId) ||
            !SemVerPattern().IsMatch(authority.SupervisorVersion) ||
            !SupervisorInstancePattern().IsMatch(authority.SupervisorInstanceId) ||
            !RouteSignerKeyIdPattern().IsMatch(authority.RouteSignerKeyId))
            throw new InvalidDataException("Device route authority identifier or Supervisor version is invalid.");
        RequireExact(authority.ProducerModule, SupervisorProducerModule, nameof(authority.ProducerModule));
        RequireExact(authority.SupervisorModuleId, SupervisorModuleId, nameof(authority.SupervisorModuleId));
        RequireExact(authority.SupervisorArtifactId, SupervisorArtifactId, nameof(authority.SupervisorArtifactId));
        RequireExact(authority.SignatureAlgorithm, RouteSignatureAlgorithm, nameof(authority.SignatureAlgorithm));
        RequireExact(authority.SignatureFormat, RouteSignatureFormat, nameof(authority.SignatureFormat));
        RequireExact(authority.AuthScope, RouteAuthScope, nameof(authority.AuthScope));
        RequireExact(authority.PolicyId, RoutePolicyId, nameof(authority.PolicyId));
        RequireSha256(authority.SupervisorArtifactSha256, nameof(authority.SupervisorArtifactSha256));
        RequireSha256(authority.RouteSignerP256SpkiSha256, nameof(authority.RouteSignerP256SpkiSha256));
        RequireSha256(authority.ActivationTokenSha256, nameof(authority.ActivationTokenSha256));
        RequireSha256(authority.RouteAuthoritySha256, nameof(authority.RouteAuthoritySha256));
        RequirePositive(authority.SupervisorGeneration, nameof(authority.SupervisorGeneration));
        RequirePositive(authority.ReleaseBomGeneration, nameof(authority.ReleaseBomGeneration));
        RequirePositive(authority.RotationEpoch, nameof(authority.RotationEpoch));
        if (!string.Equals(
                authority.RouteSignerKeyId,
                "p256_spki_" + authority.RouteSignerP256SpkiSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("Route signer key id must be its canonical DER SPKI SHA-256.");
        if (authority.ReleaseBomGeneration != expectedBomGeneration ||
            !FixedShaEquals(authority.ActivationTokenSha256, expectedActivationTokenSha256))
            throw new InvalidDataException("Route authority does not bind the receipt BOM generation and token.");
        if (authority.Revoked)
            throw new InvalidDataException("A revoked route signer cannot issue device route assignments.");
        var validFrom = ParseCanonicalUtc(authority.ValidFrom, nameof(authority.ValidFrom));
        var validUntil = ParseCanonicalUtc(authority.ValidUntil, nameof(authority.ValidUntil));
        if (validFrom >= validUntil)
            throw new InvalidDataException("Route authority validity window is empty or reversed.");
        if (validUntil - validFrom > TimeSpan.FromDays(31))
            throw new InvalidDataException("Route authority validity exceeds the 31-day policy.");
        if (!FixedShaEquals(authority.RouteAuthoritySha256, ComputeRouteAuthoritySha256(authority)))
            throw new InvalidDataException("Route authority digest is invalid.");
    }

    private static void ValidateChallengeAuthority(
        NativeStopChallengeAuthorityV1 authority,
        long expectedBomGeneration,
        string expectedActivationTokenSha256)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!LowerOpaqueIdPattern().IsMatch(authority.AuthorityId) ||
            !KeyIdPattern().IsMatch(authority.KeyId) ||
            !SemVerPattern().IsMatch(authority.PolicyVersion) ||
            !PolicyInstancePattern().IsMatch(authority.PolicyInstanceId))
            throw new InvalidDataException("Challenge authority identifier or Policy version is invalid.");
        RequireExact(authority.ProducerModule, ChallengeProducerModule, nameof(authority.ProducerModule));
        RequireExact(authority.PolicyModuleId, ChallengeProducerModule, nameof(authority.PolicyModuleId));
        RequireExact(authority.PolicyArtifactId, ChallengePolicyArtifactId, nameof(authority.PolicyArtifactId));
        RequireExact(authority.SignatureAlgorithm, RouteSignatureAlgorithm, nameof(authority.SignatureAlgorithm));
        RequireExact(authority.SignatureFormat, RouteSignatureFormat, nameof(authority.SignatureFormat));
        RequireExact(authority.AuthScope, ChallengeAuthScope, nameof(authority.AuthScope));
        RequireExact(authority.NativeStopChallengeContractId, ChallengeContractId, nameof(authority.NativeStopChallengeContractId));
        RequireExact(authority.PolicyId, ChallengePolicyId, nameof(authority.PolicyId));
        RequireSha256(authority.PolicyArtifactSha256, nameof(authority.PolicyArtifactSha256));
        RequireSha256(authority.P256SpkiSha256, nameof(authority.P256SpkiSha256));
        RequireSha256(authority.ActivationTokenSha256, nameof(authority.ActivationTokenSha256));
        RequireSha256(authority.ChallengeAuthoritySha256, nameof(authority.ChallengeAuthoritySha256));
        RequirePositive(authority.PolicyGeneration, nameof(authority.PolicyGeneration));
        RequirePositive(authority.ReleaseBomGeneration, nameof(authority.ReleaseBomGeneration));
        RequirePositive(authority.RotationEpoch, nameof(authority.RotationEpoch));
        if (authority.ReleaseBomGeneration != expectedBomGeneration ||
            !FixedShaEquals(authority.ActivationTokenSha256, expectedActivationTokenSha256))
            throw new InvalidDataException("Challenge authority does not bind the receipt BOM generation and token.");
        if (authority.Revoked)
            throw new InvalidDataException("A revoked challenge signer cannot issue native stop challenges.");
        var validFrom = ParseCanonicalUtc(authority.ValidFrom, nameof(authority.ValidFrom));
        var validUntil = ParseCanonicalUtc(authority.ValidUntil, nameof(authority.ValidUntil));
        if (validFrom >= validUntil || validUntil - validFrom > TimeSpan.FromDays(31))
            throw new InvalidDataException("Challenge authority validity is reversed or exceeds 31 days.");
        if (!FixedShaEquals(authority.ChallengeAuthoritySha256, ComputeChallengeAuthoritySha256(authority)))
            throw new InvalidDataException("Challenge authority digest is invalid.");
    }

    private static int CompareAuthorities(NativeStopAuthorityV1 left, NativeStopAuthorityV1 right)
    {
        var comparisons = new[]
        {
            string.CompareOrdinal(left.WorkerModuleId, right.WorkerModuleId),
            string.CompareOrdinal(left.WorkerArtifactSha256, right.WorkerArtifactSha256),
            string.CompareOrdinal(left.WorkerSlot, right.WorkerSlot),
            string.CompareOrdinal(left.WorkerInstanceId, right.WorkerInstanceId),
            left.WorkerGeneration.CompareTo(right.WorkerGeneration),
            left.RotationEpoch.CompareTo(right.RotationEpoch),
            string.CompareOrdinal(left.AuthorityId, right.AuthorityId),
        };
        return comparisons.FirstOrDefault(value => value != 0);
    }

    private static int CompareRouteAuthorities(
        DeviceRouteAssignmentAuthorityV1 left,
        DeviceRouteAssignmentAuthorityV1 right)
    {
        var comparisons = new[]
        {
            string.CompareOrdinal(left.SupervisorModuleId, right.SupervisorModuleId),
            string.CompareOrdinal(left.SupervisorArtifactSha256, right.SupervisorArtifactSha256),
            string.CompareOrdinal(left.SupervisorInstanceId, right.SupervisorInstanceId),
            left.SupervisorGeneration.CompareTo(right.SupervisorGeneration),
            left.RotationEpoch.CompareTo(right.RotationEpoch),
            string.CompareOrdinal(left.RouteAuthorityId, right.RouteAuthorityId),
        };
        return comparisons.FirstOrDefault(value => value != 0);
    }

    private static int CompareChallengeAuthorities(
        NativeStopChallengeAuthorityV1 left,
        NativeStopChallengeAuthorityV1 right)
    {
        var comparisons = new[]
        {
            string.CompareOrdinal(left.PolicyModuleId, right.PolicyModuleId),
            string.CompareOrdinal(left.PolicyArtifactSha256, right.PolicyArtifactSha256),
            string.CompareOrdinal(left.PolicyInstanceId, right.PolicyInstanceId),
            left.PolicyGeneration.CompareTo(right.PolicyGeneration),
            left.RotationEpoch.CompareTo(right.RotationEpoch),
            string.CompareOrdinal(left.AuthorityId, right.AuthorityId),
        };
        return comparisons.FirstOrDefault(value => value != 0);
    }

    private static DateTimeOffset ParseCanonicalUtc(string value, string name)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) || parsed.Offset != TimeSpan.Zero ||
            !string.Equals(parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
                value, StringComparison.Ordinal) ||
            parsed < new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
            throw new InvalidDataException($"{name} is not canonical UTC with seven fractional digits.");
        return parsed;
    }

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate JSON property '{property.Name}' is forbidden.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateMembers(item);
        }
    }

    private static void RequireExactObject(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} must be an object.");
        var actual = element.EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw new InvalidDataException($"{label} properties do not match the exact contract.");
    }

    private static void RequireSha256(string value, string name)
    {
        if (!Sha256Pattern().IsMatch(value))
            throw new InvalidDataException($"{name} is not lowercase SHA-256.");
    }

    private static void RequirePositive(long value, string name)
    {
        if (value < 1)
            throw new InvalidDataException($"{name} must be positive.");
    }

    private static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"{name} is not the supported contract constant.");
    }

    private static bool FixedShaEquals(string left, string right)
    {
        if (!Sha256Pattern().IsMatch(left) || !Sha256Pattern().IsMatch(right))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private sealed class CanonicalWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public void Field(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = StrictUtf8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
                _stream.Write(length);
                _stream.Write(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void Field(long value) => Field(value.ToString(CultureInfo.InvariantCulture));
        public byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}

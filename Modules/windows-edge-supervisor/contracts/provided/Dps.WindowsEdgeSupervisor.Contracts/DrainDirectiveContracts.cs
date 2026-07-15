using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.WindowsEdgeSupervisor.Contracts;

public sealed record DrainDirectiveClaimsV1(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string OccurredAt,
    string PrivacyClass,
    string DrainId,
    string Slot,
    string WorkerVersion,
    string WorkerArtifactSha256,
    string JournalArtifactSha256,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    long RoutingEpoch,
    string IssuedAt,
    string NotBefore,
    string ExpiresAt,
    string SupervisorKeyId,
    string SignatureAlgorithm);

public sealed record DrainDirectiveExpectationV1(
    string DrainId,
    string Slot,
    string WorkerVersion,
    string WorkerArtifactSha256,
    string JournalArtifactSha256,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    long RoutingEpoch,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string OccurredAt);

public sealed record SignedDrainDirectiveV1
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
    [JsonPropertyName("drain_id"), JsonRequired] public required string DrainId { get; init; }
    [JsonPropertyName("slot"), JsonRequired] public required string Slot { get; init; }
    [JsonPropertyName("worker_version"), JsonRequired] public required string WorkerVersion { get; init; }
    [JsonPropertyName("worker_artifact_sha256"), JsonRequired] public required string WorkerArtifactSha256 { get; init; }
    [JsonPropertyName("journal_artifact_sha256"), JsonRequired] public required string JournalArtifactSha256 { get; init; }
    [JsonPropertyName("release_bom_sha256"), JsonRequired] public required string ReleaseBomSha256 { get; init; }
    [JsonPropertyName("protected_policy_sha256"), JsonRequired] public required string ProtectedPolicySha256 { get; init; }
    [JsonPropertyName("routing_epoch"), JsonRequired] public long RoutingEpoch { get; init; }
    [JsonPropertyName("issued_at"), JsonRequired] public required string IssuedAt { get; init; }
    [JsonPropertyName("not_before"), JsonRequired] public required string NotBefore { get; init; }
    [JsonPropertyName("expires_at"), JsonRequired] public required string ExpiresAt { get; init; }
    [JsonPropertyName("supervisor_key_id"), JsonRequired] public required string SupervisorKeyId { get; init; }
    [JsonPropertyName("signature_algorithm"), JsonRequired] public required string SignatureAlgorithm { get; init; }
    [JsonPropertyName("signature"), JsonRequired] public required string Signature { get; init; }
}

public sealed class VerifiedDrainDirectiveV1
{
    internal VerifiedDrainDirectiveV1(
        SignedDrainDirectiveV1 envelope,
        string wireSha256,
        string statementSha256)
    {
        Envelope = envelope;
        WireSha256 = wireSha256;
        StatementSha256 = statementSha256;
    }

    public SignedDrainDirectiveV1 Envelope { get; }
    public string WireSha256 { get; }
    public string StatementSha256 { get; }
}

public static class DrainDirectiveV1Codec
{
    public const string SchemaVersion = "1.0";
    public const string ContractId = "edge.worker.drain.directive/v1";
    public const string ProducerModule = "windows-edge-supervisor";
    public const string SignatureAlgorithm = "RSA_PSS_SHA256";
    public const string StatementDomain = "dps.windows-edge-supervisor.drain-directive/v1";
    public const int MaximumWireBytes = 32 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex CanonicalUtc = new(
        "^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-5][0-9]:[0-5][0-9]\\.[0-9]{7}\\+00:00\\z",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 8
    };

    public static byte[] CreateSigningStatement(DrainDirectiveClaimsV1 claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ValidateClaims(claims);
        return Frame(StatementDomain,
        [
            claims.SchemaVersion, claims.ContractId, claims.ProducerModule,
            claims.SoulId, claims.DeviceBindingId, claims.PlatformAccountId,
            claims.TraceId, claims.IdempotencyKey, claims.OccurredAt, claims.PrivacyClass,
            claims.DrainId, claims.Slot, claims.WorkerVersion, claims.WorkerArtifactSha256,
            claims.JournalArtifactSha256, claims.ReleaseBomSha256, claims.ProtectedPolicySha256,
            claims.RoutingEpoch.ToString(CultureInfo.InvariantCulture), claims.IssuedAt,
            claims.NotBefore, claims.ExpiresAt, claims.SupervisorKeyId, claims.SignatureAlgorithm
        ]);
    }

    public static SignedDrainDirectiveV1 AttachSignature(
        DrainDirectiveClaimsV1 claims,
        string signatureBase64)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ValidateClaims(claims);
        RequireCanonicalBase64(signatureBase64, "signature");
        return new SignedDrainDirectiveV1
        {
            SchemaVersion = claims.SchemaVersion,
            ContractId = claims.ContractId,
            ProducerModule = claims.ProducerModule,
            SoulId = claims.SoulId,
            DeviceBindingId = claims.DeviceBindingId,
            PlatformAccountId = claims.PlatformAccountId,
            TraceId = claims.TraceId,
            IdempotencyKey = claims.IdempotencyKey,
            OccurredAt = claims.OccurredAt,
            PrivacyClass = claims.PrivacyClass,
            DrainId = claims.DrainId,
            Slot = claims.Slot,
            WorkerVersion = claims.WorkerVersion,
            WorkerArtifactSha256 = claims.WorkerArtifactSha256,
            JournalArtifactSha256 = claims.JournalArtifactSha256,
            ReleaseBomSha256 = claims.ReleaseBomSha256,
            ProtectedPolicySha256 = claims.ProtectedPolicySha256,
            RoutingEpoch = claims.RoutingEpoch,
            IssuedAt = claims.IssuedAt,
            NotBefore = claims.NotBefore,
            ExpiresAt = claims.ExpiresAt,
            SupervisorKeyId = claims.SupervisorKeyId,
            SignatureAlgorithm = claims.SignatureAlgorithm,
            Signature = signatureBase64
        };
    }

    public static byte[] Serialize(SignedDrainDirectiveV1 directive)
    {
        ValidateComplete(directive);
        return JsonSerializer.SerializeToUtf8Bytes(directive, StrictJson);
    }

    public static SignedDrainDirectiveV1 Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumWireBytes)
            throw new InvalidDataException("drain directive wire size is outside the contract range");
        try
        {
            var directive = JsonSerializer.Deserialize<SignedDrainDirectiveV1>(utf8Json, StrictJson) ??
                throw new InvalidDataException("drain directive is null");
            ValidateComplete(directive);
            return directive;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("drain directive JSON is invalid", exception);
        }
    }

    public static VerifiedDrainDirectiveV1 DecodeAndVerify(
        ReadOnlySpan<byte> utf8Json,
        DrainDirectiveExpectationV1 expectation,
        RSA externallyPinnedSupervisorPublicKey,
        DateTimeOffset now,
        int maximumAgeSeconds = 300,
        int maximumClockSkewSeconds = 30)
        => DecodeAndVerifyCore(
            utf8Json,
            expectation,
            externallyPinnedSupervisorPublicKey,
            now,
            validateFreshness: true,
            maximumAgeSeconds,
            maximumClockSkewSeconds);

    /// <summary>
    /// Re-verifies an exact directive already durably recorded as PREPARED or
    /// COMMITTED. It retains canonical-wire, exact-expectation, key-ID and
    /// RSA-PSS verification but intentionally does not re-apply wall-clock
    /// freshness. It must never authorize a new drain intake.
    /// </summary>
    public static VerifiedDrainDirectiveV1 DecodeAndVerifyDurableContinuation(
        ReadOnlySpan<byte> utf8Json,
        DrainDirectiveExpectationV1 expectation,
        RSA externallyPinnedSupervisorPublicKey)
        => DecodeAndVerifyCore(
            utf8Json,
            expectation,
            externallyPinnedSupervisorPublicKey,
            default,
            validateFreshness: false,
            maximumAgeSeconds: 300,
            maximumClockSkewSeconds: 30);

    private static VerifiedDrainDirectiveV1 DecodeAndVerifyCore(
        ReadOnlySpan<byte> utf8Json,
        DrainDirectiveExpectationV1 expectation,
        RSA externallyPinnedSupervisorPublicKey,
        DateTimeOffset now,
        bool validateFreshness,
        int maximumAgeSeconds,
        int maximumClockSkewSeconds)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(externallyPinnedSupervisorPublicKey);
        var directive = Deserialize(utf8Json);
        if (!utf8Json.SequenceEqual(Serialize(directive)))
            throw new InvalidDataException("drain directive wire is not the exact canonical serialization");
        var claims = ToClaims(directive);
        ValidateExpectation(claims, expectation);
        if (validateFreshness)
            ValidateFreshness(claims, now, maximumAgeSeconds, maximumClockSkewSeconds);
        if (externallyPinnedSupervisorPublicKey.KeySize < 2048 ||
            ComputeKeyId(externallyPinnedSupervisorPublicKey) != directive.SupervisorKeyId)
            throw new CryptographicException("drain directive key does not match the pinned public key");
        var statement = CreateSigningStatement(claims);
        var signature = Convert.FromBase64String(directive.Signature);
        try
        {
            if (!externallyPinnedSupervisorPublicKey.VerifyData(
                    statement,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                throw new CryptographicException("drain directive signature is invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
        return new VerifiedDrainDirectiveV1(
            directive,
            ComputeSha256(utf8Json),
            ComputeSha256(statement));
    }

    public static string ComputeKeyId(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return "sha256_" + ComputeSha256(key.ExportSubjectPublicKeyInfo());
    }

    private static void ValidateComplete(SignedDrainDirectiveV1 directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        ValidateClaims(ToClaims(directive));
        RequireCanonicalBase64(directive.Signature, "signature");
    }

    private static void ValidateExpectation(
        DrainDirectiveClaimsV1 claims,
        DrainDirectiveExpectationV1 expectation)
    {
        if (claims.DrainId != expectation.DrainId ||
            claims.Slot != expectation.Slot ||
            claims.WorkerVersion != expectation.WorkerVersion ||
            claims.WorkerArtifactSha256 != expectation.WorkerArtifactSha256 ||
            claims.JournalArtifactSha256 != expectation.JournalArtifactSha256 ||
            claims.ReleaseBomSha256 != expectation.ReleaseBomSha256 ||
            claims.ProtectedPolicySha256 != expectation.ProtectedPolicySha256 ||
            claims.RoutingEpoch != expectation.RoutingEpoch ||
            claims.SoulId != expectation.SoulId ||
            claims.DeviceBindingId != expectation.DeviceBindingId ||
            claims.PlatformAccountId != expectation.PlatformAccountId ||
            claims.TraceId != expectation.TraceId ||
            claims.IdempotencyKey != expectation.IdempotencyKey ||
            claims.OccurredAt != expectation.OccurredAt)
            throw new InvalidDataException(
                "drain directive does not match the exact authorized Worker and active drain scope");
    }

    private static DrainDirectiveClaimsV1 ToClaims(SignedDrainDirectiveV1 directive) => new(
        directive.SchemaVersion,
        directive.ContractId,
        directive.ProducerModule,
        directive.SoulId,
        directive.DeviceBindingId,
        directive.PlatformAccountId,
        directive.TraceId,
        directive.IdempotencyKey,
        directive.OccurredAt,
        directive.PrivacyClass,
        directive.DrainId,
        directive.Slot,
        directive.WorkerVersion,
        directive.WorkerArtifactSha256,
        directive.JournalArtifactSha256,
        directive.ReleaseBomSha256,
        directive.ProtectedPolicySha256,
        directive.RoutingEpoch,
        directive.IssuedAt,
        directive.NotBefore,
        directive.ExpiresAt,
        directive.SupervisorKeyId,
        directive.SignatureAlgorithm);

    private static void ValidateClaims(DrainDirectiveClaimsV1 claims)
    {
        if (claims.SchemaVersion != SchemaVersion || claims.ContractId != ContractId ||
            claims.ProducerModule != ProducerModule || claims.PrivacyClass != "internal" ||
            claims.SignatureAlgorithm != SignatureAlgorithm || claims.Slot is not ("A" or "B"))
            throw new InvalidDataException("unknown or unsupported drain directive identity");
        RequirePrefixedLowerHex(claims.SoulId, "soul_", 64, "soul_id");
        RequirePrefixedLowerHex(claims.DeviceBindingId, "db_", 32, "device_binding_id");
        RequirePrefixedLowerHex(claims.PlatformAccountId, "pa_", 32, "platform_account_id");
        RequirePrefixedLowerHex(claims.TraceId, "trace_", 32, "trace_id");
        RequirePrefixedLowerHex(claims.IdempotencyKey, "idem_", 64, "idempotency_key");
        RequirePrefixedLowerHex(claims.DrainId, "drain-", 64, "drain_id");
        RequireWorkerVersion(claims.WorkerVersion);
        RequireLowerSha256(claims.WorkerArtifactSha256, "worker_artifact_sha256");
        RequireLowerSha256(claims.JournalArtifactSha256, "journal_artifact_sha256");
        RequireLowerSha256(claims.ReleaseBomSha256, "release_bom_sha256");
        RequireLowerSha256(claims.ProtectedPolicySha256, "protected_policy_sha256");
        RequirePrefixedLowerHex(claims.SupervisorKeyId, "sha256_", 64, "supervisor_key_id");
        if (claims.RoutingEpoch < 0)
            throw new InvalidDataException("routing_epoch cannot be negative");
        _ = ParseUtc(claims.OccurredAt, "occurred_at");
        var issued = ParseUtc(claims.IssuedAt, "issued_at");
        var notBefore = ParseUtc(claims.NotBefore, "not_before");
        var expires = ParseUtc(claims.ExpiresAt, "expires_at");
        if (notBefore > issued || expires <= issued || expires - notBefore > TimeSpan.FromMinutes(5))
            throw new InvalidDataException(
                "drain directive signed validity window is structurally invalid or exceeds five minutes");
    }

    private static void ValidateFreshness(
        DrainDirectiveClaimsV1 claims,
        DateTimeOffset now,
        int maximumAgeSeconds,
        int maximumClockSkewSeconds)
    {
        if (maximumAgeSeconds is < 1 or > 900 || maximumClockSkewSeconds is < 0 or > 120)
            throw new ArgumentOutOfRangeException(nameof(maximumAgeSeconds));
        var issued = ParseUtc(claims.IssuedAt, "issued_at");
        var notBefore = ParseUtc(claims.NotBefore, "not_before");
        var expires = ParseUtc(claims.ExpiresAt, "expires_at");
        var utcNow = now.ToUniversalTime();
        var skew = TimeSpan.FromSeconds(maximumClockSkewSeconds);
        if (expires - notBefore > TimeSpan.FromSeconds(maximumAgeSeconds) ||
            issued > utcNow + skew || utcNow - issued > TimeSpan.FromSeconds(maximumAgeSeconds) + skew ||
            utcNow + skew < notBefore || utcNow >= expires)
            throw new InvalidDataException("drain directive is stale or outside its signed validity window");
    }

    private static byte[] Frame(string domain, IEnumerable<string> fields)
    {
        var output = new StringBuilder(2048).Append(domain).Append('\n');
        foreach (var value in fields)
        {
            output.Append(StrictUtf8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
            output.Append(':').Append(value).Append(';');
        }
        return StrictUtf8.GetBytes(output.ToString());
    }

    private static DateTimeOffset ParseUtc(string? value, string field)
    {
        if (value is null || !CanonicalUtc.IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException(field + " must be a canonical zero-offset UTC timestamp");
        return parsed;
    }

    private static void RequirePrefixedLowerHex(string? value, string prefix, int bodyLength, string field)
    {
        if (value is null || value.Length != prefix.Length + bodyLength ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException(field + " is not canonical");
        RequireLowerHex(value[prefix.Length..], bodyLength, field);
    }

    private static void RequireLowerSha256(string? value, string field) =>
        RequireLowerHex(value, 64, field);

    private static void RequireLowerHex(string? value, int length, string field)
    {
        if (value is null || value.Length != length ||
            value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new InvalidDataException(field + " is not canonical lowercase hexadecimal");
    }

    private static void RequireCanonicalBase64(string? value, string field)
    {
        if (value is null || value.Length is < 64 or > 2048)
            throw new InvalidDataException(field + " length is outside the contract range");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (Convert.ToBase64String(bytes) != value)
                throw new InvalidDataException(field + " is not canonical Base64");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(field + " is not canonical Base64", exception);
        }
    }

    private static bool IsAsciiAlphaNumeric(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static void RequireWorkerVersion(string? value)
    {
        if (value is null || value.Length is < 1 or > 64 || !IsAsciiAlphaNumeric(value[0]) ||
            value.Skip(1).Any(character =>
                !IsAsciiAlphaNumeric(character) && character is not ('.' or '_' or '+' or '-')))
            throw new InvalidDataException("worker_version is not a canonical ASCII version token");
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.WindowsEdgeSupervisor.Contracts;

public sealed record WorkerDrainReceiptClaimsV1(
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
    bool IntakeStopped,
    bool WorkerDrained,
    int RemainingInFlight,
    string IssuedAt,
    string NotBefore,
    string ExpiresAt);

public sealed record WorkerDrainReceiptExpectationV1(
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

public sealed record SignedWorkerDrainReceiptV1
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
    [JsonPropertyName("intake_stopped"), JsonRequired] public bool IntakeStopped { get; init; }
    [JsonPropertyName("worker_drained"), JsonRequired] public bool WorkerDrained { get; init; }
    [JsonPropertyName("remaining_in_flight"), JsonRequired] public int RemainingInFlight { get; init; }
    [JsonPropertyName("issued_at"), JsonRequired] public required string IssuedAt { get; init; }
    [JsonPropertyName("not_before"), JsonRequired] public required string NotBefore { get; init; }
    [JsonPropertyName("expires_at"), JsonRequired] public required string ExpiresAt { get; init; }
    [JsonPropertyName("worker_statement_sha256"), JsonRequired] public required string WorkerStatementSha256 { get; init; }
    [JsonPropertyName("worker_key_id"), JsonRequired] public required string WorkerKeyId { get; init; }
    [JsonPropertyName("worker_algorithm"), JsonRequired] public required string WorkerAlgorithm { get; init; }
    [JsonPropertyName("worker_signature"), JsonRequired] public required string WorkerSignature { get; init; }
}

public sealed class VerifiedWorkerDrainReceiptV1
{
    internal VerifiedWorkerDrainReceiptV1(
        SignedWorkerDrainReceiptV1 envelope,
        string wireSha256,
        string statementSha256)
    {
        Envelope = envelope;
        WireSha256 = wireSha256;
        StatementSha256 = statementSha256;
    }

    public SignedWorkerDrainReceiptV1 Envelope { get; }
    public string WireSha256 { get; }
    public string StatementSha256 { get; }
}

public static class WorkerDrainReceiptContractCodec
{
    public const string SchemaVersion = "1.0";
    public const string ContractId = "edge.worker.drain.receipt/v1";
    public const string ProducerModule = "windows-edge-worker";
    public const string SignatureAlgorithm = "RSA_PSS_SHA256";
    public const string CompatibilityProfileId = "edge.worker.drain.receipt/auth/v1";
    public const string WorkerStatementDomain = "dps.windows-edge-worker.durable-drain-receipt/v1";
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

    public static byte[] Serialize(SignedWorkerDrainReceiptV1 receipt)
    {
        ValidateComplete(receipt);
        return JsonSerializer.SerializeToUtf8Bytes(receipt, StrictJson);
    }

    public static SignedWorkerDrainReceiptV1 Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumWireBytes)
            throw new InvalidDataException("worker drain receipt wire size is outside the contract range");
        try
        {
            var receipt = JsonSerializer.Deserialize<SignedWorkerDrainReceiptV1>(utf8Json, StrictJson) ??
                throw new InvalidDataException("worker drain receipt is null");
            ValidateComplete(receipt);
            return receipt;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("worker drain receipt JSON is invalid", exception);
        }
    }

    public static VerifiedWorkerDrainReceiptV1 DecodeAndVerify(
        ReadOnlySpan<byte> utf8Json,
        WorkerDrainReceiptExpectationV1 expectation,
        RSA externallyPinnedWorkerPublicKey,
        DateTimeOffset now,
        int maximumAgeSeconds = 300,
        int maximumClockSkewSeconds = 30)
        => DecodeAndVerifyCore(
            utf8Json,
            expectation,
            externallyPinnedWorkerPublicKey,
            now,
            validateFreshness: true,
            maximumAgeSeconds,
            maximumClockSkewSeconds);

    /// <summary>
    /// Re-verifies an exact Worker receipt whose raw wire is already durable in
    /// the Journal. The caller must additionally obtain a fresh, independently
    /// signed Journal owner attestation for this exact wire digest before using
    /// the result. This method alone never proves a current durable completion.
    /// </summary>
    public static VerifiedWorkerDrainReceiptV1 DecodeAndVerifyDurableContinuation(
        ReadOnlySpan<byte> utf8Json,
        WorkerDrainReceiptExpectationV1 expectation,
        RSA externallyPinnedWorkerPublicKey)
        => DecodeAndVerifyCore(
            utf8Json,
            expectation,
            externallyPinnedWorkerPublicKey,
            default,
            validateFreshness: false,
            maximumAgeSeconds: 300,
            maximumClockSkewSeconds: 30);

    private static VerifiedWorkerDrainReceiptV1 DecodeAndVerifyCore(
        ReadOnlySpan<byte> utf8Json,
        WorkerDrainReceiptExpectationV1 expectation,
        RSA externallyPinnedWorkerPublicKey,
        DateTimeOffset now,
        bool validateFreshness,
        int maximumAgeSeconds,
        int maximumClockSkewSeconds)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(externallyPinnedWorkerPublicKey);
        var receipt = Deserialize(utf8Json);
        if (!utf8Json.SequenceEqual(Serialize(receipt)))
            throw new InvalidDataException("worker drain receipt wire is not the exact canonical serialization");
        ValidateExpectation(receipt, expectation);
        if (validateFreshness)
            ValidateFreshness(receipt, now, maximumAgeSeconds, maximumClockSkewSeconds);
        if (externallyPinnedWorkerPublicKey.KeySize < 2048 ||
            ComputeKeyId(externallyPinnedWorkerPublicKey) != receipt.WorkerKeyId)
            throw new CryptographicException("worker drain receipt key does not match the pinned public key");
        var statement = CreateSigningStatement(receipt);
        var signature = Convert.FromBase64String(receipt.WorkerSignature);
        try
        {
            if (!externallyPinnedWorkerPublicKey.VerifyData(
                    statement,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                throw new CryptographicException("worker drain receipt signature is invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
        return new VerifiedWorkerDrainReceiptV1(
            receipt,
            ComputeSha256(utf8Json),
            ComputeSha256(statement));
    }

    public static byte[] CreateSigningStatement(SignedWorkerDrainReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateStatementFields(receipt);
        return CreateSigningStatement(ToClaims(receipt));
    }

    public static byte[] CreateSigningStatement(WorkerDrainReceiptClaimsV1 claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ValidateClaims(claims);
        return CreateSigningStatement(
            claims.SoulId, claims.DeviceBindingId, claims.PlatformAccountId,
            claims.TraceId, claims.IdempotencyKey, claims.OccurredAt, claims.PrivacyClass,
            claims.DrainId, claims.Slot, claims.WorkerVersion, claims.WorkerArtifactSha256,
            claims.JournalArtifactSha256, claims.ReleaseBomSha256, claims.ProtectedPolicySha256,
            claims.RoutingEpoch, claims.IntakeStopped, claims.WorkerDrained,
            claims.RemainingInFlight, claims.IssuedAt, claims.NotBefore, claims.ExpiresAt);
    }

    public static SignedWorkerDrainReceiptV1 AttachSignature(
        WorkerDrainReceiptClaimsV1 claims,
        string workerKeyId,
        string workerSignatureBase64)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ValidateClaims(claims);
        RequireKeyId(workerKeyId, "worker_key_id");
        RequireCanonicalBase64(workerSignatureBase64, "worker_signature");
        return new SignedWorkerDrainReceiptV1
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
            IntakeStopped = claims.IntakeStopped,
            WorkerDrained = claims.WorkerDrained,
            RemainingInFlight = claims.RemainingInFlight,
            IssuedAt = claims.IssuedAt,
            NotBefore = claims.NotBefore,
            ExpiresAt = claims.ExpiresAt,
            WorkerStatementSha256 = ComputeSha256(CreateSigningStatement(claims)),
            WorkerKeyId = workerKeyId,
            WorkerAlgorithm = SignatureAlgorithm,
            WorkerSignature = workerSignatureBase64
        };
    }

    public static byte[] CreateJournalPayload(
        SignedWorkerDrainReceiptV1 receipt,
        string workerReceiptWireSha256)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateComplete(receipt);
        RequireLowerSha256(workerReceiptWireSha256, "worker_receipt_wire_sha256");
        return CreateJournalPayload(
            receipt.DrainId,
            receipt.IntakeStopped,
            receipt.JournalArtifactSha256,
            receipt.ProtectedPolicySha256,
            receipt.ReleaseBomSha256,
            receipt.RemainingInFlight,
            receipt.RoutingEpoch,
            receipt.Slot,
            receipt.WorkerArtifactSha256,
            receipt.WorkerDrained,
            workerReceiptWireSha256,
            receipt.WorkerVersion);
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string ComputeKeyId(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return "sha256_" + ComputeSha256(key.ExportSubjectPublicKeyInfo());
    }

    private static byte[] CreateSigningStatement(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        string occurredAt,
        string privacyClass,
        string drainId,
        string slot,
        string workerVersion,
        string workerArtifactSha256,
        string journalArtifactSha256,
        string releaseBomSha256,
        string protectedPolicySha256,
        long routingEpoch,
        bool intakeStopped,
        bool workerDrained,
        int remainingInFlight,
        string issuedAt,
        string notBefore,
        string expiresAt) =>
        Frame(WorkerStatementDomain,
        [
            SchemaVersion, ContractId, ProducerModule,
            soulId, deviceBindingId, platformAccountId, traceId, idempotencyKey,
            occurredAt, privacyClass, drainId, slot, workerVersion,
            workerArtifactSha256, journalArtifactSha256, releaseBomSha256,
            protectedPolicySha256, routingEpoch.ToString(CultureInfo.InvariantCulture),
            intakeStopped ? "true" : "false", workerDrained ? "true" : "false",
            remainingInFlight.ToString(CultureInfo.InvariantCulture), issuedAt, notBefore, expiresAt
        ]);

    private static byte[] CreateJournalPayload(
        string drainId,
        bool intakeStopped,
        string journalArtifactSha256,
        string protectedPolicySha256,
        string releaseBomSha256,
        int remainingInFlight,
        long routingEpoch,
        string slot,
        string workerArtifactSha256,
        bool workerDrained,
        string workerReceiptWireSha256,
        string workerVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("drain_id", drainId);
            writer.WriteBoolean("intake_stopped", intakeStopped);
            writer.WriteString("journal_artifact_sha256", journalArtifactSha256);
            writer.WriteString("protected_policy_sha256", protectedPolicySha256);
            writer.WriteString("release_bom_sha256", releaseBomSha256);
            writer.WriteNumber("remaining_in_flight", remainingInFlight);
            writer.WriteNumber("routing_epoch", routingEpoch);
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("slot", slot);
            writer.WriteString("worker_artifact_sha256", workerArtifactSha256);
            writer.WriteBoolean("worker_drained", workerDrained);
            writer.WriteString("worker_receipt_wire_sha256", workerReceiptWireSha256);
            writer.WriteString("worker_version", workerVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void ValidateComplete(SignedWorkerDrainReceiptV1 receipt)
    {
        ValidateStatementFields(receipt);
        RequireLowerSha256(receipt.WorkerStatementSha256, "worker_statement_sha256");
        RequireKeyId(receipt.WorkerKeyId, "worker_key_id");
        if (receipt.WorkerAlgorithm != SignatureAlgorithm)
            throw new InvalidDataException("worker_algorithm is not supported");
        RequireCanonicalBase64(receipt.WorkerSignature, "worker_signature");
        var statementSha256 = ComputeSha256(CreateSigningStatement(receipt));
        if (receipt.WorkerStatementSha256 != statementSha256)
            throw new InvalidDataException("worker_statement_sha256 does not match the canonical statement");
    }

    private static void ValidateExpectation(
        SignedWorkerDrainReceiptV1 receipt,
        WorkerDrainReceiptExpectationV1 expectation)
    {
        if (receipt.DrainId != expectation.DrainId ||
            receipt.Slot != expectation.Slot ||
            receipt.WorkerVersion != expectation.WorkerVersion ||
            receipt.WorkerArtifactSha256 != expectation.WorkerArtifactSha256 ||
            receipt.JournalArtifactSha256 != expectation.JournalArtifactSha256 ||
            receipt.ReleaseBomSha256 != expectation.ReleaseBomSha256 ||
            receipt.ProtectedPolicySha256 != expectation.ProtectedPolicySha256 ||
            receipt.RoutingEpoch != expectation.RoutingEpoch ||
            receipt.SoulId != expectation.SoulId ||
            receipt.DeviceBindingId != expectation.DeviceBindingId ||
            receipt.PlatformAccountId != expectation.PlatformAccountId ||
            receipt.TraceId != expectation.TraceId ||
            receipt.IdempotencyKey != expectation.IdempotencyKey ||
            receipt.OccurredAt != expectation.OccurredAt)
            throw new InvalidDataException(
                "worker drain receipt does not match the active drain and deployment policy");
    }

    private static void ValidateFreshness(
        SignedWorkerDrainReceiptV1 receipt,
        DateTimeOffset now,
        int maximumAgeSeconds,
        int maximumClockSkewSeconds)
    {
        if (maximumAgeSeconds is < 1 or > 900 || maximumClockSkewSeconds is < 0 or > 120)
            throw new ArgumentOutOfRangeException(nameof(maximumAgeSeconds));
        var issuedAt = ParseUtc(receipt.IssuedAt, "issued_at");
        var notBefore = ParseUtc(receipt.NotBefore, "not_before");
        var expiresAt = ParseUtc(receipt.ExpiresAt, "expires_at");
        var utcNow = now.ToUniversalTime();
        var skew = TimeSpan.FromSeconds(maximumClockSkewSeconds);
        if (notBefore > issuedAt || expiresAt <= notBefore ||
            expiresAt - notBefore > TimeSpan.FromSeconds(maximumAgeSeconds) ||
            issuedAt > utcNow + skew ||
            utcNow - issuedAt > TimeSpan.FromSeconds(maximumAgeSeconds) + skew ||
            utcNow + skew < notBefore || utcNow >= expiresAt)
            throw new InvalidDataException(
                "worker drain receipt is stale or outside its signed validity window");
    }

    private static void ValidateStatementFields(SignedWorkerDrainReceiptV1 receipt)
    {
        if (receipt.SchemaVersion != SchemaVersion || receipt.ContractId != ContractId ||
            receipt.ProducerModule != ProducerModule)
            throw new InvalidDataException("unknown worker drain receipt contract identity");
        ValidateFields(
            receipt.SoulId, receipt.DeviceBindingId, receipt.PlatformAccountId,
            receipt.TraceId, receipt.IdempotencyKey, receipt.OccurredAt, receipt.PrivacyClass,
            receipt.DrainId, receipt.Slot, receipt.WorkerVersion, receipt.WorkerArtifactSha256,
            receipt.JournalArtifactSha256, receipt.ReleaseBomSha256, receipt.ProtectedPolicySha256,
            receipt.RoutingEpoch, receipt.IntakeStopped, receipt.WorkerDrained,
            receipt.RemainingInFlight, receipt.IssuedAt, receipt.NotBefore, receipt.ExpiresAt);
    }

    private static WorkerDrainReceiptClaimsV1 ToClaims(SignedWorkerDrainReceiptV1 receipt) => new(
        receipt.SchemaVersion,
        receipt.ContractId,
        receipt.ProducerModule,
        receipt.SoulId,
        receipt.DeviceBindingId,
        receipt.PlatformAccountId,
        receipt.TraceId,
        receipt.IdempotencyKey,
        receipt.OccurredAt,
        receipt.PrivacyClass,
        receipt.DrainId,
        receipt.Slot,
        receipt.WorkerVersion,
        receipt.WorkerArtifactSha256,
        receipt.JournalArtifactSha256,
        receipt.ReleaseBomSha256,
        receipt.ProtectedPolicySha256,
        receipt.RoutingEpoch,
        receipt.IntakeStopped,
        receipt.WorkerDrained,
        receipt.RemainingInFlight,
        receipt.IssuedAt,
        receipt.NotBefore,
        receipt.ExpiresAt);

    private static void ValidateClaims(WorkerDrainReceiptClaimsV1 claims)
    {
        if (claims.SchemaVersion != SchemaVersion || claims.ContractId != ContractId ||
            claims.ProducerModule != ProducerModule)
            throw new InvalidDataException("unknown worker drain receipt contract identity");
        ValidateFields(
            claims.SoulId, claims.DeviceBindingId, claims.PlatformAccountId,
            claims.TraceId, claims.IdempotencyKey, claims.OccurredAt, claims.PrivacyClass,
            claims.DrainId, claims.Slot, claims.WorkerVersion, claims.WorkerArtifactSha256,
            claims.JournalArtifactSha256, claims.ReleaseBomSha256, claims.ProtectedPolicySha256,
            claims.RoutingEpoch, claims.IntakeStopped, claims.WorkerDrained,
            claims.RemainingInFlight, claims.IssuedAt, claims.NotBefore, claims.ExpiresAt);
    }

    private static void ValidateFields(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        string occurredAt,
        string privacyClass,
        string drainId,
        string slot,
        string workerVersion,
        string workerArtifactSha256,
        string journalArtifactSha256,
        string releaseBomSha256,
        string protectedPolicySha256,
        long routingEpoch,
        bool intakeStopped,
        bool workerDrained,
        int remainingInFlight,
        string issuedAt,
        string notBefore,
        string expiresAt)
    {
        RequirePrefixedLowerHex(soulId, "soul_", 64, "soul_id");
        RequirePrefixedLowerHex(deviceBindingId, "db_", 32, "device_binding_id");
        RequirePrefixedLowerHex(platformAccountId, "pa_", 32, "platform_account_id");
        RequirePrefixedLowerHex(traceId, "trace_", 32, "trace_id");
        RequirePrefixedLowerHex(idempotencyKey, "idem_", 64, "idempotency_key");
        RequirePrefixedLowerHex(drainId, "drain-", 64, "drain_id");
        _ = ParseUtc(occurredAt, "occurred_at");
        if (privacyClass != "internal" || slot is not ("A" or "B"))
            throw new InvalidDataException("worker drain privacy class or slot is invalid");
        RequireWorkerVersion(workerVersion);
        RequireLowerSha256(workerArtifactSha256, "worker_artifact_sha256");
        RequireLowerSha256(journalArtifactSha256, "journal_artifact_sha256");
        RequireLowerSha256(releaseBomSha256, "release_bom_sha256");
        RequireLowerSha256(protectedPolicySha256, "protected_policy_sha256");
        if (routingEpoch < 0 || remainingInFlight < 0 ||
            !intakeStopped || !workerDrained || remainingInFlight != 0)
            throw new InvalidDataException("worker drain truth is incomplete or counters are invalid");
        var issued = ParseUtc(issuedAt, "issued_at");
        var notBeforeValue = ParseUtc(notBefore, "not_before");
        var expires = ParseUtc(expiresAt, "expires_at");
        if (notBeforeValue > issued || expires <= issued ||
            expires - notBeforeValue > TimeSpan.FromMinutes(5))
            throw new InvalidDataException(
                "worker drain signed validity window is structurally invalid or exceeds five minutes");
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

    private static void RequireKeyId(string? value, string field) =>
        RequirePrefixedLowerHex(value, "sha256_", 64, field);

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
}

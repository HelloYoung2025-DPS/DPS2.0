using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.WindowsEdgeSupervisor;

public sealed record BridgeDirectiveRequest(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string OccurredAt,
    string PrivacyClass,
    string DirectiveKind,
    string? CommandId,
    string? ActionKind,
    string? StepKind,
    string? Selector,
    string? Text,
    int? WaitMs,
    string? ExpectedPostcondition);

public sealed record BridgeDirectiveV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] string OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("auth_key_id"), JsonRequired] string AuthKeyId,
    [property: JsonPropertyName("auth_nonce"), JsonRequired] string AuthNonce,
    [property: JsonPropertyName("auth_issued_at"), JsonRequired] string AuthIssuedAt,
    [property: JsonPropertyName("auth_body_sha256"), JsonRequired] string AuthBodySha256,
    [property: JsonPropertyName("auth_proof"), JsonRequired] string AuthProof,
    [property: JsonPropertyName("directive_kind"), JsonRequired] string DirectiveKind,
    [property: JsonPropertyName("command_id"), JsonRequired] string? CommandId,
    [property: JsonPropertyName("action_kind"), JsonRequired] string? ActionKind,
    [property: JsonPropertyName("step_kind"), JsonRequired] string? StepKind,
    [property: JsonPropertyName("selector"), JsonRequired] string? Selector,
    [property: JsonPropertyName("text"), JsonRequired] string? Text,
    [property: JsonPropertyName("wait_ms"), JsonRequired] int? WaitMs,
    [property: JsonPropertyName("expected_postcondition"), JsonRequired] string? ExpectedPostcondition);

/// <summary>
/// Production codec for the fixed supervisor-to-Zenno authentication protocol.
/// The key id is SHA-256 over DER SubjectPublicKeyInfo. Directive proofs use
/// RSA PKCS#1 v1.5 with SHA-256; artifact and capability evidence remain PSS.
/// </summary>
public static partial class BridgeDirectiveAuthenticator
{
    public const string SignatureAlgorithm = "RSA_PKCS1_SHA256";
    private const int MaximumWireBytes = 32768;
    private const string SigningDomain = "dps.edge.bridge.directive-auth/v1";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly IReadOnlyDictionary<string, string> AllowedPairs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OBSERVE"] = "OBSERVE_SCREEN",
            ["LOCATE"] = "LOCATE_SELECTOR",
            ["VERIFY"] = "VERIFY_POSTCONDITION",
            ["WAIT"] = "WAIT_DURATION",
            ["TAP"] = "TAP_SELECTOR",
            ["TYPE"] = "TYPE_TEXT"
        };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static BridgeDirectiveV1 CreateSigned(
        BridgeDirectiveRequest request,
        string authNonce,
        string authIssuedAt,
        RSA signingKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signingKey);
        var keyId = PinnedRsaTrustStore.ComputeKeyId(signingKey.ExportSubjectPublicKeyInfo());
        var unsigned = new BridgeDirectiveV1(
            "1.0",
            "edge.bridge.directive/v1",
            "windows-edge-supervisor",
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            request.PrivacyClass,
            keyId,
            authNonce,
            authIssuedAt,
            new string('0', 64),
            Convert.ToBase64String(new byte[64]),
            request.DirectiveKind,
            request.CommandId,
            request.ActionKind,
            request.StepKind,
            request.Selector,
            request.Text,
            request.WaitMs,
            request.ExpectedPostcondition);
        Validate(unsigned, validateProof: false);
        var bodySha256 = ComputeDirectiveBodySha256(unsigned);
        var statement = CreateSigningStatement(keyId, authNonce, authIssuedAt, bodySha256);
        var signature = signingKey.SignData(statement, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signed = unsigned with
        {
            AuthBodySha256 = bodySha256,
            AuthProof = Convert.ToBase64String(signature)
        };
        Validate(signed, validateProof: true);
        return signed;
    }

    public static byte[] Encode(BridgeDirectiveV1 directive)
    {
        Validate(directive, validateProof: true);
        var payload = JsonSerializer.SerializeToUtf8Bytes(directive, JsonOptions);
        if (payload.Length > MaximumWireBytes)
            throw new InvalidDataException("edge bridge directive exceeds the wire-size limit");
        return payload;
    }

    public static BridgeDirectiveV1 DecodeAndVerify(
        ReadOnlySpan<byte> utf8Json,
        string expectedNonce,
        PinnedRsaTrustStore trustStore,
        DateTimeOffset now,
        int maximumClockSkewSeconds)
    {
        ArgumentNullException.ThrowIfNull(trustStore);
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumWireBytes)
            throw new InvalidDataException("edge bridge directive wire size is outside the contract range");
        BridgeDirectiveV1 directive;
        try
        {
            directive = JsonSerializer.Deserialize<BridgeDirectiveV1>(utf8Json, JsonOptions) ??
                throw new InvalidDataException("edge bridge directive payload is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("edge bridge directive JSON is invalid", exception);
        }

        Validate(directive, validateProof: true);
        if (!string.Equals(directive.AuthNonce, expectedNonce, StringComparison.Ordinal))
            throw new InvalidDataException("edge bridge directive nonce does not match the poll request");
        var issuedAt = ParseCanonicalUtc(directive.AuthIssuedAt, "auth_issued_at");
        if (maximumClockSkewSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(maximumClockSkewSeconds));
        if (Math.Abs((now.ToUniversalTime() - issuedAt).TotalSeconds) > maximumClockSkewSeconds)
            throw new InvalidDataException("edge bridge directive authentication timestamp is outside the allowed clock window");
        var bodySha256 = ComputeDirectiveBodySha256(directive);
        if (!string.Equals(bodySha256, directive.AuthBodySha256, StringComparison.Ordinal))
            throw new InvalidDataException("edge bridge directive body digest mismatch");
        var statement = CreateSigningStatement(
            directive.AuthKeyId,
            directive.AuthNonce,
            directive.AuthIssuedAt,
            directive.AuthBodySha256);
        if (!trustStore.VerifyPkcs1Sha256Base64(directive.AuthKeyId, statement, directive.AuthProof))
            throw new InvalidDataException("edge bridge directive PKCS#1 SHA-256 proof is not trusted");
        return directive;
    }

    public static byte[] CreateSigningStatement(
        string keyId,
        string nonce,
        string issuedAt,
        string bodySha256)
    {
        RequirePrefixedLowerHex(keyId, "sha256_", 64, "auth_key_id");
        RequireLowerHex(nonce, 64, "auth_nonce");
        _ = ParseCanonicalUtc(issuedAt, "auth_issued_at");
        RequireLowerHex(bodySha256, 64, "auth_body_sha256");
        return StrictUtf8.GetBytes(string.Join("\n", SigningDomain, keyId, nonce, issuedAt, bodySha256));
    }

    public static string ComputeDirectiveBodySha256(BridgeDirectiveV1 directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        var canonical = new StringBuilder();
        AppendField(canonical, directive.SchemaVersion);
        AppendField(canonical, directive.ContractId);
        AppendField(canonical, directive.ProducerModule);
        AppendField(canonical, directive.SoulId);
        AppendField(canonical, directive.DeviceBindingId);
        AppendField(canonical, directive.PlatformAccountId);
        AppendField(canonical, directive.TraceId);
        AppendField(canonical, directive.IdempotencyKey);
        AppendField(canonical, directive.OccurredAt);
        AppendField(canonical, directive.PrivacyClass);
        AppendField(canonical, directive.DirectiveKind);
        AppendField(canonical, directive.CommandId);
        AppendField(canonical, directive.ActionKind);
        AppendField(canonical, directive.StepKind);
        AppendField(canonical, directive.Selector);
        AppendField(canonical, directive.Text);
        AppendField(canonical, directive.WaitMs?.ToString(CultureInfo.InvariantCulture));
        AppendField(canonical, directive.ExpectedPostcondition);
        return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(canonical.ToString())));
    }

    private static void Validate(BridgeDirectiveV1 directive, bool validateProof)
    {
        ArgumentNullException.ThrowIfNull(directive);
        if (directive.SchemaVersion != "1.0" ||
            directive.ContractId != "edge.bridge.directive/v1" ||
            directive.ProducerModule != "windows-edge-supervisor")
            throw new InvalidDataException("unknown edge bridge directive contract identity");
        RequirePrefixedLowerHex(directive.SoulId, "soul_", 64, "soul_id");
        RequirePrefixedLowerHex(directive.DeviceBindingId, "db_", 32, "device_binding_id");
        RequirePrefixedLowerHex(directive.PlatformAccountId, "pa_", 32, "platform_account_id");
        RequirePrefixedLowerHex(directive.TraceId, "trace_", 32, "trace_id");
        RequirePrefixedLowerHex(directive.IdempotencyKey, "idem_", 64, "idempotency_key");
        _ = ParseCanonicalUtc(directive.OccurredAt, "occurred_at");
        if (directive.PrivacyClass is not ("internal" or "personal" or "sensitive"))
            throw new InvalidDataException("unknown edge bridge directive privacy_class");
        RequirePrefixedLowerHex(directive.AuthKeyId, "sha256_", 64, "auth_key_id");
        RequireLowerHex(directive.AuthNonce, 64, "auth_nonce");
        _ = ParseCanonicalUtc(directive.AuthIssuedAt, "auth_issued_at");
        RequireLowerHex(directive.AuthBodySha256, 64, "auth_body_sha256");
        if (validateProof) RequireCanonicalBase64(directive.AuthProof, 64, 2048, "auth_proof");

        if (directive.DirectiveKind == "COMMAND")
        {
            RequireLength(directive.CommandId, 1, 128, "command_id");
            if (!AllowedPairs.TryGetValue(directive.ActionKind ?? string.Empty, out var expectedStep) ||
                expectedStep != directive.StepKind)
                throw new InvalidDataException("unknown or mismatched edge bridge action and step");
            RequireOptionalLength(directive.Selector, 2048, "selector");
            RequireOptionalLength(directive.Text, 4096, "text");
            RequireOptionalLength(directive.ExpectedPostcondition, 2048, "expected_postcondition");
            if (directive.WaitMs is < 0 or > 300000)
                throw new InvalidDataException("wait_ms is outside the edge bridge directive range");
            if (directive.ActionKind is "TAP" or "LOCATE" or "VERIFY" && string.IsNullOrWhiteSpace(directive.Selector))
                throw new InvalidDataException("selector is required for the edge bridge action");
            if (directive.ActionKind == "TYPE" && string.IsNullOrEmpty(directive.Text))
                throw new InvalidDataException("text is required for the edge bridge TYPE action");
            if (directive.ActionKind == "WAIT" && directive.WaitMs is null)
                throw new InvalidDataException("wait_ms is required for the edge bridge WAIT action");
        }
        else if (directive.DirectiveKind is "ACK" or "WAIT")
        {
            if (directive.CommandId is not null || directive.ActionKind is not null || directive.StepKind is not null ||
                directive.Selector is not null || directive.Text is not null || directive.WaitMs is not null ||
                directive.ExpectedPostcondition is not null)
                throw new InvalidDataException("ACK and WAIT directives cannot carry command fields");
        }
        else
        {
            throw new InvalidDataException("unknown edge bridge directive kind");
        }
    }

    private static void AppendField(StringBuilder output, string? value)
    {
        if (value is null)
        {
            output.Append("-1:");
        }
        else
        {
            _ = StrictUtf8.GetByteCount(value);
            output.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            output.Append(':');
            output.Append(value);
        }
        output.Append(';');
    }

    private static DateTimeOffset ParseCanonicalUtc(string value, string field)
    {
        if (!CanonicalUtcRegex().IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException($"{field} must be a canonical zero-offset UTC timestamp");
        return parsed;
    }

    private static void RequirePrefixedLowerHex(string? value, string prefix, int bodyLength, string field)
    {
        if (value is null || value.Length != prefix.Length + bodyLength ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
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
        if (value is null || value.Length < minimum || value.Length > maximum)
            throw new InvalidDataException($"{field} length is outside the contract range");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
                throw new InvalidDataException($"{field} is not canonical Base64");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{field} is not canonical Base64", exception);
        }
    }

    private static void RequireLength(string? value, int minimum, int maximum, string field)
    {
        if (value is null || value.Length < minimum || value.Length > maximum)
            throw new InvalidDataException($"{field} length is outside the contract range");
        _ = StrictUtf8.GetByteCount(value);
    }

    private static void RequireOptionalLength(string? value, int maximum, string field)
    {
        if (value is null) return;
        if (value.Length > maximum)
            throw new InvalidDataException($"{field} length is outside the contract range");
        _ = StrictUtf8.GetByteCount(value);
    }

    [GeneratedRegex(
        "^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-5][0-9]:[0-5][0-9](?:\\.[0-9]+)?(?:Z|\\+00:00)\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalUtcRegex();
}

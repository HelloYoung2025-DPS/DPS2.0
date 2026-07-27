using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.ControlPlaneHost.Contracts;

namespace Dps.ControlPlaneHost;

public sealed record SignedProviderResultV1(
    string ActiveReleaseBomSha256,
    string ProviderKeyId,
    ReadOnlyMemory<byte> PayloadUtf8,
    string SignatureBase64);

public sealed record ProviderTrustStateV1(
    long Revision,
    string SourceContractId,
    string SourceProducerModule,
    string ActiveReleaseBomSha256,
    string ProviderKeyId,
    string ProviderPublicKeySpkiBase64,
    string ProviderPublicKeySha256,
    string Status,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil);

internal sealed record ParsedProviderResult(
    ModuleResultEnvelope Result,
    string PayloadSha256);

internal static class ProviderResultAuthorization
{
    private const int MaximumPayloadBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Regex UtcDateTime = new(
        "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:Z|\\+00:00)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CanonicalZuluDateTime = new(
        "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{0,6}[1-9])?Z\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static ParsedProviderResult Parse(SignedProviderResultV1 signed)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ControlContractValidation.RequireSha256(
            signed.ActiveReleaseBomSha256,
            nameof(signed.ActiveReleaseBomSha256));
        RequireToken(signed.ProviderKeyId, 128, nameof(signed.ProviderKeyId), allowColon: true);
        if (signed.PayloadUtf8.IsEmpty || signed.PayloadUtf8.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("Provider payload size is outside the allowlist.", nameof(signed));
        }

        var signature = DecodeBase64(signed.SignatureBase64, nameof(signed.SignatureBase64));
        try
        {
            if (signature.Length != 64)
            {
                throw new ArgumentException("Provider signature must be one P-256 P1363 signature.", nameof(signed));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        var payload = signed.PayloadUtf8.ToArray();
        try
        {
            try
            {
                _ = StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ArgumentException("Provider payload is not strict UTF-8.", nameof(signed), exception);
            }

            using var document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Provider payload must be one JSON object.", nameof(signed));
            }

            var fields = ReadUniqueFields(document.RootElement);
            var schemaVersion = String(fields, "schema_version");
            var contractId = String(fields, "contract_id");
            var producer = String(fields, "producer_module");
            var soulId = String(fields, "soul_id");
            var deviceBindingId = String(fields, "device_binding_id");
            var platformAccountId = String(fields, "platform_account_id");
            var traceId = String(fields, "trace_id");
            var idempotencyKey = String(fields, "idempotency_key");
            var occurredAt = UtcTimestamp(fields, "occurred_at");

            ControlContractValidation.RequireMajor(schemaVersion, 1);
            ControlContractValidation.RequireSourceOwnerPair(contractId, producer);
            ControlContractValidation.RequireSoulId(soulId);
            ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
            ControlContractValidation.RequirePlatformAccountId(platformAccountId);
            ControlContractValidation.RequireTraceId(traceId);
            ControlContractValidation.RequireIdempotencyKey(idempotencyKey);

            var status = contractId switch
            {
                "device.registered/v1" => ValidateDevice(fields, schemaVersion),
                "platform.account.authorized/v1" => ValidatePlatformAccount(fields, schemaVersion),
                "identity.binding/v1" => ValidateBinding(fields, schemaVersion),
                "persona.revision/v1" => ValidatePersona(fields, occurredAt),
                "soul.memory.readback/v1" => ValidateGBrainReadback(fields),
                _ => throw new NotSupportedException("Unknown provider contract.")
            };
            var payloadSha256 = Sha256(payload);
            var result = new ModuleResultEnvelope(
                schemaVersion,
                contractId,
                producer,
                soulId,
                deviceBindingId,
                platformAccountId,
                traceId,
                idempotencyKey,
                occurredAt,
                payloadSha256,
                status);
            ControlPlaneResultPolicy.Validate(result);
            return new ParsedProviderResult(result, payloadSha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static string ComputeAuthorizationDigest(
        SignedProviderResultV1 signed,
        ParsedProviderResult parsed)
        => ControlPlaneCanonicalEncoding.ComputeDomainSha256(
            "dps.control-plane-host.provider-result-authorization/v1",
            signed.ActiveReleaseBomSha256,
            signed.ProviderKeyId,
            parsed.Result.SourceContractId,
            parsed.Result.SourceProducerModule,
            parsed.PayloadSha256);

    internal static void VerifySignature(
        SignedProviderResultV1 signed,
        ParsedProviderResult parsed,
        string publicKeySpkiBase64,
        string expectedPublicKeySha256)
    {
        var publicKey = DecodeBase64(publicKeySpkiBase64, nameof(publicKeySpkiBase64));
        var signature = DecodeBase64(signed.SignatureBase64, nameof(signed.SignatureBase64));
        var digest = Convert.FromHexString(ComputeAuthorizationDigest(signed, parsed));
        try
        {
            if (!FixedTimeHexEquals(Sha256(publicKey), expectedPublicKeySha256))
            {
                throw new UnauthorizedAccessException("Provider public-key digest does not match trusted state.");
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (!IsCanonicalP256SubjectPublicKeyInfo(ecdsa, publicKey, bytesRead)
                || !ecdsa.VerifyHash(
                    digest,
                    signature,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new UnauthorizedAccessException("Provider result signature is invalid.");
            }
        }
        catch (Exception exception) when (
            exception is CryptographicException or PlatformNotSupportedException)
        {
            throw new UnauthorizedAccessException("Provider trust key or signature is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static void ValidateTrustState(ProviderTrustStateV1 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Trust revision must be positive.");
        }

        ControlContractValidation.RequireSourceOwnerPair(
            state.SourceContractId,
            state.SourceProducerModule);
        ControlContractValidation.RequireSha256(
            state.ActiveReleaseBomSha256,
            nameof(state.ActiveReleaseBomSha256));
        RequireToken(state.ProviderKeyId, 128, nameof(state.ProviderKeyId), allowColon: true);
        ControlContractValidation.RequireSha256(
            state.ProviderPublicKeySha256,
            nameof(state.ProviderPublicKeySha256));
        if (state.Status is not ("ACTIVE" or "REVOKED"))
        {
            throw new ArgumentException("Provider trust status is not allowlisted.", nameof(state));
        }
        ControlContractValidation.RequireUtc(state.ValidFrom, nameof(state.ValidFrom));
        ControlContractValidation.RequireUtc(state.ValidUntil, nameof(state.ValidUntil));
        if (state.ValidUntil <= state.ValidFrom)
        {
            throw new ArgumentException("Provider trust validity window is empty.", nameof(state));
        }

        var publicKey = DecodeBase64(
            state.ProviderPublicKeySpkiBase64,
            nameof(state.ProviderPublicKeySpkiBase64));
        try
        {
            if (!FixedTimeHexEquals(Sha256(publicKey), state.ProviderPublicKeySha256))
            {
                throw new ArgumentException("Provider trust public-key checksum is invalid.", nameof(state));
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (!IsCanonicalP256SubjectPublicKeyInfo(ecdsa, publicKey, bytesRead))
            {
                throw new ArgumentException(
                    "Provider trust key must be one canonical NIST P-256 SPKI value.",
                    nameof(state));
            }
        }
        catch (Exception exception) when (
            exception is CryptographicException or PlatformNotSupportedException)
        {
            throw new ArgumentException("Provider trust key is invalid.", nameof(state), exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static string ValidateDevice(
        IReadOnlyDictionary<string, JsonElement> fields,
        string schemaVersion)
    {
        ControlContractValidation.RequireExact(schemaVersion, "1.0.0", "schema_version");
        RequireExactKeys(fields,
        [
            "schema_version", "contract_id", "producer_module", "soul_id",
            "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "privacy_class", "device_id",
            "fingerprint_hmac_sha256", "fingerprint_key_id",
            "fingerprint_key_epoch", "capability_revision", "capabilities", "status"
        ]);
        ControlContractValidation.RequireExact(String(fields, "privacy_class"), "sensitive", "privacy_class");
        RequireHex(String(fields, "device_id"), "device_", 32, "device_id");
        ControlContractValidation.RequireSha256(
            String(fields, "fingerprint_hmac_sha256"),
            "fingerprint_hmac_sha256");
        RequireHex(String(fields, "fingerprint_key_id"), "fpkey_", 32, "fingerprint_key_id");
        PositiveInteger(fields, "fingerprint_key_epoch");
        PositiveInteger(fields, "capability_revision");
        StringArray(
            fields,
            "capabilities",
            minimum: 0,
            static value =>
            {
                if (value.Length > 64)
                {
                    throw new ArgumentException("Device capability exceeds 64 ASCII bytes.");
                }
                ValidateSlug(value);
            },
            maximum: 64,
            maximumTotalAsciiBytes: 4096);
        return String(fields, "status");
    }

    private static string ValidatePlatformAccount(
        IReadOnlyDictionary<string, JsonElement> fields,
        string schemaVersion)
    {
        ControlContractValidation.RequireExact(schemaVersion, "1.0.0", "schema_version");
        RequireExactKeys(fields,
        [
            "schema_version", "contract_id", "producer_module", "soul_id",
            "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "privacy_class", "platform",
            "alias_digest", "alias_key_id", "alias_key_epoch",
            "authorization_evidence_id", "authorization_revision", "status"
        ]);
        ControlContractValidation.RequireExact(String(fields, "privacy_class"), "sensitive", "privacy_class");
        ValidateSlug(String(fields, "platform"), maximum: 64);
        ControlContractValidation.RequireSha256(String(fields, "alias_digest"), "alias_digest");
        RequireLowercaseToken(String(fields, "alias_key_id"), 64, "alias_key_id");
        PositiveInteger(fields, "alias_key_epoch");
        var evidenceId = String(fields, "authorization_evidence_id");
        if (!evidenceId.StartsWith("approval_", StringComparison.Ordinal)
            || evidenceId.Length is < 10 or > 128
            || evidenceId.AsSpan("approval_".Length).ContainsAnyExcept(
                "abcdefghijklmnopqrstuvwxyz0123456789_-"))
        {
            throw new ArgumentException("Authorization evidence ID is invalid.");
        }
        PositiveInteger(fields, "authorization_revision");
        return String(fields, "status");
    }

    private static string ValidateBinding(
        IReadOnlyDictionary<string, JsonElement> fields,
        string schemaVersion)
    {
        ControlContractValidation.RequireExact(schemaVersion, "1.0.0", "schema_version");
        RequireExactKeys(fields,
        [
            "schema_version", "contract_id", "producer_module", "soul_id",
            "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "privacy_class", "device_id",
            "binding_revision", "status", "device_registration_revision",
            "account_authorization_revision"
        ]);
        ControlContractValidation.RequireExact(String(fields, "privacy_class"), "sensitive", "privacy_class");
        RequireHex(String(fields, "device_id"), "device_", 32, "device_id");
        PositiveInteger(fields, "binding_revision");
        PositiveInteger(fields, "device_registration_revision");
        PositiveInteger(fields, "account_authorization_revision");
        return String(fields, "status");
    }

    private static string ValidatePersona(
        IReadOnlyDictionary<string, JsonElement> fields,
        DateTimeOffset occurredAt)
    {
        RequireExactKeys(fields,
        [
            "schema_version", "contract_id", "producer_module", "soul_id",
            "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "privacy_class", "persona_revision",
            "traits_sha256", "trait_keys", "evidence_sha256", "status"
        ]);
        if (occurredAt.Year is < 2020 or > 2199)
        {
            throw new ArgumentException(
                "Persona revision occurred_at must be within the owner contract's 2020-2199 window.");
        }
        ControlContractValidation.RequireExact(String(fields, "privacy_class"), "personal", "privacy_class");
        PositiveInteger(fields, "persona_revision");
        ControlContractValidation.RequireSha256(String(fields, "traits_sha256"), "traits_sha256");
        var traitCount = StringArray(fields, "trait_keys", minimum: 0, static value =>
        {
            if (value is not ("curiosity" or "humor" or "pace" or "sociality" or "tone"))
            {
                throw new ArgumentException("Persona trait key is unknown.");
            }
        });
        StringArray(fields, "evidence_sha256", minimum: 1, static value =>
            ControlContractValidation.RequireSha256(value, "evidence_sha256"),
            maximum: 64,
            maximumTotalAsciiBytes: 4096);
        var status = String(fields, "status");
        if (string.Equals(status, "active", StringComparison.Ordinal) && traitCount == 0)
        {
            throw new InvalidOperationException(
                "Active persona revisions must expose at least one trait key.");
        }
        if (string.Equals(status, "deleted", StringComparison.Ordinal) && traitCount != 0)
        {
            throw new InvalidOperationException("Deleted persona revisions cannot expose trait keys.");
        }
        return status;
    }

    private static string ValidateGBrainReadback(
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        RequireExactKeys(fields,
        [
            "schema_version", "contract_id", "producer_module", "soul_id",
            "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "privacy_class", "source_id",
            "projection_schema_version", "projection_contract_id",
            "projection_revision", "projection_checksum", "readback_checksum", "status"
        ]);
        if (!CanonicalZuluDateTime.IsMatch(String(fields, "occurred_at")))
        {
            throw new ArgumentException(
                "SoulMemory readback occurred_at must use its canonical Zulu wire.");
        }
        ControlContractValidation.RequireExact(String(fields, "privacy_class"), "personal", "privacy_class");
        // gbrain.projection/v2 derives source_id from (soul, nonce) with a domain-
        // separated SHA-256; the nonce never reaches this receipt, so the truncated-soul
        // re-derivation is gone. This layer enforces canonical format only; the binding
        // proof lives in GBrainProjectionV2.Validate() upstream, and Soul isolation here
        // remains keyed on the full soul_id scope.
        ControlContractValidation.RequireHex(String(fields, "source_id"), "dps-", 28, "source_id");
        ControlContractValidation.RequireMajor(String(fields, "projection_schema_version"), 2);
        ControlContractValidation.RequireExact(
            String(fields, "projection_contract_id"),
            "gbrain.projection/v2",
            "projection_contract_id");
        ControlContractValidation.RequireSha256(String(fields, "projection_revision"), "projection_revision");
        var projectionChecksum = String(fields, "projection_checksum");
        var readbackChecksum = String(fields, "readback_checksum");
        ControlContractValidation.RequireSha256(projectionChecksum, "projection_checksum");
        ControlContractValidation.RequireSha256(readbackChecksum, "readback_checksum");
        if (!FixedTimeHexEquals(projectionChecksum, readbackChecksum))
        {
            throw new InvalidOperationException("GBrain exact readback checksum does not match the projection.");
        }

        var status = String(fields, "status");
        ControlContractValidation.RequireExact(status, "verified", "status");
        return status;
    }

    private static Dictionary<string, JsonElement> ReadUniqueFields(JsonElement value)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new ArgumentException($"Provider payload repeats field '{property.Name}'.");
            }
        }
        return fields;
    }

    private static void RequireExactKeys(
        IReadOnlyDictionary<string, JsonElement> fields,
        IReadOnlyCollection<string> expected)
    {
        if (fields.Count != expected.Count
            || expected.Any(field => !fields.ContainsKey(field)))
        {
            throw new ArgumentException("Provider payload fields do not match its exact v1 schema.");
        }
    }

    private static string String(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Provider field '{name}' must be a string.");
        }
        return value.GetString() ?? throw new ArgumentException($"Provider field '{name}' is null.");
    }

    private static DateTimeOffset UtcTimestamp(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = String(fields, name);
        if (!UtcDateTime.IsMatch(value)
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"Provider field '{name}' must be an exact UTC timestamp.");
        }
        return parsed;
    }

    private static long PositiveInteger(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed < 1)
        {
            throw new ArgumentException($"Provider field '{name}' must be a positive integer.");
        }
        return parsed;
    }

    private static int StringArray(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        int minimum,
        Action<string> validate,
        int maximum = int.MaxValue,
        int maximumTotalAsciiBytes = int.MaxValue)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Provider field '{name}' must be an array.");
        }
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var totalAsciiBytes = 0;
        string? prior = null;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"Provider field '{name}' has a non-string item.");
            }
            var text = item.GetString() ?? throw new ArgumentException($"Provider field '{name}' has a null item.");
            validate(text);
            totalAsciiBytes = checked(totalAsciiBytes + Encoding.ASCII.GetByteCount(text));
            if (prior is not null && string.CompareOrdinal(prior, text) >= 0)
            {
                throw new ArgumentException(
                    $"Provider field '{name}' must be strictly ordinal-sorted.");
            }
            if (!observed.Add(text))
            {
                throw new ArgumentException($"Provider field '{name}' has a duplicate item.");
            }
            prior = text;
        }
        if (observed.Count < minimum)
        {
            throw new ArgumentException($"Provider field '{name}' has too few items.");
        }
        if (observed.Count > maximum || totalAsciiBytes > maximumTotalAsciiBytes)
        {
            throw new ArgumentException($"Provider field '{name}' exceeds its bounded array budget.");
        }
        return observed.Count;
    }

    private static void ValidateSlug(string value, int maximum = 128)
    {
        ControlContractValidation.RequireText(value, maximum, nameof(value));
        if (!IsLowerAlphaNumeric(value[0])
            || !IsLowerAlphaNumeric(value[^1])
            || value.Any(static character =>
                character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '.' and not '_' and not '-')
            || value.Zip(value.Skip(1)).Any(static pair =>
                IsSlugSeparator(pair.First) && IsSlugSeparator(pair.Second)))
        {
            throw new ArgumentException("Provider slug is invalid.");
        }
    }

    private static bool IsCanonicalP256SubjectPublicKeyInfo(
        ECDsa ecdsa,
        ReadOnlySpan<byte> encoded,
        int bytesRead)
    {
        if (bytesRead != encoded.Length
            || ecdsa.KeySize != 256
            || !string.Equals(
                ecdsa.ExportParameters(includePrivateParameters: false).Curve.Oid.Value,
                "1.2.840.10045.3.1.7",
                StringComparison.Ordinal))
        {
            return false;
        }

        var canonical = ecdsa.ExportSubjectPublicKeyInfo();
        try
        {
            return CryptographicOperations.FixedTimeEquals(canonical, encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static void RequireToken(
        string value,
        int maximum,
        string name,
        bool allowColon)
    {
        ControlContractValidation.RequireText(value, maximum, name);
        if (!IsAsciiAlphaNumeric(value[0])
            || value.Any(character =>
                !IsAsciiAlphaNumeric(character)
                && character is not '.' and not '_' and not '-'
                && (!allowColon || character != ':')))
        {
            throw new ArgumentException($"Provider token '{name}' is invalid.", name);
        }
    }

    private static void RequireLowercaseToken(
        string value,
        int maximum,
        string name)
    {
        ControlContractValidation.RequireText(value, maximum, name);
        if (!IsLowerAlphaNumeric(value[0])
            || value.Any(character =>
                !IsLowerAlphaNumeric(character)
                && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                $"Provider lowercase token '{name}' is invalid.",
                name);
        }
    }

    private static bool IsLowerAlpha(char value) => value is >= 'a' and <= 'z';

    private static bool IsLowerAlphaNumeric(char value)
        => IsLowerAlpha(value) || value is >= '0' and <= '9';

    private static bool IsSlugSeparator(char value) => value is '.' or '_' or '-';

    private static bool IsAsciiAlphaNumeric(char value)
        => value is >= 'A' and <= 'Z'
            || value is >= 'a' and <= 'z'
            || value is >= '0' and <= '9';

    private static void RequireHex(string value, string prefix, int length, string name)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + length
            || value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"Provider field '{name}' is invalid.", name);
        }
    }

    private static byte[] DecodeBase64(string value, string name)
    {
        ControlContractValidation.RequireText(value, 8192, name);
        try
        {
            var decoded = Convert.FromBase64String(value);
            if (!string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
                throw new ArgumentException(
                    $"Provider field '{name}' is not canonical base64.",
                    name);
            }
            return decoded;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"Provider field '{name}' is not canonical base64.", name, exception);
        }
    }

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexStringLower(SHA256.HashData(value));

    private static bool FixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("SHA-256 value is malformed.", exception);
        }
    }
}

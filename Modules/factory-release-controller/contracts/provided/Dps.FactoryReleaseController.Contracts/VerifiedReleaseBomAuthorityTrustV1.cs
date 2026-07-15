using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.FactoryReleaseController.Contracts;

public sealed class ReleaseBomAuthorityTrustAnchorsV1
{
    public ReleaseBomAuthorityTrustAnchorsV1(
        string trustPolicyId,
        string bomSignerKeyId,
        ReadOnlySpan<byte> bomSignerSubjectPublicKeyInfo,
        string authorityReceiptSignerKeyId,
        ReadOnlySpan<byte> authorityReceiptSignerSubjectPublicKeyInfo)
    {
        if (string.IsNullOrWhiteSpace(trustPolicyId) ||
            string.IsNullOrWhiteSpace(bomSignerKeyId) ||
            string.IsNullOrWhiteSpace(authorityReceiptSignerKeyId))
            throw new ArgumentException("Release trust anchor identities are required.");
        TrustPolicyId = trustPolicyId;
        BomSignerKeyId = bomSignerKeyId;
        AuthorityReceiptSignerKeyId = authorityReceiptSignerKeyId;
        BomSignerSubjectPublicKeyInfo = RequireCanonicalRsaSpki(
            bomSignerSubjectPublicKeyInfo, nameof(bomSignerSubjectPublicKeyInfo));
        AuthorityReceiptSignerSubjectPublicKeyInfo = RequireCanonicalRsaSpki(
            authorityReceiptSignerSubjectPublicKeyInfo,
            nameof(authorityReceiptSignerSubjectPublicKeyInfo));
        BomSignerSpkiSha256 = Convert.ToHexStringLower(
            SHA256.HashData(BomSignerSubjectPublicKeyInfo));
        AuthorityReceiptSignerSpkiSha256 = Convert.ToHexStringLower(
            SHA256.HashData(AuthorityReceiptSignerSubjectPublicKeyInfo));
        if (string.Equals(BomSignerKeyId, AuthorityReceiptSignerKeyId, StringComparison.Ordinal) ||
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(BomSignerSpkiSha256),
                Convert.FromHexString(AuthorityReceiptSignerSpkiSha256)))
            throw new InvalidDataException("BOM and authority-receipt signers must be distinct roles and keys.");
    }

    public string TrustPolicyId { get; }
    public string BomSignerKeyId { get; }
    public string AuthorityReceiptSignerKeyId { get; }
    public string BomSignerSpkiSha256 { get; }
    public string AuthorityReceiptSignerSpkiSha256 { get; }
    internal byte[] BomSignerSubjectPublicKeyInfo { get; }
    internal byte[] AuthorityReceiptSignerSubjectPublicKeyInfo { get; }

    private static byte[] RequireCanonicalRsaSpki(ReadOnlySpan<byte> raw, string name)
    {
        if (raw.IsEmpty || raw.Length > 4096)
            throw new InvalidDataException($"{name} is absent or oversized.");
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(raw, out var bytesRead);
            var canonical = rsa.ExportSubjectPublicKeyInfo();
            if (bytesRead != raw.Length || rsa.KeySize < 2048 || !raw.SequenceEqual(canonical))
                throw new InvalidDataException($"{name} is not canonical RSA SPKI or is below 2048 bits.");
            return canonical;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"{name} is not valid RSA SPKI.", exception);
        }
    }
}

public sealed class VerifiedNativeStopAuthorityV1
{
    internal VerifiedNativeStopAuthorityV1(NativeStopAuthorityV1 authority) => Authority = authority;
    internal NativeStopAuthorityV1 Authority { get; }
    public string AuthorityId => Authority.AuthorityId;
    public string WorkerArtifactSha256 => Authority.WorkerArtifactSha256;
    public string WorkerVersion => Authority.WorkerVersion;
    public string WorkerSlot => Authority.WorkerSlot;
    public string WorkerInstanceId => Authority.WorkerInstanceId;
    public long WorkerGeneration => Authority.WorkerGeneration;
    public string KeyId => Authority.KeyId;
    public string P256SpkiSha256 => Authority.P256SpkiSha256;
    public string WorkerAuthoritySha256 => Authority.WorkerAuthoritySha256;
    public long RotationEpoch => Authority.RotationEpoch;
    public string ValidFrom => Authority.ValidFrom;
    public string ValidUntil => Authority.ValidUntil;
}

public sealed class VerifiedDeviceRouteAssignmentAuthorityV1
{
    internal VerifiedDeviceRouteAssignmentAuthorityV1(DeviceRouteAssignmentAuthorityV1 authority) => Authority = authority;
    internal DeviceRouteAssignmentAuthorityV1 Authority { get; }
    public string RouteAuthorityId => Authority.RouteAuthorityId;
    public string SupervisorArtifactSha256 => Authority.SupervisorArtifactSha256;
    public string SupervisorVersion => Authority.SupervisorVersion;
    public string SupervisorInstanceId => Authority.SupervisorInstanceId;
    public long SupervisorGeneration => Authority.SupervisorGeneration;
    public string RouteSignerKeyId => Authority.RouteSignerKeyId;
    public string RouteSignerP256SpkiSha256 => Authority.RouteSignerP256SpkiSha256;
    public string RouteAuthoritySha256 => Authority.RouteAuthoritySha256;
    public long RotationEpoch => Authority.RotationEpoch;
    public string ValidFrom => Authority.ValidFrom;
    public string ValidUntil => Authority.ValidUntil;
}

public sealed class VerifiedNativeStopChallengeAuthorityV1
{
    internal VerifiedNativeStopChallengeAuthorityV1(NativeStopChallengeAuthorityV1 authority) => Authority = authority;
    internal NativeStopChallengeAuthorityV1 Authority { get; }
    public string AuthorityId => Authority.AuthorityId;
    public string PolicyArtifactSha256 => Authority.PolicyArtifactSha256;
    public string PolicyVersion => Authority.PolicyVersion;
    public string PolicyInstanceId => Authority.PolicyInstanceId;
    public long PolicyGeneration => Authority.PolicyGeneration;
    public string KeyId => Authority.KeyId;
    public string P256SpkiSha256 => Authority.P256SpkiSha256;
    public string ChallengeAuthoritySha256 => Authority.ChallengeAuthoritySha256;
    public long RotationEpoch => Authority.RotationEpoch;
    public string ValidFrom => Authority.ValidFrom;
    public string ValidUntil => Authority.ValidUntil;
}

public sealed class VerifiedReleaseBomAuthorityTrustV1
{
    internal VerifiedReleaseBomAuthorityTrustV1(
        ReleaseBomNativeStopAuthorityTrustReceiptV1 receipt,
        string receiptSha256)
    {
        ReleaseBomId = receipt.ReleaseBomId;
        ReleaseBomSha256 = receipt.ReleaseBomSha256;
        IntegrationCommit = receipt.IntegrationCommit;
        ReleaseBomGeneration = receipt.ReleaseBomGeneration;
        ActivationTokenSha256 = receipt.ActivationTokenSha256;
        TrustPolicyId = receipt.TrustPolicyId;
        AuthorityReceiptSha256 = receiptSha256;
        NativeStopAuthorities = Array.AsReadOnly(receipt.NativeStopAuthorities
            .Select(authority => new VerifiedNativeStopAuthorityV1(authority)).ToArray());
        DeviceRouteAssignmentAuthorities = Array.AsReadOnly(receipt.DeviceRouteAssignmentAuthorities
            .Select(authority => new VerifiedDeviceRouteAssignmentAuthorityV1(authority)).ToArray());
        NativeStopChallengeAuthorities = Array.AsReadOnly(receipt.NativeStopChallengeAuthorities
            .Select(authority => new VerifiedNativeStopChallengeAuthorityV1(authority)).ToArray());
    }

    public string ReleaseBomId { get; }
    public string ReleaseBomSha256 { get; }
    public string IntegrationCommit { get; }
    public long ReleaseBomGeneration { get; }
    public string ActivationTokenSha256 { get; }
    public string TrustPolicyId { get; }
    public string AuthorityReceiptSha256 { get; }
    public IReadOnlyList<VerifiedNativeStopAuthorityV1> NativeStopAuthorities { get; }
    public IReadOnlyList<VerifiedDeviceRouteAssignmentAuthorityV1> DeviceRouteAssignmentAuthorities { get; }
    public IReadOnlyList<VerifiedNativeStopChallengeAuthorityV1> NativeStopChallengeAuthorities { get; }

    public VerifiedDeviceRouteAssignmentAuthorityV1 RequireSingleDeviceRouteAssignmentAuthority(
        string routeAuthoritySha256,
        string supervisorArtifactSha256,
        string supervisorInstanceId,
        long supervisorGeneration,
        string routeSignerKeyId)
    {
        var matches = DeviceRouteAssignmentAuthorities.Where(authority =>
            FixedShaEquals(authority.RouteAuthoritySha256, routeAuthoritySha256) &&
            FixedShaEquals(authority.SupervisorArtifactSha256, supervisorArtifactSha256) &&
            string.Equals(authority.SupervisorInstanceId, supervisorInstanceId, StringComparison.Ordinal) &&
            authority.SupervisorGeneration == supervisorGeneration &&
            string.Equals(authority.RouteSignerKeyId, routeSignerKeyId, StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException("Exactly one verified device route authority must match the assignment scope.");
    }

    public VerifiedNativeStopAuthorityV1 RequireSingleNativeStopAuthority(
        string workerAuthoritySha256,
        string workerArtifactSha256,
        string workerSlot,
        string workerInstanceId,
        long workerGeneration,
        string keyId)
    {
        var matches = NativeStopAuthorities.Where(authority =>
            FixedShaEquals(authority.WorkerAuthoritySha256, workerAuthoritySha256) &&
            FixedShaEquals(authority.WorkerArtifactSha256, workerArtifactSha256) &&
            string.Equals(authority.WorkerSlot, workerSlot, StringComparison.Ordinal) &&
            string.Equals(authority.WorkerInstanceId, workerInstanceId, StringComparison.Ordinal) &&
            authority.WorkerGeneration == workerGeneration &&
            string.Equals(authority.KeyId, keyId, StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException("Exactly one verified native stop authority must match the proof scope.");
    }

    public VerifiedNativeStopChallengeAuthorityV1 RequireSingleNativeStopChallengeAuthority(
        string challengeAuthoritySha256,
        string policyArtifactSha256,
        string policyInstanceId,
        long policyGeneration,
        string keyId)
    {
        var matches = NativeStopChallengeAuthorities.Where(authority =>
            FixedShaEquals(authority.ChallengeAuthoritySha256, challengeAuthoritySha256) &&
            FixedShaEquals(authority.PolicyArtifactSha256, policyArtifactSha256) &&
            string.Equals(authority.PolicyInstanceId, policyInstanceId, StringComparison.Ordinal) &&
            authority.PolicyGeneration == policyGeneration &&
            string.Equals(authority.KeyId, keyId, StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException("Exactly one verified challenge authority must match the challenge scope.");
    }

    private static bool FixedShaEquals(string left, string right)
    {
        try
        {
            return left.Length == 64 && right.Length == 64 &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class ReleaseBomAuthorityTrustVerifierV1
{
    private static readonly HashSet<string> BomFields = new(StringComparer.Ordinal)
    {
        "schema_version", "bom_id", "status", "integration_commit", "created_at",
        "release_bom_generation", "activation_token_sha256", "modules",
        "instruction_hashes", "contracts", "database_versions", "dependency_dag_sha256",
        "compatibility_matrix_sha256", "feature_flags", "kill_switches", "ai_toolchain",
        "evidence", "risk", "release_approval", "rollout", "rollback",
        "previous_stable_bom", "previous_stable_bom_sha256", "native_stop_authorities",
        "device_route_assignment_authorities", "native_stop_challenge_authorities", "signature",
    };

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 48,
    };

    private static readonly JsonSerializerOptions CanonicalStringJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static VerifiedReleaseBomAuthorityTrustV1 Verify(
        ReadOnlySpan<byte> exactSignedBomJson,
        ReadOnlySpan<byte> exactSignedAuthorityReceiptJson,
        ReleaseBomAuthorityTrustAnchorsV1 anchors,
        DateTimeOffset verificationTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (exactSignedBomJson.IsEmpty || exactSignedBomJson.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Signed Release BOM is absent or oversized.");
        if (verificationTimeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Verification time must be UTC.", nameof(verificationTimeUtc));

        var receiptBytes = exactSignedAuthorityReceiptJson.ToArray();
        var receipt = NativeStopAuthorityTrustProtocolV1.DeserializeStrict(receiptBytes);
        if (!string.Equals(receipt.TrustPolicyId, anchors.TrustPolicyId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Signature.KeyId, anchors.AuthorityReceiptSignerKeyId, StringComparison.Ordinal))
            throw new InvalidDataException("Authority receipt trust-policy or signer key does not match the pinned anchor.");
        VerifyRsaPss(
            anchors.AuthorityReceiptSignerSubjectPublicKeyInfo,
            NativeStopAuthorityTrustProtocolV1.CanonicalReceiptSigningBytes(receipt),
            receipt.Signature.Value,
            "authority receipt");

        var bomBytes = exactSignedBomJson.ToArray();
        var bomSha256 = Convert.ToHexStringLower(SHA256.HashData(bomBytes));
        if (!FixedShaEquals(bomSha256, receipt.ReleaseBomSha256))
            throw new InvalidDataException("Authority receipt does not bind the exact signed Release BOM bytes.");
        using var document = ParseStrict(bomBytes, "Release BOM");
        RequireExactObject(document.RootElement, BomFields, "Release BOM");
        RejectDuplicateMembers(document.RootElement);
        var root = document.RootElement;
        RequireString(root, "schema_version", "dps.release-bom/v1");
        RequireString(root, "bom_id", receipt.ReleaseBomId);
        RequireString(root, "integration_commit", receipt.IntegrationCommit);
        RequireString(root, "activation_token_sha256", receipt.ActivationTokenSha256);
        if (root.GetProperty("release_bom_generation").GetInt64() != receipt.ReleaseBomGeneration)
            throw new InvalidDataException("Release BOM generation differs from its trust receipt.");

        var signatureElement = root.GetProperty("signature");
        RequireExactObject(signatureElement, new HashSet<string>(StringComparer.Ordinal)
            { "algorithm", "key_id", "value" }, "Release BOM signature");
        RequireString(signatureElement, "algorithm", "rsa-pss-sha256");
        RequireString(signatureElement, "key_id", anchors.BomSignerKeyId);
        using var payload = BuildBomPayloadWithoutSignature(root);
        var canonicalPayload = Canonicalize(payload.RootElement);
        var bomMessage = Encoding.UTF8.GetBytes("dps-release-bom/v1\n")
            .Concat(canonicalPayload).ToArray();
        VerifyRsaPss(
            anchors.BomSignerSubjectPublicKeyInfo,
            bomMessage,
            signatureElement.GetProperty("value").GetString() ?? string.Empty,
            "Release BOM");

        var nativeAuthorities = DeserializeAuthorities<NativeStopAuthorityV1>(
            root.GetProperty("native_stop_authorities"), "native stop authorities");
        var routeAuthorities = DeserializeAuthorities<DeviceRouteAssignmentAuthorityV1>(
            root.GetProperty("device_route_assignment_authorities"), "device route authorities");
        var challengeAuthorities = DeserializeAuthorities<NativeStopChallengeAuthorityV1>(
            root.GetProperty("native_stop_challenge_authorities"), "native stop challenge authorities");
        if (!nativeAuthorities.SequenceEqual(receipt.NativeStopAuthorities) ||
            !routeAuthorities.SequenceEqual(receipt.DeviceRouteAssignmentAuthorities) ||
            !challengeAuthorities.SequenceEqual(receipt.NativeStopChallengeAuthorities))
            throw new InvalidDataException("Release BOM authority arrays differ from the independently signed receipt.");

        var occurredAt = ParseCanonicalUtc(receipt.OccurredAt, "receipt occurred_at");
        if (occurredAt > verificationTimeUtc)
            throw new InvalidDataException("Authority receipt is from the future.");
        foreach (var authority in receipt.NativeStopAuthorities)
            RequireActive(authority.ValidFrom, authority.ValidUntil, verificationTimeUtc, "native stop authority");
        foreach (var authority in receipt.DeviceRouteAssignmentAuthorities)
            RequireActive(authority.ValidFrom, authority.ValidUntil, verificationTimeUtc, "device route authority");
        foreach (var authority in receipt.NativeStopChallengeAuthorities)
            RequireActive(authority.ValidFrom, authority.ValidUntil, verificationTimeUtc, "native stop challenge authority");

        var forbiddenReleaseSpki = new HashSet<string>(StringComparer.Ordinal)
        {
            anchors.BomSignerSpkiSha256,
            anchors.AuthorityReceiptSignerSpkiSha256,
        };
        if (receipt.NativeStopAuthorities.Any(item => forbiddenReleaseSpki.Contains(item.P256SpkiSha256)) ||
            receipt.DeviceRouteAssignmentAuthorities.Any(item => forbiddenReleaseSpki.Contains(item.RouteSignerP256SpkiSha256)) ||
            receipt.NativeStopChallengeAuthorities.Any(item => forbiddenReleaseSpki.Contains(item.P256SpkiSha256)))
            throw new InvalidDataException("Runtime authority key material cannot reuse a Release trust signer key.");

        return new VerifiedReleaseBomAuthorityTrustV1(
            receipt, Convert.ToHexStringLower(SHA256.HashData(receiptBytes)));
    }

    private static IReadOnlyList<T> DeserializeAuthorities<T>(JsonElement element, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(element.GetRawText(), StrictJson)
                ?? throw new InvalidDataException($"{label} decoded to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} are not strict contract JSON.", exception);
        }
    }

    private static JsonDocument ParseStrict(byte[] exactJson, string label)
    {
        try
        {
            return JsonDocument.Parse(exactJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 48,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is not strict JSON.", exception);
        }
    }

    private static JsonDocument BuildBomPayloadWithoutSignature(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("signature"))
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private static byte[] Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        WriteCanonical(element, stream);
        return stream.ToArray();
    }

    private static void WriteCanonical(JsonElement element, Stream destination)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteAscii(destination, "{");
                var properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                for (var index = 0; index < properties.Length; index++)
                {
                    if (index > 0) WriteAscii(destination, ",");
                    WriteAscii(destination, JsonSerializer.Serialize(properties[index].Name, CanonicalStringJson));
                    WriteAscii(destination, ":");
                    WriteCanonical(properties[index].Value, destination);
                }
                WriteAscii(destination, "}");
                break;
            case JsonValueKind.Array:
                WriteAscii(destination, "[");
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) WriteAscii(destination, ",");
                    first = false;
                    WriteCanonical(item, destination);
                }
                WriteAscii(destination, "]");
                break;
            case JsonValueKind.String:
                WriteAscii(destination, JsonSerializer.Serialize(element.GetString(), CanonicalStringJson));
                break;
            case JsonValueKind.Number:
                WriteAscii(destination, CanonicalNumber(element));
                break;
            case JsonValueKind.True:
                WriteAscii(destination, "true");
                break;
            case JsonValueKind.False:
                WriteAscii(destination, "false");
                break;
            case JsonValueKind.Null:
                WriteAscii(destination, "null");
                break;
            default:
                throw new InvalidDataException("Unsupported JSON value in signed BOM.");
        }
    }

    private static string CanonicalNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);
        if (!element.TryGetDouble(out var value) || double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException("Signed BOM contains an unsupported JSON number.");
        return value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    private static void WriteAscii(Stream destination, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        destination.Write(bytes);
    }

    private static void VerifyRsaPss(
        byte[] subjectPublicKeyInfo,
        byte[] message,
        string signatureBase64,
        string label)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{label} signature is not base64.", exception);
        }
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
        if (bytesRead != subjectPublicKeyInfo.Length ||
            !rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException($"{label} RSA-PSS signature is invalid.");
    }

    private static void RequireActive(
        string validFrom,
        string validUntil,
        DateTimeOffset now,
        string label)
    {
        if (!(ParseCanonicalUtc(validFrom, label + " valid_from") <= now &&
              now < ParseCanonicalUtc(validUntil, label + " valid_until")))
            throw new InvalidDataException($"{label} is not active at verification time.");
    }

    private static DateTimeOffset ParseCanonicalUtc(string value, string name)
    {
        if (!DateTimeOffset.TryParseExact(
                value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) || parsed.Offset != TimeSpan.Zero ||
            parsed < new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) ||
            !string.Equals(
                parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
                value, StringComparison.Ordinal))
            throw new InvalidDataException($"{name} is not canonical seven-digit UTC.");
        return parsed;
    }

    private static void RequireString(JsonElement owner, string property, string expected)
    {
        if (owner.GetProperty(property).ValueKind != JsonValueKind.String ||
            !string.Equals(owner.GetProperty(property).GetString(), expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Release BOM {property} differs from the trusted binding.");
    }

    private static void RequireExactObject(JsonElement element, IReadOnlySet<string> expected, string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} must be an object.");
        var actual = element.EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw new InvalidDataException($"{label} properties do not match the exact contract.");
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

    private static bool FixedShaEquals(string left, string right)
    {
        try
        {
            return left.Length == 64 && right.Length == 64 &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

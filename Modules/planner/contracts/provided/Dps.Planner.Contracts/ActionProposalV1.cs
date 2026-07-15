using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.Planner.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionProposalV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("action_kind")] string ActionKind,
    [property: JsonPropertyName("is_side_effect")] bool IsSideEffect,
    [property: JsonPropertyName("shadow_only")] bool ShadowOnly,
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, string> Parameters,
    [property: JsonPropertyName("evidence_refs")] IReadOnlyList<string> EvidenceRefs)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "action.proposal/v1";
    public const string CurrentProducerModule = "planner";
    public const int MaximumEvidenceReferences = 16;

    /// <summary>
    /// Validates the complete proposal and returns a frozen, ordinally sorted
    /// snapshot. Consumers must use the returned value and must not re-read the
    /// caller-owned record or its collection references after this call.
    /// </summary>
    public ActionProposalV1 CreateImmutableSnapshot()
    {
        var collections = ValidateAndSnapshotCollections();
        return this with
        {
            Parameters = collections.Parameters,
            EvidenceRefs = collections.EvidenceRefs
        };
    }

    private ProposalCollections ValidateAndSnapshotCollections()
    {
        ProposalContractGuard.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        ProposalContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ProposalContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ProposalContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        ProposalContractGuard.RequireGuid(ProposalId, nameof(ProposalId));
        ProposalContractGuard.RequireTraceId(TraceId);
        ProposalContractGuard.RequireIdempotencyKey(IdempotencyKey);
        ActionProposalIdentity.RequireDerived(
            ProposalId,
            SoulId,
            DeviceBindingId,
            PlatformAccountId,
            IdempotencyKey);
        ProposalContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        ProposalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));

        if (!ShadowOnly)
        {
            throw new InvalidOperationException("Planner proposals are shadow-only and carry no execution authority.");
        }

        var action = ActionProposalRules.Get(ActionKind);
        if (IsSideEffect != action.IsSideEffect)
        {
            throw new InvalidOperationException("The side-effect classification does not match the action allowlist.");
        }

        return new ProposalCollections(
            SnapshotParameters(Parameters, action.RequiredParameters, ActionKind),
            SnapshotEvidence(EvidenceRefs));
    }

    private static IReadOnlyDictionary<string, string> SnapshotParameters(
        IReadOnlyDictionary<string, string> source,
        IReadOnlySet<string> required,
        string actionKind)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var observed = 0;
        foreach (var pair in source)
        {
            observed++;
            if (observed > 2 || !result.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException("Parameters must be a bounded unique-key map.", nameof(Parameters));
            }

            if (!required.Contains(pair.Key))
            {
                throw new NotSupportedException($"Parameter '{pair.Key}' is not allowed for action '{actionKind}'.");
            }

            switch (pair.Key)
            {
                case "selector_ref":
                case "value_ref":
                    ProposalContractGuard.RequireOpaqueReference(pair.Value, pair.Key);
                    break;
                case "duration_ms":
                    ProposalContractGuard.RequireDuration(pair.Value);
                    break;
                default:
                    throw new NotSupportedException($"Parameter '{pair.Key}' is not supported.");
            }
        }

        if (observed != source.Count || result.Count != required.Count || required.Any(key => !result.ContainsKey(key)))
        {
            throw new ArgumentException("Parameters must exactly match the selected action contract.", nameof(Parameters));
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static IReadOnlyList<string> SnapshotEvidence(IReadOnlyList<string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var observed = 0;
        foreach (var evidenceReference in source)
        {
            observed++;
            if (observed > MaximumEvidenceReferences || !unique.Add(evidenceReference))
            {
                throw new ArgumentException(
                    $"Evidence references must contain at most {MaximumEvidenceReferences} unique values.",
                    nameof(EvidenceRefs));
            }

            ProposalContractGuard.RequireEvidenceReference(evidenceReference);
            result.Add(evidenceReference);
        }

        if (observed != source.Count)
        {
            throw new ArgumentException("Evidence collection cardinality changed during validation.", nameof(EvidenceRefs));
        }

        result.Sort(StringComparer.Ordinal);
        return Array.AsReadOnly(result.ToArray());
    }

    private sealed record ProposalCollections(
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyList<string> EvidenceRefs);
}

public static class ActionProposalRules
{
    private static readonly IReadOnlyDictionary<string, ActionDefinition> Actions =
        new Dictionary<string, ActionDefinition>(StringComparer.Ordinal)
        {
            ["observe"] = new(false, new HashSet<string>(StringComparer.Ordinal)),
            ["locate"] = new(false, new HashSet<string>(["selector_ref"], StringComparer.Ordinal)),
            ["verify"] = new(false, new HashSet<string>(["selector_ref"], StringComparer.Ordinal)),
            ["wait"] = new(false, new HashSet<string>(["duration_ms"], StringComparer.Ordinal)),
            ["fixture.tap"] = new(true, new HashSet<string>(["selector_ref"], StringComparer.Ordinal)),
            ["fixture.type"] = new(true, new HashSet<string>(["selector_ref", "value_ref"], StringComparer.Ordinal))
        };

    public static bool IsSideEffect(string actionKind) => Get(actionKind).IsSideEffect;

    internal static ActionDefinition Get(string actionKind)
    {
        ProposalContractGuard.RequireStrictUtf8(actionKind, nameof(actionKind));
        if (!Actions.TryGetValue(actionKind, out var action))
        {
            throw new NotSupportedException($"Unknown action '{actionKind}'.");
        }

        return action;
    }

    internal sealed record ActionDefinition(bool IsSideEffect, IReadOnlySet<string> RequiredParameters);
}

public static class ActionProposalV1Json
{
    public const int MaximumWireBytes = 32 * 1024;

    private static readonly IReadOnlySet<string> RequiredRootProperties = new HashSet<string>(
        [
            "schema_version", "contract_id", "producer_module", "proposal_id", "soul_id",
            "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
            "occurred_at", "privacy_class", "action_kind", "is_side_effect", "shadow_only",
            "parameters", "evidence_refs"
        ],
        StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        AllowTrailingCommas = false,
        MaxDepth = 8,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static ActionProposalV1 Deserialize(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > MaximumWireBytes)
        {
            throw new JsonException($"action.proposal/v1 must contain between 1 and {MaximumWireBytes} UTF-8 bytes.");
        }

        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        ValidateWireObject(document.RootElement);

        var proposal = JsonSerializer.Deserialize<ActionProposalV1>(json, SerializerOptions)
            ?? throw new JsonException("action.proposal/v1 cannot be null.");
        return proposal.CreateImmutableSnapshot();
    }

    public static byte[] Serialize(ActionProposalV1 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var wire = JsonSerializer.SerializeToUtf8Bytes(proposal.CreateImmutableSnapshot(), SerializerOptions);
        if (wire.Length > MaximumWireBytes)
        {
            throw new JsonException($"action.proposal/v1 exceeds {MaximumWireBytes} UTF-8 bytes.");
        }
        return wire;
    }

    private static void ValidateWireObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("action.proposal/v1 must be a JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!RequiredRootProperties.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new JsonException($"Unknown or duplicate property '{property.Name}'.");
            }
        }

        if (seen.Count != RequiredRootProperties.Count)
        {
            throw new JsonException("action.proposal/v1 is missing a required property.");
        }

        var parameters = root.GetProperty("parameters");
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("parameters must be an object.");
        }

        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters.EnumerateObject())
        {
            if (!parameterNames.Add(parameter.Name))
            {
                throw new JsonException($"Duplicate parameter '{parameter.Name}'.");
            }
        }

        ProposalContractGuard.RequireWireUuid(RequireString(root, "proposal_id"));
        ProposalContractGuard.RequireWireTimestamp(RequireString(root, "occurred_at"));
    }

    private static string RequireString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{propertyName} must be a string.");
        }

        return value.GetString() ?? throw new JsonException($"{propertyName} cannot be null.");
    }
}

public static class ActionProposalCanonical
{
    public const string Domain = "dps.planner.action-proposal-sha256/v1";

    public static string ComputeSha256(ActionProposalV1 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var snapshot = proposal.CreateImmutableSnapshot();
        using var writer = new LengthPrefixedCanonicalWriter();
        writer.Field(Domain);
        writer.Field(snapshot.SchemaVersion);
        writer.Field(snapshot.ContractId);
        writer.Field(snapshot.ProducerModule);
        writer.Field(snapshot.ProposalId.ToString("N"));
        writer.Field(snapshot.SoulId);
        writer.Field(snapshot.DeviceBindingId);
        writer.Field(snapshot.PlatformAccountId);
        writer.Field(snapshot.TraceId);
        writer.Field(snapshot.IdempotencyKey);
        writer.Field(snapshot.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        writer.Field(snapshot.PrivacyClass);
        writer.Field(snapshot.ActionKind);
        writer.Field(snapshot.IsSideEffect ? "true" : "false");
        writer.Field(snapshot.ShadowOnly ? "true" : "false");
        writer.Field(snapshot.Parameters.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in snapshot.Parameters)
        {
            writer.Field(pair.Key);
            writer.Field(pair.Value);
        }
        writer.Field(snapshot.EvidenceRefs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var evidenceReference in snapshot.EvidenceRefs)
        {
            writer.Field(evidenceReference);
        }

        return writer.ComputeSha256();
    }
}

public static class ActionProposalIdentity
{
    public const string Domain = "dps.planner.proposal-id/v1";

    public static Guid Create(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string idempotencyKey)
    {
        ProposalContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId);
        ProposalContractGuard.RequireIdempotencyKey(idempotencyKey);

        using var writer = new LengthPrefixedCanonicalWriter();
        writer.Field(Domain);
        writer.Field(soulId);
        writer.Field(deviceBindingId);
        writer.Field(platformAccountId);
        writer.Field(idempotencyKey);
        var digest = writer.ComputeSha256Bytes();
        try
        {
            Span<byte> guidBytes = stackalloc byte[16];
            digest.AsSpan(0, guidBytes.Length).CopyTo(guidBytes);
            // RFC 9562 UUIDv8 is the application-defined UUID space. This
            // construction deliberately remains SHA-256, not UUIDv5/SHA-1.
            guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x80);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
            return new Guid(guidBytes, bigEndian: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public static void RequireDerived(
        Guid proposalId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string idempotencyKey)
    {
        var expected = Create(soulId, deviceBindingId, platformAccountId, idempotencyKey);
        var expectedBytes = expected.ToByteArray(bigEndian: true);
        var actualBytes = proposalId.ToByteArray(bigEndian: true);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            {
                throw new ArgumentException(
                    "proposal_id does not match its canonical Soul, device, account, and idempotency derivation.",
                    nameof(proposalId));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }
}

internal sealed class LengthPrefixedCanonicalWriter : IDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly MemoryStream _stream = new();

    internal void Field(string value)
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

    internal string ComputeSha256()
    {
        var digest = ComputeSha256Bytes();
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal byte[] ComputeSha256Bytes()
    {
        var canonical = _stream.ToArray();
        try
        {
            return SHA256.HashData(canonical);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    public void Dispose() => _stream.Dispose();
}

public static class ProposalContractGuard
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex SoulIdPattern = Pattern("\\Asoul_[a-f0-9]{64}\\z");
    private static readonly Regex DeviceBindingIdPattern = Pattern("\\Adb_[a-f0-9]{32}\\z");
    private static readonly Regex PlatformAccountIdPattern = Pattern("\\Apa_[a-f0-9]{32}\\z");
    private static readonly Regex TraceIdPattern = Pattern("\\Atrace_[a-f0-9]{32}\\z");
    private static readonly Regex IdempotencyKeyPattern = Pattern("\\Aidem_[a-f0-9]{64}\\z");
    private static readonly Regex OpaqueReferencePattern = Pattern("\\A[A-Za-z0-9][A-Za-z0-9._:,=-]{0,127}\\z");
    private static readonly Regex EvidenceReferencePattern = Pattern("\\Aevidence:[A-Za-z0-9][A-Za-z0-9._:,=-]{0,246}\\z");
    private static readonly Regex DurationPattern = Pattern("\\A(?:[1-9][0-9]{0,4}|[1-5][0-9]{5}|600000)\\z");
    private static readonly Regex WireUuidPattern = Pattern("\\A[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z");
    private static readonly Regex WireTimestampPattern = Pattern("\\A[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\\.[0-9]{1,7})?(?:Z|\\+00:00)\\z");

    public static void RequireExact(string actual, string expected, string name)
    {
        RequireStrictUtf8(actual, name);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported {name} '{actual}'. Expected '{expected}'.");
        }
    }

    public static void RequireScope(string soulId, string deviceBindingId, string platformAccountId)
    {
        RequirePattern(soulId, SoulIdPattern, "soul_id must be an opaque soul_ identifier.", nameof(soulId));
        RequirePattern(deviceBindingId, DeviceBindingIdPattern, "device_binding_id must be an opaque db_ identifier.", nameof(deviceBindingId));
        RequirePattern(platformAccountId, PlatformAccountIdPattern, "platform_account_id must be an opaque pa_ identifier.", nameof(platformAccountId));
    }

    public static void RequireTraceId(string value)
        => RequirePattern(value, TraceIdPattern, "trace_id must be a bounded opaque trace identifier.", nameof(value));

    public static void RequireIdempotencyKey(string value)
        => RequirePattern(value, IdempotencyKeyPattern, "idempotency_key must be a bounded opaque idempotency identifier.", nameof(value));

    public static void RequireOpaqueReference(string value, string name)
        => RequirePattern(value, OpaqueReferencePattern, $"{name} must be a bounded opaque reference.", name);

    public static void RequireEvidenceReference(string value)
        => RequirePattern(value, EvidenceReferencePattern, "evidence_refs must contain bounded opaque evidence references.", nameof(value));

    public static void RequireDuration(string value)
    {
        RequirePattern(value, DurationPattern, "duration_ms must be the canonical decimal form of 1 through 600000.", nameof(value));
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var duration)
            || duration is < 1 or > 600000
            || !string.Equals(duration.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw new ArgumentException("duration_ms must be the canonical decimal form of 1 through 600000.", nameof(value));
        }
    }

    public static void RequireGuid(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException($"{name} cannot be empty.", name);
        }
        var canonical = value.ToString("D");
        if (canonical[14] != '8' || "89ab".IndexOf(canonical[19], StringComparison.Ordinal) < 0)
        {
            throw new ArgumentException($"{name} must be an RFC 9562 UUIDv8 with the RFC variant.", name);
        }
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be UTC.", name);
        }
    }

    public static void RequireStrictUtf8(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        _ = StrictUtf8.GetByteCount(value);
    }

    internal static void RequireWireUuid(string value)
    {
        RequirePattern(value, WireUuidPattern, "proposal_id must use the canonical lowercase UUID wire form.", nameof(value));
        if (!Guid.TryParseExact(value, "D", out var guid) || guid == Guid.Empty)
        {
            throw new JsonException("proposal_id must be a non-empty canonical RFC 9562 UUIDv8.");
        }
    }

    internal static void RequireWireTimestamp(string value)
    {
        RequirePattern(value, WireTimestampPattern, "occurred_at must use canonical UTC RFC 3339 form.", nameof(value));
        if (!DateTimeOffset.TryParseExact(
                value,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp)
            || timestamp.Offset != TimeSpan.Zero)
        {
            throw new JsonException("occurred_at must be a valid UTC timestamp.");
        }
    }

    private static void RequirePattern(string value, Regex pattern, string message, string name)
    {
        RequireStrictUtf8(value, name);
        if (!pattern.IsMatch(value))
        {
            throw new ArgumentException(message, name);
        }
    }

    private static Regex Pattern(string expression)
        => new(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}

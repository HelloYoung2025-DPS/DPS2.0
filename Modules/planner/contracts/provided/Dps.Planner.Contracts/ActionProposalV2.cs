using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.Planner.Contracts;

/// <summary>
/// Strict typed-reference proposal contract. Planner production output uses this
/// major. It remains shadow-only and carries no approval or execution authority.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionProposalV2(
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
    public const string CurrentSchemaVersion = "2.0.0";
    public const string CurrentContractId = "action.proposal/v2";
    public const string CurrentProducerModule = "planner";
    public const int MaximumEvidenceReferences = 16;

    public ActionProposalV2 CreateImmutableSnapshot()
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
                    ActionProposalV2ReferenceGuard.RequireSelectorReference(pair.Value);
                    break;
                case "value_ref":
                    ActionProposalV2ReferenceGuard.RequireValueReference(pair.Value);
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

            ActionProposalV2ReferenceGuard.RequireEvidenceReference(evidenceReference);
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

public static class ActionProposalV2Json
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

    public static ActionProposalV2 Deserialize(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > MaximumWireBytes)
        {
            throw new JsonException($"action.proposal/v2 must contain between 1 and {MaximumWireBytes} UTF-8 bytes.");
        }

        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        ValidateWireObject(document.RootElement);

        var proposal = JsonSerializer.Deserialize<ActionProposalV2>(json, SerializerOptions)
            ?? throw new JsonException("action.proposal/v2 cannot be null.");
        return proposal.CreateImmutableSnapshot();
    }

    public static byte[] Serialize(ActionProposalV2 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var wire = JsonSerializer.SerializeToUtf8Bytes(proposal.CreateImmutableSnapshot(), SerializerOptions);
        if (wire.Length > MaximumWireBytes)
        {
            throw new JsonException($"action.proposal/v2 exceeds {MaximumWireBytes} UTF-8 bytes.");
        }
        return wire;
    }

    private static void ValidateWireObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("action.proposal/v2 must be a JSON object.");
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
            throw new JsonException("action.proposal/v2 is missing a required property.");
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

public static class ActionProposalV2Canonical
{
    public const string Domain = "dps.planner.action-proposal-sha256/v2";

    public static string ComputeSha256(ActionProposalV2 proposal)
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

internal static class ActionProposalV2ReferenceGuard
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex SelectorReferencePattern = Pattern("\\Aselector_[a-f0-9]{64}\\z");
    private static readonly Regex ValueReferencePattern = Pattern("\\Avalue_[a-f0-9]{64}\\z");
    private static readonly Regex EvidenceReferencePattern = Pattern("\\Aevidence_[a-f0-9]{64}\\z");

    internal static void RequireSelectorReference(string value)
        => RequirePattern(
            value,
            SelectorReferencePattern,
            "selector_ref must be a typed selector_ SHA-256 reference.");

    internal static void RequireValueReference(string value)
        => RequirePattern(
            value,
            ValueReferencePattern,
            "value_ref must be a typed value_ SHA-256 reference.");

    internal static void RequireEvidenceReference(string value)
        => RequirePattern(
            value,
            EvidenceReferencePattern,
            "evidence_refs must contain typed evidence_ SHA-256 references.");

    private static void RequirePattern(string value, Regex pattern, string message)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = StrictUtf8.GetByteCount(value);
        if (!pattern.IsMatch(value))
        {
            throw new ArgumentException(message, nameof(value));
        }
    }

    private static Regex Pattern(string expression)
        => new(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}

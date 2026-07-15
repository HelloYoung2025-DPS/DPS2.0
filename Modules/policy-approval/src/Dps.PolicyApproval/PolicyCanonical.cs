using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;

namespace Dps.PolicyApproval;

internal static class PolicyCanonicalization
{
    private const int MaximumParameters = 16;
    private const int MaximumEvidenceReferences = 64;
    private const int MaximumPolicyIds = 32;
    private const int MaximumDenialReasons = 32;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RequiredParameters =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["observe"] = new HashSet<string>(StringComparer.Ordinal),
            ["locate"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal),
            ["verify"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal),
            ["wait"] = new HashSet<string>(["duration_ms"], StringComparer.Ordinal),
            ["fixture.tap"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal),
            ["fixture.type"] = new HashSet<string>(["selector_ref", "value_ref"], StringComparer.Ordinal)
        };

    internal static ActionProposalV1 SnapshotProposal(ActionProposalV1 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        // The Planner contract performs validation and collection capture in one
        // pass. From this point onward, never re-read the caller-owned proposal:
        // mutable/flipping collections must not gain a second observation window.
        var snapshot = proposal.CreateImmutableSnapshot();
        RequireStrictUtf8(
            snapshot.SchemaVersion, snapshot.ContractId, snapshot.ProducerModule, snapshot.SoulId,
            snapshot.DeviceBindingId, snapshot.PlatformAccountId, snapshot.TraceId,
            snapshot.IdempotencyKey, snapshot.PrivacyClass, snapshot.ActionKind);

        if (!RequiredParameters.TryGetValue(snapshot.ActionKind, out var required))
        {
            throw new NotSupportedException($"Unknown action '{snapshot.ActionKind}'.");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Parameters)
        {
            if (parameters.Count >= MaximumParameters || !parameters.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException("Proposal parameters must be a bounded unique-key map.", nameof(proposal));
            }
            RequireStrictUtf8(pair.Key, pair.Value);
        }
        if (parameters.Count != snapshot.Parameters.Count
            || parameters.Keys.Except(required, StringComparer.Ordinal).Any()
            || required.Any(key => !parameters.ContainsKey(key)))
        {
            throw new ArgumentException("Proposal parameters do not exactly match the action allowlist.", nameof(proposal));
        }

        var evidenceReferences = SnapshotUniqueStrings(
            snapshot.EvidenceRefs,
            MaximumEvidenceReferences,
            256,
            requireAtLeastOne: false,
            nameof(snapshot.EvidenceRefs));
        return snapshot with
        {
            Parameters = new ReadOnlyDictionary<string, string>(parameters),
            EvidenceRefs = Array.AsReadOnly(evidenceReferences)
        };
    }

    internal static ApprovalDecisionV1 SnapshotDecision(ApprovalDecisionV1 decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision.Validate();
        RequireStrictUtf8(
            decision.SchemaVersion, decision.ContractId, decision.ProducerModule, decision.SoulId,
            decision.DeviceBindingId, decision.PlatformAccountId, decision.TraceId,
            decision.IdempotencyKey, decision.PrivacyClass, decision.ActionKind, decision.Decision,
            decision.Authority, decision.PolicyVersion);
        if (decision.PlatformAuthorizationId is not null)
        {
            RequireStrictUtf8(decision.PlatformAuthorizationId);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in decision.Parameters)
        {
            if (parameters.Count >= MaximumParameters || !parameters.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException("Decision parameters must be a bounded unique-key map.", nameof(decision));
            }
            RequireStrictUtf8(pair.Key, pair.Value);
        }
        if (parameters.Count != decision.Parameters.Count)
        {
            throw new ArgumentException("Decision parameters expose inconsistent collection cardinality.", nameof(decision));
        }

        var policies = SnapshotUniqueStrings(
            decision.EvaluatedPolicyIds,
            MaximumPolicyIds,
            64,
            requireAtLeastOne: true,
            nameof(decision.EvaluatedPolicyIds));
        var reasons = SnapshotUniqueStrings(
            decision.DenialReasons,
            MaximumDenialReasons,
            128,
            requireAtLeastOne: decision.Decision == ApprovalDecisionV1.Denied,
            nameof(decision.DenialReasons));
        var snapshot = decision with
        {
            Parameters = new ReadOnlyDictionary<string, string>(parameters),
            EvaluatedPolicyIds = Array.AsReadOnly(policies),
            DenialReasons = Array.AsReadOnly(reasons)
        };
        snapshot.Validate();
        return snapshot;
    }

    internal static void RequireSha256(string value, string name)
    {
        if (value is null || value.Length != 64 || value.Any(character => !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
        {
            throw new ArgumentException($"{name} must be a lowercase SHA-256 digest.", name);
        }
    }

    internal static void RequireStrictUtf8(params string[] values)
    {
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            _ = StrictUtf8.GetByteCount(value);
        }
    }

    private static string[] SnapshotUniqueStrings(
        IReadOnlyList<string> values,
        int maximumItems,
        int maximumLength,
        bool requireAtLeastOne,
        string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximumItems)
        {
            throw new ArgumentException($"{name} accepts at most {maximumItems} items.", name);
        }

        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (result.Count >= maximumItems || string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || !unique.Add(value))
            {
                throw new ArgumentException($"{name} must be a bounded collection of unique non-empty strings.", name);
            }
            RequireStrictUtf8(value);
            result.Add(value);
        }
        if (result.Count != values.Count || requireAtLeastOne && result.Count == 0)
        {
            throw new ArgumentException($"{name} has invalid collection cardinality.", name);
        }
        return result.ToArray();
    }
}

internal sealed class PolicyCanonicalWriter : IDisposable
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

    internal void NullableField(string? value)
    {
        Field(value is null ? "false" : "true");
        if (value is not null) Field(value);
    }

    internal void Field(Guid value) => Field(value.ToString("N"));
    internal void Field(ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        _stream.Write(length);
        _stream.Write(value);
    }
    internal void Field(bool value) => Field(value ? "true" : "false");
    internal void Field(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
    internal void Field(long value) => Field(value.ToString(CultureInfo.InvariantCulture));
    internal void Field(DateTimeOffset value) => Field(value.ToString("O", CultureInfo.InvariantCulture));
    internal byte[] ToArray() => _stream.ToArray();
    public void Dispose() => _stream.Dispose();
}

internal static class PolicyCanonicalHash
{
    internal static string Compute(Action<PolicyCanonicalWriter> write)
    {
        using var writer = new PolicyCanonicalWriter();
        write(writer);
        var canonical = writer.ToArray();
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(canonical, digest);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static byte[] Bytes(Action<PolicyCanonicalWriter> write)
    {
        using var writer = new PolicyCanonicalWriter();
        write(writer);
        return writer.ToArray();
    }
}

public static class PolicyApprovalDecisionCanonical
{
    // This domain and field order intentionally match operation-compiler's consumed v1 snapshot commitment.
    public const string Domain = "dps.operation-compiler.approval-snapshot-sha256/v1";

    public static string ComputeSha256(ApprovalDecisionV1 decision)
    {
        var snapshot = PolicyCanonicalization.SnapshotDecision(decision);
        return PolicyCanonicalHash.Compute(writer => Write(writer, snapshot));
    }

    internal static void Write(PolicyCanonicalWriter writer, ApprovalDecisionV1 decision)
    {
        writer.Field(Domain);
        writer.Field(decision.SchemaVersion);
        writer.Field(decision.ContractId);
        writer.Field(decision.ProducerModule);
        writer.Field(decision.ApprovalId);
        writer.Field(decision.ProposalId);
        writer.Field(decision.SoulId);
        writer.Field(decision.DeviceBindingId);
        writer.Field(decision.PlatformAccountId);
        writer.Field(decision.TraceId);
        writer.Field(decision.IdempotencyKey);
        writer.Field(decision.OccurredAt);
        writer.Field(decision.PrivacyClass);
        writer.Field(decision.ActionKind);
        writer.Field(decision.IsSideEffect);
        writer.Field(decision.ShadowOnly);
        writer.Field(decision.Parameters.Count);
        foreach (var pair in decision.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.Field(pair.Key);
            writer.Field(pair.Value);
        }
        writer.Field(decision.Decision);
        writer.Field(decision.Authority);
        writer.Field(decision.PolicyVersion);
        writer.Field(decision.EvaluatedPolicyIds.Count);
        foreach (var policy in decision.EvaluatedPolicyIds.Order(StringComparer.Ordinal)) writer.Field(policy);
        writer.NullableField(decision.PlatformAuthorizationId);
        writer.Field(decision.DenialReasons.Count);
        foreach (var reason in decision.DenialReasons.Order(StringComparer.Ordinal)) writer.Field(reason);
    }
}

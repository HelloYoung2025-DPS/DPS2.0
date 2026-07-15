using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dps.OperationCompiler.Contracts;
using Dps.PolicyApproval.Contracts;

namespace Dps.OperationCompiler;

public sealed record ApprovalCompilationRequestV1(
    Guid ApprovalId,
    Guid ProposalId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string ApprovalSha256);

public sealed record AuthoritativeApprovalSnapshotV1(
    ApprovalDecisionV1 Approval,
    string CanonicalSha256,
    string Status)
{
    public const string Active = "ACTIVE";
}

public interface IAuthoritativeApprovalReader
{
    Task<AuthoritativeApprovalSnapshotV1> ReadAsync(
        ApprovalCompilationRequestV1 request,
        CancellationToken cancellationToken);
}

public sealed class AllowlistedOperationCompiler
{
    private readonly IAuthoritativeApprovalReader _approvalReader;

    public AllowlistedOperationCompiler(IAuthoritativeApprovalReader approvalReader)
    {
        _approvalReader = approvalReader ?? throw new ArgumentNullException(nameof(approvalReader));
    }

    public async Task<CompiledOperationV1> CompileAsync(
        ApprovalCompilationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        OperationContractGuard.RequireGuid(request.ApprovalId, nameof(request.ApprovalId));
        OperationContractGuard.RequireGuid(request.ProposalId, nameof(request.ProposalId));
        OperationContractGuard.RequireScope(request.SoulId, request.DeviceBindingId, request.PlatformAccountId);
        OperationContractGuard.RequireTraceId(request.TraceId);
        OperationContractGuard.RequireIdempotencyKey(request.IdempotencyKey);
        OperationContractGuard.RequireSha256(request.ApprovalSha256, nameof(request.ApprovalSha256));

        var readTask = _approvalReader.ReadAsync(request, cancellationToken)
            ?? throw new UnauthorizedAccessException("The authoritative approval reader returned no task.");
        var authoritative = await readTask.ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The authoritative approval reader returned no snapshot.");
        // A reader that ignores cancellation cannot turn a late approval into a
        // dispatchable operation. The outer production boundary records the late
        // terminal outcome in quarantine.
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(authoritative.Status, AuthoritativeApprovalSnapshotV1.Active, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Only an ACTIVE authoritative approval snapshot may compile.");
        OperationContractGuard.RequireSha256(authoritative.CanonicalSha256, nameof(authoritative.CanonicalSha256));
        var approval = ApprovalDecisionV1SchemaGuard.SnapshotAndValidate(authoritative.Approval);
        RequireExactRequestScope(request, approval);
        var approvalSha256 = ApprovalSnapshotV1Canonical.ComputeSha256Validated(approval);
        if (!FixedSha256Equals(approvalSha256, authoritative.CanonicalSha256) || !FixedSha256Equals(approvalSha256, request.ApprovalSha256))
            throw new UnauthorizedAccessException("The authoritative approval digest does not match the immutable snapshot and compilation request.");

        if (approval.Decision != ApprovalDecisionV1.Approved) throw new UnauthorizedAccessException("Only an APPROVED decision may compile.");
        if (approval.Authority != ApprovalDecisionV1.DeterministicAuthority) throw new UnauthorizedAccessException("Only deterministic policy authority is accepted.");
        if (approval.ShadowOnly) throw new UnauthorizedAccessException("Shadow input cannot compile into dispatchable steps.");
        ValidateParameterValues(approval.Parameters);
        var operationId = OperationCompiledV1CanonicalIds.ComputeOperationId(
            CompiledOperationV1.CurrentSchemaVersion, CompiledOperationV1.CurrentContractId, CompiledOperationV1.CurrentProducerModule,
            approval.ApprovalId, approval.ProposalId, approvalSha256,
            approval.SoulId, approval.DeviceBindingId, approval.PlatformAccountId, approval.TraceId, approval.IdempotencyKey,
            approval.OccurredAt, "internal", approval.ActionKind, approval.IsSideEffect, false, approval.PlatformAuthorizationId);
        var definition = CompileStep(operationId, approval.ActionKind, approval.Parameters);
        var operation = new CompiledOperationV1(
            CompiledOperationV1.CurrentSchemaVersion, CompiledOperationV1.CurrentContractId, CompiledOperationV1.CurrentProducerModule,
            operationId, approval.ApprovalId, approval.ProposalId, approvalSha256,
            approval.SoulId, approval.DeviceBindingId, approval.PlatformAccountId, approval.TraceId, approval.IdempotencyKey,
            approval.OccurredAt, "internal", approval.ActionKind, approval.IsSideEffect, false, approval.PlatformAuthorizationId,
            [definition]);
        return operation.ValidateAndSnapshot();
    }

    private static void RequireExactRequestScope(ApprovalCompilationRequestV1 request, ApprovalDecisionV1 approval)
    {
        if (request.ApprovalId != approval.ApprovalId || request.ProposalId != approval.ProposalId ||
            !string.Equals(request.SoulId, approval.SoulId, StringComparison.Ordinal) ||
            !string.Equals(request.DeviceBindingId, approval.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(request.PlatformAccountId, approval.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(request.TraceId, approval.TraceId, StringComparison.Ordinal) ||
            !string.Equals(request.IdempotencyKey, approval.IdempotencyKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The authoritative approval snapshot does not match the exact compilation request scope.");
    }

    private static bool FixedSha256Equals(string left, string right)
    {
        if (left is null || right is null || left.Length != 64 || right.Length != 64) return false;
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException) { return false; }
        finally
        {
            if (leftBytes is not null) CryptographicOperations.ZeroMemory(leftBytes);
            if (rightBytes is not null) CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static OperationStepV1 CompileStep(Guid operationId, string action, IReadOnlyDictionary<string, string> parameters)
    {
        var (kind, postcondition, retrySafe, required) = action switch
        {
            "observe" => ("ui.observe", "native-read-complete", true, Array.Empty<string>()),
            "locate" => ("ui.locate", "selector-resolved", true, new[] { "selector_ref" }),
            "verify" => ("ui.verify", "assertion-satisfied", true, new[] { "selector_ref" }),
            "wait" => ("control.wait", "timer-elapsed", true, new[] { "duration_ms" }),
            "fixture.tap" => ("fixture.tap", "fixture-state-changed", false, new[] { "selector_ref" }),
            "fixture.type" => ("fixture.type", "fixture-value-matched", false, new[] { "selector_ref", "value_ref" }),
            _ => throw new NotSupportedException($"Unknown action '{action}'.")
        };
        if (parameters.Keys.Any(key => key is "x" or "y" or "coordinates" or "coordinate")) throw new NotSupportedException("Coordinate fallback is forbidden.");
        if (parameters.Keys.Except(required, StringComparer.Ordinal).Any() || required.Any(key => !parameters.ContainsKey(key))) throw new ArgumentException($"Parameters do not match the allowlist for '{action}'.", nameof(parameters));
        var arguments = required.ToDictionary(key => key, key => parameters[key], StringComparer.Ordinal);
        return new OperationStepV1(OperationCompiledV1CanonicalIds.ComputeStepId(operationId, kind, arguments, retrySafe, postcondition), kind, arguments, retrySafe, postcondition);
    }

    private static readonly Encoding StrictParameterUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex OpaqueReferencePattern = SafePattern("\\A[A-Za-z0-9][A-Za-z0-9._:,=-]{0,127}\\z");
    private static readonly Regex DurationPattern = SafePattern("\\A(?:[1-9][0-9]{0,4}|[1-5][0-9]{5}|600000)\\z");
    private static readonly Regex CoordinateReferencePattern = SafePattern(
        "\\A(?:(?:-?[0-9]+(?:\\.[0-9]+)?),-?[0-9]+(?:\\.[0-9]+)?|(?:x|left)=-?[0-9]+(?:\\.[0-9]+)?,(?:y|top)=-?[0-9]+(?:\\.[0-9]+)?|(?:coordinate|coordinates|xy)[:=].+)\\z",
        RegexOptions.IgnoreCase);

    private static void ValidateParameterValues(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var pair in parameters)
        {
            if (pair.Value is null || pair.Value.Length is < 1 or > 256)
                throw new ArgumentException("Parameter values must be non-empty strings of at most 256 characters.", nameof(parameters));
            _ = StrictParameterUtf8.GetByteCount(pair.Value);
            switch (pair.Key)
            {
                case "duration_ms":
                    RequireDuration(pair.Value);
                    break;
                case "selector_ref":
                case "value_ref":
                    RequireOpaqueMachineReference(pair.Value, pair.Key);
                    break;
            }
        }
    }

    private static void RequireDuration(string value)
    {
        if (!DurationPattern.IsMatch(value)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var duration)
            || duration is < 1 or > 600000
            || !string.Equals(duration.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
            throw new ArgumentException("duration_ms must be the canonical decimal form of 1 through 600000.", nameof(value));
    }

    private static void RequireOpaqueMachineReference(string value, string name)
    {
        if (!OpaqueReferencePattern.IsMatch(value) || CoordinateReferencePattern.IsMatch(value))
            throw new ArgumentException($"{name} must be a bounded opaque machine reference, not prompt text or coordinates.", name);
    }

    private static Regex SafePattern(string pattern, RegexOptions options = RegexOptions.None)
        => new(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | options,
            TimeSpan.FromMilliseconds(100));
}

public static class ApprovalSnapshotV1Canonical
{
    public const string Domain = "dps.operation-compiler.approval-snapshot-sha256/v1";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string ComputeSha256(ApprovalDecisionV1 approval)
        => ComputeSha256Validated(ApprovalDecisionV1SchemaGuard.SnapshotAndValidate(approval));

    internal static string ComputeSha256Validated(ApprovalDecisionV1 approval) => HashHex(writer =>
    {
        writer.Field(Domain);
        writer.Field(approval.SchemaVersion);
        writer.Field(approval.ContractId);
        writer.Field(approval.ProducerModule);
        writer.Field(approval.ApprovalId);
        writer.Field(approval.ProposalId);
        writer.Field(approval.SoulId);
        writer.Field(approval.DeviceBindingId);
        writer.Field(approval.PlatformAccountId);
        writer.Field(approval.TraceId);
        writer.Field(approval.IdempotencyKey);
        writer.Field(approval.OccurredAt);
        writer.Field(approval.PrivacyClass);
        writer.Field(approval.ActionKind);
        writer.Field(approval.IsSideEffect);
        writer.Field(approval.ShadowOnly);
        writer.Field(approval.Parameters.Count);
        foreach (var pair in approval.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.Field(pair.Key);
            writer.Field(pair.Value);
        }
        writer.Field(approval.Decision);
        writer.Field(approval.Authority);
        writer.Field(approval.PolicyVersion);
        writer.Field(approval.EvaluatedPolicyIds.Count);
        foreach (var policyId in approval.EvaluatedPolicyIds.Order(StringComparer.Ordinal)) writer.Field(policyId);
        writer.NullableField(approval.PlatformAuthorizationId);
        writer.Field(approval.DenialReasons.Count);
        foreach (var reason in approval.DenialReasons.Order(StringComparer.Ordinal)) writer.Field(reason);
    });

    private static string HashHex(Action<CanonicalFieldWriter> write)
    {
        using var writer = new CanonicalFieldWriter();
        write(writer);
        var canonicalBytes = writer.ToArray();
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(canonicalBytes, digest);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private sealed class CanonicalFieldWriter : IDisposable
    {
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
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        internal void NullableField(string? value)
        {
            Field(value is not null);
            if (value is not null) Field(value);
        }

        internal void Field(Guid value) => Field(value.ToString("N"));
        internal void Field(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
        internal void Field(bool value) => Field(value ? "true" : "false");
        internal void Field(DateTimeOffset value) => Field(value.ToString("O", CultureInfo.InvariantCulture));
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}

internal static class ApprovalDecisionV1SchemaGuard
{
    private const int MaximumParameters = 16;
    private const int MaximumEvaluatedPolicyIds = 32;
    private const int MaximumDenialReasons = 32;
    private const RegexOptions SafeRegexOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan SafeRegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex SchemaVersionPattern = new("^1(?:\\.[0-9]+){0,2}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex SoulIdPattern = new("^soul_[a-f0-9]{64}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex DeviceBindingIdPattern = new("^db_[a-f0-9]{32}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex PlatformAccountIdPattern = new("^pa_[a-f0-9]{32}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex TraceIdPattern = new("^trace_[a-f0-9]{32}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex IdempotencyKeyPattern = new("^idem_[a-f0-9]{64}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex PolicyVersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex PolicyIdPattern = new("^[A-Z]+(?:-[A-Z]+)*-[0-9]{3}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly IReadOnlySet<string> AllowedActions = new HashSet<string>(
        ["observe", "locate", "verify", "wait", "fixture.tap", "fixture.type"],
        StringComparer.Ordinal);

    internal static ApprovalDecisionV1 SnapshotAndValidate(ApprovalDecisionV1 approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.SchemaVersion is null || approval.SchemaVersion.Length > 32) throw new NotSupportedException("Approval schema version exceeds the compiler boundary limit.");
        if (!SchemaVersionPattern.IsMatch(approval.SchemaVersion)) throw new NotSupportedException($"Unsupported approval schema version '{approval.SchemaVersion}'.");
        RequireExact(approval.ContractId, ApprovalDecisionV1.CurrentContractId, nameof(approval.ContractId));
        RequireExact(approval.ProducerModule, ApprovalDecisionV1.CurrentProducerModule, nameof(approval.ProducerModule));
        RequireGuid(approval.ApprovalId, nameof(approval.ApprovalId));
        RequireGuid(approval.ProposalId, nameof(approval.ProposalId));
        RequirePattern(approval.SoulId, SoulIdPattern, nameof(approval.SoulId), 69);
        RequirePattern(approval.DeviceBindingId, DeviceBindingIdPattern, nameof(approval.DeviceBindingId), 35);
        RequirePattern(approval.PlatformAccountId, PlatformAccountIdPattern, nameof(approval.PlatformAccountId), 35);
        RequirePattern(approval.TraceId, TraceIdPattern, nameof(approval.TraceId), 38);
        RequirePattern(approval.IdempotencyKey, IdempotencyKeyPattern, nameof(approval.IdempotencyKey), 69);
        if (approval.OccurredAt.Offset != TimeSpan.Zero) throw new ArgumentException("occurred_at must be UTC.", nameof(approval.OccurredAt));
        RequireExact(approval.PrivacyClass, "internal", nameof(approval.PrivacyClass));
        if (!AllowedActions.Contains(approval.ActionKind)) throw new NotSupportedException($"Unknown action '{approval.ActionKind}'.");
        if (approval.Decision is not (ApprovalDecisionV1.Approved or ApprovalDecisionV1.Denied)) throw new NotSupportedException($"Unknown decision '{approval.Decision}'.");
        RequireExact(approval.Authority, ApprovalDecisionV1.DeterministicAuthority, nameof(approval.Authority));
        RequirePattern(approval.PolicyVersion, PolicyVersionPattern, nameof(approval.PolicyVersion), 32);
        if (approval.PlatformAuthorizationId is { Length: > 256 }) throw new ArgumentException("platform_authorization_id exceeds 256 characters.", nameof(approval.PlatformAuthorizationId));

        var parameters = SnapshotParameters(approval.Parameters);
        var evaluatedPolicyIds = SnapshotUniqueStrings(approval.EvaluatedPolicyIds, "evaluated_policy_ids", 1, null, PolicyIdPattern);
        var denialReasons = SnapshotUniqueStrings(approval.DenialReasons, "denial_reasons", 0, 128, null);
        var snapshot = approval with { Parameters = parameters, EvaluatedPolicyIds = evaluatedPolicyIds, DenialReasons = denialReasons };
        snapshot.Validate();
        return snapshot;
    }

    private static IReadOnlyDictionary<string, string> SnapshotParameters(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count > MaximumParameters) throw new ArgumentException($"parameters accepts at most {MaximumParameters} entries.", nameof(parameters));
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            if (snapshot.Count == MaximumParameters) throw new ArgumentException($"parameters accepts at most {MaximumParameters} entries.", nameof(parameters));
            if (pair.Key is null || !snapshot.TryAdd(pair.Key, pair.Value)) throw new ArgumentException("parameters must contain unique string keys.", nameof(parameters));
            if (pair.Key.Length is < 1 or > 64) throw new ArgumentException("Parameter keys must contain 1 to 64 characters.", nameof(parameters));
            if (pair.Value is null || pair.Value.Length is < 1 or > 256) throw new ArgumentException("Parameter values must be non-empty strings of at most 256 characters.", nameof(parameters));
        }
        return snapshot;
    }

    private static IReadOnlyList<string> SnapshotUniqueStrings(IReadOnlyList<string> values, string name, int minimumItems, int? maximumLength, Regex? pattern)
    {
        ArgumentNullException.ThrowIfNull(values);
        var maximumItems = name == "evaluated_policy_ids" ? MaximumEvaluatedPolicyIds : MaximumDenialReasons;
        if (values.Count > maximumItems) throw new ArgumentException($"{name} accepts at most {maximumItems} items.", name);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new List<string>();
        foreach (var value in values)
        {
            if (snapshot.Count == maximumItems) throw new ArgumentException($"{name} accepts at most {maximumItems} items.", name);
            var effectiveMaximumLength = maximumLength ?? 64;
            if (value is null || value.Length == 0 || value.Length > effectiveMaximumLength || pattern is not null && !pattern.IsMatch(value))
                throw new ArgumentException($"Invalid {name} item.", name);
            if (!unique.Add(value)) throw new ArgumentException($"{name} items must be unique.", name);
            snapshot.Add(value);
        }
        if (snapshot.Count < minimumItems) throw new ArgumentException($"{name} requires at least {minimumItems} item(s).", name);
        return snapshot;
    }

    private static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new NotSupportedException($"Unsupported {name} '{actual}'.");
    }

    private static void RequireGuid(Guid value, string name)
    {
        if (value == Guid.Empty) throw new ArgumentException($"{name} cannot be empty.", name);
    }

    private static void RequirePattern(string value, Regex pattern, string name, int? maximumLength = null)
    {
        if (value is null || maximumLength is not null && value.Length > maximumLength || !pattern.IsMatch(value)) throw new ArgumentException($"Invalid {name}.", name);
    }

    private static void RequireText(string value, int maximumLength, string name)
    {
        if (value is null || value.Length == 0 || value.Length > maximumLength || string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Invalid {name}.", name);
    }
}

using System.Collections.ObjectModel;
using Dps.Planner.Contracts;
using Npgsql;

namespace Dps.PolicyApproval;

public sealed record PolicyRuntimeStateRevisionV1(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long Revision,
    string StateStatus,
    string PolicyVersion,
    IReadOnlyList<string> EnabledPolicyIds,
    bool KillSwitchEnabled,
    int RemainingRateBudget,
    bool PlatformAuthorized,
    string? PlatformAuthorizationId,
    bool ExecutionEnabled,
    string ReleaseBomSha256,
    DateTimeOffset ValidUntil)
{
    public const string Active = "ACTIVE";
    public const string Revoked = "REVOKED";

    public void Validate()
    {
        Dps.Planner.Contracts.ProposalContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        if (Revision <= 0) throw new ArgumentOutOfRangeException(nameof(Revision));
        if (StateStatus is not (Active or Revoked)) throw new NotSupportedException("Unknown policy runtime state status.");
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireSemVer(PolicyVersion);
        Dps.PolicyApproval.Contracts.ApprovalContractGuard.RequireUtc(ValidUntil, nameof(ValidUntil));
        PolicyCanonicalization.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (RemainingRateBudget < 0) throw new ArgumentOutOfRangeException(nameof(RemainingRateBudget));
        if (PlatformAuthorized != !string.IsNullOrWhiteSpace(PlatformAuthorizationId))
            throw new ArgumentException("Platform authorization state and identifier must agree.");
        if (PlatformAuthorizationId is { Length: > 256 }) throw new ArgumentException("Platform authorization identifier is too long.");
        if (EnabledPolicyIds is null || EnabledPolicyIds.Count is < 1 or > 32)
            throw new ArgumentException("Policy runtime state requires 1 to 32 enabled policies.");
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in EnabledPolicyIds)
        {
            if (string.IsNullOrWhiteSpace(policy) || policy.Length > 64 || !unique.Add(policy))
                throw new ArgumentException("Enabled policies must be unique bounded strings.");
            PolicyCanonicalization.RequireStrictUtf8(policy);
        }
    }
}

public static class PolicyRuntimeStateCommitment
{
    public static string ComputeSha256(PolicyRuntimeStateRevisionV1 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        return PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.runtime-state-sha256/v1");
            writer.Field(state.SoulId);
            writer.Field(state.DeviceBindingId);
            writer.Field(state.PlatformAccountId);
            writer.Field(state.Revision);
            writer.Field(state.StateStatus);
            writer.Field(state.PolicyVersion);
            writer.Field(state.EnabledPolicyIds.Count);
            foreach (var policy in state.EnabledPolicyIds.Order(StringComparer.Ordinal)) writer.Field(policy);
            writer.Field(state.KillSwitchEnabled);
            writer.Field(state.RemainingRateBudget);
            writer.Field(state.PlatformAuthorized);
            writer.NullableField(state.PlatformAuthorizationId);
            writer.Field(state.ExecutionEnabled);
            writer.Field(state.ReleaseBomSha256);
            writer.Field(state.ValidUntil);
        });
    }
}

public sealed class PostgresPolicyRuntimeStateSource : IPolicyRuntimeStateSource
{
    private readonly PostgresPolicyApprovalOptions _options;
    private readonly string _connectionString;
    private readonly NpgsqlConnection? _boundConnection;
    private readonly NpgsqlTransaction? _boundTransaction;

    public PostgresPolicyRuntimeStateSource(PostgresPolicyApprovalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.BuildBoundedConnectionString();
        _options = options;
    }

    internal PostgresPolicyRuntimeStateSource(
        PostgresPolicyApprovalOptions options,
        NpgsqlConnection boundConnection,
        NpgsqlTransaction boundTransaction)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(boundConnection);
        ArgumentNullException.ThrowIfNull(boundTransaction);
        options.Validate();
        if (!ReferenceEquals(boundConnection, boundTransaction.Connection))
            throw new ArgumentException("The policy runtime transaction is not bound to the supplied connection.", nameof(boundTransaction));
        _connectionString = options.BuildBoundedConnectionString();
        _options = options;
        _boundConnection = boundConnection;
        _boundTransaction = boundTransaction;
    }

    public async ValueTask<PolicyRuntimeState> ReadVerifiedStateAsync(
        ActionProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        proposal = PolicyCanonicalization.SnapshotProposal(proposal);
        if (_boundConnection is not null && _boundTransaction is not null)
            return await ReadFromConnectionAsync(
                _boundConnection, _boundTransaction, proposal, cancellationToken);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await PolicyApprovalDatabaseRoleGuard.VerifyRuntimeAsync(
            connection, _options.ExpectedRuntimeRoleName, _options.SchemaName, cancellationToken);
        return await ReadFromConnectionAsync(connection, null, proposal, cancellationToken);
    }

    private async ValueTask<PolicyRuntimeState> ReadFromConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ActionProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT revision, state_status, policy_version, enabled_policy_ids,
                   kill_switch_enabled, remaining_rate_budget, platform_authorized,
                   platform_authorization_id, execution_enabled, release_bom_sha256,
                   valid_until, state_sha256,
                   COALESCE((
                       SELECT sum(consumption.units)
                       FROM {_options.SchemaName}.policy_rate_consumptions AS consumption
                       WHERE consumption.soul_id = state.soul_id
                         AND consumption.device_binding_id = state.device_binding_id
                         AND consumption.platform_account_id = state.platform_account_id
                   ), 0)::integer AS consumed_units,
                   clock_timestamp()
            FROM {_options.SchemaName}.policy_runtime_revisions AS state
            WHERE state.soul_id = @soul_id
              AND state.device_binding_id = @device_binding_id
              AND state.platform_account_id = @platform_account_id
            ORDER BY revision DESC
            LIMIT 1
            """,
            connection,
            transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("soul_id", proposal.SoulId);
        command.Parameters.AddWithValue("device_binding_id", proposal.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", proposal.PlatformAccountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new UnauthorizedAccessException("No authoritative policy runtime state exists for the exact scope.");

        var evaluatedAt = reader.GetFieldValue<DateTimeOffset>(13).ToUniversalTime();
        var state = new PolicyRuntimeStateRevisionV1(
            proposal.SoulId,
            proposal.DeviceBindingId,
            proposal.PlatformAccountId,
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            Array.AsReadOnly(reader.GetFieldValue<string[]>(3)),
            reader.GetBoolean(4),
            reader.GetInt32(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetBoolean(8),
            reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10).ToUniversalTime());
        state.Validate();
        var recordedDigest = reader.GetString(11);
        if (!FixedDigestEquals(recordedDigest, PolicyRuntimeStateCommitment.ComputeSha256(state)))
            throw new UnauthorizedAccessException("Policy runtime state commitment verification failed.");
        if (state.StateStatus != PolicyRuntimeStateRevisionV1.Active)
            throw new UnauthorizedAccessException("The latest policy runtime state is revoked.");
        if (evaluatedAt >= state.ValidUntil)
            throw new UnauthorizedAccessException("The latest policy runtime state has expired according to PostgreSQL time.");
        var consumedUnits = reader.GetInt32(12);
        if (consumedUnits < 0) throw new UnauthorizedAccessException("Policy rate consumption cannot be negative.");
        var effectiveRemainingRateBudget = Math.Max(0, state.RemainingRateBudget - consumedUnits);

        return new PolicyRuntimeState(
            state.SoulId,
            state.DeviceBindingId,
            state.PlatformAccountId,
            state.PolicyVersion,
            new ReadOnlySet<string>(new HashSet<string>(state.EnabledPolicyIds, StringComparer.Ordinal)),
            state.KillSwitchEnabled,
            effectiveRemainingRateBudget,
            state.PlatformAuthorized,
            state.PlatformAuthorizationId,
            evaluatedAt,
            state.ExecutionEnabled,
            state.ReleaseBomSha256,
            state.Revision,
            recordedDigest,
            state.ValidUntil);
    }

    private static bool FixedDigestEquals(string left, string right)
    {
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException) { return false; }
        finally
        {
            if (leftBytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(leftBytes);
            if (rightBytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}

internal sealed class ReadOnlySet<T>(ISet<T> values) : IReadOnlySet<T>
{
    public int Count => values.Count;
    public bool Contains(T item) => values.Contains(item);
    public bool IsProperSubsetOf(IEnumerable<T> other) => values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => values.Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => values.SetEquals(other);
    public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

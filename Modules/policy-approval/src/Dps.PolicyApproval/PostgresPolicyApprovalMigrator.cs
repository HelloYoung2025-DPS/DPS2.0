using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Dps.PolicyApproval;

public sealed record PolicyApprovalMigrationOptions(
    string MigrationConnectionString,
    string SchemaName,
    string RuntimeRoleName,
    string SubmissionExecutorRoleName,
    string SubmissionReconciliationRoleName,
    string SubmissionRecoveryRoleName)
{
    private static readonly Regex SafeIdentifier = new(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MigrationConnectionString))
            throw new ArgumentException("A privileged migration connection string is required.", nameof(MigrationConnectionString));
        if (string.IsNullOrWhiteSpace(SchemaName) || !SafeIdentifier.IsMatch(SchemaName))
            throw new ArgumentException("SchemaName is not an allowlisted PostgreSQL identifier.", nameof(SchemaName));
        var roles = new[]
        {
            (RuntimeRoleName, nameof(RuntimeRoleName)),
            (SubmissionExecutorRoleName, nameof(SubmissionExecutorRoleName)),
            (SubmissionReconciliationRoleName, nameof(SubmissionReconciliationRoleName)),
            (SubmissionRecoveryRoleName, nameof(SubmissionRecoveryRoleName))
        };
        foreach (var (role, name) in roles)
            if (string.IsNullOrWhiteSpace(role) || !SafeIdentifier.IsMatch(role))
                throw new ArgumentException($"{name} is not an allowlisted PostgreSQL identifier.", name);
        if (roles.Select(static item => item.Item1).Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw new ArgumentException("Policy runtime, submission executor, reconciler, and recovery PostgreSQL roles must be pairwise distinct.");
    }

    public override string ToString()
        => $"PolicyApprovalMigrationOptions {{ MigrationConnectionString = [REDACTED], SchemaName = {SchemaName}, RuntimeRoleName = {RuntimeRoleName}, SubmissionExecutorRoleName = {SubmissionExecutorRoleName}, SubmissionReconciliationRoleName = {SubmissionReconciliationRoleName}, SubmissionRecoveryRoleName = {SubmissionRecoveryRoleName} }}";
}

public sealed record PostgresPolicyApprovalOptions(
    string RuntimeConnectionString,
    string SchemaName,
    string ExpectedRuntimeRoleName)
{
    private static readonly Regex SafeIdentifier = new(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RuntimeConnectionString))
            throw new ArgumentException("A least-privilege runtime connection string is required.", nameof(RuntimeConnectionString));
        if (string.IsNullOrWhiteSpace(SchemaName) || !SafeIdentifier.IsMatch(SchemaName))
            throw new ArgumentException("SchemaName is not an allowlisted PostgreSQL identifier.", nameof(SchemaName));
        if (string.IsNullOrWhiteSpace(ExpectedRuntimeRoleName) || !SafeIdentifier.IsMatch(ExpectedRuntimeRoleName))
            throw new ArgumentException("ExpectedRuntimeRoleName is not an allowlisted PostgreSQL identifier.", nameof(ExpectedRuntimeRoleName));
        ValidateConnection(RuntimeConnectionString, nameof(RuntimeConnectionString));
    }

    internal string BuildBoundedConnectionString()
    {
        Validate();
        return BuildBoundedConnectionString(RuntimeConnectionString);
    }

    private static string BuildBoundedConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 5,
            CommandTimeout = 5
        };
        return builder.ConnectionString;
    }

    private static void ValidateConnection(string connectionString, string parameterName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Port == 55434 || string.Equals(builder.Database, "dps_gbrain_company", StringComparison.Ordinal))
            throw new InvalidOperationException($"Policy Approval refuses the dedicated GBrain Company PostgreSQL service for {parameterName}.");
    }

    public override string ToString()
        => $"PostgresPolicyApprovalOptions {{ RuntimeConnectionString = [REDACTED], SchemaName = {SchemaName}, ExpectedRuntimeRoleName = {ExpectedRuntimeRoleName} }}";
}

internal static class PolicyApprovalDatabaseRoleGuard
{
    private const string Sql =
        """
        SELECT session_user, current_user, role.rolsuper, role.rolcreaterole,
               role.rolcreatedb, role.rolreplication, role.rolbypassrls,
               EXISTS (SELECT 1 FROM pg_auth_members AS membership WHERE membership.member = role.oid)
        FROM pg_roles AS role
        WHERE role.rolname = current_user
        """;

    internal static async Task VerifyRuntimeAsync(
        NpgsqlConnection connection,
        string expectedRoleName,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Sql, connection) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), expectedRoleName, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), expectedRoleName, StringComparison.Ordinal)
            || reader.GetBoolean(2)
            || reader.GetBoolean(3)
            || reader.GetBoolean(4)
            || reader.GetBoolean(5)
            || reader.GetBoolean(6)
            || reader.GetBoolean(7))
            throw new UnauthorizedAccessException("PostgreSQL session is not the expected least-privilege policy-approval runtime role.");
        await reader.DisposeAsync();
        await VerifyPrivilegesAsync(connection, schemaName, cancellationToken);
    }

    internal static void VerifyRuntime(
        NpgsqlConnection connection,
        string expectedRoleName,
        string schemaName)
    {
        using var command = new NpgsqlCommand(Sql, connection) { CommandTimeout = 5 };
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), expectedRoleName, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), expectedRoleName, StringComparison.Ordinal)
            || reader.GetBoolean(2)
            || reader.GetBoolean(3)
            || reader.GetBoolean(4)
            || reader.GetBoolean(5)
            || reader.GetBoolean(6)
            || reader.GetBoolean(7))
            throw new UnauthorizedAccessException("PostgreSQL session is not the expected least-privilege policy-approval runtime role.");
        reader.Dispose();
        VerifyPrivileges(connection, schemaName);
    }

    private static string PrivilegeSql(string schemaName)
        => $"""
        SELECT
            has_schema_privilege(current_user, '{schemaName}', 'USAGE'),
            has_schema_privilege(current_user, '{schemaName}', 'CREATE'),
            (
                EXISTS (
                    SELECT 1
                    FROM pg_class AS object
                    JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                    WHERE namespace.nspname = '{schemaName}'
                      AND object.relowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                )
                OR EXISTS (
                    SELECT 1
                    FROM pg_proc AS function
                    JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
                    WHERE namespace.nspname = '{schemaName}'
                      AND function.proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                )
            ),
            has_table_privilege(current_user, '{schemaName}.policy_runtime_revisions', 'SELECT'),
            has_table_privilege(current_user, '{schemaName}.policy_runtime_revisions', 'INSERT'),
            (has_table_privilege(current_user, '{schemaName}.policy_rate_consumptions', 'SELECT') AND has_table_privilege(current_user, '{schemaName}.policy_rate_consumptions', 'INSERT')),
            (has_table_privilege(current_user, '{schemaName}.approval_decisions', 'SELECT') AND has_table_privilege(current_user, '{schemaName}.approval_decisions', 'INSERT')),
            (has_table_privilege(current_user, '{schemaName}.approval_status_revisions', 'SELECT') AND has_table_privilege(current_user, '{schemaName}.approval_status_revisions', 'INSERT')),
            (has_table_privilege(current_user, '{schemaName}.approval_idempotency_receipts', 'SELECT') AND has_table_privilege(current_user, '{schemaName}.approval_idempotency_receipts', 'INSERT')),
            (has_table_privilege(current_user, '{schemaName}.approval_outbox', 'SELECT') AND has_table_privilege(current_user, '{schemaName}.approval_outbox', 'INSERT')),
            (has_table_privilege(current_user, '{schemaName}.approval_idempotency_quarantine', 'SELECT') AND has_table_privilege(current_user, '{schemaName}.approval_idempotency_quarantine', 'INSERT')),
            (
                has_table_privilege(current_user, '{schemaName}.approval_submission_attempts', 'SELECT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_acknowledgements', 'SELECT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_quarantines', 'SELECT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_reconciliations', 'SELECT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_recoveries', 'SELECT')
            ),
            (
                has_table_privilege(current_user, '{schemaName}.approval_submission_attempts', 'INSERT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_acknowledgements', 'INSERT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_quarantines', 'INSERT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_reconciliations', 'INSERT')
                OR has_table_privilege(current_user, '{schemaName}.approval_submission_recoveries', 'INSERT')
            ),
            (
                has_function_privilege(current_user, '{schemaName}.begin_approval_submission(uuid,timestamp with time zone,jsonb,text,jsonb,text)', 'EXECUTE')
                OR has_function_privilege(current_user, '{schemaName}.acknowledge_approval_submission(jsonb,text,jsonb,text)', 'EXECUTE')
                OR has_function_privilege(current_user, '{schemaName}.quarantine_approval_submission(uuid,uuid,text,text,jsonb,text)', 'EXECUTE')
                OR has_function_privilege(current_user, '{schemaName}.reconcile_approval_submission(jsonb,text,jsonb,text)', 'EXECUTE')
                OR has_function_privilege(current_user, '{schemaName}.recover_approval_submission(jsonb,text,jsonb,text)', 'EXECUTE')
            ),
            EXISTS (
                SELECT 1
                FROM pg_proc AS function
                JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
                WHERE namespace.nspname = '{schemaName}'
                  AND has_function_privilege(current_user, function.oid, 'EXECUTE')
            ),
            EXISTS (
                SELECT 1
                FROM pg_class AS object
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = '{schemaName}'
                  AND (
                      (
                          object.relkind IN ('r', 'p', 'v', 'm', 'f')
                          AND (
                              (
                                  object.relname NOT IN (
                                      'policy_runtime_revisions', 'policy_rate_consumptions',
                                      'approval_decisions', 'approval_status_revisions',
                                      'approval_idempotency_receipts', 'approval_outbox',
                                      'approval_idempotency_quarantine')
                                  AND (
                                      has_table_privilege(current_user, object.oid, 'SELECT')
                                      OR has_table_privilege(current_user, object.oid, 'INSERT')
                                      OR has_table_privilege(current_user, object.oid, 'UPDATE')
                                      OR has_table_privilege(current_user, object.oid, 'DELETE')
                                      OR has_table_privilege(current_user, object.oid, 'TRUNCATE')
                                      OR has_table_privilege(current_user, object.oid, 'REFERENCES')
                                      OR has_table_privilege(current_user, object.oid, 'TRIGGER')
                                  )
                              )
                              OR (
                                  object.relname = 'policy_runtime_revisions'
                                  AND (
                                      has_table_privilege(current_user, object.oid, 'INSERT')
                                      OR has_table_privilege(current_user, object.oid, 'UPDATE')
                                      OR has_table_privilege(current_user, object.oid, 'DELETE')
                                      OR has_table_privilege(current_user, object.oid, 'TRUNCATE')
                                      OR has_table_privilege(current_user, object.oid, 'REFERENCES')
                                      OR has_table_privilege(current_user, object.oid, 'TRIGGER')
                                  )
                              )
                              OR (
                                  object.relname IN (
                                      'policy_rate_consumptions', 'approval_decisions',
                                      'approval_status_revisions', 'approval_idempotency_receipts',
                                      'approval_outbox', 'approval_idempotency_quarantine')
                                  AND (
                                      has_table_privilege(current_user, object.oid, 'UPDATE')
                                      OR has_table_privilege(current_user, object.oid, 'DELETE')
                                      OR has_table_privilege(current_user, object.oid, 'TRUNCATE')
                                      OR has_table_privilege(current_user, object.oid, 'REFERENCES')
                                      OR has_table_privilege(current_user, object.oid, 'TRIGGER')
                                  )
                              )
                          )
                      )
                      OR (
                          object.relkind = 'S'
                          AND (
                              has_sequence_privilege(current_user, object.oid, 'USAGE')
                              OR has_sequence_privilege(current_user, object.oid, 'SELECT')
                              OR has_sequence_privilege(current_user, object.oid, 'UPDATE')
                          )
                      )
                  )
            ),
            EXISTS (
                SELECT 1
                FROM pg_proc AS function
                JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
                WHERE namespace.nspname = '{schemaName}'
                  AND function.proname IN (
                      'begin_approval_submission', 'acknowledge_approval_submission',
                      'quarantine_approval_submission', 'reconcile_approval_submission',
                      'recover_approval_submission', 'assert_submission_executor_role',
                      'assert_submission_reconciliation_role', 'assert_submission_recovery_role',
                      'assert_submission_runtime_role', 'assert_exact_submission_json')
                  AND function.oid NOT IN (
                      to_regprocedure('{schemaName}.begin_approval_submission(uuid,timestamp with time zone,jsonb,text,jsonb,text)'),
                      to_regprocedure('{schemaName}.acknowledge_approval_submission(jsonb,text,jsonb,text)'),
                      to_regprocedure('{schemaName}.quarantine_approval_submission(uuid,uuid,text,text,jsonb,text)'),
                      to_regprocedure('{schemaName}.reconcile_approval_submission(jsonb,text,jsonb,text)'),
                      to_regprocedure('{schemaName}.recover_approval_submission(jsonb,text,jsonb,text)'),
                      to_regprocedure('{schemaName}.assert_submission_executor_role()'),
                      to_regprocedure('{schemaName}.assert_submission_reconciliation_role()'),
                      to_regprocedure('{schemaName}.assert_submission_recovery_role()'),
                      to_regprocedure('{schemaName}.assert_exact_submission_json(jsonb,text[],text)'))
            )
        """;

    private static async Task VerifyPrivilegesAsync(
        NpgsqlConnection connection,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PrivilegeSql(schemaName), connection) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || !PrivilegesAreExact(reader))
            throw new UnauthorizedAccessException("PostgreSQL runtime role privileges exceed or miss the policy-approval allowlist.");
    }

    private static void VerifyPrivileges(NpgsqlConnection connection, string schemaName)
    {
        using var command = new NpgsqlCommand(PrivilegeSql(schemaName), connection) { CommandTimeout = 5 };
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !PrivilegesAreExact(reader))
            throw new UnauthorizedAccessException("PostgreSQL runtime role privileges exceed or miss the policy-approval allowlist.");
    }

    private static bool PrivilegesAreExact(NpgsqlDataReader reader)
        => reader.GetBoolean(0)
           && !reader.GetBoolean(1)
           && !reader.GetBoolean(2)
           && reader.GetBoolean(3)
           && !reader.GetBoolean(4)
           && Enumerable.Range(5, 6).All(reader.GetBoolean)
           && !reader.GetBoolean(11)
           && !reader.GetBoolean(12)
           && !reader.GetBoolean(13)
           && !reader.GetBoolean(14)
           && !reader.GetBoolean(15)
           && !reader.GetBoolean(16);
}

internal enum PolicyApprovalSubmissionDatabaseRole
{
    Executor,
    Reconciliation,
    Recovery
}

internal static class PolicyApprovalSubmissionDatabaseRoleGuard
{
    private static string PrivilegeSql(string schemaName)
        => $"""
        SELECT session_user, current_user, role.rolsuper, role.rolcreaterole,
               role.rolcreatedb, role.rolreplication, role.rolbypassrls,
               EXISTS (SELECT 1 FROM pg_auth_members AS membership WHERE membership.member = role.oid),
               has_schema_privilege(current_user, '{schemaName}', 'USAGE'),
               has_schema_privilege(current_user, '{schemaName}', 'CREATE'),
               (
                   EXISTS (
                       SELECT 1 FROM pg_class AS object
                       JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                       WHERE namespace.nspname = '{schemaName}'
                         AND object.relowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                   )
                   OR EXISTS (
                       SELECT 1 FROM pg_proc AS function
                       JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
                       WHERE namespace.nspname = '{schemaName}'
                         AND function.proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                   )
               ),
               has_table_privilege(current_user, '{schemaName}.policy_runtime_revisions', 'SELECT'),
               has_table_privilege(current_user, '{schemaName}.approval_decisions', 'SELECT'),
               has_table_privilege(current_user, '{schemaName}.approval_status_revisions', 'SELECT'),
               (
                   has_table_privilege(current_user, '{schemaName}.approval_submission_attempts', 'SELECT')
                   AND has_table_privilege(current_user, '{schemaName}.approval_submission_acknowledgements', 'SELECT')
                   AND has_table_privilege(current_user, '{schemaName}.approval_submission_quarantines', 'SELECT')
                   AND has_table_privilege(current_user, '{schemaName}.approval_submission_reconciliations', 'SELECT')
                   AND has_table_privilege(current_user, '{schemaName}.approval_submission_recoveries', 'SELECT')
               ),
               EXISTS (
                   SELECT 1
                   FROM pg_class AS object
                   JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                   WHERE namespace.nspname = '{schemaName}'
                     AND (
                         (
                             object.relkind IN ('r', 'p', 'v', 'm', 'f')
                             AND (
                                 (
                                     object.relname NOT IN (
                                         'policy_runtime_revisions', 'approval_decisions',
                                         'approval_status_revisions', 'approval_submission_attempts',
                                         'approval_submission_acknowledgements', 'approval_submission_quarantines',
                                         'approval_submission_reconciliations', 'approval_submission_recoveries')
                                     AND (
                                         has_table_privilege(current_user, object.oid, 'SELECT')
                                         OR has_table_privilege(current_user, object.oid, 'INSERT')
                                         OR has_table_privilege(current_user, object.oid, 'UPDATE')
                                         OR has_table_privilege(current_user, object.oid, 'DELETE')
                                         OR has_table_privilege(current_user, object.oid, 'TRUNCATE')
                                         OR has_table_privilege(current_user, object.oid, 'REFERENCES')
                                         OR has_table_privilege(current_user, object.oid, 'TRIGGER')
                                     )
                                 )
                                 OR (
                                     object.relname IN (
                                         'policy_runtime_revisions', 'approval_decisions',
                                         'approval_status_revisions', 'approval_submission_attempts',
                                         'approval_submission_acknowledgements', 'approval_submission_quarantines',
                                         'approval_submission_reconciliations', 'approval_submission_recoveries')
                                     AND (
                                         has_table_privilege(current_user, object.oid, 'INSERT')
                                         OR has_table_privilege(current_user, object.oid, 'UPDATE')
                                         OR has_table_privilege(current_user, object.oid, 'DELETE')
                                         OR has_table_privilege(current_user, object.oid, 'TRUNCATE')
                                         OR has_table_privilege(current_user, object.oid, 'REFERENCES')
                                         OR has_table_privilege(current_user, object.oid, 'TRIGGER')
                                     )
                                 )
                             )
                         )
                         OR (
                             object.relkind = 'S'
                             AND (
                                 has_sequence_privilege(current_user, object.oid, 'USAGE')
                                 OR has_sequence_privilege(current_user, object.oid, 'SELECT')
                                 OR has_sequence_privilege(current_user, object.oid, 'UPDATE')
                             )
                         )
                     )
               ),
               has_function_privilege(current_user, '{schemaName}.begin_approval_submission(uuid,timestamp with time zone,jsonb,text,jsonb,text)', 'EXECUTE'),
               has_function_privilege(current_user, '{schemaName}.acknowledge_approval_submission(jsonb,text,jsonb,text)', 'EXECUTE'),
               has_function_privilege(current_user, '{schemaName}.quarantine_approval_submission(uuid,uuid,text,text,jsonb,text)', 'EXECUTE'),
               has_function_privilege(current_user, '{schemaName}.reconcile_approval_submission(jsonb,text,jsonb,text)', 'EXECUTE'),
               has_function_privilege(current_user, '{schemaName}.recover_approval_submission(jsonb,text,jsonb,text)', 'EXECUTE'),
               EXISTS (
                   SELECT 1
                   FROM pg_proc AS function
                   JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
                   WHERE namespace.nspname = '{schemaName}'
                     AND has_function_privilege(current_user, function.oid, 'EXECUTE')
                     AND function.oid NOT IN (
                         to_regprocedure('{schemaName}.begin_approval_submission(uuid,timestamp with time zone,jsonb,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.acknowledge_approval_submission(jsonb,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.quarantine_approval_submission(uuid,uuid,text,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.reconcile_approval_submission(jsonb,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.recover_approval_submission(jsonb,text,jsonb,text)'))
               ),
               EXISTS (
                   SELECT 1
                   FROM pg_proc AS function
                   JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
                   WHERE namespace.nspname = '{schemaName}'
                     AND function.proname IN (
                         'begin_approval_submission', 'acknowledge_approval_submission',
                         'quarantine_approval_submission', 'reconcile_approval_submission',
                         'recover_approval_submission', 'assert_submission_executor_role',
                         'assert_submission_reconciliation_role', 'assert_submission_recovery_role',
                         'assert_submission_runtime_role', 'assert_exact_submission_json')
                     AND function.oid NOT IN (
                         to_regprocedure('{schemaName}.begin_approval_submission(uuid,timestamp with time zone,jsonb,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.acknowledge_approval_submission(jsonb,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.quarantine_approval_submission(uuid,uuid,text,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.reconcile_approval_submission(jsonb,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.recover_approval_submission(jsonb,text,jsonb,text)'),
                         to_regprocedure('{schemaName}.assert_submission_executor_role()'),
                         to_regprocedure('{schemaName}.assert_submission_reconciliation_role()'),
                         to_regprocedure('{schemaName}.assert_submission_recovery_role()'),
                         to_regprocedure('{schemaName}.assert_exact_submission_json(jsonb,text[],text)'))
               )
        FROM pg_roles AS role
        WHERE role.rolname = current_user
        """;

    internal static async Task VerifyAsync(
        NpgsqlConnection connection,
        string expectedRoleName,
        string schemaName,
        PolicyApprovalSubmissionDatabaseRole role,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PrivilegeSql(schemaName), connection) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), expectedRoleName, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), expectedRoleName, StringComparison.Ordinal)
            || Enumerable.Range(2, 6).Any(reader.GetBoolean)
            || !reader.GetBoolean(8)
            || reader.GetBoolean(9)
            || reader.GetBoolean(10)
            || reader.GetBoolean(15)
            || reader.GetBoolean(21)
            || reader.GetBoolean(22)
            || !PrivilegesMatchRole(reader, role))
            throw new UnauthorizedAccessException($"PostgreSQL submission {role} role privileges exceed or miss the exact allowlist.");
    }

    private static bool PrivilegesMatchRole(NpgsqlDataReader reader, PolicyApprovalSubmissionDatabaseRole role)
    {
        var readsAuthoritativePolicy = reader.GetBoolean(11) && reader.GetBoolean(12) && reader.GetBoolean(13);
        if (!reader.GetBoolean(14)) return false;
        return role switch
        {
            PolicyApprovalSubmissionDatabaseRole.Executor =>
                readsAuthoritativePolicy
                && reader.GetBoolean(16)
                && reader.GetBoolean(17)
                && reader.GetBoolean(18)
                && !reader.GetBoolean(19)
                && !reader.GetBoolean(20),
            PolicyApprovalSubmissionDatabaseRole.Reconciliation =>
                !reader.GetBoolean(11)
                && !reader.GetBoolean(12)
                && !reader.GetBoolean(13)
                && !reader.GetBoolean(16)
                && !reader.GetBoolean(17)
                && !reader.GetBoolean(18)
                && reader.GetBoolean(19)
                && !reader.GetBoolean(20),
            PolicyApprovalSubmissionDatabaseRole.Recovery =>
                !reader.GetBoolean(11)
                && !reader.GetBoolean(12)
                && !reader.GetBoolean(13)
                && !reader.GetBoolean(16)
                && !reader.GetBoolean(17)
                && !reader.GetBoolean(18)
                && !reader.GetBoolean(19)
                && reader.GetBoolean(20),
            _ => false
        };
    }
}

public sealed class PostgresPolicyApprovalMigrator
{
    private static readonly string[] MigrationResourceSuffixes =
    [
        "001_create_policy_approval.sql",
        "002_configure_policy_runtime_role.sql",
        "003_create_submission_lifecycle.sql",
        "004_create_native_stop_challenge_ledger.sql"
    ];
    private readonly PolicyApprovalMigrationOptions _options;

    public PostgresPolicyApprovalMigrator(PolicyApprovalMigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(_options.MigrationConnectionString)
        {
            Timeout = 5,
            CommandTimeout = 5
        };
        if (builder.Port == 55434 || string.Equals(builder.Database, "dps_gbrain_company", StringComparison.Ordinal))
            throw new InvalidOperationException("Policy Approval migrations refuse the dedicated GBrain Company PostgreSQL service.");

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (string.Equals(connection.Database, "dps_gbrain_company", StringComparison.Ordinal))
            throw new InvalidOperationException("Policy Approval migrations refuse the dedicated GBrain Company database.");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var suffix in MigrationResourceSuffixes)
        {
            var migration = await ReadMigrationAsync(suffix, cancellationToken);
            migration = migration
                .Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal)
                .Replace("__RUNTIME_ROLE__", _options.RuntimeRoleName, StringComparison.Ordinal)
                .Replace("__SUBMISSION_EXECUTOR_ROLE__", _options.SubmissionExecutorRoleName, StringComparison.Ordinal)
                .Replace("__SUBMISSION_RECONCILIATION_ROLE__", _options.SubmissionReconciliationRoleName, StringComparison.Ordinal)
                .Replace("__SUBMISSION_RECOVERY_ROLE__", _options.SubmissionRecoveryRoleName, StringComparison.Ordinal);
            await using var command = new NpgsqlCommand(migration, connection, transaction) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> ReadMigrationAsync(string suffix, CancellationToken cancellationToken)
    {
        var assembly = typeof(PostgresPolicyApprovalMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{suffix}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

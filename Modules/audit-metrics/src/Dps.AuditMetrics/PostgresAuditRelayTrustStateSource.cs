using Npgsql;

namespace Dps.AuditMetrics;

public sealed class PostgresAuditRelayTrustStateSource
{
    private readonly AuditMetricsPostgresOptions _options;

    public PostgresAuditRelayTrustStateSource(AuditMetricsPostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async ValueTask<AuditRelayTrustStateEnvelope> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await AuditPostgresRuntimeConnection.OpenVerifiedAsync(
            _options,
            cancellationToken);
        return await ReadCurrentAsync(connection, transaction: null, cancellationToken);
    }

    internal async ValueTask<AuditRelayTrustStateEnvelope> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT schema_version, contract_id, state_id, revision,
                   active_release_bom_sha256, relay_key_id, relay_public_key_sha256,
                   relay_key_status, valid_from, valid_until, signature_base64
            FROM {_options.SchemaName}.audit_relay_trust_states
            ORDER BY revision DESC
            LIMIT 1
            """,
            connection,
            transaction)
        {
            CommandTimeout = AuditPostgresRuntimeConnection.MaximumSeconds
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new UnauthorizedAccessException("Authoritative relay trust-state store is empty.");
        }

        var state = new AuditRelayTrustStateEnvelope(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8).ToUniversalTime(),
            reader.GetFieldValue<DateTimeOffset>(9).ToUniversalTime(),
            reader.GetString(10));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Authoritative relay trust-state query returned more than one current row.");
        }

        return state;
    }
}

internal static class AuditPostgresRuntimeConnection
{
    public const int MaximumSeconds = 5;

    public static async Task<NpgsqlConnection> OpenVerifiedAsync(
        AuditMetricsPostgresOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var settings = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            Timeout = MaximumSeconds,
            CommandTimeout = MaximumSeconds
        };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await VerifyRuntimeRoleAsync(connection, options, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task VerifyRuntimeRoleAsync(
        NpgsqlConnection connection,
        AuditMetricsPostgresOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT session_user::text,
                   current_user::text,
                   r.rolcanlogin,
                   r.rolsuper,
                   r.rolcreatedb,
                   r.rolcreaterole,
                   r.rolreplication,
                   r.rolbypassrls,
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_auth_members m
                       WHERE m.member = r.oid),
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_database d
                       WHERE d.datname = current_database()
                         AND d.datdba = r.oid),
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_namespace n
                       WHERE n.nspname = @schema_name
                         AND n.nspowner = r.oid),
                   NOT EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_class c
                       JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                       WHERE n.nspname = @schema_name
                         AND c.relowner = r.oid),
                   NOT EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_proc p
                       JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                       WHERE n.nspname = @schema_name
                         AND p.proowner = r.oid),
                   has_schema_privilege(r.rolname, @schema_name, 'USAGE'),
                   NOT has_schema_privilege(r.rolname, @schema_name, 'CREATE'),
                   has_table_privilege(r.rolname, @events_table, 'SELECT'),
                   has_table_privilege(r.rolname, @events_table, 'INSERT'),
                   NOT has_table_privilege(r.rolname, @events_table, 'UPDATE'),
                   NOT has_table_privilege(r.rolname, @events_table, 'DELETE'),
                   NOT has_table_privilege(r.rolname, @events_table, 'TRUNCATE'),
                   NOT has_table_privilege(r.rolname, @events_table, 'REFERENCES'),
                   NOT has_table_privilege(r.rolname, @events_table, 'TRIGGER'),
                   has_table_privilege(r.rolname, @quarantine_table, 'SELECT'),
                   has_table_privilege(r.rolname, @quarantine_table, 'INSERT'),
                   NOT has_table_privilege(r.rolname, @quarantine_table, 'UPDATE'),
                   NOT has_table_privilege(r.rolname, @quarantine_table, 'DELETE'),
                   NOT has_table_privilege(r.rolname, @quarantine_table, 'TRUNCATE'),
                   NOT has_table_privilege(r.rolname, @quarantine_table, 'REFERENCES'),
                   NOT has_table_privilege(r.rolname, @quarantine_table, 'TRIGGER'),
                   has_table_privilege(r.rolname, @trust_table, 'SELECT'),
                   NOT has_table_privilege(r.rolname, @trust_table, 'INSERT'),
                   NOT has_table_privilege(r.rolname, @trust_table, 'UPDATE'),
                   NOT has_table_privilege(r.rolname, @trust_table, 'DELETE'),
                   NOT has_table_privilege(r.rolname, @trust_table, 'TRUNCATE'),
                   NOT has_table_privilege(r.rolname, @trust_table, 'REFERENCES'),
                   NOT has_table_privilege(r.rolname, @trust_table, 'TRIGGER'),
                   has_function_privilege(r.rolname, @json_helper, 'EXECUTE'),
                   NOT has_function_privilege(r.rolname, @event_reject, 'EXECUTE'),
                   NOT has_function_privilege(r.rolname, @quarantine_reject, 'EXECUTE'),
                   NOT has_function_privilege(r.rolname, @trust_reject, 'EXECUTE'),
                   NOT has_function_privilege(r.rolname, @trust_serialize, 'EXECUTE')
            FROM pg_catalog.pg_roles r
            WHERE r.rolname::text = session_user::text
            """,
            connection)
        {
            CommandTimeout = MaximumSeconds
        };
        command.Parameters.AddWithValue("schema_name", options.SchemaName);
        command.Parameters.AddWithValue("events_table", $"{options.SchemaName}.audit_events");
        command.Parameters.AddWithValue("quarantine_table", $"{options.SchemaName}.audit_quarantine");
        command.Parameters.AddWithValue("trust_table", $"{options.SchemaName}.audit_relay_trust_states");
        command.Parameters.AddWithValue("json_helper", $"{options.SchemaName}.jsonb_has_exact_keys(jsonb,text[])");
        command.Parameters.AddWithValue("event_reject", $"{options.SchemaName}.reject_audit_event_mutation()");
        command.Parameters.AddWithValue("quarantine_reject", $"{options.SchemaName}.reject_audit_quarantine_mutation()");
        command.Parameters.AddWithValue("trust_reject", $"{options.SchemaName}.reject_audit_relay_trust_state_mutation()");
        command.Parameters.AddWithValue("trust_serialize", $"{options.SchemaName}.serialize_audit_relay_trust_state_append()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), options.RuntimeRoleName, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), options.RuntimeRoleName, StringComparison.Ordinal)
            || !reader.GetBoolean(2)
            || reader.GetBoolean(3)
            || reader.GetBoolean(4)
            || reader.GetBoolean(5)
            || reader.GetBoolean(6)
            || reader.GetBoolean(7)
            || Enumerable.Range(8, reader.FieldCount - 8).Any(index => !reader.GetBoolean(index)))
        {
            throw new UnauthorizedAccessException(
                "Audit PostgreSQL connection is not an isolated, login-capable, unprivileged runtime identity.");
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new UnauthorizedAccessException("Audit PostgreSQL runtime role attestation was ambiguous.");
        }
    }
}

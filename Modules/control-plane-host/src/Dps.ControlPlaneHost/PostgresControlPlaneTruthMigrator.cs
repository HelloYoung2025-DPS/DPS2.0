using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Dps.ControlPlaneHost;

internal static class PostgresControlPlaneConnectionPolicy
{
    internal const int MaximumSeconds = 5;
    private const int GBrainCompanyPort = 55434;
    private const string GBrainCompanyDatabase = "dps_gbrain_company";
    private static readonly Regex SafeIdentifier = new(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifier.IsMatch(value))
        {
            throw new ArgumentException("PostgreSQL identifier is not allowlisted.", parameterName);
        }
    }

    internal static string Normalize(
        string connectionString,
        string parameterName,
        string? requiredUsername = null,
        string? forbiddenUsername = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", parameterName);
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The PostgreSQL connection string is invalid.", parameterName, exception);
        }

        if (!HasExplicitKey(builder, "Host")
            || string.IsNullOrWhiteSpace(builder.Host)
            || !HasExplicitKey(builder, "Port")
            || !HasExplicitKey(builder, "Database")
            || string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new ArgumentException(
                "PostgreSQL Host, Port, and Database must be explicit.",
                parameterName);
        }

        if (builder.Port == GBrainCompanyPort
            || string.Equals(builder.Database, GBrainCompanyDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Control Plane PostgreSQL must never use the persistent GBrain Company service.");
        }

        if (!HasExplicitKey(builder, "Username")
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new ArgumentException("An explicit PostgreSQL username is required.", parameterName);
        }

        if (requiredUsername is not null
            && !string.Equals(builder.Username, requiredUsername, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The runtime connection must authenticate as the declared runtime role.");
        }

        if (forbiddenUsername is not null
            && string.Equals(builder.Username, forbiddenUsername, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Migration and runtime PostgreSQL identities must be distinct.");
        }

        if (!string.IsNullOrWhiteSpace(builder.Options))
        {
            throw new UnauthorizedAccessException(
                "PostgreSQL startup options are forbidden because they can change the authenticated role or session policy.");
        }

        builder.LogParameters = false;
        builder.IncludeErrorDetail = false;
        builder.PersistSecurityInfo = false;
        builder.Timeout = MaximumSeconds;
        builder.CommandTimeout = MaximumSeconds;
        builder.Pooling = false;
        return builder.ConnectionString;
    }

    private static bool HasExplicitKey(
        NpgsqlConnectionStringBuilder builder,
        string canonicalKey)
        => builder.Keys.Cast<string>().Any(key =>
            string.Equals(key, canonicalKey, StringComparison.Ordinal));

    internal static async Task RequirePostgres184Async(
        NpgsqlConnection connection,
        string expectedDatabase,
        int expectedPort,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection)
        {
            CommandTimeout = MaximumSeconds
        };
        var actual = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(actual, "180004", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Control Plane Integration requires exact PostgreSQL 18.4; server_version_num was '{actual ?? "missing"}'.");
        }

        await using var identityCommand = new NpgsqlCommand(
            "SELECT current_database()::text, inet_server_port()::integer",
            connection)
        {
            CommandTimeout = MaximumSeconds
        };
        await using var reader = await identityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), expectedDatabase, StringComparison.Ordinal)
            || reader.IsDBNull(1)
            || reader.GetInt32(1) != expectedPort
            || string.Equals(reader.GetString(0), GBrainCompanyDatabase, StringComparison.OrdinalIgnoreCase)
            || reader.GetInt32(1) == GBrainCompanyPort
            || await reader.ReadAsync(cancellationToken))
        {
            throw new UnauthorizedAccessException(
                "The connected PostgreSQL database or server port differs from the explicit non-GBrain target.");
        }
    }

    internal static async Task ConfigureAndVerifyTimeoutsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var configure = new NpgsqlCommand(
            "SET statement_timeout = '5s'; SET lock_timeout = '5s'; SET idle_in_transaction_session_timeout = '5s'",
            connection)
        {
            CommandTimeout = MaximumSeconds
        })
        {
            await configure.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var verify = new NpgsqlCommand(
            """
            SELECT current_setting('statement_timeout') = '5s',
                   current_setting('lock_timeout') = '5s',
                   current_setting('idle_in_transaction_session_timeout') = '5s',
                   current_setting('session_replication_role') = 'origin'
            """,
            connection)
        {
            CommandTimeout = MaximumSeconds
        };
        await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || Enumerable.Range(0, 4).Any(index => !reader.GetBoolean(index))
            || await reader.ReadAsync(cancellationToken))
        {
            throw new UnauthorizedAccessException(
                "PostgreSQL timeout or trigger-execution session policy is unsafe.");
        }
    }
}

public sealed class PostgresControlPlaneMigrationOptions
{
    private readonly string _migrationConnectionString;

    public PostgresControlPlaneMigrationOptions(
        string migrationConnectionString,
        string schemaName,
        string runtimeRoleName)
    {
        _migrationConnectionString = migrationConnectionString;
        SchemaName = schemaName;
        RuntimeRoleName = runtimeRoleName;
    }

    public string SchemaName { get; }
    public string RuntimeRoleName { get; }

    internal string ValidatedConnectionString()
    {
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(SchemaName, nameof(SchemaName));
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(RuntimeRoleName, nameof(RuntimeRoleName));
        return PostgresControlPlaneConnectionPolicy.Normalize(
            _migrationConnectionString,
            "migrationConnectionString",
            forbiddenUsername: RuntimeRoleName);
    }

    public override string ToString()
        => $"PostgresControlPlaneMigrationOptions {{ SchemaName = {SchemaName}, RuntimeRoleName = {RuntimeRoleName}, MigrationConnectionString = [REDACTED] }}";
}

public sealed class PostgresControlPlaneTruthMigrator
{
    private const string MigrationResourceSuffix = "001_create_control_plane_truth.sql";
    private readonly PostgresControlPlaneMigrationOptions _options;
    private readonly string _connectionString;
    private readonly string _expectedDatabase;
    private readonly int _expectedPort;

    public PostgresControlPlaneTruthMigrator(PostgresControlPlaneMigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.ValidatedConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        _expectedDatabase = builder.Database
            ?? throw new InvalidOperationException("An explicit PostgreSQL database is required.");
        _expectedPort = builder.Port;
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await PostgresControlPlaneConnectionPolicy.RequirePostgres184Async(
            connection,
            _expectedDatabase,
            _expectedPort,
            cancellationToken);
        await PostgresControlPlaneConnectionPolicy.ConfigureAndVerifyTimeoutsAsync(
            connection,
            cancellationToken);
        await RequireMigrationIdentityAsync(connection, cancellationToken);

        var migration = (await ReadMigrationAsync(cancellationToken))
            .Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal)
            .Replace("__RUNTIME_ROLE__", _options.RuntimeRoleName, StringComparison.Ordinal);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(migration, connection, transaction)
        {
            CommandTimeout = PostgresControlPlaneConnectionPolicy.MaximumSeconds
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AppendProviderTrustStateAsync(
        ProviderTrustStateV1 state,
        CancellationToken cancellationToken = default)
    {
        ProviderResultAuthorization.ValidateTrustState(state);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await PostgresControlPlaneConnectionPolicy.RequirePostgres184Async(
            connection,
            _expectedDatabase,
            _expectedPort,
            cancellationToken);
        await PostgresControlPlaneConnectionPolicy.ConfigureAndVerifyTimeoutsAsync(
            connection,
            cancellationToken);
        await RequireMigrationIdentityAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@contract_id, 0))",
            connection,
            transaction)
        {
            CommandTimeout = PostgresControlPlaneConnectionPolicy.MaximumSeconds
        })
        {
            lockCommand.Parameters.AddWithValue("contract_id", state.SourceContractId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_options.SchemaName);
        long latestRevision;
        await using (var latest = new NpgsqlCommand(
            $"SELECT COALESCE(max(revision), 0) FROM {quotedSchema}.provider_trust_states WHERE source_contract_id = @contract_id",
            connection,
            transaction)
        {
            CommandTimeout = PostgresControlPlaneConnectionPolicy.MaximumSeconds
        })
        {
            latest.Parameters.AddWithValue("contract_id", state.SourceContractId);
            latestRevision = Convert.ToInt64(
                await latest.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (state.Revision != latestRevision + 1)
        {
            throw new InvalidOperationException(
                "Provider trust revision must append exactly after the persisted latest revision.");
        }

        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {quotedSchema}.provider_trust_states
                (source_contract_id, revision, source_producer_module,
                 active_release_bom_sha256, provider_key_id,
                 provider_public_key_spki_base64, provider_public_key_sha256,
                 status, valid_from, valid_until)
            VALUES
                (@source_contract_id, @revision, @source_producer_module,
                 @active_release_bom_sha256, @provider_key_id,
                 @provider_public_key_spki_base64, @provider_public_key_sha256,
                 @status, @valid_from, @valid_until)
            """,
            connection,
            transaction)
        {
            CommandTimeout = PostgresControlPlaneConnectionPolicy.MaximumSeconds
        };
        insert.Parameters.AddWithValue("source_contract_id", state.SourceContractId);
        insert.Parameters.AddWithValue("revision", state.Revision);
        insert.Parameters.AddWithValue("source_producer_module", state.SourceProducerModule);
        insert.Parameters.AddWithValue("active_release_bom_sha256", state.ActiveReleaseBomSha256);
        insert.Parameters.AddWithValue("provider_key_id", state.ProviderKeyId);
        insert.Parameters.AddWithValue("provider_public_key_spki_base64", state.ProviderPublicKeySpkiBase64);
        insert.Parameters.AddWithValue("provider_public_key_sha256", state.ProviderPublicKeySha256);
        insert.Parameters.AddWithValue("status", state.Status);
        insert.Parameters.AddWithValue("valid_from", state.ValidFrom);
        insert.Parameters.AddWithValue("valid_until", state.ValidUntil);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RequireMigrationIdentityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT session_user::text, current_user::text",
            connection)
        {
            CommandTimeout = PostgresControlPlaneConnectionPolicy.MaximumSeconds
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), reader.GetString(1), StringComparison.Ordinal)
            || string.Equals(reader.GetString(0), _options.RuntimeRoleName, StringComparison.Ordinal)
            || await reader.ReadAsync(cancellationToken))
        {
            throw new UnauthorizedAccessException(
                "Migrations require one explicit non-runtime PostgreSQL login without SET ROLE indirection.");
        }
    }

    private static async Task<string> ReadMigrationAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(PostgresControlPlaneTruthMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(MigrationResourceSuffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded Control Plane migration is missing.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

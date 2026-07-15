using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Xunit;

namespace Dps.CommandOrchestrator.Tests;

internal sealed class DisposablePostgresHarness : IAsyncDisposable
{
    private const string MarkerSchema = "dps_test_harness";
    private const string MarkerTable = "disposable_database_marker";
    private const string MarkerDomain = "dps.command-orchestrator.disposable-postgresql/v1";

    private readonly string _bootstrapConnectionString;
    private readonly string _databaseName;
    private readonly string _bootstrapRole;
    private readonly long _guardKey;
    private readonly byte[] _markerNonce;
    private readonly string _markerSha256;
    private NpgsqlConnection? _guardConnection;
    private int _disposed;

    private DisposablePostgresHarness(
        string bootstrapConnectionString,
        string databaseName,
        string bootstrapRole,
        string migratorConnectionString,
        string runtimeConnectionString,
        string migratorRole,
        string runtimeRole,
        long guardKey,
        byte[] markerNonce,
        string markerSha256,
        NpgsqlConnection guardConnection)
    {
        _bootstrapConnectionString = bootstrapConnectionString;
        _databaseName = databaseName;
        _bootstrapRole = bootstrapRole;
        MigratorConnectionString = migratorConnectionString;
        RuntimeConnectionString = runtimeConnectionString;
        MigratorRole = migratorRole;
        RuntimeRole = runtimeRole;
        _guardKey = guardKey;
        _markerNonce = markerNonce;
        _markerSha256 = markerSha256;
        _guardConnection = guardConnection;
    }

    internal string MigratorConnectionString { get; }
    internal string RuntimeConnectionString { get; }
    internal string MigratorRole { get; }
    internal string RuntimeRole { get; }
    internal string BootstrapRole => _bootstrapRole;

    internal static string RequireBootstrapConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "DPS_TEST_POSTGRES must be an exact PostgreSQL 18.4 bootstrap-admin DSN capable of creating and destroying a dedicated test database; missing infrastructure is NOT_RUN/INFRA_ERROR, never PASS or skip.");
        }
        return value;
    }

    internal static async Task<DisposablePostgresHarness> CreateAsync(
        string commandSchema,
        bool runtimeOwnedSchema,
        CancellationToken cancellationToken)
    {
        var configured = RequireBootstrapConnectionString();
        var bootstrapBuilder = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
        if (string.IsNullOrWhiteSpace(bootstrapBuilder.Host)
            || string.IsNullOrWhiteSpace(bootstrapBuilder.Database)
            || string.IsNullOrWhiteSpace(bootstrapBuilder.Username))
        {
            throw new InvalidOperationException(
                "DPS_TEST_POSTGRES requires explicit host, maintenance database, and bootstrap-admin username.");
        }

        var suffix = Guid.NewGuid().ToString("N")[..18];
        var databaseName = "cmd_it_db_" + suffix;
        var migratorRole = "cmd_mig_" + suffix;
        var runtimeRole = "cmd_rt_" + suffix;
        var migratorPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var runtimePassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var nonce = RandomNumberGenerator.GetBytes(32);
        var guardBytes = RandomNumberGenerator.GetBytes(sizeof(long));
        var guardKey = BinaryPrimitives.ReadInt64BigEndian(guardBytes);
        CryptographicOperations.ZeroMemory(guardBytes);
        var bootstrapRole = string.Empty;
        var roleCreated = false;
        var runtimeCreated = false;
        var databaseCreated = false;
        NpgsqlConnection? guard = null;
        try
        {
            await using (var bootstrap = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
            {
                await bootstrap.OpenAsync(cancellationToken);
                await using (var identity = new NpgsqlCommand(
                    """
                    SELECT current_user, session_user,
                           current_setting('server_version_num')::integer,
                           role_value.rolsuper, role_value.rolcreatedb, role_value.rolcreaterole
                    FROM pg_roles AS role_value
                    WHERE role_value.rolname = current_user
                    """, bootstrap))
                await using (var reader = await identity.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("Bootstrap PostgreSQL identity was not found.");
                    bootstrapRole = reader.GetString(0);
                    if (!string.Equals(bootstrapRole, reader.GetString(1), StringComparison.Ordinal))
                        throw new InvalidOperationException("DPS_TEST_POSTGRES may not use SET ROLE masquerading.");
                    if (reader.GetInt32(2) != 180004)
                        throw new InvalidOperationException("Command Orchestrator Integration requires exact PostgreSQL 18.4.");
                    if (!reader.GetBoolean(3) && (!reader.GetBoolean(4) || !reader.GetBoolean(5)))
                        throw new InvalidOperationException(
                            "DPS_TEST_POSTGRES bootstrap identity must be superuser or have both CREATEDB and CREATEROLE for isolated test provisioning.");
                }

                await ExecuteFormattedAsync(
                    bootstrap,
                    "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', @role_name, @password)",
                    migratorRole,
                    migratorPassword,
                    cancellationToken);
                roleCreated = true;
                await ExecuteFormattedAsync(
                    bootstrap,
                    "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', @role_name, @password)",
                    runtimeRole,
                    runtimePassword,
                    cancellationToken);
                runtimeCreated = true;
                await using var formatDatabase = new NpgsqlCommand(
                    "SELECT format('CREATE DATABASE %I OWNER %I TEMPLATE template0 ENCODING ''UTF8''', @database_name, @owner_name)",
                    bootstrap);
                formatDatabase.Parameters.AddWithValue("database_name", databaseName);
                formatDatabase.Parameters.AddWithValue("owner_name", migratorRole);
                var createDatabaseSql = (string)(await formatDatabase.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("PostgreSQL did not format CREATE DATABASE."));
                await using var createDatabase = new NpgsqlCommand(createDatabaseSql, bootstrap) { CommandTimeout = 15 };
                await createDatabase.ExecuteNonQueryAsync(cancellationToken);
                databaseCreated = true;
            }

            var migratorBuilder = new NpgsqlConnectionStringBuilder(bootstrapBuilder.ConnectionString)
            {
                Database = databaseName,
                Username = migratorRole,
                Password = migratorPassword,
                Pooling = false
            };
            var runtimeBuilder = new NpgsqlConnectionStringBuilder(bootstrapBuilder.ConnectionString)
            {
                Database = databaseName,
                Username = runtimeRole,
                Password = runtimePassword,
                Pooling = false
            };
            var guardBuilder = new NpgsqlConnectionStringBuilder(bootstrapBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false
            };
            var markerSha256 = ComputeMarkerSha256(nonce, databaseName, migratorRole, guardKey);
            guard = new NpgsqlConnection(guardBuilder.ConnectionString);
            await guard.OpenAsync(cancellationToken);
            var builder = new NpgsqlCommandBuilder();
            var quotedMarkerSchema = builder.QuoteIdentifier(MarkerSchema);
            var quotedMarkerTable = builder.QuoteIdentifier(MarkerTable);
            var quotedBootstrap = builder.QuoteIdentifier(bootstrapRole);
            var quotedMigrator = builder.QuoteIdentifier(migratorRole);
            var quotedRuntime = builder.QuoteIdentifier(runtimeRole);
            await using (var provision = new NpgsqlCommand(
                $"""
                CREATE SCHEMA {quotedMarkerSchema} AUTHORIZATION {quotedBootstrap};
                REVOKE ALL ON SCHEMA {quotedMarkerSchema} FROM PUBLIC, {quotedMigrator}, {quotedRuntime};
                CREATE TABLE {quotedMarkerSchema}.{quotedMarkerTable}(
                    harness_id uuid PRIMARY KEY,
                    database_name text COLLATE "C" NOT NULL,
                    database_owner text COLLATE "C" NOT NULL,
                    guard_backend_pid integer NOT NULL,
                    marker_sha256 text COLLATE "C" NOT NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp());
                REVOKE ALL ON {quotedMarkerSchema}.{quotedMarkerTable} FROM PUBLIC, {quotedMigrator}, {quotedRuntime};
                INSERT INTO {quotedMarkerSchema}.{quotedMarkerTable}(
                    harness_id, database_name, database_owner, guard_backend_pid, marker_sha256)
                VALUES (@harness_id, current_database(), @database_owner, pg_backend_pid(), @marker_sha256);
                SELECT pg_advisory_lock(@guard_key);
                """, guard))
            {
                provision.Parameters.AddWithValue("harness_id", Guid.NewGuid());
                provision.Parameters.AddWithValue("database_owner", migratorRole);
                provision.Parameters.AddWithValue("marker_sha256", markerSha256);
                provision.Parameters.AddWithValue("guard_key", guardKey);
                await provision.ExecuteNonQueryAsync(cancellationToken);
            }
            if (runtimeOwnedSchema)
            {
                var quotedCommandSchema = builder.QuoteIdentifier(commandSchema);
                await using var createRuntimeSchema = new NpgsqlCommand(
                    $"CREATE SCHEMA {quotedCommandSchema} AUTHORIZATION {quotedRuntime}", guard);
                await createRuntimeSchema.ExecuteNonQueryAsync(cancellationToken);
            }

            var result = new DisposablePostgresHarness(
                bootstrapBuilder.ConnectionString,
                databaseName,
                bootstrapRole,
                migratorBuilder.ConnectionString,
                runtimeBuilder.ConnectionString,
                migratorRole,
                runtimeRole,
                guardKey,
                nonce,
                markerSha256,
                guard);
            guard = null;
            await result.AssertProofAsync(cancellationToken);
            return result;
        }
        catch
        {
            if (guard is not null) await guard.DisposeAsync();
            CryptographicOperations.ZeroMemory(nonce);
            await CleanupCreatedAsync(
                bootstrapBuilder.ConnectionString,
                databaseName,
                migratorRole,
                runtimeRole,
                databaseCreated,
                roleCreated,
                runtimeCreated,
                CancellationToken.None);
            throw;
        }
    }

    internal async Task AssertProofAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_guardConnection is null || _guardConnection.FullState != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Disposable PostgreSQL guard session is not open.");
        var proofBuilder = new NpgsqlConnectionStringBuilder(_bootstrapConnectionString)
        {
            Database = _databaseName,
            Pooling = false
        };
        await using var proof = new NpgsqlConnection(proofBuilder.ConnectionString);
        await proof.OpenAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
            $"""
            SELECT current_database(), current_user, session_user, database_owner.rolname,
                   marker.database_name, marker.database_owner, marker.marker_sha256,
                   marker_owner.rolname, marker.guard_backend_pid
            FROM pg_database AS database_value
            JOIN pg_roles AS database_owner ON database_owner.oid = database_value.datdba
            JOIN {MarkerSchema}.{MarkerTable} AS marker ON true
            JOIN pg_class AS marker_table ON marker_table.oid = '{MarkerSchema}.{MarkerTable}'::regclass
            JOIN pg_roles AS marker_owner ON marker_owner.oid = marker_table.relowner
            WHERE database_value.datname = current_database()
            """, proof))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)
                || !string.Equals(reader.GetString(0), _databaseName, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), _bootstrapRole, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), _bootstrapRole, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(3), MigratorRole, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(4), _databaseName, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(5), MigratorRole, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(6), _markerSha256, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(7), _bootstrapRole, StringComparison.Ordinal)
                || reader.GetInt32(8) != _guardConnection.ProcessID
                || await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Disposable PostgreSQL marker, owner, bootstrap identity, or guard backend proof is invalid.");
            }
        }
        await using var lockProbe = new NpgsqlCommand("SELECT pg_try_advisory_lock(@guard_key)", proof);
        lockProbe.Parameters.AddWithValue("guard_key", _guardKey);
        var unexpectedlyAcquired = Convert.ToBoolean(await lockProbe.ExecuteScalarAsync(cancellationToken));
        if (unexpectedlyAcquired)
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@guard_key)", proof);
            unlock.Parameters.AddWithValue("guard_key", _guardKey);
            _ = await unlock.ExecuteScalarAsync(cancellationToken);
            throw new InvalidOperationException("Disposable PostgreSQL session guard is not held by this harness.");
        }
    }

    internal async Task TamperMarkerAsync(CancellationToken cancellationToken)
    {
        if (_guardConnection is null) throw new InvalidOperationException("Guard is unavailable.");
        await using var command = new NpgsqlCommand(
            $"UPDATE {MarkerSchema}.{MarkerTable} SET marker_sha256 = repeat('0', 64)",
            _guardConnection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task ReleaseGuardAsync()
    {
        if (_guardConnection is null) return;
        await _guardConnection.DisposeAsync();
        _guardConnection = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await ReleaseGuardAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            await CleanupCreatedAsync(
                _bootstrapConnectionString,
                _databaseName,
                MigratorRole,
                RuntimeRole,
                true,
                true,
                true,
                CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_markerNonce);
        }
    }

    private static async Task ExecuteFormattedAsync(
        NpgsqlConnection connection,
        string formatSql,
        string roleName,
        string password,
        CancellationToken cancellationToken)
    {
        await using var format = new NpgsqlCommand(formatSql, connection);
        format.Parameters.AddWithValue("role_name", roleName);
        format.Parameters.AddWithValue("password", password);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not format CREATE ROLE."));
        await using var create = new NpgsqlCommand(sql, connection);
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ComputeMarkerSha256(
        byte[] nonce,
        string databaseName,
        string migratorRole,
        long guardKey)
    {
        var value = string.Join('|', MarkerDomain, Convert.ToHexStringLower(nonce), databaseName,
            migratorRole, guardKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task CleanupCreatedAsync(
        string bootstrapConnectionString,
        string databaseName,
        string migratorRole,
        string runtimeRole,
        bool databaseCreated,
        bool migratorCreated,
        bool runtimeCreated,
        CancellationToken cancellationToken)
    {
        await using var bootstrap = new NpgsqlConnection(bootstrapConnectionString);
        await bootstrap.OpenAsync(cancellationToken);
        var builder = new NpgsqlCommandBuilder();
        if (databaseCreated)
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS {builder.QuoteIdentifier(databaseName)} WITH (FORCE)",
                bootstrap) { CommandTimeout = 15 };
            await dropDatabase.ExecuteNonQueryAsync(cancellationToken);
        }
        if (runtimeCreated)
        {
            await using var dropRuntime = new NpgsqlCommand(
                $"DROP ROLE IF EXISTS {builder.QuoteIdentifier(runtimeRole)}", bootstrap)
            {
                CommandTimeout = 15
            };
            await dropRuntime.ExecuteNonQueryAsync(cancellationToken);
        }
        if (migratorCreated)
        {
            await using var dropMigrator = new NpgsqlCommand(
                $"DROP ROLE IF EXISTS {builder.QuoteIdentifier(migratorRole)}", bootstrap)
            {
                CommandTimeout = 15
            };
            await dropMigrator.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

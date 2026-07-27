using System.Data;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Dps.GBrainProjector;

public sealed record GBrainProjectorMigrationOptions(
    string MigrationConnectionString,
    string RuntimeConnectionString,
    string SchemaName)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MigrationConnectionString))
        {
            throw new ArgumentException("A PostgreSQL migration connection string is required.", nameof(MigrationConnectionString));
        }

        if (string.IsNullOrWhiteSpace(RuntimeConnectionString))
        {
            throw new ArgumentException("A PostgreSQL runtime connection string is required.", nameof(RuntimeConnectionString));
        }

        PostgresGBrainProjectorSchemaVerifier.RequireSafeIdentifier(SchemaName, nameof(SchemaName));
    }

    public override string ToString() =>
        $"GBrainProjectorMigrationOptions {{ MigrationConnectionString = [REDACTED], RuntimeConnectionString = [REDACTED], SchemaName = {SchemaName} }}";
}

public enum GBrainProjectorMigrationDisposition
{
    Created,
    VerifiedExisting
}

public sealed record GBrainProjectorMigrationResult(
    GBrainProjectorMigrationDisposition Disposition,
    string SchemaName,
    string MigrationRole,
    string RuntimeRole);

public sealed class GBrainProjectorPostgresMigrator
{
    private const string MigrationResourceSuffix = "001_create_gbrain_projector.sql";

    public async Task<GBrainProjectorMigrationResult> ApplyAsync(
        GBrainProjectorMigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        await using var migrationConnection = new NpgsqlConnection(options.MigrationConnectionString);
        await using var runtimeConnection = new NpgsqlConnection(options.RuntimeConnectionString);
        await migrationConnection.OpenAsync(cancellationToken);
        await runtimeConnection.OpenAsync(cancellationToken);

        var migrationIdentity = await PostgresGBrainProjectorSchemaVerifier.ReadIdentityAsync(
            migrationConnection, null, cancellationToken);
        var runtimeIdentity = await PostgresGBrainProjectorSchemaVerifier.ReadIdentityAsync(
            runtimeConnection, null, cancellationToken);
        PostgresGBrainProjectorSchemaVerifier.RequireDirectIdentity(migrationIdentity, "migration");
        PostgresGBrainProjectorSchemaVerifier.RequireDirectIdentity(runtimeIdentity, "runtime");
        if (string.Equals(migrationIdentity.CurrentUser, runtimeIdentity.CurrentUser, StringComparison.Ordinal))
        {
            throw new PostgresSchemaIntegrityException(
                "Migration and runtime PostgreSQL credentials must resolve to different direct login roles.");
        }
        PostgresGBrainProjectorSchemaVerifier.RequireSafeRuntimeRole(runtimeIdentity.CurrentUser);

        await PostgresGBrainProjectorSchemaVerifier.VerifyRuntimePrincipalAsync(
            migrationConnection,
            null,
            runtimeIdentity.CurrentUser,
            options.SchemaName,
            cancellationToken);

        await using var transaction = await migrationConnection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await AcquireMigrationLockAsync(
            migrationConnection,
            transaction,
            options.SchemaName,
            cancellationToken);
        var schemaExists = await SchemaExistsAsync(
            migrationConnection,
            transaction,
            options.SchemaName,
            cancellationToken);

        if (!schemaExists)
        {
            var sql = await ReadMigrationAsync(cancellationToken);
            sql = sql.Replace(
                    "__SCHEMA__",
                    PostgresGBrainProjectorSchemaVerifier.QuoteIdentifier(options.SchemaName),
                    StringComparison.Ordinal)
                .Replace(
                    "__RUNTIME_ROLE__",
                    PostgresGBrainProjectorSchemaVerifier.QuoteIdentifier(runtimeIdentity.CurrentUser),
                    StringComparison.Ordinal);
            await using var command = new NpgsqlCommand(sql, migrationConnection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await PostgresGBrainProjectorSchemaVerifier.VerifyOwnedSchemaAsync(
            migrationConnection,
            transaction,
            options.SchemaName,
            migrationIdentity.CurrentUser,
            runtimeIdentity.CurrentUser,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await PostgresGBrainProjectorSchemaVerifier.VerifyRuntimeAsync(
            runtimeConnection,
            options.SchemaName,
            cancellationToken);

        return new GBrainProjectorMigrationResult(
            schemaExists
                ? GBrainProjectorMigrationDisposition.VerifiedExisting
                : GBrainProjectorMigrationDisposition.Created,
            options.SchemaName,
            migrationIdentity.CurrentUser,
            runtimeIdentity.CurrentUser);
    }

    private static async Task AcquireMigrationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_name, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_name", "dps:gbrain-projector:migration:" + schemaName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> SchemaExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)",
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new PostgresSchemaIntegrityException("PostgreSQL did not return schema existence truth."));
    }

    private static async Task<string> ReadMigrationAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(GBrainProjectorPostgresMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(MigrationResourceSuffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new PostgresSchemaIntegrityException(
                $"Embedded migration '{MigrationResourceSuffix}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

internal sealed record PostgresRoleIdentity(string CurrentUser, string SessionUser);

internal static class PostgresGBrainProjectorSchemaVerifier
{
    private static readonly Regex IdentifierPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z",
        RegexOptions.CultureInvariant);

    private static readonly string[] ExpectedTables =
    [
        "rendered_revisions",
        "source_binding_quarantine",
        "source_bindings"
    ];

    private static readonly IReadOnlyDictionary<string, ColumnExpectation[]> ExpectedColumns =
        new Dictionary<string, ColumnExpectation[]>(StringComparer.Ordinal)
        {
            ["source_bindings"] =
            [
                new("soul_id", "text", true, ""),
                new("source_id", "text", true, ""),
                new("algorithm", "text", true, ""),
                new("nonce", "bigint", true, ""),
                new("soul_hash", "character(64)", true, ""),
                new("allocated_at", "timestamp with time zone", true, ""),
                new("binding_revision", "character(64)", true, ""),
                new("binding_checksum", "character(64)", true, ""),
                new("canonical_json", "text", true, ""),
                new("binding_json", "jsonb", true, ""),
                new("created_at", "timestamp with time zone", true, "clock_timestamp()")
            ],
            ["source_binding_quarantine"] =
            [
                new("quarantine_id", "uuid", true, ""),
                new("soul_id", "text", true, ""),
                new("soul_hash", "character(64)", true, ""),
                new("maximum_nonce", "bigint", true, ""),
                new("reason", "text", true, ""),
                new("created_at", "timestamp with time zone", true, "clock_timestamp()")
            ],
            ["rendered_revisions"] =
            [
                new("soul_id", "text", true, ""),
                new("source_id", "text", true, ""),
                new("source_binding_revision", "character(64)", true, ""),
                new("source_binding_checksum", "character(64)", true, ""),
                new("projection_revision", "character(64)", true, ""),
                new("projection_checksum", "character(64)", true, ""),
                new("source_event_count", "integer", true, ""),
                new("occurred_at", "timestamp with time zone", true, ""),
                new("canonical_json", "text", true, ""),
                new("projection_json", "jsonb", true, ""),
                new("created_at", "timestamp with time zone", true, "clock_timestamp()")
            ]
        };

    private static readonly IReadOnlyDictionary<string, ConstraintExpectation> ExpectedConstraints =
        BuildExpectedConstraints();

    private static readonly IReadOnlyDictionary<string, IndexExpectation> ExpectedIndexes =
        new Dictionary<string, IndexExpectation>(StringComparer.Ordinal)
        {
            ["source_bindings_pkey"] = new(true, ["source_bindings", "soul_id"]),
            ["source_bindings_source_id_key"] = new(true, ["source_bindings", "source_id"]),
            ["source_bindings_binding_revision_key"] = new(true, ["source_bindings", "binding_revision"]),
            ["source_bindings_binding_checksum_key"] = new(true, ["source_bindings", "binding_checksum"]),
            ["source_bindings_identity_proof_key"] = new(true, ["source_bindings", "soul_id", "source_id", "binding_revision", "binding_checksum"]),
            ["source_binding_quarantine_pkey"] = new(true, ["source_binding_quarantine", "quarantine_id"]),
            ["source_binding_quarantine_soul_idx"] = new(false, ["source_binding_quarantine", "soul_id", "created_at"]),
            ["rendered_revisions_pkey"] = new(true, ["rendered_revisions", "soul_id", "projection_revision"]),
            ["rendered_revisions_source_revision_key"] = new(true, ["rendered_revisions", "source_id", "projection_revision"]),
            ["rendered_revisions_soul_checksum_key"] = new(true, ["rendered_revisions", "soul_id", "projection_checksum"]),
            ["rendered_revisions_latest_idx"] = new(false, ["rendered_revisions", "soul_id", "source_event_count DESC", "created_at DESC"])
        };

    private static readonly IReadOnlyDictionary<string, TriggerExpectation> ExpectedTriggers =
        new Dictionary<string, TriggerExpectation>(StringComparer.Ordinal)
        {
            ["source_bindings_append_only_rows"] = new("source_bindings", 27),
            ["source_bindings_append_only_truncate"] = new("source_bindings", 34),
            ["source_binding_quarantine_append_only_rows"] = new("source_binding_quarantine", 27),
            ["source_binding_quarantine_append_only_truncate"] = new("source_binding_quarantine", 34),
            ["rendered_revisions_append_only_rows"] = new("rendered_revisions", 27),
            ["rendered_revisions_append_only_truncate"] = new("rendered_revisions", 34)
        };

    internal static void RequireSafeIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierPattern.IsMatch(value))
        {
            throw new ArgumentException(
                $"{parameterName} must be a safe lowercase PostgreSQL identifier.",
                parameterName);
        }
    }

    internal static string QuoteIdentifier(string value)
    {
        RequireSafeIdentifier(value, nameof(value));
        return '"' + value + '"';
    }

    internal static void RequireSafeRuntimeRole(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierPattern.IsMatch(value))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL runtime login role must use a safe lowercase identifier.");
        }
    }

    internal static async Task<PostgresRoleIdentity> ReadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_user, session_user",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PostgresSchemaIntegrityException("PostgreSQL did not return connection identity.");
        }

        return new PostgresRoleIdentity(reader.GetString(0), reader.GetString(1));
    }

    internal static void RequireDirectIdentity(PostgresRoleIdentity identity, string purpose)
    {
        if (!string.Equals(identity.CurrentUser, identity.SessionUser, StringComparison.Ordinal))
        {
            throw new PostgresSchemaIntegrityException(
                $"The {purpose} connection must use a direct login identity; SET ROLE impersonation is forbidden.");
        }
    }

    internal static async Task VerifyRuntimeAsync(
        NpgsqlConnection connection,
        string schemaName,
        CancellationToken cancellationToken)
    {
        RequireSafeIdentifier(schemaName, nameof(schemaName));
        var identity = await ReadIdentityAsync(connection, null, cancellationToken);
        RequireDirectIdentity(identity, "runtime");
        RequireSafeRuntimeRole(identity.CurrentUser);
        await VerifyRuntimePrincipalAsync(
            connection, null, identity.CurrentUser, schemaName, cancellationToken);
        await VerifyStructureAsync(connection, null, schemaName, cancellationToken);
        await VerifyRuntimeDoesNotOwnSchemaAsync(
            connection, null, schemaName, identity.CurrentUser, cancellationToken);
        await VerifyConsistentSchemaOwnersAsync(
            connection, schemaName, identity.CurrentUser, cancellationToken);
        await VerifyExactRuntimePrivilegesAsync(
            connection, null, schemaName, identity.CurrentUser, cancellationToken);
    }

    internal static async Task VerifyOwnedSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schemaName,
        string migrationRole,
        string runtimeRole,
        CancellationToken cancellationToken)
    {
        await VerifyRuntimePrincipalAsync(
            connection, transaction, runtimeRole, schemaName, cancellationToken);
        await VerifyStructureAsync(connection, transaction, schemaName, cancellationToken);
        await VerifyExactOwnersAsync(
            connection, transaction, schemaName, migrationRole, runtimeRole, cancellationToken);
        await VerifyExactRuntimePrivilegesAsync(
            connection, transaction, schemaName, runtimeRole, cancellationToken);
    }

    internal static async Task VerifyRuntimePrincipalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string runtimeRole,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT r.oid, r.rolcanlogin, r.rolsuper, r.rolcreaterole, r.rolcreatedb,
                   r.rolreplication, r.rolbypassrls,
                   EXISTS (
                       SELECT 1 FROM pg_auth_members membership
                       WHERE membership.member = r.oid
                   ),
                   EXISTS (
                       SELECT 1 FROM pg_auth_members membership
                       WHERE membership.roleid = r.oid
                   ),
                   EXISTS (
                       SELECT 1 FROM pg_database d
                       WHERE d.datname = current_database()
                         AND (d.datdba = r.oid OR has_database_privilege(r.oid, d.oid, 'CREATE'))
                   )
            FROM pg_roles r
            WHERE r.rolname = @runtime_role
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("runtime_role", runtimeRole);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PostgresSchemaIntegrityException("The PostgreSQL runtime role does not exist.");
        }

        if (!reader.GetBoolean(1) ||
            Enumerable.Range(2, 8).Any(reader.GetBoolean))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL runtime role is not an isolated least-privilege login role.");
        }

        await reader.DisposeAsync();
        await VerifyRuntimeDoesNotOwnSchemaAsync(
            connection, transaction, schemaName, runtimeRole, cancellationToken, allowMissingSchema: true);
    }

    private static async Task VerifyStructureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await VerifyNoUnexpectedSchemaObjectsAsync(connection, transaction, schemaName, cancellationToken);
        await VerifyNoUnexpectedAuxiliaryObjectsAsync(connection, transaction, schemaName, cancellationToken);
        await VerifyTableAndColumnShapeAsync(connection, transaction, schemaName, cancellationToken);
        await VerifyConstraintsAsync(connection, transaction, schemaName, cancellationToken);
        await VerifyIndexesAsync(connection, transaction, schemaName, cancellationToken);
        await VerifyTriggersAndFunctionAsync(connection, transaction, schemaName, cancellationToken);
    }

    private static async Task VerifyNoUnexpectedSchemaObjectsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using (var relationCommand = new NpgsqlCommand(
            """
            SELECT c.relname, c.relkind::text
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
            ORDER BY c.relname
            """,
            connection,
            transaction))
        {
            relationCommand.Parameters.AddWithValue("schema", schemaName);
            await using var reader = await relationCommand.ExecuteReaderAsync(cancellationToken);
            var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!actual.TryAdd(reader.GetString(0), reader.GetString(1)))
                {
                    throw new PostgresSchemaIntegrityException(
                        $"The PostgreSQL schema contains duplicate relation metadata for {reader.GetString(0)}.");
                }
            }

            var expected = ExpectedTables
                .ToDictionary(static name => name, static _ => "r", StringComparer.Ordinal);
            foreach (var indexName in ExpectedIndexes.Keys)
            {
                expected.Add(indexName, "i");
            }

            if (actual.Count != expected.Count ||
                expected.Any(item =>
                    !actual.TryGetValue(item.Key, out var kind) ||
                    !string.Equals(kind, item.Value, StringComparison.Ordinal)))
            {
                throw new PostgresSchemaIntegrityException(
                    "The PostgreSQL schema contains an unregistered relation, view, materialized view, sequence, or index.");
            }
        }

        await using var typeCommand = new NpgsqlCommand(
            """
            SELECT type.typname, type.typtype::text, type.typcategory::text,
                   type.typrelid,
                   COALESCE(relation.relname, ''), COALESCE(relation.relkind::text, ''),
                   COALESCE(element.typname, ''), COALESCE(element.typrelid, 0),
                   COALESCE(element_relation.relname, ''),
                   COALESCE(element_relation.relkind::text, '')
            FROM pg_type type
            JOIN pg_namespace n ON n.oid = type.typnamespace
            LEFT JOIN pg_class relation ON relation.oid = type.typrelid
            LEFT JOIN pg_type element ON element.oid = type.typelem
            LEFT JOIN pg_class element_relation ON element_relation.oid = element.typrelid
            WHERE n.nspname = @schema
            ORDER BY type.typname
            """,
            connection,
            transaction);
        typeCommand.Parameters.AddWithValue("schema", schemaName);
        await using var typeReader = await typeCommand.ExecuteReaderAsync(cancellationToken);
        var seenCompositeTypes = new HashSet<string>(StringComparer.Ordinal);
        var seenArrayTypes = new HashSet<string>(StringComparer.Ordinal);
        while (await typeReader.ReadAsync(cancellationToken))
        {
            var typeName = typeReader.GetString(0);
            var typeKind = typeReader.GetString(1);
            var category = typeReader.GetString(2);
            var typeRelationOid = typeReader.GetFieldValue<uint>(3);
            var relationName = typeReader.GetString(4);
            var relationKind = typeReader.GetString(5);
            var elementTypeName = typeReader.GetString(6);
            var elementRelationOid = typeReader.GetFieldValue<uint>(7);
            var elementRelationName = typeReader.GetString(8);
            var elementRelationKind = typeReader.GetString(9);

            var isExpectedComposite =
                ExpectedTables.Contains(typeName, StringComparer.Ordinal) &&
                string.Equals(typeKind, "c", StringComparison.Ordinal) &&
                string.Equals(category, "C", StringComparison.Ordinal) &&
                typeRelationOid != 0 &&
                string.Equals(relationName, typeName, StringComparison.Ordinal) &&
                string.Equals(relationKind, "r", StringComparison.Ordinal) &&
                seenCompositeTypes.Add(typeName);
            if (isExpectedComposite)
            {
                continue;
            }

            var isExpectedArray =
                ExpectedTables.Contains(elementTypeName, StringComparer.Ordinal) &&
                string.Equals(typeName, "_" + elementTypeName, StringComparison.Ordinal) &&
                string.Equals(typeKind, "b", StringComparison.Ordinal) &&
                string.Equals(category, "A", StringComparison.Ordinal) &&
                typeRelationOid == 0 &&
                elementRelationOid != 0 &&
                string.Equals(elementRelationName, elementTypeName, StringComparison.Ordinal) &&
                string.Equals(elementRelationKind, "r", StringComparison.Ordinal) &&
                seenArrayTypes.Add(elementTypeName);
            if (!isExpectedArray)
            {
                throw new PostgresSchemaIntegrityException(
                    $"The PostgreSQL schema contains an unregistered standalone type: {typeName}.");
            }
        }

        if (!seenCompositeTypes.SetEquals(ExpectedTables) || !seenArrayTypes.SetEquals(ExpectedTables))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL table composite-type manifest is incomplete or drifted.");
        }
    }

    private static async Task VerifyNoUnexpectedAuxiliaryObjectsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1 FROM pg_rewrite rewrite
                    JOIN pg_class relation ON relation.oid = rewrite.ev_class
                    JOIN pg_namespace n ON n.oid = relation.relnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_policy policy
                    JOIN pg_class relation ON relation.oid = policy.polrelid
                    JOIN pg_namespace n ON n.oid = relation.relnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_statistic_ext statistic
                    JOIN pg_namespace n ON n.oid = statistic.stxnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_collation object
                    JOIN pg_namespace n ON n.oid = object.collnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_conversion object
                    JOIN pg_namespace n ON n.oid = object.connamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_operator object
                    JOIN pg_namespace n ON n.oid = object.oprnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_opclass object
                    JOIN pg_namespace n ON n.oid = object.opcnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_opfamily object
                    JOIN pg_namespace n ON n.oid = object.opfnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_publication_rel publication
                    JOIN pg_class relation ON relation.oid = publication.prrelid
                    JOIN pg_namespace n ON n.oid = relation.relnamespace
                    WHERE n.nspname = @schema
                ),
                EXISTS (SELECT 1 FROM pg_publication publication WHERE publication.puballtables),
                EXISTS (
                    SELECT 1 FROM pg_publication_namespace publication
                    JOIN pg_namespace n ON n.oid = publication.pnnspid
                    WHERE n.nspname = @schema
                ),
                EXISTS (
                    SELECT 1 FROM pg_inherits inheritance
                    JOIN pg_class child ON child.oid = inheritance.inhrelid
                    JOIN pg_class parent ON parent.oid = inheritance.inhparent
                    JOIN pg_namespace child_namespace ON child_namespace.oid = child.relnamespace
                    JOIN pg_namespace parent_namespace ON parent_namespace.oid = parent.relnamespace
                    WHERE child_namespace.nspname = @schema
                       OR parent_namespace.nspname = @schema
                )
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            Enumerable.Range(0, 12).Any(reader.GetBoolean))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL schema has an unregistered rule, policy, publication, inheritance, statistic, collation, conversion, or operator object.");
        }
    }

    private static async Task VerifyTableAndColumnShapeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, c.relpersistence::text,
                   a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull,
                   COALESCE(pg_get_expr(ad.adbin, ad.adrelid), ''),
                   a.attidentity::text, a.attgenerated::text,
                   (a.attcollation = 0 OR a.attcollation = (
                       SELECT coll.oid FROM pg_collation coll
                       WHERE coll.collname = 'default'
                         AND coll.collnamespace = 'pg_catalog'::regnamespace
                   ))
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            LEFT JOIN pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'p')
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY c.relname, a.attnum
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actual = new Dictionary<string, List<ColumnExpectation>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (reader.GetBoolean(1) || reader.GetBoolean(2) ||
                !string.Equals(reader.GetString(3), "p", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(reader.GetString(8)) ||
                !string.IsNullOrEmpty(reader.GetString(9)) ||
                !reader.GetBoolean(10))
            {
                throw new PostgresSchemaIntegrityException(
                    $"PostgreSQL table or column options drifted for {table}.{reader.GetString(4)}.");
            }

            if (!actual.TryGetValue(table, out var columns))
            {
                columns = [];
                actual.Add(table, columns);
            }

            columns.Add(new ColumnExpectation(
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                NormalizeSql(reader.GetString(7))));
        }

        if (!actual.Keys.Order(StringComparer.Ordinal).SequenceEqual(ExpectedTables, StringComparer.Ordinal))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL schema has missing or unexpected base tables and cannot be adopted.");
        }

        foreach (var table in ExpectedTables)
        {
            if (!actual[table].SequenceEqual(ExpectedColumns[table]))
            {
                throw new PostgresSchemaIntegrityException(
                    $"The PostgreSQL column manifest drifted for {table}.");
            }
        }
    }

    private static async Task VerifyConstraintsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        // PostgreSQL 18 materialises every column NOT NULL as its own pg_constraint row
        // (contype 'n', auto-named <table>_<column>_not_null). This manifest predates that
        // and enumerated none of them, so every NOT NULL column read as an unexpected
        // constraint. They are verified below against the expected column list rather than
        // filtered out, because pg_attribute.attnotnull alone cannot stand in for them: a
        // NOT NULL constraint added NOT VALID over pre-existing NULL rows still sets
        // attnotnull while leaving convalidated false, so skipping these rows would let a
        // schema that actually contains NULLs pass attestation.
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, con.conname, con.contype::text, con.convalidated,
                   con.condeferrable, con.condeferred,
                   ARRAY(
                       SELECT a.attname
                       FROM unnest(con.conkey) WITH ORDINALITY key(attnum, position)
                       JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = key.attnum
                       ORDER BY key.position
                   )::text[],
                   COALESCE(ref.relname, ''),
                   ARRAY(
                       SELECT a.attname
                       FROM unnest(con.confkey) WITH ORDINALITY key(attnum, position)
                       JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = key.attnum
                       ORDER BY key.position
                   )::text[],
                   COALESCE(pg_get_expr(con.conbin, con.conrelid, false), ''),
                   con.conenforced, con.conislocal, con.coninhcount,
                   con.connoinherit, con.conperiod,
                   con.conparentid = 0, con.contypid = 0,
                   con.confupdtype::text, con.confdeltype::text,
                   con.confmatchtype::text, con.confdelsetcols IS NULL
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_class ref ON ref.oid = con.confrelid
            WHERE n.nspname = @schema
            ORDER BY c.relname, con.conname
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenNotNull = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0) + "." + reader.GetString(1);
            if (string.Equals(reader.GetString(2), "n", StringComparison.Ordinal))
            {
                // A column NOT NULL. It must name exactly one column that the column
                // manifest declares NOT NULL, carry PostgreSQL's own generated name, and
                // be both validated and enforced — an unvalidated or unenforced row means
                // the column tolerates NULLs regardless of what attnotnull reports.
                var table = reader.GetString(0);
                var columns = reader.GetFieldValue<string[]>(6);
                if (columns.Length != 1 ||
                    !ExpectedColumns.TryGetValue(table, out var tableColumns) ||
                    !tableColumns.Any(column =>
                        column.NotNull && string.Equals(column.Name, columns[0], StringComparison.Ordinal)) ||
                    !string.Equals(reader.GetString(1), $"{table}_{columns[0]}_not_null", StringComparison.Ordinal) ||
                    !seenNotNull.Add(table + "." + columns[0]) ||
                    !reader.GetBoolean(3) || reader.GetBoolean(4) || reader.GetBoolean(5) ||
                    !reader.GetBoolean(10) || !reader.GetBoolean(11) || reader.GetInt16(12) != 0 ||
                    reader.GetBoolean(13) || reader.GetBoolean(14) ||
                    !reader.GetBoolean(15) || !reader.GetBoolean(16))
                {
                    throw new PostgresSchemaIntegrityException(
                        $"The PostgreSQL constraint manifest drifted at {key}.");
                }

                continue;
            }

            if (!ExpectedConstraints.TryGetValue(key, out var expected) ||
                !seen.Add(key) ||
                !string.Equals(reader.GetString(2), expected.Type, StringComparison.Ordinal) ||
                !reader.GetBoolean(3) || reader.GetBoolean(4) || reader.GetBoolean(5) ||
                (expected.Type != "c" &&
                    !reader.GetFieldValue<string[]>(6).SequenceEqual(expected.Columns, StringComparer.Ordinal)) ||
                !string.Equals(reader.GetString(7), expected.ReferenceTable, StringComparison.Ordinal) ||
                !reader.GetFieldValue<string[]>(8).SequenceEqual(expected.ReferenceColumns, StringComparer.Ordinal) ||
                !reader.GetBoolean(10) || !reader.GetBoolean(11) || reader.GetInt16(12) != 0 ||
                // connoinherit is a deterministic function of the constraint type, not a
                // free choice: PostgreSQL marks PRIMARY KEY, UNIQUE and FOREIGN KEY
                // constraints non-inheritable (true) and CHECK constraints inheritable
                // (false). Requiring false unconditionally rejected every primary key,
                // so this verifier could never pass on a schema that has one. Pinning it
                // per type keeps the full tamper-evidence: a flip in either direction
                // still fails closed.
                reader.GetBoolean(13) != (expected.Type != "c") || reader.GetBoolean(14) ||
                !reader.GetBoolean(15) || !reader.GetBoolean(16) ||
                (expected.Type == "f" &&
                    (!string.Equals(reader.GetString(17), "a", StringComparison.Ordinal) ||
                     !string.Equals(reader.GetString(18), "a", StringComparison.Ordinal) ||
                     !string.Equals(reader.GetString(19), "s", StringComparison.Ordinal) ||
                     !reader.GetBoolean(20))))
            {
                throw new PostgresSchemaIntegrityException(
                    $"The PostgreSQL constraint manifest drifted at {key}.");
            }

            var expression = CanonicalizeConstraintExpression(reader.GetString(9));
            if (!string.Equals(expression, expected.CanonicalExpression, StringComparison.Ordinal))
            {
                throw new PostgresSchemaIntegrityException(
                    $"The PostgreSQL constraint expression drifted at {key}.");
            }
        }

        if (!seen.SetEquals(ExpectedConstraints.Keys))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL constraint set is incomplete or contains unknown constraints.");
        }

        // Every column the manifest declares NOT NULL must be backed by exactly one such
        // constraint. On PostgreSQL 18 a dropped constraint also clears attnotnull, so
        // VerifyTableAndColumnShapeAsync reaches this case first and this assertion is a
        // backstop rather than the primary guard — it is kept so the constraint manifest
        // is self-contained if that verification order ever changes.
        var expectedNotNull = ExpectedColumns
            .SelectMany(table => table.Value
                .Where(column => column.NotNull)
                .Select(column => table.Key + "." + column.Name))
            .ToHashSet(StringComparer.Ordinal);
        if (!seenNotNull.SetEquals(expectedNotNull))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL NOT NULL constraint set is incomplete or contains unknown columns.");
        }
    }

    private static async Task VerifyIndexesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT index_class.relname, table_class.relname, idx.indisunique,
                   idx.indisvalid, idx.indisready, idx.indisprimary,
                   idx.indisexclusion, idx.indimmediate, idx.indnullsnotdistinct,
                   idx.indisclustered, idx.indisreplident, idx.indislive,
                   idx.indnatts, idx.indnkeyatts,
                   idx.indpred IS NULL, idx.indexprs IS NULL,
                   index_class.relpersistence::text,
                   index_class.reltablespace = 0, index_class.reloptions IS NULL,
                   access_method.amname,
                   ARRAY(
                       SELECT pg_get_indexdef(index_class.oid, position, true)
                       FROM generate_series(1, idx.indnkeyatts) position
                       ORDER BY position
                   )::text[],
                   idx.indoption::int2[]
            FROM pg_index idx
            JOIN pg_class index_class ON index_class.oid = idx.indexrelid
            JOIN pg_class table_class ON table_class.oid = idx.indrelid
            JOIN pg_namespace n ON n.oid = table_class.relnamespace
            JOIN pg_am access_method ON access_method.oid = index_class.relam
            WHERE n.nspname = @schema
            ORDER BY index_class.relname
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!ExpectedIndexes.TryGetValue(name, out var expected))
            {
                throw new PostgresSchemaIntegrityException($"The PostgreSQL index manifest drifted at {name}.");
            }

            // A manifest fragment is written the way the DDL reads it ("created_at DESC"),
            // but pg_get_indexdef returns the bare column name in both pretty and raw
            // mode — it never emits the direction. Comparing the two directly could
            // therefore never match, so the direction is pinned where PostgreSQL actually
            // records it: pg_index.indoption, where bit 0 is DESC and bit 1 is NULLS
            // FIRST. A plain DESC key is 3 and a plain ascending key is 0, so this pins
            // the ordering strictly rather than dropping it from the manifest.
            var expectedFragments = expected.DefinitionFragments.Skip(1).ToArray();
            var expectedColumns = Array.ConvertAll(
                expectedFragments,
                fragment => fragment.EndsWith(" DESC", StringComparison.Ordinal)
                    ? fragment[..^5]
                    : fragment);
            var expectedOptions = Array.ConvertAll(
                expectedFragments,
                fragment => (short)(fragment.EndsWith(" DESC", StringComparison.Ordinal) ? 3 : 0));
            if (!seen.Add(name) ||
                !string.Equals(reader.GetString(1), expected.DefinitionFragments[0], StringComparison.Ordinal) ||
                reader.GetBoolean(2) != expected.Unique ||
                !reader.GetBoolean(3) || !reader.GetBoolean(4) ||
                reader.GetBoolean(5) != name.EndsWith("_pkey", StringComparison.Ordinal) ||
                reader.GetBoolean(6) || !reader.GetBoolean(7) || reader.GetBoolean(8) ||
                reader.GetBoolean(9) || reader.GetBoolean(10) || !reader.GetBoolean(11) ||
                reader.GetInt16(12) != expectedColumns.Length ||
                reader.GetInt16(13) != expectedColumns.Length ||
                !reader.GetBoolean(14) || !reader.GetBoolean(15) ||
                !string.Equals(reader.GetString(16), "p", StringComparison.Ordinal) ||
                !reader.GetBoolean(17) || !reader.GetBoolean(18) ||
                !string.Equals(reader.GetString(19), "btree", StringComparison.Ordinal) ||
                !reader.GetFieldValue<string[]>(20).SequenceEqual(expectedColumns, StringComparer.Ordinal) ||
                !reader.GetFieldValue<short[]>(21).SequenceEqual(expectedOptions))
            {
                throw new PostgresSchemaIntegrityException($"The PostgreSQL index manifest drifted at {name}.");
            }
        }

        if (!seen.SetEquals(ExpectedIndexes.Keys))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL index set is incomplete or contains unknown indexes.");
        }
    }

    private static async Task VerifyTriggersAndFunctionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using (var functionCommand = new NpgsqlCommand(
            """
            SELECT p.proname, language.lanname, p.prosecdef, p.provolatile::text,
                   p.prosrc, pg_get_userbyid(p.proowner), p.pronargs,
                   p.prorettype = 'trigger'::regtype, p.prokind::text,
                   p.proconfig IS NULL, NOT p.proleakproof,
                   p.proparallel::text = 'u', NOT p.proretset,
                   NOT p.proisstrict, p.prosupport = 0,
                   p.prosqlbody IS NULL
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_language language ON language.oid = p.prolang
            WHERE n.nspname = @schema
            ORDER BY p.proname
            """,
            connection,
            transaction))
        {
            functionCommand.Parameters.AddWithValue("schema", schemaName);
            await using var reader = await functionCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(reader.GetString(0), "reject_gbrain_projector_mutation", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(1), "plpgsql", StringComparison.Ordinal) ||
                reader.GetBoolean(2) ||
                !string.Equals(reader.GetString(3), "v", StringComparison.Ordinal) ||
                !string.Equals(
                    NormalizeSql(reader.GetString(4)),
                    "BEGIN RAISE EXCEPTION 'gbrain-projector identity and revision ledgers are append-only'; END;",
                    StringComparison.Ordinal) ||
                reader.GetInt16(6) != 0 || !reader.GetBoolean(7) ||
                !string.Equals(reader.GetString(8), "f", StringComparison.Ordinal) ||
                Enumerable.Range(9, 7).Any(index => !reader.GetBoolean(index)) ||
                await reader.ReadAsync(cancellationToken))
            {
                throw new PostgresSchemaIntegrityException(
                    "The PostgreSQL append-only trigger function manifest drifted.");
            }
        }

        await using var triggerCommand = new NpgsqlCommand(
            """
            SELECT table_class.relname, trigger.tgname, trigger.tgtype,
                   trigger.tgenabled::text, function.proname, function_namespace.nspname,
                   trigger.tgqual IS NULL, trigger.tgnargs = 0,
                   trigger.tgattr = ''::int2vector,
                   trigger.tgconstraint = 0,
                   NOT trigger.tgdeferrable, NOT trigger.tginitdeferred,
                   trigger.tgoldtable IS NULL, trigger.tgnewtable IS NULL
            FROM pg_trigger trigger
            JOIN pg_class table_class ON table_class.oid = trigger.tgrelid
            JOIN pg_namespace table_namespace ON table_namespace.oid = table_class.relnamespace
            JOIN pg_proc function ON function.oid = trigger.tgfoid
            JOIN pg_namespace function_namespace ON function_namespace.oid = function.pronamespace
            WHERE table_namespace.nspname = @schema
              AND NOT trigger.tgisinternal
            ORDER BY table_class.relname, trigger.tgname
            """,
            connection,
            transaction);
        triggerCommand.Parameters.AddWithValue("schema", schemaName);
        await using var triggerReader = await triggerCommand.ExecuteReaderAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await triggerReader.ReadAsync(cancellationToken))
        {
            var name = triggerReader.GetString(1);
            if (!ExpectedTriggers.TryGetValue(name, out var expected) ||
                !seen.Add(name) ||
                !string.Equals(triggerReader.GetString(0), expected.Table, StringComparison.Ordinal) ||
                triggerReader.GetInt16(2) != expected.TypeMask ||
                !string.Equals(triggerReader.GetString(3), "O", StringComparison.Ordinal) ||
                !string.Equals(triggerReader.GetString(4), "reject_gbrain_projector_mutation", StringComparison.Ordinal) ||
                !string.Equals(triggerReader.GetString(5), schemaName, StringComparison.Ordinal) ||
                Enumerable.Range(6, 8).Any(index => !triggerReader.GetBoolean(index)))
            {
                throw new PostgresSchemaIntegrityException($"The PostgreSQL trigger manifest drifted at {name}.");
            }
        }

        if (!seen.SetEquals(ExpectedTriggers.Keys))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL append-only trigger set is incomplete or contains unknown triggers.");
        }
    }

    private static async Task VerifyExactOwnersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schemaName,
        string migrationRole,
        string runtimeRole,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT pg_get_userbyid(n.nspowner) FROM pg_namespace n WHERE n.nspname = @schema),
                COALESCE((
                    SELECT bool_and(pg_get_userbyid(c.relowner) = @migration_role)
                    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @schema
                ), false),
                COALESCE((
                    SELECT bool_and(pg_get_userbyid(p.proowner) = @migration_role)
                    FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = @schema
                ), false)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("migration_role", migrationRole);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) ||
            !string.Equals(reader.GetString(0), migrationRole, StringComparison.Ordinal) ||
            !reader.GetBoolean(1) || !reader.GetBoolean(2))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL schema objects are not owned exclusively by the migration role.");
        }

        await reader.DisposeAsync();
        await VerifyRuntimeDoesNotOwnSchemaAsync(
            connection, transaction, schemaName, runtimeRole, cancellationToken);
    }

    private static async Task VerifyRuntimeDoesNotOwnSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string runtimeRole,
        CancellationToken cancellationToken,
        bool allowMissingSchema = false)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_namespace n
                JOIN pg_roles r ON r.oid = n.nspowner
                WHERE n.nspname = @schema AND r.rolname = @runtime_role
                UNION ALL
                SELECT 1 FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_roles r ON r.oid = c.relowner
                WHERE n.nspname = @schema AND r.rolname = @runtime_role
                UNION ALL
                SELECT 1 FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                JOIN pg_roles r ON r.oid = p.proowner
                WHERE n.nspname = @schema AND r.rolname = @runtime_role
            ), EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("runtime_role", runtimeRole);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetBoolean(0) ||
            (!allowMissingSchema && !reader.GetBoolean(1)))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL runtime role owns schema objects or the required schema is absent.");
        }
    }

    private static async Task VerifyConsistentSchemaOwnersAsync(
        NpgsqlConnection connection,
        string schemaName,
        string runtimeRole,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH schema_owner AS (
                SELECT n.nspowner AS owner_oid
                FROM pg_namespace n
                WHERE n.nspname = @schema
            )
            SELECT
                EXISTS (SELECT 1 FROM schema_owner),
                COALESCE((
                    SELECT bool_and(c.relowner = schema_owner.owner_oid)
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    CROSS JOIN schema_owner
                    WHERE n.nspname = @schema
                ), false),
                COALESCE((
                    SELECT bool_and(p.proowner = schema_owner.owner_oid)
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid = p.pronamespace
                    CROSS JOIN schema_owner
                    WHERE n.nspname = @schema
                ), false),
                COALESCE((
                    SELECT pg_get_userbyid(owner_oid) <> @runtime_role
                    FROM schema_owner
                ), false)
            """,
            connection);
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("runtime_role", runtimeRole);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            Enumerable.Range(0, 4).Any(index => !reader.GetBoolean(index)))
        {
            throw new PostgresSchemaIntegrityException(
                "The PostgreSQL schema, table, index, and function owners are inconsistent.");
        }
    }

    private static async Task VerifyExactRuntimePrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string runtimeRole,
        CancellationToken cancellationToken)
    {
        await using (var schemaCommand = new NpgsqlCommand(
            "SELECT has_schema_privilege(@role, @schema, 'USAGE'), has_schema_privilege(@role, @schema, 'CREATE')",
            connection,
            transaction))
        {
            schemaCommand.Parameters.AddWithValue("role", runtimeRole);
            schemaCommand.Parameters.AddWithValue("schema", schemaName);
            await using var reader = await schemaCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0) || reader.GetBoolean(1))
            {
                throw new PostgresSchemaIntegrityException(
                    "The PostgreSQL runtime schema privileges are not exactly USAGE without CREATE.");
            }
        }

        await using (var tableCommand = new NpgsqlCommand(
            """
            SELECT c.relname,
                   has_table_privilege(@role, c.oid, 'SELECT'),
                   has_table_privilege(@role, c.oid, 'INSERT'),
                   has_table_privilege(@role, c.oid, 'UPDATE'),
                   has_table_privilege(@role, c.oid, 'DELETE'),
                   has_table_privilege(@role, c.oid, 'TRUNCATE'),
                   has_table_privilege(@role, c.oid, 'REFERENCES'),
                   has_table_privilege(@role, c.oid, 'TRIGGER'),
                   has_table_privilege(@role, c.oid, 'MAINTAIN')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relkind IN ('r', 'p')
            ORDER BY c.relname
            """,
            connection,
            transaction))
        {
            tableCommand.Parameters.AddWithValue("role", runtimeRole);
            tableCommand.Parameters.AddWithValue("schema", schemaName);
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            var seen = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                seen.Add(reader.GetString(0));
                if (!reader.GetBoolean(1) || !reader.GetBoolean(2) ||
                    Enumerable.Range(3, 6).Any(reader.GetBoolean))
                {
                    throw new PostgresSchemaIntegrityException(
                        $"The PostgreSQL runtime table privileges are not exact for {reader.GetString(0)}.");
                }
            }

            if (!seen.SequenceEqual(ExpectedTables, StringComparer.Ordinal))
            {
                throw new PostgresSchemaIntegrityException("Runtime privileges do not cover the exact table set.");
            }
        }

        await VerifyColumnPrivilegesAsync(connection, transaction, schemaName, runtimeRole, cancellationToken);
        await VerifyFunctionAndAclPrivilegesAsync(connection, transaction, schemaName, runtimeRole, cancellationToken);
    }

    private static async Task VerifyColumnPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string runtimeRole,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, a.attname,
                   has_column_privilege(@role, c.oid, a.attnum, 'SELECT'),
                   has_column_privilege(@role, c.oid, a.attnum, 'INSERT'),
                   has_column_privilege(@role, c.oid, a.attnum, 'UPDATE'),
                   has_column_privilege(@role, c.oid, a.attnum, 'REFERENCES')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = @schema AND c.relkind IN ('r', 'p')
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY c.relname, a.attnum
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("role", runtimeRole);
        command.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.GetBoolean(2) || !reader.GetBoolean(3) ||
                reader.GetBoolean(4) || reader.GetBoolean(5))
            {
                throw new PostgresSchemaIntegrityException(
                    $"The PostgreSQL runtime column privileges are not exact for {reader.GetString(0)}.{reader.GetString(1)}.");
            }
        }
    }

    private static async Task VerifyFunctionAndAclPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string runtimeRole,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH grants AS (
                SELECT acl.grantee, acl.is_grantable, n.nspowner AS object_owner
                FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) acl
                WHERE n.nspname = @schema
                UNION ALL
                SELECT acl.grantee, acl.is_grantable, c.relowner AS object_owner
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                CROSS JOIN LATERAL aclexplode(c.relacl) acl
                WHERE n.nspname = @schema
                UNION ALL
                SELECT acl.grantee, acl.is_grantable, c.relowner AS object_owner
                FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                CROSS JOIN LATERAL aclexplode(a.attacl) acl
                WHERE n.nspname = @schema AND a.attnum > 0 AND NOT a.attisdropped
                UNION ALL
                SELECT acl.grantee, acl.is_grantable, p.proowner AS object_owner
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                CROSS JOIN LATERAL aclexplode(p.proacl) acl
                WHERE n.nspname = @schema
            )
            SELECT
                EXISTS (SELECT 1 FROM grants WHERE grantee = 0),
                EXISTS (
                    SELECT 1 FROM grants
                    WHERE grantee = (SELECT oid FROM pg_roles WHERE rolname = @role)
                      AND is_grantable
                ),
                EXISTS (
                    SELECT 1 FROM grants
                    WHERE grantee <> object_owner
                      AND grantee <> (SELECT oid FROM pg_roles WHERE rolname = @role)
                ),
                EXISTS (
                    SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = @schema
                      AND has_function_privilege(@role, p.oid, 'EXECUTE')
                ),
                EXISTS (
                    SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @schema AND c.relkind = 'S'
                )
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("role", runtimeRole);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2) ||
            reader.GetBoolean(3) || reader.GetBoolean(4))
        {
            throw new PostgresSchemaIntegrityException(
                "PUBLIC/runtime ACL, grant options, functions, or sequences violate least privilege.");
        }
    }

    private static IReadOnlyDictionary<string, ConstraintExpectation> BuildExpectedConstraints()
    {
        var values = new Dictionary<string, ConstraintExpectation>(StringComparer.Ordinal);

        Add("source_bindings", "source_bindings_pkey", "p", ["soul_id"]);
        Add("source_bindings", "source_bindings_source_id_key", "u", ["source_id"]);
        Add("source_bindings", "source_bindings_binding_revision_key", "u", ["binding_revision"]);
        Add("source_bindings", "source_bindings_binding_checksum_key", "u", ["binding_checksum"]);
        Add("source_bindings", "source_bindings_identity_proof_key", "u", ["soul_id", "source_id", "binding_revision", "binding_checksum"]);
        Check("source_bindings", "source_bindings_soul_id_format", "soul_id~'^soul_[a-f0-9]{64}$'");
        Check("source_bindings", "source_bindings_source_id_format", "source_id~'^dps-[a-f0-9]{28}$'");
        Check("source_bindings", "source_bindings_algorithm_fixed", "algorithm='dps.gbrain-source-binding.sha256-nonce/v1'");
        Check("source_bindings", "source_bindings_nonce_bounded", "nonce>=0andnonce<=1023");
        Check("source_bindings", "source_bindings_soul_hash_format", "soul_hash~'^[a-f0-9]{64}$'");
        Check("source_bindings", "source_bindings_soul_hash_matches", "soul_hash=substrsoul_id,6,64");
        Check("source_bindings", "source_bindings_binding_revision_format", "binding_revision~'^[a-f0-9]{64}$'");
        Check("source_bindings", "source_bindings_binding_checksum_format", "binding_checksum~'^[a-f0-9]{64}$'");
        Check("source_bindings", "source_bindings_canonical_present", "lengthcanonical_json>0");
        Check("source_bindings", "source_bindings_json_object", "jsonb_typeofbinding_json='object'");
        Check("source_bindings", "source_bindings_canonical_json_matches", "binding_json=canonical_json::jsonb");

        Add("source_binding_quarantine", "source_binding_quarantine_pkey", "p", ["quarantine_id"]);
        Check("source_binding_quarantine", "source_binding_quarantine_soul_id_format", "soul_id~'^soul_[a-f0-9]{64}$'");
        Check("source_binding_quarantine", "source_binding_quarantine_soul_hash_format", "soul_hash~'^[a-f0-9]{64}$'");
        Check("source_binding_quarantine", "source_binding_quarantine_nonce_bounded", "maximum_nonce>=0andmaximum_nonce<=1023");
        Check("source_binding_quarantine", "source_binding_quarantine_reason_bounded", "lengthreason>=1andlengthreason<=256");
        Check("source_binding_quarantine", "source_binding_quarantine_soul_hash_matches", "soul_hash=substrsoul_id,6,64");

        Add("rendered_revisions", "rendered_revisions_pkey", "p", ["soul_id", "projection_revision"]);
        Add("rendered_revisions", "rendered_revisions_source_revision_key", "u", ["source_id", "projection_revision"]);
        Add("rendered_revisions", "rendered_revisions_soul_checksum_key", "u", ["soul_id", "projection_checksum"]);
        Check("rendered_revisions", "rendered_revisions_projection_revision_format", "projection_revision~'^[a-f0-9]{64}$'");
        Check("rendered_revisions", "rendered_revisions_projection_checksum_format", "projection_checksum~'^[a-f0-9]{64}$'");
        Check("rendered_revisions", "rendered_revisions_source_event_count_nonnegative", "source_event_count>=0");
        Check("rendered_revisions", "rendered_revisions_canonical_present", "lengthcanonical_json>0");
        Check("rendered_revisions", "rendered_revisions_json_object", "jsonb_typeofprojection_json='object'");
        Check("rendered_revisions", "rendered_revisions_canonical_json_matches", "projection_json=canonical_json::jsonb");
        values.Add(
            "rendered_revisions.rendered_revisions_source_binding_fkey",
            new ConstraintExpectation(
                "f",
                ["soul_id", "source_id", "source_binding_revision", "source_binding_checksum"],
                "source_bindings",
                ["soul_id", "source_id", "binding_revision", "binding_checksum"],
                ""));

        return values;

        void Add(string table, string name, string type, string[] columns) =>
            values.Add(
                table + "." + name,
                new ConstraintExpectation(type, columns, "", [], ""));

        void Check(string table, string name, string expression) =>
            values.Add(
                table + "." + name,
                new ConstraintExpectation("c", [], "", [], expression));
    }

    private static string NormalizeSql(string value) =>
        Regex.Replace(value, "\\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string CanonicalizeConstraintExpression(string value)
    {
        var normalized = NormalizeSql(value).ToLowerInvariant();
        normalized = Regex.Replace(
            normalized,
            "::(?:text|bigint|integer)\\b",
            "",
            RegexOptions.CultureInvariant);
        return normalized
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("(", "", StringComparison.Ordinal)
            .Replace(")", "", StringComparison.Ordinal);
    }

    private sealed record ColumnExpectation(string Name, string Type, bool NotNull, string Default);
    private sealed record ConstraintExpectation(
        string Type,
        string[] Columns,
        string ReferenceTable,
        string[] ReferenceColumns,
        string CanonicalExpression);
    private sealed record IndexExpectation(bool Unique, string[] DefinitionFragments);
    private sealed record TriggerExpectation(string Table, short TypeMask);
}

public sealed class PostgresSchemaIntegrityException : InvalidOperationException
{
    public PostgresSchemaIntegrityException(string message) : base(message) { }
}

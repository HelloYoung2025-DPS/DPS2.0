using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Dps.AuditMetrics;

public sealed record AuditMetricsMigrationOptions(
    string MigrationConnectionString,
    string SchemaName,
    string RuntimeRoleName)
{
    private static readonly Regex SafeIdentifier = new(
        "^[a-z][a-z0-9_]{0,62}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MigrationConnectionString))
        {
            throw new ArgumentException(
                "A privileged migration connection string is required.",
                nameof(MigrationConnectionString));
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || !SafeIdentifier.IsMatch(SchemaName))
        {
            throw new ArgumentException("Migration schema name is not allowlisted.", nameof(SchemaName));
        }

        if (string.IsNullOrWhiteSpace(RuntimeRoleName) || !SafeIdentifier.IsMatch(RuntimeRoleName))
        {
            throw new ArgumentException("Runtime role name is not allowlisted.", nameof(RuntimeRoleName));
        }
    }
}

public sealed class PostgresAuditMetricsMigrator
{
    private static readonly string[] MigrationResourceSuffixes =
    [
        "001_create_audit_metrics.sql",
        "002_configure_audit_runtime_role.sql"
    ];

    private readonly AuditMetricsMigrationOptions _options;

    public PostgresAuditMetricsMigrator(AuditMetricsMigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.MigrationConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var suffix in MigrationResourceSuffixes)
        {
            var migration = await ReadMigrationAsync(suffix, cancellationToken);
            migration = migration
                .Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal)
                .Replace("__RUNTIME_ROLE__", _options.RuntimeRoleName, StringComparison.Ordinal);
            await using var command = new NpgsqlCommand(migration, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> ReadMigrationAsync(
        string suffix,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(PostgresAuditMetricsMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{suffix}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

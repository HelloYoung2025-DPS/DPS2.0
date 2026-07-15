using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.PlatformAccountRegistry.Contracts;
using Dps.PlatformAuthorizationAuthority.Contracts;
using Npgsql;

namespace Dps.PlatformAccountRegistry;

public sealed record PlatformAccountRegistryOptions(
    string ConnectionString,
    string SchemaName,
    string ActiveReleaseBomSha256,
    long ActiveReleaseGeneration,
    long TrustEpoch = 1)
{
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(ConnectionString));
        if (string.IsNullOrWhiteSpace(SchemaName) || !SchemaPattern.IsMatch(SchemaName))
            throw new ArgumentException("SchemaName must be a canonical PostgreSQL identifier.", nameof(SchemaName));
        AccountContractValidation.RequireSha256(ActiveReleaseBomSha256, nameof(ActiveReleaseBomSha256));
        if (ActiveReleaseGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ActiveReleaseGeneration));
        if (TrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(TrustEpoch));
    }

    public override string ToString()
        => $"PlatformAccountRegistryOptions {{ ConnectionString = [REDACTED], SchemaName = {SchemaName}, ActiveReleaseBomSha256 = {ActiveReleaseBomSha256}, ActiveReleaseGeneration = {ActiveReleaseGeneration}, TrustEpoch = {TrustEpoch} }}";
}

public enum PlatformAccountMutationStage
{
    AccountPersisted,
    RevisionPersisted,
    ReceiptPersisted,
    OutboxPersistedBeforeCommit
}

public delegate ValueTask PlatformAccountRegistryFaultInjector(
    PlatformAccountMutationStage stage,
    CancellationToken cancellationToken);

public sealed class PlatformAccountIdempotencyConflictException : InvalidOperationException
{
    public PlatformAccountIdempotencyConflictException()
        : base("The idempotency key is bound to a different platform-account mutation.") { }
}

public sealed class PlatformAccountAliasConflictException : InvalidOperationException
{
    public PlatformAccountAliasConflictException()
        : base("The verified platform alias is already registered.") { }
}

public sealed class PlatformAccountRevisionConflictException : InvalidOperationException
{
    public PlatformAccountRevisionConflictException()
        : base("The platform-account authorization revision is stale.") { }
}

public sealed partial class PostgresPlatformAccountRegistry
{
    private static readonly Regex PlatformPattern = new(
        "^[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex AliasKeyIdPattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly PlatformAccountRegistryOptions _options;
    private readonly PlatformAccountRegistryFaultInjector? _faultInjector;
    private readonly string _bindingProviderInstanceConfigurationSha256;
    private readonly PlatformAuthorizationEvidenceVerifier _evidenceVerifier;
    private int _initialized;

    public PostgresPlatformAccountRegistry(
        PlatformAccountRegistryOptions options,
        PlatformAccountRegistryFaultInjector? faultInjector = null)
        : this(
            options,
            PlatformAuthorizationEvidenceVerifier.CreatePinned(
                options.ActiveReleaseBomSha256,
                options.ActiveReleaseGeneration),
            faultInjector)
    {
    }

    internal PostgresPlatformAccountRegistry(
        PlatformAccountRegistryOptions options,
        PlatformAuthorizationEvidenceVerifier evidenceVerifier,
        PlatformAccountRegistryFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evidenceVerifier);
        options.Validate();
        _options = options;
        _evidenceVerifier = evidenceVerifier;
        _faultInjector = faultInjector;
        _bindingProviderInstanceConfigurationSha256 = PlatformAccountProviderInstanceIdentity.Compute(options);
    }

    internal string BindingProviderInstanceConfigurationSha256 => _bindingProviderInstanceConfigurationSha256;
    internal long BindingProviderInstanceTrustEpoch => _options.TrustEpoch;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ApplyMigrationsAsync(_options, cancellationToken);
        await RecordActiveReleaseGenerationAsync(_options, cancellationToken);
        Volatile.Write(ref _initialized, 1);
    }

    private static async Task ApplyMigrationsAsync(
        PlatformAccountRegistryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var assembly = typeof(PostgresPlatformAccountRegistry).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(static name => name.Contains(".Migrations.", StringComparison.Ordinal) &&
                                  name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resourceNames.Length == 0)
            throw new InvalidOperationException("No embedded platform-account migrations were found.");

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var bootstrap = new NpgsqlCommand(
            $"""
            CREATE SCHEMA IF NOT EXISTS {options.SchemaName};
            CREATE TABLE IF NOT EXISTS {options.SchemaName}.module_schema_migrations (
                migration_id text PRIMARY KEY,
                content_sha256 char(64) NOT NULL CHECK (length(content_sha256) = 64 AND content_sha256 !~ '[^a-f0-9]'),
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            REVOKE ALL ON TABLE {options.SchemaName}.module_schema_migrations FROM PUBLIC;
            """,
            connection))
        {
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var resourceName in resourceNames)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            var template = await reader.ReadToEndAsync(cancellationToken);
            var contentSha256 = ComputeSha256(template);
            var marker = ".Migrations.";
            var migrationId = resourceName[(resourceName.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireLockAsync(connection, transaction, "platform-account:migrations", cancellationToken);
            await using (var read = new NpgsqlCommand(
                $"SELECT content_sha256 FROM {options.SchemaName}.module_schema_migrations WHERE migration_id = @migration_id FOR UPDATE",
                connection,
                transaction))
            {
                read.Parameters.AddWithValue("migration_id", migrationId);
                var existing = await read.ExecuteScalarAsync(cancellationToken) as string;
                if (existing is not null)
                {
                    if (!string.Equals(existing, contentSha256, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Applied platform-account migration '{migrationId}' no longer matches its embedded SHA-256.");
                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }
            }

            var migration = template.Replace("__SCHEMA__", options.SchemaName, StringComparison.Ordinal);
            await using (var command = new NpgsqlCommand(migration, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var record = new NpgsqlCommand(
                $"INSERT INTO {options.SchemaName}.module_schema_migrations (migration_id, content_sha256) VALUES (@migration_id, @content_sha256)",
                connection,
                transaction))
            {
                record.Parameters.AddWithValue("migration_id", migrationId);
                record.Parameters.AddWithValue("content_sha256", contentSha256);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task RecordActiveReleaseGenerationAsync(
        PlatformAccountRegistryOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireLockAsync(connection, transaction, "platform-account:release-generation", cancellationToken);
        long? currentGeneration = null;
        string? currentBomSha256 = null;
        await using (var read = new NpgsqlCommand(
            $"SELECT highest_generation, release_bom_sha256 FROM {options.SchemaName}.release_generation_state WHERE scope = 'platform-account-production' FOR UPDATE",
            connection,
            transaction))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                currentGeneration = reader.GetInt64(0);
                currentBomSha256 = reader.GetString(1);
            }
        }

        EnsureReleaseGenerationTransition(
            currentGeneration,
            currentBomSha256,
            options.ActiveReleaseGeneration,
            options.ActiveReleaseBomSha256);
        await using var write = currentGeneration is null
            ? new NpgsqlCommand(
                $"INSERT INTO {options.SchemaName}.release_generation_state (scope, highest_generation, release_bom_sha256) VALUES ('platform-account-production', @generation, @bom)",
                connection,
                transaction)
            : new NpgsqlCommand(
                $"UPDATE {options.SchemaName}.release_generation_state SET highest_generation = @generation, release_bom_sha256 = @bom, updated_at = clock_timestamp() WHERE scope = 'platform-account-production'",
                connection,
                transaction);
        write.Parameters.AddWithValue("generation", options.ActiveReleaseGeneration);
        write.Parameters.AddWithValue("bom", options.ActiveReleaseBomSha256);
        if (await write.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The platform-account release generation fence was not persisted.");
        await transaction.CommitAsync(cancellationToken);
    }

    internal static void EnsureReleaseGenerationTransition(
        long? currentGeneration,
        string? currentBomSha256,
        long incomingGeneration,
        string incomingBomSha256)
    {
        if (incomingGeneration < 1) throw new ArgumentOutOfRangeException(nameof(incomingGeneration));
        AccountContractValidation.RequireSha256(incomingBomSha256, nameof(incomingBomSha256));
        if (currentGeneration is null) return;
        if (incomingGeneration < currentGeneration.Value)
            throw new UnauthorizedAccessException("A lower platform-account Release BOM generation cannot be replayed.");
        if (incomingGeneration == currentGeneration.Value &&
            !string.Equals(currentBomSha256, incomingBomSha256, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("One platform-account Release BOM generation cannot identify two BOMs.");
    }

    public async Task<PlatformAccountAuthorizedV1> AuthorizeAsync(
        AuthorizePlatformAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateAuthorize(command);
        _evidenceVerifier.VerifyAuthorizeScope(command.AuthorizationEvidence, command);
        EnsureInitialized();
        var platform = command.Platform.ToLowerInvariant();
        var aliasDigest = command.AliasDigest.ToLowerInvariant();
        var requestHash = ComputeRequestHash(
            "authorize",
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            platform,
            aliasDigest,
            command.AliasKeyId,
            command.AliasKeyEpoch.ToString(CultureInfo.InvariantCulture),
            PlatformAuthorizationEvidenceVerifier.ComputeEvidenceSha256(command.AuthorizationEvidence),
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt.ToString("O", CultureInfo.InvariantCulture));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, $"platform-account:idempotency:{command.IdempotencyKey}", cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            EnsureSameRequest(receipt, "authorize", requestHash);
            await transaction.CommitAsync(cancellationToken);
            return receipt.Result;
        }
        using var evidenceDeadline = _evidenceVerifier.CreateFreshnessDeadline(
            command.AuthorizationEvidence,
            cancellationToken);
        var mutationToken = evidenceDeadline.Token;

        await AcquireLockAsync(
            connection,
            transaction,
            $"platform-account:alias:{platform}:{command.AliasKeyId}:{command.AliasKeyEpoch}:{aliasDigest}",
            mutationToken);
        await AcquireLockAsync(connection, transaction, $"platform-account:id:{command.PlatformAccountId}", mutationToken);
        if (await AliasExistsAsync(connection, transaction, platform, command.AliasKeyId,
                command.AliasKeyEpoch, aliasDigest, mutationToken))
            throw new PlatformAccountAliasConflictException();
        if (await AccountExistsAsync(connection, transaction, command.PlatformAccountId, mutationToken))
            throw new PlatformAccountAliasConflictException();

        var result = CreateContract(
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            platform,
            aliasDigest,
            command.AliasKeyId,
            command.AliasKeyEpoch,
            command.AuthorizationEvidence.AuthorizationEvidenceId,
            1,
            "authorized",
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt);
        await InsertAccountAsync(connection, transaction, result, command.AuthorizationEvidence, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.AccountPersisted, mutationToken);
        await InsertRevisionAsync(connection, transaction, result, command.AuthorizationEvidence, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.RevisionPersisted, mutationToken);
        await InsertReceiptAsync(connection, transaction, "authorize", requestHash, result, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.ReceiptPersisted, mutationToken);
        await InsertOutboxAsync(connection, transaction, result, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.OutboxPersistedBeforeCommit, mutationToken);
        _evidenceVerifier.EnsureFresh(command.AuthorizationEvidence);
        await transaction.CommitAsync(mutationToken);
        return result;
    }

    public async Task<PlatformAccountAuthorizedV1> ChangeStatusAsync(
        ChangePlatformAccountStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateStatus(command);
        _evidenceVerifier.VerifySignatureAndIssuer(command.AuthorizationEvidence);
        EnsureInitialized();
        var requestHash = ComputeRequestHash(
            "status",
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
            command.Status,
            PlatformAuthorizationEvidenceVerifier.ComputeEvidenceSha256(command.AuthorizationEvidence),
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt.ToString("O", CultureInfo.InvariantCulture));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, $"platform-account:idempotency:{command.IdempotencyKey}", cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            EnsureSameRequest(receipt, "status", requestHash);
            await transaction.CommitAsync(cancellationToken);
            return receipt.Result;
        }

        await AcquireLockAsync(connection, transaction, $"platform-account:id:{command.PlatformAccountId}", cancellationToken);
        var current = await ReadAccountAsync(connection, transaction, command.PlatformAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown platform account.");
        EnsureScope(current, command.SoulId, command.DeviceBindingId);
        if (current.Status == "revoked")
            throw new InvalidOperationException("A revoked platform account cannot be reactivated or mutated.");
        if (current.AuthorizationRevision != command.ExpectedRevision)
            throw new PlatformAccountRevisionConflictException();
        _evidenceVerifier.VerifyStatusScope(command.AuthorizationEvidence, command, current);
        using var evidenceDeadline = _evidenceVerifier.CreateFreshnessDeadline(
            command.AuthorizationEvidence,
            cancellationToken);
        var mutationToken = evidenceDeadline.Token;
        await EnsureNoEffectiveBindingReservationAsync(
            connection,
            transaction,
            current.PlatformAccountId,
            mutationToken);

        var result = CreateContract(
            current.SoulId,
            current.DeviceBindingId,
            current.PlatformAccountId,
            current.Platform,
            current.AliasDigest,
            current.AliasKeyId,
            current.AliasKeyEpoch,
            command.AuthorizationEvidence.AuthorizationEvidenceId,
            current.AuthorizationRevision + 1,
            command.Status,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt);
        await UpdateAccountAsync(connection, transaction, result, command.AuthorizationEvidence,
            command.ExpectedRevision, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.AccountPersisted, mutationToken);
        await InsertRevisionAsync(connection, transaction, result, command.AuthorizationEvidence, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.RevisionPersisted, mutationToken);
        await InsertReceiptAsync(connection, transaction, "status", requestHash, result, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.ReceiptPersisted, mutationToken);
        await InsertOutboxAsync(connection, transaction, result, mutationToken);
        await InjectAsync(PlatformAccountMutationStage.OutboxPersistedBeforeCommit, mutationToken);
        _evidenceVerifier.EnsureFresh(command.AuthorizationEvidence);
        await transaction.CommitAsync(mutationToken);
        return result;
    }

    public async Task<PlatformAccountAuthorizedV1> GetAsync(
        string platformAccountId,
        string soulId,
        string deviceBindingId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(soulId, deviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(platformAccountId);
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var result = await ReadAccountAsync(connection, null, platformAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown platform account.");
        EnsureScope(result, soulId, deviceBindingId);
        return result;
    }

    public async Task<bool> IsAuthorizedAsync(
        string platformAccountId,
        string soulId,
        string deviceBindingId,
        CancellationToken cancellationToken = default) =>
        (await GetAsync(platformAccountId, soulId, deviceBindingId, cancellationToken)).Status == "authorized";

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 1)
            throw new InvalidOperationException(
                "The platform-account registry must complete migration and Release BOM generation fencing before use.");
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lockName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_name, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_name", lockName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> AliasExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string platform,
        string aliasKeyId,
        long aliasKeyEpoch,
        string aliasDigest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM {_options.SchemaName}.accounts WHERE platform = @platform AND alias_key_id = @alias_key_id AND alias_key_epoch = @alias_key_epoch AND alias_digest = @alias_digest",
            connection,
            transaction);
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("alias_key_id", aliasKeyId);
        command.Parameters.AddWithValue("alias_key_epoch", aliasKeyEpoch);
        command.Parameters.AddWithValue("alias_digest", aliasDigest);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<bool> AccountExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string platformAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM {_options.SchemaName}.accounts WHERE platform_account_id = @platform_account_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<ReceiptRow?> ReadReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT operation, request_sha256, result_json FROM {_options.SchemaName}.mutation_receipts WHERE idempotency_key = @idempotency_key",
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = PlatformAccountContractJson.DeserializeStrict<PlatformAccountAuthorizedV1>(reader.GetString(2));
        return new ReceiptRow(reader.GetString(0), reader.GetString(1), result);
    }

    private async Task<PlatformAccountAuthorizedV1?> ReadAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string platformAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT soul_id, device_binding_id, platform_account_id, trace_id, idempotency_key,
                   occurred_at, platform, alias_digest, alias_key_id, alias_key_epoch, authorization_evidence_id,
                   authorization_revision, status
            FROM {_options.SchemaName}.accounts
            WHERE platform_account_id = @platform_account_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return CreateContract(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetInt64(11),
            reader.GetString(12),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime());
    }

    private async Task InsertAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlatformAccountAuthorizedV1 result,
        SignedPlatformAuthorizationEvidenceV1 evidence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.accounts
                (platform_account_id, soul_id, device_binding_id, platform, alias_digest, alias_key_id,
                 alias_key_epoch, authorization_evidence_id, authorization_evidence_sha256,
                 authorization_evidence_json, authorization_revision, status, trace_id, idempotency_key,
                 occurred_at, updated_at)
            VALUES
                (@platform_account_id, @soul_id, @device_binding_id, @platform, @alias_digest, @alias_key_id,
                 @alias_key_epoch, @authorization_evidence_id, @authorization_evidence_sha256,
                 @authorization_evidence_json, @authorization_revision, @status, @trace_id, @idempotency_key,
                 @occurred_at, @occurred_at)
            """,
            connection,
            transaction);
        AddContractParameters(command, result);
        AddEvidenceParameters(command, evidence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlatformAccountAuthorizedV1 result,
        SignedPlatformAuthorizationEvidenceV1 evidence,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_options.SchemaName}.accounts
            SET authorization_evidence_id = @authorization_evidence_id,
                authorization_evidence_sha256 = @authorization_evidence_sha256,
                authorization_evidence_json = @authorization_evidence_json,
                authorization_revision = @authorization_revision,
                status = @status,
                trace_id = @trace_id,
                idempotency_key = @idempotency_key,
                occurred_at = @occurred_at,
                updated_at = clock_timestamp()
            WHERE platform_account_id = @platform_account_id
              AND soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND authorization_revision = @expected_revision
            """,
            connection,
            transaction);
        AddContractParameters(command, result);
        AddEvidenceParameters(command, evidence);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new PlatformAccountRevisionConflictException();
    }

    private async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlatformAccountAuthorizedV1 result,
        SignedPlatformAuthorizationEvidenceV1 evidence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.authorization_revisions
                (platform_account_id, authorization_revision, soul_id, device_binding_id, status,
                 authorization_evidence_id, authorization_evidence_sha256, authorization_evidence_json,
                 trace_id, idempotency_key, occurred_at)
            VALUES
                (@platform_account_id, @authorization_revision, @soul_id, @device_binding_id, @status,
                 @authorization_evidence_id, @authorization_evidence_sha256, @authorization_evidence_json,
                 @trace_id, @idempotency_key, @occurred_at)
            """,
            connection,
            transaction);
        AddContractParameters(command, result);
        AddEvidenceParameters(command, evidence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string operation,
        string requestHash,
        PlatformAccountAuthorizedV1 result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.mutation_receipts
                (idempotency_key, operation, request_sha256, platform_account_id,
                 authorization_revision, result_json)
            VALUES
                (@idempotency_key, @operation, @request_sha256, @platform_account_id,
                 @authorization_revision, @result_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("request_sha256", requestHash);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("authorization_revision", result.AuthorizationRevision);
        command.Parameters.AddWithValue("result_json", JsonSerializer.Serialize(result));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlatformAccountAuthorizedV1 result,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(result);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.outbox
                (outbox_id, idempotency_key, platform_account_id, authorization_revision, soul_id,
                 device_binding_id, trace_id, topic, payload_sha256, payload_json)
            VALUES
                (@outbox_id, @idempotency_key, @platform_account_id, @authorization_revision, @soul_id,
                 @device_binding_id, @trace_id, @topic, @payload_sha256, @payload_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("outbox_id", Guid.NewGuid());
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("authorization_revision", result.AuthorizationRevision);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("topic", result.ContractId);
        command.Parameters.AddWithValue("payload_sha256", ComputeSha256(payload));
        command.Parameters.AddWithValue("payload_json", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddContractParameters(NpgsqlCommand command, PlatformAccountAuthorizedV1 result)
    {
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("platform", result.Platform);
        command.Parameters.AddWithValue("alias_digest", result.AliasDigest);
        command.Parameters.AddWithValue("alias_key_id", result.AliasKeyId);
        command.Parameters.AddWithValue("alias_key_epoch", result.AliasKeyEpoch);
        command.Parameters.AddWithValue("authorization_evidence_id", result.AuthorizationEvidenceId);
        command.Parameters.AddWithValue("authorization_revision", result.AuthorizationRevision);
        command.Parameters.AddWithValue("status", result.Status);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", result.OccurredAt);
    }

    private static void AddEvidenceParameters(
        NpgsqlCommand command,
        SignedPlatformAuthorizationEvidenceV1 evidence)
    {
        command.Parameters.AddWithValue(
            "authorization_evidence_sha256",
            PlatformAuthorizationEvidenceVerifier.ComputeEvidenceSha256(evidence));
        command.Parameters.AddWithValue("authorization_evidence_json", JsonSerializer.Serialize(evidence));
    }

    private async ValueTask InjectAsync(PlatformAccountMutationStage stage, CancellationToken cancellationToken)
    {
        if (_faultInjector is not null) await _faultInjector(stage, cancellationToken);
    }

    private static void EnsureSameRequest(ReceiptRow receipt, string operation, string requestHash)
    {
        if (!string.Equals(receipt.Operation, operation, StringComparison.Ordinal) ||
            !FixedTimeHexEquals(receipt.RequestHash, requestHash))
            throw new PlatformAccountIdempotencyConflictException();
    }

    private static void EnsureScope(PlatformAccountAuthorizedV1 result, string soulId, string deviceBindingId)
    {
        if (!string.Equals(result.SoulId, soulId, StringComparison.Ordinal) ||
            !string.Equals(result.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Platform account scope mismatch.");
    }

    private static PlatformAccountAuthorizedV1 CreateContract(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string platform,
        string aliasDigest,
        string aliasKeyId,
        long aliasKeyEpoch,
        string authorizationEvidenceId,
        long revision,
        string status,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        var result = new PlatformAccountAuthorizedV1(
            PlatformAccountAuthorizedV1.CurrentSchemaVersion,
            PlatformAccountAuthorizedV1.CurrentContractId,
            PlatformAccountAuthorizedV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            "sensitive",
            platform,
            aliasDigest,
            aliasKeyId,
            authorizationEvidenceId,
            revision,
            status,
            aliasKeyEpoch);
        result.Validate();
        return result;
    }

    private static void ValidateAuthorize(AuthorizePlatformAccountCommand command)
    {
        ValidateScope(command.SoulId, command.DeviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(command.PlatformAccountId);
        AccountContractValidation.RequireIdentifier(command.Platform, nameof(command.Platform));
        if (!PlatformPattern.IsMatch(command.Platform))
            throw new ArgumentException("Platform must match the public ASCII identifier contract.", nameof(command.Platform));
        AccountContractValidation.RequireSha256(command.AliasDigest, nameof(command.AliasDigest));
        AccountContractValidation.RequireKeyId(command.AliasKeyId, nameof(command.AliasKeyId));
        if (!AliasKeyIdPattern.IsMatch(command.AliasKeyId))
            throw new ArgumentException("AliasKeyId must match the public ASCII key identifier contract.", nameof(command.AliasKeyId));
        if (command.AliasKeyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(command.AliasKeyEpoch));
        ArgumentNullException.ThrowIfNull(command.AuthorizationEvidence);
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
    }

    private static void ValidateStatus(ChangePlatformAccountStatusCommand command)
    {
        ValidateScope(command.SoulId, command.DeviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(command.PlatformAccountId);
        if (command.ExpectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(command.ExpectedRevision));
        if (command.Status is not ("authorized" or "suspended" or "revoked"))
            throw new ArgumentOutOfRangeException(nameof(command.Status));
        ArgumentNullException.ThrowIfNull(command.AuthorizationEvidence);
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
    }

    private static void ValidateScope(string soulId, string deviceBindingId)
    {
        AccountContractValidation.RequireSoulId(soulId);
        AccountContractValidation.RequireDeviceBindingId(deviceBindingId);
    }

    private static void ValidateEnvelope(string traceId, string idempotencyKey, DateTimeOffset occurredAt)
    {
        AccountContractValidation.RequireTraceId(traceId);
        AccountContractValidation.RequireIdempotencyKey(idempotencyKey);
        AccountContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }

    private static string ComputeRequestHash(params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, "dps.platform-account-registry.request/v1");
        foreach (var field in fields) AppendHashField(hash, field);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeHexEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64) return false;
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record ReceiptRow(
        string Operation,
        string RequestHash,
        PlatformAccountAuthorizedV1 Result);
}

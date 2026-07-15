using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.SoulRegistry.Contracts;
using Npgsql;

namespace Dps.SoulRegistry;

public sealed class PostgresSoulRegistry : IDisposable
{
    private const string MigrationResourceSuffix = "001_create_soul_registry.sql";
    private readonly SoulRegistryOptions _options;
    private readonly AliasDigester _digester;
    private readonly SoulRegistryFaultInjector? _faultInjector;

    public PostgresSoulRegistry(
        SoulRegistryOptions options,
        SoulRegistryFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _digester = new AliasDigester(options);
        _faultInjector = faultInjector;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(PostgresSoulRegistry).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(MigrationResourceSuffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The Soul Registry migration resource was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var migration = await reader.ReadToEndAsync(cancellationToken);
        migration = migration.Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(migration, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SoulResolved> RegisterVerifiedAliasAsync(
        RegisterVerifiedAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRegistration(request);
        var references = _digester.AllReferences(request.TenantId, request.AliasKind, request.RawAlias);
        var currentReference = _digester.CurrentReference(request.TenantId, request.AliasKind, request.RawAlias);
        var requestHash = ComputeRequestHash(
            "register",
            request.TenantId,
            references,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            request.TargetSoulId,
            request.Verification);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, $"idempotency:{request.TenantId}:{request.IdempotencyKey}", cancellationToken);

        var receipt = await ReadReceiptAsync(connection, transaction, request.TenantId, request.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            EnsureSameRequest(receipt, requestHash, "register");
            await transaction.CommitAsync(cancellationToken);
            return receipt.ToContract();
        }

        await AcquireAliasLocksAsync(connection, transaction, request.TenantId, references, cancellationToken);
        var matches = await ReadAliasMatchesAsync(connection, transaction, request.TenantId, references, cancellationToken);
        var activeMatches = matches.Where(static match => match.RevokedAt is null).ToArray();
        var distinctActiveSouls = activeMatches.Select(static match => match.SoulId).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctActiveSouls.Length > 1)
        {
            throw new AmbiguousAliasException();
        }

        if (activeMatches.Length == 0 && matches.Count != 0)
        {
            throw new AliasRevokedException();
        }

        string soulId;
        AliasReference resolvedReference;
        if (activeMatches.Length != 0)
        {
            var match = activeMatches[0];
            soulId = match.SoulId;
            resolvedReference = new AliasReference(match.AliasKind, match.AliasDigest, match.AliasKeyId);
            if (request.TargetSoulId is not null && !string.Equals(request.TargetSoulId, soulId, StringComparison.Ordinal))
            {
                throw new AliasConflictException();
            }
        }
        else
        {
            soulId = request.TargetSoulId ?? CreateSoulId();
            await EnsureSoulAsync(connection, transaction, request.TenantId, soulId, request.OccurredAt, request.TargetSoulId is not null, cancellationToken);
            await InsertAliasAsync(connection, transaction, request, soulId, currentReference, cancellationToken);
            resolvedReference = currentReference;
        }

        await EnsureSoulTenantAsync(connection, transaction, request.TenantId, soulId, cancellationToken);
        var result = CreateContract(
            soulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            resolvedReference);
        await InsertReceiptAsync(connection, transaction, request.TenantId, "register", requestHash, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<SoulResolved> ResolveAsync(
        ResolveSoulRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateResolve(request);
        var references = _digester.AllReferences(request.TenantId, request.AliasKind, request.RawAlias);
        var requestHash = ComputeRequestHash(
            "resolve",
            request.TenantId,
            references,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            null,
            request.Verification);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, $"idempotency:{request.TenantId}:{request.IdempotencyKey}", cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, request.TenantId, request.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            EnsureSameRequest(receipt, requestHash, "resolve");
            await transaction.CommitAsync(cancellationToken);
            return receipt.ToContract();
        }

        await AcquireAliasLocksAsync(connection, transaction, request.TenantId, references, cancellationToken);
        var matches = await ReadAliasMatchesAsync(connection, transaction, request.TenantId, references, cancellationToken);
        var active = matches.Where(static match => match.RevokedAt is null).ToArray();
        var distinctSouls = active.Select(static match => match.SoulId).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctSouls.Length > 1)
        {
            throw new AmbiguousAliasException();
        }

        if (active.Length == 0)
        {
            if (matches.Count != 0)
            {
                throw new AliasRevokedException();
            }

            throw new AliasNotFoundException();
        }

        var selected = active
            .OrderByDescending(match => string.Equals(match.AliasKeyId, _options.CurrentKeyId, StringComparison.Ordinal))
            .ThenBy(static match => match.AliasKeyId, StringComparer.Ordinal)
            .First();
        await EnsureSoulTenantAsync(connection, transaction, request.TenantId, selected.SoulId, cancellationToken);
        var result = CreateContract(
            selected.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            new AliasReference(selected.AliasKind, selected.AliasDigest, selected.AliasKeyId));
        await InsertReceiptAsync(connection, transaction, request.TenantId, "resolve", requestHash, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task RevokeAliasAsync(
        RevokeAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRevoke(request);
        var references = _digester.AllReferences(request.TenantId, request.AliasKind, request.RawAlias);
        var requestHash = ComputeMutationHash(request, references);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, $"idempotency:{request.TenantId}:{request.IdempotencyKey}", cancellationToken);
        var existingReceipt = await ReadMutationReceiptAsync(
            connection,
            transaction,
            request.TenantId,
            request.IdempotencyKey,
            cancellationToken);
        if (existingReceipt is not null)
        {
            if (!string.Equals(existingReceipt.Operation, "revoke", StringComparison.Ordinal) ||
                !string.Equals(existingReceipt.EntityId, request.ExpectedSoulId, StringComparison.Ordinal) ||
                !SecretComparison.EqualsHex(existingReceipt.RequestHash, requestHash))
            {
                throw new IdempotencyConflictException();
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await AcquireAliasLocksAsync(connection, transaction, request.TenantId, references, cancellationToken);
        var matches = await ReadAliasMatchesAsync(connection, transaction, request.TenantId, references, cancellationToken);
        var active = matches.Where(static match => match.RevokedAt is null).ToArray();
        var distinctSouls = active.Select(static match => match.SoulId).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctSouls.Length > 1)
        {
            throw new AmbiguousAliasException();
        }

        if (active.Length == 0)
        {
            if (matches.Count != 0)
            {
                throw new AliasRevokedException();
            }

            throw new AliasNotFoundException();
        }

        if (!string.Equals(distinctSouls[0], request.ExpectedSoulId, StringComparison.Ordinal))
        {
            throw new AliasConflictException();
        }

        var reasonDigest = ComputeSha256(request.Reason.Normalize(NormalizationForm.FormKC).Trim());
        foreach (var match in active)
        {
            await using var command = new NpgsqlCommand(
                $"""
                UPDATE {_options.SchemaName}.identity_aliases
                SET revoked_at = @revoked_at, revocation_reason_sha256 = @reason_digest
                WHERE alias_id = @alias_id AND revoked_at IS NULL
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("revoked_at", request.OccurredAt);
            command.Parameters.AddWithValue("reason_digest", reasonDigest);
            command.Parameters.AddWithValue("alias_id", match.AliasId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertMutationReceiptAsync(connection, transaction, request, requestHash, cancellationToken);
        if (_faultInjector is not null)
        {
            await _faultInjector(SoulRegistryMutationStage.RevokePersistedBeforeCommit, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AliasMetadata>> ExportAliasMetadataAsync(
        string tenantId,
        string soulId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        ValidateSoulId(soulId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT alias_kind, alias_digest, alias_key_id, verified_at, revoked_at
            FROM {_options.SchemaName}.identity_aliases
            WHERE tenant_id = @tenant_id AND soul_id = @soul_id
            ORDER BY alias_kind, alias_key_id, alias_digest
            """,
            connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("soul_id", soulId);
        var results = new List<AliasMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AliasMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime(),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime()));
        }

        return results;
    }

    internal IReadOnlyList<AliasReference> ComputeAliasReferences(string tenantId, IdentityAliasKind kind, string rawAlias)
    {
        ValidateTenant(tenantId);
        return _digester.AllReferences(tenantId, kind, rawAlias);
    }

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

    private static async Task AcquireAliasLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        IReadOnlyList<AliasReference> references,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references.OrderBy(static item => item.KeyId, StringComparer.Ordinal))
        {
            await AcquireLockAsync(
                connection,
                transaction,
                $"alias:{tenantId}:{reference.Kind}:{reference.KeyId}:{reference.Digest}",
                cancellationToken);
        }
    }

    private async Task<IReadOnlyList<AliasRow>> ReadAliasMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        IReadOnlyList<AliasReference> references,
        CancellationToken cancellationToken)
    {
        var predicates = references.Select((_, index) => $"(alias_kind = @kind_{index} AND alias_key_id = @key_{index} AND alias_digest = @digest_{index})");
        await using var command = new NpgsqlCommand(
            $"""
            SELECT alias_id, soul_id, alias_kind, alias_digest, alias_key_id, revoked_at
            FROM {_options.SchemaName}.identity_aliases
            WHERE tenant_id = @tenant_id AND ({string.Join(" OR ", predicates)})
            ORDER BY alias_key_id, alias_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        for (var index = 0; index < references.Count; index++)
        {
            command.Parameters.AddWithValue($"kind_{index}", references[index].Kind);
            command.Parameters.AddWithValue($"key_{index}", references[index].KeyId);
            command.Parameters.AddWithValue($"digest_{index}", references[index].Digest);
        }

        var results = new List<AliasRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AliasRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime()));
        }

        return results;
    }

    private async Task EnsureSoulAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string soulId,
        DateTimeOffset occurredAt,
        bool mustAlreadyExist,
        CancellationToken cancellationToken)
    {
        ValidateSoulId(soulId);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.souls (soul_id, tenant_id, created_at)
            VALUES (@soul_id, @tenant_id, @created_at)
            ON CONFLICT (soul_id) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("created_at", occurredAt);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (mustAlreadyExist && inserted)
        {
            throw new AliasNotFoundException();
        }

        await EnsureSoulTenantAsync(connection, transaction, tenantId, soulId, cancellationToken);
    }

    private async Task EnsureSoulTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string soulId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT tenant_id, tombstoned_at FROM {_options.SchemaName}.souls WHERE soul_id = @soul_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new AliasNotFoundException();
        }

        if (!string.Equals(reader.GetString(0), tenantId, StringComparison.Ordinal))
        {
            throw new CrossTenantIdentityException();
        }

        if (!reader.IsDBNull(1))
        {
            throw new AliasRevokedException();
        }
    }

    private async Task InsertAliasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RegisterVerifiedAliasRequest request,
        string soulId,
        AliasReference reference,
        CancellationToken cancellationToken)
    {
        var evidenceDigest = ComputeSha256(request.Verification.EvidenceId.Normalize(NormalizationForm.FormKC).Trim());
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.identity_aliases
                (alias_id, tenant_id, alias_kind, alias_digest, alias_key_id, soul_id,
                 verification_evidence_sha256, verified_at, created_at)
            VALUES
                (@alias_id, @tenant_id, @alias_kind, @alias_digest, @alias_key_id, @soul_id,
                 @verification_digest, @verified_at, @created_at)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("alias_id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant_id", request.TenantId);
        command.Parameters.AddWithValue("alias_kind", reference.Kind);
        command.Parameters.AddWithValue("alias_digest", reference.Digest);
        command.Parameters.AddWithValue("alias_key_id", reference.KeyId);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("verification_digest", evidenceDigest);
        command.Parameters.AddWithValue("verified_at", request.Verification.VerifiedAt);
        command.Parameters.AddWithValue("created_at", request.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ReceiptRow?> ReadReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT operation, request_sha256, soul_id, device_binding_id, platform_account_id,
                   trace_id, idempotency_key, occurred_at, alias_kind, alias_digest, alias_key_id
            FROM {_options.SchemaName}.resolution_receipts
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReceiptRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7).ToUniversalTime(),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10));
    }

    private async Task InsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string operation,
        string requestHash,
        SoulResolved result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.resolution_receipts
                (tenant_id, idempotency_key, operation, request_sha256, soul_id,
                 device_binding_id, platform_account_id, trace_id, occurred_at,
                 alias_kind, alias_digest, alias_key_id)
            VALUES
                (@tenant_id, @idempotency_key, @operation, @request_sha256, @soul_id,
                 @device_binding_id, @platform_account_id, @trace_id, @occurred_at,
                 @alias_kind, @alias_digest, @alias_key_id)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("request_sha256", requestHash);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("occurred_at", result.OccurredAt);
        command.Parameters.AddWithValue("alias_kind", result.AliasKind);
        command.Parameters.AddWithValue("alias_digest", result.AliasDigest);
        command.Parameters.AddWithValue("alias_key_id", result.AliasKeyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<MutationReceiptRow?> ReadMutationReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT operation, request_sha256, entity_id
            FROM {_options.SchemaName}.mutation_receipts
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MutationReceiptRow(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private async Task InsertMutationReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RevokeAliasRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.mutation_receipts
                (tenant_id, idempotency_key, operation, request_sha256, entity_id, trace_id, occurred_at)
            VALUES
                (@tenant_id, @idempotency_key, 'revoke', @request_sha256, @entity_id, @trace_id, @occurred_at)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant_id", request.TenantId);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("request_sha256", requestHash);
        command.Parameters.AddWithValue("entity_id", request.ExpectedSoulId);
        command.Parameters.AddWithValue("trace_id", request.TraceId);
        command.Parameters.AddWithValue("occurred_at", request.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void EnsureSameRequest(ReceiptRow receipt, string requestHash, string operation)
    {
        if (!string.Equals(receipt.Operation, operation, StringComparison.Ordinal) ||
            !SecretComparison.EqualsHex(receipt.RequestHash, requestHash))
        {
            throw new IdempotencyConflictException();
        }
    }

    private static SoulResolved CreateContract(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        AliasReference reference)
    {
        var result = new SoulResolved(
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            reference.Kind,
            reference.Digest,
            reference.KeyId);
        result.Validate();
        return result;
    }

    private static string ComputeRequestHash(
        string operation,
        string tenantId,
        IReadOnlyList<AliasReference> references,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string? targetSoulId,
        AliasVerification verification)
    {
        var safeProjection = JsonSerializer.Serialize(new
        {
            operation,
            tenant_id = tenantId,
            aliases = references.OrderBy(static item => item.KeyId, StringComparer.Ordinal),
            device_binding_id = deviceBindingId,
            platform_account_id = platformAccountId,
            trace_id = traceId,
            idempotency_key = idempotencyKey,
            occurred_at = occurredAt.ToUniversalTime().ToString("O"),
            target_soul_id = targetSoulId,
            verification_evidence_sha256 = ComputeSha256(verification.EvidenceId.Normalize(NormalizationForm.FormKC).Trim()),
            verified_at = verification.VerifiedAt.ToUniversalTime().ToString("O"),
            verified = verification.Verified
        });
        return ComputeSha256(safeProjection);
    }

    private static string ComputeSha256(string value)
    {
        var input = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(input);
        try
        {
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string CreateSoulId()
    {
        var random = RandomNumberGenerator.GetBytes(32);
        try
        {
            return $"soul_{Convert.ToHexStringLower(random)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    private static string ComputeMutationHash(
        RevokeAliasRequest request,
        IReadOnlyList<AliasReference> references)
    {
        var safeProjection = JsonSerializer.Serialize(new
        {
            operation = "revoke",
            tenant_id = request.TenantId,
            aliases = references.OrderBy(static item => item.KeyId, StringComparer.Ordinal),
            expected_soul_id = request.ExpectedSoulId,
            reason_sha256 = ComputeSha256(request.Reason.Normalize(NormalizationForm.FormKC).Trim()),
            trace_id = request.TraceId,
            idempotency_key = request.IdempotencyKey,
            occurred_at = request.OccurredAt.ToUniversalTime().ToString("O")
        });
        return ComputeSha256(safeProjection);
    }

    public void Dispose() => _digester.Dispose();

    private static void ValidateRegistration(RegisterVerifiedAliasRequest request)
    {
        ValidateCommon(
            request.SchemaVersion,
            request.TenantId,
            request.AliasKind,
            request.RawAlias,
            request.Verification,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt);
        if (request.TargetSoulId is not null)
        {
            ValidateSoulId(request.TargetSoulId);
        }
    }

    private static void ValidateResolve(ResolveSoulRequest request)
        => ValidateCommon(
            request.SchemaVersion,
            request.TenantId,
            request.AliasKind,
            request.RawAlias,
            request.Verification,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt);

    private static void ValidateCommon(
        string schemaVersion,
        string tenantId,
        IdentityAliasKind aliasKind,
        string rawAlias,
        AliasVerification verification,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        SoulResolvedValidation.RequireSupportedMajor(schemaVersion, 1);
        ValidateTenant(tenantId);
        _ = AliasDigester.Normalize(aliasKind, rawAlias);
        ArgumentNullException.ThrowIfNull(verification);
        if (!verification.Verified || string.IsNullOrWhiteSpace(verification.EvidenceId) || verification.EvidenceId.Length > 256 ||
            verification.VerifiedAt.Offset != TimeSpan.Zero || verification.VerifiedAt > occurredAt)
        {
            throw new ArgumentException("Alias verification proof is invalid.", nameof(verification));
        }

        ValidateOpaqueId(deviceBindingId, "db_", nameof(deviceBindingId));
        ValidateOpaqueId(platformAccountId, "pa_", nameof(platformAccountId));
        ValidatePrefixedLowerHex(traceId, "trace_", 32, nameof(traceId));
        ValidatePrefixedLowerHex(idempotencyKey, "idem_", 64, nameof(idempotencyKey));
        ValidateUtc(occurredAt, nameof(occurredAt));
    }

    private static void ValidateRevoke(RevokeAliasRequest request)
    {
        SoulResolvedValidation.RequireSupportedMajor(request.SchemaVersion, 1);
        ValidateTenant(request.TenantId);
        _ = AliasDigester.Normalize(request.AliasKind, request.RawAlias);
        ValidateSoulId(request.ExpectedSoulId);
        ValidateText(request.Reason, 256, nameof(request.Reason));
        ValidatePrefixedLowerHex(request.TraceId, "trace_", 32, nameof(request.TraceId));
        ValidatePrefixedLowerHex(request.IdempotencyKey, "idem_", 64, nameof(request.IdempotencyKey));
        ValidateUtc(request.OccurredAt, nameof(request.OccurredAt));
    }

    private static void ValidateTenant(string tenantId) => ValidateText(tenantId, 128, nameof(tenantId));

    private static void ValidateSoulId(string soulId)
    {
        if (string.IsNullOrWhiteSpace(soulId) || soulId.Length != 69 || !soulId.StartsWith("soul_", StringComparison.Ordinal) ||
            soulId.AsSpan(5).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException("Soul id is invalid.", nameof(soulId));
        }
    }

    private static void ValidateOpaqueId(string value, string prefix, string parameterName)
    {
        ValidatePrefixedLowerHex(value, prefix, 32, parameterName);
    }

    private static void ValidatePrefixedLowerHex(string value, string prefix, int bodyLength, string parameterName)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + bodyLength ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Opaque identity scope is invalid.", parameterName);
    }

    private static void ValidateText(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("A required identity field is invalid.", parameterName);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Identity timestamps must use UTC.", parameterName);
        }
    }

    private sealed record AliasRow(
        Guid AliasId,
        string SoulId,
        string AliasKind,
        string AliasDigest,
        string AliasKeyId,
        DateTimeOffset? RevokedAt);

    private sealed record ReceiptRow(
        string Operation,
        string RequestHash,
        string SoulId,
        string DeviceBindingId,
        string PlatformAccountId,
        string TraceId,
        string IdempotencyKey,
        DateTimeOffset OccurredAt,
        string AliasKind,
        string AliasDigest,
        string AliasKeyId)
    {
        public SoulResolved ToContract()
            => CreateContract(
                SoulId,
                DeviceBindingId,
                PlatformAccountId,
                TraceId,
                IdempotencyKey,
                OccurredAt,
                new AliasReference(AliasKind, AliasDigest, AliasKeyId));
    }

    private sealed record MutationReceiptRow(string Operation, string RequestHash, string EntityId);
}

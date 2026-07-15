using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dps.EvidenceService.Contracts;
using Npgsql;

namespace Dps.EvidenceService;

public sealed record EvidenceStoreOptions(string ConnectionString, string SchemaName)
{
    private static readonly Regex SchemaPattern = new("^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(ConnectionString));
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || !SchemaPattern.IsMatch(SchemaName))
        {
            throw new ArgumentException("SchemaName must be a safe lowercase PostgreSQL identifier.", nameof(SchemaName));
        }
    }

    public override string ToString()
    {
        var safeSchemaName = SchemaName is not null && SchemaPattern.IsMatch(SchemaName)
            ? SchemaName
            : "[INVALID]";
        return $"EvidenceStoreOptions {{ ConnectionString = [REDACTED], SchemaName = {safeSchemaName} }}";
    }
}

public sealed class PostgresEvidenceStore : IEvidenceStore
{
    private const string MigrationResourceSuffix = "001_create_evidence_store.sql";
    private const string ReceiptArtifactId = "system:test-evidence-receipt-v1";
    private readonly EvidenceStoreOptions _options;

    public PostgresEvidenceStore(EvidenceStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(PostgresEvidenceStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(MigrationResourceSuffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{MigrationResourceSuffix}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var migration = await reader.ReadToEndAsync(cancellationToken);
        migration = migration.Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(migration, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EvidenceStoreResult> SaveAsync(
        EvidenceBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var receipt = bundle.Receipt;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var inserted = await TryInsertAsync(connection, transaction, bundle, cancellationToken);
        if (inserted)
        {
            await InsertArtifactsAsync(connection, transaction, bundle, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EvidenceStoreResult(EvidenceStoreDisposition.Stored, receipt.EvidenceId, bundle.Checksum);
        }

        var existingChecksum = await ReadExistingChecksumAsync(
            connection,
            transaction,
            receipt.EvidenceId,
            cancellationToken) ?? throw new InvalidOperationException("The conflicting evidence record disappeared.");
        if (string.Equals(existingChecksum, bundle.Checksum, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new EvidenceStoreResult(EvidenceStoreDisposition.DuplicateNoOp, receipt.EvidenceId, bundle.Checksum);
        }

        await InsertQuarantineAsync(
            connection,
            transaction,
            bundle,
            existingChecksum,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EvidenceStoreResult(EvidenceStoreDisposition.Quarantined, receipt.EvidenceId, bundle.Checksum);
    }

    public async Task<EvidenceDigestRecord?> ReadAsync(
        Guid evidenceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT evidence_id, soul_id, device_binding_id, platform_account_id, trace_id,
                   module_id, status, verification_level, baseline_commit,
                   instruction_receipt_sha256, receipt_sha256, artifact_set_sha256, source_receipt_set_sha256,
                   runner_key_id, attestation_algorithm, attestation_issued_at,
                   attestation_sha256, bundle_checksum, occurred_at
            FROM {_options.SchemaName}.test_evidence
            WHERE evidence_id = @evidence_id
              AND soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            """,
            connection);
        command.Parameters.AddWithValue("evidence_id", evidenceId);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EvidenceDigestRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15).ToUniversalTime(),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetFieldValue<DateTimeOffset>(18).ToUniversalTime());
    }

    public Task<long> CountForSoulAsync(string soulId, CancellationToken cancellationToken = default)
        => CountAsync("test_evidence", "soul_id", soulId, cancellationToken);

    public Task<long> CountQuarantineAsync(CancellationToken cancellationToken = default)
        => CountAsync("evidence_quarantine", null, null, cancellationToken);

    public async Task<bool> VerifyPersistedEvidenceAsync(
        Guid evidenceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CryptographicRunnerAttestationVerifier attestationVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestationVerifier);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var proof = await ReadPersistedProofAsync(
            connection,
            evidenceId,
            soulId,
            deviceBindingId,
            platformAccountId,
            cancellationToken);
        if (proof is null)
        {
            return false;
        }

        var artifacts = await ReadPersistedArtifactsAsync(
            connection,
            evidenceId,
            soulId,
            deviceBindingId,
            platformAccountId,
            cancellationToken);
        var receiptArtifact = artifacts.SingleOrDefault(static item => item.Role == "receipt")
            ?? throw new InvalidOperationException("Persisted evidence is missing its canonical receipt bytes.");
        if (!string.Equals(receiptArtifact.ArtifactId, ReceiptArtifactId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted evidence contains an unknown receipt artifact.");
        }

        var receipt = TestEvidenceCanonicalizer.Deserialize(Encoding.UTF8.GetString(receiptArtifact.Content));
        var rawArtifacts = artifacts
            .Where(static item => item.Role == "source")
            .Select(static item => RawEvidenceArtifact.FromBytes(item.ArtifactId, item.Content))
            .ToArray();
        var candidate = EvidenceSubmissionCandidate.Create(receipt, rawArtifacts);
        if (!SecureEquals(candidate.ReceiptSha256, proof.ReceiptSha256) ||
            !SecureEquals(candidate.ArtifactSetSha256, proof.ArtifactSetSha256) ||
            !SecureEquals(candidate.SourceReceiptSetSha256, proof.SourceReceiptSetSha256))
        {
            throw new InvalidOperationException("Persisted receipt or artifact digests do not match their stored proof.");
        }

        var facts = new RunnerAttestationFactsV1(
            RunnerAttestationFactsV1.CurrentSchemaVersion,
            proof.RunnerKeyId,
            proof.AttestationAlgorithm,
            proof.AttestationIssuedAt,
            candidate.ReceiptSha256,
            candidate.ArtifactSetSha256,
            candidate.SourceReceiptSetSha256,
            candidate.Receipt.CommandSha256,
            candidate.Receipt.ExitCode,
            BaselineObjectVerified: true,
            InstructionReceiptVerified: true,
            RawArtifactsObserved: true,
            RoleSeparationVerified: true);
        var attestation = new SignedRunnerAttestationV1(facts, proof.AttestationSignature);
        attestationVerifier.Verify(candidate, attestation);
        if (!SecureEquals(RunnerAttestationCanonicalizer.ComputeSha256(attestation), proof.AttestationSha256))
        {
            throw new InvalidOperationException("Persisted runner attestation digest is invalid.");
        }

        var bundle = EvidenceBundle.CreateVerified(candidate, attestation);
        if (!SecureEquals(bundle.Checksum, proof.BundleChecksum))
        {
            throw new InvalidOperationException("Persisted evidence bundle checksum is invalid.");
        }

        return true;
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

    private async Task<bool> TryInsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EvidenceBundle bundle,
        CancellationToken cancellationToken)
    {
        var receipt = bundle.Receipt;
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.test_evidence
                (evidence_id, soul_id, device_binding_id, platform_account_id, trace_id, idempotency_key,
                 module_id, status, verification_level, baseline_commit,
                 instruction_receipt_sha256, receipt_sha256, artifact_set_sha256, source_receipt_set_sha256,
                 runner_key_id, attestation_algorithm, attestation_issued_at,
                 attestation_signature, attestation_sha256, bundle_checksum, occurred_at)
            VALUES
                (@evidence_id, @soul_id, @device_binding_id, @platform_account_id, @trace_id, @idempotency_key,
                 @module_id, @status, @verification_level, @baseline_commit,
                 @instruction_receipt_sha256, @receipt_sha256, @artifact_set_sha256, @source_receipt_set_sha256,
                 @runner_key_id, @attestation_algorithm, @attestation_issued_at,
                 @attestation_signature, @attestation_sha256, @bundle_checksum, @occurred_at)
            ON CONFLICT (evidence_id) DO NOTHING
            RETURNING evidence_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("evidence_id", receipt.EvidenceId);
        command.Parameters.AddWithValue("soul_id", receipt.SoulId);
        command.Parameters.AddWithValue("device_binding_id", receipt.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", receipt.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", receipt.TraceId);
        command.Parameters.AddWithValue("idempotency_key", receipt.IdempotencyKey);
        command.Parameters.AddWithValue("module_id", receipt.ModuleId);
        command.Parameters.AddWithValue("status", receipt.Status);
        command.Parameters.AddWithValue("verification_level", receipt.VerificationLevel);
        command.Parameters.AddWithValue("baseline_commit", receipt.BaselineCommit);
        command.Parameters.AddWithValue("instruction_receipt_sha256", receipt.InstructionReceiptSha256);
        command.Parameters.AddWithValue("receipt_sha256", bundle.ReceiptSha256);
        command.Parameters.AddWithValue("artifact_set_sha256", bundle.ArtifactSetSha256);
        command.Parameters.AddWithValue("source_receipt_set_sha256", bundle.SourceReceiptSetSha256);
        command.Parameters.AddWithValue("runner_key_id", bundle.Attestation.Facts.RunnerKeyId);
        command.Parameters.AddWithValue("attestation_algorithm", bundle.Attestation.Facts.Algorithm);
        command.Parameters.AddWithValue("attestation_issued_at", bundle.Attestation.Facts.IssuedAt);
        command.Parameters.AddWithValue("attestation_signature", bundle.Attestation.SignatureBase64);
        command.Parameters.AddWithValue("attestation_sha256", bundle.AttestationSha256);
        command.Parameters.AddWithValue("bundle_checksum", bundle.Checksum);
        command.Parameters.AddWithValue("occurred_at", receipt.OccurredAt);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task InsertArtifactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EvidenceBundle bundle,
        CancellationToken cancellationToken)
    {
        var canonicalReceipt = Encoding.UTF8.GetBytes(TestEvidenceCanonicalizer.Serialize(bundle.Receipt));
        try
        {
            await InsertArtifactAsync(
                connection,
                transaction,
                bundle.Receipt.EvidenceId,
                ReceiptArtifactId,
                "receipt",
                bundle.ReceiptSha256,
                "application/json",
                canonicalReceipt,
                cancellationToken);

            var metadata = bundle.Receipt.Artifacts.ToDictionary(static item => item.ArtifactId, StringComparer.Ordinal);
            foreach (var rawArtifact in bundle.RawArtifacts)
            {
                var declared = metadata[rawArtifact.ArtifactId];
                await InsertArtifactAsync(
                    connection,
                    transaction,
                    bundle.Receipt.EvidenceId,
                    rawArtifact.ArtifactId,
                    "source",
                    rawArtifact.Sha256,
                    declared.MediaType,
                    rawArtifact.Content.ToArray(),
                    cancellationToken);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalReceipt);
        }
    }

    private async Task InsertArtifactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid evidenceId,
        string artifactId,
        string artifactRole,
        string sha256,
        string mediaType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {_options.SchemaName}.evidence_artifacts
                    (evidence_id, artifact_id, artifact_role, sha256, size_bytes, media_type, content_bytes)
                VALUES
                    (@evidence_id, @artifact_id, @artifact_role, @sha256, @size_bytes, @media_type, @content_bytes)
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("evidence_id", evidenceId);
            command.Parameters.AddWithValue("artifact_id", artifactId);
            command.Parameters.AddWithValue("artifact_role", artifactRole);
            command.Parameters.AddWithValue("sha256", sha256);
            command.Parameters.AddWithValue("size_bytes", (long)content.Length);
            command.Parameters.AddWithValue("media_type", mediaType);
            command.Parameters.AddWithValue("content_bytes", content);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private async Task<PersistedProof?> ReadPersistedProofAsync(
        NpgsqlConnection connection,
        Guid evidenceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT receipt_sha256, artifact_set_sha256, source_receipt_set_sha256,
                   runner_key_id, attestation_algorithm, attestation_issued_at,
                   attestation_signature, attestation_sha256, bundle_checksum
            FROM {_options.SchemaName}.test_evidence
            WHERE evidence_id = @evidence_id
              AND soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            """,
            connection);
        command.Parameters.AddWithValue("evidence_id", evidenceId);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PersistedProof(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime(),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8))
            : null;
    }

    private async Task<IReadOnlyList<PersistedArtifact>> ReadPersistedArtifactsAsync(
        NpgsqlConnection connection,
        Guid evidenceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT artifact_id, artifact_role, sha256, size_bytes, media_type, content_bytes
            FROM {_options.SchemaName}.evidence_artifacts AS artifact
            WHERE artifact.evidence_id = @evidence_id
              AND EXISTS (
                  SELECT 1
                  FROM {_options.SchemaName}.test_evidence AS evidence
                  WHERE evidence.evidence_id = artifact.evidence_id
                    AND evidence.soul_id = @soul_id
                    AND evidence.device_binding_id = @device_binding_id
                    AND evidence.platform_account_id = @platform_account_id)
            ORDER BY artifact_id
            """,
            connection);
        command.Parameters.AddWithValue("evidence_id", evidenceId);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        var artifacts = new List<PersistedArtifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var content = reader.GetFieldValue<byte[]>(5);
            var declaredSize = reader.GetInt64(3);
            var declaredSha256 = reader.GetString(2);
            if (content.LongLength != declaredSize ||
                !SecureEquals(Convert.ToHexStringLower(SHA256.HashData(content)), declaredSha256))
            {
                throw new InvalidOperationException("Persisted raw artifact bytes do not match their digest and size.");
            }

            artifacts.Add(new PersistedArtifact(
                reader.GetString(0),
                reader.GetString(1),
                declaredSha256,
                declaredSize,
                reader.GetString(4),
                content));
        }

        return artifacts;
    }

    private static bool SecureEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<string?> ReadExistingChecksumAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT bundle_checksum FROM {_options.SchemaName}.test_evidence WHERE evidence_id = @evidence_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("evidence_id", evidenceId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EvidenceBundle bundle,
        string existingChecksum,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.evidence_quarantine
                (quarantine_id, evidence_id, incoming_soul_id, existing_checksum,
                 incoming_checksum, incoming_artifact_set_sha256, reason_code)
            VALUES
                (@quarantine_id, @evidence_id, @incoming_soul_id, @existing_checksum,
                 @incoming_checksum, @incoming_artifact_set_sha256, @reason_code)
            ON CONFLICT (evidence_id, incoming_checksum) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("quarantine_id", Guid.NewGuid());
        command.Parameters.AddWithValue("evidence_id", bundle.Receipt.EvidenceId);
        command.Parameters.AddWithValue("incoming_soul_id", bundle.Receipt.SoulId);
        command.Parameters.AddWithValue("existing_checksum", existingChecksum);
        command.Parameters.AddWithValue("incoming_checksum", bundle.Checksum);
        command.Parameters.AddWithValue("incoming_artifact_set_sha256", bundle.ArtifactSetSha256);
        command.Parameters.AddWithValue("reason_code", "evidence_id_checksum_conflict");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(
        string table,
        string? predicateColumn,
        string? predicateValue,
        CancellationToken cancellationToken)
    {
        var allowedTable = table switch
        {
            "test_evidence" => "test_evidence",
            "evidence_quarantine" => "evidence_quarantine",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        var predicate = predicateColumn == "soul_id" ? " WHERE soul_id = @predicate" : string.Empty;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {_options.SchemaName}.{allowedTable}{predicate}",
            connection);
        if (predicate.Length > 0)
        {
            command.Parameters.AddWithValue("predicate", predicateValue!);
        }

        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return an evidence count."));
    }

    private sealed record PersistedProof(
        string ReceiptSha256,
        string ArtifactSetSha256,
        string SourceReceiptSetSha256,
        string RunnerKeyId,
        string AttestationAlgorithm,
        DateTimeOffset AttestationIssuedAt,
        string AttestationSignature,
        string AttestationSha256,
        string BundleChecksum);

    private sealed record PersistedArtifact(
        string ArtifactId,
        string Role,
        string Sha256,
        long SizeBytes,
        string MediaType,
        byte[] Content);
}

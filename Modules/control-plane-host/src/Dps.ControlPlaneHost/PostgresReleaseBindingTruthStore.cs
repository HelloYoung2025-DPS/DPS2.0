using System.Data;
using System.Text;
using Dps.ControlPlaneHost.Contracts;
using Npgsql;

namespace Dps.ControlPlaneHost;

/// <summary>
/// Raised when a journal append loses the per-device compare-and-set race:
/// the appended sequence was not the exactly-next sequence at commit time.
/// The losing authority instance must fail closed without publishing.
/// </summary>
public sealed class ReleaseBindingTruthConflictException : ActiveReleaseBindingException
{
    public ReleaseBindingTruthConflictException(string message)
        : base(message)
    {
    }

    public ReleaseBindingTruthConflictException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed class PostgresReleaseBindingMigrationOptions
{
    private readonly string _migrationConnectionString;

    public PostgresReleaseBindingMigrationOptions(
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
        => $"PostgresReleaseBindingMigrationOptions {{ SchemaName = {SchemaName}, RuntimeRoleName = {RuntimeRoleName}, MigrationConnectionString = [REDACTED] }}";
}

/// <summary>
/// Applies the release binding truth migration
/// (002_create_release_binding_truth.sql) with the same connection policy as
/// PostgresControlPlaneTruthMigrator: explicit non-runtime login, exact
/// PostgreSQL 18.4, verified session timeouts, one transaction.
/// </summary>
public sealed class PostgresReleaseBindingTruthMigrator
{
    private const string MigrationResourceSuffix = "002_create_release_binding_truth.sql";
    private readonly PostgresReleaseBindingMigrationOptions _options;
    private readonly string _connectionString;
    private readonly string _expectedDatabase;
    private readonly int _expectedPort;

    public PostgresReleaseBindingTruthMigrator(PostgresReleaseBindingMigrationOptions options)
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
        var assembly = typeof(PostgresReleaseBindingTruthMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(MigrationResourceSuffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded release binding migration is missing.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

public sealed class PostgresReleaseBindingTruthStoreOptions
{
    private readonly string _runtimeConnectionString;

    public PostgresReleaseBindingTruthStoreOptions(
        string runtimeConnectionString,
        string schemaName,
        string runtimeRoleName,
        string migrationRoleName)
    {
        _runtimeConnectionString = runtimeConnectionString;
        SchemaName = schemaName;
        RuntimeRoleName = runtimeRoleName;
        MigrationRoleName = migrationRoleName;
    }

    public string SchemaName { get; }
    public string RuntimeRoleName { get; }
    public string MigrationRoleName { get; }

    internal string ValidatedConnectionString()
    {
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(SchemaName, nameof(SchemaName));
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(RuntimeRoleName, nameof(RuntimeRoleName));
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(MigrationRoleName, nameof(MigrationRoleName));
        if (string.Equals(RuntimeRoleName, MigrationRoleName, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Migration and runtime roles must be distinct.");
        }
        return PostgresControlPlaneConnectionPolicy.Normalize(
            _runtimeConnectionString,
            "runtimeConnectionString",
            requiredUsername: RuntimeRoleName);
    }

    public override string ToString()
        => $"PostgresReleaseBindingTruthStoreOptions {{ SchemaName = {SchemaName}, RuntimeRoleName = {RuntimeRoleName}, MigrationRoleName = {MigrationRoleName}, RuntimeConnectionString = [REDACTED] }}";
}

/// <summary>
/// Durable PostgreSQL implementation of the release binding truth journal
/// (F3) and of the database-issued recovery fence authority (F2).
///
/// Journal: one append-only row per successful transition, primary key
/// (device_binding_id, sequence); Append goes through the SECURITY DEFINER
/// compare-and-set function append_release_binding_record, which locks the
/// device journal, requires the exactly-next per-device sequence, and raises
/// on any conflict — the losing writer observes
/// <see cref="ReleaseBindingTruthConflictException"/> and publishes nothing.
/// The runtime role holds SELECT plus EXECUTE only; UPDATE/DELETE/TRUNCATE
/// are revoked and additionally rejected by triggers.
///
/// Rows carry the exact canonical contract wires
/// (ReleaseBindingReceiptV1Codec / ActiveReleaseBindingV1Codec bytes) plus
/// the exact signed BOM bytes, so LoadAll rebuilds
/// <see cref="ReleaseBindingTruthRecord"/> values byte-faithfully and
/// <see cref="ActiveReleaseBindingAuthority"/> replays its full
/// cross-binding re-verification (RSA-PSS signature, token commitment,
/// predecessor chain, receipt-snapshot binding) at construction; any
/// inconsistency refuses startup fail-closed.
///
/// Fences: IssueRecoveryFence reads the device journal head and fails closed
/// unless it is an active binding; CommitRecoveryFence calls the SECURITY
/// DEFINER function commit_release_binding_recovery_fence, which — inside
/// one database transaction holding the same device journal lock as appends —
/// re-verifies the head has not advanced and appends the fence record, or
/// raises so the caller never releases the signed recovery envelope.
///
/// Transitions: <see cref="BeginTransition"/> implements the durable half of
/// the global lock-order convention. It opens one verified connection and one
/// ReadCommitted transaction and takes the per-device
/// pg_advisory_xact_lock with the byte-identical key the
/// append_release_binding_record function derives
/// (hashtextextended('release-binding:' || device_binding_id, 0)). The
/// authority acquires this scope BEFORE entering its in-process gate on every
/// activation, revocation, and rollback; the scoped append runs the CAS
/// function on the same session — where the function's internal advisory
/// lock is re-entrant — and commits, so the journal row is durable before the
/// authority publishes. Policy recovery obtains a store-issued scope that
/// takes this same advisory lock and reads the binding in that transaction,
/// then holds the scope through its policy commit. No path waits for the
/// advisory lock while holding the authority gate, so no hold-and-wait cycle
/// can form.
/// </summary>
public sealed class PostgresReleaseBindingTruthStore
    : IReleaseBindingTruthStore,
      IReleaseBindingRecoveryFenceAuthority,
      IActiveReleaseBindingRecoveryCoordinator
{
    private static readonly string[] TableNames =
    [
        "release_binding_journal",
        "release_binding_recovery_fences"
    ];

    private const string SchemaMarker = "dps.control-plane-host.release-binding-baseline/v1";
    private const string SequenceConflictMessage = "release binding journal sequence conflict";
    private const string FenceConflictMessage = "release binding recovery fence conflict";

    private readonly PostgresReleaseBindingTruthStoreOptions _options;
    private readonly string _connectionString;
    private readonly string _expectedDatabase;
    private readonly int _expectedPort;
    private readonly string _qualifiedJournal;
    private readonly string _qualifiedFences;
    private readonly string _qualifiedAppend;
    private readonly string _qualifiedCommitFence;

    public PostgresReleaseBindingTruthStore(PostgresReleaseBindingTruthStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.ValidatedConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        _expectedDatabase = builder.Database
            ?? throw new InvalidOperationException("An explicit PostgreSQL database is required.");
        _expectedPort = builder.Port;
        _options = options;
        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(options.SchemaName);
        _qualifiedJournal = quotedSchema + ".release_binding_journal";
        _qualifiedFences = quotedSchema + ".release_binding_recovery_fences";
        _qualifiedAppend = quotedSchema + ".append_release_binding_record";
        _qualifiedCommitFence = quotedSchema + ".commit_release_binding_recovery_fence";
    }

    internal async Task<long> CountJournalAsync(CancellationToken cancellationToken = default)
        => await CountAsync(_qualifiedJournal, cancellationToken);

    internal async Task<long> CountRecoveryFencesAsync(
        CancellationToken cancellationToken = default)
        => await CountAsync(_qualifiedFences, cancellationToken);

    private async Task<long> CountAsync(string qualifiedTable, CancellationToken cancellationToken)
    {
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command($"SELECT count(*) FROM {qualifiedTable}", connection);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Append(ReleaseBindingTruthRecord record)
        => AppendAsync(record, CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll()
        => LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();

    public long LoadDeviceHeadSequence(string deviceBindingId)
        => LoadDeviceHeadSequenceAsync(deviceBindingId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(
        string deviceBindingId,
        long afterSequence)
        => LoadAfterAsync(deviceBindingId, afterSequence, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public ReleaseBindingJournalSnapshot LoadSnapshotAfter(
        string deviceBindingId,
        long afterSequence)
        => LoadSnapshotAfterAsync(deviceBindingId, afterSequence, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public ReleaseBindingRecoveryFence IssueRecoveryFence(string deviceBindingId)
        => IssueRecoveryFenceAsync(deviceBindingId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public void CommitRecoveryFence(
        ReleaseBindingRecoveryFence fence,
        Guid recoveryId,
        string recoveryContentSha256)
        => CommitRecoveryFenceAsync(fence, recoveryId, recoveryContentSha256, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private async Task AppendAsync(
        ReleaseBindingTruthRecord record,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = CreateAppendCommand(connection, null, record);
        await ExecuteAppendCommandAsync(command, cancellationToken);
    }

    public IReleaseBindingTransitionScope BeginTransition(string deviceBindingId)
        => BeginTransitionAsync(deviceBindingId, CancellationToken.None).GetAwaiter().GetResult();

    async ValueTask<IActiveReleaseBindingRecoveryScope>
        IActiveReleaseBindingRecoveryCoordinator.AcquireAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        var connection = await OpenVerifiedAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;
        var ownershipTransferred = false;
        try
        {
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await using (var transitionLock = Command(
                "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended('release-binding:' || @device_binding_id, 0))",
                connection,
                transaction))
            {
                transitionLock.Parameters.AddWithValue("device_binding_id", deviceBindingId);
                await transitionLock.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var bindingCommand = Command(
                $"SELECT current_binding_wire FROM {_qualifiedJournal} "
                + "WHERE device_binding_id = @device_binding_id "
                + "ORDER BY sequence DESC LIMIT 1",
                connection,
                transaction);
            bindingCommand.Parameters.AddWithValue("device_binding_id", deviceBindingId);
            var wire = await bindingCommand.ExecuteScalarAsync(cancellationToken) as byte[]
                ?? throw new ActiveReleaseBindingException(
                    "recovery coordination requires an active release binding");
            var binding = ActiveReleaseBindingV1Codec.Deserialize(wire);
            if (!string.Equals(binding.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
                || !string.Equals(binding.Status, "active", StringComparison.Ordinal))
            {
                throw new ActiveReleaseBindingException(
                    "recovery coordination requires the exact active binding for the requested device");
            }

            var scope = new PostgresRecoveryScope(binding, connection, transaction);
            ownershipTransferred = true;
            return scope;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                try
                {
                    if (transaction is not null)
                    {
                        await transaction.DisposeAsync();
                    }
                }
                finally
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }

    private async Task<IReleaseBindingTransitionScope> BeginTransitionAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        var connection = await OpenVerifiedAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;
        var ownershipTransferred = false;
        try
        {
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            // The byte-identical per-device key the append/fence SECURITY
            // DEFINER functions derive; taken here — on the scope's own
            // transaction — BEFORE the authority enters its in-process gate,
            // and re-taken re-entrantly by the append function on this same
            // session. Released automatically at commit/rollback.
            await using var command = Command(
                "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended('release-binding:' || @device_binding_id, 0))",
                connection,
                transaction);
            command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            var scope = new PostgresTransitionScope(this, connection, transaction);
            ownershipTransferred = true;
            return scope;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                try
                {
                    if (transaction is not null)
                    {
                        await transaction.DisposeAsync();
                    }
                }
                finally
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }

    private NpgsqlCommand CreateAppendCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ReleaseBindingTruthRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var receipt = record.Receipt
            ?? throw new ActiveReleaseBindingException("truth store append requires a receipt");
        var current = record.CurrentBinding
            ?? throw new ActiveReleaseBindingException("truth store append requires a current binding");
        ControlContractValidation.RequireDeviceBindingId(record.DeviceBindingId);
        if (!string.Equals(receipt.DeviceBindingId, record.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(current.DeviceBindingId, record.DeviceBindingId, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException("truth store record device identity fork");
        }
        if (string.IsNullOrEmpty(record.RequestSha256))
        {
            throw new ActiveReleaseBindingException("truth store journal request digest is missing");
        }

        var receiptWire = ReleaseBindingReceiptV1Codec.Serialize(receipt);
        var currentWire = ActiveReleaseBindingV1Codec.Serialize(current);
        var previousWire = record.PreviousBinding is null
            ? null
            : ActiveReleaseBindingV1Codec.Serialize(record.PreviousBinding);

        var command = Command(
            $"SELECT {_qualifiedAppend}(@device_binding_id, @sequence, @receipt_kind, "
            + "@receipt_wire, @current_binding_wire, @previous_binding_wire, "
            + "@last_activation_signer_generation, @request_sha256, @signed_bom_wire)",
            connection,
            transaction);
        command.Parameters.AddWithValue("device_binding_id", record.DeviceBindingId);
        command.Parameters.AddWithValue("sequence", receipt.Sequence);
        command.Parameters.AddWithValue("receipt_kind", receipt.ReceiptKind);
        command.Parameters.AddWithValue("receipt_wire", receiptWire);
        command.Parameters.AddWithValue("current_binding_wire", currentWire);
        command.Parameters.AddWithValue(
            "previous_binding_wire",
            (object?)previousWire ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "last_activation_signer_generation",
            record.LastActivationSignerGeneration);
        command.Parameters.AddWithValue("request_sha256", record.RequestSha256);
        command.Parameters.AddWithValue(
            "signed_bom_wire",
            (object?)record.SignedBomBytes ?? DBNull.Value);
        return command;
    }

    private static async Task ExecuteAppendCommandAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.RaiseException
            && exception.MessageText.StartsWith(SequenceConflictMessage, StringComparison.Ordinal))
        {
            throw new ReleaseBindingTruthConflictException(
                "truth store append sequence conflict: expected the exactly-next per-device sequence",
                exception);
        }
    }

    /// <summary>
    /// One durable per-device transition scope: the open connection and
    /// transaction that hold the device's advisory lock from before the
    /// authority's in-process gate until the append commits. Append commits
    /// the transaction, so a successful return means the journal row is
    /// durable and the lock released; disposing an unused scope rolls back
    /// and persists nothing.
    /// </summary>
    private sealed class PostgresTransitionScope : IReleaseBindingTransitionScope
    {
        private readonly PostgresReleaseBindingTruthStore _store;
        private NpgsqlConnection? _connection;
        private NpgsqlTransaction? _transaction;

        internal PostgresTransitionScope(
            PostgresReleaseBindingTruthStore store,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            _store = store;
            _connection = connection;
            _transaction = transaction;
        }

        public void Append(ReleaseBindingTruthRecord record)
            => AppendAsync(record, CancellationToken.None).GetAwaiter().GetResult();

        private async Task AppendAsync(
            ReleaseBindingTruthRecord record,
            CancellationToken cancellationToken)
        {
            if (_connection is null || _transaction is null)
            {
                throw new ObjectDisposedException(
                    nameof(PostgresTransitionScope),
                    "A completed or disposed transition scope cannot append.");
            }

            await using var command = _store.CreateAppendCommand(_connection, _transaction, record);
            await ExecuteAppendCommandAsync(command, cancellationToken);
            await _transaction.CommitAsync(cancellationToken);
            await _transaction.DisposeAsync();
            _transaction = null;
            await _connection.DisposeAsync();
            _connection = null;
        }

        public void Dispose()
        {
            _transaction?.Dispose();
            _transaction = null;
            _connection?.Dispose();
            _connection = null;
        }
    }

    private sealed class PostgresRecoveryScope : IActiveReleaseBindingRecoveryScope
    {
        private PostgresRecoveryResources? _resources;

        internal PostgresRecoveryScope(
            ActiveReleaseBindingV1 activeBinding,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            ActiveBinding = activeBinding;
            _resources = new PostgresRecoveryResources(connection, transaction);
        }

        public ActiveReleaseBindingV1 ActiveBinding { get; }

        public async ValueTask DisposeAsync()
        {
            var resources = Interlocked.Exchange(ref _resources, null);
            if (resources is null)
            {
                return;
            }

            try
            {
                try
                {
                    await resources.Transaction.RollbackAsync(CancellationToken.None);
                }
                finally
                {
                    await resources.Transaction.DisposeAsync();
                }
            }
            finally
            {
                await resources.Connection.DisposeAsync();
            }
        }
    }

    private sealed record PostgresRecoveryResources(
        NpgsqlConnection Connection,
        NpgsqlTransaction Transaction);

    private async Task<IReadOnlyList<ReleaseBindingTruthRecord>> LoadAllAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"""
            SELECT device_binding_id, sequence, receipt_kind, receipt_wire,
                   current_binding_wire, previous_binding_wire,
                   last_activation_signer_generation, request_sha256,
                   signed_bom_wire
            FROM {_qualifiedJournal}
            ORDER BY device_binding_id, sequence
            """,
            connection);
        return await ReadRecordsAsync(command, cancellationToken);
    }

    private async Task<long> LoadDeviceHeadSequenceAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"SELECT COALESCE(max(sequence), 0) FROM {_qualifiedJournal} "
            + "WHERE device_binding_id = @device_binding_id",
            connection);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<ReleaseBindingTruthRecord>> LoadAfterAsync(
        string deviceBindingId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"""
            SELECT device_binding_id, sequence, receipt_kind, receipt_wire,
                   current_binding_wire, previous_binding_wire,
                   last_activation_signer_generation, request_sha256,
                   signed_bom_wire
            FROM {_qualifiedJournal}
            WHERE device_binding_id = @device_binding_id
              AND sequence > @after_sequence
            ORDER BY sequence
            """,
            connection);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("after_sequence", afterSequence);
        return await ReadRecordsAsync(command, cancellationToken);
    }

    private async Task<ReleaseBindingJournalSnapshot> LoadSnapshotAfterAsync(
        string deviceBindingId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using var headCommand = Command(
            $"SELECT COALESCE(max(sequence), 0) FROM {_qualifiedJournal} "
            + "WHERE device_binding_id = @device_binding_id",
            connection,
            transaction);
        headCommand.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        var head = Convert.ToInt64(
            await headCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);

        await using var deltaCommand = Command(
            $"""
            SELECT device_binding_id, sequence, receipt_kind, receipt_wire,
                   current_binding_wire, previous_binding_wire,
                   last_activation_signer_generation, request_sha256,
                   signed_bom_wire
            FROM {_qualifiedJournal}
            WHERE device_binding_id = @device_binding_id
              AND sequence > @after_sequence
              AND sequence <= @snapshot_head
            ORDER BY sequence
            """,
            connection,
            transaction);
        deltaCommand.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        deltaCommand.Parameters.AddWithValue("after_sequence", afterSequence);
        deltaCommand.Parameters.AddWithValue("snapshot_head", head);
        var records = await ReadRecordsAsync(deltaCommand, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReleaseBindingJournalSnapshot(head, records);
    }

    private static async Task<IReadOnlyList<ReleaseBindingTruthRecord>> ReadRecordsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<ReleaseBindingTruthRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var deviceBindingId = reader.GetString(0);
            var sequence = reader.GetInt64(1);
            var receiptKind = reader.GetString(2);
            var receipt = ReleaseBindingReceiptV1Codec.Deserialize(
                reader.GetFieldValue<byte[]>(3));
            var current = ActiveReleaseBindingV1Codec.Deserialize(
                reader.GetFieldValue<byte[]>(4));
            var previous = reader.IsDBNull(5)
                ? null
                : ActiveReleaseBindingV1Codec.Deserialize(reader.GetFieldValue<byte[]>(5));
            if (receipt.Sequence != sequence
                || !string.Equals(receipt.ReceiptKind, receiptKind, StringComparison.Ordinal)
                || !string.Equals(receipt.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
            {
                throw new ActiveReleaseBindingException(
                    "truth store journal row identity does not match its receipt wire");
            }
            records.Add(new ReleaseBindingTruthRecord(
                deviceBindingId,
                receipt,
                current,
                previous,
                reader.GetInt64(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8)));
        }

        return records;
    }

    private async Task<ReleaseBindingRecoveryFence> IssueRecoveryFenceAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"""
            SELECT sequence, current_binding_wire
            FROM {_qualifiedJournal}
            WHERE device_binding_id = @device_binding_id
            ORDER BY sequence DESC
            LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ActiveReleaseBindingException(
                "recovery fence issuance requires an active release binding; the device has no journal");
        }

        var sequence = reader.GetInt64(0);
        var current = ActiveReleaseBindingV1Codec.Deserialize(reader.GetFieldValue<byte[]>(1));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new ActiveReleaseBindingException("release binding journal head query was ambiguous");
        }
        if (!string.Equals(current.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException("truth store record device identity fork");
        }
        if (!string.Equals(current.Status, "active", StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "recovery fence issuance requires an active release binding");
        }

        return new ReleaseBindingRecoveryFence(
            deviceBindingId,
            sequence,
            current.ReleaseBomSha256,
            current.Generation);
    }

    private async Task CommitRecoveryFenceAsync(
        ReleaseBindingRecoveryFence fence,
        Guid recoveryId,
        string recoveryContentSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ControlContractValidation.RequireDeviceBindingId(fence.DeviceBindingId);
        if (recoveryId == Guid.Empty)
        {
            throw new ActiveReleaseBindingException("recovery fence commit requires a recovery id");
        }
        if (string.IsNullOrEmpty(recoveryContentSha256) || recoveryContentSha256.Length != 64)
        {
            throw new ActiveReleaseBindingException(
                "recovery fence commit requires the recovery content digest");
        }

        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"SELECT {_qualifiedCommitFence}(@device_binding_id, @journal_sequence, "
            + "@release_bom_sha256, @release_bom_generation, @recovery_id, "
            + "@recovery_content_sha256)",
            connection);
        command.Parameters.AddWithValue("device_binding_id", fence.DeviceBindingId);
        command.Parameters.AddWithValue("journal_sequence", fence.JournalSequence);
        command.Parameters.AddWithValue("release_bom_sha256", fence.ReleaseBomSha256);
        command.Parameters.AddWithValue("release_bom_generation", fence.Generation);
        command.Parameters.AddWithValue("recovery_id", recoveryId);
        command.Parameters.AddWithValue("recovery_content_sha256", recoveryContentSha256);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.RaiseException
            && exception.MessageText.StartsWith(FenceConflictMessage, StringComparison.Ordinal))
        {
            throw new ReleaseBindingRecoveryFenceConflictException(
                "recovery fence commit conflict: the release binding revision advanced past the issued fence",
                exception);
        }
    }

    private async Task<NpgsqlConnection> OpenVerifiedAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await PostgresControlPlaneConnectionPolicy.RequirePostgres184Async(
                connection,
                _expectedDatabase,
                _expectedPort,
                cancellationToken);
            await PostgresControlPlaneConnectionPolicy.ConfigureAndVerifyTimeoutsAsync(
                connection,
                cancellationToken);
            await VerifyRuntimeSurfaceAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task VerifyRuntimeSurfaceAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var identityCommand = Command(
            """
            SELECT session_user::text = @runtime_role_name
                   AND current_user::text = @runtime_role_name,
                   EXISTS (
                       SELECT 1 FROM pg_catalog.pg_namespace n
                       WHERE n.nspname = @schema_name
                         AND pg_catalog.obj_description(n.oid, 'pg_namespace') = @schema_marker)
            """,
            connection))
        {
            identityCommand.Parameters.AddWithValue("runtime_role_name", _options.RuntimeRoleName);
            identityCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            identityCommand.Parameters.AddWithValue("schema_marker", SchemaMarker);
            await using var reader = await identityCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !reader.GetBoolean(0)
                || !reader.GetBoolean(1)
                || await reader.ReadAsync(cancellationToken))
            {
                throw new UnauthorizedAccessException(
                    "Release binding runtime connection identity or schema baseline marker is wrong.");
            }
        }

        var observedRelations = new HashSet<string>(StringComparer.Ordinal);
        await using (var inventoryCommand = Command(
            """
            SELECT c.relname::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema_name
              AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
            """,
            connection))
        {
            inventoryCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            await using var reader = await inventoryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                observedRelations.Add(reader.GetString(0));
            }
        }
        if (!observedRelations.SetEquals(TableNames))
        {
            throw new UnauthorizedAccessException(
                "Release binding schema contains an unregistered relation.");
        }

        var attestedTables = 0;
        await using (var tableCommand = Command(
            """
            SELECT c.relname::text,
                   c.relowner = owner.oid,
                   has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'SELECT'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'INSERT'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'UPDATE'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'DELETE'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'TRUNCATE'),
                   EXISTS (
                       SELECT 1 FROM pg_catalog.pg_trigger mutation_trigger
                       WHERE mutation_trigger.tgrelid = c.oid
                         AND NOT mutation_trigger.tgisinternal
                         AND mutation_trigger.tgname = c.relname || '_append_only_rows'
                         AND mutation_trigger.tgenabled = 'O'),
                   EXISTS (
                       SELECT 1 FROM pg_catalog.pg_trigger truncate_trigger
                       WHERE truncate_trigger.tgrelid = c.oid
                         AND NOT truncate_trigger.tgisinternal
                         AND truncate_trigger.tgname = c.relname || '_no_truncate'
                         AND truncate_trigger.tgenabled = 'O')
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_roles r ON r.rolname::text = session_user::text
            JOIN pg_catalog.pg_roles owner ON owner.rolname = @migration_role_name
            WHERE n.nspname = @schema_name
              AND c.relkind = 'r'
              AND c.relname = ANY(@table_names)
            """,
            connection))
        {
            tableCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            tableCommand.Parameters.AddWithValue("migration_role_name", _options.MigrationRoleName);
            tableCommand.Parameters.AddWithValue("table_names", TableNames);
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Enumerable.Range(1, 8).Any(index => !reader.GetBoolean(index)))
                {
                    throw new UnauthorizedAccessException(
                        "Release binding table ownership, ACL, or append-only trigger surface is unsafe.");
                }
                attestedTables++;
            }
        }
        if (attestedTables != TableNames.Length)
        {
            throw new UnauthorizedAccessException(
                "Release binding table attestation is incomplete.");
        }

        var attestedFunctions = 0;
        await using (var functionCommand = Command(
            """
            SELECT p.proname::text,
                   p.prosecdef,
                   p.proowner = owner.oid,
                   p.proconfig = ARRAY['search_path=pg_catalog']::text[]
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.rolname = @migration_role_name
            WHERE n.nspname = @schema_name
              AND p.proname IN (
                  'append_release_binding_record',
                  'commit_release_binding_recovery_fence')
            """,
            connection))
        {
            functionCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            functionCommand.Parameters.AddWithValue("migration_role_name", _options.MigrationRoleName);
            await using var reader = await functionCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(1) || !reader.GetBoolean(2) || !reader.GetBoolean(3))
                {
                    throw new UnauthorizedAccessException(
                        "Release binding commit function owner, mode, or search path is unsafe.");
                }
                attestedFunctions++;
            }
        }
        if (attestedFunctions != 2)
        {
            throw new UnauthorizedAccessException(
                "Release binding commit function attestation is incomplete or ambiguous.");
        }
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection)
        => Command(sql, connection, null);

    private static NpgsqlCommand Command(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction)
        => new(sql, connection, transaction)
        {
            CommandTimeout = PostgresControlPlaneConnectionPolicy.MaximumSeconds
        };
}

using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using Dps.MemoryEventLedger.Contracts;
using Npgsql;

namespace Dps.MemoryEventLedger;

public sealed record MemoryEventLedgerV2Options(
    string AdminConnectionString,
    string RuntimeConnectionString,
    string SchemaName,
    string AdminRole,
    string RuntimeRole,
    byte[] RuntimeCapability)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AdminConnectionString)) throw new ArgumentException("Admin connection string is required.", nameof(AdminConnectionString));
        if (string.IsNullOrWhiteSpace(RuntimeConnectionString)) throw new ArgumentException("Runtime connection string is required.", nameof(RuntimeConnectionString));
        RequireIdentifier(SchemaName, nameof(SchemaName)); RequireIdentifier(AdminRole, nameof(AdminRole)); RequireIdentifier(RuntimeRole, nameof(RuntimeRole));
        if (string.Equals(AdminRole, RuntimeRole, StringComparison.Ordinal)) throw new ArgumentException("Admin and runtime roles must differ.");
        ArgumentNullException.ThrowIfNull(RuntimeCapability);
        if (RuntimeCapability.Length != 32) throw new ArgumentException("Runtime capability must contain exactly 256 random bits.", nameof(RuntimeCapability));
    }

    public override string ToString() => $"{nameof(MemoryEventLedgerV2Options)} {{ Connections = [REDACTED], SchemaName = {SchemaName}, Roles = [REDACTED], RuntimeCapability = [REDACTED] }}";

    private static void RequireIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 63 || !char.IsAsciiLetterLower(value[0]) ||
            value.Any(static character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_'))
            throw new ArgumentException($"{name} must be a safe lowercase PostgreSQL identifier.", name);
    }
}

public sealed record StoredMemoryEventV2(
    long SoulSequence,
    MemoryEventV2 Event,
    string PayloadSha256,
    string PreviousChainSha256,
    string ChainSha256);

public sealed class PostgresMemoryEventLedgerV2 : IDisposable
{
    private const string MigrationV1 = "001_create_memory_event_ledger.sql";
    private const string MigrationV2 = "002_create_memory_event_ledger_v2.sql";
    private readonly MemoryEventLedgerV2Options _options;
    private readonly FixedSoulResolutionAuthorityV2 _soulAuthority;
    private readonly FixedObservationReceiptAuthorityV2 _observationAuthority;
    private readonly byte[] _runtimeCapability;
    private readonly ConditionalWeakTable<PreparedMemoryEventV2, PreparedSeal> _prepared = new();
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly AppendFaultInjector? _faultInjector;

    internal PostgresMemoryEventLedgerV2(
        MemoryEventLedgerV2Options options,
        FixedSoulResolutionAuthorityV2 soulAuthority,
        FixedObservationReceiptAuthorityV2 observationAuthority,
        AppendFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        ArgumentNullException.ThrowIfNull(soulAuthority); ArgumentNullException.ThrowIfNull(observationAuthority);
        _runtimeCapability = options.RuntimeCapability.ToArray();
        _options = options with { RuntimeCapability = Array.Empty<byte>() };
        _soulAuthority = soulAuthority; _observationAuthority = observationAuthority; _faultInjector = faultInjector;
    }

    public static PostgresMemoryEventLedgerV2 CreateProduction(MemoryEventLedgerV2Options options) =>
        throw new InvalidOperationException(
            "WAITING_EXTERNAL: soul-registry does not yet expose a fixed-authority current-resolution capability and no signed Release BOM receipt root is pinned. Caller-selected roots are forbidden.");

    public async Task<PreparedMemoryEventV2> PrepareAsync(
        MemoryAppendRequestV2 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var raw = Convert.FromBase64String(request.SignedReceiptCanonicalBase64);
        try
        {
            return await PrepareBoundAsync(
                new SoulResolutionBindingRequestV2(
                    request.EventId, request.SoulId, request.DeviceBindingId, request.PlatformAccountId,
                    request.TraceId, request.IdempotencyKey, request.OccurredAt),
                raw,
                request.InterestSignals,
                cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    internal async Task<PreparedMemoryEventV2> PrepareBoundAsync(
        SoulResolutionBindingRequestV2 soulRequest,
        ReadOnlyMemory<byte> signedReceiptRaw,
        IReadOnlyList<InterestSignalV2> interestSignals,
        CancellationToken cancellationToken = default)
    {
        var soulCapability = await _soulAuthority.IssueAsync(soulRequest, cancellationToken);
        var observationCapability = _observationAuthority.Issue(soulRequest.EventId, signedReceiptRaw, interestSignals);
        var soulSeal = await _soulAuthority.RevalidateAsync(soulCapability, cancellationToken);
        var observationSeal = _observationAuthority.Revalidate(observationCapability);
        EnsureSameScope(soulSeal.Request, observationSeal);

        var memoryEvent = new MemoryEventV2(
            MemoryEventV2.CurrentSchemaVersion, MemoryEventV2.CurrentContractId, MemoryEventV2.CurrentProducerModule,
            soulRequest.EventId, soulRequest.SoulId, soulRequest.DeviceBindingId, soulRequest.PlatformAccountId,
            soulRequest.TraceId, soulRequest.IdempotencyKey, observationSeal.OccurredAt, "personal",
            MemoryEventV2.ObservedContentEventType,
            new MemoryObservationV2(
                observationSeal.ContentDigest, observationSeal.SignalsDigest, observationSeal.SignedReceiptSha256,
                observationSeal.ReceiptId, observationSeal.CommandId, observationSeal.Signals),
            soulSeal.ToAudit(), observationSeal.ToAudit());
        memoryEvent.Validate();
        var prepared = new PreparedMemoryEventV2(memoryEvent);
        _prepared.Add(prepared, new PreparedSeal(
            _instanceId, soulCapability, observationCapability, MemoryEventCanonicalizerV2.ComputeSha256(memoryEvent)));
        return prepared;
    }

    public async Task<AppendResult> AppendAsync(PreparedMemoryEventV2 prepared, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!_prepared.TryGetValue(prepared, out var preparedSeal) || preparedSeal.InstanceId != _instanceId)
            throw new MemoryCapabilityException("Prepared event was not created by this exact ledger composition.");
        var soulSeal = await _soulAuthority.RevalidateAsync(preparedSeal.SoulCapability, cancellationToken);
        var observationSeal = _observationAuthority.Revalidate(preparedSeal.ObservationCapability);
        EnsureSameScope(soulSeal.Request, observationSeal);
        var payloadSha256 = MemoryEventCanonicalizerV2.ComputeSha256(prepared.Event);
        if (!MemoryContractValidationV2.FixedTimeEquals(payloadSha256, preparedSeal.EventSha256))
            throw new MemoryCapabilityException("Prepared event bytes changed after authority binding.");

        var canonicalJson = MemoryEventCanonicalizerV2.Serialize(prepared.Event);
        var outboxId = CreateDeterministicGuid(prepared.Event.EventId, MemoryOutboxV2.CurrentContractId);
        await using var connection = new NpgsqlConnection(_options.RuntimeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InjectAsync(AppendStage.EventInserted, cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT disposition, soul_sequence, outbox_id FROM {_options.SchemaName}.append_memory_event_v2(@capability, @event_id, @outbox_id, @canonical_json, @payload_sha256)",
            connection, transaction);
        command.Parameters.AddWithValue("capability", Convert.ToHexStringLower(_runtimeCapability));
        command.Parameters.AddWithValue("event_id", prepared.Event.EventId);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("canonical_json", canonicalJson);
        command.Parameters.AddWithValue("payload_sha256", payloadSha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("PostgreSQL append function returned no result.");
        var dispositionText = reader.GetString(0);
        var sequence = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var returnedOutbox = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
        await reader.DisposeAsync();
        await InjectAsync(AppendStage.OutboxInserted, cancellationToken);
        await InjectAsync(AppendStage.BeforeCommit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var disposition = dispositionText switch
        {
            "INSERTED" => AppendDisposition.Inserted,
            "DUPLICATE_NO_OP" => AppendDisposition.DuplicateNoOp,
            "QUARANTINED" => AppendDisposition.Quarantined,
            _ => throw new InvalidOperationException("PostgreSQL returned an unknown append disposition.")
        };
        return new AppendResult(disposition, prepared.Event.EventId, payloadSha256, sequence, returnedOutbox);
    }

    public async Task<IReadOnlyList<StoredMemoryEventV2>> ReadSoulEventsAsync(
        string soulId,
        CancellationToken cancellationToken = default)
    {
        MemoryContractValidationV2.RequireSoulId(soulId, nameof(soulId));
        await using var connection = new NpgsqlConnection(_options.RuntimeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT soul_sequence, canonical_json, payload_sha256, previous_chain_sha256, chain_sha256 FROM {_options.SchemaName}.read_soul_events_v2(@capability, @soul_id)",
            connection);
        command.Parameters.AddWithValue("capability", Convert.ToHexStringLower(_runtimeCapability));
        command.Parameters.AddWithValue("soul_id", soulId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<StoredMemoryEventV2>();
        var expectedSequence = 1L;
        var expectedPrevious = new string('0', 64);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sequence = reader.GetInt64(0);
            var canonicalJson = reader.GetString(1);
            var payloadSha256 = reader.GetString(2);
            var previousChainSha256 = reader.GetString(3);
            var chainSha256 = reader.GetString(4);
            MemoryContractValidationV2.RequireSha256(payloadSha256, nameof(payloadSha256));
            MemoryContractValidationV2.RequireSha256(previousChainSha256, nameof(previousChainSha256));
            MemoryContractValidationV2.RequireSha256(chainSha256, nameof(chainSha256));
            if (sequence != expectedSequence ||
                !MemoryContractValidationV2.FixedTimeEquals(previousChainSha256, expectedPrevious))
                throw new MemoryCapabilityException("Stored Soul sequence or previous-chain hash is discontinuous.");

            var raw = new UTF8Encoding(false, true).GetBytes(canonicalJson);
            MemoryEventV2 memoryEvent;
            try { memoryEvent = MemoryEventCanonicalizerV2.DeserializeExact(raw); }
            finally { CryptographicOperations.ZeroMemory(raw); }
            if (!string.Equals(memoryEvent.SoulId, soulId, StringComparison.Ordinal) ||
                !MemoryContractValidationV2.FixedTimeEquals(MemoryEventCanonicalizerV2.ComputeSha256(memoryEvent), payloadSha256))
                throw new MemoryCapabilityException("Stored event scope or canonical payload hash is invalid.");

            var expectedChain = ComputeChainSha256(previousChainSha256, sequence, payloadSha256);
            if (!MemoryContractValidationV2.FixedTimeEquals(expectedChain, chainSha256))
                throw new MemoryCapabilityException("Stored Soul chain hash is invalid.");
            events.Add(new StoredMemoryEventV2(sequence, memoryEvent, payloadSha256, previousChainSha256, chainSha256));
            expectedSequence++;
            expectedPrevious = chainSha256;
        }
        return events.AsReadOnly();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(PostgresMemoryEventLedgerV2).Assembly;
        await using var connection = new NpgsqlConnection(_options.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var suffix in new[] { MigrationV1, MigrationV2 })
        {
            var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Migration {suffix} missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var sql = await reader.ReadToEndAsync(cancellationToken);
            sql = sql.Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal)
                .Replace("__ADMIN_ROLE__", _options.AdminRole, StringComparison.Ordinal)
                .Replace("__RUNTIME_ROLE__", _options.RuntimeRole, StringComparison.Ordinal);
            await using var command = new NpgsqlCommand(sql, connection);
            if (suffix == MigrationV2)
            {
                var capabilityText = Convert.ToHexStringLower(_runtimeCapability);
                command.Parameters.AddWithValue(
                    "runtime_capability_sha256",
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capabilityText))));
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void EnsureSameScope(
        SoulResolutionBindingRequestV2 soul,
        FixedObservationReceiptAuthorityV2.ObservationSeal observation)
    {
        if (soul.EventId != observation.EventId ||
            !string.Equals(soul.SoulId, observation.SoulId, StringComparison.Ordinal) ||
            !string.Equals(soul.DeviceBindingId, observation.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(soul.PlatformAccountId, observation.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(soul.TraceId, observation.TraceId, StringComparison.Ordinal) ||
            !string.Equals(soul.IdempotencyKey, observation.IdempotencyKey, StringComparison.Ordinal) ||
            soul.OccurredAt != observation.OccurredAt)
            throw new MemoryCapabilityException("Soul and observation authorities do not bind the same event, Soul, device, account, trace, and idempotency scope.");
    }

    private async ValueTask InjectAsync(AppendStage stage, CancellationToken cancellationToken)
    {
        if (_faultInjector is not null) await _faultInjector(stage, cancellationToken);
    }

    private static Guid CreateDeterministicGuid(Guid input, string purpose)
    {
        var combined = input.ToByteArray().Concat(Encoding.UTF8.GetBytes(purpose)).ToArray();
        var hash = SHA256.HashData(combined); hash[6] = (byte)((hash[6] & 0x0f) | 0x50); hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string ComputeChainSha256(string previous, long sequence, string payloadSha256)
    {
        var value = string.Concat(
            previous, ":", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", payloadSha256);
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_runtimeCapability);
        _observationAuthority.Dispose();
    }

    private sealed record PreparedSeal(
        Guid InstanceId,
        VerifiedSoulResolutionCapabilityV2 SoulCapability,
        VerifiedObservationReceiptCapabilityV2 ObservationCapability,
        string EventSha256);
}

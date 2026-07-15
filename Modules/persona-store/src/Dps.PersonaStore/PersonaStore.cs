using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.Binding.Contracts;
using Dps.PersonaStore.Contracts;

namespace Dps.PersonaStore;

public sealed record PutPersonaCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    IReadOnlyDictionary<string, string> Traits,
    IReadOnlyCollection<string> EvidenceSha256,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record DeletePersonaCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    IReadOnlyCollection<string> EvidenceSha256,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record ExportPersonaHistoryCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public interface IPersonaStore
{
    ValueTask<PersonaRevisionV1> PutAsync(PutPersonaCommand command, CancellationToken cancellationToken = default);
    ValueTask<PersonaRevisionV1> DeleteAsync(DeletePersonaCommand command, CancellationToken cancellationToken = default);
    ValueTask<PersonaRevisionV1> GetCurrentAsync(string soulId, string deviceBindingId, string platformAccountId, CancellationToken cancellationToken = default);
    ValueTask<PersonaHistoryExportV1> ExportHistoryV1Async(ExportPersonaHistoryCommand command, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PersonaRevisionV1>> ReadHistoryAsync(string soulId, string deviceBindingId, string platformAccountId, CancellationToken cancellationToken = default);
}

internal sealed class InMemoryPersonaStore : IPersonaStore
{
    private readonly IBindingMutationFenceClient _bindingFenceClient;
    private readonly byte[] _requestHmacKey;
    private readonly object _gate = new();
    private readonly Dictionary<string, PersonaSnapshot> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string RequestSha256, PersonaRevisionV1 Result)> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string RequestSha256, PersonaHistoryExportV1 Result)> _exportIdempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PersonaRevisionV1>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<long, IReadOnlyDictionary<string, string>>> _historyTraits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _soulHmacKeys = new(StringComparer.Ordinal);

    internal InMemoryPersonaStore(IBindingMutationFenceClient bindingFenceClient, byte[] requestHmacKey)
    {
        _bindingFenceClient = bindingFenceClient ?? throw new ArgumentNullException(nameof(bindingFenceClient));
        ArgumentNullException.ThrowIfNull(requestHmacKey);
        if (requestHmacKey.Length != 32) throw new ArgumentException("The request-HMAC key must contain 32 bytes.", nameof(requestHmacKey));
        _requestHmacKey = requestHmacKey.ToArray();
    }

    public async ValueTask<PersonaRevisionV1> PutAsync(PutPersonaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = PersonaMutationCanonicalizer.Normalize(command);
        var requestHash = PersonaMutationCanonicalizer.HashPut(normalized, _requestHmacKey);
        lock (_gate)
        {
            if (_idempotency.TryGetValue(normalized.IdempotencyKey, out var prior))
            {
                PersonaMutationCanonicalizer.EnsureSameRequest(prior.RequestSha256, requestHash);
                return prior.Result.ImmutableCopy();
            }
        }
        await using var bindingFence = await PersonaBindingFence.AcquireAsync(
            _bindingFenceClient,
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.TraceId,
            normalized.IdempotencyKey,
            normalized.OccurredAt,
            cancellationToken);

        lock (_gate)
        {
            if (_idempotency.TryGetValue(normalized.IdempotencyKey, out var prior))
            {
                PersonaMutationCanonicalizer.EnsureSameRequest(prior.RequestSha256, requestHash);
                return prior.Result.ImmutableCopy();
            }

            var currentRevision = _current.TryGetValue(normalized.SoulId, out var current) ? current.Contract.PersonaRevision : 0;
            if (currentRevision != normalized.ExpectedRevision)
                throw new PersonaRevisionConflictException(normalized.ExpectedRevision, currentRevision);
            if (current is not null)
            {
                PersonaMutationCanonicalizer.EnsureScope(current.Contract, normalized.SoulId, normalized.DeviceBindingId, normalized.PlatformAccountId);
                if (current.Contract.Status == "deleted")
                    throw new InvalidOperationException("A deleted persona cannot be reactivated.");
            }

            if (!_soulHmacKeys.TryGetValue(normalized.SoulId, out var soulHmacKey))
            {
                soulHmacKey = PersonaMutationCanonicalizer.DeriveTestSoulHmacKey(_requestHmacKey, normalized.SoulId);
                _soulHmacKeys.Add(normalized.SoulId, soulHmacKey);
            }
            var result = PersonaMutationCanonicalizer.Create(
                normalized.SoulId,
                normalized.DeviceBindingId,
                normalized.PlatformAccountId,
                currentRevision + 1,
                PersonaMutationCanonicalizer.HashTraits(normalized.Traits, soulHmacKey),
                normalized.Traits.Keys.ToArray(),
                normalized.EvidenceSha256,
                "active",
                normalized.TraceId,
                normalized.IdempotencyKey,
                normalized.OccurredAt);
            _current[normalized.SoulId] = new PersonaSnapshot(result);
            _idempotency.Add(normalized.IdempotencyKey, (requestHash, result));
            if (!_history.TryGetValue(normalized.SoulId, out var history))
            {
                history = [];
                _history.Add(normalized.SoulId, history);
            }
            if (!_historyTraits.TryGetValue(normalized.SoulId, out var historyTraits))
            {
                historyTraits = [];
                _historyTraits.Add(normalized.SoulId, historyTraits);
            }
            history.Add(result);
            historyTraits.Add(result.PersonaRevision, PersonaTraitVocabularyV1.ValidateAndFreeze(normalized.Traits));
            return result.ImmutableCopy();
        }
    }

    public async ValueTask<PersonaRevisionV1> DeleteAsync(DeletePersonaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = PersonaMutationCanonicalizer.Normalize(command);
        var requestHash = PersonaMutationCanonicalizer.HashDelete(normalized, _requestHmacKey);
        lock (_gate)
        {
            if (_idempotency.TryGetValue(normalized.IdempotencyKey, out var prior))
            {
                PersonaMutationCanonicalizer.EnsureSameRequest(prior.RequestSha256, requestHash);
                return prior.Result.ImmutableCopy();
            }
        }
        await using var bindingFence = await PersonaBindingFence.AcquireAsync(
            _bindingFenceClient,
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.TraceId,
            normalized.IdempotencyKey,
            normalized.OccurredAt,
            cancellationToken);

        lock (_gate)
        {
            if (_idempotency.TryGetValue(normalized.IdempotencyKey, out var prior))
            {
                PersonaMutationCanonicalizer.EnsureSameRequest(prior.RequestSha256, requestHash);
                return prior.Result.ImmutableCopy();
            }

            if (!_current.TryGetValue(normalized.SoulId, out var current))
                throw new KeyNotFoundException("Unknown persona.");
            PersonaMutationCanonicalizer.EnsureScope(current.Contract, normalized.SoulId, normalized.DeviceBindingId, normalized.PlatformAccountId);
            if (current.Contract.PersonaRevision != normalized.ExpectedRevision)
                throw new PersonaRevisionConflictException(normalized.ExpectedRevision, current.Contract.PersonaRevision);
            if (current.Contract.Status == "deleted")
                throw new InvalidOperationException("Persona is already deleted.");

            var result = PersonaMutationCanonicalizer.Create(
                normalized.SoulId,
                normalized.DeviceBindingId,
                normalized.PlatformAccountId,
                current.Contract.PersonaRevision + 1,
                PersonaMutationCanonicalizer.DeletedTraitsSha256,
                [],
                normalized.EvidenceSha256,
                "deleted",
                normalized.TraceId,
                normalized.IdempotencyKey,
                normalized.OccurredAt);
            _current[normalized.SoulId] = new PersonaSnapshot(result);
            if (_soulHmacKeys.Remove(normalized.SoulId, out var erasedKey)) CryptographicOperations.ZeroMemory(erasedKey);
            _historyTraits.Remove(normalized.SoulId);
            _idempotency.Add(normalized.IdempotencyKey, (requestHash, result));
            _history[normalized.SoulId].Add(result);
            return result.ImmutableCopy();
        }
    }

    public ValueTask<PersonaRevisionV1> GetCurrentAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PersonaMutationCanonicalizer.ValidateScope(soulId, deviceBindingId, platformAccountId);
        lock (_gate)
        {
            if (!_current.TryGetValue(soulId, out var current)) throw new KeyNotFoundException("Unknown persona.");
            PersonaMutationCanonicalizer.EnsureScope(current.Contract, soulId, deviceBindingId, platformAccountId);
            return ValueTask.FromResult(current.Contract.ImmutableCopy());
        }
    }

    public ValueTask<PersonaHistoryExportV1> ExportHistoryV1Async(
        ExportPersonaHistoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = PersonaMutationCanonicalizer.Normalize(command);
        var requestHash = PersonaMutationCanonicalizer.HashExportRequest(normalized, _requestHmacKey);
        lock (_gate)
        {
            if (_exportIdempotency.TryGetValue(normalized.IdempotencyKey, out var prior))
            {
                PersonaMutationCanonicalizer.EnsureSameRequest(prior.RequestSha256, requestHash);
                PersonaMutationCanonicalizer.VerifyExportProof(prior.Result, normalized, _requestHmacKey);
                return ValueTask.FromResult(prior.Result.ImmutableCopy());
            }
            if (!_current.TryGetValue(normalized.SoulId, out var currentSnapshot))
                throw new KeyNotFoundException("Unknown persona.");
            var current = currentSnapshot.Contract;
            PersonaMutationCanonicalizer.EnsureScope(
                current,
                normalized.SoulId,
                normalized.DeviceBindingId,
                normalized.PlatformAccountId);
            _historyTraits.TryGetValue(normalized.SoulId, out var retainedTraits);
            if (current.Status == "deleted" && retainedTraits is not null)
                throw new InvalidDataException("A logically deleted persona still has retained live-primary trait payloads.");
            if (_history[normalized.SoulId].Count > 10_000)
                throw new InvalidDataException("Persona history export exceeds the v1 10,000-revision ceiling.");
            _soulHmacKeys.TryGetValue(normalized.SoulId, out var soulHmacKey);
            if (current.Status == "active" && soulHmacKey is null)
                throw new InvalidDataException("An active persona is missing its per-Soul HMAC key.");

            var revisions = _history[normalized.SoulId].Select(revision =>
            {
                IReadOnlyDictionary<string, string>? traits = null;
                if (retainedTraits is not null)
                    retainedTraits.TryGetValue(revision.PersonaRevision, out traits);
                if (current.Status == "active" && revision.Status == "active" && traits is null)
                    throw new InvalidDataException("An active persona history revision is missing its retained trait payload.");
                if (revision.Status == "deleted" && traits is not null)
                    throw new InvalidDataException("A deleted persona revision unexpectedly has a retained trait payload.");
                if (traits is not null && (soulHmacKey is null || !PersonaMutationCanonicalizer.FixedTimeSha256Equals(
                        PersonaMutationCanonicalizer.HashTraits(traits, soulHmacKey),
                        revision.TraitsSha256)))
                    throw new InvalidDataException("A retained persona history payload keyed checksum does not match its revision.");
                return new PersonaHistoryExportItemV1(
                    revision.ImmutableCopy(),
                    traits is null ? PersonaHistoryExportItemV1.LivePrimaryLogicallyDeleted : PersonaHistoryExportItemV1.Retained,
                    traits is null ? null : PersonaTraitVocabularyV1.ValidateAndFreeze(traits));
            }).ToArray();

            var payloadState = current.Status == "deleted"
                ? PersonaHistoryExportItemV1.LivePrimaryLogicallyDeleted
                : PersonaHistoryExportItemV1.Retained;
            var result = PersonaMutationCanonicalizer.CreateHistoryExport(
                normalized,
                payloadState,
                Array.AsReadOnly(revisions),
                _requestHmacKey);
            _exportIdempotency.Add(normalized.IdempotencyKey, (requestHash, result));
            return ValueTask.FromResult(result.ImmutableCopy());
        }
    }

    public async ValueTask<IReadOnlyList<PersonaRevisionV1>> ReadHistoryAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetCurrentAsync(soulId, deviceBindingId, platformAccountId, cancellationToken);
        lock (_gate)
        {
            return _history[soulId].Select(static revision => revision.ImmutableCopy()).ToArray();
        }
    }

    private sealed record PersonaSnapshot(PersonaRevisionV1 Contract);
}

internal static class PersonaBindingFence
{
    public static async Task<IBindingMutationFenceLease> AcquireAsync(
        IBindingMutationFenceClient client,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var lease = await client.AcquireAsync(new AcquireBindingMutationFenceCommand(
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey,
            occurredAt), cancellationToken);
        if (lease is null) throw new InvalidOperationException("Binding returned no mutation-fence lease.");
        try
        {
            var receipt = lease.Receipt
                ?? throw new InvalidOperationException("Binding mutation-fence lease returned no receipt.");
            receipt.Validate();
            if (receipt.SoulId != soulId ||
                receipt.DeviceBindingId != deviceBindingId ||
                receipt.PlatformAccountId != platformAccountId ||
                receipt.TraceId != traceId ||
                receipt.IdempotencyKey != idempotencyKey ||
                receipt.OccurredAt != occurredAt)
            {
                throw new UnauthorizedAccessException("Binding mutation-fence receipt does not match the exact persona mutation scope.");
            }
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }
}

internal sealed record NormalizedPutPersonaCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    SortedDictionary<string, string> Traits,
    string[] EvidenceSha256,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

internal sealed record NormalizedDeletePersonaCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    string[] EvidenceSha256,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

internal sealed record NormalizedExportPersonaHistoryCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

internal static class PersonaMutationCanonicalizer
{
    private const string TraitsHashDomain = "dps.persona-store.traits-hmac-sha256/v1";
    private const string PutHashDomain = "dps.persona-store.put-request-hmac-sha256/v1";
    private const string DeleteHashDomain = "dps.persona-store.delete-request-hmac-sha256/v1";
    private const string ExportRequestHashDomain = "dps.persona-store.history-export-request-hmac-sha256/v1";
    private const string ExportCursorHashDomain = "dps.persona-store.history-export-cursor-hmac-sha256/v1";
    private const string ExportReceiptHashDomain = "dps.persona-store.history-export-receipt-hmac-sha256/v1";
    private const string DeletedTraitsHashDomain = "dps.persona-store.deleted-traits-sha256/v1";
    public static readonly string DeletedTraitsSha256 = HashSha256(writer => writer.WriteText(DeletedTraitsHashDomain));

    public static NormalizedPutPersonaCommand Normalize(PutPersonaCommand command)
    {
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        ValidateExpectedRevision(command.ExpectedRevision);
        var traits = NormalizeTraits(command.Traits);
        return new NormalizedPutPersonaCommand(
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.ExpectedRevision,
            traits,
            NormalizeEvidence(command.EvidenceSha256),
            ValidateTrace(command.TraceId),
            ValidateIdempotencyKey(command.IdempotencyKey),
            ValidateOccurredAt(command.OccurredAt));
    }

    public static NormalizedDeletePersonaCommand Normalize(DeletePersonaCommand command)
    {
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        ValidateExpectedRevision(command.ExpectedRevision);
        return new NormalizedDeletePersonaCommand(
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.ExpectedRevision,
            NormalizeEvidence(command.EvidenceSha256),
            ValidateTrace(command.TraceId),
            ValidateIdempotencyKey(command.IdempotencyKey),
            ValidateOccurredAt(command.OccurredAt));
    }

    public static NormalizedExportPersonaHistoryCommand Normalize(ExportPersonaHistoryCommand command)
    {
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        return new NormalizedExportPersonaHistoryCommand(
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            ValidateTrace(command.TraceId),
            ValidateIdempotencyKey(command.IdempotencyKey),
            ValidateOccurredAt(command.OccurredAt));
    }

    public static void ValidateScope(string soulId, string deviceBindingId, string platformAccountId)
    {
        PersonaContractValidation.RequireSoulId(soulId);
        PersonaContractValidation.RequireDeviceBindingId(deviceBindingId);
        PersonaContractValidation.RequirePlatformAccountId(platformAccountId);
    }

    public static void EnsureScope(PersonaRevisionV1 current, string soulId, string deviceBindingId, string platformAccountId)
    {
        if (current.SoulId != soulId || current.DeviceBindingId != deviceBindingId || current.PlatformAccountId != platformAccountId)
            throw new UnauthorizedAccessException("Persona scope mismatch.");
    }

    public static void EnsureSameRequest(string actual, string expected)
    {
        if (!FixedTimeSha256Equals(actual, expected)) throw new PersonaIdempotencyConflictException();
    }

    public static string HashPut(NormalizedPutPersonaCommand command, byte[] requestHmacKey) => HashHmac(requestHmacKey, writer =>
    {
        writer.WriteText(PutHashDomain);
        writer.WriteText(command.SoulId);
        writer.WriteText(command.DeviceBindingId);
        writer.WriteText(command.PlatformAccountId);
        writer.WriteInt64(command.ExpectedRevision);
        writer.WriteInt32(command.Traits.Count);
        foreach (var pair in command.Traits)
        {
            writer.WriteText(pair.Key);
            writer.WriteText(pair.Value);
        }
        WriteEvidence(writer, command.EvidenceSha256);
        writer.WriteText(command.TraceId);
        writer.WriteText(command.IdempotencyKey);
        writer.WriteText(command.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    });

    public static string HashDelete(NormalizedDeletePersonaCommand command, byte[] requestHmacKey) => HashHmac(requestHmacKey, writer =>
    {
        writer.WriteText(DeleteHashDomain);
        writer.WriteText(command.SoulId);
        writer.WriteText(command.DeviceBindingId);
        writer.WriteText(command.PlatformAccountId);
        writer.WriteInt64(command.ExpectedRevision);
        WriteEvidence(writer, command.EvidenceSha256);
        writer.WriteText(command.TraceId);
        writer.WriteText(command.IdempotencyKey);
        writer.WriteText(command.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    });

    public static string HashExportRequest(NormalizedExportPersonaHistoryCommand command, byte[] requestHmacKey) =>
        HashHmac(requestHmacKey, writer =>
        {
            writer.WriteText(ExportRequestHashDomain);
            WriteExportRequest(writer, command);
        });

    public static string HashExportCursor(
        NormalizedExportPersonaHistoryCommand command,
        long snapshotPersonaRevision,
        byte[] requestHmacKey) => HashHmac(requestHmacKey, writer =>
    {
        writer.WriteText(ExportCursorHashDomain);
        writer.WriteText(command.SoulId);
        writer.WriteText(command.DeviceBindingId);
        writer.WriteText(command.PlatformAccountId);
        writer.WriteInt64(snapshotPersonaRevision);
    });

    public static string HashExportReceipt(
        string exportRequestHmacSha256,
        long snapshotPersonaRevision,
        string snapshotCursorHmacSha256,
        string exportPayloadSha256,
        byte[] requestHmacKey) => HashHmac(requestHmacKey, writer =>
    {
        writer.WriteText(ExportReceiptHashDomain);
        writer.WriteText(exportRequestHmacSha256);
        writer.WriteInt64(snapshotPersonaRevision);
        writer.WriteText(snapshotCursorHmacSha256);
        writer.WriteText(exportPayloadSha256);
    });

    public static PersonaHistoryExportV1 CreateHistoryExport(
        NormalizedExportPersonaHistoryCommand command,
        string payloadState,
        IReadOnlyList<PersonaHistoryExportItemV1> revisions,
        byte[] requestHmacKey)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if (revisions.Count == 0) throw new InvalidDataException("Persona history is missing.");
        var snapshotRevision = revisions[^1].Revision.PersonaRevision;
        var placeholder = new string('0', 64);
        var value = new PersonaHistoryExportV1(
            PersonaHistoryExportV1.CurrentSchemaVersion,
            PersonaHistoryExportV1.CurrentContractId,
            PersonaHistoryExportV1.CurrentProducerModule,
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt,
            "sensitive",
            payloadState,
            snapshotRevision,
            placeholder,
            placeholder,
            placeholder,
            placeholder,
            "pexport_" + placeholder,
            revisions);
        var exportPayloadSha256 = PersonaHistoryExportIntegrity.ComputePayloadSha256(value);
        var exportRequestHmacSha256 = HashExportRequest(command, requestHmacKey);
        var cursorHmacSha256 = HashExportCursor(command, snapshotRevision, requestHmacKey);
        var receiptHmacSha256 = HashExportReceipt(
            exportRequestHmacSha256,
            snapshotRevision,
            cursorHmacSha256,
            exportPayloadSha256,
            requestHmacKey);
        return (value with
        {
            SnapshotCursorHmacSha256 = cursorHmacSha256,
            ExportRequestHmacSha256 = exportRequestHmacSha256,
            ExportPayloadSha256 = exportPayloadSha256,
            ExportReceiptHmacSha256 = receiptHmacSha256,
            ExportReceiptId = "pexport_" + receiptHmacSha256
        }).ImmutableCopy();
    }

    public static void VerifyExportProof(
        PersonaHistoryExportV1 value,
        NormalizedExportPersonaHistoryCommand command,
        byte[] requestHmacKey)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        if (value.SoulId != command.SoulId || value.DeviceBindingId != command.DeviceBindingId ||
            value.PlatformAccountId != command.PlatformAccountId || value.TraceId != command.TraceId ||
            value.IdempotencyKey != command.IdempotencyKey || value.OccurredAt != command.OccurredAt)
            throw new UnauthorizedAccessException("Persona history export receipt does not match the exact request envelope.");
        var requestHmac = HashExportRequest(command, requestHmacKey);
        var cursorHmac = HashExportCursor(command, value.SnapshotPersonaRevision, requestHmacKey);
        var receiptHmac = HashExportReceipt(
            requestHmac,
            value.SnapshotPersonaRevision,
            cursorHmac,
            value.ExportPayloadSha256,
            requestHmacKey);
        if (!FixedTimeSha256Equals(requestHmac, value.ExportRequestHmacSha256) ||
            !FixedTimeSha256Equals(cursorHmac, value.SnapshotCursorHmacSha256) ||
            !FixedTimeSha256Equals(receiptHmac, value.ExportReceiptHmacSha256) ||
            !string.Equals(value.ExportReceiptId, "pexport_" + receiptHmac, StringComparison.Ordinal))
            throw new InvalidDataException("Persona history export receipt proof is invalid.");
    }

    public static bool FixedTimeSha256Equals(string actual, string expected) =>
        PersonaHistoryExportIntegrity.FixedTimeSha256Equals(actual, expected);

    public static string HashTraits(IReadOnlyDictionary<string, string> traits, byte[] soulHmacKey) => HashHmac(soulHmacKey, writer =>
    {
        writer.WriteText(TraitsHashDomain);
        writer.WriteInt32(traits.Count);
        foreach (var pair in traits.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteText(pair.Key);
            writer.WriteText(pair.Value);
        }
    });

    public static SortedDictionary<string, string> ValidateStoredTraits(IReadOnlyDictionary<string, string> traits)
    {
        try
        {
            return new SortedDictionary<string, string>(
                PersonaTraitVocabularyV1.ValidateAndFreeze(traits).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Stored Persona traits violate the closed v1 vocabulary.", exception);
        }
    }

    public static byte[] DeriveTestSoulHmacKey(byte[] requestHmacKey, string soulId)
    {
        var bytes = Encoding.UTF8.GetBytes("dps.persona-store.test-soul-key/v1\0" + soulId);
        try { return HMACSHA256.HashData(RequireHmacKey(requestHmacKey), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string HashUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static Guid DeterministicOutboxId(string soulId, long revision)
    {
        var digest = Convert.FromHexString(HashUtf8($"dps.persona-store.outbox-id/v1\0{soulId}\0{revision}"));
        try
        {
            Span<byte> value = stackalloc byte[16];
            digest.AsSpan(0, 16).CopyTo(value);
            value[6] = (byte)((value[6] & 0x0f) | 0x50);
            value[8] = (byte)((value[8] & 0x3f) | 0x80);
            return new Guid(value, bigEndian: true);
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public static PersonaRevisionV1 Create(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long revision,
        string digest,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> evidence,
        string status,
        string trace,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        var value = new PersonaRevisionV1(
            PersonaRevisionV1.CurrentSchemaVersion,
            PersonaRevisionV1.CurrentContractId,
            PersonaRevisionV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            trace,
            idempotencyKey,
            occurredAt,
            "personal",
            revision,
            digest,
            keys,
            evidence,
            status);
        return value.ImmutableCopy();
    }

    private static SortedDictionary<string, string> NormalizeTraits(IReadOnlyDictionary<string, string> input)
    {
        try
        {
            return new SortedDictionary<string, string>(
                PersonaTraitVocabularyV1.ValidateAndFreeze(input).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Unknown persona trait key or value.", nameof(input), exception);
        }
    }

    private static string[] NormalizeEvidence(IReadOnlyCollection<string> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = input.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (result.Length is < 1 or > 64) throw new ArgumentException("Between one and 64 evidence hashes are required.", nameof(input));
        foreach (var hash in result) PersonaContractValidation.RequireSha256(hash, nameof(input));
        return result;
    }

    private static void ValidateExpectedRevision(long revision)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
    }

    private static string ValidateTrace(string value)
    {
        PersonaContractValidation.RequireTraceId(value);
        return value;
    }

    private static string ValidateIdempotencyKey(string value)
    {
        PersonaContractValidation.RequireIdempotencyKey(value);
        return value;
    }

    private static DateTimeOffset ValidateOccurredAt(DateTimeOffset value)
    {
        PersonaContractValidation.RequireOccurredAt(value, nameof(value));
        return value;
    }

    private static void WriteEvidence(CanonicalWriter writer, IReadOnlyList<string> evidence)
    {
        writer.WriteInt32(evidence.Count);
        foreach (var digest in evidence) writer.WriteText(digest);
    }

    private static void WriteExportRequest(CanonicalWriter writer, NormalizedExportPersonaHistoryCommand command)
    {
        writer.WriteText(command.SoulId);
        writer.WriteText(command.DeviceBindingId);
        writer.WriteText(command.PlatformAccountId);
        writer.WriteText(command.TraceId);
        writer.WriteText(command.IdempotencyKey);
        writer.WriteText(command.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string HashSha256(Action<CanonicalWriter> write)
    {
        using var stream = new MemoryStream();
        var writer = new CanonicalWriter(stream);
        write(writer);
        var bytes = stream.ToArray();
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string HashHmac(byte[] key, Action<CanonicalWriter> write)
    {
        using var stream = new MemoryStream();
        var writer = new CanonicalWriter(stream);
        write(writer);
        var bytes = stream.ToArray();
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(RequireHmacKey(key), bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] RequireHmacKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("Persona HMAC keys must contain exactly 32 bytes.", nameof(key));
        return key;
    }

    private sealed class CanonicalWriter(Stream stream)
    {
        public void WriteText(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                WriteInt32(bytes.Length);
                stream.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}

public sealed class PersonaRevisionConflictException(long expected, long actual)
    : InvalidOperationException($"Expected persona revision {expected}, but current revision is {actual}.");

public sealed class PersonaIdempotencyConflictException()
    : InvalidOperationException("The idempotency key is already bound to a different persona mutation.");

public sealed class PersonaHistoricalReceiptException()
    : InvalidOperationException("The idempotent persona receipt is historical and no longer represents the current persona revision.");

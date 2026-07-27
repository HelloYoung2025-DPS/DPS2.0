using Dps.GBrainProjector.Contracts;
using Dps.SoulMemoryAdapter.Contracts;

namespace Dps.SoulMemoryAdapter;

public sealed record GBrainReadbackObservation(
    string SourceId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string ProjectionSchemaVersion,
    string ProjectionContractId,
    string ProjectionRevision,
    string ReadbackChecksum)
{
    internal void Validate()
    {
        SoulMemoryContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        SoulMemoryContractValidation.RequireDeviceBindingId(DeviceBindingId, nameof(DeviceBindingId));
        SoulMemoryContractValidation.RequirePlatformAccountId(
            PlatformAccountId,
            nameof(PlatformAccountId));
        // Format-only check by design: the v2 source_id derivation requires the binding
        // nonce, which an external read-back never carries. The authoritative binding
        // proof is GBrainProjectionV2.Validate() on the projection; prepared/readback
        // equality is enforced field-by-field in EnsureExactReadback.
        SoulMemoryContractValidation.RequireSourceId(SourceId, nameof(SourceId));
        SoulMemoryContractValidation.RequireMajor(ProjectionSchemaVersion, 2);
        SoulMemoryContractValidation.RequireExact(
            ProjectionContractId,
            GBrainProjectionV2.CurrentContractId,
            nameof(ProjectionContractId));
        SoulMemoryContractValidation.RequireSha256(ProjectionRevision, nameof(ProjectionRevision));
        SoulMemoryContractValidation.RequireSha256(ReadbackChecksum, nameof(ReadbackChecksum));
    }
}

public sealed record VerifyReadbackCommand(
    SoulMemoryReadbackV1 Prepared,
    GBrainReadbackObservation Readback,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed class DeterministicSoulMemoryAdapter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Payload, SoulMemoryReadbackV1 Result)> _idempotency =
        new(StringComparer.Ordinal);

    public SoulMemoryReadbackV1 Prepare(GBrainProjectionV2 projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();

        var operationKey = OperationKey("prepare", projection.IdempotencyKey);
        // The idempotency payload is a tamper-comparison set: it may only grow. The v2
        // source binding fields are pinned here so a replayed prepare cannot rebind the
        // same key to a different Source binding proof.
        var payload = string.Join(
            ':',
            projection.ContractId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            projection.SourceId,
            projection.SourceBindingAlgorithm,
            projection.SourceBindingNonce.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            projection.SourceBindingSoulHash,
            projection.SourceBindingAllocatedAt.UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            projection.SourceBindingRevision,
            projection.SourceBindingChecksum,
            projection.ProjectionRevision,
            projection.ProjectionChecksum);

        lock (_gate)
        {
            if (_idempotency.TryGetValue(operationKey, out var prior))
            {
                EnsureSame(prior.Payload, payload);
                return prior.Result;
            }

            var result = Create(
                projection.SoulId,
                projection.DeviceBindingId,
                projection.PlatformAccountId,
                projection.TraceId,
                projection.IdempotencyKey,
                projection.OccurredAt,
                projection.SourceId,
                projection.SchemaVersion,
                projection.ContractId,
                projection.ProjectionRevision,
                projection.ProjectionChecksum,
                SoulMemoryReadbackV1.NoReadbackChecksum,
                SoulMemoryReadbackV1.PreparedStatus);
            _idempotency.Add(operationKey, (payload, result));
            return result;
        }
    }

    public SoulMemoryReadbackV1 Verify(VerifyReadbackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Prepared);
        ArgumentNullException.ThrowIfNull(command.Readback);
        command.Prepared.Validate();
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);

        if (!string.Equals(
                command.Prepared.Status,
                SoulMemoryReadbackV1.PreparedStatus,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only a prepared projection can be verified.");
        }

        if (command.OccurredAt < command.Prepared.OccurredAt)
        {
            throw new InvalidOperationException(
                "Read-back verification cannot occur before projection preparation.");
        }

        ValidateUntrustedReadback(command.Readback);

        var operationKey = OperationKey("verify", command.IdempotencyKey);
        var payload = string.Join(
            ':',
            command.Prepared.SourceId,
            command.Prepared.ProjectionSchemaVersion,
            command.Prepared.ProjectionContractId,
            command.Prepared.ProjectionRevision,
            command.Prepared.ProjectionChecksum,
            command.Readback.SoulId,
            command.Readback.DeviceBindingId,
            command.Readback.PlatformAccountId,
            command.Readback.SourceId,
            command.Readback.ProjectionSchemaVersion,
            command.Readback.ProjectionContractId,
            command.Readback.ProjectionRevision,
            command.Readback.ReadbackChecksum);

        lock (_gate)
        {
            if (_idempotency.TryGetValue(operationKey, out var prior))
            {
                EnsureSame(prior.Payload, payload);
                return prior.Result;
            }

            EnsureExactReadback(command.Prepared, command.Readback);
            var result = Create(
                command.Prepared.SoulId,
                command.Prepared.DeviceBindingId,
                command.Prepared.PlatformAccountId,
                command.TraceId,
                command.IdempotencyKey,
                command.OccurredAt,
                command.Prepared.SourceId,
                command.Prepared.ProjectionSchemaVersion,
                command.Prepared.ProjectionContractId,
                command.Prepared.ProjectionRevision,
                command.Prepared.ProjectionChecksum,
                command.Readback.ReadbackChecksum,
                SoulMemoryReadbackV1.VerifiedStatus);
            _idempotency.Add(operationKey, (payload, result));
            return result;
        }
    }

    private static void ValidateUntrustedReadback(GBrainReadbackObservation readback)
    {
        try
        {
            readback.Validate();
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new GBrainReadbackMismatchException(
                "External read-back is not a valid GBrain projection observation.",
                exception);
        }
    }

    private static void EnsureExactReadback(
        SoulMemoryReadbackV1 prepared,
        GBrainReadbackObservation readback)
    {
        if (!string.Equals(readback.SoulId, prepared.SoulId, StringComparison.Ordinal) ||
            !string.Equals(
                readback.DeviceBindingId,
                prepared.DeviceBindingId,
                StringComparison.Ordinal) ||
            !string.Equals(
                readback.PlatformAccountId,
                prepared.PlatformAccountId,
                StringComparison.Ordinal) ||
            !string.Equals(readback.SourceId, prepared.SourceId, StringComparison.Ordinal) ||
            !string.Equals(
                readback.ProjectionSchemaVersion,
                prepared.ProjectionSchemaVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                readback.ProjectionContractId,
                prepared.ProjectionContractId,
                StringComparison.Ordinal) ||
            !string.Equals(
                readback.ProjectionRevision,
                prepared.ProjectionRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                readback.ReadbackChecksum,
                prepared.ProjectionChecksum,
                StringComparison.Ordinal))
        {
            throw new GBrainReadbackMismatchException(
                "External read-back does not exactly match the prepared GBrain projection.");
        }
    }

    private static void ValidateEnvelope(
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        SoulMemoryContractValidation.RequireTraceId(traceId, nameof(traceId));
        SoulMemoryContractValidation.RequireIdempotencyKey(idempotencyKey, nameof(idempotencyKey));
        SoulMemoryContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }

    private static string OperationKey(string operation, string idempotencyKey)
    {
        return operation + ":" + idempotencyKey;
    }

    private static SoulMemoryReadbackV1 Create(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string sourceId,
        string projectionSchemaVersion,
        string projectionContractId,
        string projectionRevision,
        string projectionChecksum,
        string readbackChecksum,
        string status)
    {
        var result = new SoulMemoryReadbackV1(
            SoulMemoryReadbackV1.CurrentSchemaVersion,
            SoulMemoryReadbackV1.CurrentContractId,
            SoulMemoryReadbackV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            "personal",
            sourceId,
            projectionSchemaVersion,
            projectionContractId,
            projectionRevision,
            projectionChecksum,
            readbackChecksum,
            status);
        result.Validate();
        return result;
    }

    private static void EnsureSame(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The idempotency key is bound to a different SoulMemory operation.");
        }
    }
}

public sealed class GBrainReadbackMismatchException : InvalidOperationException
{
    public GBrainReadbackMismatchException(string message)
        : base(message)
    {
    }

    public GBrainReadbackMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

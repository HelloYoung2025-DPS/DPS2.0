using System.Text.Json;
using Dps.EvidenceService.Contracts;
using Dps.GBrainProjector.Contracts;
using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry.Contracts;

namespace Dps.EvidenceService;

public sealed record VerticalSliceEvidenceMetadata(
    Guid EvidenceId,
    string TestId,
    string ModuleId,
    string EvidenceIdempotencyKey,
    string BaselineCommit,
    string InstructionReceiptId,
    string InstructionReceiptSha256,
    string ImplementerIdentity,
    string EvidenceIssuerIdentity,
    string? ReleaseApproverIdentity,
    string TestType,
    string ExecutionEnvironment,
    bool Required,
    string Status,
    string VerificationLevel,
    string CommandSha256,
    int? ExitCode,
    string? ReasonCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

public sealed record ComposedVerticalSliceEvidence(
    TestEvidenceV1 Receipt,
    IReadOnlyList<RawEvidenceArtifact> RawArtifacts,
    EvidenceSubmissionCandidate Candidate);

public static class VerticalSliceEvidenceComposer
{
    public static ComposedVerticalSliceEvidence Compose(
        SoulResolved soul,
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 interestSnapshot,
        GBrainProjectionV1 projection,
        VerticalSliceEvidenceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(soul);
        ArgumentNullException.ThrowIfNull(memoryEvents);
        ArgumentNullException.ThrowIfNull(interestSnapshot);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(metadata);

        ValidateSoulReceipt(soul);
        interestSnapshot.Validate();
        projection.Validate();
        var orderedEvents = ValidateEventSet(memoryEvents, interestSnapshot, projection);
        EnsureSameScope(soul, orderedEvents, interestSnapshot, projection);
        var canonicalEventSet = "[" + string.Join(",", orderedEvents.Select(MemoryEventCanonicalizer.Serialize)) + "]";

        var rawArtifacts = new[]
        {
            RawEvidenceArtifact.FromUtf8(
                "receipt:soul.resolved-v1",
                JsonSerializer.Serialize(soul)),
            RawEvidenceArtifact.FromUtf8(
                "receipt:memory.event-set-v1",
                canonicalEventSet),
            RawEvidenceArtifact.FromUtf8(
                "receipt:interest.snapshot-v1",
                InterestSnapshotCanonicalizer.Serialize(interestSnapshot)),
            RawEvidenceArtifact.FromUtf8(
                "receipt:gbrain.projection-v1",
                GBrainProjectionCanonicalizer.Serialize(projection))
        };
        var artifactMetadata = rawArtifacts.Select(static raw => new EvidenceArtifactV1(
            raw.ArtifactId,
            raw.Sha256,
            raw.SizeBytes,
            "application/json")).ToArray();
        var sourceReceipts = new[]
        {
            new SourceReceiptDigestV1(soul.ContractId, soul.ProducerModule, rawArtifacts[0].Sha256),
            new SourceReceiptDigestV1(orderedEvents[0].ContractId, orderedEvents[0].ProducerModule, rawArtifacts[1].Sha256),
            new SourceReceiptDigestV1(interestSnapshot.ContractId, interestSnapshot.ProducerModule, rawArtifacts[2].Sha256),
            new SourceReceiptDigestV1(projection.ContractId, projection.ProducerModule, rawArtifacts[3].Sha256)
        };

        var receipt = new TestEvidenceV1(
            TestEvidenceV1.CurrentSchemaVersion,
            TestEvidenceV1.CurrentContractId,
            TestEvidenceV1.CurrentProducerModule,
            soul.SoulId,
            soul.DeviceBindingId,
            soul.PlatformAccountId,
            soul.TraceId,
            metadata.EvidenceIdempotencyKey,
            metadata.FinishedAt,
            "personal",
            metadata.EvidenceId,
            metadata.TestId,
            metadata.ModuleId,
            metadata.TestType,
            metadata.ExecutionEnvironment,
            metadata.Required,
            metadata.Status,
            metadata.VerificationLevel,
            metadata.BaselineCommit,
            metadata.InstructionReceiptId,
            metadata.InstructionReceiptSha256,
            metadata.ImplementerIdentity,
            metadata.EvidenceIssuerIdentity,
            metadata.ReleaseApproverIdentity,
            metadata.CommandSha256,
            metadata.StartedAt,
            metadata.FinishedAt,
            metadata.ExitCode,
            artifactMetadata,
            sourceReceipts,
            metadata.ReasonCode);

        var candidate = EvidenceSubmissionCandidate.Create(receipt, rawArtifacts);
        return new ComposedVerticalSliceEvidence(candidate.Receipt, candidate.RawArtifacts, candidate);
    }

    private static void ValidateSoulReceipt(SoulResolved soul)
    {
        if (!string.Equals(soul.SchemaVersion, SoulResolved.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(soul.ContractId, SoulResolved.CurrentContractId, StringComparison.Ordinal) ||
            !string.Equals(soul.ProducerModule, SoulResolved.CurrentProducerModule, StringComparison.Ordinal) ||
            !string.Equals(soul.PrivacyClass, SoulResolved.CurrentPrivacyClass, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Unsupported Soul resolution receipt contract.");
        }

        ContractValidation.RequireSoulId(soul.SoulId, nameof(soul.SoulId));
        ContractValidation.RequireOpaqueId(soul.DeviceBindingId, "db_", nameof(soul.DeviceBindingId));
        ContractValidation.RequireOpaqueId(soul.PlatformAccountId, "pa_", nameof(soul.PlatformAccountId));
        ContractValidation.RequireTraceId(soul.TraceId, nameof(soul.TraceId));
        ContractValidation.RequireIdempotencyKey(soul.IdempotencyKey, nameof(soul.IdempotencyKey));
        ContractValidation.RequireUtc(soul.OccurredAt, nameof(soul.OccurredAt));
        ContractValidation.RequireText(soul.AliasKind, 32, nameof(soul.AliasKind));
        ContractValidation.RequireSha256(soul.AliasDigest, nameof(soul.AliasDigest));
        ContractValidation.RequireText(soul.AliasKeyId, 64, nameof(soul.AliasKeyId));

        if (soul.AliasKind is not ("email" or "phone" or "platform_id"))
        {
            throw new NotSupportedException("Unsupported verified alias kind.");
        }
    }

    private static void EnsureSameScope(
        SoulResolved soul,
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 interestSnapshot,
        GBrainProjectionV1 projection)
    {
        var receipts = memoryEvents
            .Select(static memoryEvent => (
                memoryEvent.SoulId,
                memoryEvent.DeviceBindingId,
                memoryEvent.PlatformAccountId,
                memoryEvent.TraceId))
            .Append((
                interestSnapshot.SoulId,
                interestSnapshot.DeviceBindingId,
                interestSnapshot.PlatformAccountId,
                interestSnapshot.TraceId))
            .Append((
                projection.SoulId,
                projection.DeviceBindingId,
                projection.PlatformAccountId,
                projection.TraceId))
            .ToArray();

        if (receipts.Any(item =>
                !string.Equals(item.SoulId, soul.SoulId, StringComparison.Ordinal) ||
                !string.Equals(item.DeviceBindingId, soul.DeviceBindingId, StringComparison.Ordinal) ||
                !string.Equals(item.PlatformAccountId, soul.PlatformAccountId, StringComparison.Ordinal) ||
                !string.Equals(item.TraceId, soul.TraceId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("SOUL-ISO-001: evidence source receipts crossed Soul, device, account, or trace scope.");
        }

        if (!string.Equals(projection.SourceId, GBrainSourceIds.ForSoul(soul.SoulId), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GBrain projection source is not bound to the resolved Soul.");
        }
    }

    private static IReadOnlyList<MemoryEventV1> ValidateEventSet(
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 interestSnapshot,
        GBrainProjectionV1 projection)
    {
        if (memoryEvents.Count == 0)
        {
            throw new ArgumentException("At least one memory event is required for vertical-slice evidence.", nameof(memoryEvents));
        }

        var byId = new Dictionary<Guid, (MemoryEventV1 Event, string Hash)>();
        foreach (var memoryEvent in memoryEvents)
        {
            ArgumentNullException.ThrowIfNull(memoryEvent);
            memoryEvent.Validate();
            var hash = MemoryEventCanonicalizer.ComputeSha256(memoryEvent);
            if (!byId.TryAdd(memoryEvent.EventId, (memoryEvent, hash)))
            {
                throw new InvalidOperationException("Memory event identifiers must be unique in an evidence input set.");
            }
        }

        if (interestSnapshot.SourceEventCount != byId.Count ||
            projection.SourceEventCount != byId.Count ||
            projection.Events.Count != byId.Count)
        {
            throw new InvalidOperationException("Evidence input event count must exactly match the interest snapshot and projection source counts.");
        }

        foreach (var projectedEvent in projection.Events)
        {
            if (!byId.TryGetValue(projectedEvent.EventId, out var source) ||
                !string.Equals(projectedEvent.EventHash, source.Hash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(projectedEvent.ContentDigest, source.Event.Observation.ContentDigest, StringComparison.OrdinalIgnoreCase) ||
                projectedEvent.OccurredAt != source.Event.OccurredAt)
            {
                throw new InvalidOperationException("Projected event receipt does not exactly match its memory event input.");
            }
        }

        foreach (var evidence in interestSnapshot.Interests.SelectMany(static item => item.Evidence))
        {
            ValidateEvidenceReference(evidence.EventId, evidence.EventHash, evidence.OccurredAt, byId);
        }

        foreach (var evidence in projection.Interests.SelectMany(static item => item.Evidence))
        {
            ValidateEvidenceReference(evidence.EventId, evidence.EventHash, evidence.OccurredAt, byId);
        }

        return byId.Values
            .OrderBy(static item => item.Event.OccurredAt)
            .ThenBy(static item => item.Event.EventId)
            .ThenBy(static item => item.Hash, StringComparer.Ordinal)
            .Select(static item => item.Event)
            .ToArray();
    }

    private static void ValidateEvidenceReference(
        Guid eventId,
        string eventHash,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<Guid, (MemoryEventV1 Event, string Hash)> events)
    {
        if (!events.TryGetValue(eventId, out var source) ||
            !string.Equals(eventHash, source.Hash, StringComparison.OrdinalIgnoreCase) ||
            occurredAt != source.Event.OccurredAt)
        {
            throw new InvalidOperationException("Interest evidence does not exactly reference an event in the supplied memory event set.");
        }
    }
}

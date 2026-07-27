using System.Security.Cryptography;
using System.Text.Json;
using Dps.GBrainProjector.Contracts;
using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger.Contracts;

namespace Dps.GBrainProjector;

public sealed class GBrainProjectionRenderer
{
    private readonly GBrainSourceBindingAuthority _bindingAuthority;

    /// <summary>
    /// The renderer is a pure function over a Source binding capability that only the
    /// supplied authority can issue, so a public constructor grants no authority the
    /// caller did not already hold: <see cref="Render"/> still refuses any capability
    /// issued by a different authority instance.
    /// </summary>
    public GBrainProjectionRenderer(GBrainSourceBindingAuthority bindingAuthority)
    {
        ArgumentNullException.ThrowIfNull(bindingAuthority);
        _bindingAuthority = bindingAuthority;
    }

    public GBrainProjectionV2 Render(
        VerifiedGBrainSourceBinding sourceBinding,
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 interestSnapshot)
    {
        ArgumentNullException.ThrowIfNull(sourceBinding);
        ArgumentNullException.ThrowIfNull(memoryEvents);
        ArgumentNullException.ThrowIfNull(interestSnapshot);
        var binding = _bindingAuthority.ReadVerified(sourceBinding);
        interestSnapshot.Validate();
        if (!string.Equals(binding.SoulId, interestSnapshot.SoulId, StringComparison.Ordinal))
        {
            throw new ProjectionScopeMismatchException();
        }

        var uniqueEvents = ValidateAndDeduplicate(memoryEvents, interestSnapshot);
        if (interestSnapshot.SourceEventCount != uniqueEvents.Count)
        {
            throw new ProjectionInputConflictException("Interest snapshot source count does not match the unique event set.");
        }

        ValidateEvidenceReferences(interestSnapshot, uniqueEvents);
        var projectedEvents = uniqueEvents.Values
            .Select(static item => new ProjectionEventV1(
                item.Event.EventId,
                item.Hash,
                item.Event.Observation.ContentDigest.ToLowerInvariant(),
                item.Event.OccurredAt))
            .OrderBy(static item => item.OccurredAt)
            .ThenBy(static item => item.EventId)
            .ToArray();
        var projectedInterests = interestSnapshot.Interests
            .Select(static item => new ProjectionInterestV1(
                item.Topic,
                item.OriginalConfidence,
                item.DecayedConfidence,
                item.HalfLifeSeconds,
                item.AlgorithmVersion,
                item.Evidence.Select(static evidence => new ProjectionInterestEvidenceV1(
                        evidence.EventId,
                        evidence.EventHash.ToLowerInvariant(),
                        evidence.OccurredAt,
                        evidence.OriginalConfidence,
                        evidence.DecayedConfidence))
                    .OrderBy(static evidence => evidence.OccurredAt)
                    .ThenBy(static evidence => evidence.EventId)
                    .ToArray()))
            .OrderBy(static item => item.Topic, StringComparer.Ordinal)
            .ToArray();
        var revision = ComputeRevision(binding, interestSnapshot, uniqueEvents.Values);
        var withoutChecksum = new GBrainProjectionV2(
            GBrainProjectionV2.CurrentSchemaVersion,
            GBrainProjectionV2.CurrentContractId,
            GBrainProjectionV2.CurrentProducerModule,
            interestSnapshot.SoulId,
            interestSnapshot.DeviceBindingId,
            interestSnapshot.PlatformAccountId,
            interestSnapshot.TraceId,
            interestSnapshot.IdempotencyKey,
            interestSnapshot.AsOf,
            interestSnapshot.PrivacyClass,
            binding.SourceId,
            binding.Algorithm,
            binding.Nonce,
            binding.SoulHash,
            binding.AllocatedAt,
            binding.BindingRevision,
            binding.BindingChecksum,
            revision,
            new string('0', 64),
            GBrainProjectionV2.RenderedNotWrittenStatus,
            projectedEvents.Length,
            projectedEvents,
            projectedInterests);
        var projection = withoutChecksum with
        {
            ProjectionChecksum = GBrainProjectionV2Canonicalizer.ComputeSha256(withoutChecksum)
        };
        projection.Validate();
        return projection;
    }

    private static Dictionary<Guid, CanonicalEvent> ValidateAndDeduplicate(
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 interestSnapshot)
    {
        var uniqueEvents = new Dictionary<Guid, CanonicalEvent>();
        foreach (var memoryEvent in memoryEvents)
        {
            ArgumentNullException.ThrowIfNull(memoryEvent);
            memoryEvent.Validate();
            EnsureSameIdentityScope(memoryEvent, interestSnapshot);
            if (memoryEvent.OccurredAt > interestSnapshot.AsOf)
            {
                throw new ProjectionInputConflictException("An event cannot occur after the projection as_of time.");
            }

            var hash = MemoryEventCanonicalizer.ComputeSha256(memoryEvent);
            if (uniqueEvents.TryGetValue(memoryEvent.EventId, out var existing))
            {
                if (!string.Equals(existing.Hash, hash, StringComparison.Ordinal))
                {
                    throw new ProjectionInputConflictException("A duplicate event identifier has conflicting canonical content.");
                }

                continue;
            }

            uniqueEvents.Add(memoryEvent.EventId, new CanonicalEvent(memoryEvent, hash));
        }

        return uniqueEvents;
    }

    private static void EnsureSameIdentityScope(MemoryEventV1 memoryEvent, InterestSnapshotV1 snapshot)
    {
        if (!string.Equals(memoryEvent.SoulId, snapshot.SoulId, StringComparison.Ordinal) ||
            !string.Equals(memoryEvent.DeviceBindingId, snapshot.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(memoryEvent.PlatformAccountId, snapshot.PlatformAccountId, StringComparison.Ordinal))
        {
            throw new ProjectionScopeMismatchException();
        }
    }

    private static void ValidateEvidenceReferences(
        InterestSnapshotV1 snapshot,
        IReadOnlyDictionary<Guid, CanonicalEvent> events)
    {
        foreach (var interest in snapshot.Interests)
        {
            foreach (var evidence in interest.Evidence)
            {
                if (!events.TryGetValue(evidence.EventId, out var source) ||
                    !string.Equals(source.Hash, evidence.EventHash, StringComparison.OrdinalIgnoreCase) ||
                    source.Event.OccurredAt != evidence.OccurredAt)
                {
                    throw new ProjectionInputConflictException("Interest evidence does not exactly match the scoped event set.");
                }
            }
        }
    }

    private static string ComputeRevision(
        GBrainSourceBindingV1 binding,
        InterestSnapshotV1 snapshot,
        IEnumerable<CanonicalEvent> events)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("source_binding_revision", binding.BindingRevision);
            writer.WriteString("source_binding_checksum", binding.BindingChecksum);
            writer.WriteString("soul_id", snapshot.SoulId);
            writer.WriteString("device_binding_id", snapshot.DeviceBindingId);
            writer.WriteString("platform_account_id", snapshot.PlatformAccountId);
            writer.WriteString("interest_snapshot_hash", InterestSnapshotCanonicalizer.ComputeSha256(snapshot));
            writer.WritePropertyName("events");
            writer.WriteStartArray();
            foreach (var item in events
                         .OrderBy(static value => value.Event.OccurredAt)
                         .ThenBy(static value => value.Event.EventId)
                         .ThenBy(static value => value.Hash, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("event_id", item.Event.EventId);
                writer.WriteString("event_hash", item.Hash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private sealed record CanonicalEvent(MemoryEventV1 Event, string Hash);
}

public enum ProjectionRevisionDecision
{
    Accept,
    Duplicate
}

public static class ProjectionRevisionGuard
{
    public static ProjectionRevisionDecision Evaluate(GBrainProjectionV2? current, GBrainProjectionV2 candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.Validate();
        if (current is null)
        {
            return ProjectionRevisionDecision.Accept;
        }

        current.Validate();
        if (!string.Equals(current.SoulId, candidate.SoulId, StringComparison.Ordinal) ||
            !string.Equals(current.SourceId, candidate.SourceId, StringComparison.Ordinal) ||
            !string.Equals(current.SourceBindingRevision, candidate.SourceBindingRevision, StringComparison.Ordinal) ||
            !string.Equals(current.SourceBindingChecksum, candidate.SourceBindingChecksum, StringComparison.Ordinal) ||
            !string.Equals(current.DeviceBindingId, candidate.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(current.PlatformAccountId, candidate.PlatformAccountId, StringComparison.Ordinal))
        {
            throw new ProjectionScopeMismatchException();
        }

        if (candidate.SourceEventCount < current.SourceEventCount)
        {
            throw new StaleProjectionRevisionException();
        }

        if (candidate.SourceEventCount == current.SourceEventCount)
        {
            if (string.Equals(current.ProjectionRevision, candidate.ProjectionRevision, StringComparison.Ordinal) &&
                string.Equals(current.ProjectionChecksum, candidate.ProjectionChecksum, StringComparison.Ordinal))
            {
                return ProjectionRevisionDecision.Duplicate;
            }

            throw new ProjectionInputConflictException("Equal event counts with different projection content are ambiguous and fail closed.");
        }

        return ProjectionRevisionDecision.Accept;
    }
}

public sealed class ProjectionScopeMismatchException : InvalidOperationException
{
    public ProjectionScopeMismatchException() : base("Projection input identity or Source binding scope mismatch.") { }
}

public sealed class ProjectionInputConflictException : InvalidOperationException
{
    public ProjectionInputConflictException(string message) : base(message) { }
}

public sealed class StaleProjectionRevisionException : InvalidOperationException
{
    public StaleProjectionRevisionException() : base("Projection candidate is older than the current persisted revision.") { }
}

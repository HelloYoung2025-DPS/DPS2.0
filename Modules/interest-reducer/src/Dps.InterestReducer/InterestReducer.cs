using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger.Contracts;

namespace Dps.InterestReducer;

public sealed class InterestReducer
{
    internal const int ConfidenceScale = 12;

    public InterestSnapshotV1 Reduce(
        InterestReductionRequest request,
        InterestReducerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        options ??= InterestReducerOptions.Default;
        options.Validate();

        var uniqueEvents = ValidateAndDeduplicate(request);
        var evidenceByTopic = new SortedDictionary<string, List<InterestEvidenceV1>>(StringComparer.Ordinal);

        foreach (var candidate in uniqueEvents
                     .OrderBy(static item => item.Event.OccurredAt)
                     .ThenBy(static item => item.Event.EventId)
                     .ThenBy(static item => item.Hash, StringComparer.Ordinal))
        {
            foreach (var signal in candidate.Event.Observation.InterestSignals
                         .GroupBy(static item => item.Topic, StringComparer.Ordinal)
                         .Select(static group => new
                         {
                             Topic = group.Key,
                             Confidence = group.Max(static item => item.Confidence)
                         })
                         .OrderBy(static item => item.Topic, StringComparer.Ordinal))
            {
                var originalConfidence = RoundConfidence(signal.Confidence);
                var decayedConfidence = Decay(
                    originalConfidence,
                    candidate.Event.OccurredAt,
                    request.AsOf,
                    options.HalfLifeSeconds);

                if (!evidenceByTopic.TryGetValue(signal.Topic, out var evidence))
                {
                    evidence = new List<InterestEvidenceV1>();
                    evidenceByTopic.Add(signal.Topic, evidence);
                }

                evidence.Add(new InterestEvidenceV1(
                    candidate.Event.EventId,
                    candidate.Hash,
                    candidate.Event.OccurredAt,
                    originalConfidence,
                    decayedConfidence));
            }
        }

        var interests = evidenceByTopic.Select(pair => BuildInterest(pair.Key, pair.Value, options)).ToArray();
        var snapshot = new InterestSnapshotV1(
            InterestSnapshotV1.CurrentSchemaVersion,
            InterestSnapshotV1.CurrentContractId,
            InterestSnapshotV1.CurrentProducerModule,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.AsOf,
            "personal",
            request.AsOf,
            InterestSnapshotV1.CurrentAlgorithmVersion,
            options.HalfLifeSeconds,
            uniqueEvents.Count,
            interests);

        snapshot.Validate();
        return snapshot;
    }

    private static IReadOnlyList<CanonicalMemoryEvent> ValidateAndDeduplicate(InterestReductionRequest request)
    {
        var byEventId = new Dictionary<Guid, CanonicalMemoryEvent>();

        foreach (var memoryEvent in request.Events)
        {
            ArgumentNullException.ThrowIfNull(memoryEvent);
            memoryEvent.Validate();

            if (!string.Equals(memoryEvent.SoulId, request.SoulId, StringComparison.Ordinal) ||
                !string.Equals(memoryEvent.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) ||
                !string.Equals(memoryEvent.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A MemoryEvent crossed the requested Soul, device, or platform-account boundary.");
            }

            if (memoryEvent.OccurredAt > request.AsOf)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "A MemoryEvent cannot occur after the explicit as_of time.");
            }

            var hash = MemoryEventCanonicalizer.ComputeSha256(memoryEvent);
            if (byEventId.TryGetValue(memoryEvent.EventId, out var existing))
            {
                if (!string.Equals(existing.Hash, hash, StringComparison.Ordinal))
                {
                    throw new DuplicateMemoryEventConflictException(memoryEvent.EventId);
                }

                continue;
            }

            byEventId.Add(memoryEvent.EventId, new CanonicalMemoryEvent(memoryEvent, hash));
        }

        return byEventId.Values.ToArray();
    }

    private static InterestValueV1 BuildInterest(
        string topic,
        IReadOnlyCollection<InterestEvidenceV1> evidence,
        InterestReducerOptions options)
    {
        var orderedEvidence = evidence
            .OrderBy(static item => item.OccurredAt)
            .ThenBy(static item => item.EventId)
            .ThenBy(static item => item.EventHash, StringComparer.Ordinal)
            .ToArray();

        var originalConfidence = RoundConfidence(orderedEvidence.Sum(static item => item.OriginalConfidence));
        var decayedConfidence = RoundConfidence(orderedEvidence.Sum(static item => item.DecayedConfidence));

        return new InterestValueV1(
            topic,
            Math.Min(1m, originalConfidence),
            Math.Min(1m, decayedConfidence),
            options.HalfLifeSeconds,
            InterestSnapshotV1.CurrentAlgorithmVersion,
            orderedEvidence);
    }

    private static decimal Decay(
        decimal confidence,
        DateTimeOffset occurredAt,
        DateTimeOffset asOf,
        decimal halfLifeSeconds)
    {
        var ageTicks = asOf.UtcTicks - occurredAt.UtcTicks;
        if (ageTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurredAt), "Evidence cannot occur in the future.");
        }

        var ageSeconds = ageTicks / (decimal)TimeSpan.TicksPerSecond;
        var exponent = (double)(ageSeconds / halfLifeSeconds);
        var factor = Math.Pow(0.5d, exponent);

        if (!double.IsFinite(factor) || factor < 0d || factor > 1d)
        {
            throw new ArithmeticException("Decay calculation produced a non-finite or out-of-range factor.");
        }

        return RoundConfidence(confidence * (decimal)factor);
    }

    private static decimal RoundConfidence(decimal value)
    {
        return decimal.Round(value, ConfidenceScale, MidpointRounding.ToEven);
    }

    private sealed record CanonicalMemoryEvent(MemoryEventV1 Event, string Hash);
}

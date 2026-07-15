using Dps.GBrainProjector.Contracts;

namespace Dps.GBrainProjector;

public sealed class VerifiedGBrainSourceBinding
{
    internal VerifiedGBrainSourceBinding(
        GBrainSourceBindingV1 binding,
        Guid authorityInstanceId)
    {
        Binding = binding;
        AuthorityInstanceId = authorityInstanceId;
    }

    internal GBrainSourceBindingV1 Binding { get; }
    internal Guid AuthorityInstanceId { get; }
    public string SoulId => Binding.SoulId;
    public string SourceId => Binding.SourceId;
    public string BindingRevision => Binding.BindingRevision;
    public string BindingChecksum => Binding.BindingChecksum;
}

public sealed class GBrainSourceBindingAuthority
{
    public const long MaximumNonce = GBrainSourceBindingV1.MaximumNonce;

    private readonly Guid _authorityInstanceId = Guid.NewGuid();
    private readonly ISourceBindingStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly long _maximumNonce;

    internal GBrainSourceBindingAuthority(
        ISourceBindingStore store,
        TimeProvider timeProvider,
        long maximumNonce = MaximumNonce)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maximumNonce is < 0 or > MaximumNonce)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNonce));
        }

        _store = store;
        _timeProvider = timeProvider;
        _maximumNonce = maximumNonce;
    }

    public async Task<VerifiedGBrainSourceBinding> ResolveAsync(
        string soulId,
        CancellationToken cancellationToken = default)
    {
        ProjectionContractValidation.RequireSoulId(soulId, nameof(soulId));
        var binding = await _store.ResolveOrAllocateAsync(
            soulId,
            _maximumNonce,
            NormalizePostgresTimestamp(_timeProvider.GetUtcNow()),
            cancellationToken);
        VerifyFixedBinding(binding, soulId);

        return new VerifiedGBrainSourceBinding(binding, _authorityInstanceId);
    }

    internal GBrainSourceBindingV1 ReadVerified(VerifiedGBrainSourceBinding capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.AuthorityInstanceId != _authorityInstanceId)
        {
            throw new SourceBindingCapabilityException();
        }

        VerifyFixedBinding(capability.Binding, capability.SoulId);
        return capability.Binding;
    }

    private static void VerifyFixedBinding(GBrainSourceBindingV1 binding, string expectedSoulId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        if (!string.Equals(binding.SoulId, expectedSoulId, StringComparison.Ordinal))
        {
            throw new SourceBindingIntegrityException("The binding store returned a different Soul.");
        }

        var expectedSourceId = GBrainSourceIdCandidates.Compute(binding.SoulId, binding.Nonce);
        if (!string.Equals(binding.SourceId, expectedSourceId, StringComparison.Ordinal))
        {
            throw new SourceBindingIntegrityException(
                "The binding store returned a Source identifier that was not derived by the fixed algorithm.");
        }
    }

    private static DateTimeOffset NormalizePostgresTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}

internal interface ISourceBindingStore
{
    Task<GBrainSourceBindingV1> ResolveOrAllocateAsync(
        string soulId,
        long maximumNonce,
        DateTimeOffset allocatedAt,
        CancellationToken cancellationToken);
}

internal sealed class InMemorySourceBindingStore : ISourceBindingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _canonicalBySoul = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _soulBySource = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantinedSouls = new(StringComparer.Ordinal);

    internal int QuarantineCount
    {
        get { lock (_gate) return _quarantinedSouls.Count; }
    }

    public Task<GBrainSourceBindingV1> ResolveOrAllocateAsync(
        string soulId,
        long maximumNonce,
        DateTimeOffset allocatedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_canonicalBySoul.TryGetValue(soulId, out var canonical))
            {
                return Task.FromResult(ReadAndValidate(canonical, soulId));
            }

            for (long nonce = 0; nonce <= maximumNonce; nonce++)
            {
                var sourceId = GBrainSourceIdCandidates.Compute(soulId, nonce);
                ValidateCandidate(sourceId);
                if (_soulBySource.ContainsKey(sourceId))
                {
                    continue;
                }

                var binding = GBrainSourceBindingV1.Create(soulId, sourceId, nonce, allocatedAt);
                var bindingJson = GBrainSourceBindingCanonicalizer.Serialize(binding);
                _canonicalBySoul.Add(soulId, bindingJson);
                _soulBySource.Add(sourceId, soulId);
                return Task.FromResult(binding);
            }

            _quarantinedSouls.Add(soulId);
            throw new SourceBindingNonceExhaustedException(soulId, maximumNonce);
        }
    }

    internal void Tamper(string soulId, string canonicalJson)
    {
        lock (_gate) _canonicalBySoul[soulId] = canonicalJson;
    }

    internal void ReserveSourceForTest(string sourceId)
    {
        ValidateCandidate(sourceId);
        lock (_gate)
        {
            _soulBySource.Add(sourceId, "test-reservation");
        }
    }

    private static GBrainSourceBindingV1 ReadAndValidate(string canonicalJson, string expectedSoulId)
    {
        var binding = System.Text.Json.JsonSerializer.Deserialize<GBrainSourceBindingV1>(canonicalJson)
            ?? throw new SourceBindingIntegrityException("Stored Source binding is not valid JSON.");
        binding.Validate();
        if (!string.Equals(binding.SoulId, expectedSoulId, StringComparison.Ordinal))
        {
            throw new SourceBindingIntegrityException("Stored Source binding Soul does not match its primary key.");
        }


        if (!string.Equals(GBrainSourceBindingCanonicalizer.Serialize(binding), canonicalJson, StringComparison.Ordinal))
        {
            throw new SourceBindingIntegrityException("Stored Source binding canonical bytes were tampered.");
        }

        return binding;
    }

    internal static void ValidateCandidate(string sourceId)
    {
        if (sourceId is null || sourceId.Length != GBrainSourceBindingV1.SourceIdLength ||
            !sourceId.StartsWith("dps-", StringComparison.Ordinal) ||
            sourceId.AsSpan(4).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new SourceBindingIntegrityException(
                "The Source candidate is not a canonical lowercase fixed-length identifier.");
        }
    }
}

public sealed class SourceBindingCapabilityException : InvalidOperationException
{
    public SourceBindingCapabilityException()
        : base("The Source binding capability was not issued by this fixed authority instance.") { }
}

public sealed class SourceBindingIntegrityException : InvalidOperationException
{
    public SourceBindingIntegrityException(string message) : base(message) { }
}

public sealed class SourceBindingNonceExhaustedException : InvalidOperationException
{
    public SourceBindingNonceExhaustedException(string soulId, long maximumNonce)
        : base($"Source binding allocation exhausted nonce range 0..{maximumNonce} and was quarantined for Soul hash {soulId[5..]}.") { }
}

using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger.Contracts;

namespace Dps.InterestReducer;

public sealed record InterestReductionRequest(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset AsOf,
    IReadOnlyList<MemoryEventV1> Events)
{
    public void Validate()
    {
        InterestContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        InterestContractValidation.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        InterestContractValidation.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        InterestContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        InterestContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        InterestContractValidation.RequireUtc(AsOf, nameof(AsOf));
        ArgumentNullException.ThrowIfNull(Events);
    }
}

public sealed record InterestReducerOptions(decimal HalfLifeSeconds)
{
    public const decimal DefaultHalfLifeSeconds = 2_592_000m;
    public const decimal MaximumHalfLifeSeconds = 3_155_760_000m;

    public static InterestReducerOptions Default { get; } = new(DefaultHalfLifeSeconds);

    public void Validate()
    {
        InterestContractValidation.RequirePositive(HalfLifeSeconds, nameof(HalfLifeSeconds));

        if (HalfLifeSeconds > MaximumHalfLifeSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HalfLifeSeconds),
                $"Half-life cannot exceed {MaximumHalfLifeSeconds} seconds.");
        }
    }
}

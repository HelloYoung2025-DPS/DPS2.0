namespace Dps.InterestReducer;

public sealed class DuplicateMemoryEventConflictException : InvalidOperationException
{
    public DuplicateMemoryEventConflictException(Guid eventId)
        : base($"Memory event '{eventId}' was delivered with conflicting canonical content.")
    {
        EventId = eventId;
    }

    public Guid EventId { get; }
}

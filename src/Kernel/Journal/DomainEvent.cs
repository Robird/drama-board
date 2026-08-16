using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Journal;

/// <summary>Represents an immutable fact committed to the world journal at a logical timestamp.</summary>
public sealed class DomainEvent<TPayload>
{
    /// <summary>Initializes a committed domain event.</summary>
    public DomainEvent(LogicalTimestamp timestamp, EventKind kind, TPayload payload)
    {
        Timestamp = timestamp;
        Kind = kind;
        Payload = payload;
    }

    /// <summary>Gets the event's total-order logical timestamp.</summary>
    public LogicalTimestamp Timestamp { get; }

    /// <summary>Gets the stable event kind.</summary>
    public EventKind Kind { get; }

    /// <summary>Gets the event payload.</summary>
    public TPayload Payload { get; }
}

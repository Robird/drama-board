using DramaBoard.Kernel.Journal;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Describes a resolved event before the simulation loop assigns its logical timestamp.</summary>
public sealed class UncommittedDomainEvent<TPayload>
{
    /// <summary>Initializes an event description awaiting journal commit.</summary>
    public UncommittedDomainEvent(EventKind kind, TPayload payload)
    {
        Kind = kind;
        Payload = payload;
    }

    /// <summary>Gets the stable event kind.</summary>
    public EventKind Kind { get; }

    /// <summary>Gets the event payload.</summary>
    public TPayload Payload { get; }
}

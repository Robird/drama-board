namespace DramaBoard.Kernel.Simulation;

/// <summary>Describes a resolved event before the simulation loop assigns its logical timestamp.</summary>
public sealed class UncommittedDomainEvent<TPayload>
{
    /// <summary>Initializes an event description awaiting journal commit.</summary>
    public UncommittedDomainEvent(string kind, TPayload payload)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Event kind cannot be empty.", nameof(kind));
        }

        Kind = kind;
        Payload = payload;
    }

    /// <summary>Gets the stable event kind.</summary>
    public string Kind { get; }

    /// <summary>Gets the event payload.</summary>
    public TPayload Payload { get; }
}

namespace DramaBoard.Kernel.Simulation;

/// <summary>Returns a replacement world value and the facts produced by resolving one candidate.</summary>
public sealed class ResolveResult<TWorld, TEventPayload>
{
    /// <summary>Initializes the result of a pure candidate resolution.</summary>
    public ResolveResult(
        TWorld world,
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>> events)
    {
        World = world;
        Events = events ?? throw new ArgumentNullException(nameof(events));
    }

    /// <summary>Gets the replacement world value after resolution.</summary>
    public TWorld World { get; }

    /// <summary>Gets the event descriptions to commit in their deterministic causal order.</summary>
    public IReadOnlyList<UncommittedDomainEvent<TEventPayload>> Events { get; }
}

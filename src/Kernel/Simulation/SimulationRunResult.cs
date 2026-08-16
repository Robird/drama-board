using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Reports the final world, cursor, stop reason, and work performed by a simulation run.</summary>
public sealed class SimulationRunResult<TWorld, TEventPayload>
{
    /// <summary>Initializes a completed simulation run result.</summary>
    public SimulationRunResult(
        TWorld world,
        SimulationCursor cursor,
        StopReason stopReason,
        WorldVersion version,
        IReadOnlyList<DomainEvent<TEventPayload>> decisionEvents,
        int timeAdvanceCount,
        int resolvedCandidateCount)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(decisionEvents);

        World = world;
        Cursor = cursor;
        StopReason = stopReason;
        Version = version;
        DecisionEvents = Array.AsReadOnly(decisionEvents.ToArray());
        TimeAdvanceCount = timeAdvanceCount;
        ResolvedCandidateCount = resolvedCandidateCount;
    }

    /// <summary>Gets the final replacement world value.</summary>
    public TWorld World { get; }

    /// <summary>Gets the replacement cursor to pass to the next run.</summary>
    public SimulationCursor Cursor { get; }

    /// <summary>Gets the logical clock reached by the run.</summary>
    public ModelTime CurrentTime => Cursor.Now;

    /// <summary>Gets why the run returned control to its host.</summary>
    public StopReason StopReason { get; }

    /// <summary>Gets the committed journal-prefix version at the end of the run.</summary>
    public WorldVersion Version { get; }

    /// <summary>Gets decision request events from the final committed resolve batch.</summary>
    public IReadOnlyList<DomainEvent<TEventPayload>> DecisionEvents { get; }

    /// <summary>Gets the number of nonzero logical-time jumps.</summary>
    public int TimeAdvanceCount { get; }

    /// <summary>Gets the number of candidates resolved.</summary>
    public int ResolvedCandidateCount { get; }
}

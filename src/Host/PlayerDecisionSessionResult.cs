using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Host;

/// <summary>Reports the terminal state of a Host decision session run.</summary>
public sealed class PlayerDecisionSessionResult<TWorld>
{
    /// <summary>Initializes a terminal session result.</summary>
    public PlayerDecisionSessionResult(
        TWorld world,
        SimulationCursor cursor,
        WorldVersion version,
        StopReason stopReason,
        int decisionCount,
        int skippedDecisionCount,
        int forcedDecisionCount,
        int pendingDecisionCount,
        int validationFailedDecisionCount)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        World = world;
        Cursor = cursor;
        Version = version;
        StopReason = stopReason;
        DecisionCount = decisionCount;
        SkippedDecisionCount = skippedDecisionCount;
        ForcedDecisionCount = forcedDecisionCount;
        PendingDecisionCount = pendingDecisionCount;
        ValidationFailedDecisionCount = validationFailedDecisionCount;
    }

    /// <summary>Gets the final replacement world value.</summary>
    public TWorld World { get; }

    /// <summary>Gets the final replacement simulation cursor.</summary>
    public SimulationCursor Cursor { get; }

    /// <summary>Gets the final committed journal-prefix version.</summary>
    public WorldVersion Version { get; }

    /// <summary>Gets why the simulation stopped after all pending decision requests were handled.</summary>
    public StopReason StopReason { get; }

    /// <summary>Gets the number of decisions submitted during this call.</summary>
    public int DecisionCount { get; }

    /// <summary>Gets the number of pending decision requests invalidated by the current world.</summary>
    public int SkippedDecisionCount { get; }

    /// <summary>Gets the number of decisions replaced with the Host's safe default during this call.</summary>
    public int ForcedDecisionCount { get; }

    /// <summary>Gets the number of requests that still need a valid answer.</summary>
    public int PendingDecisionCount { get; }

    /// <summary>Gets the number of Player answers invalidated by correlation validation during this call.</summary>
    public int ValidationFailedDecisionCount { get; }

    /// <summary>Gets the total number of invalidations recorded during this call.</summary>
    public int InvalidatedDecisionCount => checked(SkippedDecisionCount + ValidationFailedDecisionCount);
}

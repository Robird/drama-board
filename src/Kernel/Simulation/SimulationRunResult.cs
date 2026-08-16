using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Reports the final world, logical clock, and work performed by a simulation run.</summary>
public sealed class SimulationRunResult<TWorld>
{
    /// <summary>Initializes a completed simulation run result.</summary>
    public SimulationRunResult(
        TWorld world,
        ModelTime currentTime,
        int timeAdvanceCount,
        int resolvedCandidateCount)
    {
        World = world;
        CurrentTime = currentTime;
        TimeAdvanceCount = timeAdvanceCount;
        ResolvedCandidateCount = resolvedCandidateCount;
    }

    /// <summary>Gets the final replacement world value.</summary>
    public TWorld World { get; }

    /// <summary>Gets the logical clock reached by the run.</summary>
    public ModelTime CurrentTime { get; }

    /// <summary>Gets the number of nonzero logical-time jumps.</summary>
    public int TimeAdvanceCount { get; }

    /// <summary>Gets the number of candidates resolved.</summary>
    public int ResolvedCandidateCount { get; }
}
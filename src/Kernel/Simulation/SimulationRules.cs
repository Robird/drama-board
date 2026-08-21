namespace DramaBoard.Kernel.Simulation;

/// <summary>Contains the immutable rules required by occurrence scheduling and progress guards.</summary>
public sealed record SimulationRules
{
    /// <summary>Initializes simulation rules for one lineage.</summary>
    public SimulationRules(ulong worldSeed, int maxTransitionsPerModelTime)
    {
        if (maxTransitionsPerModelTime <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTransitionsPerModelTime),
                "The transition budget per model time must be positive.");
        }

        WorldSeed = worldSeed;
        MaxTransitionsPerModelTime = maxTransitionsPerModelTime;
    }

    /// <summary>Gets the deterministic world seed used by scheduler arbitration.</summary>
    public ulong WorldSeed { get; }

    /// <summary>Gets the maximum committed transitions allowed at one model time.</summary>
    public int MaxTransitionsPerModelTime { get; }
}

namespace DramaBoard.Kernel.Simulation;

/// <summary>Identifies one committed transition prefix within a simulation lineage.</summary>
public readonly record struct WorldVersion
{
    /// <summary>Initializes a version from a lineage and its committed transition count.</summary>
    public WorldVersion(long lineageId, long transitionCount)
    {
        if (transitionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionCount));
        }

        LineageId = lineageId;
        TransitionCount = transitionCount;
    }

    /// <summary>Gets the lineage identity; versions from different lineages are never equal.</summary>
    public long LineageId { get; }

    /// <summary>Gets the complete committed batch count within this lineage.</summary>
    public long TransitionCount { get; }
}

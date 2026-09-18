namespace DramaBoard.Kernel.Simulation;

/// <summary>Identifies one committed transition prefix within a simulation lineage.</summary>
[Atelia.DurableGraph.DurableType("DramaBoard.Kernel.WorldVersion", 1)]
public readonly partial record struct WorldVersion {
    /// <summary>Initializes a version from a lineage and its committed transition count.</summary>
    public WorldVersion(long lineageId, long transitionCount) {
        if (transitionCount < 0) {
            throw new ArgumentOutOfRangeException(nameof(transitionCount));
        }

        LineageId = lineageId;
        TransitionCount = transitionCount;
    }

    /// <summary>Gets the lineage identity; versions from different lineages are never equal.</summary>
    [field: Atelia.DurableGraph.DurableField(1)] public long LineageId { get; }

    /// <summary>Gets the complete committed batch count within this lineage.</summary>
    [field: Atelia.DurableGraph.DurableField(2)] public long TransitionCount { get; }
}

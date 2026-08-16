namespace DramaBoard.Kernel.Simulation;

/// <summary>Identifies one committed event prefix within a simulation lineage.</summary>
public readonly record struct WorldVersion
{
    /// <summary>Initializes a version from a lineage and its committed journal event count.</summary>
    public WorldVersion(long lineageId, int eventCount)
    {
        if (eventCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventCount));
        }

        LineageId = lineageId;
        EventCount = eventCount;
    }

    /// <summary>Gets the lineage identity; versions from different lineages are never equal.</summary>
    public long LineageId { get; }

    /// <summary>Gets the journal prefix length, which is the natural prefix-comparison key within a lineage.</summary>
    public int EventCount { get; }
}
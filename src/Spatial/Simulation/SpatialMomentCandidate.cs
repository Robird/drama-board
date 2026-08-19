namespace DramaBoard.Spatial;

/// <summary>Captures the exact Spatial projection identity forecast for one global earliest moment.</summary>
public sealed record SpatialMomentCandidate
{
    /// <summary>Initializes a stale-detecting Spatial moment payload.</summary>
    public SpatialMomentCandidate(long expectedSpatialRevision, long momentOrdinal)
    {
        if (expectedSpatialRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSpatialRevision));
        }

        if (momentOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(momentOrdinal));
        }

        ExpectedSpatialRevision = expectedSpatialRevision;
        MomentOrdinal = momentOrdinal;
    }

    /// <summary>Gets the exact state revision used to produce this candidate.</summary>
    public long ExpectedSpatialRevision { get; }

    /// <summary>Gets the persistent SpatialMoment identity.</summary>
    public long MomentOrdinal { get; }
}

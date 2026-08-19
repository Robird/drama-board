using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Identifies the topology edge used by one current movement leg.</summary>
public enum SpatialEdgeKind
{
    /// <summary>A four-direction edge between adjacent cells in one map.</summary>
    Orthogonal,

    /// <summary>An explicit directed portal edge.</summary>
    Portal,
}

/// <summary>Stores one already-started positive-duration movement step.</summary>
public sealed record CurrentLeg
{
    /// <summary>Initializes a current movement leg.</summary>
    public CurrentLeg(
        CellRef from,
        CellRef to,
        SpatialEdgeKind edgeKind,
        PortalId? portalId,
        ModelTime startedAt,
        ModelTime due,
        long journeyGeneration)
    {
        if (!Enum.IsDefined(edgeKind))
        {
            throw new ArgumentOutOfRangeException(nameof(edgeKind));
        }

        if ((edgeKind == SpatialEdgeKind.Portal) != portalId.HasValue)
        {
            throw new ArgumentException(
                "Portal identifier must exist exactly for portal movement legs.",
                nameof(portalId));
        }

        if (due <= startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(due), "Current leg duration must be positive.");
        }

        if (journeyGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(journeyGeneration),
                "Journey generation must be positive.");
        }

        From = from;
        To = to;
        EdgeKind = edgeKind;
        PortalId = portalId;
        StartedAt = startedAt;
        Due = due;
        JourneyGeneration = journeyGeneration;
    }

    /// <summary>Gets the authoritative cell until the leg completes.</summary>
    public CellRef From { get; }

    /// <summary>Gets the destination cell committed when the leg completes.</summary>
    public CellRef To { get; }

    /// <summary>Gets the topology edge kind.</summary>
    public SpatialEdgeKind EdgeKind { get; }

    /// <summary>Gets the portal identifier exactly for a portal leg.</summary>
    public PortalId? PortalId { get; }

    /// <summary>Gets the absolute start time.</summary>
    public ModelTime StartedAt { get; }

    /// <summary>Gets the absolute completion time.</summary>
    public ModelTime Due { get; }

    /// <summary>Gets the movement generation that owns this leg.</summary>
    public long JourneyGeneration { get; }
}

/// <summary>Stores one active semantic movement goal and its current leg.</summary>
public sealed record JourneyState
{
    /// <summary>Initializes a complete active journey.</summary>
    public JourneyState(
        JourneyId id,
        EntityId entityId,
        MoveGoal goal,
        long generation,
        CurrentLeg currentLeg)
    {
        if (id.Value <= 0)
        {
            throw new ArgumentException("Journey identifier must be initialized.", nameof(id));
        }

        if (entityId.Value <= 0)
        {
            throw new ArgumentException("Entity identifier must be initialized.", nameof(entityId));
        }

        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(currentLeg);
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Journey generation must be positive.");
        }

        if (currentLeg.JourneyGeneration != generation)
        {
            throw new ArgumentException("Current leg generation must match its journey.", nameof(currentLeg));
        }

        Id = id;
        EntityId = entityId;
        Goal = goal;
        Generation = generation;
        CurrentLeg = currentLeg;
    }

    /// <summary>Gets the persistent allocated journey identifier.</summary>
    public JourneyId Id { get; }

    /// <summary>Gets the moving entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the semantic or geometric goal.</summary>
    public MoveGoal Goal { get; }

    /// <summary>Gets the owning entity movement generation.</summary>
    public long Generation { get; }

    /// <summary>Gets the exact current or just-completed leg.</summary>
    public CurrentLeg CurrentLeg { get; }

    internal JourneyState With(MoveGoal goal, long generation, CurrentLeg currentLeg) =>
        new(Id, EntityId, goal, generation, currentLeg);

    internal JourneyState WithCurrentLeg(CurrentLeg currentLeg) =>
        new(Id, EntityId, Goal, Generation, currentLeg);
}

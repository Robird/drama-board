using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Base payload for one authoritative Graph Spatial state change.</summary>
public abstract record GraphSpatialFact;

public sealed record EntityPlacedFact(EntityId EntityId, PlaceId PlaceId) : GraphSpatialFact;

public sealed record EntityRemovedFact(EntityId EntityId) : GraphSpatialFact;

public sealed record TraversalStartedFact(
    EntityId EntityId,
    PassageId PassageId,
    PlaceId FromPlaceId,
    long SpeedSnapshot) : GraphSpatialFact;

public sealed record TraversalArrivedFact : GraphSpatialFact
{
    public TraversalArrivedFact(EntityId EntityId, long ExpectedMovementGeneration)
    {
        this.EntityId = EntityId;
        this.ExpectedMovementGeneration = ExpectedMovementGeneration;
    }

    public EntityId EntityId { get; }

    public long ExpectedMovementGeneration { get; }
}

public sealed record PassageEntryAccessChangedFact(
    PassageId PassageId,
    PassageEntryAccess ResultAccess) : GraphSpatialFact;

public sealed record PassageEntryChangeScheduledFact(
    PassageId PassageId,
    ModelTime Due,
    PassageEntryPatch Patch) : GraphSpatialFact;

public sealed record ScheduledPassageEntryChangeAppliedFact(
    PassageId PassageId,
    ModelTime Due) : GraphSpatialFact;

using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Base payload for one authoritative Graph Spatial state change.</summary>
[DurableType("DramaBoard.Spatial.GraphSpatialFact", 1)]
public abstract partial class GraphSpatialFact : DurableBase, IEquatable<GraphSpatialFact>
{
    public bool Equals(GraphSpatialFact? other) => other is not null && Equals((object)other);

    public abstract override bool Equals(object? obj);

    public abstract override int GetHashCode();

    public static bool operator ==(GraphSpatialFact? left, GraphSpatialFact? right) => Equals(left, right);

    public static bool operator !=(GraphSpatialFact? left, GraphSpatialFact? right) => !Equals(left, right);
}

[DurableType("DramaBoard.Spatial.EntityPlacedFact", 1)]
public sealed partial class EntityPlacedFact : GraphSpatialFact, IEquatable<EntityPlacedFact>
{
    [DurableField(1)] private readonly EntityId _entityId;
    [DurableField(2)] private readonly PlaceId _placeId;

    public EntityPlacedFact(EntityId EntityId, PlaceId PlaceId)
    {
        _entityId = EntityId;
        _placeId = PlaceId;
    }

    public EntityId EntityId => _entityId;

    public PlaceId PlaceId => _placeId;

    public bool Equals(EntityPlacedFact? other) => other is not null &&
        EntityId == other.EntityId && PlaceId == other.PlaceId;

    public override bool Equals(object? obj) => Equals(obj as EntityPlacedFact);

    public override int GetHashCode() => HashCode.Combine(typeof(EntityPlacedFact), EntityId, PlaceId);
}

[DurableType("DramaBoard.Spatial.EntityRemovedFact", 1)]
public sealed partial class EntityRemovedFact : GraphSpatialFact, IEquatable<EntityRemovedFact>
{
    [DurableField(1)] private readonly EntityId _entityId;

    public EntityRemovedFact(EntityId EntityId) => _entityId = EntityId;

    public EntityId EntityId => _entityId;

    public bool Equals(EntityRemovedFact? other) => other is not null && EntityId == other.EntityId;

    public override bool Equals(object? obj) => Equals(obj as EntityRemovedFact);

    public override int GetHashCode() => HashCode.Combine(typeof(EntityRemovedFact), EntityId);
}

[DurableType("DramaBoard.Spatial.TraversalStartedFact", 1)]
public sealed partial class TraversalStartedFact : GraphSpatialFact, IEquatable<TraversalStartedFact>
{
    [DurableField(1)] private readonly EntityId _entityId;
    [DurableField(2)] private readonly PassageId _passageId;
    [DurableField(3)] private readonly PlaceId _fromPlaceId;
    [DurableField(4)] private readonly long _speedSnapshot;

    public TraversalStartedFact(EntityId EntityId, PassageId PassageId, PlaceId FromPlaceId, long SpeedSnapshot)
    {
        _entityId = EntityId;
        _passageId = PassageId;
        _fromPlaceId = FromPlaceId;
        _speedSnapshot = SpeedSnapshot;
    }

    public EntityId EntityId => _entityId;

    public PassageId PassageId => _passageId;

    public PlaceId FromPlaceId => _fromPlaceId;

    public long SpeedSnapshot => _speedSnapshot;

    public bool Equals(TraversalStartedFact? other) => other is not null &&
        EntityId == other.EntityId && PassageId == other.PassageId &&
        FromPlaceId == other.FromPlaceId && SpeedSnapshot == other.SpeedSnapshot;

    public override bool Equals(object? obj) => Equals(obj as TraversalStartedFact);

    public override int GetHashCode() => HashCode.Combine(
        typeof(TraversalStartedFact), EntityId, PassageId, FromPlaceId, SpeedSnapshot);
}

[DurableType("DramaBoard.Spatial.TraversalReversedFact", 1)]
public sealed partial class TraversalReversedFact : GraphSpatialFact, IEquatable<TraversalReversedFact>
{
    [DurableField(1)] private readonly EntityId _entityId;
    [DurableField(2)] private readonly long _expectedMovementGeneration;

    public TraversalReversedFact(EntityId EntityId, long ExpectedMovementGeneration)
    {
        _entityId = EntityId;
        _expectedMovementGeneration = ExpectedMovementGeneration;
    }

    public EntityId EntityId => _entityId;

    public long ExpectedMovementGeneration => _expectedMovementGeneration;

    public bool Equals(TraversalReversedFact? other) => other is not null &&
        EntityId == other.EntityId && ExpectedMovementGeneration == other.ExpectedMovementGeneration;

    public override bool Equals(object? obj) => Equals(obj as TraversalReversedFact);

    public override int GetHashCode() => HashCode.Combine(
        typeof(TraversalReversedFact), EntityId, ExpectedMovementGeneration);
}

[DurableType("DramaBoard.Spatial.PassageContactOccurredFact", 1)]
public sealed partial class PassageContactOccurredFact : GraphSpatialFact, IEquatable<PassageContactOccurredFact>
{
    [DurableField(1)] private readonly PassageContactKey _contactKey;
    [DurableField(2)] private readonly PassageContactKind _kind;

    public PassageContactOccurredFact(PassageContactKey ContactKey, PassageContactKind Kind)
    {
        _contactKey = ContactKey;
        _kind = Kind;
    }

    public PassageContactKey ContactKey => _contactKey;

    public PassageContactKind Kind => _kind;

    public bool Equals(PassageContactOccurredFact? other) => other is not null &&
        EqualityComparer<PassageContactKey>.Default.Equals(ContactKey, other.ContactKey) && Kind == other.Kind;

    public override bool Equals(object? obj) => Equals(obj as PassageContactOccurredFact);

    public override int GetHashCode() => HashCode.Combine(typeof(PassageContactOccurredFact), ContactKey, Kind);
}

[DurableType("DramaBoard.Spatial.TraversalArrivedFact", 1)]
public sealed partial class TraversalArrivedFact : GraphSpatialFact, IEquatable<TraversalArrivedFact>
{
    [DurableField(1)] private readonly EntityId _entityId;
    [DurableField(2)] private readonly long _expectedMovementGeneration;

    public TraversalArrivedFact(EntityId EntityId, long ExpectedMovementGeneration)
    {
        _entityId = EntityId;
        _expectedMovementGeneration = ExpectedMovementGeneration;
    }

    public EntityId EntityId => _entityId;

    public long ExpectedMovementGeneration => _expectedMovementGeneration;

    public bool Equals(TraversalArrivedFact? other) => other is not null &&
        EntityId == other.EntityId && ExpectedMovementGeneration == other.ExpectedMovementGeneration;

    public override bool Equals(object? obj) => Equals(obj as TraversalArrivedFact);

    public override int GetHashCode() => HashCode.Combine(
        typeof(TraversalArrivedFact), EntityId, ExpectedMovementGeneration);
}

[DurableType("DramaBoard.Spatial.PassageEntryAccessChangedFact", 1)]
public sealed partial class PassageEntryAccessChangedFact : GraphSpatialFact, IEquatable<PassageEntryAccessChangedFact>
{
    [DurableField(1)] private readonly PassageId _passageId;
    [DurableField(2)] private readonly PassageEntryAccess _resultAccess;

    public PassageEntryAccessChangedFact(PassageId PassageId, PassageEntryAccess ResultAccess)
    {
        _passageId = PassageId;
        _resultAccess = ResultAccess;
    }

    public PassageId PassageId => _passageId;

    public PassageEntryAccess ResultAccess => _resultAccess;

    public bool Equals(PassageEntryAccessChangedFact? other) => other is not null &&
        PassageId == other.PassageId && ResultAccess == other.ResultAccess;

    public override bool Equals(object? obj) => Equals(obj as PassageEntryAccessChangedFact);

    public override int GetHashCode() => HashCode.Combine(typeof(PassageEntryAccessChangedFact), PassageId, ResultAccess);
}

[DurableType("DramaBoard.Spatial.PassageEntryChangeScheduledFact", 1)]
public sealed partial class PassageEntryChangeScheduledFact : GraphSpatialFact, IEquatable<PassageEntryChangeScheduledFact>
{
    [DurableField(1)] private readonly PassageId _passageId;
    [DurableField(2)] private readonly ModelTime _due;
    [DurableField(3)] private readonly PassageEntryPatch _patch;

    public PassageEntryChangeScheduledFact(PassageId PassageId, ModelTime Due, PassageEntryPatch Patch)
    {
        _passageId = PassageId;
        _due = Due;
        _patch = Patch;
    }

    public PassageId PassageId => _passageId;

    public ModelTime Due => _due;

    public PassageEntryPatch Patch => _patch;

    public bool Equals(PassageEntryChangeScheduledFact? other) => other is not null &&
        PassageId == other.PassageId && Due == other.Due && Patch == other.Patch;

    public override bool Equals(object? obj) => Equals(obj as PassageEntryChangeScheduledFact);

    public override int GetHashCode() => HashCode.Combine(typeof(PassageEntryChangeScheduledFact), PassageId, Due, Patch);
}

[DurableType("DramaBoard.Spatial.ScheduledPassageEntryChangeAppliedFact", 1)]
public sealed partial class ScheduledPassageEntryChangeAppliedFact : GraphSpatialFact, IEquatable<ScheduledPassageEntryChangeAppliedFact>
{
    [DurableField(1)] private readonly PassageId _passageId;
    [DurableField(2)] private readonly ModelTime _due;

    public ScheduledPassageEntryChangeAppliedFact(PassageId PassageId, ModelTime Due)
    {
        _passageId = PassageId;
        _due = Due;
    }

    public PassageId PassageId => _passageId;

    public ModelTime Due => _due;

    public bool Equals(ScheduledPassageEntryChangeAppliedFact? other) => other is not null &&
        PassageId == other.PassageId && Due == other.Due;

    public override bool Equals(object? obj) => Equals(obj as ScheduledPassageEntryChangeAppliedFact);

    public override int GetHashCode() => HashCode.Combine(typeof(ScheduledPassageEntryChangeAppliedFact), PassageId, Due);
}

using DramaBoard.Kernel.Time;
using Atelia.DurableGraph;

namespace DramaBoard.Spatial;

/// <summary>Describes one entity's location in Genesis.</summary>
public sealed record EntityPlacement
{
    public EntityPlacement(EntityId entityId, PlaceId placeId)
    {
        SpatialIdentifier.Require(entityId, nameof(entityId));
        SpatialIdentifier.Require(placeId, nameof(placeId));
        EntityId = entityId;
        PlaceId = placeId;
    }

    public EntityId EntityId { get; }

    public PlaceId PlaceId { get; }
}

/// <summary>Stores one entity and its current exclusive location.</summary>
[DurableType("DramaBoard.Spatial.SpatialEntity", 1)]
public sealed partial class SpatialEntity : IDurableObject, IEquatable<SpatialEntity>
{
    [DurableField(1)] private EntityId _id;
    [DurableField(2)] private long _movementGeneration;
    [DurableField(3)] private SpatialLocation _location = null!;
    public SpatialEntity(EntityId id, long movementGeneration, SpatialLocation location)
    {
        SpatialIdentifier.Require(id, nameof(id));
        if (movementGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movementGeneration),
                "Movement generation cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(location);
        _id = id; _movementGeneration = movementGeneration; _location = location;
    }

    public EntityId Id => _id;

    public long MovementGeneration => _movementGeneration;

    public SpatialLocation Location => _location;

    public bool Equals(SpatialEntity? other) => other is not null &&
        Id == other.Id && MovementGeneration == other.MovementGeneration && Equals(Location, other.Location);
    public override bool Equals(object? obj) => Equals(obj as SpatialEntity);
    public override int GetHashCode() => HashCode.Combine(Id, MovementGeneration, Location);
    public static bool operator ==(SpatialEntity? left, SpatialEntity? right) => Equals(left, right);
    public static bool operator !=(SpatialEntity? left, SpatialEntity? right) => !Equals(left, right);
}

/// <summary>Stores a sparse complete replacement of one passage's two entry bits.</summary>
[DurableType("DramaBoard.Spatial.PassageEntryAccessOverride", 1)]
public sealed partial class PassageEntryAccessOverride : IDurableObject, IEquatable<PassageEntryAccessOverride>
{
    [DurableField(1)] private PassageId _passageId;
    [DurableField(2)] private PassageEntryAccess _access;
    public PassageEntryAccessOverride(PassageId passageId, PassageEntryAccess access)
    {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        _passageId = passageId; _access = access;
    }

    public PassageId PassageId => _passageId;

    public PassageEntryAccess Access => _access;
    public bool Equals(PassageEntryAccessOverride? other) => other is not null && PassageId == other.PassageId && Access == other.Access;
    public override bool Equals(object? obj) => Equals(obj as PassageEntryAccessOverride);
    public override int GetHashCode() => HashCode.Combine(PassageId, Access);
    public static bool operator ==(PassageEntryAccessOverride? left, PassageEntryAccessOverride? right) => Equals(left, right);
    public static bool operator !=(PassageEntryAccessOverride? left, PassageEntryAccessOverride? right) => !Equals(left, right);
}

/// <summary>Stores one future entry-access patch, uniquely addressed by passage and due time.</summary>
[DurableType("DramaBoard.Spatial.ScheduledPassageEntryChange", 1)]
public sealed partial class ScheduledPassageEntryChange : IDurableObject, IEquatable<ScheduledPassageEntryChange>
{
    [DurableField(1)] private PassageId _passageId;
    [DurableField(2)] private ModelTime _due;
    [DurableField(3)] private PassageEntryPatch _patch;
    public ScheduledPassageEntryChange(PassageId passageId, ModelTime due, PassageEntryPatch patch)
    {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        PassageEntryPatch.Validate(patch, nameof(patch));
        _passageId = passageId; _due = due; _patch = patch;
    }

    public PassageId PassageId => _passageId;

    public ModelTime Due => _due;

    public PassageEntryPatch Patch => _patch;
    public bool Equals(ScheduledPassageEntryChange? other) => other is not null && PassageId == other.PassageId && Due == other.Due && Patch == other.Patch;
    public override bool Equals(object? obj) => Equals(obj as ScheduledPassageEntryChange);
    public override int GetHashCode() => HashCode.Combine(PassageId, Due, Patch);
    public static bool operator ==(ScheduledPassageEntryChange? left, ScheduledPassageEntryChange? right) => Equals(left, right);
    public static bool operator !=(ScheduledPassageEntryChange? left, ScheduledPassageEntryChange? right) => !Equals(left, right);
}

/// <summary>Owns canonical immutable dynamic state for one Graph Spatial world.</summary>
[DurableType("DramaBoard.Spatial.GraphSpatialState", 1)]
public sealed partial class GraphSpatialState : IDurableObject, IEquatable<GraphSpatialState>
{
    [DurableField(1)] private List<SpatialEntity> _entities = [];
    [DurableField(2)] private List<PassageEntryAccessOverride> _passageEntryAccessOverrides = [];
    [DurableField(3)] private List<ScheduledPassageEntryChange> _scheduledPassageEntryChanges = [];
    [DurableField(4)] private List<PassageContactKey> _consumedContacts = [];
    private GraphSpatialState(
        IEnumerable<SpatialEntity> entities,
        IEnumerable<PassageEntryAccessOverride> passageEntryAccessOverrides,
        IEnumerable<ScheduledPassageEntryChange> scheduledPassageEntryChanges,
        IEnumerable<PassageContactKey> consumedContacts)
    {
        SpatialEntity[] entityArray = Canonicalize(
            entities,
            entity => entity.Id,
            "entity");
        PassageEntryAccessOverride[] overrideArray = Canonicalize(
            passageEntryAccessOverrides,
            value => value.PassageId,
            "passage entry access override");
        ScheduledPassageEntryChange[] scheduleArray = CanonicalizeSchedules(scheduledPassageEntryChanges);
        PassageContactKey[] contactArray = Canonicalize(
            consumedContacts,
            value => value,
            "consumed contact");

        _entities = [.. entityArray];
        _passageEntryAccessOverrides = [.. overrideArray];
        _scheduledPassageEntryChanges = [.. scheduleArray];
        _consumedContacts = [.. contactArray];
    }

    public IReadOnlyList<SpatialEntity> Entities => _entities.AsReadOnly();

    public IReadOnlyList<PassageEntryAccessOverride> PassageEntryAccessOverrides => _passageEntryAccessOverrides.AsReadOnly();

    public IReadOnlyList<ScheduledPassageEntryChange> ScheduledPassageEntryChanges => _scheduledPassageEntryChanges.AsReadOnly();

    public IReadOnlyList<PassageContactKey> ConsumedContacts => _consumedContacts.AsReadOnly();

    public static GraphSpatialState Create(
        GraphDefinition definition,
        IEnumerable<EntityPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(placements);
        EntityPlacement[] placementArray = [.. placements];
        if (placementArray.Any(placement => placement is null))
        {
            throw new ArgumentException("Entity placements cannot contain null entries.", nameof(placements));
        }

        SpatialEntity[] entities =
        [
            .. placementArray.Select(placement =>
            {
                if (!definition.Contains(placement.PlaceId))
                {
                    throw new ArgumentException(
                        $"Entity '{placement.EntityId}' references undefined place '{placement.PlaceId}'.",
                        nameof(placements));
                }

                return new SpatialEntity(
                    placement.EntityId,
                    movementGeneration: 0,
                    new AtPlaceLocation(placement.PlaceId));
            }),
        ];
        var state = new GraphSpatialState(entities, [], [], []);
        GraphSpatialStateValidator.ValidateComplete(definition, state);
        return state;
    }

    /// <summary>
    /// Restores one complete dynamic state snapshot against immutable graph content.
    /// </summary>
    public static GraphSpatialState Restore(
        GraphDefinition definition,
        IEnumerable<SpatialEntity> entities,
        IEnumerable<PassageEntryAccessOverride> passageEntryAccessOverrides,
        IEnumerable<ScheduledPassageEntryChange> scheduledPassageEntryChanges,
        IEnumerable<PassageContactKey> consumedContacts)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var state = new GraphSpatialState(
            entities,
            passageEntryAccessOverrides,
            scheduledPassageEntryChanges,
            consumedContacts);
        GraphSpatialStateValidator.ValidateComplete(definition, state);
        return state;
    }

    public bool TryGetEntity(EntityId entityId, out SpatialEntity? entity)
    {
        entity = Entities.SingleOrDefault(value => value.Id == entityId);
        return entity is not null;
    }

    public bool Equals(GraphSpatialState? other) =>
        other is not null &&
        Entities.SequenceEqual(other.Entities) &&
        PassageEntryAccessOverrides.SequenceEqual(other.PassageEntryAccessOverrides) &&
        ScheduledPassageEntryChanges.SequenceEqual(other.ScheduledPassageEntryChanges) &&
        ConsumedContacts.SequenceEqual(other.ConsumedContacts);

    public override bool Equals(object? obj) => Equals(obj as GraphSpatialState);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (SpatialEntity entity in Entities)
        {
            hash.Add(entity);
        }

        foreach (PassageEntryAccessOverride value in PassageEntryAccessOverrides)
        {
            hash.Add(value);
        }

        foreach (ScheduledPassageEntryChange value in ScheduledPassageEntryChanges)
        {
            hash.Add(value);
        }

        foreach (PassageContactKey value in ConsumedContacts)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    internal GraphSpatialState Rebuild(
        IEnumerable<SpatialEntity>? entities = null,
        IEnumerable<PassageEntryAccessOverride>? passageEntryAccessOverrides = null,
        IEnumerable<ScheduledPassageEntryChange>? scheduledPassageEntryChanges = null,
        IEnumerable<PassageContactKey>? consumedContacts = null) =>
        new(
            entities ?? Entities,
            passageEntryAccessOverrides ?? PassageEntryAccessOverrides,
            scheduledPassageEntryChanges ?? ScheduledPassageEntryChanges,
            consumedContacts ?? ConsumedContacts);

    internal PassageEntryAccessOverride? FindOverride(PassageId passageId) =>
        PassageEntryAccessOverrides.SingleOrDefault(value => value.PassageId == passageId);

    internal ScheduledPassageEntryChange? FindSchedule(PassageId passageId, ModelTime due) =>
        ScheduledPassageEntryChanges.SingleOrDefault(value =>
            value.PassageId == passageId && value.Due == due);

    private static T[] Canonicalize<T, TId>(
        IEnumerable<T> values,
        Func<T, TId> id,
        string description)
        where T : class
        where TId : IComparable<TId>
    {
        ArgumentNullException.ThrowIfNull(values);
        T[] array = [.. values];
        if (array.Any(value => value is null))
        {
            throw new InvalidOperationException($"Graph Spatial {description} collection contains null.");
        }

        T[] canonical = [.. array.OrderBy(id)];
        for (int index = 1; index < canonical.Length; index++)
        {
            if (id(canonical[index - 1]).CompareTo(id(canonical[index])) == 0)
            {
                throw new InvalidOperationException($"Graph Spatial {description} identities must be unique.");
            }
        }

        return canonical;
    }

    private static ScheduledPassageEntryChange[] CanonicalizeSchedules(
        IEnumerable<ScheduledPassageEntryChange> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ScheduledPassageEntryChange[] array = [.. schedules];
        if (array.Any(value => value is null))
        {
            throw new InvalidOperationException("Graph Spatial schedule collection contains null.");
        }

        ScheduledPassageEntryChange[] canonical =
        [
            .. array.OrderBy(value => value.PassageId).ThenBy(value => value.Due),
        ];
        for (int index = 1; index < canonical.Length; index++)
        {
            ScheduledPassageEntryChange previous = canonical[index - 1];
            ScheduledPassageEntryChange current = canonical[index];
            if (previous.PassageId == current.PassageId && previous.Due == current.Due)
            {
                throw new InvalidOperationException(
                    "Graph Spatial schedules must be unique by PassageId and Due.");
            }
        }

        return canonical;
    }
}

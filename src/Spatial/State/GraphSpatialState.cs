using DramaBoard.Kernel.Time;

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
public sealed record SpatialEntity
{
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
        Id = id;
        MovementGeneration = movementGeneration;
        Location = location;
    }

    public EntityId Id { get; }

    public long MovementGeneration { get; }

    public SpatialLocation Location { get; }
}

/// <summary>Stores a sparse complete replacement of one passage's two entry bits.</summary>
public sealed record PassageEntryAccessOverride
{
    public PassageEntryAccessOverride(PassageId passageId, PassageEntryAccess access)
    {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        PassageId = passageId;
        Access = access;
    }

    public PassageId PassageId { get; }

    public PassageEntryAccess Access { get; }
}

/// <summary>Stores one future entry-access patch, uniquely addressed by passage and due time.</summary>
public sealed record ScheduledPassageEntryChange
{
    public ScheduledPassageEntryChange(PassageId passageId, ModelTime due, PassageEntryPatch patch)
    {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        PassageEntryPatch.Validate(patch, nameof(patch));
        PassageId = passageId;
        Due = due;
        Patch = patch;
    }

    public PassageId PassageId { get; }

    public ModelTime Due { get; }

    public PassageEntryPatch Patch { get; }
}

/// <summary>Owns canonical immutable dynamic state for one Graph Spatial world.</summary>
public sealed class GraphSpatialState : IEquatable<GraphSpatialState>
{
    private GraphSpatialState(
        IEnumerable<SpatialEntity> entities,
        IEnumerable<PassageEntryAccessOverride> passageEntryAccessOverrides,
        IEnumerable<ScheduledPassageEntryChange> scheduledPassageEntryChanges)
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

        Entities = Array.AsReadOnly(entityArray);
        PassageEntryAccessOverrides = Array.AsReadOnly(overrideArray);
        ScheduledPassageEntryChanges = Array.AsReadOnly(scheduleArray);
    }

    public IReadOnlyList<SpatialEntity> Entities { get; }

    public IReadOnlyList<PassageEntryAccessOverride> PassageEntryAccessOverrides { get; }

    public IReadOnlyList<ScheduledPassageEntryChange> ScheduledPassageEntryChanges { get; }

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
        var state = new GraphSpatialState(entities, [], []);
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
        ScheduledPassageEntryChanges.SequenceEqual(other.ScheduledPassageEntryChanges);

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

        return hash.ToHashCode();
    }

    internal GraphSpatialState Rebuild(
        IEnumerable<SpatialEntity>? entities = null,
        IEnumerable<PassageEntryAccessOverride>? passageEntryAccessOverrides = null,
        IEnumerable<ScheduledPassageEntryChange>? scheduledPassageEntryChanges = null) =>
        new(
            entities ?? Entities,
            passageEntryAccessOverrides ?? PassageEntryAccessOverrides,
            scheduledPassageEntryChanges ?? ScheduledPassageEntryChanges);

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

using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Reads objective relations from one committed Graph Spatial state.</summary>
public sealed class SpatialQueries
{
    private readonly GraphDefinition _definition;

    public SpatialQueries(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public SpatialLocationView GetLocation(
        GraphSpatialState state,
        EntityId entityId,
        ModelTime at)
    {
        RequireState(state);
        SpatialEntity entity = RequireEntityForQuery(state, entityId);
        return entity.Location switch
        {
            AtPlaceLocation atPlace => new AtPlaceView(atPlace.PlaceId),
            TraversingLocation traversal => CreateTraversingView(traversal, at),
            _ => throw new InvalidOperationException(
                $"Entity '{entityId}' has unsupported location '{entity.Location.GetType().Name}'."),
        };
    }

    public PassageEntryAccess GetPassageEntryAccess(
        GraphSpatialState state,
        PassageId passageId)
    {
        RequireState(state);
        PassageDefinition passage = RequirePassageForQuery(passageId);
        return EffectiveGraph.EntryAccess(_definition, state, passage);
    }

    public IReadOnlyList<PassageExit> GetExits(
        GraphSpatialState state,
        PlaceId placeId,
        long speedSnapshot)
    {
        RequireState(state);
        if (!_definition.Contains(placeId))
        {
            throw new KeyNotFoundException($"Place '{placeId}' does not exist.");
        }

        if (speedSnapshot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedSnapshot), "Travel speed must be positive.");
        }

        PassageExit[] exits =
        [
            .. _definition.Passages
                .Select(passage => CreateExit(state, passage, placeId, speedSnapshot))
                .Where(value => value is not null)
                .Select(value => value!),
        ];
        return Array.AsReadOnly(exits);
    }

    public IReadOnlyList<EntityId> GetCoLocatedEntities(
        GraphSpatialState state,
        EntityId entityId)
    {
        RequireState(state);
        SpatialEntity entity = RequireEntityForQuery(state, entityId);
        if (entity.Location is not AtPlaceLocation atPlace)
        {
            return Array.Empty<EntityId>();
        }

        EntityId[] colocated =
        [
            .. state.Entities
                .Where(other =>
                    other.Id != entity.Id &&
                    other.Location is AtPlaceLocation otherPlace &&
                    otherPlace.PlaceId == atPlace.PlaceId)
                .Select(other => other.Id),
        ];
        return Array.AsReadOnly(colocated);
    }

    public IReadOnlyList<SamePassageRelation> GetSamePassageRelations(
        GraphSpatialState state,
        EntityId entityId,
        ModelTime at)
    {
        RequireState(state);
        SpatialEntity entity = RequireEntityForQuery(state, entityId);
        if (entity.Location is not TraversingLocation traversal)
        {
            return Array.Empty<SamePassageRelation>();
        }

        PassageDefinition passage = _definition.GetPassage(traversal.PassageId);
        long ownOffset = SpatialMath.OffsetAt(passage, traversal, at);
        var relations = new List<SamePassageRelation>();
        foreach (SpatialEntity other in state.Entities)
        {
            if (other.Id == entity.Id ||
                other.Location is not TraversingLocation otherTraversal ||
                otherTraversal.PassageId != traversal.PassageId ||
                at < otherTraversal.StartedAt ||
                at > otherTraversal.ArrivalDue)
            {
                continue;
            }

            long otherOffset = SpatialMath.OffsetAt(passage, otherTraversal, at);
            bool coTraveling = ownOffset == otherOffset &&
                traversal.ToPlaceId == otherTraversal.ToPlaceId &&
                traversal.SpeedSnapshot == otherTraversal.SpeedSnapshot &&
                traversal.ArrivalDue == otherTraversal.ArrivalDue;
            relations.Add(new SamePassageRelation(other.Id, otherOffset, coTraveling));
        }

        return Array.AsReadOnly(relations.ToArray());
    }

    public IReadOnlyList<EntityId> GetCoTravelingEntities(
        GraphSpatialState state,
        EntityId entityId,
        ModelTime at)
    {
        EntityId[] values =
        [
            .. GetSamePassageRelations(state, entityId, at)
                .Where(relation => relation.IsCoTraveling)
                .Select(relation => relation.OtherEntityId),
        ];
        return Array.AsReadOnly(values);
    }

    private TraversingView CreateTraversingView(TraversingLocation traversal, ModelTime at)
    {
        PassageDefinition passage = _definition.GetPassage(traversal.PassageId);
        return new TraversingView(
            traversal.PassageId,
            traversal.FromPlaceId,
            traversal.ToPlaceId,
            SpatialMath.OffsetAt(passage, traversal, at),
            traversal.SpeedSnapshot,
            traversal.ArrivalDue);
    }

    private PassageExit? CreateExit(
        GraphSpatialState state,
        PassageDefinition passage,
        PlaceId placeId,
        long speedSnapshot)
    {
        if (!EffectiveGraph.TryResolveDirection(
                _definition,
                state,
                passage,
                placeId,
                out PlaceId destination,
                out bool entryAllowed))
        {
            return null;
        }

        return new PassageExit(
            passage.Id,
            destination,
            entryAllowed,
            SpatialMath.TravelDuration(passage.Length, speedSnapshot));
    }

    private SpatialEntity RequireEntityForQuery(GraphSpatialState state, EntityId entityId) =>
        state.TryGetEntity(entityId, out SpatialEntity? entity)
            ? entity!
            : throw new KeyNotFoundException($"Entity '{entityId}' does not exist.");

    private PassageDefinition RequirePassageForQuery(PassageId passageId)
    {
        if (!_definition.Contains(passageId))
        {
            throw new KeyNotFoundException($"Passage '{passageId}' does not exist.");
        }

        return _definition.GetPassage(passageId);
    }

    private void RequireState(GraphSpatialState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        GraphSpatialStateValidator.ValidateComplete(_definition, state);
    }
}

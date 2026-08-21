using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Plans objective Graph Spatial facts without queueing, committing, or performing I/O.</summary>
public sealed class SpatialPlanner
{
    private readonly GraphDefinition _definition;

    public SpatialPlanner(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public SpatialPlanResult TryPlaceEntity(
        GraphSpatialState state,
        EntityId entityId,
        PlaceId placeId)
    {
        RequireState(state);
        if (string.IsNullOrWhiteSpace(entityId.Value))
        {
            return Rejected("entity-id-uninitialized");
        }

        if (!_definition.Contains(placeId))
        {
            return Rejected("place-not-found");
        }

        if (state.TryGetEntity(entityId, out _))
        {
            return Rejected("entity-already-exists");
        }

        return Accepted(new EntityPlacedFact(entityId, placeId));
    }

    public SpatialPlanResult TryRemoveEntity(GraphSpatialState state, EntityId entityId)
    {
        RequireState(state);
        return state.TryGetEntity(entityId, out _)
            ? Accepted(new EntityRemovedFact(entityId))
            : Rejected("entity-not-found");
    }

    public SpatialPlanResult TryStartTraversal(
        GraphSpatialState state,
        EntityId entityId,
        PassageId passageId,
        long speedSnapshot,
        ModelTime at)
    {
        RequireState(state);
        if (!state.TryGetEntity(entityId, out SpatialEntity? entity))
        {
            return Rejected("entity-not-found");
        }

        if (entity!.Location is not AtPlaceLocation atPlace)
        {
            return Rejected("entity-not-at-place");
        }

        if (!_definition.Contains(passageId))
        {
            return Rejected("passage-not-found");
        }

        if (speedSnapshot <= 0)
        {
            return Rejected("invalid-speed");
        }

        PassageDefinition passage = _definition.GetPassage(passageId);
        if (!EffectiveGraph.TryResolveDirection(
                _definition,
                state,
                passage,
                atPlace.PlaceId,
                out _,
                out bool entryAllowed))
        {
            return Rejected("place-not-passage-endpoint");
        }

        if (!entryAllowed)
        {
            return Rejected("entry-closed");
        }

        try
        {
            _ = checked(entity.MovementGeneration + 1);
            _ = SpatialMath.ArrivalDue(at, passage.Length, speedSnapshot);
        }
        catch (OverflowException)
        {
            return Rejected("time-overflow");
        }

        return Accepted(new TraversalStartedFact(entityId, passageId, atPlace.PlaceId, speedSnapshot));
    }

    public SpatialPlanResult TrySetPassageEntryAccess(
        GraphSpatialState state,
        PassageId passageId,
        PassageEntryPatch patch)
    {
        RequireState(state);
        if (!_definition.Contains(passageId))
        {
            return Rejected("passage-not-found");
        }

        if (!IsValid(patch))
        {
            return Rejected("empty-entry-patch");
        }

        PassageDefinition passage = _definition.GetPassage(passageId);
        PassageEntryAccess current = EffectiveGraph.EntryAccess(_definition, state, passage);
        return Accepted(new PassageEntryAccessChangedFact(passageId, patch.Apply(current)));
    }

    public SpatialPlanResult TrySchedulePassageEntryChange(
        GraphSpatialState state,
        PassageId passageId,
        ModelTime due,
        PassageEntryPatch patch,
        ModelTime at)
    {
        RequireState(state);
        if (!_definition.Contains(passageId))
        {
            return Rejected("passage-not-found");
        }

        if (!IsValid(patch))
        {
            return Rejected("empty-entry-patch");
        }

        if (due <= at)
        {
            return Rejected("schedule-not-in-future");
        }

        if (state.FindSchedule(passageId, due) is not null)
        {
            return Rejected("schedule-already-exists");
        }

        return Accepted(new PassageEntryChangeScheduledFact(passageId, due, patch));
    }

    private void RequireState(GraphSpatialState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        GraphSpatialStateValidator.ValidateComplete(_definition, state);
    }

    private static bool IsValid(PassageEntryPatch patch) =>
        patch.EnterableFromA is not null || patch.EnterableFromB is not null;

    private static SpatialPlanAccepted Accepted(GraphSpatialFact fact) => new([fact]);

    private static SpatialPlanRejected Rejected(string reason) => new(reason);
}

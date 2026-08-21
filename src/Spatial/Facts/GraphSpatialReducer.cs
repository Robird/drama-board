using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Folds one committed Graph Spatial fact against immutable state.</summary>
public sealed class GraphSpatialReducer
{
    private readonly GraphDefinition _definition;

    public GraphSpatialReducer(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public GraphSpatialState Apply(
        GraphSpatialState state,
        LogicalInstant instant,
        GraphSpatialFact fact)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);
        GraphSpatialStateValidator.ValidateComplete(_definition, state);

        GraphSpatialState result = fact switch
        {
            EntityPlacedFact placed => ApplyPlaced(state, placed),
            EntityRemovedFact removed => ApplyRemoved(state, removed),
            TraversalStartedFact started => ApplyStarted(state, instant.ModelTime, started),
            TraversalArrivedFact arrived => ApplyArrived(state, instant.ModelTime, arrived),
            PassageEntryAccessChangedFact changed => ApplyAccessChanged(state, changed),
            PassageEntryChangeScheduledFact scheduled => ApplyScheduled(state, instant.ModelTime, scheduled),
            ScheduledPassageEntryChangeAppliedFact applied => ApplyScheduledChange(state, instant.ModelTime, applied),
            _ => throw new InvalidOperationException(
                $"Unsupported Graph Spatial fact '{fact.GetType().Name}'."),
        };

        GraphSpatialStateValidator.ValidateComplete(_definition, result);
        return result;
    }

    private GraphSpatialState ApplyPlaced(GraphSpatialState state, EntityPlacedFact fact)
    {
        SpatialIdentifier.Require(fact.EntityId, nameof(fact));
        SpatialIdentifier.Require(fact.PlaceId, nameof(fact));
        if (state.TryGetEntity(fact.EntityId, out _))
        {
            throw new InvalidOperationException($"Spatial entity '{fact.EntityId}' already exists.");
        }

        if (!_definition.Contains(fact.PlaceId))
        {
            throw new InvalidOperationException($"Spatial place '{fact.PlaceId}' does not exist.");
        }

        var entity = new SpatialEntity(fact.EntityId, 0, new AtPlaceLocation(fact.PlaceId));
        return state.Rebuild(entities: state.Entities.Append(entity));
    }

    private static GraphSpatialState ApplyRemoved(GraphSpatialState state, EntityRemovedFact fact)
    {
        SpatialEntity entity = GraphSpatialStateValidator.RequireEntity(state, fact.EntityId);
        return state.Rebuild(entities: state.Entities.Where(value => value != entity));
    }

    private GraphSpatialState ApplyStarted(
        GraphSpatialState state,
        ModelTime at,
        TraversalStartedFact fact)
    {
        SpatialEntity entity = GraphSpatialStateValidator.RequireEntity(state, fact.EntityId);
        if (entity.Location is not AtPlaceLocation atPlace || atPlace.PlaceId != fact.FromPlaceId)
        {
            throw new InvalidOperationException(
                $"Spatial entity '{fact.EntityId}' is not at traversal origin '{fact.FromPlaceId}'.");
        }

        PassageDefinition passage = GraphSpatialStateValidator.RequirePassage(_definition, fact.PassageId);
        if (!EffectiveGraph.TryResolveDirection(
                _definition,
                state,
                passage,
                fact.FromPlaceId,
                out PlaceId toPlaceId,
                out bool entryAllowed))
        {
            throw new InvalidOperationException(
                $"Place '{fact.FromPlaceId}' is not an endpoint of passage '{fact.PassageId}'.");
        }

        if (!entryAllowed)
        {
            throw new InvalidOperationException(
                $"Passage '{fact.PassageId}' cannot currently be entered from '{fact.FromPlaceId}'.");
        }

        ModelTime arrivalDue;
        try
        {
            arrivalDue = SpatialMath.ArrivalDue(at, passage.Length, fact.SpeedSnapshot);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidOperationException("Traversal timing cannot be represented.", exception);
        }

        var traversal = new TraversingLocation(
            fact.PassageId,
            fact.FromPlaceId,
            toPlaceId,
            at,
            fact.SpeedSnapshot,
            arrivalDue);
        var updated = new SpatialEntity(
            entity.Id,
            checked(entity.MovementGeneration + 1),
            traversal);
        return ReplaceEntity(state, updated);
    }

    private static GraphSpatialState ApplyArrived(
        GraphSpatialState state,
        ModelTime at,
        TraversalArrivedFact fact)
    {
        SpatialEntity entity = GraphSpatialStateValidator.RequireEntity(state, fact.EntityId);
        if (entity.MovementGeneration != fact.ExpectedMovementGeneration ||
            entity.Location is not TraversingLocation traversal)
        {
            throw new InvalidOperationException(
                $"Spatial arrival for entity '{fact.EntityId}' does not match its current movement segment.");
        }

        if (at != traversal.ArrivalDue)
        {
            throw new InvalidOperationException(
                $"Spatial arrival for entity '{fact.EntityId}' is not committed at its due time.");
        }

        var updated = new SpatialEntity(
            entity.Id,
            entity.MovementGeneration,
            new AtPlaceLocation(traversal.ToPlaceId));
        return ReplaceEntity(state, updated);
    }

    private GraphSpatialState ApplyAccessChanged(
        GraphSpatialState state,
        PassageEntryAccessChangedFact fact)
    {
        PassageDefinition passage = GraphSpatialStateValidator.RequirePassage(_definition, fact.PassageId);
        return WithResultAccess(state, passage, fact.ResultAccess);
    }

    private GraphSpatialState ApplyScheduled(
        GraphSpatialState state,
        ModelTime at,
        PassageEntryChangeScheduledFact fact)
    {
        GraphSpatialStateValidator.RequirePassage(_definition, fact.PassageId);
        PassageEntryPatch.Validate(fact.Patch, nameof(fact));
        if (fact.Due <= at)
        {
            throw new InvalidOperationException("A scheduled passage entry change must be due after its creation.");
        }

        if (state.FindSchedule(fact.PassageId, fact.Due) is not null)
        {
            throw new InvalidOperationException(
                $"Passage '{fact.PassageId}' already has an entry change due at '{fact.Due}'.");
        }

        var schedule = new ScheduledPassageEntryChange(fact.PassageId, fact.Due, fact.Patch);
        return state.Rebuild(
            scheduledPassageEntryChanges: state.ScheduledPassageEntryChanges.Append(schedule));
    }

    private GraphSpatialState ApplyScheduledChange(
        GraphSpatialState state,
        ModelTime at,
        ScheduledPassageEntryChangeAppliedFact fact)
    {
        ScheduledPassageEntryChange schedule = state.FindSchedule(fact.PassageId, fact.Due)
            ?? throw new InvalidOperationException("The selected passage entry schedule no longer exists.");
        if (at != schedule.Due)
        {
            throw new InvalidOperationException("A scheduled passage entry change must apply at its exact due time.");
        }

        PassageDefinition passage = GraphSpatialStateValidator.RequirePassage(_definition, schedule.PassageId);
        PassageEntryAccess current = EffectiveGraph.EntryAccess(_definition, state, passage);
        PassageEntryAccess result = schedule.Patch.Apply(current);
        GraphSpatialState withoutSchedule = state.Rebuild(
            scheduledPassageEntryChanges: state.ScheduledPassageEntryChanges.Where(value => value != schedule));
        return WithResultAccess(withoutSchedule, passage, result);
    }

    private static GraphSpatialState ReplaceEntity(GraphSpatialState state, SpatialEntity updated) =>
        state.Rebuild(entities: state.Entities.Select(entity => entity.Id == updated.Id ? updated : entity));

    private static GraphSpatialState WithResultAccess(
        GraphSpatialState state,
        PassageDefinition passage,
        PassageEntryAccess result)
    {
        IEnumerable<PassageEntryAccessOverride> withoutCurrent =
            state.PassageEntryAccessOverrides.Where(value => value.PassageId != passage.Id);
        return result == passage.InitialEntryAccess
            ? state.Rebuild(passageEntryAccessOverrides: withoutCurrent)
            : state.Rebuild(
                passageEntryAccessOverrides: withoutCurrent.Append(
                    new PassageEntryAccessOverride(passage.Id, result)));
    }
}

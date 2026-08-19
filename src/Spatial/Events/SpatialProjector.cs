using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Applies one exact versioned Spatial fact without rerunning domain decisions.</summary>
internal static class SpatialProjector
{
    public static SpatialState Apply(
        SpatialDefinition definition,
        SpatialState state,
        EventKind kind,
        SpatialEvent payload,
        ModelTime modelTime)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(payload);
        SpatialStateValidator.ValidateStamp(definition, state);
        EventKind expectedKind = SpatialEventKinds.For(payload);
        if (!string.Equals(kind.Id, expectedKind.Id, StringComparison.Ordinal) ||
            kind.Version != expectedKind.Version)
        {
            throw new InvalidOperationException(
                $"Spatial payload '{payload.GetType().Name}' requires kind " +
                $"'{expectedKind.Id}' v{expectedKind.Version}, not '{kind.Id}' v{kind.Version}.");
        }

        return payload switch
        {
            EntityPlacedEvent placed => ApplyEntityPlaced(definition, state, placed),
            EntityRemovedEvent removed => ApplyEntityRemoved(state, removed, modelTime),
            ObservationStateChangedEvent changed => ApplyObservationStateChanged(state, changed),
            JourneyStartedEvent started => ApplyJourneyStarted(definition, state, started, modelTime),
            JourneyRetargetedEvent retargeted => ApplyJourneyRetargeted(definition, state, retargeted, modelTime),
            JourneyCancelledEvent cancelled => ApplyJourneyEndedWithGeneration(state, cancelled, modelTime),
            JourneyInterruptedEvent interrupted => ApplyJourneyEndedWithGeneration(state, interrupted, modelTime),
            EntitySteppedEvent stepped => ApplyEntityStepped(definition, state, stepped, modelTime),
            JourneyReroutedEvent rerouted => ApplyJourneyRerouted(definition, state, rerouted, modelTime),
            JourneyContinuedEvent continued => ApplyJourneyContinued(definition, state, continued, modelTime),
            JourneyCompletedEvent completed => ApplyJourneyCompleted(definition, state, completed, modelTime),
            JourneyBlockedEvent blocked => ApplyJourneyBlocked(definition, state, blocked, modelTime),
            PortalStateChangedEvent portal => ApplyPortalStateChanged(definition, state, portal),
            CellStateChangedEvent cell => ApplyCellStateChanged(definition, state, cell),
            MutationScheduledEvent scheduled => ApplyMutationScheduled(definition, state, scheduled, modelTime),
            MutationConsumedEvent consumed => ApplyMutationConsumed(definition, state, consumed, modelTime),
            MomentResolvedEvent resolved => ApplyMomentResolved(definition, state, resolved, modelTime),
            ZoneEnteredEvent entered => ValidateZoneOutcome(definition, state, entered.EntityId, entered.ZoneId),
            ZoneLeftEvent left => ValidateZoneOutcome(definition, state, left.EntityId, left.ZoneId),
            CoPresenceStartedEvent started => ValidateCoPresenceOutcome(
                state,
                started.FirstEntityId,
                started.SecondEntityId),
            CoPresenceEndedEvent ended => ValidateCoPresenceOutcome(
                state,
                ended.FirstEntityId,
                ended.SecondEntityId),
            GeometricVisibilityChangedEvent visibility => ValidateVisibilityOutcome(state, visibility),
            _ => throw new InvalidOperationException($"Unsupported Spatial event '{payload.GetType().Name}'."),
        };
    }

    private static SpatialState ApplyEntityPlaced(
        SpatialDefinition definition,
        SpatialState state,
        EntityPlacedEvent placed)
    {
        ArgumentNullException.ThrowIfNull(placed.Entity);
        SpatialStateValidator.EnsureCellExists(definition, placed.Entity.Cell, "Placed entity");
        if (placed.Entity.MovementGeneration != 0)
        {
            throw new InvalidOperationException("A fresh entity placement must start at movement generation zero.");
        }

        if (state.Entities.Any(entity => entity.Id == placed.Entity.Id))
        {
            throw new InvalidOperationException($"Spatial entity '{placed.Entity.Id}' is already placed.");
        }

        return Changed(state, entities: [.. state.Entities, placed.Entity]);
    }

    private static SpatialState ApplyEntityRemoved(
        SpatialState state,
        EntityRemovedEvent removed,
        ModelTime modelTime)
    {
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, removed.EntityId);
        if (entity.MovementGeneration != removed.ExpectedMovementGeneration)
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' removal generation is stale.");
        }

        state.TryGetJourney(entity.Id, out JourneyState? journey);
        if (journey?.Id != removed.ExpectedActiveJourneyId ||
            (journey is null) != (removed.ExpectedActiveJourneyId is null))
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' active journey does not match removal payload.");
        }

        if (journey is not null && modelTime > journey.CurrentLeg.Due)
        {
            throw new InvalidOperationException("Entity cannot be removed after its current leg is overdue.");
        }
        if (journey is not null && entity.Cell != journey.CurrentLeg.From)
        {
            throw new InvalidOperationException("Entity removal cannot consume an unfinished step prefix.");
        }

        return Changed(
            state,
            entities: state.Entities.Where(value => value.Id != entity.Id),
            journeys: state.Journeys.Where(value => value.EntityId != entity.Id));
    }

    private static SpatialState ApplyObservationStateChanged(
        SpatialState state,
        ObservationStateChangedEvent changed)
    {
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, changed.EntityId);
        if (entity.ObservationEnabled != changed.ExpectedEnabled)
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' observation state is stale.");
        }

        if (changed.ExpectedEnabled == changed.ResultingEnabled)
        {
            throw new InvalidOperationException("Observation state event must change its value.");
        }

        return Changed(
            state,
            entities: ReplaceEntity(state, entity.With(observationEnabled: changed.ResultingEnabled)));
    }

    private static SpatialState ApplyJourneyStarted(
        SpatialDefinition definition,
        SpatialState state,
        JourneyStartedEvent started,
        ModelTime modelTime)
    {
        ArgumentNullException.ThrowIfNull(started.Journey);
        JourneyState journey = started.Journey;
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, journey.EntityId);
        if (state.TryGetJourney(entity.Id, out _))
        {
            throw new InvalidOperationException($"Entity '{entity.Id}' already has an active journey.");
        }

        if (journey.Id.Value != state.NextJourneyOrdinal)
        {
            throw new InvalidOperationException(
                $"Journey '{journey.Id}' does not consume next ordinal {state.NextJourneyOrdinal}.");
        }

        long nextGeneration = checked(entity.MovementGeneration + 1);
        if (journey.Generation != nextGeneration)
        {
            throw new InvalidOperationException("Started journey must advance its entity movement generation once.");
        }

        CurrentLeg leg = RequireCompleteLeg(journey);
        ValidateNewLeg(definition, state, entity, journey.Generation, leg, modelTime);
        SpatialStateValidator.ValidateGoal(definition, journey.Goal);
        if (GoalIsSatisfied(definition, entity.Cell, journey.Goal))
        {
            throw new InvalidOperationException("An already-satisfied assignment must use JourneyCompleted.");
        }

        return Changed(
            state,
            nextJourneyOrdinal: checked(state.NextJourneyOrdinal + 1),
            entities: ReplaceEntity(state, entity.With(movementGeneration: nextGeneration)),
            journeys: [.. state.Journeys, journey]);
    }

    private static SpatialState ApplyJourneyRetargeted(
        SpatialDefinition definition,
        SpatialState state,
        JourneyRetargetedEvent retargeted,
        ModelTime modelTime)
    {
        ArgumentNullException.ThrowIfNull(retargeted.ResultingJourney);
        JourneyState result = retargeted.ResultingJourney;
        JourneyState existing = SpatialStateValidator.RequireJourney(state, result.EntityId, result.Id);
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, result.EntityId);
        if (existing.Generation != retargeted.ExpectedGeneration ||
            entity.MovementGeneration != retargeted.ExpectedGeneration)
        {
            throw new InvalidOperationException("Journey retarget generation is stale.");
        }

        if (modelTime > existing.CurrentLeg.Due)
        {
            throw new InvalidOperationException("Journey cannot be retargeted after its current leg is overdue.");
        }
        if (entity.Cell != existing.CurrentLeg.From)
        {
            throw new InvalidOperationException("Journey cannot be retargeted from an unfinished step prefix.");
        }

        long nextGeneration = checked(retargeted.ExpectedGeneration + 1);
        if (result.Generation != nextGeneration)
        {
            throw new InvalidOperationException("Journey retarget must advance movement generation once.");
        }

        CurrentLeg leg = RequireCompleteLeg(result);
        ValidateNewLeg(definition, state, entity, result.Generation, leg, modelTime);
        SpatialStateValidator.ValidateGoal(definition, result.Goal);
        if (GoalIsSatisfied(definition, entity.Cell, result.Goal))
        {
            throw new InvalidOperationException("An already-satisfied retarget must use JourneyCompleted.");
        }

        return Changed(
            state,
            entities: ReplaceEntity(state, entity.With(movementGeneration: nextGeneration)),
            journeys: ReplaceJourney(state, result));
    }

    private static SpatialState ApplyJourneyEndedWithGeneration(
        SpatialState state,
        JourneyCancelledEvent cancelled,
        ModelTime modelTime) =>
        EndJourneyWithGeneration(
            state,
            cancelled.EntityId,
            cancelled.JourneyId,
            cancelled.ExpectedGeneration,
            cancelled.ResultingGeneration,
            modelTime);

    private static SpatialState ApplyJourneyEndedWithGeneration(
        SpatialState state,
        JourneyInterruptedEvent interrupted,
        ModelTime modelTime) =>
        EndJourneyWithGeneration(
            state,
            interrupted.EntityId,
            interrupted.JourneyId,
            interrupted.ExpectedGeneration,
            interrupted.ResultingGeneration,
            modelTime);

    private static SpatialState EndJourneyWithGeneration(
        SpatialState state,
        EntityId entityId,
        JourneyId journeyId,
        long expectedGeneration,
        long resultingGeneration,
        ModelTime modelTime)
    {
        JourneyState journey = SpatialStateValidator.RequireJourney(state, entityId, journeyId);
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, entityId);
        if (journey.Generation != expectedGeneration || entity.MovementGeneration != expectedGeneration)
        {
            throw new InvalidOperationException("Journey termination generation is stale.");
        }

        if (modelTime > journey.CurrentLeg.Due)
        {
            throw new InvalidOperationException("Journey cannot be terminated after its current leg is overdue.");
        }
        if (entity.Cell != journey.CurrentLeg.From)
        {
            throw new InvalidOperationException("Journey termination must leave the entity at CurrentLeg.From.");
        }

        if (resultingGeneration != checked(expectedGeneration + 1))
        {
            throw new InvalidOperationException("Journey termination must advance movement generation once.");
        }

        return Changed(
            state,
            entities: ReplaceEntity(state, entity.With(movementGeneration: resultingGeneration)),
            journeys: state.Journeys.Where(value => value.Id != journeyId));
    }

    private static SpatialState ApplyEntityStepped(
        SpatialDefinition definition,
        SpatialState state,
        EntitySteppedEvent stepped,
        ModelTime modelTime)
    {
        JourneyState journey = SpatialStateValidator.RequireJourney(state, stepped.EntityId, stepped.JourneyId);
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, stepped.EntityId);
        CurrentLeg leg = RequireCompleteLeg(journey);
        if (entity.Cell != stepped.From ||
            journey.Generation != stepped.JourneyGeneration ||
            entity.MovementGeneration != stepped.JourneyGeneration ||
            leg.From != stepped.From ||
            leg.To != stepped.To ||
            leg.JourneyGeneration != stepped.JourneyGeneration ||
            leg.Due != modelTime)
        {
            throw new InvalidOperationException("Entity step does not exactly match its due current leg.");
        }

        SpatialStateValidator.ValidateLeg(definition, leg);
        if (!IsLegPassable(definition, state, leg))
        {
            throw new InvalidOperationException("Entity cannot step through an impassable current leg.");
        }

        return Changed(state, entities: ReplaceEntity(state, entity.With(cell: stepped.To)));
    }

    private static SpatialState ApplyJourneyRerouted(
        SpatialDefinition definition,
        SpatialState state,
        JourneyReroutedEvent rerouted,
        ModelTime modelTime)
    {
        JourneyState journey = SpatialStateValidator.RequireJourney(state, rerouted.EntityId, rerouted.JourneyId);
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, rerouted.EntityId);
        if (journey.CurrentLeg != rerouted.FailedLeg ||
            rerouted.FailedLeg.Due != modelTime ||
            entity.Cell != rerouted.FailedLeg.From ||
            rerouted.FailedLeg.JourneyGeneration != journey.Generation)
        {
            throw new InvalidOperationException("Journey reroute does not match its failed due leg.");
        }

        if (IsLegPassable(definition, state, rerouted.FailedLeg))
        {
            throw new InvalidOperationException("Journey reroute requires an invalid current leg.");
        }

        if (rerouted.FailedLeg == rerouted.ResultingLeg)
        {
            throw new InvalidOperationException("Journey reroute must replace the failed leg.");
        }

        ValidateNewLeg(definition, state, entity, journey.Generation, rerouted.ResultingLeg, modelTime);
        return Changed(
            state,
            journeys: ReplaceJourney(state, journey.WithCurrentLeg(rerouted.ResultingLeg)));
    }

    private static SpatialState ApplyJourneyContinued(
        SpatialDefinition definition,
        SpatialState state,
        JourneyContinuedEvent continued,
        ModelTime modelTime)
    {
        JourneyState journey = SpatialStateValidator.RequireJourney(state, continued.EntityId, continued.JourneyId);
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, continued.EntityId);
        if (journey.CurrentLeg != continued.CompletedLeg ||
            continued.CompletedLeg.Due != modelTime ||
            entity.Cell != continued.CompletedLeg.To ||
            continued.CompletedLeg.JourneyGeneration != journey.Generation)
        {
            throw new InvalidOperationException("Journey continuation does not match its preceding completed leg.");
        }

        ValidateNewLeg(definition, state, entity, journey.Generation, continued.ResultingLeg, modelTime);
        if (GoalIsSatisfied(definition, entity.Cell, journey.Goal))
        {
            throw new InvalidOperationException("A journey at its goal must complete rather than continue.");
        }

        return Changed(
            state,
            journeys: ReplaceJourney(state, journey.WithCurrentLeg(continued.ResultingLeg)));
    }

    private static SpatialState ApplyJourneyCompleted(
        SpatialDefinition definition,
        SpatialState state,
        JourneyCompletedEvent completed,
        ModelTime modelTime)
    {
        SpatialStateValidator.ValidateGoal(definition, completed.Goal);
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, completed.EntityId);
        if (entity.MovementGeneration != completed.ExpectedGeneration)
        {
            throw new InvalidOperationException("Journey completion generation is stale.");
        }

        if (!GoalIsSatisfied(definition, entity.Cell, completed.Goal))
        {
            throw new InvalidOperationException("Journey completion goal is not satisfied by the entity cell.");
        }

        return completed.Reason switch
        {
            JourneyCompletionReason.ReachedGoal => ApplyReachedGoal(state, entity, completed, modelTime),
            JourneyCompletionReason.AssignedAlreadySatisfied =>
                ApplyAssignedAlreadySatisfied(state, entity, completed),
            JourneyCompletionReason.RetargetedAlreadySatisfied =>
                ApplyRetargetedAlreadySatisfied(state, entity, completed, modelTime),
            _ => throw new InvalidOperationException($"Unknown journey completion reason '{completed.Reason}'."),
        };
    }

    private static SpatialState ApplyReachedGoal(
        SpatialState state,
        SpatialEntityState entity,
        JourneyCompletedEvent completed,
        ModelTime modelTime)
    {
        JourneyState journey = SpatialStateValidator.RequireJourney(state, entity.Id, completed.JourneyId);
        CurrentLeg leg = completed.CompletedLeg ?? throw new InvalidOperationException(
            "ReachedGoal completion requires the exact completed leg.");
        if (journey.CurrentLeg != leg ||
            journey.Goal != completed.Goal ||
            journey.Generation != completed.ExpectedGeneration ||
            completed.ResultingGeneration != completed.ExpectedGeneration ||
            leg.Due != modelTime ||
            entity.Cell != leg.To)
        {
            throw new InvalidOperationException("ReachedGoal completion does not match its preceding step.");
        }

        return Changed(state, journeys: state.Journeys.Where(value => value.Id != journey.Id));
    }

    private static SpatialState ApplyAssignedAlreadySatisfied(
        SpatialState state,
        SpatialEntityState entity,
        JourneyCompletedEvent completed)
    {
        if (state.TryGetJourney(entity.Id, out _) || completed.CompletedLeg is not null)
        {
            throw new InvalidOperationException("AssignedAlreadySatisfied requires no active journey or completed leg.");
        }

        if (completed.JourneyId.Value != state.NextJourneyOrdinal ||
            completed.ResultingGeneration != checked(completed.ExpectedGeneration + 1))
        {
            throw new InvalidOperationException(
                "AssignedAlreadySatisfied must consume the next JourneyId and advance generation once.");
        }

        return Changed(
            state,
            nextJourneyOrdinal: checked(state.NextJourneyOrdinal + 1),
            entities: ReplaceEntity(
                state,
                entity.With(movementGeneration: completed.ResultingGeneration)));
    }

    private static SpatialState ApplyRetargetedAlreadySatisfied(
        SpatialState state,
        SpatialEntityState entity,
        JourneyCompletedEvent completed,
        ModelTime modelTime)
    {
        JourneyState journey = SpatialStateValidator.RequireJourney(state, entity.Id, completed.JourneyId);
        if (completed.CompletedLeg is not null ||
            journey.Generation != completed.ExpectedGeneration ||
            completed.ResultingGeneration != checked(completed.ExpectedGeneration + 1))
        {
            throw new InvalidOperationException(
                "RetargetedAlreadySatisfied must retain JourneyId and advance generation once.");
        }

        if (modelTime > journey.CurrentLeg.Due)
        {
            throw new InvalidOperationException("Journey cannot be retarget-completed after its leg is overdue.");
        }
        if (entity.Cell != journey.CurrentLeg.From)
        {
            throw new InvalidOperationException("Retarget completion cannot consume an unfinished step prefix.");
        }

        return Changed(
            state,
            entities: ReplaceEntity(
                state,
                entity.With(movementGeneration: completed.ResultingGeneration)),
            journeys: state.Journeys.Where(value => value.Id != journey.Id));
    }

    private static SpatialState ApplyJourneyBlocked(
        SpatialDefinition definition,
        SpatialState state,
        JourneyBlockedEvent blocked,
        ModelTime modelTime)
    {
        JourneyState journey = SpatialStateValidator.RequireJourney(state, blocked.EntityId, blocked.JourneyId);
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, blocked.EntityId);
        if (journey.CurrentLeg != blocked.Leg ||
            blocked.Leg.Due != modelTime ||
            blocked.Leg.JourneyGeneration != journey.Generation ||
            entity.MovementGeneration != journey.Generation)
        {
            throw new InvalidOperationException("Blocked journey does not match its exact due leg.");
        }

        bool positionMatchesReason = blocked.Reason switch
        {
            JourneyBlockedReason.LegInvalidNoRoute => entity.Cell == blocked.Leg.From,
            JourneyBlockedReason.NoContinuationAfterStep => entity.Cell == blocked.Leg.To,
            _ => throw new InvalidOperationException($"Unknown journey blocked reason '{blocked.Reason}'."),
        };
        if (!positionMatchesReason)
        {
            throw new InvalidOperationException("Blocked journey entity position does not match its blocking phase.");
        }
        if (GoalIsSatisfied(definition, entity.Cell, journey.Goal))
        {
            throw new InvalidOperationException("A journey at its goal must complete rather than block.");
        }

        if (blocked.Reason == JourneyBlockedReason.LegInvalidNoRoute &&
            IsLegPassable(definition, state, blocked.Leg))
        {
            throw new InvalidOperationException("LegInvalidNoRoute requires an impassable current leg.");
        }

        return Changed(state, journeys: state.Journeys.Where(value => value.Id != journey.Id));
    }

    private static SpatialState ApplyPortalStateChanged(
        SpatialDefinition definition,
        SpatialState state,
        PortalStateChangedEvent changed)
    {
        PortalDefinition portal = SpatialStateValidator.RequirePortal(definition, changed.PortalId);
        PortalOverrideState? current = state.PortalOverrides.SingleOrDefault(
            value => value.PortalId == changed.PortalId);
        bool? currentValue = current?.IsEnabled;
        if (currentValue != changed.ExpectedOverride ||
            changed.ExpectedOverride == changed.ResultingOverride)
        {
            throw new InvalidOperationException("Portal state event does not match and change its sparse override.");
        }

        if (changed.ResultingOverride == portal.InitiallyEnabled)
        {
            throw new InvalidOperationException("Portal override equal to Definition must be represented by null.");
        }

        IEnumerable<PortalOverrideState> remaining = state.PortalOverrides.Where(
            value => value.PortalId != changed.PortalId);
        return Changed(
            state,
            portalOverrides: changed.ResultingOverride is bool result
                ? [.. remaining, new PortalOverrideState(changed.PortalId, result)]
                : remaining);
    }

    private static SpatialState ApplyCellStateChanged(
        SpatialDefinition definition,
        SpatialState state,
        CellStateChangedEvent changed)
    {
        SpatialStateValidator.EnsureCellExists(definition, changed.Cell, "Cell state event");
        if (changed.ExpectedOverride is not null)
        {
            SpatialStateValidator.ValidateCellOverride(definition, changed.Cell, changed.ExpectedOverride);
        }

        if (changed.ResultingOverride is not null)
        {
            SpatialStateValidator.ValidateCellOverride(definition, changed.Cell, changed.ResultingOverride);
        }

        CellOverrideState? current = state.CellOverrides.SingleOrDefault(value => value.Cell == changed.Cell);
        if (current?.Value != changed.ExpectedOverride || changed.ExpectedOverride == changed.ResultingOverride)
        {
            throw new InvalidOperationException("Cell state event does not match and change its complete sparse override.");
        }

        IEnumerable<CellOverrideState> remaining = state.CellOverrides.Where(value => value.Cell != changed.Cell);
        return Changed(
            state,
            cellOverrides: changed.ResultingOverride is not null
                ? [.. remaining, new CellOverrideState(changed.Cell, changed.ResultingOverride)]
                : remaining);
    }

    private static SpatialState ApplyMutationScheduled(
        SpatialDefinition definition,
        SpatialState state,
        MutationScheduledEvent scheduled,
        ModelTime modelTime)
    {
        ArgumentNullException.ThrowIfNull(scheduled.Mutation);
        ScheduledSpatialMutationState mutation = scheduled.Mutation;
        if (mutation.Id.Value != state.NextMutationOrdinal)
        {
            throw new InvalidOperationException(
                $"Mutation '{mutation.Id}' does not consume next ordinal {state.NextMutationOrdinal}.");
        }

        if (mutation.Due <= modelTime)
        {
            throw new InvalidOperationException("Scheduled spatial mutation must be strictly in the future.");
        }

        SpatialStateValidator.ValidateMutation(definition, mutation.Mutation);
        if (state.ScheduledMutations.Any(value =>
                value.Due == mutation.Due && SameMutationTarget(value.Mutation, mutation.Mutation)))
        {
            throw new InvalidOperationException("A mutation already exists for the same target and due time.");
        }

        return Changed(
            state,
            nextMutationOrdinal: checked(state.NextMutationOrdinal + 1),
            scheduledMutations: [.. state.ScheduledMutations, mutation]);
    }

    private static SpatialState ApplyMutationConsumed(
        SpatialDefinition definition,
        SpatialState state,
        MutationConsumedEvent consumed,
        ModelTime modelTime)
    {
        ArgumentNullException.ThrowIfNull(consumed.Mutation);
        ScheduledSpatialMutationState? existing = state.ScheduledMutations.SingleOrDefault(
            value => value.Id == consumed.Mutation.Id);
        if (existing != consumed.Mutation || consumed.Mutation.Due != modelTime)
        {
            throw new InvalidOperationException("Consumed mutation does not exactly match due state.");
        }

        if (!MutationResultIsApplied(definition, state, consumed.Mutation.Mutation))
        {
            throw new InvalidOperationException("Consumed mutation target has not reached its resulting value.");
        }

        return Changed(
            state,
            scheduledMutations: state.ScheduledMutations.Where(value => value.Id != consumed.Mutation.Id));
    }

    private static SpatialState ApplyMomentResolved(
        SpatialDefinition definition,
        SpatialState state,
        MomentResolvedEvent resolved,
        ModelTime modelTime)
    {
        if (resolved.MomentOrdinal != state.NextMomentOrdinal)
        {
            throw new InvalidOperationException(
                $"Moment {resolved.MomentOrdinal} does not consume next ordinal {state.NextMomentOrdinal}.");
        }

        if (state.ScheduledMutations.Any(mutation => mutation.Due <= modelTime) ||
            state.Journeys.Any(journey => journey.CurrentLeg.Due <= modelTime))
        {
            throw new InvalidOperationException("SpatialMoment cannot resolve while due work remains.");
        }

        SpatialStateValidator.ValidateComplete(definition, state);

        return Changed(state, nextMomentOrdinal: checked(state.NextMomentOrdinal + 1));
    }

    private static CurrentLeg RequireCompleteLeg(JourneyState journey) => journey.CurrentLeg;

    private static void ValidateNewLeg(
        SpatialDefinition definition,
        SpatialState state,
        SpatialEntityState entity,
        long generation,
        CurrentLeg leg,
        ModelTime modelTime)
    {
        if (leg.From != entity.Cell ||
            leg.JourneyGeneration != generation ||
            leg.StartedAt != modelTime)
        {
            throw new InvalidOperationException("New current leg does not match entity, generation, and event time.");
        }

        SpatialStateValidator.ValidateLeg(definition, leg);
        if (!IsLegPassable(definition, state, leg))
        {
            throw new InvalidOperationException("New current leg must be passable.");
        }


        ModelTime expectedDue;
        if (leg.EdgeKind == SpatialEdgeKind.Portal)
        {
            PortalDefinition portal = SpatialStateValidator.RequirePortal(definition, leg.PortalId!.Value);
            expectedDue = modelTime + portal.TraversalDuration;
        }
        else
        {
            GridMapDefinition map = definition.GetMap(leg.To.MapId);
            CellDefinition target = definition.GetCell(leg.To);
            int effectiveMoveCost = state.CellOverrides.SingleOrDefault(
                value => value.Cell == leg.To)?.Value.MoveCost ?? target.MoveCost;
            long durationTicks = checked(map.OrthogonalStepDuration.Ticks * effectiveMoveCost);
            expectedDue = modelTime + new ModelDuration(durationTicks);
        }

        if (leg.Due != expectedDue)
        {
            throw new InvalidOperationException(
                $"New current leg due '{leg.Due}' does not match exact expected due '{expectedDue}'.");
        }
    }

    private static SpatialState ValidateZoneOutcome(
        SpatialDefinition definition,
        SpatialState state,
        EntityId entityId,
        ZoneId zoneId)
    {
        SpatialStateValidator.EnsureEntityId(entityId, "Zone outcome");
        if (string.IsNullOrWhiteSpace(zoneId.Value) || !definition.Zones.Any(zone => zone.Id == zoneId))
        {
            throw new InvalidOperationException($"Zone outcome references undefined zone '{zoneId}'.");
        }

        return state;
    }

    private static SpatialState ValidateCoPresenceOutcome(
        SpatialState state,
        EntityId firstEntityId,
        EntityId secondEntityId)
    {
        SpatialStateValidator.EnsureEntityId(firstEntityId, "Co-presence outcome");
        SpatialStateValidator.EnsureEntityId(secondEntityId, "Co-presence outcome");
        if (firstEntityId.CompareTo(secondEntityId) >= 0)
        {
            throw new InvalidOperationException("Co-presence outcome pair must be distinct and canonical.");
        }

        return state;
    }

    private static SpatialState ValidateVisibilityOutcome(
        SpatialState state,
        GeometricVisibilityChangedEvent visibility)
    {
        SpatialStateValidator.EnsureEntityId(visibility.ObserverId, "Visibility outcome observer");
        foreach (EntityId entityId in visibility.AddedEntityIds.Concat(visibility.RemovedEntityIds))
        {
            SpatialStateValidator.EnsureEntityId(entityId, "Visibility outcome target");
            if (entityId == visibility.ObserverId)
            {
                throw new InvalidOperationException("Visibility outcome cannot contain its observer as a target.");
            }
        }

        if (!IsStrictlySortedAndUnique(visibility.AddedEntityIds) ||
            !IsStrictlySortedAndUnique(visibility.RemovedEntityIds) ||
            visibility.AddedEntityIds.Intersect(visibility.RemovedEntityIds).Any())
        {
            throw new InvalidOperationException("Visibility outcome sets must be canonical, disjoint, and unique.");
        }

        return state;
    }

    private static bool IsStrictlySortedAndUnique(IReadOnlyList<EntityId> values)
    {
        for (int index = 1; index < values.Count; index++)
        {
            if (values[index - 1].CompareTo(values[index]) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLegPassable(
        SpatialDefinition definition,
        SpatialState state,
        CurrentLeg leg)
    {
        CellDefinition targetDefinition = definition.GetCell(leg.To);
        CellOverride? targetOverride = state.CellOverrides.SingleOrDefault(
            value => value.Cell == leg.To)?.Value;
        bool blocksMovement = targetOverride?.BlocksMovement ?? targetDefinition.BlocksMovement;
        if (blocksMovement)
        {
            return false;
        }

        if (leg.EdgeKind != SpatialEdgeKind.Portal)
        {
            return true;
        }

        PortalId portalId = leg.PortalId!.Value;
        PortalDefinition portal = SpatialStateValidator.RequirePortal(definition, portalId);
        return state.PortalOverrides.SingleOrDefault(value => value.PortalId == portalId)?.IsEnabled
            ?? portal.InitiallyEnabled;
    }

    private static bool MutationResultIsApplied(
        SpatialDefinition definition,
        SpatialState state,
        ScheduledSpatialMutation mutation) =>
        mutation switch
        {
            SetPortalStateMutation portal =>
                (state.PortalOverrides.SingleOrDefault(
                    value => value.PortalId == portal.PortalId)?.IsEnabled
                    ?? SpatialStateValidator.RequirePortal(definition, portal.PortalId).InitiallyEnabled) ==
                portal.IsEnabled,
            SetCellOverrideMutation cell =>
                state.CellOverrides.SingleOrDefault(value => value.Cell == cell.Cell)?.Value == cell.Value,
            _ => false,
        };

    private static bool GoalIsSatisfied(
        SpatialDefinition definition,
        CellRef cell,
        MoveGoal goal) => SpatialStateValidator.IsGoalSatisfied(definition, cell, goal);

    private static bool SameMutationTarget(
        ScheduledSpatialMutation first,
        ScheduledSpatialMutation second) =>
        (first, second) switch
        {
            (SetPortalStateMutation left, SetPortalStateMutation right) => left.PortalId == right.PortalId,
            (SetCellOverrideMutation left, SetCellOverrideMutation right) => left.Cell == right.Cell,
            _ => false,
        };

    private static IEnumerable<SpatialEntityState> ReplaceEntity(
        SpatialState state,
        SpatialEntityState replacement) =>
        state.Entities.Select(entity => entity.Id == replacement.Id ? replacement : entity);

    private static IEnumerable<JourneyState> ReplaceJourney(
        SpatialState state,
        JourneyState replacement) =>
        state.Journeys.Select(journey => journey.Id == replacement.Id ? replacement : journey);

    private static SpatialState Changed(
        SpatialState state,
        long? nextMomentOrdinal = null,
        long? nextJourneyOrdinal = null,
        long? nextMutationOrdinal = null,
        IEnumerable<SpatialEntityState>? entities = null,
        IEnumerable<JourneyState>? journeys = null,
        IEnumerable<PortalOverrideState>? portalOverrides = null,
        IEnumerable<CellOverrideState>? cellOverrides = null,
        IEnumerable<ScheduledSpatialMutationState>? scheduledMutations = null) =>
        state.Rebuild(
            revision: checked(state.Revision + 1),
            nextMomentOrdinal,
            nextJourneyOrdinal,
            nextMutationOrdinal,
            entities,
            journeys,
            portalOverrides,
            cellOverrides,
            scheduledMutations);
}

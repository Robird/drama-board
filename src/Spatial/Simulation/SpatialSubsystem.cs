using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Owns the single forecast and simultaneous resolution point for all future Spatial work.</summary>
public sealed class SpatialSubsystem :
    ISimSystem<SpatialState, SpatialMomentCandidate, SpatialEvent>
{
    private readonly SpatialDefinition _definition;
    private readonly long _sourceId;

    /// <summary>Initializes a Spatial system with one composition-root-reserved source identity.</summary>
    public SpatialSubsystem(SpatialDefinition definition, long sourceId)
    {
        SpatialRules.EnsureSupported(definition);
        if (sourceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceId), "Spatial source identifier must be positive.");
        }

        _definition = definition;
        _sourceId = sourceId;
    }

    /// <inheritdoc />
    public IReadOnlyList<EventCandidate<SpatialMomentCandidate>> ForecastNext(
        SpatialState world,
        ModelTime now)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpatialStateValidator.ValidateComplete(_definition, world);
        if (!TryGetEarliestDue(world, out ModelTime due))
        {
            return [];
        }

        if (due < now)
        {
            throw new InvalidOperationException(
                $"Spatial work due at '{due}' is overdue at forecast time '{now}'.");
        }

        return
        [
            new EventCandidate<SpatialMomentCandidate>(
                new EventCandidateId(world.NextMomentOrdinal),
                due,
                _sourceId,
                new SpatialMomentCandidate(world.Revision, world.NextMomentOrdinal)),
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> Resolve(
        SpatialState world,
        EventCandidate<SpatialMomentCandidate> candidate)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpatialStateValidator.ValidateComplete(_definition, world);
        ValidateCandidate(world, candidate);

        ModelTime modelTime = candidate.Due;
        ScheduledSpatialMutationState[] dueMutations =
        [
            .. world.ScheduledMutations
                .Where(mutation => mutation.Due == modelTime)
                .OrderBy(mutation => mutation.Id),
        ];
        JourneyState[] dueJourneys =
        [
            .. world.Journeys
                .Where(journey => journey.CurrentLeg.Due == modelTime)
                .OrderBy(journey => journey.EntityId),
        ];
        int resolvedWorkCount = checked(dueMutations.Length + dueJourneys.Length);
        if (resolvedWorkCount == 0)
        {
            throw new InvalidOperationException("A SpatialMoment candidate must resolve positive due work.");
        }

        SpatialState working = world;
        var body = new List<SpatialEvent>();

        // Phase 1: all due topology values become authoritative before any due leg is inspected.
        foreach (ScheduledSpatialMutationState mutation in dueMutations)
        {
            SpatialEvent? valueEvent = CreateMutationValueEvent(working, mutation.Mutation);
            if (valueEvent is not null)
            {
                AppendAndProject(ref working, body, valueEvent, modelTime);
            }

            AppendAndProject(ref working, body, new MutationConsumedEvent(mutation), modelTime);
        }

        // Phase 2 takes one topology snapshot after every due mutation has been applied.
        var topology = new EffectiveSpatialTopology(_definition, working);
        var canStepByEntity = dueJourneys.ToDictionary(
            journey => journey.EntityId,
            journey => topology.IsLegPassable(journey.CurrentLeg));

        // Phase 3: successful steps are decided without Actor occupancy and projected by EntityId.
        foreach (JourneyState journey in dueJourneys.Where(
                     journey => canStepByEntity[journey.EntityId]))
        {
            CurrentLeg leg = journey.CurrentLeg;
            AppendAndProject(
                ref working,
                body,
                new EntitySteppedEvent(
                    journey.EntityId,
                    journey.Id,
                    leg.From,
                    leg.To,
                    journey.Generation),
                modelTime);
        }

        // Phase 4: every due journey is restored to a complete outcome in EntityId order.
        foreach (JourneyState originalJourney in dueJourneys)
        {
            SpatialEvent outcome = CreateJourneyOutcome(
                working,
                originalJourney,
                canStepByEntity[originalJourney.EntityId],
                modelTime);
            AppendAndProject(ref working, body, outcome, modelTime);
        }

        SpatialTransitionResult result = SpatialTransition.CompleteMoment(
            _definition,
            world,
            modelTime,
            body,
            resolvedWorkCount);
        var resolved = new MomentResolvedEvent(working.NextMomentOrdinal, resolvedWorkCount);
        working = SpatialProjector.Apply(
            _definition,
            working,
            SpatialEventKinds.MomentResolved,
            resolved,
            modelTime);
        if (!result.ResultingState.Equals(working))
        {
            throw new InvalidOperationException(
                "SpatialMoment scratch projection disagrees with the completed transition projection.");
        }

        return result.Events;
    }

    private void ValidateCandidate(
        SpatialState state,
        EventCandidate<SpatialMomentCandidate> candidate)
    {
        SpatialMomentCandidate payload = candidate.Payload ?? throw new InvalidOperationException(
            "Spatial candidate payload is required.");
        if (candidate.SourceId != _sourceId)
        {
            throw new InvalidOperationException(
                $"Spatial candidate source {candidate.SourceId} does not match {_sourceId}.");
        }

        if (candidate.Id.Value != state.NextMomentOrdinal ||
            payload.MomentOrdinal != state.NextMomentOrdinal)
        {
            throw new InvalidOperationException(
                "Spatial candidate identity does not match the next persistent moment ordinal.");
        }

        if (payload.ExpectedSpatialRevision != state.Revision)
        {
            throw new InvalidOperationException(
                $"Spatial candidate revision {payload.ExpectedSpatialRevision} is stale for {state.Revision}.");
        }

        if (!TryGetEarliestDue(state, out ModelTime earliestDue))
        {
            throw new InvalidOperationException("Spatial candidate cannot resolve without future work.");
        }

        if (candidate.Due != earliestDue)
        {
            throw new InvalidOperationException(
                $"Spatial candidate due '{candidate.Due}' does not match earliest due '{earliestDue}'.");
        }
    }

    private SpatialEvent? CreateMutationValueEvent(
        SpatialState state,
        ScheduledSpatialMutation mutation) =>
        mutation switch
        {
            SetPortalStateMutation portal => CreatePortalValueEvent(state, portal),
            SetCellOverrideMutation cell => CreateCellValueEvent(state, cell),
            _ => throw new InvalidOperationException(
                $"Unsupported scheduled spatial mutation '{mutation.GetType().Name}'."),
        };

    private PortalStateChangedEvent? CreatePortalValueEvent(
        SpatialState state,
        SetPortalStateMutation mutation)
    {
        PortalDefinition definition = SpatialStateValidator.RequirePortal(_definition, mutation.PortalId);
        PortalOverrideState? current = state.PortalOverrides.SingleOrDefault(
            value => value.PortalId == mutation.PortalId);
        bool effective = current?.IsEnabled ?? definition.InitiallyEnabled;
        if (effective == mutation.IsEnabled)
        {
            return null;
        }

        bool? resultingOverride = mutation.IsEnabled == definition.InitiallyEnabled
            ? null
            : mutation.IsEnabled;
        return new PortalStateChangedEvent(
            mutation.PortalId,
            current?.IsEnabled,
            resultingOverride);
    }

    private static CellStateChangedEvent? CreateCellValueEvent(
        SpatialState state,
        SetCellOverrideMutation mutation)
    {
        CellOverride? current = state.CellOverrides.SingleOrDefault(
            value => value.Cell == mutation.Cell)?.Value;
        return current == mutation.Value
            ? null
            : new CellStateChangedEvent(mutation.Cell, current, mutation.Value);
    }

    private SpatialEvent CreateJourneyOutcome(
        SpatialState state,
        JourneyState originalJourney,
        bool stepped,
        ModelTime modelTime)
    {
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, originalJourney.EntityId);
        if (stepped && SpatialStateValidator.IsGoalSatisfied(
                _definition,
                entity.Cell,
                originalJourney.Goal))
        {
            return new JourneyCompletedEvent(
                originalJourney.EntityId,
                originalJourney.Id,
                originalJourney.Goal,
                originalJourney.Generation,
                originalJourney.Generation,
                JourneyCompletionReason.ReachedGoal,
                originalJourney.CurrentLeg);
        }

        PathSearchResult search = SpatialNavigator.FindNextStep(
            _definition,
            state,
            entity.Cell,
            originalJourney.Goal);
        if (search is PathSearchResult.NextStep next &&
            TryCreateLeg(next.Edge, modelTime, originalJourney.Generation, out CurrentLeg resultingLeg))
        {
            return stepped
                ? new JourneyContinuedEvent(
                    originalJourney.EntityId,
                    originalJourney.Id,
                    originalJourney.CurrentLeg,
                    resultingLeg)
                : new JourneyReroutedEvent(
                    originalJourney.EntityId,
                    originalJourney.Id,
                    originalJourney.CurrentLeg,
                    resultingLeg);
        }

        if (search is PathSearchResult.AlreadySatisfied)
        {
            throw new InvalidOperationException(
                $"Journey '{originalJourney.Id}' navigation satisfaction disagrees with its authoritative goal.");
        }

        return new JourneyBlockedEvent(
            originalJourney.EntityId,
            originalJourney.Id,
            originalJourney.CurrentLeg,
            stepped
                ? JourneyBlockedReason.NoContinuationAfterStep
                : JourneyBlockedReason.LegInvalidNoRoute);
    }

    private static bool TryCreateLeg(
        NavigationEdge edge,
        ModelTime modelTime,
        long generation,
        out CurrentLeg leg)
    {
        try
        {
            leg = new CurrentLeg(
                edge.From,
                edge.To,
                edge.EdgeKind,
                edge.PortalId,
                modelTime,
                modelTime + edge.Duration,
                generation);
            return true;
        }
        catch (OverflowException)
        {
            leg = null!;
            return false;
        }
    }

    private void AppendAndProject(
        ref SpatialState state,
        ICollection<SpatialEvent> body,
        SpatialEvent payload,
        ModelTime modelTime)
    {
        state = SpatialProjector.Apply(
            _definition,
            state,
            SpatialEventKinds.For(payload),
            payload,
            modelTime);
        body.Add(payload);
    }

    private static bool TryGetEarliestDue(SpatialState state, out ModelTime due)
    {
        ModelTime? earliestJourney = state.Journeys.Count == 0
            ? null
            : state.Journeys.Min(journey => journey.CurrentLeg.Due);
        ModelTime? earliestMutation = state.ScheduledMutations.Count == 0
            ? null
            : state.ScheduledMutations.Min(mutation => mutation.Due);
        if (earliestJourney is null && earliestMutation is null)
        {
            due = default;
            return false;
        }

        due = earliestJourney switch
        {
            null => earliestMutation!.Value,
            { } journeyDue when earliestMutation is null => journeyDue,
            { } journeyDue when journeyDue <= earliestMutation!.Value => journeyDue,
            _ => earliestMutation!.Value,
        };
        return true;
    }
}

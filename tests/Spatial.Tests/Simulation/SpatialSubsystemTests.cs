using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialSubsystemTests
{
    [Fact]
    public void ConstructorAndForecast_RequirePositiveSourceCompleteStateAndNonOverdueEarliestWork()
    {
        SpatialDefinition definition = Definition([Map("map", 2, 1)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialSubsystem(definition, sourceId: 0));
        var system = new SpatialSubsystem(definition, sourceId: 41);
        Assert.Empty(system.ForecastNext(SpatialState.Create(definition), ModelTime.Zero));

        SpatialState state = Place(definition, SpatialState.Create(definition), 1, Cell("map", 0, 0));
        CurrentLeg leg = OrthogonalLeg(Cell("map", 0, 0), Cell("map", 1, 0));
        state = StartJourney(definition, state, 1, 1, leg, new CellGoal(leg.To));
        ModelTime mutationDue = new(500);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            mutationDue,
            new SetCellOverrideMutation(Cell("map", 0, 0), new CellOverride(blocksSight: true)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(mutation));

        EventCandidate<SpatialMomentCandidate> candidate = Assert.Single(
            system.ForecastNext(state, ModelTime.Zero));
        Assert.Equal(mutationDue, candidate.Due);
        Assert.Equal(41, candidate.SourceId);
        Assert.Equal(new EventCandidateId(state.NextMomentOrdinal), candidate.Id);
        Assert.Equal(state.Revision, candidate.Payload.ExpectedSpatialRevision);
        Assert.Equal(state.NextMomentOrdinal, candidate.Payload.MomentOrdinal);
        Assert.Throws<InvalidOperationException>(() => system.ForecastNext(state, new ModelTime(501)));

        SpatialState prefix = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntitySteppedEvent(new EntityId(1), new JourneyId(1), leg.From, leg.To, 1),
            leg.Due);
        Assert.Throws<InvalidOperationException>(() => system.ForecastNext(prefix, leg.Due));

        SpatialDefinition otherDefinition = Definition([Map("other", 1, 1)]);
        Assert.Throws<InvalidOperationException>(() => system.ForecastNext(
            SpatialState.Create(otherDefinition),
            ModelTime.Zero));
    }

    [Fact]
    public void Resolve_RejectsStaleAndIncorrectCandidateWrappers()
    {
        SpatialDefinition definition = Definition([Map("map", 1, 1)]);
        var system = new SpatialSubsystem(definition, sourceId: 41);
        ModelTime due = new(500);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(Cell("map", 0, 0), new CellOverride(blocksSight: true)));
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(mutation));
        EventCandidate<SpatialMomentCandidate> valid = Assert.Single(
            system.ForecastNext(state, ModelTime.Zero));

        Assert.Throws<InvalidOperationException>(() => system.Resolve(
            state,
            Candidate(valid, sourceId: 42)));
        Assert.Throws<InvalidOperationException>(() => system.Resolve(
            state,
            Candidate(valid, candidateId: 2)));
        Assert.Throws<InvalidOperationException>(() => system.Resolve(
            state,
            Candidate(valid, due: new ModelTime(501))));
        Assert.Throws<InvalidOperationException>(() => system.Resolve(
            state,
            Candidate(valid, payload: new SpatialMomentCandidate(state.Revision, momentOrdinal: 2))));
        Assert.Throws<InvalidOperationException>(() => system.Resolve(
            state,
            Candidate(valid, payload: new SpatialMomentCandidate(state.Revision + 1, state.NextMomentOrdinal))));
        Assert.Throws<InvalidOperationException>(() => system.Resolve(
            state,
            new EventCandidate<SpatialMomentCandidate>(
                valid.Id,
                valid.Due,
                valid.SourceId,
                payload: null!)));

        SpatialState changed = Place(definition, state, 1, Cell("map", 0, 0));
        Assert.Throws<InvalidOperationException>(() => system.Resolve(changed, valid));
        SpatialDefinition otherDefinition = Definition([Map("other", 1, 1)]);
        Assert.Throws<InvalidOperationException>(() => system.Resolve(
            SpatialState.Create(otherDefinition),
            valid));
    }

    [Fact]
    public void Resolve_IdempotentMutationConsumesWorkWithoutValueEvent()
    {
        PortalFixture fixture = PortalWorld(initiallyEnabled: true);
        ModelTime due = AtSecond(1);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetPortalStateMutation(fixture.Portal.Id, isEnabled: true));
        SpatialState state = SpatialEventTestHarness.Apply(
            fixture.Definition,
            SpatialState.Create(fixture.Definition),
            new MutationScheduledEvent(mutation));
        var system = new SpatialSubsystem(fixture.Definition, sourceId: 7);
        EventCandidate<SpatialMomentCandidate> candidate = Assert.Single(
            system.ForecastNext(state, ModelTime.Zero));

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(state, candidate);

        Assert.Collection(
            events.Select(value => value.Payload),
            payload => Assert.Equal(mutation, Assert.IsType<MutationConsumedEvent>(payload).Mutation),
            payload => Assert.Equal(1, Assert.IsType<MomentResolvedEvent>(payload).ResolvedWorkCount));
        SpatialState result = Fold(fixture.Definition, state, due, events);
        Assert.Empty(result.ScheduledMutations);
        Assert.Empty(result.PortalOverrides);
        Assert.Equal(2, result.NextMomentOrdinal);
    }

    [Fact]
    public void Resolve_DueSightOverrideEmitsVisibilityDeltaFromFinalTopology()
    {
        SpatialDefinition definition = Definition([Map("map", 3, 1, visionRange: 3)]);
        CellRef observerCell = Cell("map", 0, 0);
        CellRef sightBlocker = Cell("map", 1, 0);
        CellRef targetCell = Cell("map", 2, 0);
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, 1, observerCell, observationEnabled: true);
        state = Place(definition, state, 2, targetCell);
        ModelTime due = AtSecond(1);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(sightBlocker, new CellOverride(blocksSight: true)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(mutation));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        GeometricVisibilityChangedEvent visibility = Assert.Single(
            events.Select(value => value.Payload).OfType<GeometricVisibilityChangedEvent>());
        Assert.Equal(new EntityId(1), visibility.ObserverId);
        Assert.Empty(visibility.AddedEntityIds);
        Assert.Equal([new EntityId(2)], visibility.RemovedEntityIds);
        Assert.True(
            events.ToList().FindIndex(value => value.Payload is GeometricVisibilityChangedEvent) >
            events.ToList().FindIndex(value => value.Payload is MutationConsumedEvent));
        Assert.IsType<MomentResolvedEvent>(events[^1].Payload);
        SpatialState result = Fold(definition, state, due, events);
        Assert.True(new EffectiveSpatialTopology(definition, result).BlocksSight(sightBlocker));
    }

    [Fact]
    public void Resolve_DoorClosingAtDueBlocksPortalLegBeforeStep()
    {
        PortalFixture fixture = PortalWorld(initiallyEnabled: true);
        SpatialState state = Place(
            fixture.Definition,
            SpatialState.Create(fixture.Definition),
            1,
            fixture.Portal.From);
        CurrentLeg leg = PortalLeg(fixture.Portal);
        state = StartJourney(
            fixture.Definition,
            state,
            1,
            1,
            leg,
            new CellGoal(fixture.Portal.To));
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            leg.Due,
            new SetPortalStateMutation(fixture.Portal.Id, isEnabled: false));
        state = SpatialEventTestHarness.Apply(
            fixture.Definition,
            state,
            new MutationScheduledEvent(mutation));
        var system = new SpatialSubsystem(fixture.Definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        Assert.Collection(
            events.Select(value => value.Payload),
            payload => Assert.IsType<PortalStateChangedEvent>(payload),
            payload => Assert.IsType<MutationConsumedEvent>(payload),
            payload =>
            {
                var blocked = Assert.IsType<JourneyBlockedEvent>(payload);
                Assert.Equal(JourneyBlockedReason.LegInvalidNoRoute, blocked.Reason);
            },
            payload => Assert.Equal(2, Assert.IsType<MomentResolvedEvent>(payload).ResolvedWorkCount));
        Assert.DoesNotContain(events, value => value.Payload is EntitySteppedEvent);
        SpatialState result = Fold(fixture.Definition, state, leg.Due, events);
        Assert.Empty(result.Journeys);
        Assert.Equal(fixture.Portal.From, Assert.Single(result.Entities).Cell);
        Assert.False(Assert.Single(result.PortalOverrides).IsEnabled);
    }

    [Fact]
    public void Resolve_SwapStepsBothActorsWithoutFalseCoPresence()
    {
        SpatialDefinition definition = Definition([Map("map", 2, 1)]);
        CellRef west = Cell("map", 0, 0);
        CellRef east = Cell("map", 1, 0);
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, 1, west);
        state = Place(definition, state, 2, east);
        state = StartJourney(definition, state, 1, 1, OrthogonalLeg(west, east), new CellGoal(east));
        state = StartJourney(definition, state, 2, 2, OrthogonalLeg(east, west), new CellGoal(west));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        Assert.Equal(
            [1L, 2L],
            events.Select(value => value.Payload)
                .OfType<EntitySteppedEvent>()
                .Select(value => value.EntityId.Value));
        Assert.Equal(
            [1L, 2L],
            events.Select(value => value.Payload)
                .OfType<JourneyCompletedEvent>()
                .Select(value => value.EntityId.Value));
        Assert.DoesNotContain(events, value =>
            value.Payload is CoPresenceStartedEvent or CoPresenceEndedEvent);
        SpatialState result = Fold(definition, state, AtSecond(1), events);
        Assert.Equal(east, result.Entities.Single(entity => entity.Id == new EntityId(1)).Cell);
        Assert.Equal(west, result.Entities.Single(entity => entity.Id == new EntityId(2)).Cell);
        Assert.Empty(result.Journeys);
    }

    [Fact]
    public void Resolve_SameTargetLetsBothActorsArriveAndEmitsOneFinalCoPresence()
    {
        SpatialDefinition definition = Definition([Map("map", 3, 1)]);
        CellRef west = Cell("map", 0, 0);
        CellRef target = Cell("map", 1, 0);
        CellRef east = Cell("map", 2, 0);
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, 1, west);
        state = Place(definition, state, 2, east);
        state = StartJourney(definition, state, 1, 1, OrthogonalLeg(west, target), new CellGoal(target));
        state = StartJourney(definition, state, 2, 2, OrthogonalLeg(east, target), new CellGoal(target));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        Assert.Equal(2, events.Count(value => value.Payload is EntitySteppedEvent));
        CoPresenceStartedEvent coPresence = Assert.Single(
            events.Select(value => value.Payload).OfType<CoPresenceStartedEvent>());
        Assert.Equal(new EntityId(1), coPresence.FirstEntityId);
        Assert.Equal(new EntityId(2), coPresence.SecondEntityId);
        SpatialState result = Fold(definition, state, AtSecond(1), events);
        Assert.All(result.Entities, entity => Assert.Equal(target, entity.Cell));
    }

    [Fact]
    public void Resolve_BlockedLegReroutesFromSourceAfterDueMutation()
    {
        SpatialDefinition definition = Definition([Map("map", 2, 2)]);
        CellRef source = Cell("map", 0, 0);
        CellRef failedTarget = Cell("map", 1, 0);
        CellRef rerouteTarget = Cell("map", 0, 1);
        CellRef goal = Cell("map", 1, 1);
        SpatialState state = Place(definition, SpatialState.Create(definition), 1, source);
        CurrentLeg failedLeg = OrthogonalLeg(source, failedTarget);
        state = StartJourney(definition, state, 1, 1, failedLeg, new CellGoal(goal));
        var blocker = new CellOverride(blocksMovement: true);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            failedLeg.Due,
            new SetCellOverrideMutation(failedTarget, blocker));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(mutation));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        Assert.DoesNotContain(events, value => value.Payload is EntitySteppedEvent);
        JourneyReroutedEvent rerouted = Assert.Single(
            events.Select(value => value.Payload).OfType<JourneyReroutedEvent>());
        Assert.Equal(failedLeg, rerouted.FailedLeg);
        Assert.Equal(source, rerouted.ResultingLeg.From);
        Assert.Equal(rerouteTarget, rerouted.ResultingLeg.To);
        Assert.Equal(failedLeg.Due, rerouted.ResultingLeg.StartedAt);
        SpatialState result = Fold(definition, state, failedLeg.Due, events);
        Assert.Equal(source, Assert.Single(result.Entities).Cell);
        Assert.Equal(rerouted.ResultingLeg, Assert.Single(result.Journeys).CurrentLeg);
    }

    [Fact]
    public void Resolve_AppliesEveryDueMutationBeforeReroutingInvalidLeg()
    {
        CellRef source = Cell("from", 0, 0);
        CellRef goal = Cell("to", 0, 0);
        var currentPortal = new PortalDefinition(
            new PortalId("current"),
            source,
            goal,
            ModelDuration.FromSeconds(1),
            initiallyEnabled: true);
        var alternatePortal = new PortalDefinition(
            new PortalId("alternate"),
            source,
            goal,
            ModelDuration.FromSeconds(2),
            initiallyEnabled: false);
        SpatialDefinition definition = Definition(
            [Map("from", 1, 1), Map("to", 1, 1)],
            [alternatePortal, currentPortal]);
        SpatialState state = Place(definition, SpatialState.Create(definition), 1, source);
        CurrentLeg failedLeg = PortalLeg(currentPortal);
        state = StartJourney(definition, state, 1, 1, failedLeg, new CellGoal(goal));
        var closeCurrent = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            failedLeg.Due,
            new SetPortalStateMutation(currentPortal.Id, isEnabled: false));
        var openAlternate = new ScheduledSpatialMutationState(
            new ScheduledMutationId(2),
            failedLeg.Due,
            new SetPortalStateMutation(alternatePortal.Id, isEnabled: true));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(closeCurrent));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(openAlternate));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        Assert.Equal(
            [1L, 2L],
            events.Select(value => value.Payload)
                .OfType<MutationConsumedEvent>()
                .Select(value => value.Mutation.Id.Value));
        JourneyReroutedEvent rerouted = Assert.Single(
            events.Select(value => value.Payload).OfType<JourneyReroutedEvent>());
        Assert.Equal(currentPortal.Id, rerouted.FailedLeg.PortalId);
        Assert.Equal(alternatePortal.Id, rerouted.ResultingLeg.PortalId);
        Assert.True(
            events.ToList().FindIndex(value => value.Payload is JourneyReroutedEvent) >
            events.ToList().FindLastIndex(value => value.Payload is MutationConsumedEvent));
        SpatialState result = Fold(definition, state, failedLeg.Due, events);
        Assert.Equal(alternatePortal.Id, Assert.Single(result.Journeys).CurrentLeg.PortalId);
    }

    [Fact]
    public void Resolve_SuccessfulStepWithoutContinuationEndsAsPostStepBlocked()
    {
        SpatialDefinition definition = Definition([Map("map", 3, 1)]);
        CellRef source = Cell("map", 0, 0);
        CellRef middle = Cell("map", 1, 0);
        CellRef goal = Cell("map", 2, 0);
        SpatialState state = Place(definition, SpatialState.Create(definition), 1, source);
        CurrentLeg leg = OrthogonalLeg(source, middle);
        state = StartJourney(definition, state, 1, 1, leg, new CellGoal(goal));
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            leg.Due,
            new SetCellOverrideMutation(goal, new CellOverride(blocksMovement: true)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(mutation));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        EntitySteppedEvent stepped = Assert.Single(
            events.Select(value => value.Payload).OfType<EntitySteppedEvent>());
        Assert.Equal(middle, stepped.To);
        JourneyBlockedEvent blocked = Assert.Single(
            events.Select(value => value.Payload).OfType<JourneyBlockedEvent>());
        Assert.Equal(JourneyBlockedReason.NoContinuationAfterStep, blocked.Reason);
        Assert.DoesNotContain(events, value => value.Payload is JourneyContinuedEvent);
        SpatialState result = Fold(definition, state, leg.Due, events);
        Assert.Equal(middle, Assert.Single(result.Entities).Cell);
        Assert.Empty(result.Journeys);
    }

    [Fact]
    public void Resolve_RerouteCostOverflowEndsAsPreStepBlocked()
    {
        CellRef source = Cell("source", 0, 0);
        CellRef routeEntry = Cell("route", 0, 0);
        CellRef goal = Cell("route", 1, 0);
        var currentPortal = new PortalDefinition(
            new PortalId("current"),
            source,
            goal,
            ModelDuration.FromSeconds(1),
            initiallyEnabled: true);
        var overlongPortal = new PortalDefinition(
            new PortalId("overlong"),
            source,
            routeEntry,
            new ModelDuration(long.MaxValue - 10),
            initiallyEnabled: true);
        SpatialDefinition definition = Definition(
            [
                Map("source", 1, 1),
                TestSpatialDefinitionBuilder.Map(
                    "route",
                    width: 2,
                    height: 1,
                    new ModelDuration(20),
                    visionRange: 0),
            ],
            [currentPortal, overlongPortal]);
        SpatialState state = Place(definition, SpatialState.Create(definition), 1, source);
        CurrentLeg failedLeg = PortalLeg(currentPortal);
        state = StartJourney(definition, state, 1, 1, failedLeg, new CellGoal(goal));
        var closeCurrent = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            failedLeg.Due,
            new SetPortalStateMutation(currentPortal.Id, isEnabled: false));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(closeCurrent));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        JourneyBlockedEvent blocked = Assert.Single(
            events.Select(value => value.Payload).OfType<JourneyBlockedEvent>());
        Assert.Equal(JourneyBlockedReason.LegInvalidNoRoute, blocked.Reason);
        Assert.DoesNotContain(events, value =>
            value.Payload is EntitySteppedEvent or JourneyReroutedEvent);
        SpatialState result = Fold(definition, state, failedLeg.Due, events);
        Assert.Equal(source, Assert.Single(result.Entities).Cell);
        Assert.Empty(result.Journeys);
    }

    [Fact]
    public void Resolve_AbsoluteNextDueOverflowEndsAsPostStepBlocked()
    {
        SpatialDefinition definition = Definition(
            [
                TestSpatialDefinitionBuilder.Map(
                    "map",
                    width: 3,
                    height: 1,
                    new ModelDuration(1),
                    visionRange: 0),
            ]);
        CellRef source = Cell("map", 0, 0);
        CellRef middle = Cell("map", 1, 0);
        CellRef goal = Cell("map", 2, 0);
        ModelTime startedAt = new(long.MaxValue - 1);
        ModelTime due = new(long.MaxValue);
        var leg = new CurrentLeg(
            source,
            middle,
            SpatialEdgeKind.Orthogonal,
            portalId: null,
            startedAt,
            due,
            journeyGeneration: 1);
        SpatialState state = Place(definition, SpatialState.Create(definition), 1, source);
        state = StartJourney(definition, state, 1, 1, leg, new CellGoal(goal));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, startedAt)));

        Assert.Single(events.Select(value => value.Payload).OfType<EntitySteppedEvent>());
        JourneyBlockedEvent blocked = Assert.Single(
            events.Select(value => value.Payload).OfType<JourneyBlockedEvent>());
        Assert.Equal(JourneyBlockedReason.NoContinuationAfterStep, blocked.Reason);
        Assert.DoesNotContain(events, value => value.Payload is JourneyContinuedEvent);
        SpatialState result = Fold(definition, state, due, events);
        Assert.Equal(middle, Assert.Single(result.Entities).Cell);
        Assert.Empty(result.Journeys);
    }

    [Fact]
    public void Resolve_SuccessfulIntermediateStepContinuesWithCompleteNextLeg()
    {
        SpatialDefinition definition = Definition([Map("map", 3, 1)]);
        CellRef first = Cell("map", 0, 0);
        CellRef middle = Cell("map", 1, 0);
        CellRef goal = Cell("map", 2, 0);
        SpatialState state = Place(definition, SpatialState.Create(definition), 1, first);
        CurrentLeg firstLeg = OrthogonalLeg(first, middle);
        state = StartJourney(definition, state, 1, 1, firstLeg, new CellGoal(goal));
        var system = new SpatialSubsystem(definition, sourceId: 7);

        IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> events = system.Resolve(
            state,
            Assert.Single(system.ForecastNext(state, ModelTime.Zero)));

        Assert.IsType<EntitySteppedEvent>(events[0].Payload);
        JourneyContinuedEvent continued = Assert.IsType<JourneyContinuedEvent>(events[1].Payload);
        Assert.Equal(firstLeg, continued.CompletedLeg);
        Assert.Equal(middle, continued.ResultingLeg.From);
        Assert.Equal(goal, continued.ResultingLeg.To);
        Assert.Equal(AtSecond(1), continued.ResultingLeg.StartedAt);
        Assert.Equal(AtSecond(2), continued.ResultingLeg.Due);
        Assert.IsType<MomentResolvedEvent>(events[^1].Payload);
        SpatialState result = Fold(definition, state, AtSecond(1), events);
        Assert.Equal(middle, Assert.Single(result.Entities).Cell);
        Assert.Equal(continued.ResultingLeg, Assert.Single(result.Journeys).CurrentLeg);
    }

    [Fact]
    public void SimulationLoop_TwoMomentsCommitReplayAndExhaustDeterministically()
    {
        SpatialDefinition definition = Definition([Map("map", 3, 1)]);
        CellRef first = Cell("map", 0, 0);
        CellRef middle = Cell("map", 1, 0);
        CellRef goal = Cell("map", 2, 0);
        SpatialState initial = Place(definition, SpatialState.Create(definition), 1, first);
        initial = StartJourney(
            definition,
            initial,
            1,
            1,
            OrthogonalLeg(first, middle),
            new CellGoal(goal));
        var system = new SpatialSubsystem(definition, sourceId: 7);
        var reducer = new SpatialReducer(definition);
        var journal = new InMemoryJournal<SpatialEvent>();
        var loop = new SimulationLoop<SpatialState, SpatialMomentCandidate, SpatialEvent>(
            [system],
            reducer);

        SimulationRunResult<SpatialState, SpatialEvent> run = loop.Run(
            initial,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            AtSecond(3),
            journal);

        Assert.Equal(StopReason.Exhausted, run.StopReason);
        Assert.Equal(AtSecond(2), run.CurrentTime);
        Assert.Equal(2, run.ResolvedCandidateCount);
        Assert.Equal(goal, Assert.Single(run.World.Entities).Cell);
        Assert.Empty(run.World.Journeys);
        Assert.Equal(3, run.World.NextMomentOrdinal);
        AssertResolveBatchShape(journal.Events);

        SpatialState replayed = journal.Events.Aggregate(initial, reducer.Apply);
        Assert.Equal(run.World, replayed);
        SpatialStateValidator.ValidateComplete(definition, replayed);

        DomainEvent<SpatialEvent>[] firstBatch =
        [
            .. journal.Events.Where(value => value.Cause.BatchOrdinal == 0),
        ];
        SpatialState replayedFirstMoment = firstBatch.Aggregate(initial, reducer.Apply);
        var partialJournal = new InMemoryJournal<SpatialEvent>();
        SimulationRunResult<SpatialState, SpatialEvent> partial = loop.Run(
            initial,
            SimulationCursor.CreateInitial(lineageId: 2, ModelTime.Zero),
            AtSecond(1),
            partialJournal);
        EventCandidate<SpatialMomentCandidate> expectedNext = Assert.Single(
            system.ForecastNext(partial.World, partial.CurrentTime));
        EventCandidate<SpatialMomentCandidate> replayedNext = Assert.Single(
            system.ForecastNext(replayedFirstMoment, AtSecond(1)));
        Assert.Equal(expectedNext.Id, replayedNext.Id);
        Assert.Equal(expectedNext.Due, replayedNext.Due);
        Assert.Equal(expectedNext.SourceId, replayedNext.SourceId);
        Assert.Equal(expectedNext.Payload, replayedNext.Payload);

        SimulationRunResult<SpatialState, SpatialEvent> split = loop.Run(
            partial.World,
            partial.Cursor,
            AtSecond(3),
            partialJournal);
        Assert.Equal(run.World, split.World);
        AssertJournalsEquivalent(journal.Events, partialJournal.Events);
        AssertResolveBatchShape(partialJournal.Events);

        var forkJournal = new InMemoryJournal<SpatialEvent>();
        foreach (DomainEvent<SpatialEvent> domainEvent in firstBatch)
        {
            forkJournal.Append(domainEvent);
        }

        SimulationRunResult<SpatialState, SpatialEvent> fork = loop.Run(
            replayedFirstMoment,
            SimulationCursor.CreateFork(
                lineageId: 3,
                now: AtSecond(1),
                nextBatchOrdinal: 1),
            AtSecond(3),
            forkJournal);
        Assert.Equal(run.World, fork.World);
        AssertJournalsEquivalent(journal.Events, forkJournal.Events);
        AssertResolveBatchShape(forkJournal.Events);
    }

    private static void AssertResolveBatchShape(
        IReadOnlyList<DomainEvent<SpatialEvent>> events)
    {
        foreach (IGrouping<long, DomainEvent<SpatialEvent>> batch in events.GroupBy(
                     value => value.Cause.BatchOrdinal))
        {
            DomainEvent<SpatialEvent>[] values = [.. batch];
            EventCause cause = values[0].Cause;
            Assert.Equal(CauseKind.ResolveBatch, cause.Kind);
            Assert.All(values, value =>
            {
                Assert.Equal(cause, value.Cause);
                Assert.Equal(cause.Due, value.Timestamp.ModelTime);
            });
            Assert.Equal(
                Enumerable.Range(0, values.Length),
                values.Select(value => value.Timestamp.Microstep.Value));
            Assert.Single(values, value => value.Payload is MomentResolvedEvent);
            Assert.IsType<MomentResolvedEvent>(values[^1].Payload);
        }
    }

    private static void AssertJournalsEquivalent(
        IReadOnlyList<DomainEvent<SpatialEvent>> expected,
        IReadOnlyList<DomainEvent<SpatialEvent>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Timestamp, actual[index].Timestamp);
            Assert.Equal(expected[index].Cause, actual[index].Cause);
            Assert.Equal(expected[index].Kind, actual[index].Kind);
            Assert.Equal(expected[index].Payload, actual[index].Payload);
        }
    }

    private static EventCandidate<SpatialMomentCandidate> Candidate(
        EventCandidate<SpatialMomentCandidate> template,
        long? sourceId = null,
        long? candidateId = null,
        ModelTime? due = null,
        SpatialMomentCandidate? payload = null) =>
        new(
            new EventCandidateId(candidateId ?? template.Id.Value),
            due ?? template.Due,
            sourceId ?? template.SourceId,
            payload ?? template.Payload);

    private static SpatialState Fold(
        SpatialDefinition definition,
        SpatialState initial,
        ModelTime time,
        IEnumerable<UncommittedDomainEvent<SpatialEvent>> events)
    {
        SpatialState state = initial;
        foreach (UncommittedDomainEvent<SpatialEvent> value in events)
        {
            state = SpatialEventTestHarness.Apply(
                definition,
                state,
                value.Payload,
                time,
                value.Kind);
        }

        SpatialStateValidator.ValidateComplete(definition, state);
        return state;
    }

    private static SpatialState Place(
        SpatialDefinition definition,
        SpatialState state,
        long entityId,
        CellRef cell,
        bool observationEnabled = false) =>
        SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntityPlacedEvent(new SpatialEntityState(
                new EntityId(entityId),
                cell,
                observationEnabled,
                movementGeneration: 0)));

    private static SpatialState StartJourney(
        SpatialDefinition definition,
        SpatialState state,
        long entityId,
        long journeyId,
        CurrentLeg leg,
        MoveGoal goal) =>
        SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(journeyId),
                new EntityId(entityId),
                goal,
                generation: 1,
                leg)),
            leg.StartedAt);

    private static CurrentLeg OrthogonalLeg(CellRef from, CellRef to) =>
        new(
            from,
            to,
            SpatialEdgeKind.Orthogonal,
            portalId: null,
            ModelTime.Zero,
            AtSecond(1),
            journeyGeneration: 1);

    private static CurrentLeg PortalLeg(PortalDefinition portal) =>
        new(
            portal.From,
            portal.To,
            SpatialEdgeKind.Portal,
            portal.Id,
            ModelTime.Zero,
            ModelTime.Zero + portal.TraversalDuration,
            journeyGeneration: 1);

    private static PortalFixture PortalWorld(bool initiallyEnabled)
    {
        CellRef from = Cell("from", 0, 0);
        CellRef to = Cell("to", 0, 0);
        var portal = new PortalDefinition(
            new PortalId("gate"),
            from,
            to,
            ModelDuration.FromSeconds(1),
            initiallyEnabled);
        return new PortalFixture(
            Definition([Map("from", 1, 1), Map("to", 1, 1)], [portal]),
            portal);
    }

    private static SpatialDefinition Definition(
        IReadOnlyList<GridMapDefinition> maps,
        IReadOnlyList<PortalDefinition>? portals = null) =>
        TestSpatialDefinitionBuilder.Create(maps, portals);

    private static GridMapDefinition Map(
        string id,
        int width,
        int height,
        int visionRange = 0) =>
        TestSpatialDefinitionBuilder.Map(id, width, height, visionRange: visionRange);

    private static CellRef Cell(string mapId, int x, int y) =>
        TestSpatialDefinitionBuilder.Cell(mapId, x, y);

    private static ModelTime AtSecond(long seconds) =>
        ModelTime.Zero + ModelDuration.FromSeconds(seconds);

    private sealed record PortalFixture(SpatialDefinition Definition, PortalDefinition Portal);
}

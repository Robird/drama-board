using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialTransitionTests
{
    [Fact]
    public void Complete_ProjectsCanonicalZoneCoPresenceAndVisibilityMatrix()
    {
        SpatialDefinition definition = Definition(
            width: 3,
            visionRange: 3,
            zones:
            [
                new ZoneDefinition(new ZoneId("west"), [Cell(0)]),
                new ZoneDefinition(new ZoneId("east"), [Cell(2)]),
            ]);
        SpatialState preState = SpatialState.Create(definition);
        SpatialEvent[] body =
        [
            Place(3, Cell(2), observationEnabled: true),
            Place(1, Cell(0), observationEnabled: true),
            Place(2, Cell(0), observationEnabled: false),
        ];

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition,
            preState,
            ModelTime.Zero,
            body);

        Assert.Equal(body, result.Events.Take(body.Length).Select(value => value.Payload));
        Assert.Collection(
            result.Events.Skip(body.Length).Select(value => value.Payload),
            payload => Assert.Equal(
                new ZoneEnteredEvent(new EntityId(1), new ZoneId("west")),
                payload),
            payload => Assert.Equal(
                new ZoneEnteredEvent(new EntityId(2), new ZoneId("west")),
                payload),
            payload => Assert.Equal(
                new ZoneEnteredEvent(new EntityId(3), new ZoneId("east")),
                payload),
            payload => Assert.Equal(
                new CoPresenceStartedEvent(new EntityId(1), new EntityId(2)),
                payload),
            payload => Assert.Equal(
                new GeometricVisibilityChangedEvent(
                    new EntityId(1),
                    [new EntityId(2), new EntityId(3)],
                    []),
                payload),
            payload => Assert.Equal(
                new GeometricVisibilityChangedEvent(
                    new EntityId(3),
                    [new EntityId(1), new EntityId(2)],
                    []),
                payload));
        Assert.Equal(
            result.Events.Select(value => SpatialEventKinds.For(value.Payload)),
            result.Events.Select(value => value.Kind));
        SpatialStateValidator.ValidateComplete(definition, result.ResultingState);
        Assert.Equal(
            result.ResultingState,
            FoldFormalReducer(definition, preState, ModelTime.Zero, result.Events));
    }

    [Fact]
    public void Complete_ObservationEnableAndDisableUseTrackedObserverUnion()
    {
        SpatialDefinition definition = Definition(width: 2, visionRange: 2);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(definition, state, Place(1, Cell(0), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(definition, state, Place(2, Cell(1), observationEnabled: false));

        SpatialTransitionResult enabled = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new ObservationStateChangedEvent(new EntityId(1), false, true)]);

        GeometricVisibilityChangedEvent added = Assert.IsType<GeometricVisibilityChangedEvent>(
            enabled.Events.Last().Payload);
        Assert.Equal(new EntityId(1), added.ObserverId);
        Assert.Equal([new EntityId(2)], added.AddedEntityIds);
        Assert.Empty(added.RemovedEntityIds);

        SpatialTransitionResult disabled = SpatialTransition.Complete(
            definition,
            enabled.ResultingState,
            ModelTime.Zero,
            [new ObservationStateChangedEvent(new EntityId(1), true, false)]);

        GeometricVisibilityChangedEvent removed = Assert.IsType<GeometricVisibilityChangedEvent>(
            disabled.Events.Last().Payload);
        Assert.Equal(new EntityId(1), removed.ObserverId);
        Assert.Empty(removed.AddedEntityIds);
        Assert.Equal([new EntityId(2)], removed.RemovedEntityIds);
    }

    [Fact]
    public void Complete_RemovalProducesAllRelationshipEndingsInFamilyOrder()
    {
        SpatialDefinition definition = Definition(
            width: 2,
            visionRange: 2,
            zones: [new ZoneDefinition(new ZoneId("home"), [Cell(0)])]);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(definition, state, Place(1, Cell(0), observationEnabled: true));
        state = SpatialEventTestHarness.Apply(definition, state, Place(2, Cell(0), observationEnabled: true));

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new EntityRemovedEvent(new EntityId(1), expectedMovementGeneration: 0, expectedActiveJourneyId: null)]);

        Assert.Collection(
            result.Events.Skip(1).Select(value => value.Payload),
            payload => Assert.IsType<ZoneLeftEvent>(payload),
            payload => Assert.IsType<CoPresenceEndedEvent>(payload),
            payload =>
            {
                var visibility = Assert.IsType<GeometricVisibilityChangedEvent>(payload);
                Assert.Equal(new EntityId(1), visibility.ObserverId);
                Assert.Equal([new EntityId(2)], visibility.RemovedEntityIds);
            },
            payload =>
            {
                var visibility = Assert.IsType<GeometricVisibilityChangedEvent>(payload);
                Assert.Equal(new EntityId(2), visibility.ObserverId);
                Assert.Equal([new EntityId(1)], visibility.RemovedEntityIds);
            });
    }

    [Fact]
    public void Complete_MultipleStepPrefixesDiffOnlyAfterAllJourneysComplete()
    {
        SpatialDefinition definition = Definition(
            width: 3,
            visionRange: 0,
            zones:
            [
                new ZoneDefinition(new ZoneId("start"), [Cell(0)]),
                new ZoneDefinition(new ZoneId("meeting"), [Cell(1)]),
            ]);
        CurrentLeg firstLeg = Leg(Cell(0), Cell(1), generation: 1);
        CurrentLeg secondLeg = Leg(Cell(2), Cell(1), generation: 1);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(definition, state, Place(1, Cell(0), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(definition, state, Place(2, Cell(2), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(definition, state, Place(3, Cell(0), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1),
                new EntityId(1),
                new CellGoal(Cell(1)),
                generation: 1,
                firstLeg)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(2),
                new EntityId(2),
                new CellGoal(Cell(1)),
                generation: 1,
                secondLeg)));
        SpatialEvent[] body =
        [
            new EntitySteppedEvent(new EntityId(1), new JourneyId(1), Cell(0), Cell(1), 1),
            new EntitySteppedEvent(new EntityId(2), new JourneyId(2), Cell(2), Cell(1), 1),
            ReachedGoal(1, 1, firstLeg),
            ReachedGoal(2, 2, secondLeg),
        ];

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition,
            state,
            AtSecond(1),
            body);

        SpatialEvent[] derived = [.. result.Events.Skip(body.Length).Select(value => value.Payload)];
        Assert.Collection(
            derived,
            payload => Assert.Equal(new ZoneEnteredEvent(new EntityId(1), new ZoneId("meeting")), payload),
            payload => Assert.Equal(new ZoneLeftEvent(new EntityId(1), new ZoneId("start")), payload),
            payload => Assert.Equal(new ZoneEnteredEvent(new EntityId(2), new ZoneId("meeting")), payload),
            payload => Assert.Equal(new CoPresenceStartedEvent(new EntityId(1), new EntityId(2)), payload),
            payload => Assert.Equal(new CoPresenceEndedEvent(new EntityId(1), new EntityId(3)), payload));
        SpatialStateValidator.ValidateComplete(definition, result.ResultingState);
        Assert.Empty(result.ResultingState.Journeys);
    }

    [Fact]
    public void Complete_ZoneFamilySortsLowIdEnterBeforeHighIdLeave()
    {
        SpatialDefinition definition = Definition(
            width: 1,
            visionRange: 0,
            zones: [new ZoneDefinition(new ZoneId("shared"), [Cell(0)])]);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            Place(9, Cell(0), observationEnabled: false));
        SpatialEvent[] body =
        [
            new EntityRemovedEvent(
                new EntityId(9),
                expectedMovementGeneration: 0,
                expectedActiveJourneyId: null),
            Place(1, Cell(0), observationEnabled: false),
        ];

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            body);

        Assert.Collection(
            result.Events.Skip(body.Length).Select(value => value.Payload),
            payload => Assert.Equal(
                new ZoneEnteredEvent(new EntityId(1), new ZoneId("shared")),
                payload),
            payload => Assert.Equal(
                new ZoneLeftEvent(new EntityId(9), new ZoneId("shared")),
                payload));
    }

    [Fact]
    public void Complete_CoPresenceFamilySortsLowPairStartBeforeHighPairEnd()
    {
        SpatialDefinition definition = Definition(width: 1, visionRange: 0);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            Place(8, Cell(0), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            Place(9, Cell(0), observationEnabled: false));
        SpatialEvent[] body =
        [
            new EntityRemovedEvent(
                new EntityId(8),
                expectedMovementGeneration: 0,
                expectedActiveJourneyId: null),
            new EntityRemovedEvent(
                new EntityId(9),
                expectedMovementGeneration: 0,
                expectedActiveJourneyId: null),
            Place(1, Cell(0), observationEnabled: false),
            Place(2, Cell(0), observationEnabled: false),
        ];

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            body);

        Assert.Collection(
            result.Events.Skip(body.Length).Select(value => value.Payload),
            payload => Assert.Equal(
                new CoPresenceStartedEvent(new EntityId(1), new EntityId(2)),
                payload),
            payload => Assert.Equal(
                new CoPresenceEndedEvent(new EntityId(8), new EntityId(9)),
                payload));
    }

    [Fact]
    public void Complete_ReturnsNoEventsAndSameStateForNoOp()
    {
        SpatialDefinition definition = Definition(width: 1, visionRange: 0);
        SpatialState state = SpatialState.Create(definition);

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            []);

        Assert.Same(state, result.ResultingState);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void Complete_InvalidLaterEventDoesNotMutateOrExposePartialState()
    {
        SpatialDefinition definition = Definition(width: 1, visionRange: 0);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(definition, state, Place(1, Cell(0), observationEnabled: true));
        long revision = state.Revision;

        Assert.Throws<InvalidOperationException>(() => SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [
                new ObservationStateChangedEvent(new EntityId(1), true, false),
                Place(1, Cell(0), observationEnabled: false),
            ]));

        Assert.Equal(revision, state.Revision);
        Assert.True(state.Entities.Single().ObservationEnabled);
    }

    [Fact]
    public void Complete_RejectsUnrepairedStepPrefixAtFinalBoundary()
    {
        SpatialDefinition definition = Definition(width: 2, visionRange: 0);
        CurrentLeg leg = Leg(Cell(0), Cell(1), generation: 1);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(definition, state, Place(1, Cell(0), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1),
                new EntityId(1),
                new CellGoal(Cell(1)),
                generation: 1,
                leg)));

        Assert.Throws<InvalidOperationException>(() => SpatialTransition.Complete(
            definition,
            state,
            AtSecond(1),
            [new EntitySteppedEvent(new EntityId(1), new JourneyId(1), Cell(0), Cell(1), 1)]));

        Assert.Equal(Cell(0), state.Entities.Single().Cell);
        SpatialStateValidator.ValidateComplete(definition, state);
    }

    [Fact]
    public void Complete_RejectsDerivedAndMomentEventsInBodyBeforeProjection()
    {
        SpatialDefinition definition = Definition(width: 1, visionRange: 0);
        SpatialState state = SpatialState.Create(definition);

        Assert.Throws<ArgumentException>(() => SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new ZoneEnteredEvent(new EntityId(1), new ZoneId("missing"))]));
        Assert.Throws<ArgumentException>(() => SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new MomentResolvedEvent(1, 1)]));
    }

    [Fact]
    public void CompleteMoment_AppendsStrictFinalMomentAndMatchesFormalReducerFold()
    {
        CellRef from = TestSpatialDefinitionBuilder.Cell("a", 0, 0);
        CellRef to = TestSpatialDefinitionBuilder.Cell("b", 0, 0);
        var portal = new PortalDefinition(
            new PortalId("gate"),
            from,
            to,
            ModelDuration.FromSeconds(1),
            initiallyEnabled: true);
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            [
                TestSpatialDefinitionBuilder.Map("a", width: 1, height: 1),
                TestSpatialDefinitionBuilder.Map("b", width: 1, height: 1),
            ],
            [portal]);
        ModelTime due = AtSecond(1);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetPortalStateMutation(portal.Id, isEnabled: false));
        SpatialState preState = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(mutation));
        SpatialEvent[] body =
        [
            new PortalStateChangedEvent(portal.Id, expectedOverride: null, resultingOverride: false),
            new MutationConsumedEvent(mutation),
        ];

        SpatialTransitionResult result = SpatialTransition.CompleteMoment(
            definition,
            preState,
            due,
            body,
            resolvedWorkCount: 1);

        MomentResolvedEvent terminal = Assert.IsType<MomentResolvedEvent>(result.Events.Last().Payload);
        Assert.Equal(1, terminal.MomentOrdinal);
        Assert.Equal(1, terminal.ResolvedWorkCount);
        Assert.DoesNotContain(result.Events.Take(result.Events.Count - 1), value =>
            value.Payload is MomentResolvedEvent);
        Assert.Equal(2, result.ResultingState.NextMomentOrdinal);
        Assert.Empty(result.ResultingState.ScheduledMutations);

        SpatialState replayed = FoldFormalReducer(definition, preState, due, result.Events);
        Assert.Equal(result.ResultingState, replayed);
    }

    [Fact]
    public void CompleteMoment_CountsDueJourneysAndMutationsFromPreState()
    {
        SpatialDefinition definition = Definition(width: 2, visionRange: 0);
        ModelTime due = AtSecond(1);
        CurrentLeg leg = Leg(Cell(0), Cell(1), generation: 1);
        var sightOverride = new CellOverride(blocksSight: true);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(Cell(0), sightOverride));
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            Place(1, Cell(0), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1),
                new EntityId(1),
                new CellGoal(Cell(1)),
                generation: 1,
                leg)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(mutation));
        SpatialEvent[] body =
        [
            new CellStateChangedEvent(
                Cell(0),
                expectedOverride: null,
                resultingOverride: sightOverride),
            new MutationConsumedEvent(mutation),
            new EntitySteppedEvent(
                new EntityId(1),
                new JourneyId(1),
                Cell(0),
                Cell(1),
                journeyGeneration: 1),
            ReachedGoal(1, 1, leg),
        ];

        SpatialTransitionResult result = SpatialTransition.CompleteMoment(
            definition,
            state,
            due,
            body,
            resolvedWorkCount: 2);

        var resolved = Assert.IsType<MomentResolvedEvent>(result.Events.Last().Payload);
        Assert.Equal(2, resolved.ResolvedWorkCount);
        Assert.Empty(result.ResultingState.Journeys);
        Assert.Empty(result.ResultingState.ScheduledMutations);
    }

    [Fact]
    public void CompleteMoment_RejectsNonPositiveNoDueFutureOnlyWrongCountAndEmptyBody()
    {
        SpatialDefinition definition = Definition(width: 1, visionRange: 0);
        SpatialState state = SpatialState.Create(definition);

        Assert.Throws<ArgumentOutOfRangeException>(() => SpatialTransition.CompleteMoment(
            definition,
            state,
            ModelTime.Zero,
            [],
            resolvedWorkCount: 0));
        Assert.Throws<InvalidOperationException>(() => SpatialTransition.CompleteMoment(
            definition,
            state,
            ModelTime.Zero,
            [],
            resolvedWorkCount: 1));

        ModelTime due = AtSecond(2);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(Cell(0), new CellOverride(blocksSight: true)));
        SpatialState futureOnly = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(mutation));
        Assert.Throws<InvalidOperationException>(() => SpatialTransition.CompleteMoment(
            definition,
            futureOnly,
            AtSecond(1),
            [new MutationConsumedEvent(mutation)],
            resolvedWorkCount: 1));
        Assert.Throws<InvalidOperationException>(() => SpatialTransition.CompleteMoment(
            definition,
            futureOnly,
            due,
            [new MutationConsumedEvent(mutation)],
            resolvedWorkCount: 2));
        Assert.Throws<ArgumentException>(() => SpatialTransition.CompleteMoment(
            definition,
            futureOnly,
            due,
            [],
            resolvedWorkCount: 1));
    }

    [Fact]
    public void DerivedRelations_RejectStampMismatchAtPublicInternalBoundary()
    {
        SpatialDefinition definition = Definition(width: 1, visionRange: 0);
        SpatialDefinition other = SpatialDefinition.Create(
            new SpatialDefinitionId("other"),
            revision: 0,
            rulesVersion: 1,
            [TestSpatialDefinitionBuilder.Map("other-map", width: 1, height: 1)]);

        Assert.Throws<InvalidOperationException>(() => DerivedSpatialRelations.Diff(
            definition,
            SpatialState.Create(definition),
            SpatialState.Create(other)));
    }

    private static SpatialState FoldFormalReducer(
        SpatialDefinition definition,
        SpatialState initial,
        ModelTime time,
        IEnumerable<UncommittedDomainEvent<SpatialEvent>> events)
    {
        SpatialState state = initial;
        int microstep = 0;
        var reducer = new SpatialReducer(definition);
        foreach (UncommittedDomainEvent<SpatialEvent> value in events)
        {
            state = reducer.Apply(
                state,
                new DomainEvent<SpatialEvent>(
                    new LogicalTimestamp(time, new Microstep(microstep++)),
                    EventCause.FromExternalInput(batchOrdinal: 10),
                    value.Kind,
                    value.Payload));
        }

        return state;
    }

    private static EntityPlacedEvent Place(long id, CellRef cell, bool observationEnabled) =>
        new(new SpatialEntityState(new EntityId(id), cell, observationEnabled, movementGeneration: 0));

    private static CurrentLeg Leg(CellRef from, CellRef to, long generation) =>
        new(
            from,
            to,
            SpatialEdgeKind.Orthogonal,
            portalId: null,
            ModelTime.Zero,
            AtSecond(1),
            generation);

    private static JourneyCompletedEvent ReachedGoal(long entityId, long journeyId, CurrentLeg leg) =>
        new(
            new EntityId(entityId),
            new JourneyId(journeyId),
            new CellGoal(Cell(1)),
            expectedGeneration: 1,
            resultingGeneration: 1,
            JourneyCompletionReason.ReachedGoal,
            leg);

    private static SpatialDefinition Definition(
        int width,
        int visionRange,
        IEnumerable<ZoneDefinition>? zones = null) =>
        TestSpatialDefinitionBuilder.Create(
            [TestSpatialDefinitionBuilder.Map("map", width, height: 1, visionRange: visionRange)],
            zones: zones);

    private static CellRef Cell(int x) => TestSpatialDefinitionBuilder.Cell("map", x, 0);

    private static ModelTime AtSecond(long seconds) =>
        ModelTime.Zero + ModelDuration.FromSeconds(seconds);
}

using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Planning;

public sealed class SpatialPlannerReducerTests
{
    [Fact]
    public void StartTraversal_UsesCeilingDurationAndIncrementsGenerationPerSegment()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(definition, ("actor", GraphTestWorld.A));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        var actor = new EntityId("actor");

        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(20),
            planner.TryStartTraversal(state, actor, GraphTestWorld.Bridge, speedSnapshot: 3, GraphTestWorld.Time(20)));

        SpatialEntity firstSegment = Assert.Single(state.Entities);
        Assert.Equal(1, firstSegment.MovementGeneration);
        TraversingLocation traversal = Assert.IsType<TraversingLocation>(firstSegment.Location);
        Assert.Equal(GraphTestWorld.Time(24), traversal.ArrivalDue);

        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(24),
            new TraversalArrivedFact(actor, ExpectedMovementGeneration: 1));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(30),
            planner.TryStartTraversal(state, actor, GraphTestWorld.Bridge, speedSnapshot: 5, GraphTestWorld.Time(30)));

        SpatialEntity secondSegment = Assert.Single(state.Entities);
        Assert.Equal(2, secondSegment.MovementGeneration);
        Assert.Equal(GraphTestWorld.Time(32), Assert.IsType<TraversingLocation>(secondSegment.Location).ArrivalDue);
    }

    [Fact]
    public void DirectionalEntry_IsCheckedAtSegmentCreationOnly()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(
                GraphTestWorld.Bridge,
                GraphTestWorld.A,
                GraphTestWorld.B,
                enterableFromA: true,
                enterableFromB: false)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("forward", GraphTestWorld.A),
            ("reverse", GraphTestWorld.B));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);

        SpatialPlanAccepted forward = GraphTestWorld.Accepted(planner.TryStartTraversal(
            state,
            new EntityId("forward"),
            GraphTestWorld.Bridge,
            speedSnapshot: 2,
            GraphTestWorld.Time(0)));
        Assert.IsType<TraversalStartedFact>(Assert.Single(forward.Facts));
        GraphTestWorld.Rejected(
            planner.TryStartTraversal(
                state,
                new EntityId("reverse"),
                GraphTestWorld.Bridge,
                speedSnapshot: 2,
                GraphTestWorld.Time(0)),
            "entry-closed");

        state = reducer.Apply(state, GraphTestWorld.Instant(0), Assert.Single(forward.Facts));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(1),
            planner.TrySetPassageEntryAccess(
                state,
                GraphTestWorld.Bridge,
                new PassageEntryPatch(false, null)));

        GraphTestWorld.Rejected(
            planner.TryStartTraversal(
                state,
                new EntityId("reverse"),
                GraphTestWorld.Bridge,
                speedSnapshot: 2,
                GraphTestWorld.Time(1)),
            "entry-closed");

        // Closing after commitment neither rewrites nor blocks completion of the active segment.
        TraversingLocation active = Assert.IsType<TraversingLocation>(state.Entities.Single(e => e.Id == new EntityId("forward")).Location);
        state = reducer.Apply(
            state,
            new LogicalInstant(active.ArrivalDue, 0),
            new TraversalArrivedFact(new EntityId("forward"), ExpectedMovementGeneration: 1));
        Assert.Equal(
            GraphTestWorld.B,
            Assert.IsType<AtPlaceLocation>(state.Entities.Single(e => e.Id == new EntityId("forward")).Location).PlaceId);
    }

    [Fact]
    public void GateFrontPlace_SeparatesApproachCompletionFromClosedCityEntry()
    {
        var outside = new PlaceId("outside");
        var gateFront = new PlaceId("gate-front");
        var city = new PlaceId("city");
        var approach = new PassageId("approach");
        var gate = new PassageId("gate");
        GraphDefinition definition = GraphDefinition.Create(
            [outside, gateFront, city],
            [
                GraphTestWorld.Passage(approach, outside, gateFront, length: 2),
                GraphTestWorld.Passage(gate, gateFront, city, length: 1),
            ]);
        GraphSpatialState state = GraphTestWorld.State(definition, ("actor", outside));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        var actor = new EntityId("actor");

        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0),
            planner.TryStartTraversal(state, actor, approach, speedSnapshot: 1, GraphTestWorld.Time(0)));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(1),
            planner.TrySetPassageEntryAccess(state, gate, new PassageEntryPatch(false, null)));
        state = reducer.Apply(state, GraphTestWorld.Instant(2), new TraversalArrivedFact(actor, 1));

        Assert.Equal(gateFront, Assert.IsType<AtPlaceLocation>(Assert.Single(state.Entities).Location).PlaceId);
        GraphTestWorld.Rejected(
            planner.TryStartTraversal(state, actor, gate, speedSnapshot: 1, GraphTestWorld.Time(2)),
            "entry-closed");
    }

    [Fact]
    public void ScheduledPartialPatch_UsesEffectiveAccessAtDueAndConsumesOnlyItself()
    {
        var second = new PassageId("second");
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C],
            [
                GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B),
                GraphTestWorld.Passage(second, GraphTestWorld.B, GraphTestWorld.C),
            ]);
        GraphSpatialState state = GraphTestWorld.State(definition);
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);

        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0),
            planner.TrySchedulePassageEntryChange(
                state,
                GraphTestWorld.Bridge,
                GraphTestWorld.Time(10),
                new PassageEntryPatch(false, null),
                GraphTestWorld.Time(0)));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0, 1),
            planner.TrySchedulePassageEntryChange(
                state,
                second,
                GraphTestWorld.Time(10),
                new PassageEntryPatch(false, null),
                GraphTestWorld.Time(0)));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(5),
            planner.TrySetPassageEntryAccess(
                state,
                GraphTestWorld.Bridge,
                new PassageEntryPatch(null, false)));

        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(10),
            new ScheduledPassageEntryChangeAppliedFact(GraphTestWorld.Bridge, GraphTestWorld.Time(10)));

        PassageEntryAccessOverride bridgeOverride = Assert.Single(
            state.PassageEntryAccessOverrides,
            value => value.PassageId == GraphTestWorld.Bridge);
        Assert.Equal(new PassageEntryAccess(false, false), bridgeOverride.Access);
        ScheduledPassageEntryChange remaining = Assert.Single(state.ScheduledPassageEntryChanges);
        Assert.Equal(second, remaining.PassageId);
    }

    [Fact]
    public void ScheduledNoOp_IsStillConsumedAndCanonicalDefaultRemovesOverride()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B)]);
        GraphSpatialState state = GraphTestWorld.State(definition);
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);

        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0),
            planner.TrySchedulePassageEntryChange(
                state,
                GraphTestWorld.Bridge,
                GraphTestWorld.Time(10),
                new PassageEntryPatch(true, null),
                GraphTestWorld.Time(0)));
        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(10),
            new ScheduledPassageEntryChangeAppliedFact(GraphTestWorld.Bridge, GraphTestWorld.Time(10)));

        Assert.Empty(state.ScheduledPassageEntryChanges);
        Assert.Empty(state.PassageEntryAccessOverrides);

        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(11),
            planner.TrySetPassageEntryAccess(
                state,
                GraphTestWorld.Bridge,
                new PassageEntryPatch(false, null)));
        Assert.Single(state.PassageEntryAccessOverrides);
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(12),
            planner.TrySetPassageEntryAccess(
                state,
                GraphTestWorld.Bridge,
                new PassageEntryPatch(true, null)));
        Assert.Empty(state.PassageEntryAccessOverrides);
    }

    [Fact]
    public void Planner_RejectsInvalidOrStaleObjectiveProposalsWithoutFacts()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B)]);
        GraphSpatialState state = GraphTestWorld.State(definition, ("actor", GraphTestWorld.A));
        var planner = new SpatialPlanner(definition);

        GraphTestWorld.Rejected(
            planner.TryStartTraversal(state, new EntityId("missing"), GraphTestWorld.Bridge, 1, GraphTestWorld.Time(0)),
            "entity-not-found");
        GraphTestWorld.Rejected(
            planner.TryStartTraversal(state, new EntityId("actor"), GraphTestWorld.Bridge, 0, GraphTestWorld.Time(0)),
            "invalid-speed");
        GraphTestWorld.Rejected(
            planner.TrySchedulePassageEntryChange(
                state,
                GraphTestWorld.Bridge,
                GraphTestWorld.Time(0),
                new PassageEntryPatch(false, null),
                GraphTestWorld.Time(0)),
            "schedule-not-in-future");
    }
}

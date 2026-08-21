using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Simulation;

public sealed class SpatialOccurrenceRuleTests
{
    private static readonly SimulationRules Rules = new(worldSeed: 123, maxTransitionsPerModelTime: 100);

    [Fact]
    public async Task Forecast_EmitsEveryArrivalAndScheduleAsAnIndependentCandidate()
    {
        TestContext context = CreateFourContenderContext();
        var rule = new SpatialOccurrenceRule(context.Definition);

        IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> candidates =
            rule.Forecast(context.State, Rules);

        Assert.Equal(4, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(GraphTestWorld.Time(10), candidate.Due.ModelTime));
        Assert.Equal(4, candidates.Select(candidate => candidate.Key).Distinct().Count());
        Assert.Equal(2, candidates.Count(candidate => candidate.Data is TraversalArrivalOccurrenceData));
        Assert.Equal(2, candidates.Count(candidate => candidate.Data is PassageEntryChangeOccurrenceData));

        OccurrenceCandidate<SpatialOccurrenceData> selected = candidates.Single(candidate =>
            candidate.Data is PassageEntryChangeOccurrenceData data &&
            data.Change.PassageId == GraphTestWorld.Bridge);
        TransitionDraft<GraphSpatialFact> draft = await rule.PlanSelectedAsync(
            context.State,
            selected,
            CancellationToken.None);
        GraphSpatialState next = context.Reducer.Apply(
            context.State,
            GraphTestWorld.Instant(10),
            Assert.Single(draft.Facts));

        IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> remaining = rule.Forecast(next, Rules);
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, candidate => candidate.Key == selected.Key);
        Assert.Contains(remaining, candidate =>
            candidate.Data is PassageEntryChangeOccurrenceData data &&
            data.Change.PassageId == new PassageId("second"));
        Assert.Equal(2, remaining.Count(candidate => candidate.Data is TraversalArrivalOccurrenceData));
    }

    [Fact]
    public async Task ScheduledNoOp_StillProducesANonEmptyFactThatConsumesItsExactSchedule()
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
        var rule = new SpatialOccurrenceRule(definition);
        OccurrenceCandidate<SpatialOccurrenceData> candidate = Assert.Single(rule.Forecast(state, Rules));

        TransitionDraft<GraphSpatialFact> draft = await rule.PlanSelectedAsync(
            state,
            candidate,
            CancellationToken.None);
        ScheduledPassageEntryChangeAppliedFact fact =
            Assert.IsType<ScheduledPassageEntryChangeAppliedFact>(Assert.Single(draft.Facts));
        state = reducer.Apply(state, GraphTestWorld.Instant(10), fact);

        Assert.Empty(state.ScheduledPassageEntryChanges);
        Assert.Empty(state.PassageEntryAccessOverrides);
        Assert.Empty(rule.Forecast(state, Rules));
    }

    [Fact]
    public async Task ArrivalPlanning_RejectsAStaleMovementGeneration()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B)]);
        GraphSpatialState state = GraphTestWorld.State(definition, ("actor", GraphTestWorld.A));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0),
            planner.TryStartTraversal(
                state,
                new EntityId("actor"),
                GraphTestWorld.Bridge,
                speedSnapshot: 1,
                GraphTestWorld.Time(0)));
        var rule = new SpatialOccurrenceRule(definition);
        OccurrenceCandidate<SpatialOccurrenceData> candidate = Assert.Single(rule.Forecast(state, Rules));
        TraversalArrivalOccurrenceData data = Assert.IsType<TraversalArrivalOccurrenceData>(candidate.Data);
        var stale = new OccurrenceCandidate<SpatialOccurrenceData>(
            candidate.Key,
            candidate.Due,
            data with { MovementGeneration = data.MovementGeneration + 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rule.PlanSelectedAsync(state, stale, CancellationToken.None));
    }

    private static TestContext CreateFourContenderContext()
    {
        var second = new PassageId("second");
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C],
            [
                GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10),
                GraphTestWorld.Passage(second, GraphTestWorld.B, GraphTestWorld.C, length: 10),
            ]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("alice", GraphTestWorld.A),
            ("bob", GraphTestWorld.C));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0),
            planner.TryStartTraversal(state, new EntityId("alice"), GraphTestWorld.Bridge, 1, GraphTestWorld.Time(0)));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0, 1),
            planner.TryStartTraversal(state, new EntityId("bob"), second, 1, GraphTestWorld.Time(0)));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0, 2),
            planner.TrySchedulePassageEntryChange(
                state,
                GraphTestWorld.Bridge,
                GraphTestWorld.Time(10),
                new PassageEntryPatch(false, null),
                GraphTestWorld.Time(0)));
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0, 3),
            planner.TrySchedulePassageEntryChange(
                state,
                second,
                GraphTestWorld.Time(10),
                new PassageEntryPatch(null, false),
                GraphTestWorld.Time(0)));
        return new TestContext(definition, state, reducer);
    }

    private sealed record TestContext(
        GraphDefinition Definition,
        GraphSpatialState State,
        GraphSpatialReducer Reducer);
}

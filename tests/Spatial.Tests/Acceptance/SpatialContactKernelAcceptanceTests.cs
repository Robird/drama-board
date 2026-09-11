using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Acceptance;

public sealed class SpatialContactKernelAcceptanceTests
{
    [Fact]
    public async Task ContactOnlyKernel_ForecastsPlansFoldsAndReplaysWithoutAnotherDomain()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState genesis = GraphTestWorld.State(
            definition,
            ("alice", GraphTestWorld.A),
            ("bob", GraphTestWorld.B));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        genesis = GraphTestWorld.Fold(
            reducer,
            genesis,
            GraphTestWorld.Instant(0),
            planner.TryStartTraversal(
                genesis,
                new EntityId("alice"),
                GraphTestWorld.Bridge,
                speedSnapshot: 4,
                GraphTestWorld.Time(0)));
        genesis = GraphTestWorld.Fold(
            reducer,
            genesis,
            GraphTestWorld.Instant(0, 1),
            planner.TryStartTraversal(
                genesis,
                new EntityId("bob"),
                GraphTestWorld.Bridge,
                speedSnapshot: 3,
                GraphTestWorld.Time(0)));
        var history = new InMemoryOccurrenceHistory<GraphSpatialState, GraphSpatialFact>(
            genesis, new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        var kernel = new SimulationKernel<
            GraphSpatialState,
            PassageContactOccurrenceData,
            GraphSpatialFact>(
                history,
                new SimulationRules(worldSeed: 17, maxTransitionsPerModelTime: 100),
                [new SpatialContactOccurrenceRule(definition)],
                reducer.Apply,
                state => GraphSpatialStateValidator.ValidateComplete(definition, state));

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(GraphTestWorld.Time(2)));
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(GraphTestWorld.Time(2)));
        Assert.Single(kernel.World.ConsumedContacts);
        Assert.Single(history.CompletedEvents);
        Assert.IsType<PassageContactOccurredFact>(Assert.Single(history.CompletedEvents[0].Facts));

        ReplayResult<GraphSpatialState> replay = SimulationReplay.Replay(
            genesis,
            lineageId: 1,
            genesisTime: ModelTime.Zero,
            Batches(history),
            reducer.Apply,
            state => GraphSpatialStateValidator.ValidateComplete(definition, state));

        Assert.Equal(kernel.World, replay.World);
        Assert.Equal(kernel.Version, replay.Version);
        Assert.Equal(kernel.LastCommittedInstant, replay.LastCommittedInstant);
    }

    private static IReadOnlyList<JournalBatch<GraphSpatialFact>> Batches(
        InMemoryOccurrenceHistory<GraphSpatialState, GraphSpatialFact> history) =>
        history.CompletedEvents.Select(value => new JournalBatch<GraphSpatialFact>(
            value.TargetInstant, value.CauseKey, value.Facts)).ToArray();
}

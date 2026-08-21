using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Acceptance;

public sealed class SpatialKernelAcceptanceTests
{
    [Fact]
    public async Task SameTickWinner_CommitsOneBatchAndQueriesReadTheCommittedPrefixWithPeersPending()
    {
        ContenderWorld context = CreateContenderWorld(reverseInputs: false);
        ulong seed = FindSeedSelectingScheduleFirst(context);
        InMemoryJournal<GraphSpatialFact> journal = new(lineageId: 1);
        SimulationKernel<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact> kernel =
            CreateKernel(context, seed, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(GraphTestWorld.Time(10)));

        Assert.Equal(GraphTestWorld.Time(10), kernel.CurrentModelTime);
        Assert.Equal(new WorldVersion(1, 1), kernel.Version);
        Assert.Single(journal.Batches);
        Assert.Single(journal.Batches[0].Facts);
        Assert.IsType<ScheduledPassageEntryChangeAppliedFact>(journal.Batches[0].Facts[0]);

        SpatialEntity pendingArrival = kernel.World.Entities.First(entity =>
            entity.Location is TraversingLocation traversal && traversal.ArrivalDue == GraphTestWorld.Time(10));
        TraversingView boundary = Assert.IsType<TraversingView>(
            context.Queries.GetLocation(kernel.World, pendingArrival.Id, kernel.CurrentModelTime));
        PassageDefinition passage = context.Definition.GetPassage(boundary.PassageId);
        Assert.Equal(
            boundary.ToPlaceId == passage.EndpointB ? passage.Length : 0,
            boundary.Offset);

        IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> peers =
            context.Rule.Forecast(kernel.World, new SimulationRules(seed, 100));
        Assert.Equal(3, peers.Count);
        Assert.All(peers, candidate => Assert.Equal(GraphTestWorld.Time(10), candidate.Due.ModelTime));
    }

    [Fact]
    public async Task SameTickCandidates_AreGloballyArbitratedThenFullyReforecastToExhaustion()
    {
        ContenderWorld context = CreateContenderWorld(reverseInputs: false);
        InMemoryJournal<GraphSpatialFact> journal = new(lineageId: 1);
        SimulationKernel<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact> kernel =
            CreateKernel(context, worldSeed: 987, journal);

        for (int expectedCount = 1; expectedCount <= 4; expectedCount++)
        {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(GraphTestWorld.Time(10)));
            Assert.Equal(expectedCount, kernel.Version.TransitionCount);
            Assert.Equal(expectedCount, journal.Batches.Count);
        }

        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(GraphTestWorld.Time(10)));
        Assert.Equal([0L, 1L, 2L, 3L], journal.Batches.Select(batch => batch.Instant.CausalOrdinal));
        Assert.All(journal.Batches, batch =>
        {
            Assert.Equal(GraphTestWorld.Time(10), batch.Instant.ModelTime);
            Assert.Single(batch.Facts);
        });
        Assert.All(kernel.World.Entities, entity => Assert.IsType<AtPlaceLocation>(entity.Location));
        Assert.Empty(kernel.World.ScheduledPassageEntryChanges);
        Assert.Equal(2, kernel.World.PassageEntryAccessOverrides.Count);
    }

    [Fact]
    public async Task DefinitionPermutationAndForecastEnumeration_DoNotChangeWinnerSequence()
    {
        ContenderWorld first = CreateContenderWorld(reverseInputs: false);
        ContenderWorld second = CreateContenderWorld(reverseInputs: true);
        InMemoryJournal<GraphSpatialFact> firstJournal = new(lineageId: 1);
        InMemoryJournal<GraphSpatialFact> secondJournal = new(lineageId: 1);
        SimulationKernel<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact> firstKernel =
            CreateKernel(first, worldSeed: 71, firstJournal);
        SimulationKernel<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact> secondKernel =
            CreateKernel(second, worldSeed: 71, secondJournal);

        for (int index = 0; index < 4; index++)
        {
            Assert.Equal(StepStatus.Committed, await firstKernel.StepAsync(GraphTestWorld.Time(10)));
            Assert.Equal(StepStatus.Committed, await secondKernel.StepAsync(GraphTestWorld.Time(10)));
        }

        Assert.Equal(
            firstJournal.Batches.Select(batch => batch.CauseKey),
            secondJournal.Batches.Select(batch => batch.CauseKey));
        Assert.Equal(
            firstJournal.Batches.Select(batch => batch.Facts.Single()),
            secondJournal.Batches.Select(batch => batch.Facts.Single()));
        Assert.Equal(firstKernel.World, secondKernel.World);
    }

    [Fact]
    public async Task Replay_FoldsCompleteSpatialBatchesAndForkContinuesIndependently()
    {
        ContenderWorld context = CreateContenderWorld(reverseInputs: false);
        const ulong Seed = 41;
        var simulationRules = new SimulationRules(Seed, 100);
        InMemoryJournal<GraphSpatialFact> sourceJournal = new(lineageId: 1);
        SimulationKernel<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact> source =
            CreateKernel(context, Seed, sourceJournal);
        for (int index = 0; index < 4; index++)
        {
            Assert.Equal(StepStatus.Committed, await source.StepAsync(GraphTestWorld.Time(10)));
        }

        ReplayResult<GraphSpatialState> replay = SimulationReplay.Replay(
            context.Genesis,
            lineageId: 1,
            genesisTime: ModelTime.Zero,
            sourceJournal.Batches,
            context.Reducer.Apply,
            state => GraphSpatialStateValidator.ValidateComplete(context.Definition, state));

        Assert.Equal(source.World, replay.World);
        Assert.Equal(source.Version, replay.Version);
        Assert.Equal(source.LastCommittedInstant, replay.LastCommittedInstant);
        Assert.Equal(source.CurrentModelTime, replay.CurrentModelTime);

        InMemoryForkResult<GraphSpatialState, GraphSpatialFact> fork = SimulationFork.Create(
            context.Genesis,
            ModelTime.Zero,
            sourceJournal,
            prefixTransitionCount: 2,
            newLineageId: 99,
            simulationRules,
            context.Reducer.Apply,
            state => GraphSpatialStateValidator.ValidateComplete(context.Definition, state));
        var forkKernel = new SimulationKernel<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact>(
            fork.Replay.World,
            fork.Replay.Version,
            ModelTime.Zero,
            fork.Replay.LastCommittedInstant,
            fork.SimulationRules,
            [context.Rule],
            fork.Journal,
            context.Reducer.Apply,
            state => GraphSpatialStateValidator.ValidateComplete(context.Definition, state));
        Assert.Equal(StepStatus.Committed, await forkKernel.StepAsync(GraphTestWorld.Time(10)));
        Assert.Equal(StepStatus.Committed, await forkKernel.StepAsync(GraphTestWorld.Time(10)));
        Assert.Equal(StepStatus.Exhausted, await forkKernel.StepAsync(GraphTestWorld.Time(10)));

        Assert.Equal(source.World, forkKernel.World);
        Assert.Equal(4, sourceJournal.Batches.Count);
        Assert.Equal(4, fork.Journal.Batches.Count);
        Assert.Equal(1, sourceJournal.LineageId);
        Assert.Equal(99, fork.Journal.LineageId);
        Assert.NotSame(sourceJournal, fork.Journal);
    }

    private static SimulationKernel<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact> CreateKernel(
        ContenderWorld context,
        ulong worldSeed,
        InMemoryJournal<GraphSpatialFact> journal) =>
        new(
            context.Genesis,
            new WorldVersion(journal.LineageId, 0),
            ModelTime.Zero,
            lastCommittedInstant: null,
            new SimulationRules(worldSeed, maxTransitionsPerModelTime: 100),
            [context.Rule],
            journal,
            context.Reducer.Apply,
            state => GraphSpatialStateValidator.ValidateComplete(context.Definition, state));

    private static ulong FindSeedSelectingScheduleFirst(ContenderWorld context)
    {
        IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> candidates =
            context.Rule.Forecast(context.Genesis, new SimulationRules(0, 100));
        for (ulong seed = 0; seed < 10_000; seed++)
        {
            OccurrenceCandidate<SpatialOccurrenceData> winner =
                OccurrenceScheduler.SelectWinner(candidates, seed);
            if (winner.Data is PassageEntryChangeOccurrenceData)
            {
                return seed;
            }
        }

        throw new InvalidOperationException("Could not find a deterministic schedule-first test seed.");
    }

    private static ContenderWorld CreateContenderWorld(bool reverseInputs)
    {
        var second = new PassageId("second");
        PlaceId[] places = [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C];
        PassageDefinition[] passages =
        [
            GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10),
            GraphTestWorld.Passage(second, GraphTestWorld.B, GraphTestWorld.C, length: 10),
        ];
        if (reverseInputs)
        {
            Array.Reverse(places);
            Array.Reverse(passages);
        }

        GraphDefinition definition = GraphDefinition.Create(places, passages);
        EntityPlacement[] placements =
        [
            new(new EntityId("alice"), GraphTestWorld.A),
            new(new EntityId("bob"), GraphTestWorld.C),
        ];
        if (reverseInputs)
        {
            Array.Reverse(placements);
        }

        GraphSpatialState state = GraphSpatialState.Create(definition, placements);
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
        return new ContenderWorld(
            definition,
            state,
            reducer,
            new SpatialOccurrenceRule(definition),
            new SpatialQueries(definition));
    }

    private sealed record ContenderWorld(
        GraphDefinition Definition,
        GraphSpatialState Genesis,
        GraphSpatialReducer Reducer,
        SpatialOccurrenceRule Rule,
        SpatialQueries Queries);
}

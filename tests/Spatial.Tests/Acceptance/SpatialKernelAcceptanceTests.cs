using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests.Acceptance;

public sealed class SpatialKernelAcceptanceTests
{
    [Fact]
    public async Task CommandFactsThenIndependentArrivals_ReachGoalAndReplay()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            maps: [TestSpatialDefinitionBuilder.Map("world", width: 3, height: 1)]);
        SpatialState genesis = SpatialState.Create(definition);
        var handler = new SpatialCommandHandler(definition);
        SpatialCommandPlan placement = handler.Handle(
            genesis,
            new PlaceEntityCommand(
                new SpatialCommandId("place"),
                new EntityId(1),
                TestSpatialDefinitionBuilder.Cell("world", 0, 0),
                observationEnabled: true),
            ModelTime.Zero);

        var reducer = new SpatialReducer(definition);
        SpatialState prepared = genesis;
        var commandInstant = new LogicalInstant(ModelTime.Zero, 0);
        foreach (SpatialEvent fact in placement.Facts)
        {
            prepared = reducer.Apply(prepared, commandInstant, fact);
        }

        SpatialCommandPlan assignment = handler.Handle(
            prepared,
            new AssignMoveGoalCommand(
                new SpatialCommandId("move"),
                new EntityId(1),
                new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 2, 0))),
            ModelTime.Zero);
        foreach (SpatialEvent fact in assignment.Facts)
        {
            prepared = reducer.Apply(prepared, commandInstant, fact);
        }

        Assert.Equal(SpatialCommandDisposition.Accepted, placement.Result.Disposition);
        Assert.Equal(SpatialCommandDisposition.Accepted, assignment.Result.Disposition);
        Assert.All(placement.Facts.Concat(assignment.Facts), fact =>
            Assert.IsAssignableFrom<SpatialEvent>(fact));

        JourneyState journey = Assert.Single(prepared.Journeys);
        var journal = new InMemoryJournal<SpatialEvent>(lineageId: 101);
        var rules = new SimulationRules(worldSeed: 37, maxTransitionsPerModelTime: 8);
        var kernel = new SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent>(
            prepared,
            new WorldVersion(101, 0),
            ModelTime.Zero,
            lastCommittedInstant: null,
            rules,
            [new SpatialOccurrenceRule(definition)],
            journal,
            reducer.Apply,
            world => SpatialStateValidator.ValidateComplete(definition, world));

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Single(kernel.World.Journeys);
        Assert.NotEqual(journey.CurrentLeg, kernel.World.Journeys[0].CurrentLeg);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Empty(kernel.World.Journeys);
        Assert.Equal(TestSpatialDefinitionBuilder.Cell("world", 2, 0), Assert.Single(kernel.World.Entities).Cell);
        Assert.Equal(2, journal.Batches.Count);

        ReplayResult<SpatialState> replay = SimulationReplay.Replay(
            prepared,
            lineageId: 202,
            ModelTime.Zero,
            journal.Batches,
            reducer.Apply,
            world => SpatialStateValidator.ValidateComplete(definition, world));
        Assert.Equal(kernel.World, replay.World);
        Assert.Equal(new WorldVersion(202, 2), replay.Version);
    }
}

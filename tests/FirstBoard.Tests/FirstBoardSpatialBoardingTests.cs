using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class FirstBoardSpatialBoardingTests
{
    private const long LineageId = 45_001;
    private const string TicketId = "ferry-ticket";
    private static readonly EntityId Traveler = new(1);
    private static readonly MapId Map = new("ferry");
    private static readonly CellRef Origin = new(Map, 0, 0);
    private static readonly CellRef Destination = new(Map, 1, 0);

    [Fact]
    public async Task TicketConsumptionAndRealJourneyStart_CommitInOneBatch()
    {
        SpatialDefinition definition = CreateDefinition(destinationBlocked: false);
        FirstBoardSpatialWorld genesis = CreateWorld(definition);
        var journal = new InMemoryJournal<FirstBoardSpatialFact>(LineageId);
        SimulationKernel<
            FirstBoardSpatialWorld,
            TicketedTraversalCandidate,
            FirstBoardSpatialFact> kernel = CreateKernel(genesis, definition, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        JournalBatch<FirstBoardSpatialFact> batch = Assert.Single(journal.Batches);
        Assert.Contains(batch.Facts, fact => fact is TicketConsumedFact);
        Assert.Contains(batch.Facts, fact =>
            fact is SpatialBoardingFact { Value: JourneyStartedEvent });
        Assert.DoesNotContain(kernel.World.Game.Objects, item => item.Key == TicketId);
        Assert.Single(kernel.World.Spatial.Journeys);
        Assert.Equal(1, kernel.Version.TransitionCount);
    }

    [Fact]
    public async Task RealSpatialRejection_LeavesTicketWorldAndJournalUntouched()
    {
        SpatialDefinition definition = CreateDefinition(destinationBlocked: true);
        FirstBoardSpatialWorld genesis = CreateWorld(definition);
        var journal = new InMemoryJournal<FirstBoardSpatialFact>(LineageId);
        SimulationKernel<
            FirstBoardSpatialWorld,
            TicketedTraversalCandidate,
            FirstBoardSpatialFact> kernel = CreateKernel(genesis, definition, journal);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(ModelTime.Zero));

        Assert.Contains(nameof(SpatialCommandRejectionCode.JourneyUnreachable), exception.Message);
        Assert.Empty(journal.Batches);
        Assert.Contains(kernel.World.Game.Objects, item => item.Key == TicketId);
        Assert.Empty(kernel.World.Spatial.Journeys);
        Assert.Equal(genesis, kernel.World);
        Assert.Equal(0, kernel.Version.TransitionCount);
    }

    private static SimulationKernel<
        FirstBoardSpatialWorld,
        TicketedTraversalCandidate,
        FirstBoardSpatialFact> CreateKernel(
        FirstBoardSpatialWorld world,
        SpatialDefinition definition,
        IJournalSink<FirstBoardSpatialFact> journal) =>
        FirstBoardSpatialBoarding.CreateKernel(
            world,
            definition,
            BoardIds.Alice,
            TicketId,
            Traveler,
            Destination,
            journal);

    private static FirstBoardSpatialWorld CreateWorld(SpatialDefinition definition)
    {
        FirstBoardWorld game = FirstBoardWorld.CreateInitial(worldSeed: 73);
        BoardActor alice = game.Actor(BoardIds.Alice);
        var ticket = new BoardObject(game.NextPersistentId, TicketId, PlaceId: null, alice.Id);
        game = game with
        {
            NextPersistentId = checked(game.NextPersistentId + 1),
            Objects = Array.AsReadOnly(game.Objects.Append(ticket).ToArray()),
        };

        var handler = new SpatialCommandHandler(definition);
        var reducer = new SpatialReducer(definition);
        SpatialState spatial = SpatialState.Create(definition);
        SpatialCommandPlan placement = handler.Handle(
            spatial,
            new PlaceEntityCommand(
                new SpatialCommandId("place-ferry-traveler"),
                Traveler,
                Origin,
                observationEnabled: false),
            ModelTime.Zero);
        foreach (SpatialEvent fact in placement.Facts)
        {
            spatial = reducer.Apply(spatial, new LogicalInstant(ModelTime.Zero, 0), fact);
        }

        return new FirstBoardSpatialWorld(game, spatial);
    }

    private static SpatialDefinition CreateDefinition(bool destinationBlocked)
    {
        var floor = new CellDefinition(
            new TerrainId("floor"),
            moveCost: 1,
            blocksMovement: false,
            blocksSight: false);
        var destination = new CellDefinition(
            new TerrainId(destinationBlocked ? "blocked" : "floor"),
            moveCost: 1,
            blocksMovement: destinationBlocked,
            blocksSight: destinationBlocked);
        var map = new GridMapDefinition(
            Map,
            width: 2,
            height: 1,
            ModelDuration.FromSeconds(1),
            visionRange: 0,
            [floor, destination]);
        return SpatialDefinition.Create(
            new SpatialDefinitionId("ticketed-ferry"),
            revision: 0,
            rulesVersion: 1,
            [map]);
    }
}

using Atelia.DurableGraph.StateStore;
using DramaBoard.FirstBoard.Persistence;
using DramaBoard.FirstBoard.Tests;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests;

public sealed class ContactFloorPersistenceTests
{
    private static readonly ModelTime ContactDue = new(1);
    private static readonly FirstBoardDriverBinding DriverBinding =
        new([BoardIds.Alice], "fractional-contact-response/v1");

    [Theory]
    [InlineData(PassageEncounterResolution.Continued)]
    [InlineData(PassageEncounterResolution.Reversed)]
    public async Task FractionalContact_PendingRecoveryAndResponseMatchUninterruptedRun(
        PassageEncounterResolution response)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance scenario = CreateScenario();
        FirstBoardWorld initial = CreateTravelingWorld(scenario);
        var expectedHistory = FirstBoardScenario.CreateMemoryHistory(initial);
        KernelCursor initialCursor = expectedHistory.Cursor;
        var expectedDriver = new ResponseDriver(response);
        var expectedKernel = FirstBoardScenario.CreateKernel(Drivers(expectedDriver), scenario, expectedHistory);

        // Exact contact is 10 / 7; Alice's exact arrival is 10 / 6.
        // Interaction opens at floor(contact) = 1, before ceil(arrival) = 2.
        Assert.Equal(new ModelTime(2), Traversal(initial, BoardIds.Alice).ArrivalDue);
        Assert.Equal(StepStatus.Committed, await expectedKernel.StepAsync(ContactDue));
        var opening = Assert.Single(expectedHistory.CompletedEvents);
        Assert.Equal(ContactDue, opening.TargetInstant.ModelTime);
        Assert.Single(opening.Facts.OfType<SpatialBoardFact>(),
            fact => fact.Value is PassageContactOccurredFact);
        Assert.NotNull(expectedKernel.World.Game.PendingEncounter);
        Assert.Equal(0, expectedDriver.Calls);
        FirstBoardWorld openedWorld = expectedKernel.World;
        KernelCursor openedCursor = expectedKernel.Cursor;

        // Leave a real E-head containing the floor-tick facts, without publishing S.
        using (var history = FirstBoardOccurrenceHistory.Create(directory.Path, "main", scenario,
            initial, initialCursor, new SimulationRules(scenario.WorldSeed, 10_000), DriverBinding))
        {
            history.CommitEvent(opening);
        }

        Assert.Equal(StepStatus.Committed, await expectedKernel.StepAsync(ContactDue));
        Assert.Equal(1, expectedDriver.Calls);
        Assert.Null(expectedKernel.World.Game.PendingEncounter);

        using (var history = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriverBinding))
        {
            Assert.Equal(ContactDue, Assert.IsType<OccurrenceEvent<FirstBoardFact>>(history.PendingEvent)
                .TargetInstant.ModelTime);
            EncounterPersistenceOracle.AssertWorldEqual(initial, history.State);
            var recovery = new RecoveryOnlyKernel(history);
            Assert.True(recovery.Kernel.RecoverPending());
            Assert.Equal(opening.Facts.Count, recovery.FoldCalls);
            Assert.Equal(0, recovery.RuleCalls);
            Assert.Null(history.PendingEvent);
            EncounterPersistenceOracle.AssertWorldEqual(openedWorld, history.State);
            Assert.Equal(openedCursor, history.Cursor);

            var resumedDriver = new ResponseDriver(response);
            var resumed = FirstBoardScenario.CreateKernel(Drivers(resumedDriver), history.Scenario, history);
            Assert.Equal(StepStatus.Committed, await resumed.StepAsync(ContactDue));
            Assert.Equal(1, resumedDriver.Calls);
            EncounterPersistenceOracle.AssertWorldEqual(expectedKernel.World, history.State);
            Assert.Equal(expectedKernel.Cursor, history.Cursor);
        }

        // A later S-head reopen performs no old contact fold or Player invocation.
        using (var history = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriverBinding))
        {
            var recovery = new RecoveryOnlyKernel(history);
            Assert.False(recovery.Kernel.RecoverPending());
            Assert.Equal(0, recovery.FoldCalls);
            Assert.Equal(0, recovery.RuleCalls);
            EncounterPersistenceOracle.AssertWorldEqual(expectedKernel.World, history.State);
            Assert.Equal(expectedKernel.Cursor, history.Cursor);
        }

        using var reader = EventHistoryRepository.OpenReadOnlyExisting(directory.Path);
        Assert.Equal(2, reader.ReadEvents("main").Count);
        Assert.Equal(5, reader.ReadFrames("main").Count);
    }

    private static ScenarioInstance CreateScenario() => new(ScenarioDefinition.Default with
    {
        Passages = ScenarioDefinition.Default.Passages.Select(passage =>
            passage.Id == BoardIds.TavernMarketRoad ? passage with { Length = 10 } : passage).ToArray(),
    }, 1901);

    private static FirstBoardWorld CreateTravelingWorld(ScenarioInstance scenario)
    {
        FirstBoardWorld world = scenario.CreateInitialWorld();
        var reducer = new FirstBoardReducer(scenario.Graph);
        var planner = new SpatialPlanner(scenario.Graph);
        var instant = new LogicalInstant(ModelTime.Zero, 0);
        foreach ((string actorId, long speed) in new[] { (BoardIds.Alice, 6L), (BoardIds.Bob, 1L) })
        {
            var plan = Assert.IsType<SpatialPlanAccepted>(planner.TryStartTraversal(world.Spatial,
                new EntityId(actorId), new PassageId(BoardIds.TavernMarketRoad), speed, ModelTime.Zero));
            foreach (GraphSpatialFact fact in plan.Facts)
            {
                world = reducer.Apply(world, instant, new SpatialBoardFact(fact));
            }
        }
        reducer.Validate(world);
        return world;
    }

    private static TraversingLocation Traversal(FirstBoardWorld world, string actorId) =>
        Assert.IsType<TraversingLocation>(world.Spatial.Entities.Single(entity =>
            entity.Id == new EntityId(actorId)).Location);

    private static Dictionary<string, IPlayerDriver> Drivers(IPlayerDriver driver) =>
        new(StringComparer.Ordinal) { [BoardIds.Alice] = driver };

    private sealed class ResponseDriver(PassageEncounterResolution response) : IPlayerDriver
    {
        public int Calls { get; private set; }

        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(ContactDue.Ticks, request.ModelTimeMs);
            Assert.Equal(BoardIds.Alice, request.ActorId);
            Assert.Equal([ActionKinds.ContinueTravel, ActionKinds.ReverseTravel],
                request.AvailableActions.Select(action => action.ActionKind));
            return ValueTask.FromResult(new PlayerDecision(request.DecisionId, new Intent(
                response == PassageEncounterResolution.Continued
                    ? ActionKinds.ContinueTravel : ActionKinds.ReverseTravel)));
        }
    }

    private sealed class RecoveryOnlyKernel : IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
    {
        public RecoveryOnlyKernel(FirstBoardOccurrenceHistory history)
        {
            var reducer = new FirstBoardReducer(history.Scenario.Graph);
            Kernel = new(history, history.Rules, [this], (world, instant, fact) =>
            {
                FoldCalls++;
                return reducer.Apply(world, instant, fact);
            }, reducer.Validate);
        }

        public SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> Kernel { get; }
        public int FoldCalls { get; private set; }
        public int RuleCalls { get; private set; }

        public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(FirstBoardWorld world, SimulationRules rules)
        {
            RuleCalls++;
            throw new InvalidOperationException("Pending recovery must not forecast.");
        }

        public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(FirstBoardWorld world,
            OccurrenceCandidate<BoardCandidate> winner, CancellationToken cancellationToken)
        {
            RuleCalls++;
            throw new InvalidOperationException("Pending recovery must not plan or invoke a Player.");
        }
    }
}

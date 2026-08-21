using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Tests;

public sealed class FirstBoardTests
{
    [Fact]
    public void CreateKernel_MismatchedWorldSeedIsRejected()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 41);
        FirstBoardWorld mismatchedWorld = ScenarioInstance.CreateDefault(worldSeed: 42)
            .CreateInitialWorld();
        var journal = new InMemoryJournal<BoardEventPayload>(FirstBoardScenario.LineageId);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
                instance,
                journal,
                mismatchedWorld));

        Assert.Equal("world", exception.ParamName);
        Assert.Empty(journal.Batches);
    }

    [Fact]
    public void CreateKernel_EmptyJournalUsesCommittedWorldNowAsGenesis()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 43);
        FirstBoardWorld world = instance.CreateInitialWorld() with
        {
            Now = new ModelTime(500),
        };
        var journal = new InMemoryJournal<BoardEventPayload>(FirstBoardScenario.LineageId);

        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
                instance,
                journal,
                world);

        Assert.Equal(world.Now, kernel.CurrentModelTime);
        Assert.Equal(world.Now, kernel.World.Now);
    }

    [Fact]
    public void CreateKernel_DefaultVersionUsesJournalLineage()
    {
        const long lineageId = 77_001;
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 44);
        var journal = new InMemoryJournal<BoardEventPayload>(lineageId);

        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
                instance,
                journal,
                instance.CreateInitialWorld());

        Assert.Equal(new WorldVersion(lineageId, 0), kernel.Version);
    }

    [Fact]
    public async Task SameTickActors_OnlyWinnerIsCalled_ThenLoserSeesCommittedWorld()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 42);
        FirstBoardWorld initial = PutBothActorsInTavern(instance.CreateInitialWorld());
        var alice = TravelDriver();
        var bob = TravelDriver();
        var journal = new InMemoryJournal<BoardEventPayload>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            FirstBoardScenario.CreateKernel(Drivers(alice, bob), instance, journal, initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        Assert.Equal(1, alice.Requests.Count + bob.Requests.Count);
        Assert.Single(journal.Batches);

        CapturingDriver first = alice.Requests.Count == 1 ? alice : bob;
        CapturingDriver loser = ReferenceEquals(first, alice) ? bob : alice;
        string winnerActorId = first.Requests[0].ActorId;

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        DecisionRequest loserRequest = Assert.Single(loser.Requests);
        Assert.DoesNotContain(winnerActorId, loserRequest.Observation.VisibleActorIds);
        Assert.Equal(2, journal.Batches.Count);
    }

    [Fact]
    public async Task WrongDecisionCorrelation_FailsBeforeCommit()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 7);
        var malformed = new CapturingDriver(request =>
            new PlayerDecision(new DecisionId("wrong-decision"), new Intent(ActionKinds.Wait)));
        var journal = new InMemoryJournal<BoardEventPayload>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(malformed, malformed), instance, journal, instance.CreateInitialWorld());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(ModelTime.Zero));

        Assert.Empty(journal.Batches);
        Assert.Equal(0, kernel.Version.TransitionCount);
        Assert.All(kernel.World.Actors, actor => Assert.Equal(0, actor.DecisionSequence));
    }

    [Fact]
    public async Task IntentOutsideAdvertisedAffordance_FailsBeforeCommit()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 8);
        var malformed = new CapturingDriver(request =>
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Travel, DestinationId: "missing-place")));
        var journal = new InMemoryJournal<BoardEventPayload>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(malformed, malformed), instance, journal, instance.CreateInitialWorld());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(ModelTime.Zero));

        Assert.Empty(journal.Batches);
    }

    [Fact]
    public async Task LegalActionDefeatedByHiddenWorldFact_CommitsFailure()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 9);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        initial = initial with
        {
            CellarSealed = true,
            Actors = Array.AsReadOnly(initial.Actors.Select(actor => actor.Key switch
            {
                BoardIds.Alice => actor with { PlaceId = BoardIds.Market },
                BoardIds.Bob => actor with
                {
                    Activity = new BoardActivity(
                        BoardActivityKind.Wait,
                        new ModelTime(BoardTiming.DefaultWaitTicks)),
                },
                _ => actor,
            }).ToArray()),
        };
        var alice = new CapturingDriver(request =>
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Travel, DestinationId: BoardIds.Cellar)));
        var journal = new InMemoryJournal<BoardEventPayload>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice, new NullPlayerDriver()), instance, journal, initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        JournalBatch<BoardEventPayload> batch = Assert.Single(journal.Batches);
        ActionRejectedEvent rejected = Assert.IsType<ActionRejectedEvent>(Assert.Single(batch.Facts));
        Assert.Equal("cellar is sealed", rejected.Reason);
        Assert.Equal(1, kernel.World.Actor(BoardIds.Alice).DecisionSequence);
    }

    [Fact]
    public async Task Replay_FoldsCompleteBatchesWithoutCallingPlayers()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 10);
        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
            instance,
            new ModelTime(120_000));

        FirstBoardWorld replayed = capture.InitialWorld;
        var reducer = new FirstBoardReducer();
        foreach (JournalBatch<BoardEventPayload> batch in capture.Journal.Batches)
        {
            foreach (BoardEventPayload fact in batch.Facts)
            {
                replayed = reducer.Apply(replayed, batch.Instant, fact);
            }
        }

        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(capture.Result.World),
            FirstBoardScenario.WorldSnapshot(replayed));
        Assert.Equal(capture.Journal.Batches.Count, capture.Result.Version.TransitionCount);
    }

    [Fact]
    public void BuildRequest_ContainsOnlyActorVisibleProjectionAndAffordances()
    {
        FirstBoardWorld world = ScenarioInstance.CreateDefault(11).CreateInitialWorld();
        BoardActor alice = world.Actor(BoardIds.Alice);

        DecisionRequest request = FirstBoardScenario.BuildRequest(world, alice, ModelTime.Zero);

        Assert.Equal("decision.alice.1", request.DecisionId.Value);
        Assert.Equal(alice.Key, request.ActorId);
        Assert.Equal(ModelTime.Zero.Ticks, request.ModelTimeMs);
        Assert.Contains(request.AvailableActions, action => action.ActionKind == ActionKinds.Wait);
        Assert.DoesNotContain(BoardIds.BrassKey, request.Observation.VisibleObjectIds);
    }

    private static FirstBoardWorld PutBothActorsInTavern(FirstBoardWorld world) =>
        world with
        {
            Actors = Array.AsReadOnly(world.Actors.Select(actor =>
                actor with { PlaceId = BoardIds.Tavern }).ToArray()),
        };

    private static CapturingDriver TravelDriver() =>
        new(request =>
        {
            AvailableAction travel = request.AvailableActions.Single(action =>
                action.ActionKind == ActionKinds.Travel);
            return new PlayerDecision(
                request.DecisionId,
                new Intent(
                    ActionKinds.Travel,
                    DestinationId: travel.CandidateDestinationIds![0]));
        });

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(
        IPlayerDriver alice,
        IPlayerDriver bob) =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = alice,
            [BoardIds.Bob] = bob,
        };

    private sealed class CapturingDriver : IPlayerDriver
    {
        private readonly Func<DecisionRequest, PlayerDecision> _decide;
        private readonly List<DecisionRequest> _requests = [];

        public CapturingDriver(Func<DecisionRequest, PlayerDecision> decide)
        {
            _decide = decide;
        }

        public IReadOnlyList<DecisionRequest> Requests => _requests.AsReadOnly();

        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);
            return ValueTask.FromResult(_decide(request));
        }
    }
}

using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class TravelGoalAtomicityAndReplayTests
{
    [Fact]
    public async Task InitialTravelTo_AppendFailurePublishesNoGoalTicketOrTraversalPrefix()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 211);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob);
        var journal = new ThrowBeforePublishHistory(initial);
        var alice = new OneIntentPlayerDriver(ActionKinds.TravelTo, BoardIds.Cellar);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice),
                instance,
                journal);
        WorldVersion initialVersion = kernel.Version;
        string initialSnapshot = FirstBoardScenario.WorldSnapshot(initial);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.StepAsync(ModelTime.Zero));

        Assert.IsType<TestJournalFailure>(failure.InnerException);
        Assert.Empty(journal.CompletedEvents);
        Assert.Equal(initialVersion, kernel.Version);
        Assert.Equal(initialSnapshot, FirstBoardScenario.WorldSnapshot(kernel.World));
        Assert.Null(kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.Contains(kernel.World.Objects, item => item.Key == BoardIds.SilverCoinOne);
        Assert.True(kernel.World.IsAtPlace(BoardIds.Alice, new PlaceId(BoardIds.Tavern)));
        Assert.Equal(1, alice.CallCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InitialTravelTo_AnyFactFoldFailureInstallsNoScratchPrefix(int failAtFact)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 214);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob);
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        var reducer = new FirstBoardReducer(instance.Graph);
        var alice = new OneIntentPlayerDriver(ActionKinds.TravelTo, BoardIds.Cellar);
        int foldCount = 0;
        FirstBoardWorld FailingFold(
            FirstBoardWorld world,
            LogicalInstant instant,
            FirstBoardFact fact)
        {
            foldCount++;
            if (foldCount == failAtFact)
            {
                throw new TestFoldFailure();
            }

            return reducer.Apply(world, instant, fact);
        }

        var getter = FullMapPlayerSpatialKnowledgeGetter<FirstBoardWorld>.Instance;
        var kernel = new SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact>(
            journal,
            new SimulationRules(instance.WorldSeed, 10_000),
            [
                new CellarDeadlineRule(
                    instance.Graph,
                    instance.Definition.CellarDeadlineMs),
                new ActivityCompletionRule(),
                new SpatialHostOccurrenceRule(instance.Graph),
                new TravelGoalRule(instance, getter),
                new DecisionPointRule(Drivers(alice), instance, getter),
            ],
            FailingFold,
            reducer.Validate);
        WorldVersion initialVersion = kernel.Version;
        string initialSnapshot = FirstBoardScenario.WorldSnapshot(initial);

        await Assert.ThrowsAsync<TestFoldFailure>(async () =>
            await kernel.StepAsync(ModelTime.Zero));

        Assert.Equal(failAtFact, foldCount);
        Assert.Empty(journal.CompletedEvents);
        Assert.Equal(initialVersion, kernel.Version);
        Assert.Equal(initialSnapshot, FirstBoardScenario.WorldSnapshot(kernel.World));
        Assert.Null(kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.Contains(kernel.World.Objects, item => item.Key == BoardIds.SilverCoinOne);
        Assert.True(kernel.World.IsAtPlace(BoardIds.Alice, new PlaceId(BoardIds.Tavern)));
    }

    [Fact]
    public async Task LocallyClosedFirstLeg_UnadvertisedTravelToFailsBeforeCommit()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 212);
        FirstBoardWorld initial = MoveInitialEntity(
            instance,
            instance.CreateInitialWorld(),
            BoardIds.Alice,
            BoardIds.CellarGate);
        initial = CloseCellarGate(instance, initial);
        initial = WithWaitingActor(initial, BoardIds.Bob);
        var alice = new OneIntentPlayerDriver(ActionKinds.TravelTo, BoardIds.Cellar);
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice),
                instance,
                journal);
        WorldVersion initialVersion = kernel.Version;
        string initialSnapshot = FirstBoardScenario.WorldSnapshot(initial);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(ModelTime.Zero));

        Assert.Empty(journal.CompletedEvents);
        Assert.Equal(initialVersion, kernel.Version);
        Assert.Equal(initialSnapshot, FirstBoardScenario.WorldSnapshot(kernel.World));
        Assert.Null(kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.True(kernel.World.IsAtPlace(BoardIds.Alice, new PlaceId(BoardIds.CellarGate)));
        Assert.DoesNotContain(
            BoardIds.Cellar,
            alice.LastRequest!.AvailableActions
                .Where(action => action.ActionKind == ActionKinds.TravelTo)
                .SelectMany(action => action.CandidateDestinationIds ?? []));
    }

    [Fact]
    public async Task ExactTravel_ArrivalRestoresAConsciousDecisionPoint()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 215);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob);
        var alice = new RecordingPlayerDriver(
            request => new PlayerDecision(
                request.DecisionId,
                new Intent(
                    ActionKinds.Travel,
                    ExitId: $"exit:{BoardIds.TavernMarketRoad}")),
            request => new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Wait, DurationMs: 1)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
                {
                    [BoardIds.Alice] = alice,
                    [BoardIds.Bob] = new ThrowingPlayerDriver(),
                },
                instance,
                journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        TraversingLocation travel = Assert.IsType<TraversingLocation>(
            kernel.World.Spatial.Entities.Single(entity =>
                entity.Id == new EntityId(BoardIds.Alice)).Location);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(travel.ArrivalDue));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(travel.ArrivalDue));

        Assert.Equal(2, alice.Requests.Count);
        Assert.Null(kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.NotNull(kernel.World.Actor(BoardIds.Alice).Activity);
        Assert.True(kernel.World.IsAtPlace(BoardIds.Alice, new PlaceId(BoardIds.Market)));
    }

    [Fact]
    public async Task ActiveGoalForkCreationDoesNotCallGetter_ButForkContinuationDoes()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 213);
        FirstBoardWorld genesis = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob);
        var source = FirstBoardScenario.CreateMemoryHistory(genesis);
        var alice = new OneIntentPlayerDriver(ActionKinds.TravelTo, BoardIds.Cellar);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> sourceKernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice),
                instance,
                source);
        Assert.Equal(StepStatus.Committed, await sourceKernel.StepAsync(ModelTime.Zero));
        TraversingLocation firstLeg = Assert.IsType<TraversingLocation>(
            sourceKernel.World.Spatial.Entities.Single(entity =>
                entity.Id == new EntityId(BoardIds.Alice)).Location);
        Assert.Equal(StepStatus.Committed, await sourceKernel.StepAsync(firstLeg.ArrivalDue));
        Assert.Equal(2, source.CompletedEvents.Count);
        Assert.IsType<TraversalStartedFact>(
            Assert.IsType<SpatialBoardFact>(source.CompletedEvents[0].Facts[^1]).Value);
        Assert.IsType<TraversalArrivedFact>(
            Assert.IsType<SpatialBoardFact>(Assert.Single(source.CompletedEvents[1].Facts)).Value);
        var reducer = new FirstBoardReducer(instance.Graph);
        var getter = new CountingFullMapGetter();

        var fork = new InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact>(
            source.State,
            new KernelCursor(
                new WorldVersion(FirstBoardScenario.LineageId + 1, source.Cursor.Version.TransitionCount),
                source.Cursor.GenesisTime,
                source.Cursor.LastInstant,
                source.Cursor.LastCauseKey));

        Assert.Equal(0, getter.CallCount);
        Assert.Equal(
            new PlaceId(BoardIds.Cellar),
            fork.State.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.True(fork.State.IsAtPlace(
            BoardIds.Alice,
            new PlaceId(BoardIds.Market)));

        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new ThrowingPlayerDriver()),
                instance,
                fork,
                getter);
        Assert.Equal(
            StepStatus.Committed,
            await kernel.StepAsync(fork.Cursor.CurrentModelTime));

        Assert.Equal(1, getter.CallCount);
        Assert.Equal(
            new PlaceId(BoardIds.Cellar),
            kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        TraversingLocation nextLeg = Assert.IsType<TraversingLocation>(
            kernel.World.Spatial.Entities.Single(entity =>
                entity.Id == new EntityId(BoardIds.Alice)).Location);
        Assert.Equal(new PassageId(BoardIds.MarketCellarApproach), nextLeg.PassageId);
    }

    [Fact]
    public async Task TravelGoalRule_RejectsTamperedKeyDueAndStateFieldsBeforeGetter()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 216);
        FirstBoardWorld prefix = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob);
        prefix = ReplaceActor(
            prefix,
            prefix.Actor(BoardIds.Alice).WithTravelGoal(new PlaceId(BoardIds.Cellar)));
        var getter = new CountingFullMapGetter();
        var rule = new TravelGoalRule(instance, getter);
        OccurrenceCandidate<BoardCandidate> valid = Assert.Single(
            rule.Forecast(prefix, new SimulationRules(instance.WorldSeed, 10_000)));
        TravelGoalCandidate data = Assert.IsType<TravelGoalCandidate>(valid.Data);
        BoardActor bob = prefix.Actor(BoardIds.Bob);
        OccurrenceCandidate<BoardCandidate>[] tampered =
        [
            new(CandidateKey.FromUtf8("tampered"), valid.Due, data),
            new(valid.Key, new CandidateDue(new ModelTime(1)), data),
            new(valid.Key, valid.Due, data with { ActorId = bob.Id }),
            new(valid.Key, valid.Due, data with { ActorGeneration = data.ActorGeneration + 1 }),
            new(valid.Key, valid.Due, data with
            {
                SpatialMovementGeneration = data.SpatialMovementGeneration + 1,
            }),
            new(valid.Key, valid.Due, data with
            {
                CurrentPlaceId = new PlaceId(BoardIds.Market),
            }),
            new(valid.Key, valid.Due, data with
            {
                DestinationPlaceId = new PlaceId(BoardIds.Market),
            }),
        ];

        foreach (OccurrenceCandidate<BoardCandidate> candidate in tampered)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await rule.PlanSelectedAsync(prefix, candidate, CancellationToken.None));
        }

        Assert.Equal(0, getter.CallCount);
    }

    private static FirstBoardWorld ReplaceActor(
        FirstBoardWorld world,
        BoardActor replacement) =>
        world.With(game: world.Game.With(actors: Array.AsReadOnly(world.Actors
                    .Select(actor => actor.Id == replacement.Id ? replacement : actor)
                    .ToArray())));

    private static FirstBoardWorld WithWaitingActor(
        FirstBoardWorld world,
        string actorId) =>
        world.With(game: world.Game.With(actors: Array.AsReadOnly(world.Actors
                    .Select(actor => actor.Key == actorId
                        ? actor.WithActivity(new BoardWaitActivity(new ModelTime(10_000_000)))
                        : actor)
                    .ToArray())));

    private static FirstBoardWorld MoveInitialEntity(
        ScenarioInstance instance,
        FirstBoardWorld world,
        string entityId,
        string placeId)
    {
        EntityPlacement[] placements =
        [
            .. world.Spatial.Entities.Select(entity => new EntityPlacement(
                entity.Id,
                entity.Id == new EntityId(entityId)
                    ? new PlaceId(placeId)
                    : Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId)),
        ];
        return world.With(spatial: GraphSpatialState.Create(instance.Graph, placements));
    }

    private static FirstBoardWorld CloseCellarGate(
        ScenarioInstance instance,
        FirstBoardWorld world)
    {
        SpatialPlanAccepted accepted = Assert.IsType<SpatialPlanAccepted>(
            new SpatialPlanner(instance.Graph).TrySetPassageEntryAccess(
                world.Spatial,
                new PassageId(BoardIds.CellarGatePassage),
                new PassageEntryPatch(enterableFromA: false, enterableFromB: null)));
        var reducer = new FirstBoardReducer(instance.Graph);
        var instant = new LogicalInstant(world.Now, 0);
        FirstBoardWorld closed = reducer.Apply(
            world,
            instant,
            new GameBoardFact(new CellarSealedEvent()));
        foreach (GraphSpatialFact fact in accepted.Facts)
        {
            closed = reducer.Apply(closed, instant, new SpatialBoardFact(fact));
        }

        reducer.Validate(closed);
        return closed;
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(IPlayerDriver alice) =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = alice,
            [BoardIds.Bob] = new ThrowingPlayerDriver(),
        };

    private sealed class OneIntentPlayerDriver : IPlayerDriver
    {
        private readonly ActionKind _actionKind;
        private readonly string _destinationId;

        public OneIntentPlayerDriver(ActionKind actionKind, string destinationId)
        {
            _actionKind = actionKind;
            _destinationId = destinationId;
        }

        public int CallCount { get; private set; }

        public DecisionRequest? LastRequest { get; private set; }

        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(new PlayerDecision(
                request.DecisionId,
                new Intent(_actionKind, DestinationId: _destinationId)));
        }
    }

    private sealed class ThrowingPlayerDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TravelGoal continuation must not call a Player.");
    }

    private sealed class CountingFullMapGetter :
        IPlayerSpatialKnowledgeGetter<FirstBoardWorld>
    {
        public int CallCount { get; private set; }

        public PlayerSpatialKnowledgeSnapshot GetKnownGraph(
            FirstBoardWorld committedWorld,
            string subjectId,
            GraphDefinition objectiveGraph)
        {
            _ = committedWorld;
            _ = subjectId;
            CallCount++;
            return PlayerSpatialKnowledgeSnapshot.FullMap(objectiveGraph);
        }
    }

    private sealed class ThrowBeforePublishHistory(FirstBoardWorld state) :
        IOccurrenceHistory<FirstBoardWorld, FirstBoardFact>
    {
        public FirstBoardWorld State => state;
        public KernelCursor Cursor { get; } = FirstBoardScenario.CreateMemoryHistory(state).Cursor;
        public OccurrenceEvent<FirstBoardFact>? PendingEvent => null;
        public IReadOnlyList<OccurrenceEvent<FirstBoardFact>> CompletedEvents { get; } =
            Array.Empty<OccurrenceEvent<FirstBoardFact>>();
        public void CommitEvent(OccurrenceEvent<FirstBoardFact> occurrence) =>
            throw new TestJournalFailure();
        public void CommitState(FirstBoardWorld nextState, KernelCursor nextCursor) =>
            throw new TestJournalFailure();
    }

    private sealed class TestJournalFailure : Exception;

    private sealed class TestFoldFailure : Exception;
}

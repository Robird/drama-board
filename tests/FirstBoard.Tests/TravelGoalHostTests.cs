using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class TravelGoalHostTests
{
    private static readonly ModelTime FarFuture = new(10_000_000);

    [Fact]
    public async Task AliceTravelToCellar_AutomaticallyCompletesAndAdvancesDecisionOnce()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 201);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            FarFuture);
        long initialDecisionSequence = initial.Actor(BoardIds.Alice).DecisionSequence;
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.TravelTo, DestinationId: BoardIds.Cellar)));
        IReadOnlyDictionary<string, IPlayerDriver> drivers =
            Drivers(alice, new NullPlayerDriver());
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(drivers, instance, journal, initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Collection(
            journal.Batches[0].Facts,
            fact =>
            {
                ActorTravelGoalSetEvent set = Assert.IsType<ActorTravelGoalSetEvent>(
                    Assert.IsType<GameBoardFact>(fact).Value);
                Assert.Equal(new PlaceId(BoardIds.Cellar), set.DestinationPlaceId);
            },
            fact =>
            {
                TicketConsumedEvent consumed = Assert.IsType<TicketConsumedEvent>(
                    Assert.IsType<GameBoardFact>(fact).Value);
                Assert.Equal(BoardIds.SilverCoinOne, consumed.TicketObjectId);
            },
            fact => AssertStartedPassage(fact, BoardIds.TavernMarketFerry));
        Assert.Equal(
            new PlaceId(BoardIds.Cellar),
            kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.Equal(initialDecisionSequence + 1, kernel.World.Actor(BoardIds.Alice).DecisionSequence);

        await ArriveAsync(kernel, BoardIds.Alice, BoardIds.Market);
        AssertActiveGoalPrefix(instance, kernel.World, drivers, BoardIds.Alice, BoardIds.Cellar);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(kernel.World.Now));
        Assert.Equal(
            new PassageId(BoardIds.MarketCellarApproach),
            AssertTraversing(kernel.World, BoardIds.Alice).PassageId);

        await ArriveAsync(kernel, BoardIds.Alice, BoardIds.CellarGate);
        AssertActiveGoalPrefix(instance, kernel.World, drivers, BoardIds.Alice, BoardIds.Cellar);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(kernel.World.Now));
        Assert.Equal(
            new PassageId(BoardIds.CellarGatePassage),
            AssertTraversing(kernel.World, BoardIds.Alice).PassageId);

        await ArriveAsync(kernel, BoardIds.Alice, BoardIds.Cellar);
        AssertActiveGoalPrefix(instance, kernel.World, drivers, BoardIds.Alice, BoardIds.Cellar);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(kernel.World.Now));

        BoardActor completed = kernel.World.Actor(BoardIds.Alice);
        Assert.Null(completed.TravelGoalPlaceId);
        Assert.Equal(initialDecisionSequence + 1, completed.DecisionSequence);
        AssertAtPlace(kernel.World, BoardIds.Alice, BoardIds.Cellar);
        ActorTravelGoalResolvedEvent resolved = Assert.IsType<ActorTravelGoalResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(journal.Batches[^1].Facts)).Value);
        Assert.Equal(TravelGoalResolution.Completed, resolved.Resolution);
        Assert.Equal(7, journal.Batches.Count);
        Assert.DoesNotContain(
            journal.Batches.SelectMany(batch => batch.Facts),
            fact => fact is GameBoardFact { Value: ActorTravelStartedEvent });
        Assert.Single(alice.Requests);
        new FirstBoardReducer(instance.Graph).Validate(kernel.World);
    }

    [Fact]
    public async Task BobTravelToTavern_WithoutTicketChoosesCart()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 202);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Alice,
            FarFuture);
        var bob = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.TravelTo, DestinationId: BoardIds.Tavern)));
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), bob),
                instance,
                journal,
                initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Collection(
            Assert.Single(journal.Batches).Facts,
            fact => Assert.IsType<ActorTravelGoalSetEvent>(
                Assert.IsType<GameBoardFact>(fact).Value),
            fact => AssertStartedPassage(fact, BoardIds.MarketTavernCart));
        Assert.Equal(
            new PassageId(BoardIds.MarketTavernCart),
            AssertTraversing(kernel.World, BoardIds.Bob).PassageId);
        Assert.DoesNotContain(
            journal.Batches.SelectMany(batch => batch.Facts),
            fact => fact is GameBoardFact { Value: TicketConsumedEvent });
        Assert.Equal(2, kernel.World.Objects.Count(item =>
            item.Key is BoardIds.SilverCoinOne or BoardIds.SilverCoinTwo));
        Assert.Single(bob.Requests);
    }

    [Fact]
    public async Task RemoteClosedGate_IsAdvertisedUntilArrivalThenBlocksLocally()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 203);
        FirstBoardWorld initial = CloseCellarGate(instance, instance.CreateInitialWorld());
        initial = WithWaitingActor(initial, BoardIds.Bob, FarFuture);
        var alice = new RecordingPlayerDriver(request =>
        {
            Assert.Contains(
                BoardIds.Cellar,
                TravelToAction(request).CandidateDestinationIds!);
            return new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.TravelTo, DestinationId: BoardIds.Cellar));
        });
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice, new NullPlayerDriver()),
                instance,
                journal,
                initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        Assert.Equal(
            new PassageId(BoardIds.TavernMarketFerry),
            AssertTraversing(kernel.World, BoardIds.Alice).PassageId);

        await ArriveAsync(kernel, BoardIds.Alice, BoardIds.Market);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(kernel.World.Now));
        Assert.Equal(
            new PassageId(BoardIds.MarketCellarApproach),
            AssertTraversing(kernel.World, BoardIds.Alice).PassageId);

        await ArriveAsync(kernel, BoardIds.Alice, BoardIds.CellarGate);
        Assert.Equal(
            new PlaceId(BoardIds.Cellar),
            kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(kernel.World.Now));

        AssertAtPlace(kernel.World, BoardIds.Alice, BoardIds.CellarGate);
        Assert.Null(kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        ActorTravelGoalResolvedEvent resolved = Assert.IsType<ActorTravelGoalResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(journal.Batches[^1].Facts)).Value);
        Assert.Equal(TravelGoalResolution.Blocked, resolved.Resolution);
        Assert.DoesNotContain(
            journal.Batches.SelectMany(batch => batch.Facts),
            fact => fact is SpatialBoardFact
            {
                Value: TraversalStartedFact
                {
                    PassageId: var passageId,
                },
            } && passageId == new PassageId(BoardIds.CellarGatePassage));
    }

    [Fact]
    public async Task LocalClosedGate_IsNotAdvertisedAndActiveGoalBlocksWithoutLooping()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 204);
        FirstBoardWorld atGate = MoveInitialEntity(
            instance,
            instance.CreateInitialWorld(),
            BoardIds.Alice,
            BoardIds.CellarGate);
        atGate = CloseCellarGate(instance, atGate);
        atGate = WithWaitingActor(atGate, BoardIds.Bob, FarFuture);
        BoardActor alice = atGate.Actor(BoardIds.Alice);
        PlayerSpatialKnowledgeSnapshot full = PlayerSpatialKnowledgeSnapshot.FullMap(instance.Graph);
        DecisionRequest request = FirstBoardScenario.BuildRequest(
            instance,
            atGate,
            alice,
            ModelTime.Zero,
            full);
        AvailableAction? travelTo = request.AvailableActions.SingleOrDefault(
            action => action.ActionKind == ActionKinds.TravelTo);
        Assert.DoesNotContain(BoardIds.Cellar, travelTo?.CandidateDestinationIds ?? []);

        FirstBoardWorld prefix = WithTravelGoal(atGate, BoardIds.Alice, BoardIds.Cellar);
        new FirstBoardReducer(instance.Graph).Validate(prefix);
        var getter = new CountingKnowledgeGetter(full);
        var rule = new TravelGoalRule(instance, getter);
        OccurrenceCandidate<BoardCandidate> candidate = Assert.Single(
            rule.Forecast(prefix, new SimulationRules(instance.WorldSeed, 10_000)));
        Assert.Equal(0, getter.CallCount);
        Assert.IsType<TravelGoalCandidate>(candidate.Data);

        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
                instance,
                journal,
                prefix,
                spatialKnowledgeGetter: getter);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Equal(1, getter.CallCount);
        AssertAtPlace(kernel.World, BoardIds.Alice, BoardIds.CellarGate);
        Assert.Null(kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        ActorTravelGoalResolvedEvent resolved = Assert.IsType<ActorTravelGoalResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(Assert.Single(journal.Batches).Facts)).Value);
        Assert.Equal(TravelGoalResolution.Blocked, resolved.Resolution);
    }

    [Fact]
    public async Task KnownSubgraphHidesShortcut_AndOneSnapshotServesRequestAndSelection()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 205);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            FarFuture);
        GraphDefinition knownGraph = GraphDefinition.Create(
            [new PlaceId(BoardIds.Tavern), new PlaceId(BoardIds.Market)],
            [instance.Graph.GetPassage(new PassageId(BoardIds.TavernMarketRoad))]);
        PlayerSpatialKnowledgeSnapshot known =
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(instance.Graph, knownGraph);
        var getter = new CountingKnowledgeGetter(known);
        var alice = new RecordingPlayerDriver(request =>
        {
            Assert.Equal(
                [BoardIds.Market],
                TravelToAction(request).CandidateDestinationIds);
            return new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.TravelTo, DestinationId: BoardIds.Market));
        });
        IReadOnlyDictionary<string, IPlayerDriver> drivers =
            Drivers(alice, new NullPlayerDriver());
        var decisionRule = new DecisionPointRule(drivers, instance, getter);

        Assert.Single(decisionRule.Forecast(
            initial,
            new SimulationRules(instance.WorldSeed, 10_000)));
        Assert.Equal(0, getter.CallCount);

        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                drivers,
                instance,
                journal,
                initial,
                spatialKnowledgeGetter: getter);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Equal(1, getter.CallCount);
        Assert.Equal([BoardIds.Alice], getter.SubjectIds);
        Assert.Equal(
            new PassageId(BoardIds.TavernMarketRoad),
            AssertTraversing(kernel.World, BoardIds.Alice).PassageId);
        Assert.DoesNotContain(
            journal.Batches[0].Facts,
            fact => fact is GameBoardFact { Value: TicketConsumedEvent });
        Assert.Collection(
            journal.Batches[0].Facts,
            fact => Assert.IsType<ActorTravelGoalSetEvent>(
                Assert.IsType<GameBoardFact>(fact).Value),
            fact => AssertStartedPassage(fact, BoardIds.TavernMarketRoad));
    }

    [Fact]
    public async Task EqualCostParallelRoutes_UseNavigatorOrdinalTieBreak()
    {
        ScenarioDefinition definition = ScenarioDefinition.Default with
        {
            Id = "firstboard.travel-to-equal-cost",
            Revision = ScenarioDefinition.Default.Revision + 1,
            Passages = Array.AsReadOnly(
                ScenarioDefinition.Default.Passages
                    .Select(passage => passage.Id is BoardIds.TavernMarketRoad or
                            BoardIds.TavernMarketFerry
                        ? passage with { Length = 180_000 }
                        : passage)
                    .ToArray()),
        };
        var instance = new ScenarioInstance(definition, worldSeed: 206);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            FarFuture);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.TravelTo, DestinationId: BoardIds.Market)));
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice, new NullPlayerDriver()),
                instance,
                journal,
                initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Equal(
            new PassageId(BoardIds.TavernMarketFerry),
            AssertTraversing(kernel.World, BoardIds.Alice).PassageId);
        Assert.IsType<TicketConsumedEvent>(
            Assert.IsType<GameBoardFact>(journal.Batches[0].Facts[1]).Value);
    }

    [Fact]
    public async Task SameTickGateCloseAndGoalContinuation_WorldSeedSelectsBothOrders()
    {
        (ulong goalFirstSeed, ulong closeFirstSeed) = FindGateGoalContestSeeds();
        Assert.NotEqual(goalFirstSeed, closeFirstSeed);

        ScenarioInstance goalFirstInstance = ScenarioInstance.CreateDefault(goalFirstSeed);
        FirstBoardWorld goalFirstWorld = GateGoalContestWorld(goalFirstInstance);
        var goalFirstJournal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> goalFirstKernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
                goalFirstInstance,
                goalFirstJournal,
                goalFirstWorld);

        ModelTime deadline = new(BoardTiming.DeadlineTicks);
        Assert.Equal(StepStatus.Committed, await goalFirstKernel.StepAsync(deadline));
        AssertStartedPassage(
            Assert.Single(goalFirstJournal.Batches[0].Facts),
            BoardIds.CellarGatePassage);
        TraversingLocation committed = AssertTraversing(goalFirstKernel.World, BoardIds.Alice);
        Assert.Equal(StepStatus.Committed, await goalFirstKernel.StepAsync(deadline));
        Assert.True(goalFirstKernel.World.CellarSealed);
        Assert.Equal(committed, AssertTraversing(goalFirstKernel.World, BoardIds.Alice));
        await ArriveAsync(goalFirstKernel, BoardIds.Alice, BoardIds.Cellar);
        Assert.Equal(StepStatus.Committed, await goalFirstKernel.StepAsync(goalFirstKernel.World.Now));
        Assert.Null(goalFirstKernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);

        ScenarioInstance closeFirstInstance = ScenarioInstance.CreateDefault(closeFirstSeed);
        FirstBoardWorld closeFirstWorld = GateGoalContestWorld(closeFirstInstance);
        var closeFirstJournal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> closeFirstKernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
                closeFirstInstance,
                closeFirstJournal,
                closeFirstWorld);

        Assert.Equal(StepStatus.Committed, await closeFirstKernel.StepAsync(deadline));
        Assert.IsType<CellarSealedEvent>(
            Assert.IsType<GameBoardFact>(closeFirstJournal.Batches[0].Facts[0]).Value);
        Assert.Equal(StepStatus.Committed, await closeFirstKernel.StepAsync(deadline));
        AssertAtPlace(closeFirstKernel.World, BoardIds.Alice, BoardIds.CellarGate);
        Assert.Null(closeFirstKernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        ActorTravelGoalResolvedEvent blocked = Assert.IsType<ActorTravelGoalResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(closeFirstJournal.Batches[1].Facts)).Value);
        Assert.Equal(TravelGoalResolution.Blocked, blocked.Resolution);
    }

    private static async Task ArriveAsync(
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel,
        string actorId,
        string expectedPlaceId)
    {
        TraversingLocation traversal = AssertTraversing(kernel.World, actorId);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(traversal.ArrivalDue));
        AssertAtPlace(kernel.World, actorId, expectedPlaceId);
    }

    private static void AssertActiveGoalPrefix(
        ScenarioInstance instance,
        FirstBoardWorld world,
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        string actorId,
        string destinationId)
    {
        BoardActor actor = world.Actor(actorId);
        Assert.Equal(new PlaceId(destinationId), actor.TravelGoalPlaceId);
        Assert.False(world.IsReadyForDecision(actor));
        Assert.DoesNotContain(
            new DecisionPointRule(drivers, instance).Forecast(
                world,
                new SimulationRules(instance.WorldSeed, 10_000)),
            candidate => candidate.Data is DecisionPointCandidate decision &&
                decision.ActorId == actor.Id);
        new FirstBoardReducer(instance.Graph).Validate(world);
    }

    private static void AssertStartedPassage(FirstBoardFact fact, string expectedPassageId)
    {
        TraversalStartedFact started = Assert.IsType<TraversalStartedFact>(
            Assert.IsType<SpatialBoardFact>(fact).Value);
        Assert.Equal(new PassageId(expectedPassageId), started.PassageId);
    }

    private static AvailableAction TravelToAction(DecisionRequest request) =>
        request.AvailableActions.Single(action => action.ActionKind == ActionKinds.TravelTo);

    private static FirstBoardWorld WithWaitingActor(
        FirstBoardWorld world,
        string actorId,
        ModelTime due) =>
        world with
        {
            Game = world.Game with
            {
                Actors = Array.AsReadOnly(world.Actors
                    .Select(actor => actor.Key == actorId
                        ? actor with { Activity = new BoardWaitActivity(due) }
                        : actor)
                    .ToArray()),
            },
        };

    private static FirstBoardWorld WithTravelGoal(
        FirstBoardWorld world,
        string actorId,
        string destinationId) =>
        world with
        {
            Game = world.Game with
            {
                Actors = Array.AsReadOnly(world.Actors
                    .Select(actor => actor.Key == actorId
                        ? actor with
                        {
                            Activity = null,
                            TravelGoalPlaceId = new PlaceId(destinationId),
                        }
                        : actor)
                    .ToArray()),
            },
        };

    private static FirstBoardWorld CloseCellarGate(
        ScenarioInstance instance,
        FirstBoardWorld world)
    {
        var planner = new SpatialPlanner(instance.Graph);
        SpatialPlanAccepted accepted = Assert.IsType<SpatialPlanAccepted>(
            planner.TrySetPassageEntryAccess(
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
        return world with { Spatial = GraphSpatialState.Create(instance.Graph, placements) };
    }

    private static FirstBoardWorld GateGoalContestWorld(ScenarioInstance instance)
    {
        FirstBoardWorld world = MoveInitialEntity(
            instance,
            instance.CreateInitialWorld(),
            BoardIds.Alice,
            BoardIds.CellarGate);
        world = world with
        {
            Game = world.Game with { Now = new ModelTime(BoardTiming.DeadlineTicks) },
        };
        world = WithTravelGoal(world, BoardIds.Alice, BoardIds.Cellar);
        world = WithWaitingActor(
            world,
            BoardIds.Bob,
            new ModelTime(BoardTiming.DeadlineTicks + 1_000_000));
        new FirstBoardReducer(instance.Graph).Validate(world);
        return world;
    }

    private static (ulong GoalFirst, ulong CloseFirst) FindGateGoalContestSeeds()
    {
        ScenarioInstance template = ScenarioInstance.CreateDefault(worldSeed: 0);
        FirstBoardWorld world = GateGoalContestWorld(template);
        var rules = new SimulationRules(template.WorldSeed, 10_000);
        var getter = FullMapPlayerSpatialKnowledgeGetter<FirstBoardWorld>.Instance;
        OccurrenceCandidate<BoardCandidate>[] contenders =
        [
            .. new CellarDeadlineRule(template.Graph).Forecast(world, rules),
            .. new TravelGoalRule(template, getter).Forecast(world, rules),
        ];
        ulong? goalFirst = null;
        ulong? closeFirst = null;
        for (ulong seed = 0; seed < 10_000 &&
             (goalFirst is null || closeFirst is null); seed++)
        {
            BoardCandidate winner = OccurrenceScheduler.SelectWinner(contenders, seed).Data;
            if (winner is TravelGoalCandidate)
            {
                goalFirst ??= seed;
            }
            else if (winner is DeadlineCandidate)
            {
                closeFirst ??= seed;
            }
        }

        return (
            goalFirst ?? throw new InvalidOperationException("No goal-first seed was found."),
            closeFirst ?? throw new InvalidOperationException("No close-first seed was found."));
    }

    private static TraversingLocation AssertTraversing(FirstBoardWorld world, string entityId)
    {
        Assert.True(world.Spatial.TryGetEntity(new EntityId(entityId), out SpatialEntity? entity));
        return Assert.IsType<TraversingLocation>(entity!.Location);
    }

    private static void AssertAtPlace(FirstBoardWorld world, string entityId, string placeId)
    {
        Assert.True(world.Spatial.TryGetEntity(new EntityId(entityId), out SpatialEntity? entity));
        AtPlaceLocation location = Assert.IsType<AtPlaceLocation>(entity!.Location);
        Assert.Equal(new PlaceId(placeId), location.PlaceId);
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(
        IPlayerDriver alice,
        IPlayerDriver bob) =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = alice,
            [BoardIds.Bob] = bob,
        };

    private sealed class CountingKnowledgeGetter :
        IPlayerSpatialKnowledgeGetter<FirstBoardWorld>
    {
        private readonly PlayerSpatialKnowledgeSnapshot _snapshot;
        private readonly List<string> _subjectIds = [];

        public CountingKnowledgeGetter(PlayerSpatialKnowledgeSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int CallCount => _subjectIds.Count;

        public IReadOnlyList<string> SubjectIds => _subjectIds.AsReadOnly();

        public PlayerSpatialKnowledgeSnapshot GetKnownGraph(
            FirstBoardWorld committedWorld,
            string subjectId,
            GraphDefinition objectiveGraph)
        {
            Assert.NotNull(committedWorld);
            Assert.NotNull(objectiveGraph);
            _subjectIds.Add(subjectId);
            return _snapshot;
        }
    }
}

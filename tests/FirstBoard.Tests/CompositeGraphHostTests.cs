using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class CompositeGraphHostTests
{
    [Fact]
    public void Genesis_GraphOwnsActorLooseObjectAndChestLocations()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 101);
        FirstBoardWorld world = instance.CreateInitialWorld();

        AssertAtPlace(world, BoardIds.Alice, BoardIds.Tavern);
        AssertAtPlace(world, BoardIds.Bob, BoardIds.Market);
        AssertAtPlace(world, BoardIds.BrassKey, BoardIds.Market);
        AssertAtPlace(world, BoardIds.LockedChest, BoardIds.Cellar);
        AssertNoSpatialEntity(world, BoardIds.DuchessLetter);
        AssertNoSpatialEntity(world, BoardIds.SilverCoinOne);
        AssertNoSpatialEntity(world, BoardIds.SilverCoinTwo);

        Assert.Null(world.Object(BoardIds.BrassKey).OwnerActorId);
        Assert.Null(world.Object(BoardIds.DuchessLetter).OwnerActorId);
        Assert.Equal(world.Actor(BoardIds.Alice).Id, world.Object(BoardIds.SilverCoinOne).OwnerActorId);
        Assert.Null(typeof(BoardActor).GetProperty("PlaceId"));
        Assert.Null(typeof(BoardObject).GetProperty("PlaceId"));
        Assert.Null(typeof(ScenarioPlaceDefinition).GetProperty("AdjacentPlaceIds"));
        new FirstBoardReducer(instance.Graph).Validate(world);
    }

    [Fact]
    public void BuildRequest_ProjectsParallelExitsEtaAndCurrentDirectionalAvailability()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 102);
        FirstBoardWorld world = instance.CreateInitialWorld();

        DecisionRequest aliceRequest = FirstBoardScenario.BuildRequest(
            instance,
            world,
            world.Actor(BoardIds.Alice),
            ModelTime.Zero);
        ObservedExit road = Exit(aliceRequest, BoardIds.TavernMarketRoad);
        ObservedExit ferry = Exit(aliceRequest, BoardIds.TavernMarketFerry);
        ObservedExit cartFromTavern = Exit(aliceRequest, BoardIds.MarketTavernCart);

        Assert.Equal(BoardIds.Market, road.DestinationId);
        Assert.Equal(BoardIds.Market, ferry.DestinationId);
        Assert.NotEqual(road.ExitId, ferry.ExitId);
        Assert.Equal(300_000, road.ExpectedDurationMs);
        Assert.Equal(180_000, ferry.ExpectedDurationMs);
        Assert.True(road.IsAvailable);
        Assert.True(ferry.IsAvailable);
        Assert.False(cartFromTavern.IsAvailable);
        AssertAdvertisesExactlyAvailableExits(aliceRequest);

        DecisionRequest bobRequest = FirstBoardScenario.BuildRequest(
            instance,
            world,
            world.Actor(BoardIds.Bob),
            ModelTime.Zero);
        Assert.True(Exit(bobRequest, BoardIds.MarketTavernCart).IsAvailable);
        Assert.False(Exit(bobRequest, BoardIds.TavernMarketFerry).IsAvailable);
        AssertAdvertisesExactlyAvailableExits(bobRequest);
        Assert.DoesNotContain(
            ExitId(BoardIds.TavernMarketFerry),
            TravelAction(bobRequest).CandidateExitIds!);
    }

    [Fact]
    public async Task TakeFerry_CommitsTicketGameOutcomeAndTraversalInOneBatch()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 103);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            new ModelTime(1_000_000));
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.TavernMarketFerry))));
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice, new NullPlayerDriver()),
                instance,
                journal,
                initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        JournalBatch<FirstBoardFact> batch = Assert.Single(journal.Batches);
        Assert.Collection(
            batch.Facts,
            fact => Assert.IsType<TicketConsumedEvent>(Assert.IsType<GameBoardFact>(fact).Value),
            fact =>
            {
                ActorTravelStartedEvent started = Assert.IsType<ActorTravelStartedEvent>(
                    Assert.IsType<GameBoardFact>(fact).Value);
                Assert.Equal(ExitId(BoardIds.TavernMarketFerry), started.ExitId);
                Assert.Equal(BoardIds.Market, started.DestinationId);
            },
            fact =>
            {
                TraversalStartedFact started = Assert.IsType<TraversalStartedFact>(
                    Assert.IsType<SpatialBoardFact>(fact).Value);
                Assert.Equal(new PassageId(BoardIds.TavernMarketFerry), started.PassageId);
            });
        Assert.Equal(new WorldVersion(FirstBoardScenario.LineageId, 1), kernel.Version);
        Assert.DoesNotContain(kernel.World.Objects, item => item.Key == BoardIds.SilverCoinOne);
        TraversingLocation traversal = AssertTraversing(kernel.World, BoardIds.Alice);
        Assert.Equal(new PassageId(BoardIds.TavernMarketFerry), traversal.PassageId);
        Assert.Equal(new PlaceId(BoardIds.Market), traversal.ToPlaceId);
        Assert.Null(kernel.World.Actor(BoardIds.Alice).Activity);
        Assert.Single(alice.Requests);
    }

    [Fact]
    public async Task UnadvertisedFerryExit_FailsBeforeAnyCompositeStateIsCommitted()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 108);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Alice,
            new ModelTime(1_000_000));
        string initialSnapshot = FirstBoardScenario.WorldSnapshot(initial);
        long initialSequence = initial.Actor(BoardIds.Bob).DecisionSequence;
        var bob = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.TavernMarketFerry))));
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), bob),
                instance,
                journal,
                initial);
        WorldVersion initialVersion = kernel.Version;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(ModelTime.Zero));

        DecisionRequest request = Assert.Single(bob.Requests);
        Assert.False(Exit(request, BoardIds.TavernMarketFerry).IsAvailable);
        Assert.DoesNotContain(
            ExitId(BoardIds.TavernMarketFerry),
            TravelAction(request).CandidateExitIds!);
        Assert.Equal(initialSnapshot, FirstBoardScenario.WorldSnapshot(kernel.World));
        Assert.Equal(initialVersion, kernel.Version);
        Assert.Empty(journal.Batches);
        Assert.Equal(initialSequence, kernel.World.Actor(BoardIds.Bob).DecisionSequence);
    }

    [Fact]
    public async Task TraversingActor_HasNoPlaceAffordancesOrSecondStart_AndArrivesOnlyThroughGraph()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 104);
        FirstBoardWorld initial = MoveInitialEntity(
            instance,
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            BoardIds.Tavern);
        initial = WithWaitingActor(initial, BoardIds.Bob, new ModelTime(1_000_000));
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.TavernMarketRoad))));
        IReadOnlyDictionary<string, IPlayerDriver> drivers = Drivers(alice, new NullPlayerDriver());
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(drivers, instance, journal, initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        TraversingLocation traversal = AssertTraversing(kernel.World, BoardIds.Alice);

        var decisions = new DecisionPointRule(drivers, instance);
        Assert.DoesNotContain(
            decisions.Forecast(kernel.World, new SimulationRules(instance.WorldSeed, 10_000)),
            candidate => candidate.Data is DecisionPointCandidate value &&
                value.ActorId == kernel.World.Actor(BoardIds.Alice).Id);
        Assert.Throws<InvalidOperationException>(() => FirstBoardScenario.BuildRequest(
            instance,
            kernel.World,
            kernel.World.Actor(BoardIds.Alice),
            ModelTime.Zero));

        FirstBoardWorld bobCanObserve = WithObjectOwner(
            WithIdleActor(kernel.World, BoardIds.Bob),
            BoardIds.SilverCoinTwo,
            kernel.World.Actor(BoardIds.Bob).Id);
        DecisionRequest originRequest = FirstBoardScenario.BuildRequest(
            instance,
            bobCanObserve,
            bobCanObserve.Actor(BoardIds.Bob),
            ModelTime.Zero);
        Assert.DoesNotContain(BoardIds.Alice, originRequest.Observation.VisibleActorIds);
        Assert.DoesNotContain(
            originRequest.AvailableActions.SelectMany(action => action.CandidateActorIds ?? []),
            candidate => candidate == BoardIds.Alice);
        Assert.DoesNotContain(
            originRequest.AvailableActions,
            action => action.ActionKind == ActionKinds.Talk ||
                action.ActionKind == ActionKinds.Give ||
                action.ActionKind == ActionKinds.Show);

        SpatialPlanRejected secondStart = Assert.IsType<SpatialPlanRejected>(
            new SpatialPlanner(instance.Graph).TryStartTraversal(
                kernel.World.Spatial,
                new EntityId(BoardIds.Alice),
                new PassageId(BoardIds.TavernMarketFerry),
                BoardTiming.TravelSpeed,
                ModelTime.Zero));
        Assert.Equal("entity-not-at-place", secondStart.Reason);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(traversal.ArrivalDue));

        AssertAtPlace(kernel.World, BoardIds.Alice, BoardIds.Market);
        Assert.Null(kernel.World.Actor(BoardIds.Alice).Activity);
        JournalBatch<FirstBoardFact> arrivalBatch = journal.Batches[^1];
        SpatialBoardFact arrivalHostFact = Assert.IsType<SpatialBoardFact>(
            Assert.Single(arrivalBatch.Facts));
        Assert.IsType<TraversalArrivedFact>(arrivalHostFact.Value);
        Assert.Null(typeof(BoardActor).GetProperty("ArrivalDue"));
        Assert.Null(typeof(BoardActor).GetProperty("DestinationId"));
    }

    [Fact]
    public async Task TakeThenPut_FoldsGameCustodyAndSpatialPlacementAtomically()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 105);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Alice,
            new ModelTime(1_000_000));
        var bob = new RecordingPlayerDriver(
            request => new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey)),
            request => new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Put, TargetObjectId: BoardIds.BrassKey)));
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), bob),
                instance,
                journal,
                initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Collection(
            journal.Batches[0].Facts,
            fact => Assert.IsType<ObjectTakenEvent>(Assert.IsType<GameBoardFact>(fact).Value),
            fact => Assert.IsType<EntityRemovedFact>(Assert.IsType<SpatialBoardFact>(fact).Value));
        Assert.Equal(kernel.World.Actor(BoardIds.Bob).Id, kernel.World.Object(BoardIds.BrassKey).OwnerActorId);
        AssertNoSpatialEntity(kernel.World, BoardIds.BrassKey);
        new FirstBoardReducer(instance.Graph).Validate(kernel.World);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Collection(
            journal.Batches[1].Facts,
            fact => Assert.IsType<ObjectPlacedEvent>(Assert.IsType<GameBoardFact>(fact).Value),
            fact => Assert.IsType<EntityPlacedFact>(Assert.IsType<SpatialBoardFact>(fact).Value));
        Assert.Null(kernel.World.Object(BoardIds.BrassKey).OwnerActorId);
        AssertAtPlace(kernel.World, BoardIds.BrassKey, BoardIds.Market);
        new FirstBoardReducer(instance.Graph).Validate(kernel.World);
        Assert.Equal(2, bob.Requests.Count);
    }

    [Fact]
    public async Task CellarDeadline_ClosesOnlyInboundGateWhileCommittedTraversalStillArrives()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 106);
        ModelTime startAt = new(BoardTiming.DeadlineTicks - 1);
        FirstBoardWorld initial = MoveInitialEntity(
            instance,
            instance.CreateInitialWorld(),
            BoardIds.Alice,
            BoardIds.CellarGate);
        initial = initial with
        {
            Game = initial.Game with { Now = startAt },
        };
        initial = WithWaitingActor(
            initial,
            BoardIds.Bob,
            new ModelTime(BoardTiming.DeadlineTicks + 1_000_000));
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.CellarGatePassage))));
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice, new NullPlayerDriver()),
                instance,
                journal,
                initial);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(startAt));
        TraversingLocation committedTraversal = AssertTraversing(kernel.World, BoardIds.Alice);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(BoardTiming.DeadlineTicks)));

        JournalBatch<FirstBoardFact> deadlineBatch = journal.Batches[1];
        Assert.Collection(
            deadlineBatch.Facts,
            fact => Assert.IsType<CellarSealedEvent>(Assert.IsType<GameBoardFact>(fact).Value),
            fact =>
            {
                PassageEntryAccessChangedFact changed = Assert.IsType<PassageEntryAccessChangedFact>(
                    Assert.IsType<SpatialBoardFact>(fact).Value);
                Assert.Equal(new PassageId(BoardIds.CellarGatePassage), changed.PassageId);
                Assert.False(changed.ResultAccess.EnterableFromA);
                Assert.True(changed.ResultAccess.EnterableFromB);
            });
        Assert.True(kernel.World.CellarSealed);
        PassageEntryAccess access = new SpatialQueries(instance.Graph).GetPassageEntryAccess(
            kernel.World.Spatial,
            new PassageId(BoardIds.CellarGatePassage));
        Assert.False(access.EnterableFromA);
        Assert.True(access.EnterableFromB);
        Assert.Equal(committedTraversal, AssertTraversing(kernel.World, BoardIds.Alice));

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(committedTraversal.ArrivalDue));
        AssertAtPlace(kernel.World, BoardIds.Alice, BoardIds.Cellar);
        Assert.IsType<TraversalArrivedFact>(
            Assert.IsType<SpatialBoardFact>(Assert.Single(journal.Batches[^1].Facts)).Value);
    }

    [Fact]
    public async Task SameTickTravelAndCellarClose_WorldSeedSelectsBothCausalOrders()
    {
        (ulong startFirstSeed, ulong closeFirstSeed) = FindGateContestSeeds();
        Assert.NotEqual(startFirstSeed, closeFirstSeed);

        ScenarioInstance startFirstInstance = ScenarioInstance.CreateDefault(startFirstSeed);
        FirstBoardWorld startFirstWorld = GateContestWorld(startFirstInstance);
        var startFirstPlayer = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.CellarGatePassage))));
        var startFirstJournal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> startFirstKernel =
            FirstBoardScenario.CreateKernel(
                Drivers(startFirstPlayer, new NullPlayerDriver()),
                startFirstInstance,
                startFirstJournal,
                startFirstWorld);

        Assert.Equal(
            StepStatus.Committed,
            await startFirstKernel.StepAsync(new ModelTime(BoardTiming.DeadlineTicks)));
        Assert.Contains(
            startFirstJournal.Batches[0].Facts,
            fact => fact is SpatialBoardFact { Value: TraversalStartedFact });
        Assert.False(startFirstKernel.World.CellarSealed);
        TraversingLocation committedSegment = AssertTraversing(
            startFirstKernel.World,
            BoardIds.Alice);

        Assert.Equal(
            StepStatus.Committed,
            await startFirstKernel.StepAsync(new ModelTime(BoardTiming.DeadlineTicks)));
        Assert.True(startFirstKernel.World.CellarSealed);
        Assert.Equal(committedSegment, AssertTraversing(startFirstKernel.World, BoardIds.Alice));
        Assert.Equal(
            StepStatus.Committed,
            await startFirstKernel.StepAsync(committedSegment.ArrivalDue));
        AssertAtPlace(startFirstKernel.World, BoardIds.Alice, BoardIds.Cellar);

        ScenarioInstance closeFirstInstance = ScenarioInstance.CreateDefault(closeFirstSeed);
        FirstBoardWorld closeFirstWorld = GateContestWorld(closeFirstInstance);
        var closeFirstPlayer = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.CellarGatePassage))));
        var closeFirstJournal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> closeFirstKernel =
            FirstBoardScenario.CreateKernel(
                Drivers(closeFirstPlayer, new NullPlayerDriver()),
                closeFirstInstance,
                closeFirstJournal,
                closeFirstWorld);

        Assert.Equal(
            StepStatus.Committed,
            await closeFirstKernel.StepAsync(new ModelTime(BoardTiming.DeadlineTicks)));
        Assert.Contains(
            closeFirstJournal.Batches[0].Facts,
            fact => fact is GameBoardFact { Value: CellarSealedEvent });
        Assert.Empty(closeFirstPlayer.Requests);
        DecisionRequest afterClose = FirstBoardScenario.BuildRequest(
            closeFirstInstance,
            closeFirstKernel.World,
            closeFirstKernel.World.Actor(BoardIds.Alice),
            new ModelTime(BoardTiming.DeadlineTicks));
        Assert.False(Exit(afterClose, BoardIds.CellarGatePassage).IsAvailable);
        Assert.DoesNotContain(
            ExitId(BoardIds.CellarGatePassage),
            TravelAction(afterClose).CandidateExitIds!);
        AssertAtPlace(closeFirstKernel.World, BoardIds.Alice, BoardIds.CellarGate);
    }

    [Fact]
    public async Task OneHostWinner_AtomicallyClosesTwoPublicPassagesAndLeavesSecretUnchanged()
    {
        const long lineageId = 65_001;
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 109);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        var road = new PassageId(BoardIds.TavernMarketRoad);
        var ferry = new PassageId(BoardIds.TavernMarketFerry);
        var secret = new PassageId(BoardIds.MarketTavernCart);
        var queries = new SpatialQueries(instance.Graph);
        PassageEntryAccess secretBefore = queries.GetPassageEntryAccess(initial.Spatial, secret);
        var journal = new InMemoryJournal<FirstBoardFact>(lineageId);
        var reducer = new FirstBoardReducer(instance.Graph);
        var kernel = new SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact>(
            initial,
            new WorldVersion(lineageId, 0),
            initial.Now,
            lastCommittedInstant: null,
            new SimulationRules(instance.WorldSeed, 10_000),
            [new ClosePublicPassagesRule(instance.Graph, [road, ferry])],
            journal,
            reducer.Apply,
            reducer.Validate);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        JournalBatch<FirstBoardFact> batch = Assert.Single(journal.Batches);
        Assert.Equal(2, batch.Facts.Count);
        Assert.All(batch.Facts, fact => Assert.IsType<SpatialBoardFact>(fact));
        Assert.Equal(
            [ferry, road],
            batch.Facts
                .Cast<SpatialBoardFact>()
                .Select(fact => Assert.IsType<PassageEntryAccessChangedFact>(fact.Value).PassageId)
                .Order()
                .ToArray());
        Assert.Equal(
            new PassageEntryAccess(false, false),
            queries.GetPassageEntryAccess(kernel.World.Spatial, road));
        Assert.Equal(
            new PassageEntryAccess(false, false),
            queries.GetPassageEntryAccess(kernel.World.Spatial, ferry));
        Assert.Equal(secretBefore, queries.GetPassageEntryAccess(kernel.World.Spatial, secret));
        Assert.Equal(new WorldVersion(lineageId, 1), kernel.Version);
    }

    [Fact]
    public async Task CompositePrefixFork_ContinuesIndependentlyWithoutMutatingSource()
    {
        const long forkLineageId = 75_001;
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 110);
        FirstBoardWorld genesis = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            new ModelTime(1_000_000));
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.TavernMarketFerry))));
        var sourceJournal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> sourceKernel =
            FirstBoardScenario.CreateKernel(
                Drivers(alice, new NullPlayerDriver()),
                instance,
                sourceJournal,
                genesis);
        Assert.Equal(StepStatus.Committed, await sourceKernel.StepAsync(ModelTime.Zero));
        Assert.Contains(
            sourceJournal.Batches[0].Facts,
            fact => fact is GameBoardFact);
        Assert.Contains(
            sourceJournal.Batches[0].Facts,
            fact => fact is SpatialBoardFact);
        string sourceSnapshot = FirstBoardScenario.WorldSnapshot(sourceKernel.World);
        WorldVersion sourceVersion = sourceKernel.Version;
        int sourceBatchCount = sourceJournal.Batches.Count;
        TraversingLocation sourceTraversal = AssertTraversing(sourceKernel.World, BoardIds.Alice);
        var reducer = new FirstBoardReducer(instance.Graph);

        InMemoryForkResult<FirstBoardWorld, FirstBoardFact> fork = SimulationFork.Create(
            genesis,
            genesis.Now,
            sourceJournal,
            prefixTransitionCount: 1,
            forkLineageId,
            new SimulationRules(instance.WorldSeed, 10_000),
            reducer.Apply,
            reducer.Validate);
        Assert.Equal(sourceSnapshot, FirstBoardScenario.WorldSnapshot(fork.Replay.World));
        Assert.Equal(new WorldVersion(forkLineageId, 1), fork.Replay.Version);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> forkKernel =
            FirstBoardScenario.CreateKernel(
                Drivers(new NullPlayerDriver(), new NullPlayerDriver()),
                instance,
                fork.Journal,
                fork.Replay.World,
                fork.Replay.Version,
                fork.Replay.LastCommittedInstant);

        Assert.Equal(StepStatus.Committed, await forkKernel.StepAsync(sourceTraversal.ArrivalDue));

        AssertAtPlace(forkKernel.World, BoardIds.Alice, BoardIds.Market);
        Assert.Equal(new WorldVersion(forkLineageId, 2), forkKernel.Version);
        Assert.Equal(2, fork.Journal.Batches.Count);
        Assert.Equal(sourceSnapshot, FirstBoardScenario.WorldSnapshot(sourceKernel.World));
        Assert.Equal(sourceVersion, sourceKernel.Version);
        Assert.Equal(sourceBatchCount, sourceJournal.Batches.Count);
        Assert.Equal(sourceTraversal, AssertTraversing(sourceKernel.World, BoardIds.Alice));
    }

    [Fact]
    public async Task CurrentFormatReplay_RebuildsCompleteCompositeBatchesWithoutCallingPlayers()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 107);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            new ModelTime(1_000_000));
        var alice = new RecordingPlayerDriver(
            request => new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Travel, ExitId: ExitId(BoardIds.TavernMarketFerry))),
            request => new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Wait, DurationMs: 60_000)));
        var bob = new RecordingPlayerDriver();
        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            Drivers(alice, bob),
            instance,
            until: new ModelTime(180_000),
            initialWorld: initial);
        int callsBeforeReplay = alice.Requests.Count + bob.Requests.Count;
        var reducer = new FirstBoardReducer(instance.Graph);

        ReplayResult<FirstBoardWorld> replay = SimulationReplay.Replay(
            capture.InitialWorld,
            capture.Journal.LineageId,
            capture.InitialWorld.Now,
            capture.Journal.Batches,
            reducer.Apply,
            reducer.Validate);

        Assert.NotEmpty(capture.Journal.Batches);
        Assert.Contains(
            capture.Journal.Batches,
            batch => batch.Facts.Any(fact => fact is GameBoardFact) &&
                batch.Facts.Any(fact => fact is SpatialBoardFact));
        Assert.Equal(callsBeforeReplay, alice.Requests.Count + bob.Requests.Count);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(capture.Result.World),
            FirstBoardScenario.WorldSnapshot(replay.World));
        Assert.Equal(capture.Result.Version, replay.Version);
        Assert.Equal(capture.Result.CurrentModelTime, replay.CurrentModelTime);
        Assert.Equal(capture.Journal.Batches[^1].Instant, replay.LastCommittedInstant);
    }

    private static ObservedExit Exit(DecisionRequest request, string passageId) =>
        request.Observation.Exits.Single(exit => exit.ExitId == ExitId(passageId));

    private static AvailableAction TravelAction(DecisionRequest request) =>
        request.AvailableActions.Single(action => action.ActionKind == ActionKinds.Travel);

    private static void AssertAdvertisesExactlyAvailableExits(DecisionRequest request)
    {
        string[] expected =
        [
            .. request.Observation.Exits
                .Where(exit => exit.IsAvailable)
                .Select(exit => exit.ExitId)
                .Order(StringComparer.Ordinal),
        ];
        Assert.Equal(expected, TravelAction(request).CandidateExitIds);
        Assert.Null(TravelAction(request).CandidateDestinationIds);
    }

    private static string ExitId(string passageId) => $"exit:{passageId}";

    private static (ulong StartFirst, ulong CloseFirst) FindGateContestSeeds()
    {
        ScenarioInstance template = ScenarioInstance.CreateDefault(worldSeed: 0);
        FirstBoardWorld world = GateContestWorld(template);
        IReadOnlyDictionary<string, IPlayerDriver> drivers =
            Drivers(new NullPlayerDriver(), new NullPlayerDriver());
        var simulationRules = new SimulationRules(template.WorldSeed, 10_000);
        OccurrenceCandidate<BoardCandidate>[] contenders =
        [
            .. new CellarDeadlineRule(template.Graph).Forecast(world, simulationRules),
            .. new DecisionPointRule(drivers, template).Forecast(world, simulationRules),
        ];
        ulong? startFirst = null;
        ulong? closeFirst = null;
        for (ulong seed = 0; seed < 10_000 &&
             (startFirst is null || closeFirst is null); seed++)
        {
            BoardCandidate winner = OccurrenceScheduler.SelectWinner(contenders, seed).Data;
            if (winner is DecisionPointCandidate)
            {
                startFirst ??= seed;
            }
            else if (winner is DeadlineCandidate)
            {
                closeFirst ??= seed;
            }
        }

        return (
            startFirst ?? throw new InvalidOperationException("No start-first seed was found."),
            closeFirst ?? throw new InvalidOperationException("No close-first seed was found."));
    }

    private static FirstBoardWorld GateContestWorld(ScenarioInstance instance)
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
        return WithWaitingActor(
            world,
            BoardIds.Bob,
            new ModelTime(BoardTiming.DeadlineTicks + 1_000_000));
    }

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

    private static FirstBoardWorld WithIdleActor(FirstBoardWorld world, string actorId) =>
        world with
        {
            Game = world.Game with
            {
                Actors = Array.AsReadOnly(world.Actors
                    .Select(actor => actor.Key == actorId
                        ? actor with { Activity = null }
                        : actor)
                    .ToArray()),
            },
        };

    private static FirstBoardWorld WithObjectOwner(
        FirstBoardWorld world,
        string objectId,
        long ownerActorId) =>
        world with
        {
            Game = world.Game with
            {
                Objects = Array.AsReadOnly(world.Objects
                    .Select(item => item.Key == objectId
                        ? item with { OwnerActorId = ownerActorId }
                        : item)
                    .ToArray()),
            },
        };

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

    private static void AssertNoSpatialEntity(FirstBoardWorld world, string entityId) =>
        Assert.False(world.Spatial.TryGetEntity(new EntityId(entityId), out _));

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(
        IPlayerDriver alice,
        IPlayerDriver bob) =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = alice,
            [BoardIds.Bob] = bob,
        };

    private sealed record ClosePublicPassagesCandidate : BoardCandidate;

    private sealed class ClosePublicPassagesRule :
        IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
    {
        private readonly SpatialPlanner _planner;
        private readonly IReadOnlyList<PassageId> _passageIds;

        public ClosePublicPassagesRule(
            GraphDefinition definition,
            IReadOnlyList<PassageId> passageIds)
        {
            _planner = new SpatialPlanner(definition);
            _passageIds = passageIds;
        }

        public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
            FirstBoardWorld world,
            SimulationRules rules) =>
            [
                new(
                    CandidateKey.FromUtf8("test/host/close-public-passages"),
                    new CandidateDue(world.Now),
                    new ClosePublicPassagesCandidate()),
            ];

        public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
            FirstBoardWorld world,
            OccurrenceCandidate<BoardCandidate> winner,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsType<ClosePublicPassagesCandidate>(winner.Data);
            FirstBoardFact[] facts =
            [
                .. _passageIds
                    .Order()
                    .SelectMany(passageId => Assert.IsType<SpatialPlanAccepted>(
                        _planner.TrySetPassageEntryAccess(
                            world.Spatial,
                            passageId,
                            new PassageEntryPatch(false, false))).Facts)
                    .Select(fact => new SpatialBoardFact(fact)),
            ];
            return ValueTask.FromResult(new TransitionDraft<FirstBoardFact>(facts));
        }
    }
}

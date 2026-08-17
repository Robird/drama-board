using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Tests;

public sealed class FirstBoardTests
{
    [Fact]
    public void Reducer_SameEventIdWithNewSchemaVersion_StillRoutes()
    {
        FirstBoardWorld world = FirstBoardWorld.CreateInitial(worldSeed: 1);
        var domainEvent = new DomainEvent<BoardEventPayload>(
            new LogicalTimestamp(ModelTime.Zero, new Microstep(0)),
            EventCause.FromExternalInput(batchOrdinal: 0),
            new EventKind(BoardEventKinds.CellarSealed.Id, version: 2),
            new CellarSealedEvent());

        FirstBoardWorld updated = new FirstBoardReducer().Apply(world, domainEvent);

        Assert.True(updated.CellarSealed);
    }

    [Fact]
    public void WaitWhoseCompletionWouldOverflow_IsRejectedAndLoopContinues()
    {
        var reducer = new FirstBoardReducer();
        var loop = new SimulationLoop<FirstBoardWorld, BoardCandidate, BoardEventPayload>(
            [new ActionResolutionSystem()],
            reducer);
        var journal = new InMemoryJournal<BoardEventPayload>();
        ModelTime now = new(long.MaxValue - 100);

        SimulationRunResult<FirstBoardWorld, BoardEventPayload> result = loop.Run(
            FirstBoardWorld.CreateInitial(worldSeed: 1),
            SimulationCursor.CreateInitial(FirstBoardScenario.LineageId, now),
            now,
            journal,
            [FirstBoardScenario.ActionInput(
                BoardIds.Alice,
                "overflowing-wait",
                new Intent(ActionKinds.Wait, DurationMs: 101))]);

        Assert.Equal(StopReason.Exhausted, result.StopReason);
        Assert.Null(result.World.Actor(BoardIds.Alice).PendingAction);
        DomainEvent<BoardEventPayload> rejectedEvent = Assert.Single(
            journal.Events,
            domainEvent => domainEvent.Kind == BoardEventKinds.ActionRejected);
        Assert.Equal(
            "wait completion time exceeds the model-time range",
            Assert.IsType<ActionRejectedEvent>(rejectedEvent.Payload).Reason);
    }

    [Fact]
    public async Task ScriptedGame_FindsSecret_ReplaysAndPreservesAsymmetricKnowledge()
    {
        ScriptedCapture capture = await RunDiscoveryScriptAsync();
        FirstBoardWorld world = capture.Run.Result.World;
        BoardActor alice = world.Actor(BoardIds.Alice);
        BoardActor bob = world.Actor(BoardIds.Bob);
        BoardObject brassKey = world.Object(BoardIds.BrassKey);

        Assert.Equal(StopReason.BoundaryReached, capture.Run.Result.StopReason);
        Assert.Equal(0, capture.Run.Result.ForcedDecisionCount);
        Assert.Equal(BoardIds.Cellar, alice.PlaceId);
        Assert.Equal(BoardIds.Tavern, bob.PlaceId);
        Assert.Equal(alice.Id, brassKey.OwnerActorId);
        Assert.True(world.ChestOpened);
        Assert.Contains(alice.KnownFacts, fact => fact.Kind == BoardIds.ChestContainsLetter);
        Assert.DoesNotContain(bob.KnownFacts, fact => fact.Kind == BoardIds.ChestContainsLetter);
        Assert.Contains(bob.KnownFacts, fact => fact.Kind == BoardIds.KeyLocationKnown);

        Assert.Equal(
            [
                "action.travel-requested",
                "actor.departed",
                "actor.arrived",
                "action.take-requested",
                "object.taken",
                "action.talk-requested",
                "actor.spoke",
                "action.travel-requested",
                "actor.departed",
                "actor.arrived",
                "action.use-requested",
                "chest.opened",
            ],
            AliceKeyHistory(capture.Run.Journal));

        var reducer = new FirstBoardReducer();
        FirstBoardWorld replayed = capture.Run.Journal.Events.Aggregate(
            capture.Run.InitialWorld,
            reducer.Apply);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(world),
            FirstBoardScenario.WorldSnapshot(replayed));

        DecisionRequest aliceInitial = capture.Alice.Requests[0];
        DecisionRequest aliceLast = capture.Alice.Requests[^1];
        DecisionRequest bobLast = capture.Bob.Requests[^1];
        Assert.Equal(BoardIds.Tavern, aliceInitial.Observation.LocationId);
        Assert.Empty(aliceInitial.Observation.VisibleActorIds);
        Assert.Empty(aliceInitial.Observation.VisibleObjectIds);
        Assert.Contains(BoardIds.LockedChest, aliceLast.Observation.VisibleObjectIds);
        Assert.Contains(aliceLast.Observation.KnownFacts, fact =>
            fact.FactKind.Id == BoardIds.ChestContainsLetter);
        Assert.Contains(aliceLast.Observation.KnownFacts, fact =>
            fact.FactKind.Id == BoardIds.LastActionOutcome &&
            fact.Text.Contains("found the duchess's letter", StringComparison.Ordinal));
        Assert.DoesNotContain(bobLast.Observation.KnownFacts, fact =>
            fact.FactKind.Id == BoardIds.ChestContainsLetter);
    }

    [Fact]
    public async Task DawdlingGame_DeadlineRejectsCellarTravelAndMakesSecretUnobtainable()
    {
        var alice = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Travel, DestinationId: BoardIds.Market)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 3_300_000)),
            request => Decide(request, new Intent(ActionKinds.Travel, DestinationId: BoardIds.Cellar)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));
        var bob = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));

        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            Drivers(alice, bob),
            worldSeed: 17,
            new ModelTime(BoardTiming.RandomRunBoundaryTicks));

        Assert.True(capture.Result.World.CellarSealed);
        Assert.Equal(BoardIds.Market, capture.Result.World.Actor(BoardIds.Alice).PlaceId);
        Assert.DoesNotContain(
            capture.Result.World.Actors.SelectMany(actor => actor.KnownFacts),
            fact => fact.Kind == BoardIds.ChestContainsLetter);

        DomainEvent<BoardEventPayload> rejectedEvent = Assert.Single(
            capture.Journal.Events,
            domainEvent => domainEvent.Kind == BoardEventKinds.ActionRejected);
        var rejected = Assert.IsType<ActionRejectedEvent>(rejectedEvent.Payload);
        Assert.Equal("cellar is sealed", rejected.Reason);
        Assert.Equal(
            new Intent(ActionKinds.Travel, DestinationId: BoardIds.Cellar),
            rejected.RejectedIntent);
        Assert.Equal(BoardTiming.DeadlineTicks, rejectedEvent.Timestamp.ModelTime.Ticks);

        DecisionRequest unwittingRequest = alice.Requests[2];
        AvailableAction unwittingTravel = Assert.Single(
            unwittingRequest.AvailableActions,
            action => action.ActionKind == ActionKinds.Travel);
        Assert.Contains(BoardIds.Cellar, unwittingTravel.CandidateDestinationIds!);
        Assert.Equal(DecisionReasons.Scheduled, unwittingRequest.Reason);
        Assert.Null(unwittingRequest.RejectedIntent);

        DecisionRequest rejectionRequest = alice.Requests[3];
        Assert.Equal(DecisionReasons.ActionRejected, rejectionRequest.Reason);
        Assert.Equal(rejected.RejectedIntent, rejectionRequest.RejectedIntent);
        Assert.Contains(rejectionRequest.Observation.KnownFacts, fact =>
            fact.FactKind.Id == BoardIds.ActionRejected &&
            fact.RelatedId == ActionKinds.Travel.Id &&
            fact.Text.Contains("cellar is sealed", StringComparison.Ordinal) &&
            fact.Text.Contains("destination=cellar", StringComparison.Ordinal));
        Assert.Contains(rejectionRequest.Observation.KnownFacts, fact =>
            fact.FactKind.Id == BoardIds.CellarSealedKnown);
        AvailableAction informedTravel = Assert.Single(
            rejectionRequest.AvailableActions,
            action => action.ActionKind == ActionKinds.Travel);
        Assert.DoesNotContain(BoardIds.Cellar, informedTravel.CandidateDestinationIds!);
        int sealedIndex = capture.Journal.Events.ToList().FindIndex(domainEvent =>
            domainEvent.Kind == BoardEventKinds.CellarSealed);
        int rejectedIndex = capture.Journal.Events.ToList().FindIndex(domainEvent =>
            domainEvent.Kind == BoardEventKinds.ActionRejected);
        Assert.True(sealedIndex >= 0 && sealedIndex < rejectedIndex);
    }

    [Fact]
    public void CellarSealed_ActorsPresentInCellarImmediatelyLearnWhileRemoteActorsDoNot()
    {
        FirstBoardWorld initial = FirstBoardWorld.CreateInitial(worldSeed: 19);
        initial = initial with
        {
            Actors = Array.AsReadOnly(initial.Actors
                .Select(actor => actor.Key == BoardIds.Alice
                    ? actor with { PlaceId = BoardIds.Cellar }
                    : actor)
                .ToArray()),
        };
        var sealedEvent = new DomainEvent<BoardEventPayload>(
            new LogicalTimestamp(new ModelTime(BoardTiming.DeadlineTicks), new Microstep(0)),
            EventCause.FromExternalInput(batchOrdinal: 0),
            BoardEventKinds.CellarSealed,
            new CellarSealedEvent());

        FirstBoardWorld updated = new FirstBoardReducer().Apply(initial, sealedEvent);

        Assert.Contains(updated.Actor(BoardIds.Alice).KnownFacts, fact =>
            fact.Kind == BoardIds.CellarSealedKnown);
        Assert.DoesNotContain(updated.Actor(BoardIds.Bob).KnownFacts, fact =>
            fact.Kind == BoardIds.CellarSealedKnown);
    }

    [Fact]
    public void BuildRequest_UnwitnessedCellarFlag_DoesNotChangeObservationOrAffordances()
    {
        FirstBoardWorld openWorld = FirstBoardWorld.CreateInitial(worldSeed: 23);
        FirstBoardWorld sealedWorld = openWorld with { CellarSealed = true };

        DecisionRequest openRequest = RequestFor(openWorld, BoardIds.Bob);
        DecisionRequest sealedRequest = RequestFor(sealedWorld, BoardIds.Bob);

        Assert.Equal(openRequest.Observation.LocationId, sealedRequest.Observation.LocationId);
        Assert.Equal(openRequest.Observation.VisibleActorIds, sealedRequest.Observation.VisibleActorIds);
        Assert.Equal(openRequest.Observation.VisibleObjectIds, sealedRequest.Observation.VisibleObjectIds);
        Assert.Equal(openRequest.Observation.KnownFacts, sealedRequest.Observation.KnownFacts);
        Assert.Equal(
            Assert.Single(openRequest.AvailableActions, action => action.ActionKind == ActionKinds.Travel)
                .CandidateDestinationIds,
            Assert.Single(sealedRequest.AvailableActions, action => action.ActionKind == ActionKinds.Travel)
                .CandidateDestinationIds);
    }

    [Fact]
    public void Observation_CarriedObjectsAreVisibleOnlyToOwnerAndPlacedObjectsRemainVisible()
    {
        FirstBoardWorld world = BothActorsAtMarket(FirstBoardWorld.CreateInitial(worldSeed: 31));
        BoardActor bob = world.Actor(BoardIds.Bob);
        FirstBoardWorld carriedWorld = world with
        {
            Objects = Array.AsReadOnly(world.Objects
                .Select(item => item.Key == BoardIds.BrassKey
                    ? item with { PlaceId = null, OwnerActorId = bob.Id }
                    : item)
                .ToArray()),
        };

        DecisionRequest aliceWithCarriedKey = RequestFor(carriedWorld, BoardIds.Alice);
        DecisionRequest bobWithOwnKey = RequestFor(carriedWorld, BoardIds.Bob);
        DecisionRequest aliceWithPlacedKey = RequestFor(world, BoardIds.Alice);

        Assert.DoesNotContain(BoardIds.BrassKey, aliceWithCarriedKey.Observation.VisibleObjectIds);
        Assert.Contains(BoardIds.BrassKey, bobWithOwnKey.Observation.VisibleObjectIds);
        Assert.Contains(BoardIds.BrassKey, aliceWithPlacedKey.Observation.VisibleObjectIds);
        Assert.DoesNotContain(aliceWithCarriedKey.Observation.KnownFacts, fact =>
            fact.FactKind.Id == BoardIds.ObjectHeld);
        Assert.Contains(bobWithOwnKey.Observation.KnownFacts, fact =>
            fact.FactKind.Id == BoardIds.ObjectHeld &&
            fact.RelatedId == BoardIds.BrassKey &&
            fact.Text.Contains("You are carrying", StringComparison.Ordinal));
    }

    [Fact]
    public void TalkEvent_AwakensWaitingListenerAndMakesDialogueAuthoritativeInput()
    {
        FirstBoardWorld world = BothActorsAtMarket(FirstBoardWorld.CreateInitial(worldSeed: 33));
        world = world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor.Key switch
                {
                    BoardIds.Alice => actor with
                    {
                        PendingAction = new SubmittedAction(
                            "decision.alice.1",
                            new Intent(
                                ActionKinds.Talk,
                                TargetActorId: BoardIds.Bob,
                                FreeText: "Meet me in the cellar.")),
                    },
                    BoardIds.Bob => actor with
                    {
                        Activity = new BoardActivity(
                            BoardActivityKind.Wait,
                            new ModelTime(600_000)),
                    },
                    _ => actor,
                })
                .ToArray()),
        };
        var spokeEvent = new DomainEvent<BoardEventPayload>(
            new LogicalTimestamp(new ModelTime(10_000), new Microstep(0)),
            EventCause.FromExternalInput(batchOrdinal: 0),
            BoardEventKinds.ActorSpoke,
            new ActorSpokeEvent(
                BoardIds.Alice,
                BoardIds.Bob,
                "Meet me in the cellar.",
                SharedFactKind: null));

        FirstBoardWorld updated = new FirstBoardReducer().Apply(world, spokeEvent);
        BoardActor bob = updated.Actor(BoardIds.Bob);

        Assert.Null(bob.Activity);
        Assert.Contains(bob.KnownFacts, fact =>
            fact.Kind == BoardIds.DialogueHeard &&
            fact.RelatedId == BoardIds.Alice &&
            fact.Text.Contains("Meet me in the cellar", StringComparison.Ordinal));
        Assert.Contains(bob.KnownFacts, fact =>
            fact.Kind == BoardIds.LastActionOutcome &&
            fact.Text.Contains("wait was interrupted", StringComparison.Ordinal));

        var scheduler = new DecisionSchedulingSystem();
        EventCandidate<BoardCandidate> candidate = Assert.Single(
            scheduler.ForecastNext(updated, spokeEvent.Timestamp.ModelTime));
        IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> decisions =
            scheduler.Resolve(updated, candidate);
        Assert.Contains(decisions, item =>
            Assert.IsType<DecisionRequestedEvent>(item.Payload).ActorId == BoardIds.Bob);
    }

    [Fact]
    public void BuildRequest_KeyHolderInCellar_CanUseLockedChest()
    {
        FirstBoardWorld world = FirstBoardWorld.CreateInitial(worldSeed: 35);
        BoardActor alice = world.Actor(BoardIds.Alice);
        world = world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor.Key == BoardIds.Alice
                    ? actor with { PlaceId = BoardIds.Cellar }
                    : actor)
                .ToArray()),
            Objects = Array.AsReadOnly(world.Objects
                .Select(item => item.Key == BoardIds.BrassKey
                    ? item with { PlaceId = null, OwnerActorId = alice.Id }
                    : item)
                .ToArray()),
        };

        DecisionRequest request = RequestFor(world, BoardIds.Alice);

        AvailableAction use = Assert.Single(
            request.AvailableActions,
            action => action.ActionKind == ActionKinds.Use);
        Assert.Equal([BoardIds.LockedChest], use.CandidateObjectIds);
    }

    [Fact]
    public async Task RepeatedIllegalTravel_AfterEightRejectionsForcesWaitAndAdvancesWorld()
    {
        var alice = new AlwaysIllegalTravelPlayerDriver();
        var bob = new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]);

        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            Drivers(alice, bob),
            worldSeed: 37,
            new ModelTime(BoardTiming.DefaultWaitTicks - 1));

        Assert.Equal(8, alice.DecisionCount);
        Assert.Equal(1, capture.Result.ForcedDecisionCount);
        BoardActivity forcedWait = Assert.IsType<BoardActivity>(
            capture.Result.World.Actor(BoardIds.Alice).Activity);
        Assert.Equal(BoardActivityKind.Wait, forcedWait.Kind);
        Assert.Equal(BoardTiming.DefaultWaitTicks, forcedWait.Due.Ticks);
        Assert.Equal(8, capture.Journal.Events.Count(domainEvent =>
            domainEvent.Kind == BoardEventKinds.ActionRejected &&
            Assert.IsType<ActionRejectedEvent>(domainEvent.Payload).ActorId == BoardIds.Alice));
        Assert.Single(capture.Journal.Events, domainEvent =>
            domainEvent.Kind == BoardEventKinds.ActorWaitStarted &&
            Assert.IsType<ActorWaitStartedEvent>(domainEvent.Payload).ActorId == BoardIds.Alice);
    }

    [Fact]
    public async Task ForcedWait_WhenDomainRejectsIt_ThrowsDiagnosticException()
    {
        ModelTime now = new(long.MaxValue - 100);
        var journal = new InMemoryJournal<BoardEventPayload>();
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new AlwaysIllegalTravelPlayerDriver(),
            [BoardIds.Bob] = new NullPlayerDriver(),
        };
        var session = new PlayerDecisionSession<FirstBoardWorld, BoardCandidate, BoardEventPayload>(
            FirstBoardScenario.CreateLoop(new FirstBoardReducer()),
            journal,
            FirstBoardWorld.CreateInitial(worldSeed: 41) with { CellarSealed = true },
            SimulationCursor.CreateInitial(FirstBoardScenario.LineageId, now),
            FirstBoardScenario.SelectActor,
            drivers,
            FirstBoardScenario.BuildRequest,
            FirstBoardScenario.TranslateDecision,
            maxConsecutiveRejectionsPerActor: 1,
            rejectionSelector: FirstBoardScenario.SelectRejectedActor);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.RunUntilAsync(now, CancellationToken.None));

        Assert.Contains("Forced wait", exception.Message);
        Assert.Contains(BoardIds.Alice, exception.Message);
        Assert.Contains(now.Ticks.ToString(), exception.Message);
    }

    [Fact]
    public async Task RandomGame_IsDeterministicPerSeed_DivergesAcrossSeedsAndReplays()
    {
        BoardRunCapture first = await RunRandomAsync(0xA11CEUL);
        BoardRunCapture repeated = await RunRandomAsync(0xA11CEUL);
        BoardRunCapture alternate = await RunRandomAsync(0xB0BUL);

        Assert.Equal(
            FirstBoardScenario.EventSnapshots(first.Journal),
            FirstBoardScenario.EventSnapshots(repeated.Journal));
        Assert.False(
            FirstBoardScenario.EventSnapshots(first.Journal)
                .SequenceEqual(FirstBoardScenario.EventSnapshots(alternate.Journal)));
        Assert.Contains(
            first.Result.StopReason,
            new[] { StopReason.BoundaryReached, StopReason.Exhausted });

        var reducer = new FirstBoardReducer();
        FirstBoardWorld replayed = first.Journal.Events.Aggregate(
            first.InitialWorld,
            reducer.Apply);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(first.Result.World),
            FirstBoardScenario.WorldSnapshot(replayed));
    }

    [Fact]
    public async Task GiveAction_TransfersHeldObjectThroughAdvertisedAffordance()
    {
        var alice = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Travel, DestinationId: BoardIds.Market)),
            request => Decide(request, new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey)),
            request =>
            {
                AvailableAction give = Assert.Single(
                    request.AvailableActions,
                    action => action.ActionKind == ActionKinds.Give);
                Assert.Contains(BoardIds.Bob, give.CandidateActorIds!);
                Assert.Contains(BoardIds.BrassKey, give.CandidateObjectIds!);
                return Decide(request, new Intent(
                    ActionKinds.Give,
                    TargetActorId: BoardIds.Bob,
                    TargetObjectId: BoardIds.BrassKey));
            },
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));
        var bob = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));

        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            Drivers(alice, bob),
            worldSeed: 29,
            new ModelTime(600_000));

        Assert.Equal(
            capture.Result.World.Actor(BoardIds.Bob).Id,
            capture.Result.World.Object(BoardIds.BrassKey).OwnerActorId);
        Assert.Single(capture.Journal.Events, domainEvent =>
            domainEvent.Kind == BoardEventKinds.ObjectGiven);
    }

    [Fact]
    public async Task KeyContention_HostBatchIsSymmetricAndWinnerIgnoresDriverRegistrationOrder()
    {
        HostContentionCapture first = await RunHostContentionAsync(reverseDriverRegistration: false);
        HostContentionCapture second = await RunHostContentionAsync(reverseDriverRegistration: true);

        Assert.Equal(first.Run.Result.World.Object(BoardIds.BrassKey).OwnerActorId,
            second.Run.Result.World.Object(BoardIds.BrassKey).OwnerActorId);
        Assert.Equal(
            first.Run.Result.World.Actor(first.Contention.WinnerActorId).Key,
            second.Run.Result.World.Actor(second.Contention.WinnerActorId).Key);
        Assert.Equal(first.Contention.Sample, second.Contention.Sample);
        Assert.Equal(
            first.Run.InitialWorld.Actors.Select(actor => actor.Id).Order().ToArray(),
            first.Contention.CompetitorActorIds);
        Assert.Equal(first.Alice.Requests[0].BasedOnWorldVersion, first.Bob.Requests[0].BasedOnWorldVersion);
        Assert.Equal(first.Alice.Requests[0].ModelTimeMs, first.Bob.Requests[0].ModelTimeMs);
        Assert.Contains(BoardIds.BrassKey, first.Alice.Requests[0].Observation.VisibleObjectIds);
        Assert.Contains(BoardIds.BrassKey, first.Bob.Requests[0].Observation.VisibleObjectIds);
        Assert.Contains(
            Assert.Single(first.Alice.Requests[0].AvailableActions,
                action => action.ActionKind == ActionKinds.Take).CandidateObjectIds!,
            objectId => objectId == BoardIds.BrassKey);
        Assert.Contains(
            Assert.Single(first.Bob.Requests[0].AvailableActions,
                action => action.ActionKind == ActionKinds.Take).CandidateObjectIds!,
            objectId => objectId == BoardIds.BrassKey);

        DomainEvent<BoardEventPayload>[] actionInputs =
        [
            .. first.Run.Journal.Events.Where(domainEvent =>
                domainEvent.Kind == BoardEventKinds.TakeRequested),
        ];
        Assert.Equal(2, actionInputs.Length);
        Assert.Equal(actionInputs[0].Cause, actionInputs[1].Cause);
        Assert.Equal(CauseKind.ExternalInput, actionInputs[0].Cause.Kind);
        Assert.True(actionInputs[^1].Timestamp < Assert.Single(
            first.Run.Journal.Events,
            domainEvent => domainEvent.Kind == BoardEventKinds.ObjectContentionResolved).Timestamp);
    }

    [Fact]
    public async Task ScriptedGame_JournalFormatsAsTimestampKindAndPayloadLines()
    {
        ScriptedCapture capture = await RunDiscoveryScriptAsync();

        string history = FirstBoardScenario.FormatJournal(capture.Run.Journal);

        Assert.Contains("actor.departed actor=alice origin=tavern destination=market", history);
        Assert.Contains("object.taken actor=alice object=brass-key", history);
        Assert.Contains(
            "actor.spoke actor=alice target=bob text=fact:key.location-known",
            history);
        Assert.Contains(
            "chest.opened actor=alice object=locked-chest key=brass-key",
            history);
        Assert.Contains("3600000:0 cellar.sealed place=cellar", history);
    }

    private static async Task<ScriptedCapture> RunDiscoveryScriptAsync()
    {
        var alice = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Travel, DestinationId: BoardIds.Market)),
            request => Decide(request, new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey)),
            request => Decide(request, new Intent(
                ActionKinds.Talk,
                TargetActorId: BoardIds.Bob,
                FreeText: $"fact:{BoardIds.KeyLocationKnown}")),
            request => Decide(request, new Intent(ActionKinds.Travel, DestinationId: BoardIds.Cellar)),
            request => Decide(request, new Intent(
                ActionKinds.Use,
                TargetObjectId: BoardIds.LockedChest)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));
        var bob = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: BoardTiming.TravelTicks)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: BoardTiming.TravelTicks)),
            request => Decide(request, new Intent(ActionKinds.Travel, DestinationId: BoardIds.Tavern)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));
        BoardRunCapture run = await FirstBoardScenario.RunAsync(
            Drivers(alice, bob),
            worldSeed: 42,
            new ModelTime(BoardTiming.RandomRunBoundaryTicks));
        return new ScriptedCapture(run, alice, bob);
    }

    private static async Task<BoardRunCapture> RunRandomAsync(ulong seed) =>
        await FirstBoardScenario.RunAsync(
            new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
            {
                [BoardIds.Alice] = new RandomPlayerDriver(unchecked((long)seed)),
                [BoardIds.Bob] = new RandomPlayerDriver(unchecked((long)(seed ^ 0x9E3779B97F4A7C15UL))),
            },
            worldSeed: seed,
            new ModelTime(BoardTiming.RandomRunBoundaryTicks));

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(
        IPlayerDriver alice,
        IPlayerDriver bob) =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = alice,
            [BoardIds.Bob] = bob,
        };

    private static PlayerDecision Decide(DecisionRequest request, Intent intent) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            intent);

    private static string[] AliceKeyHistory(InMemoryJournal<BoardEventPayload> journal) =>
        [
            .. journal.Events
                .Where(domainEvent => IsAliceKeyEvent(domainEvent.Payload))
                .Select(domainEvent => domainEvent.Kind.Id),
        ];

    private static bool IsAliceKeyEvent(BoardEventPayload payload) =>
        payload switch
        {
            ActionRequestedEvent requested =>
                requested.ActorId == BoardIds.Alice &&
                requested.Intent.ActionKind != ActionKinds.Wait,
            ActorDepartedEvent departed => departed.ActorId == BoardIds.Alice,
            ActorArrivedEvent arrived => arrived.ActorId == BoardIds.Alice,
            ObjectTakenEvent taken => taken.ActorId == BoardIds.Alice,
            ActorSpokeEvent spoke => spoke.ActorId == BoardIds.Alice,
            ActorObservedEvent observed => observed.ActorId == BoardIds.Alice,
            ChestOpenedEvent opened => opened.ActorId == BoardIds.Alice,
            _ => false,
        };

    private static FirstBoardWorld BothActorsAtMarket(FirstBoardWorld world) =>
        world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor with { PlaceId = BoardIds.Market })
                .ToArray()),
        };

    private static DecisionRequest RequestFor(FirstBoardWorld world, string actorId)
    {
        string decisionId = $"test.{actorId}";
        FirstBoardWorld awaitingWorld = world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor.Key == actorId
                    ? actor with { AwaitingDecision = true, OpenDecisionId = decisionId }
                    : actor)
                .ToArray()),
        };
        var decisionEvent = new DomainEvent<BoardEventPayload>(
            new LogicalTimestamp(ModelTime.Zero, new Microstep(0)),
            EventCause.FromExternalInput(batchOrdinal: 0),
            BoardEventKinds.DecisionRequested,
            new DecisionRequestedEvent(actorId, DecisionNumber: 1, decisionId));
        return FirstBoardScenario.BuildRequest(
            awaitingWorld,
            decisionEvent,
            new WorldVersion(FirstBoardScenario.LineageId, eventCount: 1))!;
    }

    private static async Task<HostContentionCapture> RunHostContentionAsync(
        bool reverseDriverRegistration)
    {
        var alice = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));
        var bob = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
            request => Decide(request, new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal);
        if (reverseDriverRegistration)
        {
            drivers.Add(BoardIds.Bob, bob);
            drivers.Add(BoardIds.Alice, alice);
        }
        else
        {
            drivers.Add(BoardIds.Alice, alice);
            drivers.Add(BoardIds.Bob, bob);
        }

        BoardRunCapture run = await FirstBoardScenario.RunAsync(
            drivers,
            worldSeed: 73,
            ModelTime.Zero,
            BothActorsAtMarket(FirstBoardWorld.CreateInitial(worldSeed: 73)));
        DomainEvent<BoardEventPayload> contentionEvent = Assert.Single(
            run.Journal.Events,
            domainEvent => domainEvent.Kind == BoardEventKinds.ObjectContentionResolved);
        var contention = Assert.IsType<ObjectContentionResolvedEvent>(contentionEvent.Payload);
        return new HostContentionCapture(run, alice, bob, contention);
    }

    private sealed record ScriptedCapture(
        BoardRunCapture Run,
        RecordingPlayerDriver Alice,
        RecordingPlayerDriver Bob);

    private sealed record HostContentionCapture(
        BoardRunCapture Run,
        RecordingPlayerDriver Alice,
        RecordingPlayerDriver Bob,
        ObjectContentionResolvedEvent Contention);

    private sealed class AlwaysIllegalTravelPlayerDriver : IPlayerDriver
    {
        public int DecisionCount { get; private set; }

        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecisionCount = checked(DecisionCount + 1);
            return ValueTask.FromResult(Decide(
                request,
                new Intent(ActionKinds.Travel, DestinationId: "missing-place")));
        }
    }
}

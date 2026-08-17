using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Tests;

public sealed class FirstBoardTests
{
    [Fact]
    public async Task ScriptedGame_FindsSecret_ReplaysAndPreservesAsymmetricKnowledge()
    {
        ScriptedCapture capture = await RunDiscoveryScriptAsync();
        FirstBoardWorld world = capture.Run.Result.World;
        BoardActor alice = world.Actor(BoardIds.Alice);
        BoardActor bob = world.Actor(BoardIds.Bob);
        BoardObject brassKey = world.Object(BoardIds.BrassKey);

        Assert.Equal(StopReason.BoundaryReached, capture.Run.Result.StopReason);
        Assert.Equal(BoardIds.Cellar, alice.PlaceId);
        Assert.Equal(BoardIds.Tavern, bob.PlaceId);
        Assert.Equal(alice.Id, brassKey.OwnerActorId);
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
                "action.observe-requested",
                "actor.observed",
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
        Assert.Equal(BoardTiming.DeadlineTicks, rejectedEvent.Timestamp.ModelTime.Ticks);

        DecisionRequest postDeadlineRequest = alice.Requests[2];
        AvailableAction travel = Assert.Single(
            postDeadlineRequest.AvailableActions,
            action => action.ActionKind == ActionKinds.Travel);
        Assert.DoesNotContain(BoardIds.Cellar, travel.CandidateDestinationIds!);
        int sealedIndex = capture.Journal.Events.ToList().FindIndex(domainEvent =>
            domainEvent.Kind == BoardEventKinds.CellarSealed);
        int rejectedIndex = capture.Journal.Events.ToList().FindIndex(domainEvent =>
            domainEvent.Kind == BoardEventKinds.ActionRejected);
        Assert.True(sealedIndex >= 0 && sealedIndex < rejectedIndex);
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
    public void KeyContention_WinnerDoesNotDependOnActorRegistrationOrder()
    {
        FirstBoardWorld normal = BothActorsAtMarket(FirstBoardWorld.CreateInitial(worldSeed: 73));
        FirstBoardWorld reversed = normal with
        {
            Actors = Array.AsReadOnly(normal.Actors.Reverse().ToArray()),
        };

        ContentionCapture first = RunContention(normal, reverseSubmissions: false);
        ContentionCapture second = RunContention(reversed, reverseSubmissions: true);

        Assert.Equal(first.WinnerActorId, second.WinnerActorId);
        Assert.Equal(
            normal.Actor(first.WinnerActorId).Key,
            reversed.Actor(second.WinnerActorId).Key);
        Assert.Equal(first.Contention.Sample, second.Contention.Sample);
        Assert.Equal(
            normal.Actors.Select(actor => actor.Id).Order().ToArray(),
            first.Contention.CompetitorActorIds);
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
        Assert.Contains("actor.observed actor=alice facts=", history);
        Assert.Contains(BoardIds.ChestContainsLetter, history);
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
            request => Decide(request, new Intent(ActionKinds.Observe)),
            request => Decide(request, new Intent(ActionKinds.Wait, DurationMs: 5_000_000)),
        ]));
        var bob = new RecordingPlayerDriver(new ScriptedPlayerDriver(
        [
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
                [BoardIds.Alice] = new RandomPlayerDriver(seed),
                [BoardIds.Bob] = new RandomPlayerDriver(seed ^ 0x9E3779B97F4A7C15UL),
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
            _ => false,
        };

    private static FirstBoardWorld BothActorsAtMarket(FirstBoardWorld world) =>
        world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor with { PlaceId = BoardIds.Market })
                .ToArray()),
        };

    private static ContentionCapture RunContention(
        FirstBoardWorld initialWorld,
        bool reverseSubmissions)
    {
        UncommittedDomainEvent<BoardEventPayload>[] submissions =
        [
            FirstBoardScenario.ActionInput(
                BoardIds.Alice,
                "contention.alice",
                new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey)),
            FirstBoardScenario.ActionInput(
                BoardIds.Bob,
                "contention.bob",
                new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey)),
        ];
        if (reverseSubmissions)
        {
            Array.Reverse(submissions);
        }

        var reducer = new FirstBoardReducer();
        var journal = new InMemoryJournal<BoardEventPayload>();
        SimulationRunResult<FirstBoardWorld, BoardEventPayload> result =
            FirstBoardScenario.CreateLoop(reducer).Run(
                initialWorld,
                SimulationCursor.CreateInitial(FirstBoardScenario.LineageId, ModelTime.Zero),
                ModelTime.Zero,
                journal,
                submissions);
        DomainEvent<BoardEventPayload> contentionEvent = Assert.Single(
            journal.Events,
            domainEvent => domainEvent.Kind == BoardEventKinds.ObjectContentionResolved);
        var contention = Assert.IsType<ObjectContentionResolvedEvent>(contentionEvent.Payload);
        return new ContentionCapture(
            result.World.Object(BoardIds.BrassKey).OwnerActorId!.Value,
            contention);
    }

    private sealed record ScriptedCapture(
        BoardRunCapture Run,
        RecordingPlayerDriver Alice,
        RecordingPlayerDriver Bob);

    private sealed record ContentionCapture(
        long WinnerActorId,
        ObjectContentionResolvedEvent Contention);
}
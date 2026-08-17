using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.Host.Tests;

public sealed class PlayerDecisionSessionTests
{
    [Fact]
    public async Task RunUntilAsync_TwoScriptedDecisionsChangeWorldAndReplayWithoutDriver()
    {
        var driver = new ScriptedPlayerDriver(
        [
            request =>
            {
                AssertRequestVersion(request, eventCount: 1, modelTime: 10);
                return TravelTo(request, "left");
            },
            request =>
            {
                AssertRequestVersion(request, eventCount: 3, modelTime: 20);
                return TravelTo(request, "right");
            },
        ]);

        RunCapture first = await RunAsync(driver);
        RunCapture alternate = await RunAsync(new ScriptedPlayerDriver(
        [
            request => TravelTo(request, "right"),
            request => TravelTo(request, "left"),
        ]));
        var reducer = new TravelerReducer();
        TravelerWorld replayed = first.Journal.Events.Aggregate(TravelerWorld.Initial, reducer.Apply);

        Assert.Equal(StopReason.Exhausted, first.Result.StopReason);
        Assert.Equal(2, first.Result.DecisionCount);
        Assert.Equal(new WorldVersion(77, 5), first.Result.Version);
        Assert.Equal("endpoint.left.right", first.Result.World.Location);
        Assert.Equal("endpoint.right.left", alternate.Result.World.Location);
        Assert.NotEqual(first.Result.World, alternate.Result.World);
        Assert.Equal(first.Result.World, replayed);
        Assert.Equal(
            [
                "traveler.reached-fork",
                "traveler.direction-chosen",
                "traveler.reached-fork",
                "traveler.direction-chosen",
                "traveler.arrived",
            ],
            first.Journal.Events.Select(domainEvent => domainEvent.Kind.Id));
        Assert.Equal(
            [(10L, 0), (10L, 1), (20L, 0), (20L, 1), (30L, 0)],
            first.Journal.Events.Select(domainEvent =>
                (domainEvent.Timestamp.ModelTime.Ticks, domainEvent.Timestamp.Microstep.Value)));
    }

    [Fact]
    public async Task RunUntilAsync_RandomDriverSameSeedProducesEqualEventHistory()
    {
        RunCapture first = await RunAsync(new RandomPlayerDriver(12345));
        RunCapture second = await RunAsync(new RandomPlayerDriver(12345));

        Assert.Equal(first.Result.World, second.Result.World);
        Assert.Equal(Snapshots(first.Journal), Snapshots(second.Journal));
    }

    [Fact]
    public async Task RunUntilAsync_NullDriverWaitsAtBothForksAndReachesDefaultEndpoint()
    {
        RunCapture capture = await RunAsync(new NullPlayerDriver());

        Assert.Equal("endpoint.default", capture.Result.World.Location);
        Assert.Equal(2, capture.Result.DecisionCount);
        Assert.Equal(2, capture.Journal.Events.Count(domainEvent =>
            domainEvent.Kind.Id == TravelerEventKinds.Waited.Id));
    }

    [Fact]
    public async Task RunUntilAsync_WrongDecisionIdThrows()
    {
        var driver = new ScriptedPlayerDriver(
        [
            request => new PlayerDecision(
                new DecisionId("decision.wrong"),
                request.BasedOnWorldVersion,
                request.LineageId,
                new Intent(ActionKinds.Wait)),
        ]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await RunAsync(driver));

        Assert.Contains("DecisionId", exception.Message);
    }

    [Fact]
    public async Task RunUntilAsync_StaleEventCountThrows()
    {
        var driver = new ScriptedPlayerDriver(
        [
            request => new PlayerDecision(
                request.DecisionId,
                request.BasedOnWorldVersion + 1,
                request.LineageId,
                new Intent(ActionKinds.Wait)),
        ]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await RunAsync(driver));

        Assert.Contains("stale world version", exception.Message);
    }

    [Fact]
    public async Task RunUntilAsync_WrongLineageThrows()
    {
        var driver = new ScriptedPlayerDriver(
        [
            request => new PlayerDecision(
                request.DecisionId,
                request.BasedOnWorldVersion,
                request.LineageId + 1,
                new Intent(ActionKinds.Wait)),
        ]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await RunAsync(driver));

        Assert.Contains("different world lineage", exception.Message);
    }

    [Fact]
    public async Task RunUntilAsync_SecondSameBatchRequestUsesWorldAfterFirstDecision()
    {
        var driver = new ScriptedPlayerDriver(
        [
            request =>
            {
                Assert.Equal("decision.a", request.DecisionId.Value);
                Assert.Equal(2, request.BasedOnWorldVersion);
                Assert.Equal("value.0", request.Observation.LocationId);
                return Wait(request);
            },
            request =>
            {
                Assert.Equal("decision.b", request.DecisionId.Value);
                Assert.Equal(3, request.BasedOnWorldVersion);
                Assert.Equal("value.5", request.Observation.LocationId);
                return Wait(request);
            },
        ]);

        QueuedRunCapture capture = await RunQueuedAsync(
            driver,
            QueuedDecisionScenario.BuildRequest,
            (decision, _) => QueuedDecisionScenario.Apply(
                decision,
                decision.DecisionId.Value == "decision.a" ? 5 : 7));

        Assert.Equal(12, capture.Result.World.Value);
        Assert.Equal(2, capture.Result.DecisionCount);
        Assert.Equal(0, capture.Result.SkippedDecisionCount);
        Assert.Equal(new WorldVersion(88, 4), capture.Result.Version);
    }

    [Fact]
    public async Task RunUntilAsync_NullRequestSkipsDriverAndCountsInvalidatedDecision()
    {
        var driver = new ScriptedPlayerDriver([request => Wait(request)]);

        QueuedRunCapture capture = await RunQueuedAsync(
            driver,
            (world, decisionEvent, version) =>
                decisionEvent.Payload.DecisionId == "decision.b" && world.Value == 5
                    ? null
                    : QueuedDecisionScenario.BuildRequest(world, decisionEvent, version),
            (decision, _) => QueuedDecisionScenario.Apply(decision, delta: 5));

        Assert.Equal(1, capture.Result.DecisionCount);
        Assert.Equal(1, capture.Result.SkippedDecisionCount);
        Assert.Equal(5, capture.Result.World.Value);
        Assert.Equal(3, capture.Journal.Events.Count);
    }

    [Fact]
    public async Task RunUntilAsync_NewDecisionRequestsAppendBehindExistingQueue()
    {
        var requestOrder = new List<string>();
        var driver = new ScriptedPlayerDriver(
        [
            request => RecordAndWait(requestOrder, request),
            request => RecordAndWait(requestOrder, request),
            request => RecordAndWait(requestOrder, request),
        ]);

        QueuedRunCapture capture = await RunQueuedAsync(
            driver,
            QueuedDecisionScenario.BuildRequest,
            (decision, _) => QueuedDecisionScenario.Apply(
                decision,
                delta: decision.DecisionId.Value switch
                {
                    "decision.a" => 1,
                    "decision.b" => 2,
                    "decision.c" => 4,
                    _ => throw new InvalidOperationException(),
                },
                opensFollowUp: decision.DecisionId.Value == "decision.a"));

        Assert.Equal(["decision.a", "decision.b", "decision.c"], requestOrder);
        Assert.Equal("decision.a,decision.b,decision.c", capture.Result.World.AppliedOrder);
        Assert.Equal(7, capture.Result.World.Value);
        Assert.Equal(3, capture.Result.DecisionCount);
        Assert.Equal(0, capture.Result.SkippedDecisionCount);
    }

    [Fact]
    public async Task RunUntilAsync_EmptyTranslationProducesNoEventAndContinuesQueue()
    {
        var driver = new ScriptedPlayerDriver(
        [
            request => Wait(request),
            request =>
            {
                Assert.Equal("decision.b", request.DecisionId.Value);
                Assert.Equal(2, request.BasedOnWorldVersion);
                Assert.Equal("value.0", request.Observation.LocationId);
                return Wait(request);
            },
        ]);

        QueuedRunCapture capture = await RunQueuedAsync(
            driver,
            QueuedDecisionScenario.BuildRequest,
            (decision, _) => decision.DecisionId.Value == "decision.a"
                ? []
                : QueuedDecisionScenario.Apply(decision, delta: 2));

        Assert.Equal(2, capture.Result.DecisionCount);
        Assert.Equal(0, capture.Result.SkippedDecisionCount);
        Assert.Equal(2, capture.Result.World.Value);
        Assert.Equal(3, capture.Journal.Events.Count);
        Assert.Single(capture.Journal.Events, domainEvent =>
            domainEvent.Kind.Id == QueuedDecisionEventKinds.DecisionApplied.Id);
    }

    [Fact]
    public async Task RunUntilAsync_IndividuallySubmittedJournalReplaysByPureFold()
    {
        var driver = new ScriptedPlayerDriver([Wait, Wait]);
        QueuedRunCapture capture = await RunQueuedAsync(
            driver,
            QueuedDecisionScenario.BuildRequest,
            (decision, _) => QueuedDecisionScenario.Apply(
                decision,
                decision.DecisionId.Value == "decision.a" ? 3 : 4));
        var reducer = new QueuedDecisionReducer();

        QueuedDecisionWorld replayed = capture.Journal.Events.Aggregate(
            QueuedDecisionWorld.Initial,
            reducer.Apply);

        Assert.Equal(capture.Result.World, replayed);
        Assert.Equal(
            [
                "queue.decision-requested",
                "queue.decision-requested",
                "queue.decision-applied",
                "queue.decision-applied",
            ],
            capture.Journal.Events.Select(domainEvent => domainEvent.Kind.Id));
    }

    [Fact]
    public async Task RunUntilAsync_BoundaryReachedStillSubmitsRemainingQueuedDecisionAtCursorNow()
    {
        var driver = new ScriptedPlayerDriver(
        [
            request => Wait(request),
            request =>
            {
                Assert.Equal("decision.b", request.DecisionId.Value);
                Assert.Equal(3, request.BasedOnWorldVersion);
                Assert.Equal("value.1", request.Observation.LocationId);
                return Wait(request);
            },
        ]);

        QueuedRunCapture capture = await RunQueuedAsync(
            driver,
            QueuedDecisionScenario.BuildRequest,
            (decision, _) => QueuedDecisionScenario.Apply(decision, delta: 1),
            until: new ModelTime(10),
            forecastFutureWork: true);

        Assert.Equal(StopReason.BoundaryReached, capture.Result.StopReason);
        Assert.Equal(new ModelTime(10), capture.Result.Cursor.Now);
        Assert.Equal(2, capture.Result.DecisionCount);
        Assert.Equal(0, capture.Result.SkippedDecisionCount);
        Assert.Equal([0, 1, 2, 3], capture.Journal.Events.Select(domainEvent =>
            domainEvent.Timestamp.Microstep.Value));
    }

    private static async Task<RunCapture> RunAsync(IPlayerDriver driver)
    {
        var reducer = new TravelerReducer();
        var loop = new SimulationLoop<TravelerWorld, TravelerCandidate, TravelerEvent>(
            [new TravelerSystem()],
            reducer,
            decisionRequestPredicate: domainEvent => domainEvent.Kind.Id == TravelerEventKinds.ReachedFork.Id);
        var journal = new InMemoryJournal<TravelerEvent>();
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [TravelerScenario.ActorId] = driver,
        };
        var session = new PlayerDecisionSession<TravelerWorld, TravelerCandidate, TravelerEvent>(
            loop,
            journal,
            TravelerWorld.Initial,
            SimulationCursor.CreateInitial(77, ModelTime.Zero),
            TravelerScenario.SelectActor,
            drivers,
            TravelerScenario.BuildRequest,
            TravelerScenario.TranslateDecision);

        PlayerDecisionSessionResult<TravelerWorld> result = await session.RunUntilAsync(
            new ModelTime(100),
            CancellationToken.None);
        return new RunCapture(result, journal);
    }

    private static async Task<QueuedRunCapture> RunQueuedAsync(
        IPlayerDriver driver,
        Func<QueuedDecisionWorld, DomainEvent<QueuedDecisionEvent>, WorldVersion, DecisionRequest?> requestBuilder,
        Func<PlayerDecision, QueuedDecisionWorld, IReadOnlyList<UncommittedDomainEvent<QueuedDecisionEvent>>> translator,
        ModelTime? until = null,
        bool forecastFutureWork = false)
    {
        var reducer = new QueuedDecisionReducer();
        var loop = new SimulationLoop<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent>(
            [new QueuedDecisionSystem(forecastFutureWork)],
            reducer,
            decisionRequestPredicate: domainEvent =>
                domainEvent.Kind.Id == QueuedDecisionEventKinds.DecisionRequested.Id);
        var journal = new InMemoryJournal<QueuedDecisionEvent>();
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            ["actor.a"] = driver,
            ["actor.b"] = driver,
            ["actor.c"] = driver,
        };
        var session = new PlayerDecisionSession<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent>(
            loop,
            journal,
            QueuedDecisionWorld.Initial,
            SimulationCursor.CreateInitial(88, ModelTime.Zero),
            QueuedDecisionScenario.SelectActor,
            drivers,
            requestBuilder,
            translator);

        PlayerDecisionSessionResult<QueuedDecisionWorld> result = await session.RunUntilAsync(
            until ?? new ModelTime(100),
            CancellationToken.None);
        return new QueuedRunCapture(result, journal);
    }

    private static PlayerDecision TravelTo(DecisionRequest request, string destination) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            new Intent(ActionKinds.Travel, DestinationId: destination));

    private static PlayerDecision Wait(DecisionRequest request) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            new Intent(ActionKinds.Wait));

    private static PlayerDecision RecordAndWait(
        ICollection<string> requestOrder,
        DecisionRequest request)
    {
        requestOrder.Add(request.DecisionId.Value);
        return Wait(request);
    }

    private static void AssertRequestVersion(DecisionRequest request, long eventCount, long modelTime)
    {
        Assert.Equal(eventCount, request.BasedOnWorldVersion);
        Assert.Equal(77, request.LineageId);
        Assert.Equal(modelTime, request.ModelTimeMs);
        Assert.Equal(0, request.Microstep);
        Assert.Equal(request.ModelTimeMs, request.Observation.ModelTimeMs);
        Assert.Equal(request.Microstep, request.Observation.Microstep);
    }

    private static EventSnapshot[] Snapshots(InMemoryJournal<TravelerEvent> journal) =>
        [.. journal.Events.Select(domainEvent => new EventSnapshot(
            domainEvent.Timestamp.ModelTime.Ticks,
            domainEvent.Timestamp.Microstep.Value,
            domainEvent.Kind.Id,
            domainEvent.Payload))];

    private sealed record RunCapture(
        PlayerDecisionSessionResult<TravelerWorld> Result,
        InMemoryJournal<TravelerEvent> Journal);

    private sealed record QueuedRunCapture(
        PlayerDecisionSessionResult<QueuedDecisionWorld> Result,
        InMemoryJournal<QueuedDecisionEvent> Journal);

    private sealed record EventSnapshot(
        long ModelTime,
        int Microstep,
        string Kind,
        TravelerEvent Payload);
}

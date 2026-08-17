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
            WrongDecisionId,
            WrongDecisionId,
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
            StaleDecision,
            StaleDecision,
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
            WrongLineageDecision,
            WrongLineageDecision,
        ]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await RunAsync(driver));

        Assert.Contains("different world lineage", exception.Message);
    }

    [Fact]
    public async Task RunUntilAsync_ConcurrentSecondCallThrows()
    {
        var driver = new BlockingPlayerDriver();
        var journal = new InMemoryJournal<TravelerEvent>();
        PlayerDecisionSession<TravelerWorld, TravelerCandidate, TravelerEvent> session =
            CreateTravelerSession(driver, journal);
        Task<PlayerDecisionSessionResult<TravelerWorld>> firstRun = session.RunUntilAsync(
            new ModelTime(100),
            CancellationToken.None).AsTask();
        await driver.WaitUntilEnteredAsync();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            session.RunUntilAsync(new ModelTime(100), CancellationToken.None));

        Assert.Contains("only one in-flight", exception.Message);
        driver.Release();
        await firstRun;
    }

    [Fact]
    public async Task RunUntilAsync_SameBatchRequestsUseSameWorldAndSubmitOneExternalBatch()
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
                Assert.Equal(2, request.BasedOnWorldVersion);
                Assert.Equal("value.0", request.Observation.LocationId);
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
        Assert.All(
            capture.Journal.Events.Skip(2),
            domainEvent => Assert.Equal(EventCause.FromExternalInput(batchOrdinal: 1), domainEvent.Cause));
        Assert.Equal([0, 1, 2, 3], capture.Journal.Events.Select(domainEvent =>
            domainEvent.Timestamp.Microstep.Value));
    }

    [Fact]
    public async Task RunUntilAsync_NullRequestSkipsDriverAndCountsInvalidatedDecision()
    {
        var driver = new ScriptedPlayerDriver([request => Wait(request)]);

        QueuedRunCapture capture = await RunQueuedAsync(
            driver,
            (world, decisionEvent, version) =>
                decisionEvent.Payload.DecisionId == "decision.b"
                    ? null
                    : QueuedDecisionScenario.BuildRequest(world, decisionEvent, version),
            (decision, _) => QueuedDecisionScenario.Apply(decision, delta: 5));

        Assert.Equal(1, capture.Result.DecisionCount);
        Assert.Equal(1, capture.Result.SkippedDecisionCount);
        Assert.Equal(1, capture.Result.InvalidatedDecisionCount);
        Assert.Equal(0, capture.Result.PendingDecisionCount);
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
    public async Task RunUntilAsync_DecisionInputThatIsAnotherRequest_IsQueuedAndHandled()
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
            (decision, _) => decision.DecisionId.Value switch
            {
                "decision.a" => [QueuedDecisionScenario.Request("actor.c", "decision.c")],
                "decision.b" => QueuedDecisionScenario.Apply(decision, delta: 2),
                "decision.c" => QueuedDecisionScenario.Apply(decision, delta: 4),
                _ => throw new InvalidOperationException(),
            });

        Assert.Equal(["decision.a", "decision.b", "decision.c"], requestOrder);
        Assert.Equal("decision.b,decision.c", capture.Result.World.AppliedOrder);
        Assert.Equal(6, capture.Result.World.Value);
        Assert.Equal(3, capture.Result.DecisionCount);
        Assert.Equal(3, capture.Journal.Events.Count(domainEvent =>
            domainEvent.Kind == QueuedDecisionEventKinds.DecisionRequested));
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
    public async Task RunUntilAsync_SimultaneousBatchSubmitsBeforeReturningAtBoundary()
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

    [Fact]
    public async Task RunUntilAsync_CancellationRetainsAnsweredAndOpenRequestsThenResumesBatch()
    {
        var driver = new CancelThenResumeDriver();
        (PlayerDecisionSession<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent> session,
            InMemoryJournal<QueuedDecisionEvent> journal) = CreateQueuedSession(
                driver,
                QueuedDecisionScenario.BuildRequest,
                (decision, _) => QueuedDecisionScenario.Apply(
                    decision,
                    decision.DecisionId.Value == "decision.a" ? 5 : 7));
        using var cancellation = new CancellationTokenSource();
        Task<PlayerDecisionSessionResult<QueuedDecisionWorld>> interrupted = session.RunUntilAsync(
            new ModelTime(100),
            cancellation.Token).AsTask();
        await driver.WaitUntilBlockedAsync();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await interrupted);

        Assert.Equal(
            [PendingDecisionStatus.Answered, PendingDecisionStatus.Open],
            session.PendingDecisions.Select(pending => pending.Status));
        Assert.Equal(["decision.a", "decision.b"], session.PendingDecisions.Select(pending =>
            pending.RequestEvent.Payload.DecisionId));
        Assert.Equal(0, session.PendingDecisions[0].RequestEvent.Timestamp.ModelTime.Ticks - 10);
        Assert.Equal(2, journal.Events.Count);

        PlayerDecisionSessionResult<QueuedDecisionWorld> resumed = await session.RunUntilAsync(
            new ModelTime(100),
            CancellationToken.None);

        Assert.Equal(12, resumed.World.Value);
        Assert.Equal(2, resumed.DecisionCount);
        Assert.Equal(0, resumed.PendingDecisionCount);
        Assert.Empty(session.PendingDecisions);
        Assert.Equal(1, driver.ActorACallCount);
        Assert.Equal(2, driver.ActorBCallCount);
    }

    [Fact]
    public async Task RunUntilAsync_StaleRequestIsQueryableAsInvalidatedWhilePeerRemainsOpen()
    {
        var driver = new BlockingPlayerDriver();
        (PlayerDecisionSession<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent> session, _) =
            CreateQueuedSession(
                driver,
                (world, decisionEvent, version) =>
                    decisionEvent.Payload.DecisionId == "decision.a"
                        ? null
                        : QueuedDecisionScenario.BuildRequest(world, decisionEvent, version),
                (decision, _) => QueuedDecisionScenario.Apply(decision, delta: 1));
        using var cancellation = new CancellationTokenSource();
        Task<PlayerDecisionSessionResult<QueuedDecisionWorld>> interrupted = session.RunUntilAsync(
            new ModelTime(100),
            cancellation.Token).AsTask();
        await driver.WaitUntilEnteredAsync();

        Assert.Equal(PendingDecisionStatus.Invalidated, session.PendingDecisions[0].Status);
        Assert.Equal(
            PendingDecisionInvalidationReason.StaleRequest,
            session.PendingDecisions[0].InvalidationReason);
        Assert.Equal(PendingDecisionStatus.Open, session.PendingDecisions[1].Status);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await interrupted);

        driver.Release();
        PlayerDecisionSessionResult<QueuedDecisionWorld> resumed = await session.RunUntilAsync(
            new ModelTime(100),
            CancellationToken.None);

        Assert.Equal(1, resumed.SkippedDecisionCount);
        Assert.Equal(1, resumed.InvalidatedDecisionCount);
        Assert.Equal(1, resumed.World.Value);
    }

    [Fact]
    public async Task RunUntilAsync_FirstValidationFailureInvalidatesAndReasksOnce()
    {
        var driver = new ScriptedPlayerDriver([WrongDecisionId, Wait, Wait]);

        RunCapture capture = await RunAsync(driver);

        Assert.Equal(2, capture.Result.DecisionCount);
        Assert.Equal(1, capture.Result.ValidationFailedDecisionCount);
        Assert.Equal(1, capture.Result.InvalidatedDecisionCount);
        Assert.Equal(StopReason.Exhausted, capture.Result.StopReason);
    }

    [Fact]
    public async Task RunUntilAsync_WholeRunAndSplitRunHaveIdenticalJournalEvents()
    {
        var wholeJournal = new InMemoryJournal<TravelerEvent>();
        PlayerDecisionSession<TravelerWorld, TravelerCandidate, TravelerEvent> wholeSession =
            CreateTravelerSession(new NullPlayerDriver(), wholeJournal);
        PlayerDecisionSessionResult<TravelerWorld> whole = await wholeSession.RunUntilAsync(
            new ModelTime(100),
            CancellationToken.None);

        var splitJournal = new InMemoryJournal<TravelerEvent>();
        PlayerDecisionSession<TravelerWorld, TravelerCandidate, TravelerEvent> splitSession =
            CreateTravelerSession(new NullPlayerDriver(), splitJournal);
        PlayerDecisionSessionResult<TravelerWorld> firstHalf = await splitSession.RunUntilAsync(
            new ModelTime(15),
            CancellationToken.None);
        PlayerDecisionSessionResult<TravelerWorld> split = await splitSession.RunUntilAsync(
            new ModelTime(100),
            CancellationToken.None);

        Assert.Equal(StopReason.BoundaryReached, firstHalf.StopReason);
        Assert.Equal(whole.World, split.World);
        Assert.Equal(DetailedSnapshots(wholeJournal), DetailedSnapshots(splitJournal));
    }

    [Fact]
    public async Task RunUntilAsync_SameActorTwiceInBatchRetainsWholeBatchAndAlwaysThrows()
    {
        var driver = new CountingPlayerDriver();
        (PlayerDecisionSession<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent> session, _) =
            CreateQueuedSession(
                driver,
                QueuedDecisionScenario.BuildRequest,
                (decision, _) => QueuedDecisionScenario.Apply(decision, delta: 1),
                duplicateActorInInitialBatch: true);

        InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.RunUntilAsync(new ModelTime(100), CancellationToken.None));
        InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.RunUntilAsync(new ModelTime(100), CancellationToken.None));

        Assert.Contains("more than one request", first.Message);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(2, session.PendingDecisions.Count);
        Assert.All(session.PendingDecisions, pending =>
            Assert.Equal(PendingDecisionStatus.Open, pending.Status));
        Assert.Equal(0, driver.CallCount);
    }

    private static async Task<RunCapture> RunAsync(IPlayerDriver driver)
    {
        var journal = new InMemoryJournal<TravelerEvent>();
        PlayerDecisionSession<TravelerWorld, TravelerCandidate, TravelerEvent> session =
            CreateTravelerSession(driver, journal);

        PlayerDecisionSessionResult<TravelerWorld> result = await session.RunUntilAsync(
            new ModelTime(100),
            CancellationToken.None);
        return new RunCapture(result, journal);
    }

    private static PlayerDecisionSession<TravelerWorld, TravelerCandidate, TravelerEvent> CreateTravelerSession(
        IPlayerDriver driver,
        InMemoryJournal<TravelerEvent> journal)
    {
        var reducer = new TravelerReducer();
        var loop = new SimulationLoop<TravelerWorld, TravelerCandidate, TravelerEvent>(
            [new TravelerSystem()],
            reducer,
            decisionRequestPredicate: domainEvent => domainEvent.Kind.Id == TravelerEventKinds.ReachedFork.Id);
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [TravelerScenario.ActorId] = driver,
        };
        return new PlayerDecisionSession<TravelerWorld, TravelerCandidate, TravelerEvent>(
            loop,
            journal,
            TravelerWorld.Initial,
            SimulationCursor.CreateInitial(77, ModelTime.Zero),
            TravelerScenario.SelectActor,
            drivers,
            TravelerScenario.BuildRequest,
            TravelerScenario.TranslateDecision);
    }

    private static async Task<QueuedRunCapture> RunQueuedAsync(
        IPlayerDriver driver,
        Func<QueuedDecisionWorld, DomainEvent<QueuedDecisionEvent>, WorldVersion, DecisionRequest?> requestBuilder,
        Func<PlayerDecision, QueuedDecisionWorld, IReadOnlyList<UncommittedDomainEvent<QueuedDecisionEvent>>> translator,
        ModelTime? until = null,
        bool forecastFutureWork = false)
    {
        (PlayerDecisionSession<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent> session,
            InMemoryJournal<QueuedDecisionEvent> journal) = CreateQueuedSession(
                driver,
                requestBuilder,
                translator,
                forecastFutureWork);

        PlayerDecisionSessionResult<QueuedDecisionWorld> result = await session.RunUntilAsync(
            until ?? new ModelTime(100),
            CancellationToken.None);
        return new QueuedRunCapture(result, journal);
    }

    private static (
        PlayerDecisionSession<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent> Session,
        InMemoryJournal<QueuedDecisionEvent> Journal) CreateQueuedSession(
            IPlayerDriver driver,
            Func<QueuedDecisionWorld, DomainEvent<QueuedDecisionEvent>, WorldVersion, DecisionRequest?> requestBuilder,
            Func<PlayerDecision, QueuedDecisionWorld, IReadOnlyList<UncommittedDomainEvent<QueuedDecisionEvent>>> translator,
            bool forecastFutureWork = false,
            bool duplicateActorInInitialBatch = false)
    {
        var reducer = new QueuedDecisionReducer();
        var loop = new SimulationLoop<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent>(
            [new QueuedDecisionSystem(forecastFutureWork, duplicateActorInInitialBatch)],
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
        return (session, journal);
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

    private static PlayerDecision WrongDecisionId(DecisionRequest request) =>
        new(
            new DecisionId("decision.wrong"),
            request.BasedOnWorldVersion,
            request.LineageId,
            new Intent(ActionKinds.Wait));

    private static PlayerDecision StaleDecision(DecisionRequest request) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion + 1,
            request.LineageId,
            new Intent(ActionKinds.Wait));

    private static PlayerDecision WrongLineageDecision(DecisionRequest request) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId + 1,
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

    private static DetailedEventSnapshot[] DetailedSnapshots(InMemoryJournal<TravelerEvent> journal) =>
        [.. journal.Events.Select(domainEvent => new DetailedEventSnapshot(
            domainEvent.Timestamp.ModelTime.Ticks,
            domainEvent.Timestamp.Microstep.Value,
            domainEvent.Cause,
            domainEvent.Kind,
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

    private sealed record DetailedEventSnapshot(
        long ModelTime,
        int Microstep,
        EventCause Cause,
        EventKind Kind,
        TravelerEvent Payload);

    private sealed class CancelThenResumeDriver : IPlayerDriver
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActorACallCount { get; private set; }

        public int ActorBCallCount { get; private set; }

        public async ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            if (request.ActorId == "actor.a")
            {
                ActorACallCount = checked(ActorACallCount + 1);
                return Wait(request);
            }

            ActorBCallCount = checked(ActorBCallCount + 1);
            if (ActorBCallCount == 1)
            {
                _blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Wait(request);
        }

        public Task WaitUntilBlockedAsync() => _blocked.Task;
    }

    private sealed class CountingPlayerDriver : IPlayerDriver
    {
        public int CallCount { get; private set; }

        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount = checked(CallCount + 1);
            return ValueTask.FromResult(Wait(request));
        }
    }

    private sealed class BlockingPlayerDriver : IPlayerDriver
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return Wait(request);
        }

        public Task WaitUntilEnteredAsync() => _entered.Task;

        public void Release() => _release.TrySetResult();
    }
}

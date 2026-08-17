using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SimulationControlTests
{
    private static readonly EventKind ControlEventKind = new("test.control-event", 1);
    private static readonly EventKind CounterEventKind = new("test.counter-event", 1);

    [Fact]
    public void Run_EmptyForecast_ReturnsExhausted()
    {
        var loop = new SimulationLoop<int, string, ControlEvent>([], new ControlReducer());
        var journal = new InMemoryJournal<ControlEvent>();
        SimulationCursor initialCursor = Cursor(lineageId: 42, now: 5);

        SimulationRunResult<int, ControlEvent> result = loop.Run(
            7,
            initialCursor,
            new ModelTime(100),
            journal);

        Assert.Equal(StopReason.Exhausted, result.StopReason);
        Assert.Equal(initialCursor, result.Cursor);
        Assert.Empty(result.DecisionEvents);
        Assert.Equal(new WorldVersion(42, 0), result.Version);
    }

    [Fact]
    public void Run_NextCandidateAfterUntil_ReturnsBoundaryReached()
    {
        var system = new BatchSystem(
            sourceId: 7,
            candidateId: 1,
            due: new ModelTime(10),
            blockFlag: 1,
            new ControlEvent("late", 1, RequiresDecision: false));
        var loop = new SimulationLoop<int, string, ControlEvent>([system], new ControlReducer());
        var journal = new InMemoryJournal<ControlEvent>();

        SimulationRunResult<int, ControlEvent> result = loop.Run(0, Cursor(), new ModelTime(9), journal);

        Assert.Equal(StopReason.BoundaryReached, result.StopReason);
        Assert.Equal(ModelTime.Zero, result.Cursor.Now);
        Assert.Empty(result.DecisionEvents);
        Assert.Empty(journal.Events);
    }

    [Fact]
    public void Run_DecisionEvent_CommitsBatchThenReturnsDecisionRequired()
    {
        var system = new BatchSystem(
            sourceId: 7,
            candidateId: 1,
            due: new ModelTime(5),
            blockFlag: 1,
            new ControlEvent("request", 1, RequiresDecision: true));
        var loop = DecisionLoop([system]);
        var journal = new InMemoryJournal<ControlEvent>();
        SimulationCursor initialCursor = Cursor(lineageId: 42);

        SimulationRunResult<int, ControlEvent> result = loop.Run(
            0,
            initialCursor,
            new ModelTime(20),
            journal);

        DomainEvent<ControlEvent> decision = Assert.Single(result.DecisionEvents);
        Assert.Equal(StopReason.DecisionRequired, result.StopReason);
        Assert.Same(journal.Events[0], decision);
        Assert.Equal(1, result.World);
        Assert.Equal(new WorldVersion(42, 1), result.Version);
        Assert.Equal(0, initialCursor.ResolveCountAtCurrentTime);
        Assert.Equal(1, result.Cursor.ResolveCountAtCurrentTime);
    }

    [Fact]
    public void Run_MultiEventBatch_ReturnsEveryMatchingDecisionAfterCommittingWholeBatch()
    {
        var system = new BatchSystem(
            sourceId: 7,
            candidateId: 1,
            due: new ModelTime(5),
            blockFlag: 15,
            new ControlEvent("before", 1, RequiresDecision: false),
            new ControlEvent("first-request", 2, RequiresDecision: true),
            new ControlEvent("between", 4, RequiresDecision: false),
            new ControlEvent("second-request", 8, RequiresDecision: true));
        var loop = DecisionLoop([system]);
        var journal = new InMemoryJournal<ControlEvent>();

        SimulationRunResult<int, ControlEvent> result = loop.Run(0, Cursor(), new ModelTime(20), journal);

        Assert.Equal(15, result.World);
        Assert.Equal(4, journal.Events.Count);
        Assert.Equal(
            ["first-request", "second-request"],
            result.DecisionEvents.Select(domainEvent => domainEvent.Payload.Name));
        Assert.Equal([0, 1, 2, 3], journal.Events.Select(domainEvent => domainEvent.Timestamp.Microstep.Value));
        EventCause expectedCause = EventCause.FromResolve(
            sourceId: 7,
            new EventCandidateId(1),
            new ModelTime(5),
            batchOrdinal: 0);
        Assert.All(journal.Events, domainEvent => Assert.Equal(expectedCause, domainEvent.Cause));
    }

    [Fact]
    public void Run_SameTimeCandidates_StopsAfterDecisionBatchAndResumesWithNextCandidate()
    {
        BatchSystem[] systems =
        [
            new(
                sourceId: 1,
                candidateId: 1,
                due: new ModelTime(5),
                blockFlag: 1,
                new ControlEvent("request", 1, RequiresDecision: true)),
            new(
                sourceId: 2,
                candidateId: 1,
                due: new ModelTime(5),
                blockFlag: 2,
                new ControlEvent("follow-up", 2, RequiresDecision: false)),
        ];
        var loop = DecisionLoop(systems);
        var journal = new InMemoryJournal<ControlEvent>();

        SimulationRunResult<int, ControlEvent> first = loop.Run(0, Cursor(), new ModelTime(20), journal);
        SimulationRunResult<int, ControlEvent> second = loop.Run(
            first.World,
            first.Cursor,
            new ModelTime(20),
            journal);

        Assert.Equal(StopReason.DecisionRequired, first.StopReason);
        Assert.Equal(StopReason.Exhausted, second.StopReason);
        Assert.Equal(["request", "follow-up"], journal.Events.Select(domainEvent => domainEvent.Payload.Name));
        Assert.Equal([0, 1], journal.Events.Select(domainEvent => domainEvent.Timestamp.Microstep.Value));
        Assert.Equal([0L, 1L], journal.Events.Select(domainEvent => domainEvent.Cause.BatchOrdinal));
        Assert.Equal(2, second.Cursor.NextBatchOrdinal);
        Assert.Equal(3, second.World);
    }

    [Fact]
    public void Run_ExternalInputs_MatchingDecisionPredicateCommitWholeBatchAndStop()
    {
        var reducer = new ControlReducer();
        var loop = new SimulationLoop<int, string, ControlEvent>(
            [],
            reducer,
            decisionRequestPredicate: _ => true);
        var journal = new InMemoryJournal<ControlEvent>();
        journal.Append(new DomainEvent<ControlEvent>(
            new LogicalTimestamp(new ModelTime(5), new Microstep(2)),
            EventCause.FromExternalInput(batchOrdinal: 0),
            ControlEventKind,
            new ControlEvent("prefix", 1, RequiresDecision: false)));

        SimulationRunResult<int, ControlEvent> result = loop.Run(
            initialWorld: 1,
            cursor: SimulationCursor.CreateFork(lineageId: 1, new ModelTime(5), nextBatchOrdinal: 1),
            until: new ModelTime(5),
            journal,
            externalInputs:
            [
                Event("input-a", stateFlag: 2, requiresDecision: true),
                Event("input-b", stateFlag: 4, requiresDecision: true),
            ]);
        int replayed = ReplayHarness.Replay(0, journal.Events, reducer);

        Assert.Equal(StopReason.DecisionRequired, result.StopReason);
        Assert.Equal(
            ["input-a", "input-b"],
            result.DecisionEvents.Select(domainEvent => domainEvent.Payload.Name));
        Assert.Equal([2, 3, 4], journal.Events.Select(domainEvent => domainEvent.Timestamp.Microstep.Value));
        Assert.All(
            journal.Events.Skip(1),
            domainEvent => Assert.Equal(EventCause.FromExternalInput(batchOrdinal: 1), domainEvent.Cause));
        Assert.Equal(7, result.World);
        Assert.Equal(result.World, replayed);
        Assert.Equal(new WorldVersion(1, 3), result.Version);
        Assert.Equal(0, result.TimeAdvanceCount);
        Assert.Equal(0, result.ResolvedCandidateCount);
    }

    [Fact]
    public void Run_DecisionStopsAcrossRuns_DoNotResetSameTimeResolveBudget()
    {
        var loop = new SimulationLoop<int, string, string>(
            [new SameTimeCounterSystem()],
            new IncrementingReducer(),
            maxResolveCountPerModelTime: 3,
            decisionRequestPredicate: _ => true);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<int, string> first = loop.Run(0, Cursor(), new ModelTime(10), journal);
        SimulationRunResult<int, string> second = loop.Run(first.World, first.Cursor, new ModelTime(10), journal);
        SimulationRunResult<int, string> third = loop.Run(second.World, second.Cursor, new ModelTime(10), journal);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            loop.Run(third.World, third.Cursor, new ModelTime(10), journal));

        Assert.Contains("Resolve budget", exception.Message);
        Assert.Equal(3, third.Cursor.ResolveCountAtCurrentTime);
        Assert.Equal(3, journal.Events.Count);
    }

    [Fact]
    public void Run_NoOpGuardSurvivesAnExhaustedRunBoundary()
    {
        var loop = new SimulationLoop<int, string, string>([new ReturningNoOpSystem()], new IdentityReducer());
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<int, string> first = loop.Run(0, Cursor(), new ModelTime(10), journal);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            loop.Run(first.World, first.Cursor, new ModelTime(10), journal));

        Assert.Equal(StopReason.Exhausted, first.StopReason);
        Assert.Contains("identical candidate remained next", exception.Message);
    }

    [Fact]
    public void Version_EqualEventCountsAcrossDifferentLineages_AreNotEqual()
    {
        var loop = new SimulationLoop<int, string, ControlEvent>([], new ControlReducer());
        var firstJournal = new InMemoryJournal<ControlEvent>();
        var secondJournal = new InMemoryJournal<ControlEvent>();

        WorldVersion first = loop.Run(0, Cursor(lineageId: 1), ModelTime.Zero, firstJournal).Version;
        WorldVersion second = loop.Run(0, Cursor(lineageId: 2), ModelTime.Zero, secondJournal).Version;

        Assert.Equal(0, first.EventCount);
        Assert.NotEqual(first, second);
    }

    private static SimulationLoop<int, string, ControlEvent> DecisionLoop(IEnumerable<BatchSystem> systems) =>
        new(systems, new ControlReducer(), decisionRequestPredicate: domainEvent => domainEvent.Payload.RequiresDecision);

    private static SimulationCursor Cursor(long lineageId = 1, long now = 0) =>
        SimulationCursor.CreateInitial(lineageId, new ModelTime(now));

    private static UncommittedDomainEvent<ControlEvent> Event(
        string name,
        int stateFlag,
        bool requiresDecision) =>
        new(ControlEventKind, new ControlEvent(name, stateFlag, requiresDecision));

    private sealed record ControlEvent(string Name, int StateFlag, bool RequiresDecision);

    private sealed class ControlReducer : IEventReducer<int, ControlEvent>
    {
        public int Apply(int world, DomainEvent<ControlEvent> domainEvent) =>
            domainEvent.Kind.Id == ControlEventKind.Id
                ? world | domainEvent.Payload.StateFlag
                : throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'.");
    }

    private sealed class BatchSystem : ISimSystem<int, string, ControlEvent>
    {
        private readonly long _sourceId;
        private readonly EventCandidateId _candidateId;
        private readonly ModelTime _due;
        private readonly int _blockFlag;
        private readonly IReadOnlyList<UncommittedDomainEvent<ControlEvent>> _events;

        public BatchSystem(
            long sourceId,
            long candidateId,
            ModelTime due,
            int blockFlag,
            params ControlEvent[] events)
        {
            _sourceId = sourceId;
            _candidateId = new EventCandidateId(candidateId);
            _due = due;
            _blockFlag = blockFlag;
            _events = [.. events.Select(controlEvent => new UncommittedDomainEvent<ControlEvent>(ControlEventKind, controlEvent))];
        }

        public IReadOnlyList<EventCandidate<string>> ForecastNext(int world, ModelTime now) =>
            (world & _blockFlag) == 0
                ? [new EventCandidate<string>(_candidateId, _due, _sourceId, "batch")]
                : [];

        public IReadOnlyList<UncommittedDomainEvent<ControlEvent>> Resolve(
            int world,
            EventCandidate<string> candidate) =>
            _events;
    }

    private sealed class SameTimeCounterSystem : ISimSystem<int, string, string>
    {
        public IReadOnlyList<EventCandidate<string>> ForecastNext(int world, ModelTime now) =>
            [new EventCandidate<string>(new EventCandidateId(world), now, sourceId: 1, "request")];

        public IReadOnlyList<UncommittedDomainEvent<string>> Resolve(
            int world,
            EventCandidate<string> candidate) =>
            [new UncommittedDomainEvent<string>(CounterEventKind, candidate.Payload)];
    }

    private sealed class ReturningNoOpSystem : ISimSystem<int, string, string>
    {
        private int _forecastCount;

        public IReadOnlyList<EventCandidate<string>> ForecastNext(int world, ModelTime now)
        {
            _forecastCount++;
            return _forecastCount == 2
                ? []
                : [new EventCandidate<string>(new EventCandidateId(1), now, sourceId: 1, "repeat")];
        }

        public IReadOnlyList<UncommittedDomainEvent<string>> Resolve(
            int world,
            EventCandidate<string> candidate) => [];
    }

    private sealed class IncrementingReducer : IEventReducer<int, string>
    {
        public int Apply(int world, DomainEvent<string> domainEvent) => checked(world + 1);
    }

    private sealed class IdentityReducer : IEventReducer<int, string>
    {
        public int Apply(int world, DomainEvent<string> domainEvent) => world;
    }
}

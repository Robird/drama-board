using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SameTimeSemanticsTests
{
    private static readonly ModelTime Due = new(10);
    private static readonly EventKind FlagResolved = new("test.flag-resolved", 1);

    [Fact]
    public void Run_SameTimeIndependentCandidates_UsesTupleTieBreakRegardlessOfInsertionOrder()
    {
        InMemoryJournal<FlagEvent> first = RunIndependent(reverseSystems: false);
        InMemoryJournal<FlagEvent> repeated = RunIndependent(reverseSystems: false);
        InMemoryJournal<FlagEvent> reversed = RunIndependent(reverseSystems: true);

        Assert.Equal(["A", "B"], first.Events.Select(domainEvent => domainEvent.Payload.Name));
        Assert.Equal([Due, Due], first.Events.Select(domainEvent => domainEvent.Timestamp.ModelTime));
        Assert.Equal([0, 1], first.Events.Select(domainEvent => domainEvent.Timestamp.Microstep.Value));
        Assert.Equal(Snapshot(first), Snapshot(repeated));
        Assert.Equal(Snapshot(first), Snapshot(reversed));
    }

    [Fact]
    public void Run_FirstSameTimeResolveSuppressesSecondForecast_SecondCandidateDoesNotOccur()
    {
        var first = new FlagSystem(
            sourceId: 1,
            candidateId: 1,
            payload: "A",
            resolvedFlag: 1,
            forecastBlockedByFlags: 1);
        var second = new FlagSystem(
            sourceId: 2,
            candidateId: 1,
            payload: "B",
            resolvedFlag: 2,
            forecastBlockedByFlags: 3);
        Assert.Single(second.ForecastNext(world: 0, ModelTime.Zero));

        var loop = new SimulationLoop<int, string, FlagEvent>([second, first], new FlagReducer());
        var journal = new InMemoryJournal<FlagEvent>();

        // WP5 locks same-time handling to sequential Resolve followed by a full re-Forecast after each event.
        SimulationRunResult<int> result = loop.Run(0, ModelTime.Zero, Due, journal);

        Assert.Equal(["A"], journal.Events.Select(domainEvent => domainEvent.Payload.Name));
        Assert.Equal(1, result.World);
        Assert.Equal(1, result.ResolvedCandidateCount);
    }

    private static InMemoryJournal<FlagEvent> RunIndependent(bool reverseSystems)
    {
        FlagSystem[] systems =
        [
            new(
                sourceId: 1,
                candidateId: 99,
                payload: "A",
                resolvedFlag: 1,
                forecastBlockedByFlags: 1),
            new(
                sourceId: 2,
                candidateId: 1,
                payload: "B",
                resolvedFlag: 2,
                forecastBlockedByFlags: 2),
        ];

        if (reverseSystems)
        {
            Array.Reverse(systems);
        }

        var loop = new SimulationLoop<int, string, FlagEvent>(systems, new FlagReducer());
        var journal = new InMemoryJournal<FlagEvent>();
        _ = loop.Run(0, ModelTime.Zero, Due, journal);

        return journal;
    }

    private static (LogicalTimestamp Timestamp, EventKind Kind, FlagEvent Payload)[] Snapshot(
        InMemoryJournal<FlagEvent> journal) =>
        [
            .. journal.Events.Select(
                domainEvent => (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
        ];

    private sealed record FlagEvent(string Name, int ResolvedFlag);

    private sealed class FlagReducer : IEventReducer<int, FlagEvent>
    {
        public int Apply(int world, DomainEvent<FlagEvent> domainEvent) =>
            domainEvent.Kind.Id == FlagResolved.Id
                ? world | domainEvent.Payload.ResolvedFlag
                : throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'.");
    }

    private sealed class FlagSystem : ISimSystem<int, string, FlagEvent>
    {
        private readonly long _sourceId;
        private readonly EventCandidateId _candidateId;
        private readonly string _payload;
        private readonly int _resolvedFlag;
        private readonly int _forecastBlockedByFlags;

        public FlagSystem(
            long sourceId,
            long candidateId,
            string payload,
            int resolvedFlag,
            int forecastBlockedByFlags)
        {
            _sourceId = sourceId;
            _candidateId = new EventCandidateId(candidateId);
            _payload = payload;
            _resolvedFlag = resolvedFlag;
            _forecastBlockedByFlags = forecastBlockedByFlags;
        }

        public IReadOnlyList<EventCandidate<string>> ForecastNext(int world, ModelTime now) =>
            (world & _forecastBlockedByFlags) == 0
                ? [new EventCandidate<string>(_candidateId, Due, _sourceId, 0, _payload)]
                : [];

        public IReadOnlyList<UncommittedDomainEvent<FlagEvent>> Resolve(
            int world,
            EventCandidate<string> candidate) =>
            [new UncommittedDomainEvent<FlagEvent>(FlagResolved, new FlagEvent(candidate.Payload, _resolvedFlag))];
    }
}

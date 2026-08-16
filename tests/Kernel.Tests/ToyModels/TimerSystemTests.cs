using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class TimerSystemTests
{
    [Fact]
    public void Run_ThreeIndependentTimers_CommitsInTimeOrderWithThreeJumps()
    {
        TimerSystem[] systems =
        [
            new("A", sourceId: 1, AtSecond(10)),
            new("B", sourceId: 2, AtSecond(20)),
            new("C", sourceId: 3, AtSecond(15)),
        ];
        var loop = new SimulationLoop<TimerWorld, string, string>(systems, new TimerReducer());
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<TimerWorld> result = loop.Run(
            new TimerWorld([]),
            initialTime: ModelTime.Zero,
            until: AtSecond(20),
            journal);

        Assert.Equal(["A", "C", "B"], journal.Events.Select(domainEvent => domainEvent.Payload));
        Assert.Equal([10_000L, 15_000L, 20_000L], journal.Events.Select(domainEvent => domainEvent.Timestamp.ModelTime.Ticks));
        Assert.Equal(3, result.TimeAdvanceCount);
        Assert.Equal(3, result.ResolvedCandidateCount);
        Assert.Equal(["A", "C", "B"], result.World.FiredTimers);
    }

    [Fact]
    public void Run_CommittedJournal_ReplaysToFinalWorld()
    {
        TimerWorld initialWorld = new([]);
        var reducer = new TimerReducer();
        var journal = new InMemoryJournal<string>();
        var loop = new SimulationLoop<TimerWorld, string, string>(CreateSystems(), reducer);

        SimulationRunResult<TimerWorld> result = loop.Run(
            initialWorld,
            ModelTime.Zero,
            AtSecond(20),
            journal);
        TimerWorld replayed = journal.Events.Aggregate(initialWorld, reducer.Apply);

        Assert.Equal(result.World.FiredTimers, replayed.FiredTimers);
    }

    [Fact]
    public void Run_ReducerObservesOnlyEventsAlreadyCommittedToJournal()
    {
        var journal = new InMemoryJournal<string>();
        var reducer = new JournalObservingTimerReducer(journal);
        var loop = new SimulationLoop<TimerWorld, string, string>(CreateSystems(), reducer);

        _ = loop.Run(new TimerWorld([]), ModelTime.Zero, AtSecond(20), journal);

        Assert.True(reducer.ObservedOnlyCommittedEvents);
    }

    private static TimerSystem[] CreateSystems() =>
    [
        new("A", sourceId: 1, AtSecond(10)),
        new("B", sourceId: 2, AtSecond(20)),
        new("C", sourceId: 3, AtSecond(15)),
    ];

    private static ModelTime AtSecond(long seconds) => ModelTime.Zero + ModelDuration.FromSeconds(seconds);

    private sealed class JournalObservingTimerReducer : IEventReducer<TimerWorld, string>
    {
        private readonly InMemoryJournal<string> _journal;
        private readonly TimerReducer _inner = new();

        public JournalObservingTimerReducer(InMemoryJournal<string> journal)
        {
            _journal = journal;
        }

        public bool ObservedOnlyCommittedEvents { get; private set; } = true;

        public TimerWorld Apply(TimerWorld world, DomainEvent<string> domainEvent)
        {
            ObservedOnlyCommittedEvents &= _journal.Events.Count > 0 &&
                ReferenceEquals(_journal.Events[^1], domainEvent);
            return _inner.Apply(world, domainEvent);
        }
    }
}

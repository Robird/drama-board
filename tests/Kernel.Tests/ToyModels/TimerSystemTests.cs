using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class TimerSystemTests
{
    [Fact]
    public void Run_ThreeIndependentTimers_CommitsInTimeOrderWithThreeJumps()
    {
        TimerSystem[] systems = [new()];
        var loop = new SimulationLoop<TimerWorld, string, string>(systems, new TimerReducer());
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<TimerWorld, string> result = loop.Run(
            CreateWorld(),
            cursor: Cursor(),
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
        TimerWorld initialWorld = CreateWorld();
        var reducer = new TimerReducer();
        var journal = new InMemoryJournal<string>();
        var loop = new SimulationLoop<TimerWorld, string, string>(CreateSystems(), reducer);

        SimulationRunResult<TimerWorld, string> result = loop.Run(
            initialWorld,
            Cursor(),
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

        _ = loop.Run(CreateWorld(), Cursor(), AtSecond(20), journal);

        Assert.True(reducer.ObservedOnlyCommittedEvents);
    }

    private static TimerSystem[] CreateSystems() => [new()];

    private static TimerWorld CreateWorld() => TimerWorld.Start(
        new TimerEntity(1, "A", AtSecond(10)),
        new TimerEntity(2, "B", AtSecond(20)),
        new TimerEntity(3, "C", AtSecond(15)));

    private static SimulationCursor Cursor() => SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero);

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

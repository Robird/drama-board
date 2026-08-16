using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class WorldSnapshotContractTests
{
    [Fact]
    public void Run_RetainedWorldSnapshots_AreNotRewrittenByLaterEvents()
    {
        RunOutput output = RunTimers();

        Assert.Equal(output.Journal.Events.Count + 1, output.Reducer.Snapshots.Count);
        Assert.Equal(
            ["", "A", "A,C", "A,C,B"],
            output.Reducer.Snapshots.Select(snapshot => string.Join(',', snapshot.FiredTimersAtCapture)));
        Assert.All(
            output.Reducer.Snapshots,
            snapshot => Assert.Equal(snapshot.FiredTimersAtCapture, snapshot.World.FiredTimers));
    }

    [Fact]
    public void Replay_EachForkPrefix_MatchesRetainedWorldSnapshotAfterFullRun()
    {
        RunOutput output = RunTimers();
        var reducer = new TimerReducer();

        for (int eventCount = 0; eventCount <= output.Journal.Events.Count; eventCount++)
        {
            RetainedSnapshot retained = output.Reducer.Snapshots[eventCount];
            TimerWorld forkWorld = ReplayHarness.Replay(
                output.InitialWorld,
                output.Journal.Events.Take(eventCount),
                reducer);

            Assert.Equal(retained.FiredTimersAtCapture, retained.World.FiredTimers);
            Assert.Equal(retained.World.FiredTimers, forkWorld.FiredTimers);
        }
    }

    private static RunOutput RunTimers()
    {
        TimerWorld initialWorld = new([]);
        TimerSystem[] systems =
        [
            new("A", sourceId: 1, AtSecond(10)),
            new("B", sourceId: 2, AtSecond(20)),
            new("C", sourceId: 3, AtSecond(15)),
        ];
        var reducer = new SnapshotRecordingTimerReducer(initialWorld);
        var loop = new SimulationLoop<TimerWorld, string, string>(systems, reducer);
        var journal = new InMemoryJournal<string>();

        _ = loop.Run(initialWorld, ModelTime.Zero, AtSecond(20), journal);

        return new RunOutput(initialWorld, journal, reducer);
    }

    private static ModelTime AtSecond(long seconds) =>
        ModelTime.Zero + ModelDuration.FromSeconds(seconds);

    private sealed class SnapshotRecordingTimerReducer : IEventReducer<TimerWorld, string>
    {
        private readonly TimerReducer _inner = new();
        private readonly List<RetainedSnapshot> _snapshots;

        public SnapshotRecordingTimerReducer(TimerWorld initialWorld)
        {
            _snapshots = [Capture(initialWorld)];
        }

        public IReadOnlyList<RetainedSnapshot> Snapshots => _snapshots;

        public TimerWorld Apply(TimerWorld world, DomainEvent<string> domainEvent)
        {
            TimerWorld nextWorld = _inner.Apply(world, domainEvent);
            _snapshots.Add(Capture(nextWorld));
            return nextWorld;
        }

        private static RetainedSnapshot Capture(TimerWorld world) =>
            new(world, [.. world.FiredTimers]);
    }

    private sealed record RetainedSnapshot(TimerWorld World, string[] FiredTimersAtCapture);

    private sealed record RunOutput(
        TimerWorld InitialWorld,
        InMemoryJournal<string> Journal,
        SnapshotRecordingTimerReducer Reducer);
}

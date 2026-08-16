using CsCheck;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Tests.ToyModels;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SimulationPropertiesTests
{
    [Fact]
    public void Journal_RandomTimerSet_ModelTimeNeverDecreases()
    {
        Gen.Int[0, 20_000].Array[0, 16].Sample(dueTicks =>
        {
            (_, InMemoryJournal<string> journal) = RunTimers(dueTicks, untilTicks: 20_000);

            Assert.True(journal.Events
                .Zip(journal.Events.Skip(1))
                .All(pair => pair.First.Timestamp.ModelTime <= pair.Second.Timestamp.ModelTime));
        });
    }

    [Fact]
    public void Rerun_RandomScenarios_ProducesIdenticalJournals()
    {
        Gen.Int[-300, 20_000].Array[5, 17].Sample(values =>
        {
            int[] timerDueTicks = [.. values.Skip(5).Select(value => Math.Abs((long)value) % 20_001L).Select(value => (int)value)];
            AssertEqualJournal(
                RunTimers(timerDueTicks, untilTicks: 20_000).Journal,
                RunTimers(timerDueTicks, untilTicks: 20_000).Journal);

            BouncingWorld bouncingWorld = CreateBouncingWorld(values);
            AssertEqualJournal(
                RunBouncing(bouncingWorld),
                RunBouncing(bouncingWorld));

            ulong seed = checked((ulong)(values[0] + 300));
            ModelDuration meanInterval = ModelDuration.FromSeconds(10 + Math.Abs(values[4] % 51));
            AssertEqualJournal(
                RunMining(seed, meanInterval),
                RunMining(seed, meanInterval));
        });
    }

    [Fact]
    public void Run_RandomUntilBoundary_CommitsNoLaterEvents()
    {
        Gen.Int[0, 20_000].Array[1, 17].Sample(values =>
        {
            long untilTicks = values[0];
            (_, InMemoryJournal<string> journal) = RunTimers(values.Skip(1), untilTicks);

            Assert.All(
                journal.Events,
                domainEvent => Assert.True(domainEvent.Timestamp.ModelTime <= new ModelTime(untilTicks)));
        });
    }

    [Fact]
    public void Run_RandomSameTimeTimerSet_MicrostepsAreStrictAndStartAtZero()
    {
        Gen.Int[1, 32].Sample(timerCount =>
        {
            int[] dueTicks = Enumerable.Repeat(1_000, timerCount).ToArray();
            (_, InMemoryJournal<string> journal) = RunTimers(dueTicks, untilTicks: 1_000);

            Assert.Equal(
                Enumerable.Range(0, timerCount),
                journal.Events.Select(domainEvent => domainEvent.Timestamp.Microstep.Value));
        });
    }

    [Fact]
    public void Replay_RandomTimerJournal_ReconstructsRunFinalWorld()
    {
        Gen.Int[0, 20_000].Array[0, 16].Sample(dueTicks =>
        {
            TimerWorld initialWorld = new([]);
            (SimulationRunResult<TimerWorld> result, InMemoryJournal<string> journal) =
                RunTimers(dueTicks, untilTicks: 20_000);

            TimerWorld replayed = ReplayHarness.Replay(
                initialWorld,
                journal.Events,
                static (world, domainEvent) => domainEvent.Kind == "TimerFired"
                    ? new TimerWorld([.. world.FiredTimers, domainEvent.Payload])
                    : throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind}'."));

            Assert.Equal(result.World.FiredTimers, replayed.FiredTimers);
        });
    }

    [Fact]
    public void Run_RandomReroute_InvalidatedArrivalNeverCommits()
    {
        Gen.Int[1, 10_000].Array[3].Sample(values =>
        {
            ModelTime rerouteAt = new(values[0]);
            ModelTime arrivalAtB = new(checked(values[0] + values[1]));
            ModelTime arrivalAtC = new(checked(values[0] + values[1] + values[2]));
            ISimSystem<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>[] systems =
            [
                new TravelSystem(arrivalAtB, arrivalAtC),
                new ScheduledInputSystem(rerouteAt, destination: "C"),
            ];
            var loop = new SimulationLoop<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>(systems);
            var journal = new InMemoryJournal<RerouteEventPayload>();

            SimulationRunResult<RerouteWorld> result = loop.Run(
                new RerouteWorld("B", false, false),
                ModelTime.Zero,
                arrivalAtC,
                journal);

            Assert.DoesNotContain(
                journal.Events,
                domainEvent => domainEvent.Payload is ArrivedEventPayload { Destination: "B" });
            Assert.Equal(new RerouteWorld("C", true, true), result.World);
        });
    }

    private static (SimulationRunResult<TimerWorld> Result, InMemoryJournal<string> Journal) RunTimers(
        IEnumerable<int> dueTicks,
        long untilTicks)
    {
        ISimSystem<TimerWorld, string, string>[] systems =
        [
            .. dueTicks.Select((due, index) =>
                (ISimSystem<TimerWorld, string, string>)new TimerSystem(
                    $"Timer-{index}",
                    sourceId: index + 1,
                    new ModelTime(due))),
        ];
        var loop = new SimulationLoop<TimerWorld, string, string>(systems);
        var journal = new InMemoryJournal<string>();
        SimulationRunResult<TimerWorld> result = loop.Run(
            new TimerWorld([]),
            ModelTime.Zero,
            new ModelTime(untilTicks),
            journal);
        return (result, journal);
    }

    private static BouncingWorld CreateBouncingWorld(IReadOnlyList<int> values)
    {
        double x = 1.0 + (Math.Abs(values[1]) % 800) / 100.0;
        double y = 1.0 + (Math.Abs(values[2]) % 800) / 100.0;
        double velocityX = values[3] / 100.0;
        double velocityY = values[4] / 100.0;
        return new BouncingWorld(
            width: 10,
            height: 10,
            ModelTime.Zero,
            [new BouncingBall(1, 0.5, 1, new PhysicsVector(x, y), new PhysicsVector(velocityX, velocityY))]);
    }

    private static InMemoryJournal<CollisionEventPayload> RunBouncing(BouncingWorld initialWorld)
    {
        var loop = new SimulationLoop<BouncingWorld, CollisionCandidatePayload, CollisionEventPayload>(
            [new BouncingSystem()]);
        var journal = new InMemoryJournal<CollisionEventPayload>();
        _ = loop.Run(
            initialWorld,
            ModelTime.Zero,
            ModelTime.Zero + ModelDuration.FromSeconds(5),
            journal);
        return journal;
    }

    private static InMemoryJournal<MiningDiscovery> RunMining(ulong seed, ModelDuration meanInterval)
    {
        var system = new MiningSystem(sourceId: 17, activityStreamId: 73, meanInterval);
        var loop = new SimulationLoop<MiningWorld, MiningForecast, MiningDiscovery>([system]);
        var journal = new InMemoryJournal<MiningDiscovery>();
        _ = loop.Run(
            MiningWorld.Start(seed),
            ModelTime.Zero,
            ModelTime.Zero + ModelDuration.FromSeconds(300),
            journal);
        return journal;
    }

    private static void AssertEqualJournal<TEventPayload>(
        InMemoryJournal<TEventPayload> first,
        InMemoryJournal<TEventPayload> second) =>
        Assert.Equal(
            first.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
            second.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)));
}
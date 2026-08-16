using CsCheck;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
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
            AssertEqualBouncingJournal(
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
    public void Run_RandomSegmentBoundariesAndInputPlan_EqualsSingleRun()
    {
        Gen.Int[-10_000, 10_000].Array[12, 24].Sample(values =>
        {
            int timerCount = 1 + Math.Abs(values[0]) % 5;
            int requestedSegmentCount = 1 + Math.Abs(values[1]) % 5;
            int inputCount = Math.Abs(values[2]) % (timerCount + 1);
            TimerEntity[] timers =
            [
                .. Enumerable.Range(0, timerCount).Select(index =>
                    new TimerEntity(
                        index + 1,
                        $"Timer-{index}",
                        new ModelTime(100 + Math.Abs(values[3 + index]) % 901))),
            ];
            TimerWorld initialWorld = TimerWorld.Start(timers);
            UncommittedDomainEvent<string>[] inputPlan =
            [
                .. timers.Take(inputCount).Select(timer =>
                    new UncommittedDomainEvent<string>(TimerEventKinds.Fired, timer.Name)),
            ];
            ModelTime[] boundaries =
            [
                .. values
                    .Skip(8)
                    .Take(requestedSegmentCount - 1)
                    .Select(value => new ModelTime(1 + Math.Abs(value) % 99))
                    .Distinct()
                    .OrderBy(boundary => boundary),
            ];
            int segmentCount = boundaries.Length + 1;
            var singleJournal = new InMemoryJournal<string>();
            var splitJournal = new InMemoryJournal<string>();
            var singleLoop = new SimulationLoop<TimerWorld, string, string>([new TimerSystem()], new TimerReducer());
            var splitLoop = new SimulationLoop<TimerWorld, string, string>([new TimerSystem()], new TimerReducer());
            SimulationCursor initialCursor = SimulationCursor.CreateInitial(lineageId: 77, ModelTime.Zero);
            var until = new ModelTime(1_000);

            SimulationRunResult<TimerWorld, string> single = singleLoop.Run(
                initialWorld,
                initialCursor,
                until,
                singleJournal,
                inputPlan);

            TimerWorld splitWorld = initialWorld;
            SimulationCursor splitCursor = initialCursor;
            for (int segmentIndex = 0; segmentIndex < boundaries.Length; segmentIndex++)
            {
                int inputStart = inputPlan.Length * segmentIndex / segmentCount;
                int inputEnd = inputPlan.Length * (segmentIndex + 1) / segmentCount;
                SimulationRunResult<TimerWorld, string> segment = splitLoop.Run(
                    splitWorld,
                    splitCursor,
                    boundaries[segmentIndex],
                    splitJournal,
                    inputPlan[inputStart..inputEnd]);
                splitWorld = segment.World;
                splitCursor = segment.Cursor;
                Assert.Equal(StopReason.BoundaryReached, segment.StopReason);
            }

            int finalInputStart = inputPlan.Length * boundaries.Length / segmentCount;
            SimulationRunResult<TimerWorld, string> split = splitLoop.Run(
                splitWorld,
                splitCursor,
                until,
                splitJournal,
                inputPlan[finalInputStart..]);

            AssertEqualJournal(singleJournal, splitJournal);
            Assert.Equal(single.World.Timers, split.World.Timers);
            Assert.Equal(single.World.FiredTimers, split.World.FiredTimers);
            Assert.Equal(single.Cursor, split.Cursor);
            Assert.Equal(single.StopReason, split.StopReason);
        });
    }

    [Fact]
    public void Run_RandomCandidateTimelineCuts_EqualsSingleRun()
    {
        Gen.Int[-10_000, 10_000].Array[12, 24].Sample(values =>
        {
            int timerCount = 1 + Math.Abs(values[0]) % 5;
            int requestedCutCount = Math.Abs(values[1]) % 5;
            int inputCount = Math.Abs(values[2]) % (timerCount + 1);
            TimerEntity[] timers =
            [
                .. Enumerable.Range(0, timerCount).Select(index =>
                    new TimerEntity(
                        index + 1,
                        $"Timer-{index}",
                        new ModelTime(1 + Math.Abs(values[3 + index]) % 1_000))),
            ];
            TimerWorld initialWorld = TimerWorld.Start(timers);
            UncommittedDomainEvent<string>[] initiallyReadyInputs =
            [
                .. timers.Take(inputCount).Select(timer =>
                    new UncommittedDomainEvent<string>(TimerEventKinds.Fired, timer.Name)),
            ];
            ModelTime[] cuts =
            [
                .. values
                    .Skip(8)
                    .Take(requestedCutCount)
                    .Select(value => new ModelTime(Math.Abs(value) % 1_001))
                    .Distinct()
                    .OrderBy(cut => cut),
            ];
            var singleJournal = new InMemoryJournal<string>();
            var splitJournal = new InMemoryJournal<string>();
            var singleLoop = new SimulationLoop<TimerWorld, string, string>([new TimerSystem()], new TimerReducer());
            var splitLoop = new SimulationLoop<TimerWorld, string, string>([new TimerSystem()], new TimerReducer());
            SimulationCursor initialCursor = SimulationCursor.CreateInitial(lineageId: 91, ModelTime.Zero);
            var until = new ModelTime(1_000);

            SimulationRunResult<TimerWorld, string> single = singleLoop.Run(
                initialWorld,
                initialCursor,
                until,
                singleJournal,
                initiallyReadyInputs);

            TimerWorld splitWorld = initialWorld;
            SimulationCursor splitCursor = initialCursor;
            bool inputsSubmitted = false;
            foreach (ModelTime cut in cuts)
            {
                SimulationRunResult<TimerWorld, string> segment = splitLoop.Run(
                    splitWorld,
                    splitCursor,
                    cut,
                    splitJournal,
                    inputsSubmitted ? null : initiallyReadyInputs);
                splitWorld = segment.World;
                splitCursor = segment.Cursor;
                inputsSubmitted = true;
            }

            SimulationRunResult<TimerWorld, string> split = splitLoop.Run(
                splitWorld,
                splitCursor,
                until,
                splitJournal,
                inputsSubmitted ? null : initiallyReadyInputs);

            AssertEqualJournal(singleJournal, splitJournal);
            Assert.Equal(single.World.Timers, split.World.Timers);
            Assert.Equal(single.World.FiredTimers, split.World.FiredTimers);
            Assert.Equal(single.Cursor, split.Cursor);
            Assert.Equal(single.StopReason, split.StopReason);
        });
    }

    [Fact]
    public void Replay_RandomTimerJournal_ReconstructsRunFinalWorld()
    {
        Gen.Int[0, 20_000].Array[0, 16].Sample(dueTicks =>
        {
            TimerWorld initialWorld = CreateTimerWorld(dueTicks);
            (SimulationRunResult<TimerWorld, string> result, InMemoryJournal<string> journal) =
                RunTimers(dueTicks, untilTicks: 20_000);

            TimerWorld replayed = ReplayHarness.Replay(
                initialWorld,
                journal.Events,
                new TimerReducer());

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
                new ScheduledRerouteSystem(),
            ];
            var loop = new SimulationLoop<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>(
                systems,
                new RerouteReducer());
            var journal = new InMemoryJournal<RerouteEventPayload>();

            SimulationRunResult<RerouteWorld, RerouteEventPayload> result = loop.Run(
                RerouteWorld.Start("B"),
                SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
                arrivalAtC,
                journal,
                [
                    new UncommittedDomainEvent<RerouteEventPayload>(
                        RerouteEventKinds.RerouteScheduled,
                        new RerouteScheduledEventPayload(rerouteAt, "C")),
                ]);

            Assert.DoesNotContain(
                journal.Events,
                domainEvent => domainEvent.Payload is ArrivedEventPayload { Destination: "B" });
            Assert.Equal(
                RerouteWorld.Start("C") with { HasRedirected = true, HasArrived = true },
                result.World);
        });
    }

    [Fact]
    public void ForecastNext_AllToySystems_IsIndependentOfNow()
    {
        Gen.Int[0, 20_000].Array[2, 8].Sample(nowTicks =>
        {
            ModelTime[] nows = [.. nowTicks.Select(ticks => new ModelTime(ticks))];
            AssertForecastIndependentOfNow(
                new TimerSystem(),
                TimerWorld.Start(new TimerEntity(1, "Timer", new ModelTime(100))),
                nows);
            AssertForecastIndependentOfNow(
                new TravelSystem(new ModelTime(100), new ModelTime(200)),
                RerouteWorld.Start("B"),
                nows);
            AssertForecastIndependentOfNow(
                new ScheduledRerouteSystem(),
                RerouteWorld.Start("B") with
                {
                    PendingReroute = new ScheduledReroute(new ModelTime(50), "C"),
                },
                nows);
            AssertForecastIndependentOfNow(
                new BouncingSystem(),
                new BouncingWorld(
                    width: 20,
                    height: 12,
                    [new BouncingBall(
                        1,
                        1,
                        1,
                        new PhysicsVector(5, 5),
                        new PhysicsVector(1, 0),
                        new ModelTime(25))]),
                nows);
            AssertForecastIndependentOfNow(
                new MiningSystem(ModelDuration.FromSeconds(30)),
                MiningWorld.Start(worldSeed: 42),
                nows);
            AssertForecastIndependentOfNow(
                new InterruptedMiningSystem(ModelDuration.FromSeconds(120)),
                InterruptedMiningWorld.Start(worldSeed: 42) with
                {
                    Activity = new MiningActivity(new ModelTime(5)),
                    AliceAtMine = true,
                    AliceArrivedAt = new ModelTime(25),
                },
                nows);
            AssertForecastIndependentOfNow(
                new AliceArrivalSystem(),
                InterruptedMiningWorld.Start(worldSeed: 42) with
                {
                    ScheduledAliceArrivalAt = new ModelTime(50),
                },
                nows);
            AssertForecastIndependentOfNow(
                new EntityLifecycleSystem(),
                EntityLifecycleWorld.Start(
                    nextEntityId: 30,
                    new LifecycleEntity(10, 0, new ModelTime(10), EntityDirective.Act)),
                nows);
            AssertForecastIndependentOfNow(
                new LootContentionSystem(),
                new LootWorld(
                    WorldSeed: 42,
                    new LootItem(Id: 99, ContentionRound: 0, OwnerId: null),
                    [new LootActor(10, "Alice", new ModelTime(10))]),
                nows);
        });
    }

    private static (SimulationRunResult<TimerWorld, string> Result, InMemoryJournal<string> Journal) RunTimers(
        IEnumerable<int> dueTicks,
        long untilTicks)
    {
        TimerWorld initialWorld = CreateTimerWorld(dueTicks);
        ISimSystem<TimerWorld, string, string>[] systems = [new TimerSystem()];
        var loop = new SimulationLoop<TimerWorld, string, string>(systems, new TimerReducer());
        var journal = new InMemoryJournal<string>();
        SimulationRunResult<TimerWorld, string> result = loop.Run(
            initialWorld,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            new ModelTime(untilTicks),
            journal);
        return (result, journal);
    }

    private static TimerWorld CreateTimerWorld(IEnumerable<int> dueTicks) =>
        TimerWorld.Start(
            [.. dueTicks.Select((due, index) => new TimerEntity(index + 1, $"Timer-{index}", new ModelTime(due)))]);

    private static BouncingWorld CreateBouncingWorld(IReadOnlyList<int> values)
    {
        double x = 1.0 + (Math.Abs(values[1]) % 800) / 100.0;
        double y = 1.0 + (Math.Abs(values[2]) % 800) / 100.0;
        double velocityX = values[3] / 100.0;
        double velocityY = values[4] / 100.0;
        return new BouncingWorld(
            width: 10,
            height: 10,
            [new BouncingBall(1, 0.5, 1, new PhysicsVector(x, y), new PhysicsVector(velocityX, velocityY))]);
    }

    private static InMemoryJournal<CollisionEventPayload> RunBouncing(BouncingWorld initialWorld)
    {
        var loop = new SimulationLoop<BouncingWorld, CollisionCandidatePayload, CollisionEventPayload>(
            [new BouncingSystem()],
            new BouncingReducer());
        var journal = new InMemoryJournal<CollisionEventPayload>();
        _ = loop.Run(
            initialWorld,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            ModelTime.Zero + ModelDuration.FromSeconds(5),
            journal);
        return journal;
    }

    private static InMemoryJournal<MiningDiscovery> RunMining(ulong seed, ModelDuration meanInterval)
    {
        var system = new MiningSystem(meanInterval);
        var loop = new SimulationLoop<MiningWorld, MiningForecast, MiningDiscovery>(
            [system],
            new MiningReducer());
        var journal = new InMemoryJournal<MiningDiscovery>();
        _ = loop.Run(
            MiningWorld.Start(seed),
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            ModelTime.Zero + ModelDuration.FromSeconds(300),
            journal);
        return journal;
    }

    private static void AssertForecastIndependentOfNow<TWorld, TCandidatePayload, TEventPayload>(
        ISimSystem<TWorld, TCandidatePayload, TEventPayload> system,
        TWorld world,
        IEnumerable<ModelTime> nows)
    {
        (EventCandidateId Id, ModelTime Due, long SourceId, TCandidatePayload Payload)[] expected =
            ForecastSnapshot(system.ForecastNext(world, ModelTime.Zero));

        foreach (ModelTime now in nows)
        {
            Assert.Equal(expected, ForecastSnapshot(system.ForecastNext(world, now)));
        }
    }

    private static (EventCandidateId Id, ModelTime Due, long SourceId, TCandidatePayload Payload)[]
        ForecastSnapshot<TCandidatePayload>(IEnumerable<EventCandidate<TCandidatePayload>> candidates) =>
        [
            .. candidates
                .OrderBy(candidate => candidate.Due)
                .ThenBy(candidate => candidate.SourceId)
                .ThenBy(candidate => candidate.Id)
                .Select(candidate =>
                    (candidate.Id, candidate.Due, candidate.SourceId, candidate.Payload)),
        ];

    private static void AssertEqualJournal<TEventPayload>(
        InMemoryJournal<TEventPayload> first,
        InMemoryJournal<TEventPayload> second) =>
        Assert.Equal(
            first.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
            second.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)));

    private static void AssertEqualBouncingJournal(
        InMemoryJournal<CollisionEventPayload> first,
        InMemoryJournal<CollisionEventPayload> second) =>
        Assert.Equal(BouncingSnapshot(first), BouncingSnapshot(second));

    private static (int EventIndex, LogicalTimestamp Timestamp, EventKind Kind, CollisionKind CollisionKind, int FirstBallId, int? SecondBallId, BouncingBallResolution Resolution)[] BouncingSnapshot(
        InMemoryJournal<CollisionEventPayload> journal) =>
    [
        .. journal.Events.SelectMany((domainEvent, eventIndex) =>
            domainEvent.Payload.BallResolutions.Select(resolution =>
                (eventIndex,
                 domainEvent.Timestamp,
                 domainEvent.Kind,
                 domainEvent.Payload.Kind,
                 domainEvent.Payload.FirstBallId,
                 domainEvent.Payload.SecondBallId,
                 resolution))),
    ];
}

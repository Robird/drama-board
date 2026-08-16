using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class ReplayDeterminismTests
{
    [Fact]
    public void Rerun_TimerModel_ProducesIdenticalJournal()
    {
        static InMemoryJournal<string> Run()
        {
            TimerWorld initialWorld = TimerWorld.Start(
                new TimerEntity(1, "A", ModelTime.Zero + ModelDuration.FromSeconds(10)),
                new TimerEntity(2, "B", ModelTime.Zero + ModelDuration.FromSeconds(20)),
                new TimerEntity(3, "C", ModelTime.Zero + ModelDuration.FromSeconds(15)));
            TimerSystem[] systems = [new()];
            var loop = new SimulationLoop<TimerWorld, string, string>(systems, new TimerReducer());
            var journal = new InMemoryJournal<string>();
            _ = loop.Run(
                initialWorld,
                SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
                ModelTime.Zero + ModelDuration.FromSeconds(20),
                journal);
            return journal;
        }

        AssertRerunProducesIdenticalJournal(Run);
    }

    [Fact]
    public void Rerun_RerouteModel_ProducesIdenticalJournal()
    {
        static InMemoryJournal<RerouteEventPayload> Run()
        {
            ISimSystem<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>[] systems =
            [
                new TravelSystem(AtSecond(10), AtSecond(17)),
                new ScheduledRerouteSystem(),
            ];
            var loop = new SimulationLoop<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>(
                systems,
                new RerouteReducer());
            var journal = new InMemoryJournal<RerouteEventPayload>();
            _ = loop.Run(
                RerouteWorld.Start("B"),
                SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
                AtSecond(20),
                journal,
                [
                    new UncommittedDomainEvent<RerouteEventPayload>(
                        RerouteEventKinds.RerouteScheduled,
                        new RerouteScheduledEventPayload(AtSecond(5), "C")),
                ]);
            return journal;
        }

        AssertRerunProducesIdenticalJournal(Run);
    }

    [Fact]
    public void Rerun_BouncingModel_ProducesIdenticalJournal()
    {
        static InMemoryJournal<CollisionEventPayload> Run()
        {
            var initialWorld = new BouncingWorld(
                width: 100,
                height: 20,
                [
                    new BouncingBall(1, 1, 1, new PhysicsVector(10, 10), new PhysicsVector(3, 0)),
                    new BouncingBall(2, 1, 1, new PhysicsVector(20, 10), new PhysicsVector(0, 0)),
                ]);
            var loop = new SimulationLoop<BouncingWorld, CollisionCandidatePayload, CollisionEventPayload>(
                [new BouncingSystem()],
                new BouncingReducer());
            var journal = new InMemoryJournal<CollisionEventPayload>();
            _ = loop.Run(
                initialWorld,
                SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
                AtSecond(3),
                journal);
            return journal;
        }

        InMemoryJournal<CollisionEventPayload> first = Run();
        InMemoryJournal<CollisionEventPayload> second = Run();
        Assert.Equal(BouncingSnapshot(first), BouncingSnapshot(second));
    }

    [Fact]
    public void Rerun_MiningModel_ProducesIdenticalJournal()
    {
        static InMemoryJournal<MiningDiscovery> Run()
        {
            var system = new MiningSystem(
                activityStreamId: 73,
                meanDiscoveryInterval: ModelDuration.FromSeconds(30));
            var loop = new SimulationLoop<MiningWorld, MiningForecast, MiningDiscovery>(
                [system],
                new MiningReducer());
            var journal = new InMemoryJournal<MiningDiscovery>();
            _ = loop.Run(
                MiningWorld.Start(worldSeed: 42),
                SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
                ModelTime.Zero + ModelDuration.FromSeconds(300),
                journal);
            return journal;
        }

        AssertRerunProducesIdenticalJournal(Run);
    }

    [Fact]
    public void Rerun_InterruptedMiningModel_ProducesIdenticalJournal()
    {
        static InMemoryJournal<InterruptedMiningEvent> Run()
        {
            ModelTime arrivalAt = ModelTime.Zero + ModelDuration.FromSeconds(17 * 60);
            ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>[] systems =
            [
                new InterruptedMiningSystem(
                    completionDuration: ModelDuration.FromSeconds(2 * 60 * 60),
                    activityStreamId: 73,
                    meanDiscoveryInterval: ModelDuration.FromSeconds(2 * 60)),
                new AliceArrivalSystem(),
            ];
            var loop = new SimulationLoop<
                InterruptedMiningWorld,
                InterruptedMiningForecast,
                InterruptedMiningEvent>(systems, new InterruptedMiningReducer());
            var journal = new InMemoryJournal<InterruptedMiningEvent>();
            _ = loop.Run(
                InterruptedMiningWorld.Start(worldSeed: 42),
                SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
                ModelTime.Zero + ModelDuration.FromSeconds(3 * 60 * 60),
                journal,
                [
                    new UncommittedDomainEvent<InterruptedMiningEvent>(
                        InterruptedMiningEventKinds.AliceArrivalScheduled,
                        new AliceArrivalScheduledEvent(arrivalAt)),
                ]);
            return journal;
        }

        AssertRerunProducesIdenticalJournal(Run);
    }

    private static void AssertRerunProducesIdenticalJournal<TEventPayload>(
        Func<InMemoryJournal<TEventPayload>> run)
    {
        InMemoryJournal<TEventPayload> first = run();
        InMemoryJournal<TEventPayload> second = run();

        Assert.Equal(
            first.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
            second.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)));
    }

    private static ModelTime AtSecond(long seconds) =>
        ModelTime.Zero + ModelDuration.FromSeconds(seconds);

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

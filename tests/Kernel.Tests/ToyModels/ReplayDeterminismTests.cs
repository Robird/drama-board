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
            TimerSystem[] systems =
            [
                new("A", sourceId: 1, ModelTime.Zero + ModelDuration.FromSeconds(10)),
                new("B", sourceId: 2, ModelTime.Zero + ModelDuration.FromSeconds(20)),
                new("C", sourceId: 3, ModelTime.Zero + ModelDuration.FromSeconds(15)),
            ];
            var loop = new SimulationLoop<TimerWorld, string, string>(systems);
            var journal = new InMemoryJournal<string>();
            _ = loop.Run(
                new TimerWorld([]),
                ModelTime.Zero,
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
                new ScheduledInputSystem(AtSecond(5), destination: "C"),
            ];
            var loop = new SimulationLoop<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>(systems);
            var journal = new InMemoryJournal<RerouteEventPayload>();
            _ = loop.Run(new RerouteWorld("B", false, false), ModelTime.Zero, AtSecond(20), journal);
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
                ModelTime.Zero,
                [
                    new BouncingBall(1, 1, 1, new PhysicsVector(10, 10), new PhysicsVector(3, 0)),
                    new BouncingBall(2, 1, 1, new PhysicsVector(20, 10), new PhysicsVector(0, 0)),
                ]);
            var loop = new SimulationLoop<BouncingWorld, CollisionCandidatePayload, CollisionEventPayload>(
                [new BouncingSystem()]);
            var journal = new InMemoryJournal<CollisionEventPayload>();
            _ = loop.Run(initialWorld, ModelTime.Zero, AtSecond(3), journal);
            return journal;
        }

        AssertRerunProducesIdenticalJournal(Run);
    }

    [Fact]
    public void Rerun_MiningModel_ProducesIdenticalJournal()
    {
        static InMemoryJournal<MiningDiscovery> Run()
        {
            var system = new MiningSystem(
                sourceId: 17,
                activityStreamId: 73,
                meanDiscoveryInterval: ModelDuration.FromSeconds(30));
            var loop = new SimulationLoop<MiningWorld, MiningForecast, MiningDiscovery>([system]);
            var journal = new InMemoryJournal<MiningDiscovery>();
            _ = loop.Run(
                MiningWorld.Start(worldSeed: 42),
                ModelTime.Zero,
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
                    sourceId: 20,
                    completionDuration: ModelDuration.FromSeconds(2 * 60 * 60),
                    activityStreamId: 73,
                    meanDiscoveryInterval: ModelDuration.FromSeconds(2 * 60)),
                new AliceArrivalSystem(sourceId: 10, arrivalAt),
            ];
            var loop = new SimulationLoop<
                InterruptedMiningWorld,
                InterruptedMiningForecast,
                InterruptedMiningEvent>(systems);
            var journal = new InMemoryJournal<InterruptedMiningEvent>();
            _ = loop.Run(
                InterruptedMiningWorld.Start(worldSeed: 42),
                ModelTime.Zero,
                ModelTime.Zero + ModelDuration.FromSeconds(3 * 60 * 60),
                journal);
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
}

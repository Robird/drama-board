using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class MiningSystemTests
{
    [Fact]
    public void Run_SameSeedTwice_CommitsIdenticalJournals()
    {
        InMemoryJournal<MiningDiscovery> first = Run(worldSeed: 42);
        InMemoryJournal<MiningDiscovery> second = Run(worldSeed: 42);

        Assert.NotEmpty(first.Events);
        Assert.Equal(
            first.Events.Select(domainEvent => (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
            second.Events.Select(domainEvent => (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)));
    }

    [Fact]
    public void Run_DifferentSeeds_ProducesDifferentDiscoveryTimes()
    {
        InMemoryJournal<MiningDiscovery> first = Run(worldSeed: 42);
        InMemoryJournal<MiningDiscovery> second = Run(worldSeed: 43);

        Assert.NotEmpty(first.Events);
        Assert.NotEmpty(second.Events);
        Assert.NotEqual(
            first.Events[0].Timestamp.ModelTime,
            second.Events[0].Timestamp.ModelTime);
    }

    [Fact]
    public void ForecastNext_SameWorldCalledRepeatedly_ReturnsIdenticalCandidate()
    {
        MiningSystem system = CreateSystem();
        MiningWorld world = MiningWorld.Start(worldSeed: 42);

        EventCandidate<MiningForecast> first = Assert.Single(system.ForecastNext(world, ModelTime.Zero));
        EventCandidate<MiningForecast> second = Assert.Single(system.ForecastNext(world, ModelTime.Zero));

        Assert.Equal(
            (first.Id, first.Due, first.SourceId, first.Payload),
            (second.Id, second.Due, second.SourceId, second.Payload));
    }

    [Fact]
    public void Resolve_DiscoveryAdvancesGeneration_NextForecastChangesDeterministically()
    {
        MiningSystem system = CreateSystem();
        MiningWorld initialWorld = MiningWorld.Start(worldSeed: 42);
        EventCandidate<MiningForecast> first = Assert.Single(
            system.ForecastNext(initialWorld, ModelTime.Zero));

        UncommittedDomainEvent<MiningDiscovery> resolved = Assert.Single(system.Resolve(initialWorld, first));
        MiningWorld resolvedWorld = new MiningReducer().Apply(
            initialWorld,
            new DomainEvent<MiningDiscovery>(
                new LogicalTimestamp(first.Due, new Microstep(0)),
                EventCause.FromResolve(first.SourceId, first.Id, first.Due, batchOrdinal: 0),
                resolved.Kind,
                resolved.Payload));
        EventCandidate<MiningForecast> next = Assert.Single(
            system.ForecastNext(resolvedWorld, first.Due));
        EventCandidate<MiningForecast> repeated = Assert.Single(
            system.ForecastNext(resolvedWorld, first.Due));

        Assert.Equal(1, resolvedWorld.DiscoveryCount);
        Assert.Equal(1, next.Payload.Generation);
        Assert.True(next.Due > first.Due);
        Assert.NotEqual(first.Due, next.Due);
        Assert.Equal(
            (next.Id, next.Due, next.SourceId, next.Payload),
            (repeated.Id, repeated.Due, repeated.SourceId, repeated.Payload));
    }

    [Fact]
    public void Replay_EachDiscoveryCoordinatesRecomputeRecordedDelayAndMineral()
    {
        const ulong worldSeed = 42;
        string[] minerals = ["Quartz", "Silver", "Opal"];
        ModelDuration meanInterval = ModelDuration.FromSeconds(30);
        MiningWorld auditWorld = MiningWorld.Start(worldSeed);
        InMemoryJournal<MiningDiscovery> journal = Run(worldSeed);
        var reducer = new MiningReducer();

        foreach (DomainEvent<MiningDiscovery> domainEvent in journal.Events)
        {
            MiningDiscovery discovery = domainEvent.Payload;
            ulong expectedGeneration = checked((ulong)auditWorld.DiscoveryCount);
            ulong expectedDelayStream = DeterministicRandom.DeriveStreamId(auditWorld.ActivityId);
            ulong expectedMineralStream = DeterministicRandom.DeriveStreamId(expectedDelayStream, "mineral");
            Assert.Equal(
                new RandomSampleCoordinates(expectedDelayStream, expectedGeneration, 0),
                discovery.DelaySample);
            Assert.Equal(
                new RandomSampleCoordinates(expectedMineralStream, expectedGeneration, 0),
                discovery.MineralSample);

            ModelDuration expectedDelay = DeterministicRandom.SampleExponentialDuration(
                auditWorld.WorldSeed,
                discovery.DelaySample.StreamId,
                discovery.DelaySample.Generation,
                meanInterval,
                discovery.DelaySample.SampleIndex);
            int expectedMineralIndex = DeterministicRandom.SampleInt32(
                auditWorld.WorldSeed,
                discovery.MineralSample.StreamId,
                discovery.MineralSample.Generation,
                0,
                minerals.Length,
                discovery.MineralSample.SampleIndex);
            Assert.Equal(auditWorld.LastDiscoveryAt + expectedDelay, discovery.DiscoveredAt);
            Assert.Equal(minerals[expectedMineralIndex], discovery.Mineral);

            auditWorld = reducer.Apply(auditWorld, domainEvent);
        }

        Assert.Equal(journal.Events.Count, auditWorld.DiscoveryCount);
        Assert.Equal(journal.Events.Select(domainEvent => domainEvent.Payload), auditWorld.Discoveries);
    }

    [Fact]
    public void Apply_SameKindIdWithNewerVersion_RoutesToExistingReducerBranch()
    {
        MiningWorld initialWorld = MiningWorld.Start(worldSeed: 42);
        var discovery = new MiningDiscovery(
            0,
            new ModelTime(10),
            "Quartz",
            new RandomSampleCoordinates(1, 0, 0),
            new RandomSampleCoordinates(2, 0, 0));
        var versionTwoKind = new EventKind(MiningEventKinds.MineralDiscovered.Id, 2);
        var domainEvent = new DomainEvent<MiningDiscovery>(
            new LogicalTimestamp(discovery.DiscoveredAt, new Microstep(0)),
            EventCause.FromResolve(
                initialWorld.ActivityId,
                new EventCandidateId(0),
                discovery.DiscoveredAt,
                batchOrdinal: 0),
            versionTwoKind,
            discovery);

        MiningWorld result = new MiningReducer().Apply(initialWorld, domainEvent);

        Assert.Equal(1, result.DiscoveryCount);
        Assert.Equal(discovery, Assert.Single(result.Discoveries));
    }

    private static InMemoryJournal<MiningDiscovery> Run(ulong worldSeed)
    {
        MiningSystem system = CreateSystem();
        var loop = new SimulationLoop<MiningWorld, MiningForecast, MiningDiscovery>([system], new MiningReducer());
        var journal = new InMemoryJournal<MiningDiscovery>();

        _ = loop.Run(
            MiningWorld.Start(worldSeed),
            cursor: SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            until: ModelTime.Zero + ModelDuration.FromSeconds(300),
            journal);

        return journal;
    }

    private static MiningSystem CreateSystem() =>
        new(
            meanDiscoveryInterval: ModelDuration.FromSeconds(30));
}

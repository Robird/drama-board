using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record MiningForecast(
    long Generation,
    string Mineral,
    RandomSampleCoordinates DelaySample,
    RandomSampleCoordinates MineralSample);

internal sealed record MiningDiscovery(
    long Generation,
    ModelTime DiscoveredAt,
    string Mineral,
    RandomSampleCoordinates DelaySample,
    RandomSampleCoordinates MineralSample);

internal static class MiningEventKinds
{
    public static readonly EventKind MineralDiscovered = new("mining.mineral-discovered", 1);
}

internal sealed record MiningWorld(
    ulong WorldSeed,
    long ActivityId,
    long DiscoveryCount,
    ModelTime LastDiscoveryAt,
    IReadOnlyList<MiningDiscovery> Discoveries)
{
    public static MiningWorld Start(ulong worldSeed, long activityId = 17) =>
        new(worldSeed, activityId, 0, ModelTime.Zero, []);
}

internal sealed class MiningSystem : ISimSystem<MiningWorld, MiningForecast, MiningDiscovery>
{
    private static readonly string[] Minerals = ["Quartz", "Silver", "Opal"];

    private readonly ModelDuration _meanDiscoveryInterval;

    public MiningSystem(ModelDuration meanDiscoveryInterval)
    {
        _meanDiscoveryInterval = meanDiscoveryInterval;
    }

    public IReadOnlyList<EventCandidate<MiningForecast>> ForecastNext(MiningWorld world, ModelTime now)
    {
        ulong generation = checked((ulong)world.DiscoveryCount);
        ulong activityStreamId = DeterministicRandom.DeriveStreamId(world.ActivityId);
        ulong mineralStreamId = DeterministicRandom.DeriveStreamId(activityStreamId, "mineral");
        var delaySample = new RandomSampleCoordinates(activityStreamId, generation, 0);
        var mineralSample = new RandomSampleCoordinates(mineralStreamId, generation, 0);
        ModelDuration delay = DeterministicRandom.SampleExponentialDuration(
            world.WorldSeed,
            delaySample.StreamId,
            generation,
            _meanDiscoveryInterval,
            delaySample.SampleIndex);
        int mineralIndex = DeterministicRandom.SampleInt32(
            world.WorldSeed,
            mineralSample.StreamId,
            generation,
            minInclusive: 0,
            maxExclusive: Minerals.Length,
            sampleIndex: mineralSample.SampleIndex);
        var forecast = new MiningForecast(
            world.DiscoveryCount,
            Minerals[mineralIndex],
            delaySample,
            mineralSample);

        return
        [
            new EventCandidate<MiningForecast>(
                new EventCandidateId(world.DiscoveryCount),
                world.LastDiscoveryAt + delay,
                world.ActivityId,
                forecast),
        ];
    }

    public IReadOnlyList<UncommittedDomainEvent<MiningDiscovery>> Resolve(
        MiningWorld world,
        EventCandidate<MiningForecast> candidate)
    {
        if (candidate.SourceId != world.ActivityId ||
            candidate.Payload.Generation != world.DiscoveryCount)
        {
            throw new InvalidOperationException("The mining forecast does not match the current activity generation.");
        }

        var discovery = new MiningDiscovery(
            world.DiscoveryCount,
            candidate.Due,
            candidate.Payload.Mineral,
            candidate.Payload.DelaySample,
            candidate.Payload.MineralSample);
        return [new UncommittedDomainEvent<MiningDiscovery>(MiningEventKinds.MineralDiscovered, discovery)];
    }
}

internal sealed class MiningReducer : IEventReducer<MiningWorld, MiningDiscovery>
{
    public MiningWorld Apply(MiningWorld world, DomainEvent<MiningDiscovery> domainEvent)
    {
        if (domainEvent.Kind.Id != MiningEventKinds.MineralDiscovered.Id ||
            domainEvent.Payload.Generation != world.DiscoveryCount)
        {
            throw new InvalidOperationException($"Unknown or out-of-sequence event kind '{domainEvent.Kind.Id}'.");
        }

        return new MiningWorld(
            world.WorldSeed,
            world.ActivityId,
            checked(world.DiscoveryCount + 1),
            domainEvent.Payload.DiscoveredAt,
            [.. world.Discoveries, domainEvent.Payload]);
    }
}

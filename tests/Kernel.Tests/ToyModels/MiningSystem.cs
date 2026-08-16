using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record MiningForecast(long Generation, string Mineral);

internal sealed record MiningDiscovery(long Generation, ModelTime DiscoveredAt, string Mineral);

internal sealed record MiningWorld(
    ulong WorldSeed,
    long DiscoveryCount,
    ModelTime LastDiscoveryAt,
    IReadOnlyList<MiningDiscovery> Discoveries)
{
    public static MiningWorld Start(ulong worldSeed) => new(worldSeed, 0, ModelTime.Zero, []);
}

internal sealed class MiningSystem : ISimSystem<MiningWorld, MiningForecast, MiningDiscovery>
{
    private static readonly string[] Minerals = ["Quartz", "Silver", "Opal"];

    private readonly long _sourceId;
    private readonly ulong _activityStreamId;
    private readonly ulong _mineralStreamId;
    private readonly ModelDuration _meanDiscoveryInterval;

    public MiningSystem(long sourceId, ulong activityStreamId, ModelDuration meanDiscoveryInterval)
    {
        _sourceId = sourceId;
        _activityStreamId = activityStreamId;
        _mineralStreamId = DeterministicRandom.DeriveStreamId(activityStreamId, "mineral");
        _meanDiscoveryInterval = meanDiscoveryInterval;
    }

    public IReadOnlyList<EventCandidate<MiningForecast>> ForecastNext(MiningWorld world, ModelTime now)
    {
        ulong generation = checked((ulong)world.DiscoveryCount);
        ModelDuration delay = DeterministicRandom.SampleExponentialDuration(
            world.WorldSeed,
            _activityStreamId,
            generation,
            _meanDiscoveryInterval);
        int mineralIndex = DeterministicRandom.SampleInt32(
            world.WorldSeed,
            _mineralStreamId,
            generation,
            minInclusive: 0,
            maxExclusive: Minerals.Length);
        var forecast = new MiningForecast(world.DiscoveryCount, Minerals[mineralIndex]);

        return
        [
            new EventCandidate<MiningForecast>(
                new EventCandidateId(world.DiscoveryCount),
                world.LastDiscoveryAt + delay,
                _sourceId,
                world.DiscoveryCount,
                forecast),
        ];
    }

    public ResolveResult<MiningWorld, MiningDiscovery> Resolve(
        MiningWorld world,
        EventCandidate<MiningForecast> candidate)
    {
        if (candidate.SourceId != _sourceId ||
            candidate.Generation != world.DiscoveryCount ||
            candidate.Payload.Generation != world.DiscoveryCount)
        {
            throw new InvalidOperationException("The mining forecast does not match the current activity generation.");
        }

        var discovery = new MiningDiscovery(
            world.DiscoveryCount,
            candidate.Due,
            candidate.Payload.Mineral);
        var nextWorld = new MiningWorld(
            world.WorldSeed,
            checked(world.DiscoveryCount + 1),
            candidate.Due,
            [.. world.Discoveries, discovery]);

        return new ResolveResult<MiningWorld, MiningDiscovery>(
            nextWorld,
            [new UncommittedDomainEvent<MiningDiscovery>("MineralDiscovered", discovery)]);
    }
}

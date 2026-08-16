using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal abstract record MinerActivity;

internal sealed record WaitingToMineActivity : MinerActivity;

internal sealed record MiningActivity(ModelTime StartedAt) : MinerActivity;

internal sealed record ConversationActivity(ModelTime StartedAt) : MinerActivity;

internal sealed record FinishedMiningActivity(ModelTime CompletedAt) : MinerActivity;

internal sealed record InterruptedMiningWorld(
    ulong WorldSeed,
    MinerActivity Activity,
    bool AliceAtMine,
    long DiscoveryGeneration,
    ModelTime LastDiscoveryAt)
{
    public static InterruptedMiningWorld Start(ulong worldSeed) =>
        new(worldSeed, new WaitingToMineActivity(), false, 0, ModelTime.Zero);
}

internal abstract record InterruptedMiningForecast;

internal sealed record StartMiningForecast : InterruptedMiningForecast;

internal sealed record CompleteMiningForecast : InterruptedMiningForecast;

internal sealed record InterruptMiningForecast : InterruptedMiningForecast;

internal sealed record DiscoverMineralForecast(long Generation, string Mineral) : InterruptedMiningForecast;

internal sealed record AliceArrivalForecast : InterruptedMiningForecast;

internal abstract record InterruptedMiningEvent;

internal sealed record MiningStartedEvent(ModelTime StartedAt) : InterruptedMiningEvent;

internal sealed record MiningCompletedEvent(ModelTime CompletedAt) : InterruptedMiningEvent;

internal sealed record MiningInterruptedEvent(
    ModelTime InterruptedAt,
    ModelDuration Elapsed,
    decimal ProgressFraction) : InterruptedMiningEvent;

internal sealed record MineralDiscoveredEvent(
    long Generation,
    ModelTime DiscoveredAt,
    string Mineral) : InterruptedMiningEvent;

internal sealed record AliceArrivedEvent(ModelTime ArrivedAt) : InterruptedMiningEvent;

internal sealed class InterruptedMiningSystem :
    ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>
{
    private static readonly string[] Minerals = ["Quartz", "Silver", "Opal"];

    private readonly long _sourceId;
    private readonly ModelDuration _completionDuration;
    private readonly ulong _activityStreamId;
    private readonly ulong _mineralStreamId;
    private readonly ModelDuration? _meanDiscoveryInterval;

    public InterruptedMiningSystem(
        long sourceId,
        ModelDuration completionDuration,
        ulong activityStreamId,
        ModelDuration? meanDiscoveryInterval = null)
    {
        if (completionDuration.Ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completionDuration));
        }

        if (meanDiscoveryInterval is { Ticks: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(meanDiscoveryInterval));
        }

        _sourceId = sourceId;
        _completionDuration = completionDuration;
        _activityStreamId = activityStreamId;
        _mineralStreamId = DeterministicRandom.DeriveStreamId(activityStreamId, "mineral");
        _meanDiscoveryInterval = meanDiscoveryInterval;
    }

    public IReadOnlyList<EventCandidate<InterruptedMiningForecast>> ForecastNext(
        InterruptedMiningWorld world,
        ModelTime now)
    {
        if (world.Activity is WaitingToMineActivity)
        {
            return [Candidate(0, ModelTime.Zero, 0, new StartMiningForecast())];
        }

        if (world.Activity is not MiningActivity mining)
        {
            return [];
        }

        if (world.AliceAtMine)
        {
            return
            [
                Candidate(
                    2,
                    now,
                    world.DiscoveryGeneration,
                    new InterruptMiningForecast()),
            ];
        }

        var candidates = new List<EventCandidate<InterruptedMiningForecast>>
        {
            Candidate(
                1,
                mining.StartedAt + _completionDuration,
                world.DiscoveryGeneration,
                new CompleteMiningForecast()),
        };

        if (_meanDiscoveryInterval is ModelDuration meanDiscoveryInterval)
        {
            ulong generation = checked((ulong)world.DiscoveryGeneration);
            ModelDuration delay = DeterministicRandom.SampleExponentialDuration(
                world.WorldSeed,
                _activityStreamId,
                generation,
                meanDiscoveryInterval);
            int mineralIndex = DeterministicRandom.SampleInt32(
                world.WorldSeed,
                _mineralStreamId,
                generation,
                minInclusive: 0,
                maxExclusive: Minerals.Length);

            candidates.Add(
                Candidate(
                    checked(100 + world.DiscoveryGeneration),
                    world.LastDiscoveryAt + delay,
                    world.DiscoveryGeneration,
                    new DiscoverMineralForecast(world.DiscoveryGeneration, Minerals[mineralIndex])));
        }

        return candidates;
    }

    public ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent> Resolve(
        InterruptedMiningWorld world,
        EventCandidate<InterruptedMiningForecast> candidate)
    {
        if (candidate.SourceId != _sourceId)
        {
            throw new InvalidOperationException("The mining candidate belongs to another source.");
        }

        return candidate.Payload switch
        {
            StartMiningForecast when world.Activity is WaitingToMineActivity => StartMining(world, candidate.Due),
            CompleteMiningForecast when world.Activity is MiningActivity mining && !world.AliceAtMine =>
                CompleteMining(world, mining, candidate.Due),
            InterruptMiningForecast when world.Activity is MiningActivity mining && world.AliceAtMine =>
                InterruptMining(world, mining, candidate.Due),
            DiscoverMineralForecast discovery when world.Activity is MiningActivity && !world.AliceAtMine =>
                DiscoverMineral(world, candidate, discovery),
            _ => throw new InvalidOperationException("The mining candidate is stale for the current activity."),
        };
    }

    private ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent> StartMining(
        InterruptedMiningWorld world,
        ModelTime startedAt)
    {
        InterruptedMiningWorld nextWorld = world with
        {
            Activity = new MiningActivity(startedAt),
            LastDiscoveryAt = startedAt,
        };

        return Result(nextWorld, "MiningStarted", new MiningStartedEvent(startedAt));
    }

    private ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent> CompleteMining(
        InterruptedMiningWorld world,
        MiningActivity mining,
        ModelTime completedAt)
    {
        if (completedAt != mining.StartedAt + _completionDuration)
        {
            throw new InvalidOperationException("The mining completion candidate has the wrong due time.");
        }

        InterruptedMiningWorld nextWorld = world with
        {
            Activity = new FinishedMiningActivity(completedAt),
        };

        return Result(nextWorld, "MiningCompleted", new MiningCompletedEvent(completedAt));
    }

    private ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent> InterruptMining(
        InterruptedMiningWorld world,
        MiningActivity mining,
        ModelTime interruptedAt)
    {
        ModelDuration elapsed = interruptedAt - mining.StartedAt;
        decimal progressFraction = elapsed.Ticks / (decimal)_completionDuration.Ticks;
        InterruptedMiningWorld nextWorld = world with
        {
            Activity = new ConversationActivity(interruptedAt),
        };
        var interrupted = new MiningInterruptedEvent(interruptedAt, elapsed, progressFraction);

        return Result(nextWorld, "MiningInterrupted", interrupted);
    }

    private ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent> DiscoverMineral(
        InterruptedMiningWorld world,
        EventCandidate<InterruptedMiningForecast> candidate,
        DiscoverMineralForecast discovery)
    {
        if (candidate.Generation != world.DiscoveryGeneration ||
            discovery.Generation != world.DiscoveryGeneration)
        {
            throw new InvalidOperationException("The discovery candidate has the wrong generation.");
        }

        var discovered = new MineralDiscoveredEvent(
            world.DiscoveryGeneration,
            candidate.Due,
            discovery.Mineral);
        InterruptedMiningWorld nextWorld = world with
        {
            DiscoveryGeneration = checked(world.DiscoveryGeneration + 1),
            LastDiscoveryAt = candidate.Due,
        };

        return Result(nextWorld, "MineralDiscovered", discovered);
    }

    private EventCandidate<InterruptedMiningForecast> Candidate(
        long candidateId,
        ModelTime due,
        long generation,
        InterruptedMiningForecast payload) =>
        new(new EventCandidateId(candidateId), due, _sourceId, generation, payload);

    private static ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent> Result(
        InterruptedMiningWorld world,
        string kind,
        InterruptedMiningEvent payload) =>
        new(world, [new UncommittedDomainEvent<InterruptedMiningEvent>(kind, payload)]);
}

internal sealed class AliceArrivalSystem :
    ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>
{
    private readonly long _sourceId;
    private readonly ModelTime _arrivalAt;

    public AliceArrivalSystem(long sourceId, ModelTime arrivalAt)
    {
        _sourceId = sourceId;
        _arrivalAt = arrivalAt;
    }

    public IReadOnlyList<EventCandidate<InterruptedMiningForecast>> ForecastNext(
        InterruptedMiningWorld world,
        ModelTime now) =>
        world.AliceAtMine
            ? []
            :
            [
                new EventCandidate<InterruptedMiningForecast>(
                    new EventCandidateId(0),
                    _arrivalAt,
                    _sourceId,
                    0,
                    new AliceArrivalForecast()),
            ];

    public ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent> Resolve(
        InterruptedMiningWorld world,
        EventCandidate<InterruptedMiningForecast> candidate)
    {
        if (candidate.SourceId != _sourceId || candidate.Payload is not AliceArrivalForecast)
        {
            throw new InvalidOperationException("The arrival candidate does not belong to Alice.");
        }

        InterruptedMiningWorld nextWorld = world with { AliceAtMine = true };
        var arrived = new AliceArrivedEvent(candidate.Due);

        return new ResolveResult<InterruptedMiningWorld, InterruptedMiningEvent>(
            nextWorld,
            [new UncommittedDomainEvent<InterruptedMiningEvent>("AliceArrived", arrived)]);
    }
}
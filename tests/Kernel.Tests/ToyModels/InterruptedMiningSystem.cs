using DramaBoard.Kernel.Journal;
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
    long MinerId,
    long AliceId,
    MinerActivity Activity,
    bool AliceAtMine,
    long DiscoveryGeneration,
    ModelTime LastDiscoveryAt,
    ModelTime? ScheduledAliceArrivalAt)
{
    public static InterruptedMiningWorld Start(ulong worldSeed, long minerId = 20, long aliceId = 10) =>
        new(worldSeed, minerId, aliceId, new WaitingToMineActivity(), false, 0, ModelTime.Zero, null);
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

internal sealed record AliceArrivalScheduledEvent(ModelTime ArrivalAt) : InterruptedMiningEvent;

internal static class InterruptedMiningEventKinds
{
    public static readonly EventKind MiningStarted = new("mining.started", 1);
    public static readonly EventKind MiningCompleted = new("mining.completed", 1);
    public static readonly EventKind MiningInterrupted = new("mining.interrupted", 1);
    public static readonly EventKind MineralDiscovered = new("interrupted-mining.mineral-discovered", 1);
    public static readonly EventKind AliceArrived = new("character.alice-arrived", 1);
    public static readonly EventKind AliceArrivalScheduled = new("character.alice-arrival-scheduled", 1);
}

internal sealed class InterruptedMiningSystem :
    ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>
{
    private static readonly string[] Minerals = ["Quartz", "Silver", "Opal"];

    private readonly ModelDuration _completionDuration;
    private readonly ulong _activityStreamId;
    private readonly ulong _mineralStreamId;
    private readonly ModelDuration? _meanDiscoveryInterval;

    public InterruptedMiningSystem(
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
            return [Candidate(world.MinerId, 0, ModelTime.Zero, new StartMiningForecast())];
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
                    world.MinerId,
                    2,
                    now,
                    new InterruptMiningForecast()),
            ];
        }

        var candidates = new List<EventCandidate<InterruptedMiningForecast>>
        {
            Candidate(
                world.MinerId,
                1,
                mining.StartedAt + _completionDuration,
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
                    world.MinerId,
                    checked(100 + world.DiscoveryGeneration),
                    world.LastDiscoveryAt + delay,
                    new DiscoverMineralForecast(world.DiscoveryGeneration, Minerals[mineralIndex])));
        }

        return candidates;
    }

    public IReadOnlyList<UncommittedDomainEvent<InterruptedMiningEvent>> Resolve(
        InterruptedMiningWorld world,
        EventCandidate<InterruptedMiningForecast> candidate)
    {
        if (candidate.SourceId != world.MinerId)
        {
            throw new InvalidOperationException("The mining candidate belongs to another source.");
        }

        return candidate.Payload switch
        {
            StartMiningForecast when world.Activity is WaitingToMineActivity => StartMining(candidate.Due),
            CompleteMiningForecast when world.Activity is MiningActivity mining && !world.AliceAtMine =>
                CompleteMining(mining, candidate.Due),
            InterruptMiningForecast when world.Activity is MiningActivity mining && world.AliceAtMine =>
                InterruptMining(mining, candidate.Due),
            DiscoverMineralForecast discovery when world.Activity is MiningActivity && !world.AliceAtMine =>
                DiscoverMineral(world, candidate, discovery),
            _ => throw new InvalidOperationException("The mining candidate is stale for the current activity."),
        };
    }

    private static IReadOnlyList<UncommittedDomainEvent<InterruptedMiningEvent>> StartMining(
        ModelTime startedAt) =>
        Result(InterruptedMiningEventKinds.MiningStarted, new MiningStartedEvent(startedAt));

    private IReadOnlyList<UncommittedDomainEvent<InterruptedMiningEvent>> CompleteMining(
        MiningActivity mining,
        ModelTime completedAt)
    {
        if (completedAt != mining.StartedAt + _completionDuration)
        {
            throw new InvalidOperationException("The mining completion candidate has the wrong due time.");
        }

        return Result(InterruptedMiningEventKinds.MiningCompleted, new MiningCompletedEvent(completedAt));
    }

    private IReadOnlyList<UncommittedDomainEvent<InterruptedMiningEvent>> InterruptMining(
        MiningActivity mining,
        ModelTime interruptedAt)
    {
        ModelDuration elapsed = interruptedAt - mining.StartedAt;
        decimal progressFraction = elapsed.Ticks / (decimal)_completionDuration.Ticks;
        var interrupted = new MiningInterruptedEvent(interruptedAt, elapsed, progressFraction);

        return Result(InterruptedMiningEventKinds.MiningInterrupted, interrupted);
    }

    private IReadOnlyList<UncommittedDomainEvent<InterruptedMiningEvent>> DiscoverMineral(
        InterruptedMiningWorld world,
        EventCandidate<InterruptedMiningForecast> candidate,
        DiscoverMineralForecast discovery)
    {
        if (discovery.Generation != world.DiscoveryGeneration)
        {
            throw new InvalidOperationException("The discovery candidate has the wrong generation.");
        }

        var discovered = new MineralDiscoveredEvent(
            world.DiscoveryGeneration,
            candidate.Due,
            discovery.Mineral);
        return Result(InterruptedMiningEventKinds.MineralDiscovered, discovered);
    }

    private EventCandidate<InterruptedMiningForecast> Candidate(
        long sourceId,
        long candidateId,
        ModelTime due,
        InterruptedMiningForecast payload) =>
        new(new EventCandidateId(candidateId), due, sourceId, payload);

    private static IReadOnlyList<UncommittedDomainEvent<InterruptedMiningEvent>> Result(
        EventKind kind,
        InterruptedMiningEvent payload) =>
        [new UncommittedDomainEvent<InterruptedMiningEvent>(kind, payload)];
}

internal sealed class AliceArrivalSystem :
    ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>
{
    public IReadOnlyList<EventCandidate<InterruptedMiningForecast>> ForecastNext(
        InterruptedMiningWorld world,
        ModelTime now) =>
        world.AliceAtMine || world.ScheduledAliceArrivalAt is not ModelTime arrivalAt
            ? []
            :
            [
                new EventCandidate<InterruptedMiningForecast>(
                    new EventCandidateId(0),
                    arrivalAt,
                    world.AliceId,
                    new AliceArrivalForecast()),
            ];

    public IReadOnlyList<UncommittedDomainEvent<InterruptedMiningEvent>> Resolve(
        InterruptedMiningWorld world,
        EventCandidate<InterruptedMiningForecast> candidate)
    {
        if (candidate.SourceId != world.AliceId || candidate.Payload is not AliceArrivalForecast)
        {
            throw new InvalidOperationException("The arrival candidate does not belong to Alice.");
        }

        var arrived = new AliceArrivedEvent(candidate.Due);

        return
        [
            new UncommittedDomainEvent<InterruptedMiningEvent>(
                InterruptedMiningEventKinds.AliceArrived,
                arrived),
        ];
    }
}

internal sealed class InterruptedMiningReducer : IEventReducer<InterruptedMiningWorld, InterruptedMiningEvent>
{
    public InterruptedMiningWorld Apply(
        InterruptedMiningWorld world,
        DomainEvent<InterruptedMiningEvent> domainEvent) =>
        (domainEvent.Kind.Id, domainEvent.Payload) switch
        {
            ({ } kindId, MiningStartedEvent started) when kindId == InterruptedMiningEventKinds.MiningStarted.Id =>
                world with
                {
                    Activity = new MiningActivity(started.StartedAt),
                    LastDiscoveryAt = started.StartedAt,
                },
            ({ } kindId, MiningCompletedEvent completed) when kindId == InterruptedMiningEventKinds.MiningCompleted.Id =>
                world with { Activity = new FinishedMiningActivity(completed.CompletedAt) },
            ({ } kindId, MiningInterruptedEvent interrupted) when kindId == InterruptedMiningEventKinds.MiningInterrupted.Id =>
                world with { Activity = new ConversationActivity(interrupted.InterruptedAt) },
            ({ } kindId, MineralDiscoveredEvent discovered)
                when kindId == InterruptedMiningEventKinds.MineralDiscovered.Id &&
                    discovered.Generation == world.DiscoveryGeneration =>
                world with
                {
                    DiscoveryGeneration = checked(world.DiscoveryGeneration + 1),
                    LastDiscoveryAt = discovered.DiscoveredAt,
                },
            ({ } kindId, AliceArrivedEvent) when kindId == InterruptedMiningEventKinds.AliceArrived.Id =>
                world with { AliceAtMine = true, ScheduledAliceArrivalAt = null },
            ({ } kindId, AliceArrivalScheduledEvent scheduled)
                when kindId == InterruptedMiningEventKinds.AliceArrivalScheduled.Id =>
                world with { ScheduledAliceArrivalAt = scheduled.ArrivalAt },
            _ => throw new InvalidOperationException($"Unknown or out-of-sequence event kind '{domainEvent.Kind.Id}'."),
        };
}

using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record TimerEntity(long Id, string Name, ModelTime Due);

internal sealed record TimerWorld(
    IReadOnlyList<TimerEntity> Timers,
    IReadOnlyList<string> FiredTimers)
{
    public static TimerWorld Start(params TimerEntity[] timers) => new(Array.AsReadOnly(timers), []);
}

internal static class TimerEventKinds
{
    public static readonly EventKind Fired = new("timer.fired", 1);
}

internal sealed class TimerSystem : ISimSystem<TimerWorld, string, string>
{
    public IReadOnlyList<EventCandidate<string>> ForecastNext(TimerWorld world, ModelTime now) =>
        [
            .. world.Timers
                .Where(timer => !world.FiredTimers.Contains(timer.Name))
                .Select(timer => new EventCandidate<string>(
                    new EventCandidateId(timer.Id),
                    timer.Due,
                    timer.Id,
                    timer.Name)),
        ];

    public IReadOnlyList<UncommittedDomainEvent<string>> Resolve(
        TimerWorld world,
        EventCandidate<string> candidate) =>
        [new UncommittedDomainEvent<string>(TimerEventKinds.Fired, candidate.Payload)];
}

internal sealed class TimerReducer : IEventReducer<TimerWorld, string>
{
    public TimerWorld Apply(TimerWorld world, DomainEvent<string> domainEvent) =>
        domainEvent.Kind.Id == TimerEventKinds.Fired.Id
            ? world with { FiredTimers = [.. world.FiredTimers, domainEvent.Payload] }
            : throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'.");
}

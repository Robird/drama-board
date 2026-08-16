using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record TimerWorld(IReadOnlyList<string> FiredTimers);

internal static class TimerEventKinds
{
    public static readonly EventKind Fired = new("timer.fired", 1);
}

internal sealed class TimerSystem : ISimSystem<TimerWorld, string, string>
{
    private readonly string _timerId;
    private readonly long _sourceId;
    private readonly ModelTime _due;

    public TimerSystem(string timerId, long sourceId, ModelTime due)
    {
        _timerId = timerId;
        _sourceId = sourceId;
        _due = due;
    }

    public IReadOnlyList<EventCandidate<string>> ForecastNext(TimerWorld world, ModelTime now) =>
        world.FiredTimers.Contains(_timerId)
            ? []
            : [new EventCandidate<string>(new EventCandidateId(_sourceId), _due, _sourceId, 0, _timerId)];

    public IReadOnlyList<UncommittedDomainEvent<string>> Resolve(
        TimerWorld world,
        EventCandidate<string> candidate) =>
        [new UncommittedDomainEvent<string>(TimerEventKinds.Fired, candidate.Payload)];
}

internal sealed class TimerReducer : IEventReducer<TimerWorld, string>
{
    public TimerWorld Apply(TimerWorld world, DomainEvent<string> domainEvent) =>
        domainEvent.Kind == TimerEventKinds.Fired
            ? new TimerWorld([.. world.FiredTimers, domainEvent.Payload])
            : throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'.");
}

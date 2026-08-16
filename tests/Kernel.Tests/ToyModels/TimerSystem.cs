using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record TimerWorld(IReadOnlyList<string> FiredTimers);

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

    public ResolveResult<TimerWorld, string> Resolve(
        TimerWorld world,
        EventCandidate<string> candidate)
    {
        var nextWorld = new TimerWorld([.. world.FiredTimers, candidate.Payload]);
        UncommittedDomainEvent<string>[] events = [new("TimerFired", candidate.Payload)];
        return new ResolveResult<TimerWorld, string>(nextWorld, events);
    }
}

using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Journal.Atelia.Tests;

internal sealed record CounterWorld(int Total, int NextStep, int FinalStep);

internal sealed record CounterCandidate(int Step);

internal sealed record CounterEvent(int Step, int Delta, string Route);

internal static class CounterEventKinds
{
    public static EventKind Advanced { get; } = new("counter.advanced", 1);
}

internal sealed class CounterSystem : ISimSystem<CounterWorld, CounterCandidate, CounterEvent>
{
    private const long SourceId = 101;

    public IReadOnlyList<EventCandidate<CounterCandidate>> ForecastNext(
        CounterWorld world,
        ModelTime now) =>
        world.NextStep > world.FinalStep
            ? []
            :
            [
                new EventCandidate<CounterCandidate>(
                    new EventCandidateId(world.NextStep),
                    new ModelTime(world.NextStep * 10L),
                    SourceId,
                    new CounterCandidate(world.NextStep)),
            ];

    public IReadOnlyList<UncommittedDomainEvent<CounterEvent>> Resolve(
        CounterWorld world,
        EventCandidate<CounterCandidate> candidate) =>
        candidate.Payload.Step != world.NextStep
            ? throw new InvalidOperationException("The counter candidate is stale.")
            :
            [
                new UncommittedDomainEvent<CounterEvent>(
                    CounterEventKinds.Advanced,
                    new CounterEvent(candidate.Payload.Step, candidate.Payload.Step, "loop")),
            ];
}

internal sealed class CounterReducer : IEventReducer<CounterWorld, CounterEvent>
{
    public CounterWorld Apply(CounterWorld world, DomainEvent<CounterEvent> domainEvent)
    {
        if (domainEvent.Kind != CounterEventKinds.Advanced)
        {
            throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'.");
        }

        return world with
        {
            Total = checked(world.Total + domainEvent.Payload.Delta),
            NextStep = checked(world.NextStep + 1),
        };
    }
}

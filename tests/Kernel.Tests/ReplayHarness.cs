using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests;

internal static class ReplayHarness
{
    public static TWorld Replay<TWorld, TEventPayload>(
        TWorld initialWorld,
        IEnumerable<DomainEvent<TEventPayload>> events,
        Func<TWorld, DomainEvent<TEventPayload>, TWorld> apply) =>
        events.Aggregate(initialWorld, apply);

    public static ReplayForkResult<TWorld, TEventPayload> Fork<TWorld, TCandidatePayload, TEventPayload>(
        TWorld initialWorld,
        ModelTime initialTime,
        IReadOnlyList<DomainEvent<TEventPayload>> journal,
        int eventCount,
        Func<TWorld, DomainEvent<TEventPayload>, TWorld> apply,
        IEnumerable<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> systems,
        ModelTime until)
    {
        if (eventCount < 0 || eventCount > journal.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(eventCount));
        }

        DomainEvent<TEventPayload>[] prefix = [.. journal.Take(eventCount)];
        TWorld forkWorld = Replay(initialWorld, prefix, apply);
        ModelTime forkTime = prefix.Length == 0
            ? initialTime
            : prefix[^1].Timestamp.ModelTime;
        var forkJournal = new InMemoryJournal<TEventPayload>();
        foreach (DomainEvent<TEventPayload> domainEvent in prefix)
        {
            forkJournal.Append(domainEvent);
        }

        var loop = new SimulationLoop<TWorld, TCandidatePayload, TEventPayload>(systems);
        SimulationRunResult<TWorld> result = loop.Run(forkWorld, forkTime, until, forkJournal);
        return new ReplayForkResult<TWorld, TEventPayload>(result, forkJournal);
    }
}

internal sealed record ReplayForkResult<TWorld, TEventPayload>(
    SimulationRunResult<TWorld> Result,
    InMemoryJournal<TEventPayload> Journal);

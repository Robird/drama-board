using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests;

internal static class ReplayHarness
{
    public static TWorld Replay<TWorld, TEventPayload>(
        TWorld initialWorld,
        IEnumerable<DomainEvent<TEventPayload>> events,
        IEventReducer<TWorld, TEventPayload> reducer) =>
        events.Aggregate(initialWorld, reducer.Apply);

    public static ReplayForkResult<TWorld, TEventPayload> Fork<TWorld, TCandidatePayload, TEventPayload>(
        TWorld initialWorld,
        ModelTime initialTime,
        long lineageId,
        IReadOnlyList<DomainEvent<TEventPayload>> journal,
        int eventCount,
        IEventReducer<TWorld, TEventPayload> reducer,
        IEnumerable<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> systems,
        ModelTime until,
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>>? externalInputs = null)
    {
        if (eventCount < 0 || eventCount > journal.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(eventCount));
        }

        if (eventCount > 0 &&
            eventCount < journal.Count &&
            journal[eventCount - 1].Cause.BatchOrdinal == journal[eventCount].Cause.BatchOrdinal)
        {
            throw new ArgumentException(
                "The fork point cannot cut through the middle of a committed event batch.",
                nameof(eventCount));
        }

        DomainEvent<TEventPayload>[] prefix = [.. journal.Take(eventCount)];
        TWorld forkWorld = Replay(initialWorld, prefix, reducer);
        ModelTime forkTime = prefix.Length == 0
            ? initialTime
            : prefix[^1].Timestamp.ModelTime;
        long nextBatchOrdinal = prefix.Length == 0
            ? 0
            : checked(prefix[^1].Cause.BatchOrdinal + 1);
        var forkJournal = new InMemoryJournal<TEventPayload>();
        foreach (DomainEvent<TEventPayload> domainEvent in prefix)
        {
            forkJournal.Append(domainEvent);
        }

        var loop = new SimulationLoop<TWorld, TCandidatePayload, TEventPayload>(systems, reducer);
        SimulationRunResult<TWorld, TEventPayload> result = loop.Run(
            forkWorld,
            SimulationCursor.CreateFork(lineageId, forkTime, nextBatchOrdinal),
            until,
            forkJournal,
            externalInputs);
        return new ReplayForkResult<TWorld, TEventPayload>(result, forkJournal);
    }
}

internal sealed record ReplayForkResult<TWorld, TEventPayload>(
    SimulationRunResult<TWorld, TEventPayload> Result,
    InMemoryJournal<TEventPayload> Journal);

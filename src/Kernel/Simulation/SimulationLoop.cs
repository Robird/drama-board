using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Runs deterministic Forecast, Advance, Resolve, and journal-commit cycles.</summary>
public sealed class SimulationLoop<TWorld, TCandidatePayload, TEventPayload>
{
    private readonly IReadOnlyList<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> _systems;

    /// <summary>Initializes a loop from the systems that participate in each full forecast.</summary>
    public SimulationLoop(
        IEnumerable<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> systems)
    {
        ArgumentNullException.ThrowIfNull(systems);

        ISimSystem<TWorld, TCandidatePayload, TEventPayload>[] systemArray = [.. systems];
        if (systemArray.Any(system => system is null))
        {
            throw new ArgumentException("Simulation systems cannot contain null entries.", nameof(systems));
        }

        _systems = systemArray;
    }

    /// <summary>Runs until no candidate remains or the next candidate would be later than the inclusive time boundary.</summary>
    public SimulationRunResult<TWorld> Run(
        TWorld initialWorld,
        ModelTime initialTime,
        ModelTime until,
        IJournalSink<TEventPayload> journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        if (until < initialTime)
        {
            throw new ArgumentOutOfRangeException(nameof(until), "The simulation boundary cannot precede its initial time.");
        }

        TWorld world = initialWorld;
        ModelTime now = initialTime;
        int timeAdvanceCount = 0;
        int resolvedCandidateCount = 0;
        LogicalTimestamp? lastCommittedTimestamp = journal.Events.Count == 0
            ? null
            : journal.Events[^1].Timestamp;

        while (true)
        {
            (ForecastQueue<TCandidatePayload> queue, Dictionary<EventCandidateId, ISimSystem<TWorld, TCandidatePayload, TEventPayload>> owners) =
                ForecastAll(world, now);

            if (!queue.TryPeekEarliest(out EventCandidate<TCandidatePayload> next))
            {
                return Result(world, now, timeAdvanceCount, resolvedCandidateCount);
            }

            if (next.Due < now)
            {
                throw new InvalidOperationException(
                    $"Candidate {next.Id} is due at {next.Due}, before current model time {now}.");
            }

            if (next.Due > until)
            {
                return Result(world, now, timeAdvanceCount, resolvedCandidateCount);
            }

            next = queue.DequeueEarliest();
            if (next.Due > now)
            {
                now = next.Due;
                timeAdvanceCount = checked(timeAdvanceCount + 1);
            }

            ResolveResult<TWorld, TEventPayload> resolved = owners[next.Id].Resolve(world, next)
                ?? throw new InvalidOperationException($"System resolving candidate {next.Id} returned null.");
            world = resolved.World;
            Commit(resolved.Events, now, journal, ref lastCommittedTimestamp);
            resolvedCandidateCount = checked(resolvedCandidateCount + 1);
        }
    }

    private (ForecastQueue<TCandidatePayload> Queue, Dictionary<EventCandidateId, ISimSystem<TWorld, TCandidatePayload, TEventPayload>> Owners)
        ForecastAll(TWorld world, ModelTime now)
    {
        var queue = new ForecastQueue<TCandidatePayload>();
        var owners = new Dictionary<EventCandidateId, ISimSystem<TWorld, TCandidatePayload, TEventPayload>>();

        foreach (ISimSystem<TWorld, TCandidatePayload, TEventPayload> system in _systems)
        {
            IReadOnlyList<EventCandidate<TCandidatePayload>> candidates = system.ForecastNext(world, now)
                ?? throw new InvalidOperationException("A simulation system returned a null forecast.");

            foreach (EventCandidate<TCandidatePayload> candidate in candidates)
            {
                queue.Enqueue(candidate);
                owners.Add(candidate.Id, system);
            }
        }

        return (queue, owners);
    }

    private static void Commit(
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>> events,
        ModelTime now,
        IJournalSink<TEventPayload> journal,
        ref LogicalTimestamp? lastCommittedTimestamp)
    {
        foreach (UncommittedDomainEvent<TEventPayload> uncommitted in events)
        {
            Microstep microstep = NextMicrostep(now, lastCommittedTimestamp);
            var timestamp = new LogicalTimestamp(now, microstep);
            var domainEvent = new DomainEvent<TEventPayload>(timestamp, uncommitted.Kind, uncommitted.Payload);

            journal.Append(domainEvent);
            lastCommittedTimestamp = timestamp;
        }
    }

    private static Microstep NextMicrostep(ModelTime now, LogicalTimestamp? lastCommittedTimestamp)
    {
        if (lastCommittedTimestamp is not LogicalTimestamp previous || previous.ModelTime < now)
        {
            return new Microstep(0);
        }

        if (previous.ModelTime > now)
        {
            throw new InvalidOperationException("A resolved event cannot be committed before the journal's latest model time.");
        }

        return new Microstep(checked(previous.Microstep.Value + 1));
    }

    private static SimulationRunResult<TWorld> Result(
        TWorld world,
        ModelTime now,
        int timeAdvanceCount,
        int resolvedCandidateCount) =>
        new(world, now, timeAdvanceCount, resolvedCandidateCount);
}
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Runs deterministic simulation cycles whose world updates come only from committed events.</summary>
public sealed class SimulationLoop<TWorld, TCandidatePayload, TEventPayload>
{
    private readonly IReadOnlyList<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> _systems;
    private readonly IEventReducer<TWorld, TEventPayload> _reducer;

    /// <summary>Initializes a loop from participating systems and the journal projection reducer.</summary>
    public SimulationLoop(
        IEnumerable<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> systems,
        IEventReducer<TWorld, TEventPayload> reducer)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(reducer);

        ISimSystem<TWorld, TCandidatePayload, TEventPayload>[] systemArray = [.. systems];
        if (systemArray.Any(system => system is null))
        {
            throw new ArgumentException("Simulation systems cannot contain null entries.", nameof(systems));
        }

        _systems = systemArray;
    _reducer = reducer;
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
        (long SourceId, EventCandidateId CandidateId, ModelTime Due)? lastResolvedCandidate = null;
        bool lastResolveProducedNoEvents = false;

        while (true)
        {
            (ForecastQueue<TCandidatePayload> queue, Dictionary<(long SourceId, EventCandidateId CandidateId), ISimSystem<TWorld, TCandidatePayload, TEventPayload>> owners) =
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

            var nextIdentity = (next.SourceId, next.Id, next.Due);
            if (lastResolveProducedNoEvents && lastResolvedCandidate == nextIdentity)
            {
                throw new InvalidOperationException(
                    $"Resolving candidate ({next.SourceId}, {next.Id}, {next.Due}) produced no events and " +
                    "the identical candidate remained next after re-forecasting, which would cause an infinite loop.");
            }

            next = queue.DequeueEarliest();
            if (next.Due > now)
            {
                now = next.Due;
                timeAdvanceCount = checked(timeAdvanceCount + 1);
            }

            IReadOnlyList<UncommittedDomainEvent<TEventPayload>> events =
                owners[(next.SourceId, next.Id)].Resolve(world, next)
                ?? throw new InvalidOperationException($"System resolving candidate {next.Id} returned null.");
            world = CommitAndApply(events, world, now, journal, ref lastCommittedTimestamp);
            lastResolvedCandidate = nextIdentity;
            lastResolveProducedNoEvents = events.Count == 0;
            resolvedCandidateCount = checked(resolvedCandidateCount + 1);
        }
    }

    private (ForecastQueue<TCandidatePayload> Queue, Dictionary<(long SourceId, EventCandidateId CandidateId), ISimSystem<TWorld, TCandidatePayload, TEventPayload>> Owners)
        ForecastAll(TWorld world, ModelTime now)
    {
        var queue = new ForecastQueue<TCandidatePayload>();
        var owners = new Dictionary<(long SourceId, EventCandidateId CandidateId), ISimSystem<TWorld, TCandidatePayload, TEventPayload>>();

        foreach (ISimSystem<TWorld, TCandidatePayload, TEventPayload> system in _systems)
        {
            IReadOnlyList<EventCandidate<TCandidatePayload>> candidates = system.ForecastNext(world, now)
                ?? throw new InvalidOperationException("A simulation system returned a null forecast.");

            foreach (EventCandidate<TCandidatePayload> candidate in candidates)
            {
                queue.Enqueue(candidate);
                owners.Add((candidate.SourceId, candidate.Id), system);
            }
        }

        return (queue, owners);
    }

    private TWorld CommitAndApply(
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>> events,
        TWorld world,
        ModelTime now,
        IJournalSink<TEventPayload> journal,
        ref LogicalTimestamp? lastCommittedTimestamp)
    {
        foreach (UncommittedDomainEvent<TEventPayload> uncommitted in events)
        {
            if (uncommitted is null)
            {
                throw new InvalidOperationException("A simulation system returned a null event description.");
            }

            Microstep microstep = NextMicrostep(now, lastCommittedTimestamp);
            var timestamp = new LogicalTimestamp(now, microstep);
            var domainEvent = new DomainEvent<TEventPayload>(timestamp, uncommitted.Kind, uncommitted.Payload);

            journal.Append(domainEvent);
            lastCommittedTimestamp = timestamp;
            world = _reducer.Apply(world, domainEvent);
        }

        return world;
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

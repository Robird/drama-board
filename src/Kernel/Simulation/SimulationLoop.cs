using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Runs deterministic simulation cycles whose world updates come only from committed events.</summary>
public sealed class SimulationLoop<TWorld, TCandidatePayload, TEventPayload>
{
    private const int DefaultMaxResolveCountPerModelTime = 10_000;

    private readonly IReadOnlyList<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> _systems;
    private readonly IEventReducer<TWorld, TEventPayload> _reducer;
    private readonly int _maxResolveCountPerModelTime;
    private readonly Func<DomainEvent<TEventPayload>, bool>? _decisionRequestPredicate;

    /// <summary>Initializes a loop from participating systems and the journal projection reducer.</summary>
    public SimulationLoop(
        IEnumerable<ISimSystem<TWorld, TCandidatePayload, TEventPayload>> systems,
        IEventReducer<TWorld, TEventPayload> reducer,
        int maxResolveCountPerModelTime = DefaultMaxResolveCountPerModelTime,
        Func<DomainEvent<TEventPayload>, bool>? decisionRequestPredicate = null)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(reducer);
        if (maxResolveCountPerModelTime <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResolveCountPerModelTime),
                "The resolve budget per model time must be positive.");
        }

        ISimSystem<TWorld, TCandidatePayload, TEventPayload>[] systemArray = [.. systems];
        if (systemArray.Any(system => system is null))
        {
            throw new ArgumentException("Simulation systems cannot contain null entries.", nameof(systems));
        }

        _systems = systemArray;
        _reducer = reducer;
        _maxResolveCountPerModelTime = maxResolveCountPerModelTime;
        _decisionRequestPredicate = decisionRequestPredicate;
    }

    /// <summary>Commits ready external inputs, then runs until exhaustion, a boundary, or a decision request.</summary>
    public SimulationRunResult<TWorld, TEventPayload> Run(
        TWorld initialWorld,
        SimulationCursor cursor,
        ModelTime until,
        IJournalSink<TEventPayload> journal,
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>>? externalInputs = null)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(journal);

        if (until < cursor.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(until), "The simulation boundary cannot precede its initial time.");
        }

        TWorld world = initialWorld;
        int timeAdvanceCount = 0;
        int resolvedCandidateCount = 0;
        LogicalTimestamp? lastCommittedTimestamp = journal.Events.Count == 0
            ? null
            : journal.Events[^1].Timestamp;

        if (externalInputs is { Count: > 0 })
        {
            List<DomainEvent<TEventPayload>>? committedEvents = _decisionRequestPredicate is null ? null : [];
            EventCause cause = EventCause.FromExternalInput(cursor.NextBatchOrdinal);
            world = CommitAndApply(
                externalInputs,
                cause,
                world,
                cursor.Now,
                journal,
                ref lastCommittedTimestamp,
                committedEvents);
            cursor = cursor.RecordExternalInputs();

            DomainEvent<TEventPayload>[] decisionEvents = committedEvents is null
                ? []
                : [.. committedEvents.Where(_decisionRequestPredicate!)];
            if (decisionEvents.Length > 0)
            {
                return Result(
                    world,
                    cursor,
                    StopReason.DecisionRequired,
                    journal,
                    decisionEvents,
                    timeAdvanceCount,
                    resolvedCandidateCount);
            }
        }

        while (true)
        {
            (ForecastQueue<TCandidatePayload> queue, Dictionary<(long SourceId, EventCandidateId CandidateId), ISimSystem<TWorld, TCandidatePayload, TEventPayload>> owners) =
                ForecastAll(world, cursor.Now);

            if (!queue.TryPeekEarliest(out EventCandidate<TCandidatePayload> next))
            {
                return Result(world, cursor, StopReason.Exhausted, journal, [], timeAdvanceCount, resolvedCandidateCount);
            }

            if (next.Due < cursor.Now)
            {
                throw new InvalidOperationException(
                    $"Candidate {next.Id} is due at {next.Due}, before current model time {cursor.Now}.");
            }

            if (next.Due > until)
            {
                return Result(world, cursor, StopReason.BoundaryReached, journal, [], timeAdvanceCount, resolvedCandidateCount);
            }

            var nextIdentity = new ResolvedCandidateIdentity(next.SourceId, next.Id, next.Due);
            if (cursor.IsRepeatedNoOp(nextIdentity))
            {
                throw new InvalidOperationException(
                    $"Resolving candidate ({next.SourceId}, {next.Id}, {next.Due}) produced no events and " +
                    "the identical candidate remained next after re-forecasting, which would cause an infinite loop.");
            }

            next = queue.DequeueEarliest();
            if (next.Due > cursor.Now)
            {
                cursor = cursor.AdvanceTo(next.Due);
                timeAdvanceCount = checked(timeAdvanceCount + 1);
            }

            if (cursor.ResolveCountAtCurrentTime >= _maxResolveCountPerModelTime)
            {
                string recentCandidate = cursor.LastResolvedCandidate is { } recent
                    ? $"(SourceId: {recent.SourceId}, CandidateId: {recent.CandidateId}, Due: {recent.Due})"
                    : "none";
                throw new InvalidOperationException(
                    $"Resolve budget of {_maxResolveCountPerModelTime} exhausted at model time {cursor.Now} " +
                    $"after {cursor.ResolveCountAtCurrentTime} resolves. Most recent resolved candidate: {recentCandidate}.");
            }

            IReadOnlyList<UncommittedDomainEvent<TEventPayload>> events =
                owners[(next.SourceId, next.Id)].Resolve(world, next)
                ?? throw new InvalidOperationException($"System resolving candidate {next.Id} returned null.");
            List<DomainEvent<TEventPayload>>? committedEvents = _decisionRequestPredicate is null ? null : [];
            EventCause cause = EventCause.FromResolve(next.SourceId, next.Id, next.Due, cursor.NextBatchOrdinal);
            world = CommitAndApply(
                events,
                cause,
                world,
                cursor.Now,
                journal,
                ref lastCommittedTimestamp,
                committedEvents);
            cursor = cursor.RecordResolve(nextIdentity, events.Count == 0);
            resolvedCandidateCount = checked(resolvedCandidateCount + 1);

            DomainEvent<TEventPayload>[] decisionEvents = committedEvents is null
                ? []
                : [.. committedEvents.Where(_decisionRequestPredicate!)];
            if (decisionEvents.Length > 0)
            {
                return Result(
                    world,
                    cursor,
                    StopReason.DecisionRequired,
                    journal,
                    decisionEvents,
                    timeAdvanceCount,
                    resolvedCandidateCount);
            }
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
        EventCause cause,
        TWorld world,
        ModelTime now,
        IJournalSink<TEventPayload> journal,
        ref LogicalTimestamp? lastCommittedTimestamp,
        ICollection<DomainEvent<TEventPayload>>? committedEvents = null)
    {
        var batch = new List<DomainEvent<TEventPayload>>(events.Count);
        LogicalTimestamp? nextTimestamp = lastCommittedTimestamp;
        foreach (UncommittedDomainEvent<TEventPayload> uncommitted in events)
        {
            if (uncommitted is null)
            {
                throw new InvalidOperationException("A simulation system returned a null event description.");
            }

            Microstep microstep = NextMicrostep(now, nextTimestamp);
            var timestamp = new LogicalTimestamp(now, microstep);
            var domainEvent = new DomainEvent<TEventPayload>(timestamp, cause, uncommitted.Kind, uncommitted.Payload);

            batch.Add(domainEvent);
            nextTimestamp = timestamp;
        }

        journal.AppendBatch(batch);
        foreach (DomainEvent<TEventPayload> domainEvent in batch)
        {
            world = _reducer.Apply(world, domainEvent);
            committedEvents?.Add(domainEvent);
        }

        lastCommittedTimestamp = nextTimestamp;

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

    private static SimulationRunResult<TWorld, TEventPayload> Result(
        TWorld world,
        SimulationCursor cursor,
        StopReason stopReason,
        IJournalSink<TEventPayload> journal,
        IReadOnlyList<DomainEvent<TEventPayload>> decisionEvents,
        int timeAdvanceCount,
        int resolvedCandidateCount) =>
        new(
            world,
            cursor,
            stopReason,
            new WorldVersion(cursor.LineageId, journal.Events.Count),
            decisionEvents,
            timeAdvanceCount,
            resolvedCandidateCount);
}

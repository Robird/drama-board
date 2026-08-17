using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.Host;

/// <summary>Advances a simulation and submits each simultaneous decision set as one input batch.</summary>
public sealed class PlayerDecisionSession<TWorld, TCandidatePayload, TEventPayload>
{
    private const long ForcedWaitDurationMs = 60_000;

    private readonly SimulationLoop<TWorld, TCandidatePayload, TEventPayload> _loop;
    private readonly IJournalSink<TEventPayload> _journal;
    private readonly Func<DomainEvent<TEventPayload>, string> _actorSelector;
    private readonly IReadOnlyDictionary<string, IPlayerDriver> _drivers;
    private readonly Func<TWorld, DomainEvent<TEventPayload>, WorldVersion, DecisionRequest?> _requestBuilder;
    private readonly Func<PlayerDecision, TWorld, IReadOnlyList<UncommittedDomainEvent<TEventPayload>>> _decisionTranslator;
    private readonly int _maxConsecutiveRejectionsPerActor;
    private readonly Func<DomainEvent<TEventPayload>, string?>? _rejectionSelector;
    private readonly Dictionary<string, RejectionStreak> _rejectionStreaksByActor = new(StringComparer.Ordinal);
    private readonly List<PendingDecision<TEventPayload>> _pendingDecisions = [];
    private TWorld _world;
    private SimulationCursor _cursor;
    private int _runInProgress;

    /// <summary>Initializes a session from its simulation state and domain translation functions.</summary>
    public PlayerDecisionSession(
        SimulationLoop<TWorld, TCandidatePayload, TEventPayload> loop,
        IJournalSink<TEventPayload> journal,
        TWorld initialWorld,
        SimulationCursor initialCursor,
        Func<DomainEvent<TEventPayload>, string> actorSelector,
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        Func<TWorld, DomainEvent<TEventPayload>, WorldVersion, DecisionRequest?> requestBuilder,
        Func<PlayerDecision, TWorld, IReadOnlyList<UncommittedDomainEvent<TEventPayload>>> decisionTranslator,
        int maxConsecutiveRejectionsPerActor = 8,
        Func<DomainEvent<TEventPayload>, string?>? rejectionSelector = null)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(initialCursor);
        ArgumentNullException.ThrowIfNull(actorSelector);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(requestBuilder);
        ArgumentNullException.ThrowIfNull(decisionTranslator);
        if (maxConsecutiveRejectionsPerActor < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConsecutiveRejectionsPerActor),
                maxConsecutiveRejectionsPerActor,
                "The consecutive rejection budget must be positive.");
        }

        var driverCopy = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal);
        foreach ((string actorId, IPlayerDriver driver) in drivers)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new ArgumentException("Player driver actor identifiers cannot be empty.", nameof(drivers));
            }

            if (driver is null)
            {
                throw new ArgumentException("Player drivers cannot contain null values.", nameof(drivers));
            }

            driverCopy.Add(actorId, driver);
        }

        _loop = loop;
        _journal = journal;
        _world = initialWorld;
        _cursor = initialCursor;
        _actorSelector = actorSelector;
        _drivers = driverCopy;
        _requestBuilder = requestBuilder;
        _decisionTranslator = decisionTranslator;
        _maxConsecutiveRejectionsPerActor = maxConsecutiveRejectionsPerActor;
        _rejectionSelector = rejectionSelector;
    }

    /// <summary>Gets the decision batch currently retained by the session.</summary>
    public IReadOnlyList<PendingDecision<TEventPayload>> PendingDecisions => _pendingDecisions.AsReadOnly();

    /// <summary>Runs through decision points until the simulation is exhausted or reaches the boundary.</summary>
    public ValueTask<PlayerDecisionSessionResult<TWorld>> RunUntilAsync(
        ModelTime until,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _runInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A Player decision session permits only one in-flight RunUntilAsync call.");
        }

        return RunSingleFlightAsync(until, cancellationToken);
    }

    private async ValueTask<PlayerDecisionSessionResult<TWorld>> RunSingleFlightAsync(
        ModelTime until,
        CancellationToken cancellationToken)
    {
        try
        {
            int decisionCount = 0;
            int skippedDecisionCount = 0;
            int forcedDecisionCount = 0;
            int validationFailedDecisionCount = 0;

            cancellationToken.ThrowIfCancellationRequested();
            SimulationRunResult<TWorld, TEventPayload>? run = null;
            if (_pendingDecisions.Count == 0)
            {
                run = RunSimulation(until, externalInputs: null);
                CaptureDecisionBatch(run);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureUniquePendingActors();
                if (_pendingDecisions.Count == 0)
                {
                    if (run is not null && run.StopReason != StopReason.DecisionRequired)
                    {
                        return new PlayerDecisionSessionResult<TWorld>(
                            _world,
                            _cursor,
                            run.Version,
                            run.StopReason,
                            decisionCount,
                            skippedDecisionCount,
                            forcedDecisionCount,
                            pendingDecisionCount: 0,
                            validationFailedDecisionCount);
                    }

                    run = RunSimulation(until, externalInputs: null);
                    CaptureDecisionBatch(run);
                    continue;
                }

                WorldVersion batchVersion = CurrentVersion();
                foreach (PendingDecision<TEventPayload> pending in _pendingDecisions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pending.Status == PendingDecisionStatus.Answered || pending.IsTerminalInvalidation)
                    {
                        continue;
                    }

                    DecisionRequest? request = BuildRequest(
                        pending.RequestEvent,
                        batchVersion,
                        pending.RequestEvent.Timestamp);
                    if (request is null)
                    {
                        pending.Status = PendingDecisionStatus.Invalidated;
                        pending.InvalidationReason = PendingDecisionInvalidationReason.StaleRequest;
                        continue;
                    }

                    ValidateRequestActor(request, pending.ActorId);
                    bool isForcedDecision = ShouldForceDecision(pending.ActorId);
                    PlayerDecision decision;
                    if (isForcedDecision)
                    {
                        decision = ForcedWait(request);
                    }
                    else
                    {
                        if (!_drivers.TryGetValue(pending.ActorId, out IPlayerDriver? driver))
                        {
                            throw new InvalidOperationException(
                                $"No Player driver is registered for actor '{pending.ActorId}'.");
                        }

                        decision = await driver.DecideAsync(request, cancellationToken)
                            ?? throw new InvalidOperationException("A Player driver returned null.");
                    }

                    string? validationFailure = DecisionValidationFailure(decision, request);
                    if (validationFailure is not null)
                    {
                        pending.Status = PendingDecisionStatus.Invalidated;
                        pending.InvalidationReason = PendingDecisionInvalidationReason.ValidationFailed;
                        pending.ValidationFailureCount = checked(pending.ValidationFailureCount + 1);
                        if (pending.ValidationFailureCount >= 2)
                        {
                            throw new InvalidOperationException(validationFailure);
                        }

                        continue;
                    }

                    pending.Status = PendingDecisionStatus.Answered;
                    pending.InvalidationReason = null;
                    pending.Answer = decision;
                    pending.IsForced = isForcedDecision;
                }

                if (_pendingDecisions.Any(pending =>
                    pending.Status == PendingDecisionStatus.Open ||
                    pending.InvalidationReason == PendingDecisionInvalidationReason.ValidationFailed))
                {
                    continue;
                }

                var decisionInputs = new List<UncommittedDomainEvent<TEventPayload>>();
                var submittedDecisions = new List<SubmittedDecision>();
                skippedDecisionCount = checked(skippedDecisionCount + _pendingDecisions.Count(pending =>
                    pending.IsTerminalInvalidation));
                validationFailedDecisionCount = checked(
                    validationFailedDecisionCount +
                    _pendingDecisions.Sum(pending => pending.ValidationFailureCount));
                foreach (PendingDecision<TEventPayload> pending in _pendingDecisions.Where(current =>
                    current.Status == PendingDecisionStatus.Answered))
                {
                    IReadOnlyList<UncommittedDomainEvent<TEventPayload>> translated =
                        _decisionTranslator(pending.Answer!, _world)
                        ?? throw new InvalidOperationException("The decision translator returned null.");
                    decisionCount = checked(decisionCount + 1);
                    if (pending.IsForced)
                    {
                        forcedDecisionCount = checked(forcedDecisionCount + 1);
                    }

                    if (translated.Count == 0)
                    {
                        RecordRejectedOrNonProgressing(pending.ActorId);
                        if (pending.IsForced)
                        {
                            throw ForcedWaitRejected(
                                pending.ActorId,
                                "the decision translator produced no input events");
                        }
                    }

                    decisionInputs.AddRange(translated);
                    submittedDecisions.Add(new SubmittedDecision(
                        pending.ActorId,
                        translated.Count > 0,
                        pending.IsForced));
                }

                int firstNewEventIndex = _journal.Events.Count;
                ModelTime previousModelTime = _cursor.Now;
                run = RunSimulation(until, decisionInputs);
                foreach (SubmittedDecision submitted in submittedDecisions.Where(current => current.HasInputs))
                {
                    bool wasRejected = WasRejected(submitted.ActorId, firstNewEventIndex);
                    if (submitted.IsForced && wasRejected)
                    {
                        throw ForcedWaitRejected(
                            submitted.ActorId,
                            "the rejection selector matched a new journal event");
                    }

                    if (wasRejected && _cursor.Now == previousModelTime)
                    {
                        RecordRejectedOrNonProgressing(submitted.ActorId);
                    }
                    else
                    {
                        ClearConsecutiveRejections(submitted.ActorId);
                    }
                }

                _pendingDecisions.Clear();
                CaptureDecisionBatch(run);
            }
        }
        finally
        {
            Volatile.Write(ref _runInProgress, 0);
        }
    }

    private SimulationRunResult<TWorld, TEventPayload> RunSimulation(
        ModelTime until,
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>>? externalInputs)
    {
        SimulationRunResult<TWorld, TEventPayload> run = _loop.Run(
            _world,
            _cursor,
            until,
            _journal,
            externalInputs);
        _world = run.World;
        _cursor = run.Cursor;
        return run;
    }

    private bool ShouldForceDecision(string actorId)
    {
        if (_rejectionSelector is null ||
            !_rejectionStreaksByActor.TryGetValue(actorId, out RejectionStreak streak))
        {
            return false;
        }

        if (streak.ModelTime != _cursor.Now)
        {
            _rejectionStreaksByActor.Remove(actorId);
            return false;
        }

        return streak.Count >= _maxConsecutiveRejectionsPerActor;
    }

    private bool WasRejected(string actorId, int firstNewEventIndex)
    {
        if (_rejectionSelector is null)
        {
            return false;
        }

        for (int index = firstNewEventIndex; index < _journal.Events.Count; index++)
        {
            if (string.Equals(_rejectionSelector(_journal.Events[index]), actorId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void RecordRejectedOrNonProgressing(string actorId)
    {
        if (_rejectionSelector is null)
        {
            return;
        }

        int nextCount = _rejectionStreaksByActor.TryGetValue(actorId, out RejectionStreak current) &&
            current.ModelTime == _cursor.Now
                ? checked(current.Count + 1)
                : 1;
        _rejectionStreaksByActor[actorId] = new RejectionStreak(nextCount, _cursor.Now);
    }

    private void ClearConsecutiveRejections(string actorId)
    {
        if (_rejectionSelector is not null)
        {
            _rejectionStreaksByActor.Remove(actorId);
        }
    }

    private static PlayerDecision ForcedWait(DecisionRequest request) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            new Intent(ActionKinds.Wait, DurationMs: ForcedWaitDurationMs));

    private InvalidOperationException ForcedWaitRejected(string actorId, string detail) =>
        new(
            $"Forced wait for actor '{actorId}' was rejected after " +
            $"{_maxConsecutiveRejectionsPerActor} consecutive rejected or non-progressing decisions at " +
            $"model time {_cursor.Now.Ticks}: {detail}.");

    private readonly record struct RejectionStreak(int Count, ModelTime ModelTime);

    private readonly record struct SubmittedDecision(string ActorId, bool HasInputs, bool IsForced);

    private WorldVersion CurrentVersion() => new(_cursor.LineageId, _journal.Events.Count);

    private void CaptureDecisionBatch(SimulationRunResult<TWorld, TEventPayload> run)
    {
        if (run.DecisionEvents.Count == 0)
        {
            return;
        }

        foreach (DomainEvent<TEventPayload> decisionEvent in run.DecisionEvents)
        {
            string actorId = _actorSelector(decisionEvent);
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new InvalidOperationException("The decision actor selector returned an empty actor identifier.");
            }

            _pendingDecisions.Add(new PendingDecision<TEventPayload>(decisionEvent, actorId));
        }

        EnsureUniquePendingActors();
    }

    private void EnsureUniquePendingActors()
    {
        string? duplicateActorId = _pendingDecisions
            .GroupBy(pending => pending.ActorId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateActorId is not null)
        {
            throw new InvalidOperationException(
                $"A simultaneous decision batch contains more than one request for actor '{duplicateActorId}'.");
        }
    }

    private static void ValidateRequestActor(DecisionRequest request, string actorId)
    {
        if (!string.Equals(request.ActorId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The decision request actor '{request.ActorId}' does not match routed actor '{actorId}'.");
        }
    }

    private DecisionRequest? BuildRequest(
        DomainEvent<TEventPayload> decisionEvent,
        WorldVersion version,
        LogicalTimestamp currentTimestamp)
    {
        DecisionRequest? built = _requestBuilder(_world, decisionEvent, version);
        if (built is null)
        {
            return null;
        }

        Observation observation = built.Observation
            ?? throw new InvalidOperationException("The decision request builder returned a null observation.");
        observation = observation with
        {
            ModelTimeMs = currentTimestamp.ModelTime.Ticks,
            Microstep = currentTimestamp.Microstep.Value,
        };

        return built with
        {
            BasedOnWorldVersion = version.EventCount,
            LineageId = version.LineageId,
            ModelTimeMs = currentTimestamp.ModelTime.Ticks,
            Microstep = currentTimestamp.Microstep.Value,
            Observation = observation,
        };
    }

    private static string? DecisionValidationFailure(
        PlayerDecision decision,
        DecisionRequest request)
    {
        if (decision.DecisionId != request.DecisionId)
        {
            return "The Player decision does not match the requested DecisionId.";
        }

        if (decision.BasedOnWorldVersion != request.BasedOnWorldVersion)
        {
            return "The Player decision is based on a stale world version.";
        }

        if (decision.LineageId != request.LineageId)
        {
            return "The Player decision belongs to a different world lineage.";
        }

        return null;
    }
}

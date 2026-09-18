using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Plans one occurrence, records its validated Event, then publishes its complete State.</summary>
public sealed class SimulationKernel<TWorld, TCandidateData, TFact> {
    private readonly SimulationRules _simulationRules;
    private readonly IReadOnlyList<IOccurrenceRule<TWorld, TCandidateData, TFact>> _rules;
    private readonly IOccurrenceHistory<TWorld, TFact> _history;
    private readonly Func<TWorld, LogicalInstant, TFact, TWorld> _fold;
    private readonly Action<TWorld> _validate;
    private TWorld _world;
    private KernelCursor _cursor;
    private int _stepInFlight;

    /// <summary>Loads a completed boundary. Complete pending work explicitly with RecoverPending.</summary>
    public SimulationKernel(IOccurrenceHistory<TWorld, TFact> history, SimulationRules simulationRules,
        IEnumerable<IOccurrenceRule<TWorld, TCandidateData, TFact>> rules,
        Func<TWorld, LogicalInstant, TFact, TWorld> fold, Action<TWorld> validate) {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(simulationRules);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(history.State);
        IOccurrenceRule<TWorld, TCandidateData, TFact>[] ruleArray = [.. rules];
        if (ruleArray.Any(rule => rule is null)) {
            throw new ArgumentException("Occurrence rules cannot contain null entries.", nameof(rules));
        }
        history.Cursor.Validate();
        validate(history.State);
        _world = history.State;
        _cursor = history.Cursor;
        _simulationRules = simulationRules;
        _rules = Array.AsReadOnly(ruleArray);
        _history = history;
        _fold = fold;
        _validate = validate;
    }

    public TWorld World => _world;
    public KernelCursor Cursor => _cursor;
    public WorldVersion Version => _cursor.Version;
    public LogicalInstant? LastCommittedInstant => _cursor.LastInstant;
    public ModelTime CurrentModelTime => _cursor.CurrentModelTime;
    public bool IsFaulted { get; private set; }
    public OccurrenceCompletion<TFact>? LastCompletion { get; private set; }

    /// <summary>Advances by one complete occurrence at most; pending work requires RecoverPending.</summary>
    public ValueTask<StepStatus> StepAsync(ModelTime notAfter, CancellationToken cancellationToken = default) {
        BeginOperation();
        return StepCoreAsync(notAfter, cancellationToken);
    }

    /// <summary>Completes only the published pending Event without Forecast, Plan or Player calls.
    /// Returns false if none exists. Cancellation is checked before recovery begins.</summary>
    public bool RecoverPending(CancellationToken cancellationToken = default) {
        BeginOperation();
        try {
            EnsureHistoryAligned();
            cancellationToken.ThrowIfCancellationRequested();
            OccurrenceEvent<TFact>? occurrence = _history.PendingEvent;
            if (occurrence is null) { return false; }
            try {
                occurrence.Validate();
                RequireProgress(occurrence.CauseKey);
                LogicalInstant expectedInstant = LogicalInstantRules.Propose(
                    new CandidateDue(occurrence.TargetInstant.ModelTime), _cursor.GenesisTime,
                    _cursor.LastInstant, _simulationRules.MaxTransitionsPerModelTime);
                if (occurrence.TargetInstant != expectedInstant) {
                    throw new InvalidOperationException("Pending Event target instant does not continue the completed cursor.");
                }
                KernelCursor nextCursor = _cursor.Advance(occurrence.CauseKey, occurrence.TargetInstant);
                TWorld scratch = FoldAndValidate(occurrence.TargetInstant, occurrence.Facts);
                PublishStateAndInstall(occurrence, scratch, nextCursor);
                return true;
            } catch {
                IsFaulted = true;
                throw;
            }
        } finally { EndOperation(); }
    }

    private async ValueTask<StepStatus> StepCoreAsync(ModelTime notAfter, CancellationToken cancellationToken) {
        try {
            EnsureHistoryAligned();
            if (_history.PendingEvent is not null) {
                throw new InvalidOperationException("Call RecoverPending before requesting a new Step.");
            }
            if (notAfter < CurrentModelTime) {
                throw new ArgumentOutOfRangeException(nameof(notAfter), "The Step boundary cannot precede the current committed model time.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            ForecastWinner<TWorld, TCandidateData, TFact>? selection =
                ForecastRound.SelectWinner(_world, CurrentModelTime, _simulationRules, _rules);
            if (selection is null) { return StepStatus.Exhausted; }
            OccurrenceCandidate<TCandidateData> winner = selection.Candidate;
            RequireProgress(winner.Key);
            if (winner.Due.ModelTime > notAfter) { return StepStatus.BoundaryReached; }
            LogicalInstant nextInstant = LogicalInstantRules.Propose(
                winner.Due, _cursor.GenesisTime, _cursor.LastInstant, _simulationRules.MaxTransitionsPerModelTime);
            KernelCursor nextCursor = _cursor.Advance(winner.Key, nextInstant);
            TransitionDraft<TFact> draft = await selection.Owner
                .PlanSelectedAsync(_world, winner, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The selected occurrence rule returned a null draft.");
            TWorld scratch = FoldAndValidate(nextInstant, draft.Facts);
            var occurrence = new OccurrenceEvent<TFact>(winner.Key, nextInstant, draft.Facts);
            cancellationToken.ThrowIfCancellationRequested();
            try {
                _history.CommitEvent(occurrence);
                if (!SameOccurrence(_history.PendingEvent, occurrence)) {
                    throw new InvalidOperationException("History did not expose exactly the proposed pending Event.");
                }
            } catch (Exception error) { throw PublicationFailure(error); }
            // E is durable work: ordinary cancellation cannot interrupt its completion.
            PublishStateAndInstall(occurrence, scratch, nextCursor);
            return StepStatus.Committed;
        } finally { EndOperation(); }
    }

    private TWorld FoldAndValidate(LogicalInstant instant, IReadOnlyList<TFact> facts) {
        TWorld scratch = _world;
        foreach (TFact fact in facts) {
            scratch = _fold(scratch, instant, fact);
            if (scratch is null) { throw new InvalidOperationException("The fact fold returned a null HostWorld."); }
        }
        _validate(scratch);
        return scratch;
    }

    private void PublishStateAndInstall(OccurrenceEvent<TFact> occurrence, TWorld scratch, KernelCursor nextCursor) {
        try {
            _history.CommitState(scratch, nextCursor);
            if (_history.Cursor != nextCursor || _history.PendingEvent is not null ||
                !SameWorld(_history.State, scratch)) {
                throw new InvalidOperationException("History did not expose the completed State cursor.");
            }
        } catch (Exception error) { throw PublicationFailure(error); }
        _world = scratch;
        _cursor = nextCursor;
        LastCompletion = new OccurrenceCompletion<TFact>(nextCursor, occurrence);
    }

    private static bool SameOccurrence(OccurrenceEvent<TFact>? actual, OccurrenceEvent<TFact> expected) =>
        actual is not null && actual.TargetInstant == expected.TargetInstant &&
        actual.CauseKey == expected.CauseKey && actual.Facts.SequenceEqual(expected.Facts);

    private void RequireProgress(CandidateKey key) {
        if (_cursor.LastCauseKey == key) {
            throw new InvalidOperationException($"Candidate '{key}' repeated immediately after it was committed; the owning rule made no key-visible authoritative progress.");
        }
    }

    private static bool SameWorld(TWorld actual, TWorld expected) => typeof(TWorld).IsValueType
        ? EqualityComparer<TWorld>.Default.Equals(actual, expected)
        : ReferenceEquals(actual, expected);

    private void EnsureHistoryAligned() {
        if (_history.Cursor != _cursor || !SameWorld(_history.State, _world)) {
            IsFaulted = true;
            throw new InvalidOperationException("History moved outside this Kernel; stop and reopen the session.");
        }
    }

    private InvalidOperationException PublicationFailure(Exception error) {
        IsFaulted = true;
        return new InvalidOperationException("History publication failed; its outcome cannot be safely determined. Stop and reopen the session.", error);
    }

    private void BeginOperation() {
        if (Interlocked.CompareExchange(ref _stepInFlight, 1, 0) != 0) {
            throw new InvalidOperationException("Another simulation operation is already in flight for this lineage.");
        }
        if (IsFaulted) {
            EndOperation();
            throw new InvalidOperationException("This Kernel is faulted; stop and reopen the session.");
        }
        LastCompletion = null;
    }

    private void EndOperation() => Volatile.Write(ref _stepInFlight, 0);
}

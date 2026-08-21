using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Forecasts, selects, plans, validates, and atomically commits one occurrence per step.</summary>
public sealed class SimulationKernel<TWorld, TCandidateData, TFact>
{
    private readonly ModelTime _genesisTime;
    private readonly SimulationRules _simulationRules;
    private readonly IReadOnlyList<IOccurrenceRule<TWorld, TCandidateData, TFact>> _rules;
    private readonly IJournalSink<TFact> _journal;
    private readonly Func<TWorld, LogicalInstant, TFact, TWorld> _fold;
    private readonly Action<TWorld> _validate;

    private TWorld _world;
    private WorldVersion _version;
    private LogicalInstant? _lastCommittedInstant;
    private int _stepInFlight;
    private bool _requiresReplay;

    /// <summary>Initializes a kernel at an already committed journal boundary.</summary>
    public SimulationKernel(
        TWorld committedWorld,
        WorldVersion worldVersion,
        ModelTime genesisTime,
        LogicalInstant? lastCommittedInstant,
        SimulationRules simulationRules,
        IEnumerable<IOccurrenceRule<TWorld, TCandidateData, TFact>> rules,
        IJournalSink<TFact> journal,
        Func<TWorld, LogicalInstant, TFact, TWorld> fold,
        Action<TWorld> validate)
    {
        if (committedWorld is null)
        {
            throw new ArgumentNullException(nameof(committedWorld));
        }

        ArgumentNullException.ThrowIfNull(simulationRules);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(validate);

        IOccurrenceRule<TWorld, TCandidateData, TFact>[] ruleArray = [.. rules];
        if (ruleArray.Any(rule => rule is null))
        {
            throw new ArgumentException("Occurrence rules cannot contain null entries.", nameof(rules));
        }

        ValidateCommittedBoundary(worldVersion, genesisTime, lastCommittedInstant, journal);
        validate(committedWorld);

        _world = committedWorld;
        _version = worldVersion;
        _genesisTime = genesisTime;
        _lastCommittedInstant = lastCommittedInstant;
        _simulationRules = simulationRules;
        _rules = Array.AsReadOnly(ruleArray);
        _journal = journal;
        _fold = fold;
        _validate = validate;
    }

    /// <summary>Gets the currently installed committed world.</summary>
    public TWorld World => _world;

    /// <summary>Gets the currently installed committed transition version.</summary>
    public WorldVersion Version => _version;

    /// <summary>Gets the last committed occurrence instant, or null for an empty lineage.</summary>
    public LogicalInstant? LastCommittedInstant => _lastCommittedInstant;

    /// <summary>Gets the current committed model time without fabricating boundary advances.</summary>
    public ModelTime CurrentModelTime => _lastCommittedInstant?.ModelTime ?? _genesisTime;

    /// <summary>Advances by at most one complete occurrence transition.</summary>
    public ValueTask<StepStatus> StepAsync(
        ModelTime notAfter,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _stepInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Another simulation Step is already in flight for this lineage.");
        }

        if (_requiresReplay)
        {
            Volatile.Write(ref _stepInFlight, 0);
            throw ReplayRequiredException();
        }

        return StepCoreAsync(notAfter, cancellationToken);
    }

    private async ValueTask<StepStatus> StepCoreAsync(
        ModelTime notAfter,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureJournalAligned();
            if (notAfter < CurrentModelTime)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(notAfter),
                    "The Step boundary cannot precede the current committed model time.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            TWorld frozenWorld = _world;
            WorldVersion expectedVersion = _version;
            LogicalInstant? expectedLastInstant = _lastCommittedInstant;
            ForecastWinner<TWorld, TCandidateData, TFact>? selection =
                ForecastRound.SelectWinner(frozenWorld, CurrentModelTime, _simulationRules, _rules);
            if (selection is null)
            {
                return StepStatus.Exhausted;
            }

            OccurrenceCandidate<TCandidateData> winner = selection.Candidate;
            if (_journal.Batches.Count > 0 && _journal.Batches[^1].CauseKey == winner.Key)
            {
                throw new InvalidOperationException(
                    $"Candidate '{winner.Key}' repeated immediately after it was committed; " +
                    "the owning rule made no key-visible authoritative progress.");
            }

            if (winner.Due.ModelTime > notAfter)
            {
                return StepStatus.BoundaryReached;
            }

            LogicalInstant nextInstant = LogicalInstantRules.Propose(
                winner.Due,
                _genesisTime,
                expectedLastInstant,
                _simulationRules.MaxTransitionsPerModelTime);
            var nextVersion = new WorldVersion(
                expectedVersion.LineageId,
                checked(expectedVersion.TransitionCount + 1));

            TransitionDraft<TFact> draft = await selection.Owner
                .PlanSelectedAsync(frozenWorld, winner, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The selected occurrence rule returned a null draft.");

            TWorld scratchWorld = frozenWorld;
            foreach (TFact fact in draft.Facts)
            {
                scratchWorld = _fold(scratchWorld, nextInstant, fact);
                if (scratchWorld is null)
                {
                    throw new InvalidOperationException("The fact fold returned a null HostWorld.");
                }
            }

            _validate(scratchWorld);

            var batch = new JournalBatch<TFact>(nextInstant, winner.Key, draft.Facts);
            cancellationToken.ThrowIfCancellationRequested();

            int batchCountBeforePublish = _journal.Batches.Count;
            try
            {
                _journal.AppendBatch(batch);
            }
            catch (Exception publicationFailure)
            {
                _requiresReplay = true;
                throw new InvalidOperationException(
                    "Journal publication threw, so its outcome cannot be safely determined; " +
                    "the Kernel is stopped and must be rebuilt by Replay.",
                    publicationFailure);
            }

            JournalBatch<TFact>? publishedBatch = _journal.Batches.Count == checked(batchCountBeforePublish + 1)
                ? _journal.Batches[^1]
                : null;
            if (publishedBatch is null ||
                publishedBatch.Instant != nextInstant ||
                publishedBatch.CauseKey != winner.Key ||
                !publishedBatch.Facts.SequenceEqual(draft.Facts))
            {
                _requiresReplay = true;
                throw new InvalidOperationException(
                    "Journal publication returned without exposing exactly the proposed batch; " +
                    "the Kernel is stopped and must be rebuilt by Replay.");
            }

            // Publication is the irreversible commit point. Do not observe cancellation below it.
            _world = scratchWorld;
            _version = nextVersion;
            _lastCommittedInstant = nextInstant;
            return StepStatus.Committed;
        }
        finally
        {
            Volatile.Write(ref _stepInFlight, 0);
        }
    }

    private void EnsureJournalAligned()
    {
        bool countMatches = (long)_journal.Batches.Count == _version.TransitionCount;
        bool headMatches = _journal.Batches.Count == 0
            ? _lastCommittedInstant is null
            : _lastCommittedInstant == _journal.Batches[^1].Instant;
        if (countMatches && headMatches)
        {
            return;
        }

        _requiresReplay = true;
        throw ReplayRequiredException();
    }

    private static void ValidateCommittedBoundary(
        WorldVersion worldVersion,
        ModelTime genesisTime,
        LogicalInstant? lastCommittedInstant,
        IJournalSink<TFact> journal)
    {
        if (journal.LineageId != worldVersion.LineageId)
        {
            throw new ArgumentException(
                "The Journal LineageId must equal WorldVersion.LineageId.",
                nameof(journal));
        }

        if ((long)journal.Batches.Count != worldVersion.TransitionCount)
        {
            throw new ArgumentException(
                "WorldVersion.TransitionCount must equal the committed journal batch count.",
                nameof(worldVersion));
        }

        if (worldVersion.TransitionCount == 0 && lastCommittedInstant is not null)
        {
            throw new ArgumentException(
                "An empty lineage cannot have a last committed instant.",
                nameof(lastCommittedInstant));
        }

        if (worldVersion.TransitionCount > 0 && lastCommittedInstant is null)
        {
            throw new ArgumentException(
                "A non-empty lineage requires its last committed instant.",
                nameof(lastCommittedInstant));
        }

        if (lastCommittedInstant is LogicalInstant last)
        {
            if (last.ModelTime < genesisTime)
            {
                throw new ArgumentException(
                    "The last committed instant cannot precede Genesis.",
                    nameof(lastCommittedInstant));
            }

            if (journal.Batches.Count == 0 || journal.Batches[^1].Instant != last)
            {
                throw new ArgumentException(
                    "The last committed instant must equal the Journal batch head.",
                    nameof(lastCommittedInstant));
            }
        }
    }

    private static InvalidOperationException ReplayRequiredException() =>
        new(
            "The Kernel's in-memory state is no longer aligned with authoritative Journal history; " +
            "this instance permanently requires Replay.");
}

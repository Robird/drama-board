using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Playground;

/// <summary>One selected branch's E/S history: disk stores business/cursor, the session keeps a ready runtime world.</summary>
/// <remarks>
/// Exclusively owned by one Kernel. Any failed commit attempt retires this adapter; dispose and reopen
/// before inspecting durable progress. Disposing never completes pending work or saves memory changes.
/// </remarks>
public sealed partial class PlaygroundDurableHistory : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact>, IDisposable {
    private readonly Repository _repository;
    private readonly WorkingCopy _work;
    private readonly int _gameRevision;
    private PlaygroundDurableRoot _readyRoot;
    private PlaygroundWorld _state;
    private PlaygroundDurableEvent? _pending;
    private bool _faulted;
    private bool _disposed;

    private PlaygroundDurableHistory(Repository repository, WorkingCopy work, int targetRevision,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot>? upgradeState = null,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot, PlaygroundDurableEvent, PlaygroundDurableEvent>? upgradeEvent = null,
        PlaygroundWorldDefinition? expectedDefinition = null) {
        _repository = repository;
        _work = work;
        _gameRevision = targetRevision;

        // The WorkingCopy retains the physical State/head baseline. Never replace it just to load.
        PlaygroundDurableRoot restored = ReadRestoredRoot();
        restored.ValidateStructure();
        PlaygroundDurableEvent? pending = ReadPending(restored.Cursor);
        (_readyRoot, _pending) = PlaygroundGameRevision.UpgradeLoadedBoundary(
            restored, pending, targetRevision, upgradeState, upgradeEvent);
        if (expectedDefinition is not null) {
            expectedDefinition.Validate();
            RequireSameContent(_readyRoot.Business.Definition, expectedDefinition);
        }
        _readyRoot.Validate();
        if (_pending is not null) {
            ValidateContinuation(_pending.Occurrence, _readyRoot.Cursor);
        }
        // One business graph supplies both the publication boundary and its derived runtime view.
        _state = PlaygroundEngine.Rebuild(_readyRoot.Business, _readyRoot.Cursor.CurrentModelTime);
    }

    public PlaygroundWorld State { get { RequireUsable(); return _state; } }
    public KernelCursor Cursor { get { RequireUsable(); return _readyRoot.Cursor; } }
    public OccurrenceEvent<PlaygroundFact>? PendingEvent { get { RequireUsable(); return _pending?.Occurrence; } }
    public bool IsFaulted => _faulted || _repository.IsFaulted;

    /// <summary>Finds this branch's operation effects already covered by a completed State, without replaying them.</summary>
    /// <remarks>Pending E is excluded until the Kernel completes it. Intended for cold-open repair of
    /// missing host receipts; neither receipt absence nor presence authorizes executing an input again.</remarks>
    public IReadOnlyList<PlaygroundCommittedOperation> ReadCommittedOperations(IEnumerable<string> operationRecordIds) {
        RequireUsable();
        ArgumentNullException.ThrowIfNull(operationRecordIds);
        var remaining = new HashSet<string>(operationRecordIds, StringComparer.Ordinal);
        if (remaining.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("Operation references must be nonempty.", nameof(operationRecordIds)); }
        var results = new List<PlaygroundCommittedOperation>();
        if (remaining.Count == 0) { return results.AsReadOnly(); }
        CommitId oldestState = _work.StateSourceCommitId
            ?? throw new InvalidDataException("The game has no completed State.");
        HistoryStart? start = HistoryStart.Before(oldestState);
        long transitionCount = Cursor.Version.TransitionCount;
        bool expectEvent = true;
        while (start is { } boundary) {
            CommitHistoryPage page = _repository.ReadHistoryPage(boundary, new HistoryPageLimits(64, long.MaxValue));
            foreach (CommitInfo commit in page.Items) {
                if (commit.Kind != (expectEvent ? CommitKind.Event : CommitKind.State)) {
                    throw new InvalidDataException("Playground history must alternate completed State/Event pairs.");
                }
                expectEvent = !expectEvent;
                if (commit.Kind != CommitKind.Event) { oldestState = commit.Id; continue; }
                if (transitionCount <= 0) { throw new InvalidDataException("History exceeds the completed transition count."); }
                var entry = _repository.LoadEvent(commit.Id) as PlaygroundDurableEvent
                    ?? throw new InvalidDataException("The historical Event is not a Playground occurrence.");
                entry.ValidateStructure();
                entry.ValidateContent(_readyRoot.Business.Definition);
                foreach (PlaygroundFact fact in entry.Occurrence.Facts) {
                    // Inherited world facts are not receipts for inputs received by this branch. A
                    // child may reuse an operation ID, and its unexecuted input must stay unproven.
                    if (commit.CreatedOnBranchId != _work.BranchId) { continue; }
                    if (fact.OperationRecordId is not { } recordId || !remaining.Remove(recordId)) { continue; }
                    if (string.IsNullOrWhiteSpace(fact.ToolCallId) || string.IsNullOrWhiteSpace(fact.ActorId) || fact.OperationDisposition is null) {
                        throw new InvalidDataException("A recorded operation effect has incomplete provenance.");
                    }
                    results.Add(new(recordId, fact.ToolCallId, fact.ActorId, PlaygroundOperationResults.Describe(fact), fact.OperationDisposition.Value,
                        fact.At.Ticks, Cursor.Version.LineageId, transitionCount, entry.Occurrence.CauseKey.ToString()));
                }
                transitionCount--;
                if (remaining.Count == 0) { return results.AsReadOnly(); }
            }
            start = page.Continuation;
        }
        // Create also accepts a completed snapshot with a nonzero simulation version. Its root State
        // is the beginning of this repository, not necessarily the beginning of that simulation.
        var genesis = _repository.LoadState(oldestState) as PlaygroundDurableRoot
            ?? throw new InvalidDataException("The initial State is not a Playground root.");
        genesis.ValidateContent();
        RequireSameContent(_readyRoot.Business.Definition, genesis.Business.Definition);
        if (!expectEvent || genesis.Cursor.Version.TransitionCount != transitionCount ||
            genesis.Cursor.Version.LineageId != Cursor.Version.LineageId) {
            throw new InvalidDataException("Incomplete Playground history prefix.");
        }
        return results.AsReadOnly();
    }

    /// <summary>Creates a new repository at a previously nonexistent path and publishes its initial State.</summary>
    public static PlaygroundDurableHistory Create(string path, PlaygroundWorld world, KernelCursor cursor) {
        ArgumentNullException.ThrowIfNull(world);
        var root = new PlaygroundDurableRoot(world.Business, cursor);
        Repository repository = Repository.CreateNew(path, CreateModels());
        try {
            BranchId branch = repository.CreateRootBranchFromState("playground", root).Id;
            return Attach(repository, branch, PlaygroundGameRevision.Current);
        } catch {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>Opens an unambiguous single root branch. Multiple branches require an explicit selection.</summary>
    /// <remarks>Restores S/E, upgrades their in-memory representations, then validates and rebuilds State.
    /// Loading neither publishes nor executes pending work. Unsupported revisions are rejected.</remarks>
    public static PlaygroundDurableHistory Open(string path) => OpenCore(path, PlaygroundGameRevision.Current);

    /// <summary>Requires the exact effective content identity before rebuilding or executing the saved world.</summary>
    public static PlaygroundDurableHistory Open(string path, PlaygroundWorldDefinition expectedDefinition) =>
        OpenCore(path, PlaygroundGameRevision.Current, expectedDefinition: expectedDefinition ?? throw new ArgumentNullException(nameof(expectedDefinition)));

    /// <summary>Opens the selected branch, preserving any pending Event for normal Kernel recovery.</summary>
    public static PlaygroundDurableHistory Open(string path, BranchId branch) => OpenCore(path, PlaygroundGameRevision.Current, branch: branch);

    public static PlaygroundDurableHistory Open(string path, string branchId) => Open(path, ParseBranchId(branchId));

    public static PlaygroundDurableHistory Open(string path, BranchId branch, PlaygroundWorldDefinition expectedDefinition) =>
        OpenCore(path, PlaygroundGameRevision.Current, branch: branch,
            expectedDefinition: expectedDefinition ?? throw new ArgumentNullException(nameof(expectedDefinition)));

    public static PlaygroundDurableHistory Open(string path, string branchId, PlaygroundWorldDefinition expectedDefinition) =>
        Open(path, ParseBranchId(branchId), expectedDefinition);

    // Fixture seam for historical semantics; it uses the same adapter, version checks and publication path.
    internal static PlaygroundDurableHistory Open(string path, int targetRevision,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot> upgradeState,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot, PlaygroundDurableEvent, PlaygroundDurableEvent> upgradeEvent) =>
        OpenCore(path, targetRevision, upgradeState, upgradeEvent);

    private static PlaygroundDurableHistory OpenCore(string path, int targetRevision,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot>? upgradeState = null,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot, PlaygroundDurableEvent, PlaygroundDurableEvent>? upgradeEvent = null,
        BranchId? branch = null, PlaygroundWorldDefinition? expectedDefinition = null) {
        Repository repository = Repository.OpenExisting(path, CreateModels());
        try {
            if (branch is not { } selected) {
                BranchInfo[] branches = repository.ListBranches();
                if (branches.Length != 1 || branches[0].ForkedFromCommitId is not null) {
                    throw new InvalidDataException("Multiple Playground branches require an explicit branch ID.");
                }
                selected = branches[0].Id;
            }
            return Attach(repository, selected, targetRevision, upgradeState, upgradeEvent, expectedDefinition);
        } catch {
            repository.Dispose();
            throw;
        }
    }

    public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
        RequireUsable();
        try {
            if (_pending is not null) {
                throw new InvalidOperationException("Complete the pending Event before recording another.");
            }
            ArgumentNullException.ThrowIfNull(occurrence);
            ValidateContinuation(occurrence, _readyRoot.Cursor);
            var pending = new PlaygroundDurableEvent(occurrence, _readyRoot.Business.Definition, _gameRevision);
            _work.CommitEvent(pending);
            _pending = pending;
        } catch {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Publishes the completion of exactly one pending occurrence.</summary>
    /// <remarks>This is the normal Kernel publication path, not a standalone game revision entry point.</remarks>
    public void CommitState(PlaygroundWorld nextState, KernelCursor nextCursor) {
        RequireUsable();
        try {
            OccurrenceEvent<PlaygroundFact> pending = _pending?.Occurrence
                ?? throw new InvalidOperationException("A State must complete a pending Event.");
            KernelCursor expected = _readyRoot.Cursor.Advance(pending.CauseKey, pending.TargetInstant);
            if (nextCursor != expected) {
                throw new ArgumentException("The State cursor must complete exactly the pending occurrence.", nameof(nextCursor));
            }
            ArgumentNullException.ThrowIfNull(nextState);
            _pending!.ValidateContent(_readyRoot.Business.Definition);
            RequireSameContent(_readyRoot.Business.Definition, nextState.Definition);
            PlaygroundEngine.Validate(nextState, nextCursor.CurrentModelTime);
            PlaygroundDurableRoot nextRoot = PlaygroundDurableRoot.CreateRevisionCandidate(nextState.Business, nextCursor, _gameRevision);
            nextRoot.Validate();
            _work.CommitState(nextRoot);
            _readyRoot = nextRoot;
            _state = nextState;
            _pending = null;
        } catch {
            _faulted = true;
            throw;
        }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        try { _work.Dispose(); }
        finally { _repository.Dispose(); }
    }

    private static PlaygroundDurableHistory Attach(Repository repository, BranchId branch, int targetRevision,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot>? upgradeState = null,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot, PlaygroundDurableEvent, PlaygroundDurableEvent>? upgradeEvent = null,
        PlaygroundWorldDefinition? expectedDefinition = null) {
        WorkingCopy work = repository.OpenWorkingCopy(branch);
        try { return new PlaygroundDurableHistory(repository, work, targetRevision, upgradeState, upgradeEvent, expectedDefinition); }
        catch {
            work.Dispose();
            throw;
        }
    }

    private static StateModelRegistry CreateModels() {
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        return models;
    }

    private PlaygroundDurableRoot ReadRestoredRoot() => _work.State as PlaygroundDurableRoot
        ?? throw new InvalidDataException("The saved State root is not a Playground root.");

    private PlaygroundDurableEvent? ReadPending(KernelCursor cursor) {
        CommitInfo head = _repository.ReadCommitInfo(_work.BaseCommitId);
        if (head.Kind == CommitKind.State) {
            if (_work.StateSourceCommitId != head.Id) {
                throw new InvalidDataException("The completed head does not supply the restored State.");
            }
            return null;
        }
        if (head.Kind != CommitKind.Event) {
            throw new InvalidDataException("Unknown Playground history entry kind.");
        }

        // Checking only the nearest State would silently accept an invalid S/E/E tail.
        CommitHistoryPage parentPage = _repository.ReadHistoryPage(
            HistoryStart.Before(head.Id), new HistoryPageLimits(1, long.MaxValue));
        if (parentPage.Items.Count != 1 || parentPage.Items[0].Kind != CommitKind.State ||
            parentPage.Items[0].Id != _work.StateSourceCommitId ||
            _repository.GetPreviousState(head.Id) != _work.StateSourceCommitId) {
            throw new InvalidDataException("A pending Event must immediately follow the restored State.");
        }
        PlaygroundDurableEvent pending = _repository.LoadEvent(head.Id) as PlaygroundDurableEvent
            ?? throw new InvalidDataException("The pending Event is not a versioned Playground occurrence.");
        pending.ValidateStructure();
        ValidateContinuation(pending.Occurrence, cursor);
        return pending;
    }

    private static void ValidateContinuation(OccurrenceEvent<PlaygroundFact> occurrence, KernelCursor cursor) {
        occurrence.Validate();
        if (occurrence.CauseKey == cursor.LastCauseKey || occurrence.TargetInstant.ModelTime < cursor.CurrentModelTime) {
            throw new InvalidDataException("The occurrence does not advance the completed cursor.");
        }
        long expectedOrdinal = cursor.LastInstant is { } last && last.ModelTime == occurrence.TargetInstant.ModelTime
            ? checked(last.CausalOrdinal + 1)
            : 0;
        if (occurrence.TargetInstant.CausalOrdinal != expectedOrdinal) {
            throw new InvalidDataException("The occurrence causal ordinal does not continue the completed cursor.");
        }
    }

    private static void RequireSameContent(PlaygroundWorldDefinition actual, PlaygroundWorldDefinition expected) {
        if (actual.ContentId != expected.ContentId || actual.Fingerprint != expected.Fingerprint) {
            throw new InvalidDataException("The world content identity differs from the required effective definition.");
        }
    }

    private void RequireUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFaulted) {
            throw new InvalidOperationException("Playground history is faulted; dispose and reopen it.");
        }
    }
}

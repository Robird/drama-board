using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.ResourceCycle;

/// <summary>Single-branch E/S history. Every State replaces one complete immutable world/cursor root.</summary>
/// <remarks>
/// Exclusively owned by one Kernel. Any failed commit attempt retires this adapter; dispose and reopen
/// before inspecting durable progress. Disposing never completes pending work or saves memory changes.
/// </remarks>
public sealed class CycleDurableHistory : IOccurrenceHistory<CycleWorld, CycleFact>, IDisposable {
    private readonly Repository _repository;
    private readonly WorkingCopy _work;
    private OccurrenceEvent<CycleFact>? _pending;
    private bool _faulted;
    private bool _disposed;

    private CycleDurableHistory(Repository repository, WorkingCopy work) {
        _repository = repository;
        _work = work;
        ReadRoot().Validate();
        LoadPending();
    }

    public CycleWorld State { get { RequireUsable(); return ReadRoot().World; } }
    public KernelCursor Cursor { get { RequireUsable(); return ReadRoot().Cursor; } }
    public OccurrenceEvent<CycleFact>? PendingEvent { get { RequireUsable(); return _pending; } }
    public bool IsFaulted => _faulted || _repository.IsFaulted;

    /// <summary>Creates a new repository at a previously nonexistent path and publishes its initial State.</summary>
    public static CycleDurableHistory Create(string path, CycleWorld world, KernelCursor cursor) {
        var root = new CycleDurableRoot(world, cursor);
        Repository repository = Repository.CreateNew(path, CreateModels());
        try {
            BranchId branch = repository.CreateRootBranchFromState("resource-cycle", root).Id;
            return Attach(repository, branch);
        } catch {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>Opens exactly one root branch. Pending E is exposed; the Kernel explicitly recovers it.</summary>
    public static CycleDurableHistory Open(string path) {
        Repository repository = Repository.OpenExisting(path, CreateModels());
        try {
            BranchInfo[] branches = repository.ListBranches();
            if (branches.Length != 1 || branches[0].ForkedFromCommitId is not null) {
                throw new InvalidDataException("ResourceCycle requires exactly one root branch and no forks.");
            }
            return Attach(repository, branches[0].Id);
        } catch {
            repository.Dispose();
            throw;
        }
    }

    public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
        RequireUsable();
        try {
            if (_pending is not null) {
                throw new InvalidOperationException("Complete the pending Event before recording another.");
            }
            ArgumentNullException.ThrowIfNull(occurrence);
            ValidateContinuation(occurrence, ReadRoot().Cursor);
            _work.CommitEvent(occurrence);
            _pending = occurrence;
        } catch {
            _faulted = true;
            throw;
        }
    }

    public void CommitState(CycleWorld nextState, KernelCursor nextCursor) {
        RequireUsable();
        try {
            OccurrenceEvent<CycleFact> pending = _pending
                ?? throw new InvalidOperationException("A State must complete a pending Event.");
            KernelCursor expected = ReadRoot().Cursor.Advance(pending.CauseKey, pending.TargetInstant);
            if (nextCursor != expected) {
                throw new ArgumentException("The State cursor must complete exactly the pending occurrence.", nameof(nextCursor));
            }
            var nextRoot = new CycleDurableRoot(nextState, nextCursor);
            _work.CommitState(nextRoot);
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

    private static CycleDurableHistory Attach(Repository repository, BranchId branch) {
        WorkingCopy work = repository.OpenWorkingCopy(branch);
        try { return new CycleDurableHistory(repository, work); }
        catch {
            work.Dispose();
            throw;
        }
    }

    private static StateModelRegistry CreateModels() {
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        return models;
    }

    private CycleDurableRoot ReadRoot() => _work.State as CycleDurableRoot
        ?? throw new InvalidDataException("The saved State root is not a ResourceCycle root.");

    private void LoadPending() {
        CommitInfo head = _repository.ReadCommitInfo(_work.BaseCommitId);
        if (head.Kind == CommitKind.State) {
            if (_work.StateSourceCommitId != head.Id) {
                throw new InvalidDataException("The completed head does not supply the restored State.");
            }
            return;
        }
        if (head.Kind != CommitKind.Event) {
            throw new InvalidDataException("Unknown ResourceCycle history entry kind.");
        }

        // Checking only the nearest State would silently accept an invalid S/E/E tail.
        CommitHistoryPage parentPage = _repository.ReadHistoryPage(
            HistoryStart.Before(head.Id), new HistoryPageLimits(1, long.MaxValue));
        if (parentPage.Items.Count != 1 || parentPage.Items[0].Kind != CommitKind.State ||
            parentPage.Items[0].Id != _work.StateSourceCommitId ||
            _repository.GetPreviousState(head.Id) != _work.StateSourceCommitId) {
            throw new InvalidDataException("A pending Event must immediately follow the restored State.");
        }
        _pending = _repository.LoadEvent(head.Id) as OccurrenceEvent<CycleFact>
            ?? throw new InvalidDataException("The pending Event is not a ResourceCycle occurrence.");
        ValidateContinuation(_pending, ReadRoot().Cursor);
    }

    private static void ValidateContinuation(OccurrenceEvent<CycleFact> occurrence, KernelCursor cursor) {
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

    private void RequireUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFaulted) {
            throw new InvalidOperationException("ResourceCycle history is faulted; dispose and reopen it.");
        }
    }
}

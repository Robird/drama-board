using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown;

public sealed record TownBranchInfo(BranchId Id, string DisplayLabel, CommitId? ForkedFromCommitId,
    CommitId HeadCommitId, CommitId CompletedStateCommitId, bool HasPendingEvent);

/// <summary>An exact completed pre-decision State. Repeated requests within one decision can have different State IDs.</summary>
public sealed record TownDecisionBoundary(string GameId, int GameRevision, BranchId BranchId, CommitId StateCommitId,
    string ActorId, long DecisionSequence, long OperationSequence, long LineageId, long TransitionCount, long TimeTicks);

/// <summary>One branch's E/S publication adapter. Opening restores values and predictions, never executes pending work.</summary>
/// <remarks>One Kernel owns this instance. Any commit failure retires it; dispose and reopen to discover durable progress.</remarks>
public sealed partial class TownDurableHistory : IOccurrenceHistory<TownWorld, TownFact>, IDisposable {
    private readonly Repository _repository;
    private readonly WorkingCopy _work;
    private TownDurableRoot _root;
    private TownWorld _state;
    private TownDurableEvent? _pending;
    private bool _faulted;
    private bool _disposed;

    private TownDurableHistory(Repository repository, WorkingCopy work) {
        _repository = repository; _work = work;
        _root = work.State as TownDurableRoot ?? throw new InvalidDataException("The saved State is not a Microtown root.");
        _root.Validate();
        CommitId completed = work.StateSourceCommitId ?? throw new InvalidDataException("Town requires a completed State.");
        ValidateCompletedBoundary(repository, completed, _root);
        CommitInfo head = repository.ReadCommitInfo(work.BaseCommitId);
        if (head.Kind == CommitKind.State) {
            if (head.Id != completed) { throw new InvalidDataException("The head does not supply the restored State."); }
        } else if (head.Kind == CommitKind.Event) {
            CommitHistoryPage parents = repository.ReadHistoryPage(HistoryStart.Before(head.Id), new HistoryPageLimits(1, long.MaxValue));
            if (parents.Items.Count != 1 || parents.Items[0].Id != completed || parents.Items[0].Kind != CommitKind.State) {
                throw new InvalidDataException("A pending town Event must immediately follow its completed State.");
            }
            _pending = repository.LoadEvent(head.Id) as TownDurableEvent
                ?? throw new InvalidDataException("The pending Event is not a Microtown event.");
            _pending.Validate();
            ValidateContinuation(_pending.Occurrence, _root.Cursor);
        } else { throw new InvalidDataException("Unknown town history entry kind."); }
        _state = TownEngine.Rebuild(_root.Business);
    }

    public TownWorld State { get { RequireUsable(); return _state; } }
    public KernelCursor Cursor { get { RequireUsable(); return _root.Cursor; } }
    public OccurrenceEvent<TownFact>? PendingEvent { get { RequireUsable(); return _pending?.Occurrence; } }
    public bool IsFaulted => _faulted || _repository.IsFaulted;
    public BranchId BranchId { get { RequireUsable(); return _work.BranchId; } }
    public TownBranchInfo CurrentBranch { get { RequireUsable(); return Describe(_repository, _repository.ReadBranchInfo(_work.BranchId)); } }
    public CommitId HeadCommitId { get { RequireUsable(); return _work.BaseCommitId; } }
    public CommitId CompletedStateCommitId { get { RequireUsable(); return _work.StateSourceCommitId!.Value; } }

    public TownDecisionBoundary CaptureDecisionBoundary(string actorId) {
        RequireUsable();
        if (_pending is not null || HeadCommitId != CompletedStateCommitId) {
            throw new InvalidOperationException("A decision anchor requires a completed State without pending work.");
        }
        return DescribeDecisionBoundary(BranchId, CompletedStateCommitId, _root, _state, actorId);
    }

    public static TownDurableHistory Create(string path, TownWorld world, KernelCursor cursor) {
        ArgumentNullException.ThrowIfNull(world);
        TownEngine.Validate(world, cursor.CurrentModelTime);
        var root = new TownDurableRoot(world.Business, cursor);
        root.Validate();
        Repository repository = Repository.CreateNew(path, CreateModels());
        try {
            BranchId branch = repository.CreateRootBranchFromState(TownGameRevision.GameId, root).Id;
            return Attach(repository, branch);
        } catch { repository.Dispose(); throw; }
    }

    public static TownDurableHistory Open(string path, BranchId? branch = null) {
        Repository repository = Repository.OpenExisting(path, CreateModels());
        try {
            if (branch is null) {
                BranchInfo[] branches = repository.ListBranches();
                if (branches.Length != 1 || branches[0].ForkedFromCommitId is not null) {
                    throw new InvalidDataException("Multiple town branches require an explicit branch ID.");
                }
                branch = branches[0].Id;
            }
            return Attach(repository, branch.Value);
        } catch { repository.Dispose(); throw; }
    }

    private static TownDurableHistory Attach(Repository repository, BranchId branch) {
        WorkingCopy work = repository.OpenWorkingCopy(branch);
        try { return new(repository, work); }
        catch { work.Dispose(); throw; }
    }

    public void CommitEvent(OccurrenceEvent<TownFact> occurrence) {
        RequireUsable();
        try {
            if (_pending is not null) { throw new InvalidOperationException("Complete the pending Event first."); }
            ValidateContinuation(occurrence, _root.Cursor);
            var pending = new TownDurableEvent(occurrence);
            pending.Validate();
            _work.CommitEvent(pending);
            _pending = pending;
        } catch { _faulted = true; throw; }
    }

    public void CommitState(TownWorld nextState, KernelCursor nextCursor) {
        RequireUsable();
        try {
            OccurrenceEvent<TownFact> pending = _pending?.Occurrence ?? throw new InvalidOperationException("State must complete a pending Event.");
            if (nextCursor != _root.Cursor.Advance(pending.CauseKey, pending.TargetInstant) ||
                !ReferenceEquals(nextState.Business, pending.Facts[^1].NextBusiness)) {
                throw new InvalidDataException("State must publish exactly the pending occurrence's final business and cursor.");
            }
            TownEngine.Validate(nextState, nextCursor.CurrentModelTime);
            var root = new TownDurableRoot(nextState.Business, nextCursor);
            root.Validate();
            _work.CommitState(root);
            _root = root; _state = nextState; _pending = null;
        } catch { _faulted = true; throw; }
    }

    public static IReadOnlyList<TownBranchInfo> ListBranches(string path) {
        using Repository repository = Repository.OpenReadOnlyExisting(path, CreateModels());
        return repository.ListBranches().Select(branch => Describe(repository, branch)).ToArray();
    }

    /// <summary>Registers a child at an exact, open decision State in the specified source history, without publishing another State.</summary>
    public static TownBranchInfo Fork(string path, BranchId sourceBranch, CommitId completedState, string label) {
        using Repository repository = Repository.OpenExisting(path, CreateModels());
        TownDurableRoot root = ReadSelectedState(repository, sourceBranch, completedState);
        if (root.Business.OpenDecisionActorId is not { } actor) {
            throw new InvalidDataException("The selected State must contain an open resident decision.");
        }
        DescribeDecisionBoundary(sourceBranch, completedState, root, TownEngine.Rebuild(root.Business), actor);
        BranchInfo child = repository.ForkBranch(completedState, label);
        // No graph loading or publication after registering the child: a successful result is unambiguous.
        return new(child.Id, child.DisplayLabel, completedState, completedState, completedState, false);
    }

    private static TownBranchInfo Describe(Repository repository, BranchInfo branch) {
        CommitInfo head = repository.ReadCurrentCommit(branch.Id).Commit;
        CommitId completed = head.Kind == CommitKind.State ? head.Id : repository.GetPreviousState(head.Id)
            ?? throw new InvalidDataException("Town branch has no completed State.");
        return new(branch.Id, branch.DisplayLabel, branch.ForkedFromCommitId, head.Id, completed, head.Kind == CommitKind.Event);
    }

    private static void ValidateCompletedBoundary(Repository repository, CommitId stateId, TownDurableRoot root) {
        CommitHistoryPage parents = repository.ReadHistoryPage(HistoryStart.Before(stateId), new HistoryPageLimits(2, long.MaxValue));
        if (parents.Items.Count == 0) { return; }
        if (parents.Items.Count != 2 || parents.Items[0].Kind != CommitKind.Event || parents.Items[1].Kind != CommitKind.State ||
            repository.LoadEvent(parents.Items[0].Id) is not TownDurableEvent previousEvent ||
            repository.LoadState(parents.Items[1].Id) is not TownDurableRoot previousState) {
            throw new InvalidDataException("A completed town State must follow an immediate State/Event pair.");
        }
        previousEvent.Validate(); previousState.Validate();
        ValidateContinuation(previousEvent.Occurrence, previousState.Cursor);
        if (previousState.Cursor.Advance(previousEvent.Occurrence.CauseKey, previousEvent.Occurrence.TargetInstant) != root.Cursor) {
            throw new InvalidDataException("Completed State cursor does not match its predecessor Event.");
        }
    }

    private static void ValidateContinuation(OccurrenceEvent<TownFact> occurrence, KernelCursor cursor) {
        ArgumentNullException.ThrowIfNull(occurrence);
        occurrence.Validate();
        long ordinal = cursor.LastInstant is { } last && last.ModelTime == occurrence.TargetInstant.ModelTime ? checked(last.CausalOrdinal + 1) : 0;
        if (occurrence.CauseKey == cursor.LastCauseKey || occurrence.TargetInstant.ModelTime < cursor.CurrentModelTime ||
            occurrence.TargetInstant.CausalOrdinal != ordinal) {
            throw new InvalidDataException("Town occurrence does not continue its completed cursor.");
        }
    }

    private static StateModelRegistry CreateModels() {
        var models = new StateModelRegistry(); TownDurableModels.Register(models); return models;
    }

    public static string FormatBranchId(BranchId id) {
        Span<byte> bytes = stackalloc byte[Atelia.DurableGraph.BranchId.EncodedSize];
        if (!id.TryWrite(bytes)) { throw new ArgumentException("Invalid branch ID.", nameof(id)); }
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public static string FormatCommitId(CommitId id) {
        Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
        if (!id.TryWrite(bytes)) { throw new ArgumentException("Invalid commit ID.", nameof(id)); }
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public static BranchId ParseBranchId(string text) {
        if (text.Length != Atelia.DurableGraph.BranchId.EncodedSize * 2 ||
            !Atelia.DurableGraph.BranchId.TryRead(Convert.FromHexString(text), out BranchId id)) { throw new FormatException("Invalid branch ID."); }
        return id;
    }
    public static CommitId ParseCommitId(string text) {
        if (text.Length != CommitId.EncodedSize * 2 || !CommitId.TryRead(Convert.FromHexString(text), out CommitId id)) { throw new FormatException("Invalid commit ID."); }
        return id;
    }

    private void RequireUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFaulted) { throw new InvalidOperationException("Town history is faulted; dispose and reopen it."); }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        try { _work.Dispose(); }
        finally { _repository.Dispose(); }
    }
}

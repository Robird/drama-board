using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace DramaBoard.ResourceCycle;

/// <summary>A persisted, completed decision prestate. IDs are hexadecimal DG encodings scoped to the original repository.</summary>
public sealed record CycleDecisionBoundary(string BranchId, string StateCommitId, string ActorId,
    long ContextSequence, long OperationSequence, long LineageId, long TransitionCount, long TimeTicks, int GameRevision);

/// <summary>Detached metadata; listing it does not restore domain graphs or complete pending work.</summary>
public sealed record CycleBranchInfo(string BranchId, string DisplayLabel, string? ForkedFromCommitId,
    string HeadCommitId, string? CompletedStateCommitId, bool HasPendingEvent);

public sealed record CycleHistoryEntry(string CommitId, string CreatedOnBranchId, string Kind);

/// <summary>Registration succeeded. Opening and running the returned branch are separate operations.</summary>
public sealed record CycleBranchForkResult(CycleBranchInfo Branch, CycleDecisionBoundary Source);

public sealed partial class CycleDurableHistory {
    public BranchId BranchId { get { RequireUsable(); return _work.BranchId; } }
    public CommitId HeadCommitId { get { RequireUsable(); return _work.BaseCommitId; } }
    public CommitId CompletedStateCommitId { get { RequireUsable(); return _work.StateSourceCommitId ?? throw new InvalidDataException("The game has no completed State."); } }
    public CycleBranchInfo CurrentBranch { get { RequireUsable(); return DescribeBranch(_repository, _repository.ReadBranchInfo(_work.BranchId)); } }

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

    public static BranchId ParseBranchId(string encoded) {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length != Atelia.DurableGraph.BranchId.EncodedSize * 2) { throw new FormatException("A branch ID requires exactly 8 hexadecimal characters."); }
        byte[] bytes = Convert.FromHexString(encoded);
        if (!Atelia.DurableGraph.BranchId.TryRead(bytes, out BranchId result)) { throw new FormatException("Invalid encoded branch ID."); }
        return result;
    }

    public static CommitId ParseCommitId(string encoded) {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length != CommitId.EncodedSize * 2) { throw new FormatException("A commit ID requires exactly 24 hexadecimal characters."); }
        byte[] bytes = Convert.FromHexString(encoded);
        if (!CommitId.TryRead(bytes, out CommitId result)) { throw new FormatException("Invalid encoded commit ID."); }
        return result;
    }

    /// <summary>Captures the exact prestate while a controller is about to choose for the already selected actor.</summary>
    public CycleDecisionBoundary CaptureDecisionBoundary(string actorId) {
        RequireUsable();
        if (_pending is not null || _work.BaseCommitId != _work.StateSourceCommitId) {
            throw new InvalidOperationException("A decision boundary requires a completed State, not pending Event work.");
        }
        return DescribeDecisionBoundary(_work.BranchId, CompletedStateCommitId, _readyRoot, _state, actorId);
    }

    public static IReadOnlyList<CycleBranchInfo> ListBranches(string path) {
        using Repository repository = Repository.OpenReadOnlyExisting(path, CreateModels());
        return repository.ListBranches().Select(branch => DescribeBranch(repository, branch))
            .OrderBy(branch => branch.BranchId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Returns only the selected branch's inherited prefix and local suffix, newest first.</summary>
    public static IReadOnlyList<CycleHistoryEntry> ReadBranchHistory(string path, string branchId) {
        using Repository repository = Repository.OpenReadOnlyExisting(path, CreateModels());
        return repository.ReadFrames(ParseBranchId(branchId)).Reverse()
            .Select(commit => new CycleHistoryEntry(FormatCommitId(commit.Id), FormatBranchId(commit.CreatedOnBranchId), commit.Kind.ToString())).ToArray();
    }

    /// <summary>Checks exact ancestry and decision readiness; does not substitute a nearest/latest State.</summary>
    public static CycleDecisionBoundary InspectDecisionBoundary(string path, string branchId, string stateCommitId, string actorId) {
        using Repository repository = Repository.OpenReadOnlyExisting(path, CreateModels());
        return InspectDecisionBoundary(repository, ParseBranchId(branchId), ParseCommitId(stateCommitId), actorId);
    }

    /// <summary>Registers an independent branch from a supported exact prestate, without opening it.</summary>
    /// <remarks>Preserves Kernel lineage/cursor and business random coordinates. The DG branch supplies the new history identity.
    /// Published/Unknown RepositoryPublicationException outcomes must be inspected after reopening, never transparently retried.</remarks>
    public static CycleBranchForkResult Fork(string path, string sourceBranchId, string sourceCommitId, string actorId, string label) =>
        ForkCore(path, sourceBranchId, sourceCommitId, actorId, label);

    // Test seam for publication faults; the production sequence remains validation -> registration -> return.
    internal static CycleBranchForkResult ForkCore(string path, string sourceBranchId, string sourceCommitId, string actorId,
        string label, Action<Repository>? configureRepository = null) {
        using Repository repository = Repository.OpenExisting(path, CreateModels());
        BranchId sourceBranch = ParseBranchId(sourceBranchId);
        CommitId sourceCommit = ParseCommitId(sourceCommitId);
        CycleDecisionBoundary source = InspectDecisionBoundary(repository, sourceBranch, sourceCommit, actorId);
        configureRepository?.Invoke(repository);
        BranchInfo created = repository.ForkBranch(sourceCommit, label);
        // No post-registration graph load can hide a successfully created branch from this result.
        return new(new(FormatBranchId(created.Id), created.DisplayLabel, FormatCommitId(sourceCommit),
            FormatCommitId(sourceCommit), FormatCommitId(sourceCommit), false), source);
    }

    private static CycleBranchInfo DescribeBranch(Repository repository, BranchInfo branch) {
        CommitInfo head = repository.ReadCurrentCommit(branch.Id).Commit;
        CommitId? completed = head.Kind == CommitKind.State ? head.Id : repository.GetPreviousState(head.Id);
        return new(FormatBranchId(branch.Id), branch.DisplayLabel,
            branch.ForkedFromCommitId is { } origin ? FormatCommitId(origin) : null,
            FormatCommitId(head.Id), completed is { } state ? FormatCommitId(state) : null, head.Kind == CommitKind.Event);
    }

    private static CycleDecisionBoundary InspectDecisionBoundary(Repository repository, BranchId branch, CommitId selected, string actorId) {
        IReadOnlyList<CommitInfo> history = repository.ReadFrames(branch);
        int index = -1;
        for (int i = 0; i < history.Count; i++) { if (history[i].Id == selected) { index = i; break; } }
        if (index < 0) { throw new InvalidDataException("The selected commit is not in the specified branch's history."); }
        if (history[index].Kind != CommitKind.State) { throw new InvalidDataException("Forking requires an exact completed State; Event positions are not supported."); }
        if (index > 0 && history[index - 1].Kind != CommitKind.Event) { throw new InvalidDataException("The selected State does not complete an Event."); }
        if (repository.LoadState(selected) is not CycleDurableRoot restored) { throw new InvalidDataException("The selected State is not a ResourceCycle root."); }
        var (root, _) = CycleGameRevision.UpgradeLoadedBoundary(restored, null, CycleGameRevision.Current);
        root.Validate();
        if (index > 0) {
            if (index < 2 || history[index - 2].Kind != CommitKind.State ||
                repository.LoadState(history[index - 2].Id) is not CycleDurableRoot previous ||
                repository.LoadEvent(history[index - 1].Id) is not CycleDurableEvent occurrence) {
                throw new InvalidDataException("The selected completion has no immediate State/Event predecessor pair.");
            }
            previous.ValidateStructure(); occurrence.ValidateStructure();
            if (previous.GameRevision != CycleGameRevision.Current || occurrence.GameRevision != CycleGameRevision.Current) {
                throw new InvalidDataException("This decision fork requires current-revision predecessor State and Event representations.");
            }
            ValidateContinuation(occurrence.Occurrence, previous.Cursor);
            if (previous.Cursor.Advance(occurrence.Occurrence.CauseKey, occurrence.Occurrence.TargetInstant) != root.Cursor) {
                throw new InvalidDataException("The selected State cursor does not complete its predecessor Event.");
            }
        }
        CycleWorld world = CycleEngine.Rebuild(root.Business, root.Cursor.CurrentModelTime);
        return DescribeDecisionBoundary(branch, selected, root, world, actorId);
    }

    private static CycleDecisionBoundary DescribeDecisionBoundary(BranchId branch, CommitId stateCommit,
        CycleDurableRoot root, CycleWorld world, string actorId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (CycleEngine.NextDecisionActor(world, root.Cursor) != actorId ||
            !world.Business.InteractionStates.TryGetValue(actorId, out CharacterInteractionState? interaction) ||
            interaction.CurrentContext is not { Status: CharacterContextStatus.Open } context) {
            throw new InvalidOperationException("The selected State does not have an open decision window for the next scheduled actor.");
        }
        return new(FormatBranchId(branch), FormatCommitId(stateCommit), actorId, context.ContextSequence,
            context.LastOperationSequence, root.Cursor.Version.LineageId, root.Cursor.Version.TransitionCount,
            root.Cursor.CurrentModelTime.Ticks, root.GameRevision);
    }
}

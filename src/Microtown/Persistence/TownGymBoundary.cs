using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace DramaBoard.Microtown;

/// <summary>An operation's persisted result and the exact Event/State pair that completed it.</summary>
public sealed record TownCommittedOperation(TownOperationReceipt Receipt, CommitId EventCommitId, CommitId StateCommitId);

public sealed partial class TownDurableHistory {
    /// <summary>Checks the exact State's membership and open decision, without executing or publishing anything.</summary>
    /// <remarks>The branch may have advanced beyond this State. No nearest-State substitution is performed.</remarks>
    public static TownDecisionBoundary InspectDecisionBoundary(string path, BranchId branch, CommitId exactState, string actorId) {
        using Repository repository = Repository.OpenReadOnlyExisting(path, CreateModels());
        TownDurableRoot root = ReadSelectedState(repository, branch, exactState);
        return DescribeDecisionBoundary(branch, exactState, root, TownEngine.Rebuild(root.Business), actorId);
    }

    /// <summary>Finds completed operation results in this branch's inherited prefix and local suffix, oldest first.</summary>
    /// <remarks>Pending Events and copied receipts in a repository's initial snapshot are excluded. Neither a missing
    /// nor a present result authorizes executing an input again. Duplicate matching operation IDs are ambiguous and rejected.</remarks>
    public IReadOnlyList<TownCommittedOperation> ReadCommittedOperations(IEnumerable<string> operationRecordIds) {
        RequireUsable();
        ArgumentNullException.ThrowIfNull(operationRecordIds);
        var requested = new HashSet<string>(operationRecordIds, StringComparer.Ordinal);
        if (requested.Any(string.IsNullOrWhiteSpace)) {
            throw new ArgumentException("Operation references must be nonempty.", nameof(operationRecordIds));
        }
        var results = new List<TownCommittedOperation>();
        if (requested.Count == 0) { return results.AsReadOnly(); }

        IReadOnlyList<CommitInfo> history = _repository.ReadFrames(BranchId);
        int end = FindSelectedState(history, CompletedStateCommitId);
        if (history[0].Kind != CommitKind.State || end % 2 != 0) {
            throw new InvalidDataException("Town history must alternate completed State/Event pairs.");
        }
        TownDurableRoot previous = LoadRoot(_repository, history[0].Id);
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < end; i += 2) {
            CommitInfo eventCommit = history[i], stateCommit = history[i + 1];
            if (eventCommit.Kind != CommitKind.Event || stateCommit.Kind != CommitKind.State ||
                _repository.LoadEvent(eventCommit.Id) is not TownDurableEvent entry) {
                throw new InvalidDataException("Town history must alternate Microtown State/Event pairs.");
            }
            entry.Validate();
            ValidateContinuation(entry.Occurrence, previous.Cursor);
            TownDurableRoot completed = LoadRoot(_repository, stateCommit.Id);
            if (completed.Cursor != previous.Cursor.Advance(entry.Occurrence.CauseKey, entry.Occurrence.TargetInstant)) {
                throw new InvalidDataException("Historical State cursor does not complete its Event.");
            }
            foreach (TownFact fact in entry.Occurrence.Facts) {
                if (fact.OperationReceipt is not { } receipt) { continue; }
                receipt.Validate();
                if (!requested.Contains(receipt.Operation.OperationRecordId)) { continue; }
                if (fact.Kind != TownEventKind.Operation || fact.ActorId != receipt.ActorId || fact.Accepted != receipt.Accepted ||
                    fact.Result != receipt.Result || fact.At != receipt.At) {
                    throw new InvalidDataException("Historical operation provenance does not match its Event fact.");
                }
                if (!found.Add(receipt.Operation.OperationRecordId)) {
                    throw new InvalidDataException("The branch contains multiple completed operations with the same operation ID.");
                }
                results.Add(new(receipt, eventCommit.Id, stateCommit.Id));
            }
            previous = completed;
        }
        return results.AsReadOnly();
    }

    private static TownDurableRoot ReadSelectedState(Repository repository, BranchId branch, CommitId selected) {
        FindSelectedState(repository.ReadFrames(branch), selected);
        TownDurableRoot root = LoadRoot(repository, selected);
        ValidateCompletedBoundary(repository, selected, root);
        return root;
    }

    private static int FindSelectedState(IReadOnlyList<CommitInfo> history, CommitId selected) {
        for (int i = 0; i < history.Count; i++) {
            if (history[i].Id != selected) { continue; }
            if (history[i].Kind != CommitKind.State) {
                throw new InvalidDataException("An exact completed State is required; Event positions are not supported.");
            }
            return i;
        }
        throw new InvalidDataException("The selected State is not in the specified branch's history.");
    }

    private static TownDurableRoot LoadRoot(Repository repository, CommitId stateId) {
        TownDurableRoot root = repository.LoadState(stateId) as TownDurableRoot
            ?? throw new InvalidDataException("The selected State is not a Microtown root.");
        root.Validate();
        return root;
    }

    private static TownDecisionBoundary DescribeDecisionBoundary(BranchId branch, CommitId stateId,
        TownDurableRoot root, TownWorld world, string actorId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (root.Business.OpenDecisionActorId != actorId || TownEngine.NextDecisionActor(world) != actorId) {
            throw new InvalidOperationException("This resident does not own the selected open decision.");
        }
        TownCharacter actor = root.Business.Character(actorId);
        return new(root.GameId, root.GameRevision, branch, stateId, actorId, actor.DecisionSequence, actor.OperationSequence,
            root.Cursor.Version.LineageId, root.Cursor.Version.TransitionCount, root.Cursor.CurrentModelTime.Ticks);
    }
}

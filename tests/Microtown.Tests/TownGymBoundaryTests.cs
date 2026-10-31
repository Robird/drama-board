using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownGymBoundaryTests {
    [Fact]
    public async Task InspectFindsExactReadOnlyAnchorsIncludingARejectedRetry() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        TownDecisionBoundary first, retry;
        CommitId eventCommit, genesis;
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            genesis = history.CompletedStateCommitId;
            var observed = new EventObserver(history);
            var kernel = TownEngine.CreateControlledKernel(observed, (_, _) =>
                ValueTask.FromResult(new TownControllerOperation("  invalid\n", "bad-tool", "bad-operation")));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            first = history.CaptureDecisionBoundary("a");
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            retry = history.CaptureDecisionBoundary("a");
            eventCommit = observed.LastEventCommit!.Value;
            Assert.Equal(first.DecisionSequence, retry.DecisionSequence);
            Assert.Equal(first.OperationSequence + 1, retry.OperationSequence);
            Assert.NotEqual(first.StateCommitId, retry.StateCommitId);
        }

        string[] before = Fingerprint(directory.Path);
        Assert.Equal(first, TownDurableHistory.InspectDecisionBoundary(directory.Path, first.BranchId, first.StateCommitId, "a"));
        Assert.Equal(retry, TownDurableHistory.InspectDecisionBoundary(directory.Path, retry.BranchId, retry.StateCommitId, "a"));
        Assert.Throws<InvalidOperationException>(() => TownDurableHistory.InspectDecisionBoundary(directory.Path, first.BranchId, first.StateCommitId, "b"));
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.InspectDecisionBoundary(directory.Path, first.BranchId, eventCommit, "a"));
        Assert.Throws<InvalidOperationException>(() => TownDurableHistory.InspectDecisionBoundary(directory.Path, first.BranchId, genesis, "a"));
        Assert.Equal(before, Fingerprint(directory.Path));
    }

    [Theory]
    [InlineData("buy grain 1", true)]
    [InlineData("  invalid\n", false)]
    public async Task PendingReceiptIsExcludedUntilRecoveryAndThenHasExactEventStateIdentity(string raw, bool accepted) {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        TownOperationReceipt recorded;
        CommitId eventCommit, previousState;
        int calls = 0;
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            var kernel = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Opening must not call a controller."));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            previousState = history.CompletedStateCommitId;
            var interrupted = TownEngine.CreateControlledKernel(new EventObserver(history, failState: true), (_, _) => {
                calls++;
                return ValueTask.FromResult(new TownControllerOperation(raw, "pending-tool", "pending-operation"));
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.StepAsync(start.World.Business.EndTime).AsTask());
            eventCommit = history.HeadCommitId;
            recorded = Assert.Single(history.PendingEvent!.Facts).OperationReceipt!;
            Assert.Equal(accepted, recorded.Accepted);
            Assert.Empty(history.ReadCommittedOperations(["pending-operation"]));
            Assert.Equal(previousState, history.CompletedStateCommitId);
        }
        Assert.Equal(1, calls);

        string[] beforeRead = Fingerprint(directory.Path);
        using (var reopened = TownDurableHistory.Open(directory.Path)) {
            Assert.Empty(reopened.ReadCommittedOperations(["pending-operation", "missing"]));
            Assert.Equal(eventCommit, reopened.HeadCommitId);
            Assert.Equal(previousState, reopened.CompletedStateCommitId);
            Assert.NotNull(reopened.PendingEvent);
        }
        Assert.Equal(beforeRead, Fingerprint(directory.Path));

        CommitId recoveredState;
        using (var reopened = TownDurableHistory.Open(directory.Path)) {
            var recovery = TownEngine.CreateControlledKernel(reopened, (_, _) => throw new InvalidOperationException("Recovery must not call a controller."));
            Assert.True(recovery.RecoverPending());
            recoveredState = reopened.CompletedStateCommitId;
            TownCommittedOperation operation = Assert.Single(reopened.ReadCommittedOperations(["missing", "pending-operation", "pending-operation"]));
            Assert.Equal(eventCommit, operation.EventCommitId);
            Assert.Equal(recoveredState, operation.StateCommitId);
            Assert.NotEqual(previousState, operation.StateCommitId);
            Assert.Equal(recorded, operation.Receipt);
            Assert.Equal(raw, operation.Receipt.Operation.RawInput);
            Assert.Equal("pending-tool", operation.Receipt.Operation.ToolCallId);
            Assert.False(recovery.RecoverPending());
            Assert.Single(reopened.ReadCommittedOperations(["pending-operation"]));
        }
        Assert.Equal(1, calls);

        string[] afterRecovery = Fingerprint(directory.Path);
        using (var reopened = TownDurableHistory.Open(directory.Path)) {
            TownCommittedOperation operation = Assert.Single(reopened.ReadCommittedOperations(["pending-operation"]));
            Assert.Equal(recoveredState, operation.StateCommitId);
            Assert.Equal(recorded, operation.Receipt);
            Assert.Empty(reopened.ReadCommittedOperations([]));
        }
        Assert.Equal(afterRecovery, Fingerprint(directory.Path));
    }

    [Fact]
    public async Task ForkQueriesInheritOnlySourcePrefixAndKeepBothFuturesSeparateInChronologicalOrder() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        TownDecisionBoundary source, parentFuture;
        TownCommittedOperation prefix, parentOperation;
        using (var parent = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            var commands = new Queue<TownControllerOperation>([
                new("invalid-prefix", "prefix-tool", "prefix"),
                new("buy grain 1", "parent-tool", "parent-future")
            ]);
            var kernel = TownEngine.CreateControlledKernel(parent, (_, _) => ValueTask.FromResult(commands.Dequeue()));
            await Advance(kernel, start.World);
            await Advance(kernel, start.World);
            source = parent.CaptureDecisionBoundary("a");
            prefix = Assert.Single(parent.ReadCommittedOperations(["prefix"]));
            Assert.False(prefix.Receipt.Accepted);
            await Advance(kernel, start.World);
            parentOperation = Assert.Single(parent.ReadCommittedOperations(["parent-future"]));
            await Advance(kernel, start.World);
            parentFuture = parent.CaptureDecisionBoundary("b");
            Assert.Equal(new[] { "prefix", "parent-future" }, parent.ReadCommittedOperations(["parent-future", "prefix"])
                .Select(operation => operation.Receipt.Operation.OperationRecordId));
        }

        TownBranchInfo childInfo = TownDurableHistory.Fork(directory.Path, source.BranchId, source.StateCommitId, "alternative");
        Assert.Equal(source with { BranchId = childInfo.Id }, TownDurableHistory.InspectDecisionBoundary(directory.Path, childInfo.Id, source.StateCommitId, "a"));
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.InspectDecisionBoundary(directory.Path, childInfo.Id, parentFuture.StateCommitId, "b"));
        TownDecisionBoundary childFuture;
        TownCommittedOperation childOperation;
        using (var child = TownDurableHistory.Open(directory.Path, childInfo.Id)) {
            TownBranchInfo metadata = child.CurrentBranch;
            Assert.Equal(childInfo.Id, metadata.Id);
            Assert.Equal(source.StateCommitId, metadata.ForkedFromCommitId);
            Assert.Equal(source.StateCommitId, metadata.HeadCommitId);
            Assert.Equal(source.StateCommitId, metadata.CompletedStateCommitId);
            Assert.False(metadata.HasPendingEvent);
            TownCommittedOperation inherited = Assert.Single(child.ReadCommittedOperations(["prefix", "parent-future"]));
            Assert.Equal(prefix, inherited);
            var kernel = TownEngine.CreateControlledKernel(child, (_, _) =>
                ValueTask.FromResult(new TownControllerOperation("buy wood 1", "child-tool", "child-future")));
            await Advance(kernel, start.World);
            childOperation = Assert.Single(child.ReadCommittedOperations(["child-future"]));
            Assert.Equal("buy wood 1", childOperation.Receipt.Operation.RawInput);
            Assert.Empty(child.ReadCommittedOperations(["parent-future"]));
            await Advance(kernel, start.World);
            childFuture = child.CaptureDecisionBoundary("b");
            Assert.Equal(childFuture.StateCommitId, child.CurrentBranch.HeadCommitId);
            Assert.Equal(childFuture.StateCommitId, child.CurrentBranch.CompletedStateCommitId);
            Assert.Equal(source.StateCommitId, child.CurrentBranch.ForkedFromCommitId);
            Assert.Equal(new[] { "prefix", "child-future" }, child.ReadCommittedOperations(["child-future", "prefix"])
                .Select(operation => operation.Receipt.Operation.OperationRecordId));
        }
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.InspectDecisionBoundary(directory.Path, source.BranchId, childFuture.StateCommitId, "b"));

        string[] beforeRead = Fingerprint(directory.Path);
        using (var parent = TownDurableHistory.Open(directory.Path, source.BranchId)) {
            Assert.Equal(parentOperation, Assert.Single(parent.ReadCommittedOperations(["parent-future"])));
            Assert.Empty(parent.ReadCommittedOperations(["child-future"]));
            Assert.Equal(parentFuture.StateCommitId, parent.HeadCommitId);
        }
        using (var child = TownDurableHistory.Open(directory.Path, childInfo.Id)) {
            Assert.Equal(childOperation, Assert.Single(child.ReadCommittedOperations(["child-future"])));
            Assert.Empty(child.ReadCommittedOperations(["parent-future"]));
            Assert.Equal(childFuture.StateCommitId, child.HeadCommitId);
        }
        Assert.Equal(beforeRead, Fingerprint(directory.Path));
    }

    [Fact]
    public async Task DuplicateCompletedOperationIdentityIsRejectedInsteadOfChoosingAnArbitraryReceipt() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            var kernel = TownEngine.CreateControlledKernel(history, (_, _) =>
                ValueTask.FromResult(new TownControllerOperation("invalid", "duplicate-tool", "duplicate-operation")));
            await Advance(kernel, start.World);
            await Advance(kernel, start.World);
            Assert.Single(history.ReadCommittedOperations(["duplicate-operation"]));
            await Advance(kernel, start.World);
            Assert.Equal(2, history.State.Business.Character("a").OperationSequence);
            Assert.Throws<InvalidDataException>(() => history.ReadCommittedOperations(["duplicate-operation"]));
            Assert.Empty(history.ReadCommittedOperations(["unrelated"]));
        }
        using var reopened = TownDurableHistory.Open(directory.Path);
        Assert.Throws<InvalidDataException>(() => reopened.ReadCommittedOperations(["duplicate-operation"]));
    }

    [Fact]
    public async Task ReceiptCarriedIntoANewRepositoryGenesisDoesNotInventAnOperationEvent() {
        using var sourceDirectory = new TemporaryStore();
        using var targetDirectory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        TownWorld importedWorld;
        KernelCursor importedCursor;
        using (var source = TownDurableHistory.Create(sourceDirectory.Path, start.World, start.Cursor)) {
            var kernel = TownEngine.CreateControlledKernel(source, (_, _) =>
                ValueTask.FromResult(new TownControllerOperation("buy grain 1", "old-tool", "old-operation")));
            await Advance(kernel, start.World);
            await Advance(kernel, start.World);
            Assert.Single(source.ReadCommittedOperations(["old-operation"]));
            importedWorld = source.State;
            importedCursor = source.Cursor;
        }
        using (var target = TownDurableHistory.Create(targetDirectory.Path, importedWorld, importedCursor)) {
            Assert.Equal("old-operation", target.State.Business.Character("a").LastOperationReceipt!.Operation.OperationRecordId);
            Assert.Empty(target.ReadCommittedOperations(["old-operation"]));
        }
        using var reopened = TownDurableHistory.Open(targetDirectory.Path);
        Assert.Empty(reopened.ReadCommittedOperations(["old-operation"]));
    }

    private static async Task Advance(SimulationKernel<TownWorld, TownPrediction, TownFact> kernel, TownWorld start) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.Business.EndTime));

    private static string[] Fingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();

    private sealed class EventObserver(TownDurableHistory inner, bool failState = false) : IOccurrenceHistory<TownWorld, TownFact> {
        public CommitId? LastEventCommit { get; private set; }
        public TownWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<TownFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<TownFact> occurrence) {
            inner.CommitEvent(occurrence);
            LastEventCommit = inner.HeadCommitId;
        }
        public void CommitState(TownWorld state, KernelCursor cursor) {
            if (failState) { throw new IOException("Interrupted after the operation Event."); }
            inner.CommitState(state, cursor);
        }
    }

    private sealed class TemporaryStore : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DramaBoard.Microtown.Gym.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}

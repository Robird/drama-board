using System.Security.Cryptography;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

/// <summary>Independent disk probes for controller provenance, rejection branches and prepublication pauses.</summary>
public sealed class TownPersistenceReviewTests {
    [Fact]
    public async Task RejectionCreatesANewForkableStateWithinTheSameDecisionAndRetainsItsPrivateReceipt() {
        using var files = new Store();
        var initial = TownGameFactory.Create(2);
        BranchId parent;
        CommitId beforeRejection, rejected;
        long decision;
        string material;
        using (var history = TownDurableHistory.Create(files.Path, initial.World, initial.Cursor)) {
            parent = history.BranchId;
            var kernel = TownEngine.CreateControlledKernel(history, (_, _) => ValueTask.FromResult(
                new TownControllerOperation("  secret-invalid-command\n", "same-provider-call", "rejected-operation")));
            await kernel.StepAsync(initial.World.Business.EndTime);
            beforeRejection = history.CompletedStateCommitId;
            decision = history.State.Business.Character("a").DecisionSequence;
            await kernel.StepAsync(initial.World.Business.EndTime);
            rejected = history.CompletedStateCommitId;
            Assert.NotEqual(beforeRejection, rejected);
            Assert.Equal(decision, history.State.Business.Character("a").DecisionSequence);
            Assert.Equal("a", history.State.Business.OpenDecisionActorId);
            material = TownTextRenderer.Render(TownObservation.Capture(history.State, "a"));
            Assert.DoesNotContain("secret-invalid-command", TownTextRenderer.Render(TownObservation.Capture(history.State, "b")));
        }
        string[] filesBefore = Fingerprint(files.Path);
        using (var reopened = TownDurableHistory.Open(files.Path, parent)) {
            Assert.Equal(material, TownTextRenderer.Render(TownObservation.Capture(reopened.State, "a")));
            Assert.False(reopened.State.Business.Character("a").LastOperationReceipt!.Accepted);
        }
        Assert.Equal(filesBefore, Fingerprint(files.Path));
        TownBranchInfo child = TownDurableHistory.Fork(files.Path, parent, rejected, "retry-after-rejection");
        using (var history = TownDurableHistory.Open(files.Path, child.Id)) {
            var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
                Assert.Equal(decision, observation.DecisionSequence);
                Assert.Equal(1, observation.OperationSequence);
                Assert.Equal("rejected-operation", observation.LastOperationReceipt!.Operation.OperationRecordId);
                // Providers may repeat call IDs across independent requests; host operation identity distinguishes them.
                return ValueTask.FromResult(new TownControllerOperation("buy grain 1", "same-provider-call", "child-operation"));
            });
            await kernel.StepAsync(initial.World.Business.EndTime);
            Assert.Equal(1, history.State.Business.Character("a").Inventory.Count("grain"));
            Assert.Equal(2, history.State.Business.Character("a").LastOperationReceipt!.OperationSequence);
        }
        using var unchanged = TownDurableHistory.Open(files.Path, parent);
        Assert.Equal(rejected, unchanged.HeadCommitId);
        Assert.Equal(0, unchanged.State.Business.Character("a").Inventory.Count("grain"));
        Assert.Equal(material, TownTextRenderer.Render(TownObservation.Capture(unchanged.State, "a")));
    }

    [Theory]
    [InlineData("buy grain 2", true)]
    [InlineData("  not-a-command\n", false)]
    [InlineData("create-goal {\"title\":\"Private provision\",\"criteria\":\"Own two meals\"}", true)]
    public async Task DiskPendingOperationRetainsExactReceiptAndRecoveryDoesNotInvokeController(string raw, bool accepted) {
        using var files = new Store();
        var initial = TownGameFactory.Create(2);
        using (var history = TownDurableHistory.Create(files.Path, initial.World, initial.Cursor)) {
            var opening = TownEngine.CreateControlledKernel(history, (_, _) => throw new Exception("Opening must not ask a controller."));
            await opening.StepAsync(initial.World.Business.EndTime);
            var scratch = new InMemoryOccurrenceHistory<TownWorld, TownFact>(history.State, history.Cursor);
            var prepare = TownEngine.CreateControlledKernel(scratch, (_, _) => ValueTask.FromResult(
                new TownControllerOperation(raw, "disk-tool", "disk-operation")));
            await prepare.StepAsync(initial.World.Business.EndTime);
            history.CommitEvent(prepare.LastCompletion!.Event);
        }
        string[] filesBefore = Fingerprint(files.Path);
        using (var preview = TownDurableHistory.Open(files.Path)) {
            Assert.Null(preview.State.Business.Character("a").LastOperationReceipt);
            Assert.NotNull(preview.PendingEvent);
        }
        Assert.Equal(filesBefore, Fingerprint(files.Path));
        using (var reopened = TownDurableHistory.Open(files.Path)) {
            Assert.Null(reopened.State.Business.Character("a").LastOperationReceipt);
            TownOperationReceipt receipt = Assert.Single(reopened.PendingEvent!.Facts).OperationReceipt!;
            Assert.Equal(raw, receipt.Operation.RawInput);
            Assert.Equal(accepted, receipt.Accepted);
            var kernel = TownEngine.CreateControlledKernel(reopened, (_, _) => throw new Exception("Recovery asked the controller."));
            Assert.True(kernel.RecoverPending());
            Assert.False(kernel.RecoverPending());
            Assert.Equal(receipt, reopened.State.Business.Character("a").LastOperationReceipt);
            Assert.Equal(raw == "buy grain 2" ? 2 : 0, reopened.State.Business.Character("a").Inventory.Count("grain"));
            Assert.Equal(1, reopened.State.Business.Character("a").OperationSequence);
        }
        using var cold = TownDurableHistory.Open(files.Path);
        Assert.Null(cold.PendingEvent);
        TownOperationReceipt saved = cold.State.Business.Character("a").LastOperationReceipt!;
        Assert.Equal(raw, saved.Operation.RawInput);
        Assert.Equal("disk-operation", saved.Operation.OperationRecordId);
        Assert.Equal("disk-tool", saved.Operation.ToolCallId);
        Assert.Equal(accepted, saved.Accepted);
        if (!accepted) { Assert.Equal("a", cold.State.Business.OpenDecisionActorId); }
        cold.State.Business.Economy.Verify(cold.State.Business);
    }

    [Fact]
    public async Task ControllerPauseLeavesCompletedDecisionUnchangedAndColdReopenCanResumeIt() {
        using var files = new Store();
        var initial = TownGameFactory.Create(2);
        CommitId completed;
        KernelCursor cursor;
        using (var history = TownDurableHistory.Create(files.Path, initial.World, initial.Cursor)) {
            var kernel = TownEngine.CreateControlledKernel(history, (_, _) => throw new PauseProbe());
            await kernel.StepAsync(initial.World.Business.EndTime);
            completed = history.CompletedStateCommitId;
            cursor = history.Cursor;
        }
        string[] before = Fingerprint(files.Path);
        using (var history = TownDurableHistory.Open(files.Path)) {
            var kernel = TownEngine.CreateControlledKernel(history, (_, _) => throw new PauseProbe());
            await Assert.ThrowsAsync<PauseProbe>(async () => await kernel.StepAsync(initial.World.Business.EndTime));
            Assert.False(kernel.IsFaulted);
            Assert.Null(history.PendingEvent);
            Assert.Equal(completed, history.HeadCommitId);
            Assert.Equal(cursor, history.Cursor);
        }
        Assert.Equal(before, Fingerprint(files.Path));
        using var reopened = TownDurableHistory.Open(files.Path);
        Assert.Equal(completed, reopened.CompletedStateCommitId);
        Assert.Equal(cursor, reopened.Cursor);
        var resume = TownEngine.CreateControlledKernel(reopened, (observation, _) => {
            Assert.True(observation.IsDecisionOpen);
            Assert.Equal("a", observation.ActorId);
            Assert.Equal(0, observation.OperationSequence);
            return ValueTask.FromResult(new TownControllerOperation("wait 1", "resume-tool", "resume-operation"));
        });
        Assert.Equal(StepStatus.Committed, await resume.StepAsync(initial.World.Business.EndTime));
        Assert.Equal(TownActivityKind.Wait, reopened.State.Business.Character("a").Activity!.Kind);
    }

    private static string[] Fingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(stream))}";
        }).ToArray();
    private sealed class PauseProbe : Exception;
    private sealed class Store : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "town-review-" + Guid.NewGuid().ToString("N"));
        public Store() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "world");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}

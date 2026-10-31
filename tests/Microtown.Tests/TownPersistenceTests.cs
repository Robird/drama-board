using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownPersistenceTests {
    [Fact]
    public async Task DecisionAnchorsIdentifyActualCompletedStatesAcrossRejectedRetryAndColdReopen() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        TownDecisionBoundary first, retry;
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("a"));
            int calls = 0;
            var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
                calls++;
                TownDecisionBoundary anchor = history.CaptureDecisionBoundary(observation.ActorId);
                Assert.Equal(history.CompletedStateCommitId, anchor.StateCommitId);
                Assert.Equal(history.HeadCommitId, anchor.StateCommitId);
                Assert.Equal(observation.DecisionSequence, anchor.DecisionSequence);
                Assert.Equal(0, anchor.OperationSequence);
                return ValueTask.FromResult(new TownControllerOperation("invalid-command", "bad-tool", "bad-operation"));
            });
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            Assert.Equal(0, calls);
            first = history.CaptureDecisionBoundary("a");
            Assert.Equal(TownGameRevision.GameId, first.GameId);
            Assert.Equal(TownGameRevision.Current, first.GameRevision);
            Assert.Equal(history.BranchId, first.BranchId);
            Assert.Equal("a", first.ActorId);
            Assert.Equal(history.Cursor.Version.LineageId, first.LineageId);
            Assert.Equal(history.Cursor.Version.TransitionCount, first.TransitionCount);
            Assert.Equal(history.Cursor.CurrentModelTime.Ticks, first.TimeTicks);
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("b"));
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("missing"));

            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            Assert.Equal(1, calls);
            retry = history.CaptureDecisionBoundary("a");
            Assert.Equal(first.DecisionSequence, retry.DecisionSequence);
            Assert.Equal(first.OperationSequence + 1, retry.OperationSequence);
            Assert.NotEqual(first.StateCommitId, retry.StateCommitId);
            Assert.Equal(first.TransitionCount + 1, retry.TransitionCount);
            Assert.Equal(first.TimeTicks, retry.TimeTicks);
            Assert.Equal(history.CompletedStateCommitId, retry.StateCommitId);
            Assert.False(history.State.Business.Character("a").LastOperationReceipt!.Accepted);
        }

        using (var reopened = TownDurableHistory.Open(directory.Path)) {
            Assert.Equal(retry, reopened.CaptureDecisionBoundary("a"));
            int calls = 0;
            var kernel = TownEngine.CreateControlledKernel(reopened, (observation, _) => {
                calls++;
                Assert.Equal(retry, reopened.CaptureDecisionBoundary(observation.ActorId));
                Assert.False(observation.LastOperationReceipt!.Accepted);
                return ValueTask.FromResult(new TownControllerOperation("buy grain 1", "buy-tool", "buy-operation"));
            });
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            Assert.Equal(1, calls);
            TownCharacter actor = reopened.State.Business.Character("a");
            Assert.True(actor.LastOperationReceipt!.Accepted);
            Assert.Equal(retry.DecisionSequence, actor.DecisionSequence);
            Assert.Equal(retry.OperationSequence + 1, actor.OperationSequence);
            Assert.Equal(1, actor.Inventory.Count("grain"));
            Assert.NotEqual(retry.StateCommitId, reopened.CompletedStateCommitId);
            Assert.Throws<InvalidOperationException>(() => reopened.CaptureDecisionBoundary("a"));
        }
    }

    [Fact]
    public async Task EveryEconomicBoundaryColdReopensAndContinuesTheSameTrace() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(26);
        var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var baseline = TownEngine.CreateKernel(memory, EconomicScript);
        using (var created = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            Assert.Equal(Snapshot(memory.State), Snapshot(created.State));
            Assert.Equal(memory.Cursor, created.Cursor);
        }

        var kinds = new HashSet<TownEventKind>();
        for (int step = 0; step < 400; step++) {
            using var reopened = TownDurableHistory.Open(directory.Path);
            Assert.Null(reopened.PendingEvent);
            Assert.Equal(Snapshot(memory.State), Snapshot(reopened.State));
            Assert.Equal(memory.Cursor, reopened.Cursor);
            Assert.Same(reopened.State.Business.Spatial, reopened.State.Spatial.Business);
            var resumed = TownEngine.CreateKernel(reopened, EconomicScript);
            Assert.False(resumed.RecoverPending());
            if (memory.State.Business.Ended) {
                Assert.Contains(TownEventKind.ProductionCompleted, kinds);
                Assert.Contains(TownEventKind.MarketRefreshed, kinds);
                Assert.Contains(TownEventKind.Arrived, kinds);
                Assert.All(reopened.State.Business.Characters, actor => Assert.False(actor.Failed));
                Assert.True(reopened.State.Business.Economy.ExportIncome > 0);
                Assert.All(reopened.State.Business.Characters, actor => Assert.Single(actor.Goals.Goals));
                return;
            }
            Assert.Equal(StepStatus.Committed, await baseline.StepAsync(start.World.Business.EndTime));
            Assert.Equal(StepStatus.Committed, await resumed.StepAsync(start.World.Business.EndTime));
            Assert.Equal(Snapshot(memory.State), Snapshot(reopened.State));
            Assert.Equal(memory.Cursor, reopened.Cursor);
            Assert.Equal(baseline.LastCompletion!.Event.CauseKey, resumed.LastCompletion!.Event.CauseKey);
            Assert.All(resumed.LastCompletion.Event.Facts, fact => {
                kinds.Add(fact.Kind);
                Assert.True(fact.Accepted, fact.Result);
            });
        }
        Assert.Fail("The finite cold-reopen economy did not finish within 400 occurrences.");
    }

    [Fact]
    public async Task OpenIsReadOnlyAndPendingRecoveryAppliesPurchaseExactlyOnceWithoutController() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        string before, expected;
        KernelCursor beforeCursor, expectedCursor;
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            var opening = TownEngine.CreateKernel(history, BuyGrain);
            Assert.Equal(StepStatus.Committed, await opening.StepAsync(start.World.Business.EndTime));
            Assert.Equal("a", history.State.Business.OpenDecisionActorId);
            before = Snapshot(history.State);
            beforeCursor = history.Cursor;

            var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(history.State, history.Cursor);
            var baseline = TownEngine.CreateKernel(memory, BuyGrain);
            Assert.Equal(StepStatus.Committed, await baseline.StepAsync(start.World.Business.EndTime));
            expected = Snapshot(memory.State);
            expectedCursor = memory.Cursor;
            history.CommitEvent(baseline.LastCompletion!.Event);
            Assert.Equal(before, Snapshot(history.State));
        }

        string[] disk = Fingerprint(directory.Path);
        using (var reopened = TownDurableHistory.Open(directory.Path)) {
            Assert.Equal(before, Snapshot(reopened.State));
            Assert.Equal(beforeCursor, reopened.Cursor);
            Assert.NotNull(reopened.PendingEvent);
            Assert.Throws<InvalidOperationException>(() => reopened.CaptureDecisionBoundary("a"));
        }
        Assert.Equal(disk, Fingerprint(directory.Path));
        using (var reopened = TownDurableHistory.Open(directory.Path)) {
            var resumed = TownEngine.CreateKernel(reopened, (_, _) => throw new InvalidOperationException("Recovery called the controller."));
            Assert.True(resumed.RecoverPending());
            Assert.Equal(expected, Snapshot(resumed.World));
            Assert.Equal(expectedCursor, resumed.Cursor);
            Assert.Null(reopened.PendingEvent);
            Assert.False(resumed.RecoverPending());
            Assert.Equal(1, resumed.World.Business.Character("a").Inventory.Count("grain"));
            Assert.Equal(16, resumed.World.Business.Character("a").Money);
            Assert.Equal(2, resumed.World.Business.Economy.ImportExpense);
        }
        using var final = TownDurableHistory.Open(directory.Path);
        Assert.Equal(expected, Snapshot(final.State));
        Assert.Null(final.PendingEvent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KernelInstallsNoPreparedWorldWhenStatePublicationThrows(bool afterPublication) {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        string expected;
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            TownWorld old = history.State;
            var observed = new CrashOnState(history, afterPublication);
            var kernel = TownEngine.CreateKernel(observed, BuyGrain);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(start.World.Business.EndTime).AsTask());
            Assert.IsType<IOException>(error.InnerException);
            Assert.True(kernel.IsFaulted);
            Assert.Same(old, kernel.World);
            Assert.Equal(start.Cursor, kernel.Cursor);
            expected = Snapshot(Assert.IsType<TownWorld>(observed.Prepared));
            if (afterPublication) {
                Assert.Same(observed.Prepared, history.State);
                Assert.Null(history.PendingEvent);
            } else {
                Assert.Same(old, history.State);
                Assert.NotNull(history.PendingEvent);
            }
        }
        using var reopened = TownDurableHistory.Open(directory.Path);
        var recovered = TownEngine.CreateKernel(reopened, (_, _) => throw new InvalidOperationException("Recovery replanned."));
        Assert.Equal(!afterPublication, recovered.RecoverPending());
        Assert.Equal(expected, Snapshot(recovered.World));
        Assert.False(recovered.RecoverPending());
    }

    [Fact]
    public async Task RejectedStateCursorFaultsAdapterAndLeavesRecoverableEvent() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var baseline = TownEngine.CreateKernel(memory, BuyGrain);
        Assert.Equal(StepStatus.Committed, await baseline.StepAsync(start.World.Business.EndTime));
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            history.CommitEvent(baseline.LastCompletion!.Event);
            Assert.Throws<InvalidDataException>(() => history.CommitState(memory.State, start.Cursor));
            Assert.True(history.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => history.State);
        }
        using var reopened = TownDurableHistory.Open(directory.Path);
        var resumed = TownEngine.CreateKernel(reopened, (_, _) => throw new InvalidOperationException("Recovery replanned."));
        Assert.True(resumed.RecoverPending());
        Assert.Equal(Snapshot(memory.State), Snapshot(resumed.World));
        Assert.Equal(memory.Cursor, resumed.Cursor);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnknownGameOrRevisionInStateOrPendingEventIsRejectedWithoutWriting(bool eventBoundary, bool wrongRevision) {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var kernel = TownEngine.CreateKernel(memory, BuyGrain);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
        string gameId = wrongRevision ? TownGameRevision.GameId : "another-game";
        int revision = wrongRevision ? TownGameRevision.Current + 1 : TownGameRevision.Current;
        var root = new TownDurableRoot(start.World.Business, start.Cursor,
            eventBoundary ? TownGameRevision.GameId : gameId,
            eventBoundary ? TownGameRevision.Current : revision);
        using (Repository repository = Repository.CreateNew(directory.Path, Models())) {
            BranchId branch = repository.CreateRootBranchFromState("invalid-version-fixture", root).Id;
            if (eventBoundary) {
                using WorkingCopy work = repository.OpenWorkingCopy(branch);
                work.CommitEvent(new TownDurableEvent(kernel.LastCompletion!.Event, gameId, revision));
            }
        }
        string[] before = Fingerprint(directory.Path);
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.Open(directory.Path));
        Assert.Equal(before, Fingerprint(directory.Path));
    }

    [Fact]
    public async Task ForkFromOldCompletedStatePreservesCursorAndEconomyAndIsolatesBothFutures() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        BranchId parentId;
        CommitId source, parentHead;
        KernelCursor sourceCursor;
        string sourceState, parentState;
        using (var parent = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            parentId = parent.BranchId;
            var kernel = TownEngine.CreateKernel(parent, BuyGrain);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            source = parent.CompletedStateCommitId;
            sourceCursor = parent.Cursor;
            sourceState = Snapshot(parent.State);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            parentHead = parent.HeadCommitId;
            parentState = Snapshot(parent.State);
        }
        string[] beforeList = Fingerprint(directory.Path);
        Assert.Single(TownDurableHistory.ListBranches(directory.Path));
        Assert.Equal(beforeList, Fingerprint(directory.Path));
        TownBranchInfo fork = TownDurableHistory.Fork(directory.Path, parentId, source, "alternate purchase");
        Assert.Equal(source, fork.ForkedFromCommitId);
        Assert.Equal(source, fork.HeadCommitId);
        Assert.Equal(source, fork.CompletedStateCommitId);
        Assert.False(fork.HasPendingEvent);
        Assert.Equal(2, TownDurableHistory.ListBranches(directory.Path).Count);
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.Open(directory.Path));
        CommitId childHead;
        using (var child = TownDurableHistory.Open(directory.Path, fork.Id)) {
            Assert.Equal(sourceState, Snapshot(child.State));
            Assert.Equal(sourceCursor, child.Cursor);
            var kernel = TownEngine.CreateKernel(child, (_, _) => ValueTask.FromResult(TownCommand.Parse("buy wood 1")));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            Assert.Equal(1, child.State.Business.Character("a").Inventory.Count("wood"));
            Assert.Equal(0, child.State.Business.Character("a").Inventory.Count("grain"));
            childHead = child.HeadCommitId;
        }
        using (var parent = TownDurableHistory.Open(directory.Path, parentId)) {
            Assert.Equal(parentHead, parent.HeadCommitId);
            Assert.Equal(parentState, Snapshot(parent.State));
            Assert.Equal(0, parent.State.Business.Character("a").Inventory.Count("wood"));
            Assert.Equal(1, parent.State.Business.Character("a").Inventory.Count("grain"));
        }
        using (var child = TownDurableHistory.Open(directory.Path, fork.Id)) {
            Assert.Equal(childHead, child.HeadCommitId);
            Assert.Equal(3, child.State.Business.Economy.ImportExpense);
        }
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.Fork(directory.Path, fork.Id, parentHead, "foreign future"));
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.Fork(directory.Path, parentId, childHead, "foreign child"));
    }

    [Fact]
    public void ForkRejectsGenesisWithoutAnOpenDecisionAndCreatesNoBranch() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        BranchId branch;
        CommitId genesis;
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            Assert.Null(history.State.Business.OpenDecisionActorId);
            branch = history.BranchId;
            genesis = history.CompletedStateCommitId;
        }
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.Fork(directory.Path, branch, genesis, "no decision"));
        Assert.Single(TownDurableHistory.ListBranches(directory.Path));
    }

    [Fact]
    public async Task ForkRejectsAnEventPositionWithoutCreatingAnotherBranch() {
        using var directory = new TemporaryStore();
        var start = TownGameFactory.Create(2);
        var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var kernel = TownEngine.CreateKernel(memory, BuyGrain);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
        BranchId branch;
        CommitId eventCommit;
        using (var history = TownDurableHistory.Create(directory.Path, start.World, start.Cursor)) {
            branch = history.BranchId;
            history.CommitEvent(kernel.LastCompletion!.Event);
            eventCommit = history.HeadCommitId;
            Assert.NotEqual(history.CompletedStateCommitId, eventCommit);
        }
        Assert.Throws<InvalidDataException>(() => TownDurableHistory.Fork(directory.Path, branch, eventCommit, "invalid event fork"));
        Assert.Single(TownDurableHistory.ListBranches(directory.Path));
    }

    private static ValueTask<TownCommand> BuyGrain(TownDecision decision, CancellationToken token) =>
        ValueTask.FromResult(TownCommand.Parse("buy grain 1"));

    private static ValueTask<TownCommand> EconomicScript(TownDecision decision, CancellationToken token) {
        string[] initial = ["create-goal {\"title\":\"Live and trade\",\"criteria\":\"Finish the experiment with food\",\"focus\":true}",
            "buy grain 4", "buy wood 2", "go market-workshop", "produce meal 2", "produce woodware 1",
            "go market-workshop", "sell woodware 1", "sell meal 1"];
        long operation = decision.Character.OperationSequence;
        string command = operation < initial.Length ? initial[(int)operation]
            : decision.Character.Food <= 40 && decision.Character.Inventory.Count("meal") > 0 ? "eat" : "wait 4";
        return ValueTask.FromResult(TownCommand.Parse(command));
    }

    private static string Snapshot(TownWorld world) => JsonSerializer.Serialize(new {
        world.Business,
        // SpatialLocation is abstract; serialize concrete values so traversal clocks and route identity are compared too.
        Locations = world.Business.Spatial.Entities.Select(entity => new { entity.Id, Location = (object)entity.Location })
    });
    private static string[] Fingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(stream))}";
        }).ToArray();
    private static StateModelRegistry Models() {
        var registry = new StateModelRegistry();
        TownDurableModels.Register(registry);
        return registry;
    }

    private sealed class CrashOnState(TownDurableHistory inner, bool afterPublication) : IOccurrenceHistory<TownWorld, TownFact> {
        public TownWorld? Prepared { get; private set; }
        public TownWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<TownFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<TownFact> occurrence) => inner.CommitEvent(occurrence);
        public void CommitState(TownWorld state, KernelCursor cursor) {
            Prepared = state;
            if (afterPublication) { inner.CommitState(state, cursor); }
            throw new IOException("Injected interruption at State publication.");
        }
    }

    private sealed class TemporaryStore : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DramaBoard.Microtown.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}

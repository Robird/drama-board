using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownSocialAcceptancePersistenceTests {
    [Fact]
    public async Task Pending_busy_cancel_returns_inputs_once_and_a_second_cancel_is_rejected_after_cold_recovery() {
        using var files = new Files();
        var start = TownGameFactory.Create(4);
        using (var history = TownDurableHistory.Create(files.Store, start.World, start.Cursor)) {
            var a = new Queue<string>(["buy grain 4", "go market-workshop", "produce meal 2"]);
            var kernel = TownEngine.CreateKernel(history, (decision, _) => ValueTask.FromResult(TownCommand.Parse(
                decision.ActorId switch {
                    "a" => a.Dequeue(),
                    "b" when decision.World.Business.Place("b") == "market" => "go market-workshop",
                    "b" when decision.World.Business.Character("a").Activity?.Kind == TownActivityKind.Produce => "say a interrupt production",
                    "b" => "wait 1",
                    _ => "wait 4"
                })));
            for (int step = 0; step < 100 && !(history.State.Business.OpenDecisionActorId == "a" &&
                history.State.Business.Character("a").Activity is not null && history.State.Business.Character("a").Interaction.NeedsResponse); step++) {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            }
            Assert.Equal("a", history.State.Business.OpenDecisionActorId);
            Assert.Equal(TownActivityKind.Produce, history.State.Business.Character("a").Activity!.Kind);
            Assert.Equal(4, history.State.Business.Character("a").Activity!.InputCount);
            var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(history.State, history.Cursor);
            var cancel = TownEngine.CreateControlledKernel(memory, (_, _) => ValueTask.FromResult(
                new TownControllerOperation("cancel", "cancel-tool", "cancel-operation")));
            Assert.Equal(StepStatus.Committed, await cancel.StepAsync(start.World.Business.EndTime));
            Assert.True(Assert.Single(cancel.LastCompletion!.Event.Facts).Accepted);
            history.CommitEvent(cancel.LastCompletion.Event);
            Assert.Equal(0, history.State.Business.Character("a").Inventory.Count("grain"));
        }
        using (var history = TownDurableHistory.Open(files.Store)) {
            var kernel = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Recovery asked for a cancellation again."));
            Assert.True(kernel.RecoverPending());
            Assert.False(kernel.RecoverPending());
            Assert.Null(history.State.Business.Character("a").Activity);
            Assert.Equal(4, history.State.Business.Character("a").Inventory.Count("grain"));
            Assert.Empty(history.State.Business.Economy.Produced);
            Assert.Empty(history.State.Business.Economy.RecipeInputs);
        }
        using var final = TownDurableHistory.Open(files.Store);
        bool retried = false;
        var retry = TownEngine.CreateControlledKernel(final, (observation, _) => {
            string command = observation.ActorId == "a" ? "cancel" : "wait 4";
            retried |= observation.ActorId == "a";
            return ValueTask.FromResult(new TownControllerOperation(command, "retry-" + observation.ActorId, "retry-op-" + observation.ActorId));
        });
        for (int step = 0; step < 100 && !retried; step++) { Assert.Equal(StepStatus.Committed, await retry.StepAsync(start.World.Business.EndTime)); }
        Assert.True(retried);
        Assert.False(final.State.Business.Character("a").LastOperationReceipt!.Accepted);
        Assert.Equal(4, final.State.Business.Character("a").Inventory.Count("grain"));
        Assert.Equal(1, final.State.Business.Character("a").Inventory.Count("meal"));
        final.State.Business.Economy.Verify(final.State.Business);
    }

    [Theory]
    [InlineData("give b meal 1", 18, 18, 0, 2)]
    [InlineData("give b money 5", 13, 23, 1, 1)]
    [InlineData("say b persistent hello", 18, 18, 1, 1)]
    public async Task Cold_pending_social_fact_restores_both_sides_once_without_reasking_controller(
        string raw, long moneyA, long moneyB, int mealsA, int mealsB) {
        using var files = new Files();
        var start = TownGameFactory.Create(4);
        string entryId;
        using (var history = TownDurableHistory.Create(files.Store, start.World, start.Cursor)) {
            var opening = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Opening must not call a controller."));
            Assert.Equal(StepStatus.Committed, await opening.StepAsync(start.World.Business.EndTime));
            var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(history.State, history.Cursor);
            var planner = TownEngine.CreateControlledKernel(memory, (_, _) => ValueTask.FromResult(
                new TownControllerOperation(raw, "persisted-social-tool", "persisted-social-operation")));
            Assert.Equal(StepStatus.Committed, await planner.StepAsync(start.World.Business.EndTime));
            TownFact fact = Assert.Single(planner.LastCompletion!.Event.Facts);
            Assert.True(fact.Accepted, fact.Result);
            entryId = Assert.Single(memory.State.Business.Character("b").Interaction.Entries).Id;
            history.CommitEvent(planner.LastCompletion.Event); // Real disk E; deliberately omit S.
            Assert.Empty(history.State.Business.Character("b").Interaction.Entries);
        }
        using (var pending = TownDurableHistory.Open(files.Store)) {
            Assert.NotNull(pending.PendingEvent);
            Assert.All(pending.State.Business.Characters, actor => {
                Assert.Equal(18, actor.Money);
                Assert.Equal(1, actor.Inventory.Count("meal"));
                Assert.Empty(actor.Interaction.Entries);
            });
            var recovery = TownEngine.CreateControlledKernel(pending, (_, _) => throw new InvalidOperationException("Recovery called a controller."));
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Verify(pending.State.Business);
        }
        using (var reopened = TownDurableHistory.Open(files.Store)) {
            Assert.Null(reopened.PendingEvent);
            Verify(reopened.State.Business);
            var kernel = TownEngine.CreateControlledKernel(reopened, (_, _) => throw new InvalidOperationException("Publishing an opening called the controller."));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            Assert.Equal("b", reopened.State.Business.OpenDecisionActorId);
            Assert.Equal(1, reopened.State.Business.Character("b").Interaction.DeliveredThrough);
            Assert.Equal(0, reopened.State.Business.Character("b").Interaction.PendingCount);
        }
        using var final = TownDurableHistory.Open(files.Store);
        string first = TownTextRenderer.Render(TownObservation.Capture(final.State, "b"));
        Assert.Equal(first, TownTextRenderer.Render(TownObservation.Capture(final.State, "b")));
        Assert.Equal(1, final.State.Business.Character("b").Interaction.DeliveredThrough);
        Assert.Equal(entryId, Assert.Single(final.State.Business.Character("b").Interaction.Entries).Id);

        void Verify(TownBusinessState state) {
            Assert.Equal(moneyA, state.Character("a").Money);
            Assert.Equal(moneyB, state.Character("b").Money);
            Assert.Equal(mealsA, state.Character("a").Inventory.Count("meal"));
            Assert.Equal(mealsB, state.Character("b").Inventory.Count("meal"));
            Assert.Equal(entryId, Assert.Single(state.Character("a").Interaction.Entries).Id);
            Assert.Equal(entryId, Assert.Single(state.Character("b").Interaction.Entries).Id);
            Assert.Empty(state.Character("c").Interaction.Entries);
            Assert.Equal(1, state.Character("a").OperationSequence);
            Assert.Equal("persisted-social-operation", state.Character("a").LastOperationReceipt!.Operation.OperationRecordId);
            Assert.Equal(54, state.Characters.Sum(actor => actor.Money));
            Assert.Equal(3, state.Characters.Sum(actor => actor.Inventory.Count("meal")));
            state.Economy.Verify(state);
        }
    }

    [Fact]
    public async Task Exact_completed_state_fork_keeps_parent_future_messages_out_of_child_and_child_out_of_parent() {
        using var files = new Files();
        var start = TownGameFactory.Create(4);
        TownDecisionBoundary source;
        string parentHead, parentMaterial;
        using (var history = TownDurableHistory.Create(files.Store, start.World, start.Cursor)) {
            var kernel = TownEngine.CreateControlledKernel(history, (_, _) => ValueTask.FromResult(
                new TownControllerOperation("say b parent-only future", "parent-tool", "parent-operation")));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            source = history.CaptureDecisionBoundary("a");
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            parentHead = TownDurableHistory.FormatCommitId(history.HeadCommitId);
            parentMaterial = TownTextRenderer.Render(TownObservation.Capture(history.State, "b"));
            Assert.Contains("parent-only future", parentMaterial);
        }
        TownBranchInfo child = TownDurableHistory.Fork(files.Store, source.BranchId, source.StateCommitId, "social-isolation");
        using (var history = TownDurableHistory.Open(files.Store, child.Id)) {
            Assert.Equal(source.StateCommitId, history.CompletedStateCommitId);
            Assert.All(history.State.Business.Characters, actor => Assert.Empty(actor.Interaction.Entries));
            int calls = 0;
            var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
                calls++;
                Assert.Equal("a", observation.ActorId);
                Assert.DoesNotContain("parent-only future", TownTextRenderer.Render(observation));
                return ValueTask.FromResult(new TownControllerOperation("say b child-only branch", "child-tool", "child-operation"));
            });
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
            Assert.Equal(1, calls);
            var message = Assert.Single(history.State.Business.Character("b").Interaction.Entries);
            Assert.Equal("child-only branch", message.Text);
        }
        using (var parent = TownDurableHistory.Open(files.Store, source.BranchId)) {
            Assert.Equal(parentHead, TownDurableHistory.FormatCommitId(parent.HeadCommitId));
            Assert.Equal(parentMaterial, TownTextRenderer.Render(TownObservation.Capture(parent.State, "b")));
            var message = Assert.Single(parent.State.Business.Character("b").Interaction.Entries);
            Assert.Equal("parent-only future", message.Text);
        }
        using var coldChild = TownDurableHistory.Open(files.Store, child.Id);
        var childMessage = Assert.Single(coldChild.State.Business.Character("b").Interaction.Entries);
        Assert.Equal("child-only branch", childMessage.Text);
    }

    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "microtown-social-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}

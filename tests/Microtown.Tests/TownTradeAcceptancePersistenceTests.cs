using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownTradeAcceptancePersistenceTests {
    [Fact]
    public async Task Pending_acceptance_cold_recovers_both_sides_once_without_reasking_and_repeat_accept_is_rejected() {
        using var files = new Files();
        var start = TownGameFactory.Create(4);
        string offerId, before, settled, pendingHead;
        using (var history = TownDurableHistory.Create(files.Store, start.World, start.Cursor)) {
            offerId = await OpenRecipientDecisionAfterOffer(history);
            before = Snapshot(history.State.Business);
            VerifyUnsettled(history.State.Business);

            // Generate the actual acceptance through the normal Kernel at the exact persisted decision S.
            var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(history.State, history.Cursor);
            int calls = 0;
            var planner = TownEngine.CreateControlledKernel(memory, (observation, _) => {
                calls++;
                Assert.Equal("b", observation.ActorId);
                return ValueTask.FromResult(new TownControllerOperation($"accept {offerId}", "accept-tool", "accept-operation"));
            });
            Assert.Equal(StepStatus.Committed, await planner.StepAsync(start.World.Business.EndTime));
            Assert.Equal(1, calls);
            TownFact fact = Assert.Single(planner.LastCompletion!.Event.Facts);
            Assert.True(fact.Accepted, fact.Result);
            VerifySettled(memory.State.Business);
            Assert.Equal(TownTradeStatus.Accepted, Assert.Single(memory.State.Business.Trades.Offers).Status);
            settled = Snapshot(memory.State.Business);

            history.CommitEvent(planner.LastCompletion.Event); // Deliberate interruption after durable E, before S.
            Assert.NotNull(history.PendingEvent);
            Assert.Equal(before, Snapshot(history.State.Business));
            pendingHead = TownDurableHistory.FormatCommitId(history.HeadCommitId);
        }

        using (var history = TownDurableHistory.Open(files.Store)) {
            Assert.NotNull(history.PendingEvent);
            Assert.Equal(pendingHead, TownDurableHistory.FormatCommitId(history.HeadCommitId));
            Assert.Equal(before, Snapshot(history.State.Business));
            var recovery = TownEngine.CreateControlledKernel(history,
                (_, _) => throw new InvalidOperationException("Recovery must not request another acceptance."));
            Assert.True(recovery.RecoverPending());
            Assert.Null(history.PendingEvent);
            Assert.NotEqual(pendingHead, TownDurableHistory.FormatCommitId(history.HeadCommitId));
            Assert.Equal(settled, Snapshot(history.State.Business));
            string completedHead = TownDurableHistory.FormatCommitId(history.HeadCommitId);
            Assert.False(recovery.RecoverPending());
            Assert.Equal(completedHead, TownDurableHistory.FormatCommitId(history.HeadCommitId));
            Assert.Equal(settled, Snapshot(history.State.Business));
        }

        using (var history = TownDurableHistory.Open(files.Store)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(settled, Snapshot(history.State.Business));
            Assert.Equal("accept-operation", history.State.Business.Character("b").LastOperationReceipt!.Operation.OperationRecordId);
            await ExecuteOperation(history, "b", $"accept {offerId}", "repeat-accept", expectedAccepted: false);
            VerifySettled(history.State.Business);
            Assert.Equal("b", history.State.Business.OpenDecisionActorId);
            Assert.False(history.State.Business.Character("b").LastOperationReceipt!.Accepted);
            Assert.Equal("repeat-accept", history.State.Business.Character("b").LastOperationReceipt!.Operation.OperationRecordId);
            Assert.All(history.State.Business.Characters, actor => Assert.Empty(actor.Interaction.Entries));
        }

        using var final = TownDurableHistory.Open(files.Store);
        VerifySettled(final.State.Business);
        Assert.False(final.State.Business.Character("b").LastOperationReceipt!.Accepted);
        Assert.Equal("repeat-accept", final.State.Business.Character("b").LastOperationReceipt!.Operation.OperationRecordId);
    }

    [Fact]
    public async Task Fork_before_acceptance_excludes_parent_trade_and_messages_and_child_acceptance_does_not_move_parent_head() {
        using var files = new Files();
        var start = TownGameFactory.Create(4);
        TownDecisionBoundary source;
        KernelCursor sourceCursor;
        string offerId, sourceSnapshot, parentHead, parentSnapshot;
        using (var history = TownDurableHistory.Create(files.Store, start.World, start.Cursor)) {
            offerId = await OpenRecipientDecisionAfterOffer(history);
            source = history.CaptureDecisionBoundary("b");
            sourceCursor = history.Cursor;
            sourceSnapshot = Snapshot(history.State.Business);
            await ExecuteOperation(history, "b", $"accept {offerId}", "parent-accept");
            await ExecuteOperation(history, "a", "say b parent-only trade future", "parent-message");
            VerifySettled(history.State.Business);
            Assert.Contains(history.State.Business.Character("b").Interaction.Entries, entry => entry.Text == "parent-only trade future");
            parentHead = TownDurableHistory.FormatCommitId(history.HeadCommitId);
            parentSnapshot = Snapshot(history.State.Business);
        }

        TownBranchInfo child = TownDurableHistory.Fork(files.Store, source.BranchId, source.StateCommitId, "before-trade-acceptance");
        Assert.Equal(source.StateCommitId, child.ForkedFromCommitId);
        string childSnapshot, childHead;
        using (var history = TownDurableHistory.Open(files.Store, child.Id)) {
            Assert.Equal(source.StateCommitId, history.CompletedStateCommitId);
            Assert.Equal(sourceCursor, history.Cursor);
            Assert.Equal(sourceSnapshot, Snapshot(history.State.Business));
            VerifyUnsettled(history.State.Business);
            Assert.Equal(offerId, Assert.Single(history.State.Business.Trades.Offers).Id);
            Assert.Equal(TownTradeStatus.Active, Assert.Single(history.State.Business.Trades.Offers).Status);
            Assert.All(history.State.Business.Characters, actor => Assert.Empty(actor.Interaction.Entries));
            Assert.DoesNotContain("parent-only trade future", TownTextRenderer.Render(TownObservation.Capture(history.State, "b")));

            await ExecuteOperation(history, "b", "say a child-only alternative", "child-message");
            VerifyUnsettled(history.State.Business);
            // Parent acceptance must not consume the child's exact source offer or its assets.
            await ExecuteOperation(history, "b", $"accept {offerId}", "child-accept");
            VerifySettled(history.State.Business);
            Assert.Equal("child-accept", history.State.Business.Character("b").LastOperationReceipt!.Operation.OperationRecordId);
            Assert.Contains(history.State.Business.Character("a").Interaction.Entries, entry => entry.Text == "child-only alternative");
            Assert.DoesNotContain(history.State.Business.Characters.SelectMany(actor => actor.Interaction.Entries),
                entry => entry.Text == "parent-only trade future");
            childHead = TownDurableHistory.FormatCommitId(history.HeadCommitId);
            childSnapshot = Snapshot(history.State.Business);
        }

        using (var parent = TownDurableHistory.Open(files.Store, source.BranchId)) {
            Assert.Equal(parentHead, TownDurableHistory.FormatCommitId(parent.HeadCommitId));
            Assert.Equal(parentSnapshot, Snapshot(parent.State.Business));
            VerifySettled(parent.State.Business);
            Assert.DoesNotContain(parent.State.Business.Characters.SelectMany(actor => actor.Interaction.Entries),
                entry => entry.Text == "child-only alternative");
        }
        using var coldChild = TownDurableHistory.Open(files.Store, child.Id);
        Assert.Equal(childHead, TownDurableHistory.FormatCommitId(coldChild.HeadCommitId));
        Assert.Equal(childSnapshot, Snapshot(coldChild.State.Business));
        VerifySettled(coldChild.State.Business);
    }

    private static async Task<string> OpenRecipientDecisionAfterOffer(TownDurableHistory history) {
        var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
            Assert.Equal("a", observation.ActorId);
            return ValueTask.FromResult(new TownControllerOperation("offer b meal 1 money 5 4", "offer-tool", "offer-operation"));
        });
        for (int step = 0; step < 10 && history.State.Business.OpenDecisionActorId != "b"; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(history.State.Business.EndTime));
        }
        Assert.Equal("b", history.State.Business.OpenDecisionActorId);
        Assert.True(history.State.Business.Character("a").LastOperationReceipt!.Accepted);
        var offer = Assert.Single(history.State.Business.Trades.Offers);
        Assert.Equal("a", offer.ProposerId);
        Assert.Equal("b", offer.RecipientId);
        Assert.Equal(TownTradeStatus.Active, offer.Status);
        VerifyUnsettled(history.State.Business);
        return offer.Id;
    }

    private static async Task ExecuteOperation(TownDurableHistory history, string actor, string command, string operationId,
        bool expectedAccepted = true) {
        bool performed = false;
        int incidental = 0;
        var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
            if (observation.ActorId == actor) {
                performed = true;
                return ValueTask.FromResult(new TownControllerOperation(command, operationId + "-tool", operationId));
            }
            string id = $"{operationId}-wait-{++incidental}";
            return ValueTask.FromResult(new TownControllerOperation("wait 4", id + "-tool", id));
        });
        for (int step = 0; step < 100 && !performed; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(history.State.Business.EndTime));
        }
        Assert.True(performed, $"Resident {actor} did not receive a decision for {command}.");
        TownFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(actor, fact.ActorId);
        Assert.True(fact.Accepted == expectedAccepted, fact.Result);
    }

    private static void VerifyUnsettled(TownBusinessState state) {
        Assert.All(state.Characters, actor => { Assert.Equal(18, actor.Money); Assert.Equal(1, actor.Inventory.Count("meal")); });
        state.Economy.Verify(state);
    }

    private static void VerifySettled(TownBusinessState state) {
        Assert.Equal(23, state.Character("a").Money);
        Assert.Equal(13, state.Character("b").Money);
        Assert.Equal(18, state.Character("c").Money);
        Assert.Equal(0, state.Character("a").Inventory.Count("meal"));
        Assert.Equal(2, state.Character("b").Inventory.Count("meal"));
        Assert.Equal(1, state.Character("c").Inventory.Count("meal"));
        Assert.Equal(54, state.Characters.Sum(actor => actor.Money));
        Assert.Equal(3, state.Characters.Sum(actor => actor.Inventory.Count("meal")));
        Assert.Equal(0, state.Economy.ImportExpense);
        Assert.Equal(0, state.Economy.ExportIncome);
        state.Economy.Verify(state);
    }

    private static string Snapshot(TownBusinessState state) => JsonSerializer.Serialize(new {
        state.Time, state.Generation, state.OpenDecisionActorId, state.LastServedActorId,
        Characters = state.Characters.OrderBy(actor => actor.Id).Select(actor => new {
            actor.Id, actor.Money, actor.FoodReserve, actor.DecisionSequence, actor.OperationSequence,
            Items = TownContent.Items.Select(item => new { Item = item, Count = actor.Inventory.Count(item) }),
            actor.Interaction, actor.LastOperationReceipt
        }),
        Trades = state.Trades.Offers.OrderBy(offer => offer.Id)
    });

    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "microtown-trade-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}

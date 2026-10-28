using DramaBoard.Kernel.Simulation;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CommittedOperationRecoveryTests {
    [Fact]
    public async Task Repository_created_from_completed_snapshot_has_no_receipts_before_its_root() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreatePlayable(characters: [new("a", "Alice")]);
        var planned = CycleEngine.CreateKernel(Memory(initial.World), characterController: new FixedController());
        await Step(planned);
        using var history = CycleDurableHistory.Create(store.StorePath, planned.World, planned.Cursor);
        Assert.Empty(history.ReadCommittedOperations(["record-1"]));
        Assert.Equal(planned.Cursor, history.Cursor);
    }

    [Fact]
    public async Task Pending_effect_is_not_a_completed_receipt_until_recovered_without_controller() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreatePlayable(characters: [new("a", "Alice")]);
        var controller = new FixedController();
        var planned = CycleEngine.CreateKernel(Memory(initial.World), characterController: controller);
        await Step(planned);
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, InitialCursor(initial.World))) {
            history.CommitEvent(planned.LastCompletion!.Event);
        }
        using (var reopened = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Empty(reopened.ReadCommittedOperations(["record-1", "unexecuted"]));
            var recovering = CycleEngine.CreateKernel(reopened);
            Assert.True(recovering.RecoverPending());
            CycleCommittedOperation receipt = Assert.Single(reopened.ReadCommittedOperations(["record-1", "unexecuted"]));
            Assert.Equal("provider-call-id", receipt.ToolCallId);
            Assert.Equal("a", receipt.ActorId);
            Assert.Equal(1, receipt.TransitionCount);
            Assert.Equal(planned.LastCompletion!.Event.CauseKey.ToString(), receipt.DomainEvent);
            Assert.Equal(planned.LastCompletion.Event.Facts[0].Result, receipt.Result);
            Assert.False(recovering.RecoverPending());
        }
        using var completed = CycleDurableHistory.Open(store.StorePath);
        Assert.Single(completed.ReadCommittedOperations(["record-1"]));
        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task Cold_lookup_crosses_pages_and_scopes_repeated_provider_ids_by_record_reference() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreatePlayable(characters: [new("a", "Alice")]);
        var controller = new FixedController();
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = CycleEngine.CreateKernel(history, characterController: controller);
            while (controller.Calls < 40) { await Step(kernel); }
        }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        var cursor = reopened.Cursor;
        string state = reopened.State.DescribeState();
        var receipts = reopened.ReadCommittedOperations(["record-1", "record-40", "never-received"]);
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt => Assert.Equal("provider-call-id", receipt.ToolCallId));
        Assert.Equal(1, receipts.Single(receipt => receipt.OperationRecordId == "record-1").TransitionCount);
        Assert.Equal(cursor.Version.TransitionCount,
            receipts.Single(receipt => receipt.OperationRecordId == "record-40").TransitionCount);
        Assert.Equal(cursor, reopened.Cursor);
        Assert.Equal(state, reopened.State.DescribeState());
    }

    private sealed class FixedController : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(new CycleControllerOperation("provider-call-id", CycleCommand.Wait(1), $"record-{Calls}"));
        }
    }
}

using DramaBoard.Kernel.Simulation;
using DramaBoard.Playground;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class CommittedOperationRecoveryTests {
    [Fact]
    public async Task Repository_created_from_completed_snapshot_has_no_receipts_before_its_root() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreatePlayable(characters: [new("a", "Alice")]);
        var planned = PlaygroundEngine.CreateKernel(Memory(initial.World), characterController: new FixedController());
        await StepWorldEvent(planned);
        using var history = PlaygroundDurableHistory.Create(store.StorePath, planned.World, planned.Cursor);
        Assert.Empty(history.ReadCommittedOperations(["record-1"]));
        Assert.Equal(planned.Cursor, history.Cursor);
    }

    [Fact]
    public async Task Pending_effect_is_not_a_completed_receipt_until_recovered_without_controller() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreatePlayable(characters: [new("a", "Alice")]);
        var controller = new FixedController();
        var planned = PlaygroundEngine.CreateKernel(Memory(initial.World), characterController: controller);
        await Step(planned);
        PlaygroundWorld opened = planned.World;
        var openedCursor = planned.Cursor;
        await Step(planned);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, opened, openedCursor)) {
            history.CommitEvent(planned.LastCompletion!.Event);
        }
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Empty(reopened.ReadCommittedOperations(["record-1", "unexecuted"]));
            var recovering = PlaygroundEngine.CreateKernel(reopened);
            Assert.True(recovering.RecoverPending());
            PlaygroundCommittedOperation receipt = Assert.Single(reopened.ReadCommittedOperations(["record-1", "unexecuted"]));
            Assert.Equal("provider-call-id", receipt.ToolCallId);
            Assert.Equal("a", receipt.ActorId);
            Assert.Equal(2, receipt.TransitionCount);
            Assert.Equal(planned.LastCompletion!.Event.CauseKey.ToString(), receipt.DomainEvent);
            Assert.Equal(PlaygroundOperationResults.Describe(planned.LastCompletion.Event.Facts[0]), receipt.Result);
            Assert.False(recovering.RecoverPending());
        }
        using var completed = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Single(completed.ReadCommittedOperations(["record-1"]));
        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task Cold_lookup_crosses_pages_and_scopes_repeated_provider_ids_by_record_reference() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreatePlayable(characters: [new("a", "Alice")]);
        var controller = new FixedController();
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
            while (controller.Calls < 40) { await StepWorldEvent(kernel); }
        }
        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        var cursor = reopened.Cursor;
        string state = reopened.State.DescribeState();
        var receipts = reopened.ReadCommittedOperations(["record-1", "record-40", "never-received"]);
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt => Assert.Equal("provider-call-id", receipt.ToolCallId));
        Assert.Equal(2, receipts.Single(receipt => receipt.OperationRecordId == "record-1").TransitionCount);
        Assert.Equal(cursor.Version.TransitionCount,
            receipts.Single(receipt => receipt.OperationRecordId == "record-40").TransitionCount);
        Assert.Equal(cursor, reopened.Cursor);
        Assert.Equal(state, reopened.State.DescribeState());
    }

    private sealed class FixedController : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(new PlaygroundControllerOperation("provider-call-id", "wait 1", $"record-{Calls}"));
        }
    }
}

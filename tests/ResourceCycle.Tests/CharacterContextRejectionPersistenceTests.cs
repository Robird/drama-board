using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;
using static DramaBoard.ResourceCycle.Tests.CharacterContextPersistenceTests;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CharacterContextRejectionPersistenceTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fourth_rejection_cold_recovery_preserves_pruned_window_and_audit_disposition(bool eventOnly) {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var expected = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor), characterController: Inputs());
        for (int step = 0; step < 5; step++) { await Advance(expected); }
        var expectedState = expected.World.Business.InteractionStates["a"];
        Assert.Equal(new long?[] { 2, 3, 4 }, expectedState.CurrentContext!.Items.Select(item => item.OperationSequence));

        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var source = Inputs();
            IOccurrenceHistory<CycleWorld, CycleFact> writer = eventOnly ? new StopAfterFourthRejection(history) : history;
            var kernel = CycleEngine.CreateKernel(writer, characterController: source);
            for (int step = 0; step < 4; step++) { await Advance(kernel); }
            var before = kernel.World;
            var cursor = kernel.Cursor;
            if (eventOnly) {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(kernel));
                Assert.IsType<InjectedCrash>(error.InnerException);
                Assert.True(kernel.IsFaulted);
                Assert.Same(before, history.State);
                Assert.Equal(cursor, history.Cursor);
                Assert.NotNull(history.PendingEvent);
                Assert.DoesNotContain(history.ReadCommittedOperations(["raw/4"]), _ => true);
            } else {
                await Advance(kernel);
                Assert.Null(history.PendingEvent);
            }
            Assert.Equal(4, source.Calls);
        }

        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            var controller = new ForbiddenController();
            var kernel = CycleEngine.CreateKernel(history, characterController: controller);
            CycleFact? pending = history.PendingEvent?.Facts.Single();
            Assert.Equal(eventOnly, kernel.RecoverPending());
            Assert.Equal(0, controller.Calls);
            Assert.Null(history.PendingEvent);
            Assert.Equal(expected.Cursor, history.Cursor);
            AssertSemanticEqual(expectedState, kernel.World.Business.InteractionStates["a"]);
            if (pending is not null) {
                Assert.Same(Assert.Single(pending.InteractionChanges), kernel.World.Business.InteractionStates["a"]);
            }
            var receipts = history.ReadCommittedOperations(["raw/1", "raw/2", "raw/3", "raw/4", "never-received"]);
            Assert.Equal(4, receipts.Count);
            Assert.All(receipts, receipt => Assert.Equal(CycleOperationDisposition.Rejected, receipt.Disposition));
            for (int index = 1; index <= 4; index++) {
                var receipt = Assert.Single(receipts, value => value.OperationRecordId == $"raw/{index}");
                Assert.Equal($"provider/{index}", receipt.ToolCallId);
                Assert.Equal("a", receipt.ActorId);
            }
            Assert.Equal(CycleOperationResults.Describe(expected.LastCompletion!.Event.Facts.Single()),
                receipts.Single(receipt => receipt.OperationRecordId == "raw/4").Result);
            // Reading the complete audit cannot put aged-out rejection 1 back into current material.
            AssertSemanticEqual(expectedState, kernel.World.Business.InteractionStates["a"]);
        }

        CharacterInteractionState afterSupply;
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            AssertSemanticEqual(expectedState, history.State.Business.InteractionStates["a"]);
            var kernel = CycleEngine.CreateKernel(history, characterController: new SupplyController());
            await Advance(kernel);
            afterSupply = kernel.World.Business.InteractionStates["a"];
            var context = afterSupply.CurrentContext!;
            Assert.Equal(1, context.ContextSequence);
            Assert.Equal(5, context.LastOperationSequence);
            Assert.Equal(new long?[] { 3, 4, 5 }, context.Items.Select(item => item.OperationSequence));
            Assert.Equal(new long[] { 3, 4, 5 }, context.Items.Select(item => item.ItemSequence));
            Assert.Equal(6, context.NextItemSequence);
            Assert.Equal("provider/5", context.Items[^1].ToolCallId);
            Assert.Equal("  use   item.water  ", context.Items[^1].RawInput);
            Assert.False(context.Items[^1].IsRejected);
            var receipts = history.ReadCommittedOperations(["raw/1", "raw/2", "raw/5"]);
            Assert.Equal(3, receipts.Count);
            Assert.Equal(CycleOperationDisposition.Succeeded, receipts.Single(value => value.OperationRecordId == "raw/5").Disposition);
            Assert.Equal(2, receipts.Count(value => value.Disposition == CycleOperationDisposition.Rejected));
            Assert.DoesNotContain(context.Items, item => item.OperationSequence is 1 or 2);
        }
        using var final = CycleDurableHistory.Open(store.StorePath);
        AssertSemanticEqual(afterSupply, final.State.Business.InteractionStates["a"]);
        Assert.Null(final.PendingEvent);
    }

    [Fact]
    public async Task Pruning_does_not_refund_same_time_budget_and_cold_open_cannot_accept_a_release_after_exhaustion() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var source = new RepeatingController("invalid");
        var planned = CycleEngine.CreateKernel(Memory(initial.World), characterController: source);
        for (int index = 0; index < 1023; index++) { await Advance(planned); }
        Assert.Equal(1022, source.Calls); // First transition freezes the opening.
        Assert.Equal(1022, planned.Cursor.LastInstant!.Value.CausalOrdinal);
        Assert.Equal(3, planned.World.Business.InteractionStates["a"].CurrentContext!.Items.Count);
        using var store = new TemporaryStore();
        KernelCursor savedCursor;
        CharacterInteractionState saved;
        using (var history = CycleDurableHistory.Create(store.StorePath, planned.World, planned.Cursor)) {
            var finalSource = new RepeatingController("invalid", initialCalls: 1022);
            var kernel = CycleEngine.CreateKernel(history, characterController: finalSource);
            await Advance(kernel);
            savedCursor = kernel.Cursor;
            saved = kernel.World.Business.InteractionStates["a"];
            Assert.Equal(1023, savedCursor.LastInstant!.Value.CausalOrdinal);
            Assert.Equal(1023, saved.CurrentContext!.LastOperationSequence);
            Assert.Equal(new long?[] { 1021, 1022, 1023 }, saved.CurrentContext.Items.Select(item => item.OperationSequence));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(kernel));
            Assert.Contains("Transition budget of 1024 exhausted", error.Message);
            Assert.Equal(1023, finalSource.Calls);
            Assert.Equal(savedCursor, history.Cursor);
            Assert.Same(saved, history.State.Business.InteractionStates["a"]);
            Assert.Null(history.PendingEvent);
        }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        var release = new RepeatingController("wait 1");
        var recovered = CycleEngine.CreateKernel(reopened, characterController: release);
        Assert.False(recovered.RecoverPending());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(recovered));
        Assert.Contains("Transition budget of 1024 exhausted", failure.Message);
        Assert.Equal(0, release.Calls);
        Assert.Equal(savedCursor, recovered.Cursor);
        Assert.Equal(initial.World.Time, recovered.World.Time);
        AssertSemanticEqual(saved, recovered.World.Business.InteractionStates["a"]);
        Assert.Null(reopened.PendingEvent);
    }

    private static CharacterContextRejectionTests.RawController Inputs() => new("bad-1", "bad-2", "bad-3", "bad-4");
    private sealed class SupplyController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation("provider/5", "  use   item.water  ", "raw/5"));
    }
    private sealed class RepeatingController(string raw, int initialCalls = 0) : ICycleCharacterController {
        public int Calls { get; private set; } = initialCalls;
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(new CycleControllerOperation($"budget/{Calls}", raw, $"budget-operation/{Calls}"));
        }
    }
    private sealed class ForbiddenController : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            throw new InvalidOperationException("Recovery must not request another command.");
        }
    }
    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterFourthRejection(IOccurrenceHistory<CycleWorld, CycleFact> inner) : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.OperationRecordId == "raw/4")) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
}

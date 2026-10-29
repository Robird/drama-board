using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class OperationRecoveryIntegrationTests {
    [Fact]
    public async Task Clamped_supply_result_missing_after_S_recovers_same_actual_gain_and_operation_identity() {
        string directory = Path.Combine(Path.GetTempPath(), "drama-clamped-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string store = Path.Combine(directory, "game");
            string journalPath = Path.Combine(directory, "operations.jsonl");
            var initial = CycleGameFactory.CreateSurvival();
            CycleCharacter actor = initial.World.GetCharacter("a");
            var replacement = new CycleCharacter(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack,
                survival: CycleSurvival.Create(initial.World.Time, water: 90));
            CycleWorld world = CycleEngine.Rebuild(initial.World.Business.Replace(initial.World.Time,
                changedCharacters: [replacement]), initial.World.Time);
            CycleOperationReceipt receipt;
            using (var journal = CycleOperationJournal.Open(journalPath)) {
                receipt = journal.Receive("batch", "provider:clamp", "a", 1, "use", "use item.water");
            }
            var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(world, initial.Cursor);
            var kernel = CycleEngine.CreateKernel(memory, characterController: new SupplyController(receipt));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            CycleWorld openedWorld = kernel.World;
            KernelCursor openedCursor = kernel.Cursor;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            string normalResult = CycleOperationResults.Describe(kernel.LastCompletion!.Event.Facts.Single());
            Assert.Contains("+10", normalResult);
            using (var history = CycleDurableHistory.Create(store, openedWorld, openedCursor)) {
                history.CommitEvent(kernel.LastCompletion.Event);
                history.CommitState(kernel.World, kernel.Cursor);
            }
            using var reopened = CycleDurableHistory.Open(store);
            using var operations = CycleOperationJournal.Open(journalPath);
            operations.Reconcile(reopened.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new CycleOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent)));
            CycleOperationResult result = operations.Entries.Single().Completed!;
            Assert.Equal(normalResult, result.Result);
            Assert.Equal(receipt.ToolCallId, result.ToolCallId);
            Assert.Equal(receipt.OperationRecordId, operations.Entries.Single().Received.OperationRecordId);
            Assert.False(result.ExactResult);
            Assert.Equal(100 * CycleSurvival.Scale, reopened.State.GetCharacter("a").Survival!.WaterUnits);
            Assert.Equal(0, reopened.State.GetCharacter("a").Backpack.Count("item.water"));
            Assert.Equal(2, reopened.Cursor.Version.TransitionCount);
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_host_results_reconcile_from_real_E_S_without_reexecuting_input(bool completeState) {
        string directory = Path.Combine(Path.GetTempPath(), "drama-receipt-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string store = Path.Combine(directory, "game");
            string journalPath = Path.Combine(directory, "operations.jsonl");
            var initial = CycleGameFactory.CreateSurvival();
            CycleOperationReceipt receipt;
            using (var journal = CycleOperationJournal.Open(journalPath)) {
                receipt = journal.Receive("provider-batch", "opaque/provider:42", "a", 1, "wait", "wait 1");
                journal.Receive("provider-batch", "query-call", "a", 1, ":look", ":look", "lost-query");
            }
            var controller = new CapturedController(receipt);
            var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
            var planned = CycleEngine.CreateKernel(memory, characterController: controller);
            Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
            CycleWorld openedWorld = planned.World;
            KernelCursor openedCursor = planned.Cursor;
            Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
            using (var history = CycleDurableHistory.Create(store, openedWorld, openedCursor)) {
                history.CommitEvent(planned.LastCompletion!.Event);
                if (completeState) { history.CommitState(planned.World, planned.Cursor); }
            }
            using (var reopened = CycleDurableHistory.Open(store))
            using (var journal = CycleOperationJournal.Open(journalPath)) {
                var kernel = CycleEngine.CreateKernel(reopened); // No controller is available to replay.
                Assert.Equal(!completeState, kernel.RecoverPending());
                journal.Reconcile(reopened.ReadCommittedOperations(journal.PendingOperationRecordIds)
                    .Select(commit => new CycleOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                        commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent)));
                var result = journal.Entries.Single(entry => entry.Received.OperationRecordId == receipt.OperationRecordId).Completed!;
                Assert.Equal(CycleOperationStatus.Succeeded, result.Status);
                Assert.Equal(receipt.ToolCallId, result.ToolCallId);
                Assert.False(result.ExactResult);
                Assert.Equal("1:2", result.WorldVersion);
                Assert.Equal(CycleOperationStatus.Interrupted,
                    journal.Entries.Single(entry => entry.Received.OperationRecordId == "lost-query").Completed!.Status);
                Assert.Equal(2, kernel.Cursor.Version.TransitionCount);
                Assert.Equal(receipt.ToolCallId, kernel.World.Business.InteractionStates["a"].PendingAdvance!.ToolCallId);
                Assert.Empty(journal.PendingOperationRecordIds);
            }
            using var final = CycleDurableHistory.Open(store);
            Assert.Null(final.PendingEvent);
            Assert.Equal(2, final.Cursor.Version.TransitionCount);
            Assert.Equal(1, controller.Calls);
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CapturedController(CycleOperationReceipt receipt) : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(new CycleControllerOperation(receipt.ToolCallId, CycleCommand.Wait(1), receipt.OperationRecordId));
        }
    }

    private sealed class SupplyController(CycleOperationReceipt receipt) : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation(receipt.ToolCallId, CycleCommand.Use("item.water"), receipt.OperationRecordId));
    }
}

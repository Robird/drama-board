using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class OperationRecoveryIntegrationTests {
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
            using (var history = CycleDurableHistory.Create(store, initial.World, initial.Cursor)) {
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
                Assert.Equal("1:1", result.WorldVersion);
                Assert.Equal(CycleOperationStatus.Interrupted,
                    journal.Entries.Single(entry => entry.Received.OperationRecordId == "lost-query").Completed!.Status);
                Assert.Equal(1, kernel.Cursor.Version.TransitionCount);
                Assert.Equal(receipt.ToolCallId, kernel.World.Characters[0].Continuity!.Pending!.Action.ToolCallId);
                Assert.Empty(journal.PendingOperationRecordIds);
            }
            using var final = CycleDurableHistory.Open(store);
            Assert.Null(final.PendingEvent);
            Assert.Equal(1, final.Cursor.Version.TransitionCount);
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
}

using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class OperationRecoveryIntegrationTests {
    [Fact]
    public async Task Clamped_supply_result_missing_after_S_recovers_same_actual_gain_and_operation_identity() {
        string directory = Path.Combine(Path.GetTempPath(), "drama-clamped-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string store = Path.Combine(directory, "game");
            string journalPath = Path.Combine(directory, "host-events.jsonl");
            var initial = PlaygroundGameFactory.CreateSurvival();
            PlaygroundCharacter actor = initial.World.GetCharacter("a");
            var replacement = new PlaygroundCharacter(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack,
                survival: PlaygroundSurvival.Create(initial.World.Time, water: 90));
            PlaygroundWorld world = PlaygroundEngine.Rebuild(initial.World.Business.Replace(initial.World.Time,
                changedCharacters: [replacement]), initial.World.Time);
            PlaygroundOperationReceipt receipt;
            using (var journalStore = PlaygroundHostJournal.Open(journalPath)) {
                var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
                receipt = journal.Receive("batch", "provider:clamp", "a", 1, "use", "use item.water");
            }
            var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(world, initial.Cursor);
            var kernel = PlaygroundEngine.CreateKernel(memory, characterController: new SupplyController(receipt));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            PlaygroundWorld openedWorld = kernel.World;
            KernelCursor openedCursor = kernel.Cursor;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            string normalResult = PlaygroundOperationResults.Describe(kernel.LastCompletion!.Event.Facts.Single());
            Assert.Contains("+10", normalResult);
            using (var history = PlaygroundDurableHistory.Create(store, openedWorld, openedCursor)) {
                history.CommitEvent(kernel.LastCompletion.Event);
                history.CommitState(kernel.World, kernel.Cursor);
            }
            using var reopened = PlaygroundDurableHistory.Open(store);
            using var operationsStore = PlaygroundHostJournal.Open(journalPath);
            var operations = new PlaygroundOperationAudit(operationsStore, "test-branch", "test-run");
            operations.Reconcile(reopened.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new PlaygroundOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
            PlaygroundOperationResult result = operations.Entries.Single().Completed!;
            Assert.Equal(normalResult, result.Result);
            Assert.Equal(receipt.ToolCallId, result.ToolCallId);
            Assert.Equal(receipt.OperationRecordId, operations.Entries.Single().Received.OperationRecordId);
            Assert.False(result.ExactResult);
            Assert.Equal(100 * PlaygroundSurvival.Scale, reopened.State.GetCharacter("a").Survival!.WaterUnits);
            Assert.Equal(0, reopened.State.GetCharacter("a").Backpack.Count("item.water"));
            Assert.Equal(2, reopened.Cursor.Version.TransitionCount);
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "wait 1", PlaygroundOperationDisposition.Succeeded)]
    [InlineData(true, "wait 1", PlaygroundOperationDisposition.Succeeded)]
    [InlineData(false, "  invalid  ", PlaygroundOperationDisposition.Rejected)]
    [InlineData(true, "  invalid  ", PlaygroundOperationDisposition.Rejected)]
    [InlineData(false, "wait 0", PlaygroundOperationDisposition.Rejected)]
    [InlineData(true, "wait 0", PlaygroundOperationDisposition.Rejected)]
    public async Task Missing_host_results_reconcile_from_real_E_S_without_reexecuting_input(bool completeState,
        string rawInput, PlaygroundOperationDisposition disposition) {
        string directory = Path.Combine(Path.GetTempPath(), "drama-receipt-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string store = Path.Combine(directory, "game");
            string journalPath = Path.Combine(directory, "host-events.jsonl");
            var initial = PlaygroundGameFactory.CreateSurvival();
            PlaygroundOperationReceipt receipt;
            using (var journalStore = PlaygroundHostJournal.Open(journalPath)) {
                var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
                receipt = journal.Receive("provider-batch", "opaque/provider:42", "a", 1, "command", rawInput);
                journal.Receive("provider-batch", "query-call", "a", 1, ":look", ":look", "lost-query");
            }
            var controller = new CapturedController(receipt);
            var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
            var planned = PlaygroundEngine.CreateKernel(memory, characterController: controller);
            Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
            PlaygroundWorld openedWorld = planned.World;
            KernelCursor openedCursor = planned.Cursor;
            Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
            using var normalJournalStore = PlaygroundHostJournal.CreateInMemory();
            var normalJournal = new PlaygroundOperationAudit(normalJournalStore, "test-branch", "test-run");
            normalJournal.Receive(receipt.BatchId, receipt.ToolCallId, receipt.ActorId, receipt.ContextSequence,
                receipt.ToolName, receipt.RawInput, receipt.OperationRecordId);
            PlayConsole.CompleteOperations(normalJournal, planned.LastCompletion!);
            PlaygroundOperationResult normalResult = Assert.Single(normalJournal.Entries).Completed!;
            using (var history = PlaygroundDurableHistory.Create(store, openedWorld, openedCursor)) {
                history.CommitEvent(planned.LastCompletion!.Event);
                if (completeState) { history.CommitState(planned.World, planned.Cursor); }
            }
            using (var reopened = PlaygroundDurableHistory.Open(store))
            using (var journalStore = PlaygroundHostJournal.Open(journalPath)) {
                var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
                var kernel = PlaygroundEngine.CreateKernel(reopened); // No controller is available to replay.
                Assert.Equal(!completeState, kernel.RecoverPending());
                journal.Reconcile(reopened.ReadCommittedOperations(journal.PendingOperationRecordIds)
                    .Select(commit => new PlaygroundOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                        commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
                var result = journal.Entries.Single(entry => entry.Received.OperationRecordId == receipt.OperationRecordId).Completed!;
                Assert.Equal(PlaygroundOperationCommit.StatusOf(disposition), result.Status);
                Assert.Equal(normalResult.Status, result.Status);
                Assert.Equal(normalResult.Result, result.Result);
                Assert.Equal(normalResult.DomainEvent, result.DomainEvent);
                Assert.Equal(normalResult.WorldVersion, result.WorldVersion);
                Assert.Equal(receipt.ToolCallId, result.ToolCallId);
                Assert.False(result.ExactResult);
                Assert.Equal("1:2", result.WorldVersion);
                Assert.Equal(PlaygroundOperationStatus.Interrupted,
                    journal.Entries.Single(entry => entry.Received.OperationRecordId == "lost-query").Completed!.Status);
                Assert.Equal(2, kernel.Cursor.Version.TransitionCount);
                var interaction = kernel.World.Business.InteractionStates["a"];
                if (disposition == PlaygroundOperationDisposition.Succeeded) {
                    Assert.Equal(receipt.ToolCallId, interaction.PendingAdvance!.ToolCallId);
                    Assert.Equal(rawInput, interaction.PendingAdvance.RawInput);
                } else {
                    var rejected = Assert.Single(interaction.CurrentContext!.Items);
                    Assert.True(rejected.IsRejected);
                    Assert.Equal(rawInput, rejected.RawInput);
                    Assert.Equal(receipt.ToolCallId, rejected.ToolCallId);
                    Assert.Equal(result.Result, rejected.Result);
                    Assert.Equal(1L, interaction.CurrentContext.ContextSequence);
                }
                Assert.Empty(journal.PendingOperationRecordIds);
            }
            using var final = PlaygroundDurableHistory.Open(store);
            Assert.Null(final.PendingEvent);
            Assert.Equal(2, final.Cursor.Version.TransitionCount);
            Assert.Equal(1, controller.Calls);
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Repairing_a_pruned_rejection_result_does_not_restore_it_to_the_live_context() {
        string directory = Path.Combine(Path.GetTempPath(), "drama-pruned-rejection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string store = Path.Combine(directory, "game");
            string journalPath = Path.Combine(directory, "host-events.jsonl");
            var initial = PlaygroundGameFactory.CreateSurvival();
            string rejectionRecord;
            string rejectionCall;
            string rejectionResult;
            using (var history = PlaygroundDurableHistory.Create(store, initial.World, initial.Cursor))
            using (var journalStore = PlaygroundHostJournal.Open(journalPath))
            using (var input = new StringReader("invalid\nhandoff 一\nhandoff 二\nhandoff 三\n"))
            using (var output = new StringWriter()) {
                var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
                var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true),
                    ConsoleMetaCommands.HumanDefault, input, output, operations: journal);
                var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
                var rejected = Assert.Single(journal.Entries);
                rejectionRecord = rejected.Received.OperationRecordId;
                rejectionCall = rejected.Received.ToolCallId;
                rejectionResult = PlaygroundOperationResults.Describe(kernel.LastCompletion!.Event.Facts.Single());
                // Deliberately lose only this host result. Later successful operations prune the Item.
                for (int i = 0; i < 3; i++) {
                    Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
                    PlayConsole.CompleteOperations(journal, kernel.LastCompletion!);
                }
                Assert.DoesNotContain(kernel.World.Business.InteractionStates["a"].CurrentContext!.Items,
                    item => item.OperationRecordId == rejectionRecord);
            }
            using (var reopened = PlaygroundDurableHistory.Open(store))
            using (var journalStore = PlaygroundHostJournal.Open(journalPath)) {
                var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
                CharacterContext context = reopened.State.Business.InteractionStates["a"].CurrentContext!;
                Assert.Equal(4L, context.LastOperationSequence);
                Assert.Equal(new long[] { 2, 3, 4 }, context.Items.Select(item => item.OperationSequence!.Value));
                journal.Reconcile(reopened.ReadCommittedOperations(journal.PendingOperationRecordIds)
                    .Select(commit => new PlaygroundOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                        commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
                PlaygroundOperationResult result = journal.Entries.Single(entry => entry.Received.OperationRecordId == rejectionRecord).Completed!;
                Assert.Equal(PlaygroundOperationStatus.Rejected, result.Status);
                Assert.Equal(rejectionCall, result.ToolCallId);
                Assert.Equal(rejectionResult, result.Result);
                Assert.Same(context, reopened.State.Business.InteractionStates["a"].CurrentContext);
                Assert.Empty(journal.PendingOperationRecordIds);
            }
            using var final = PlaygroundDurableHistory.Open(store);
            Assert.DoesNotContain(final.State.Business.InteractionStates["a"].CurrentContext!.Items,
                item => item.OperationRecordId == rejectionRecord);
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CapturedController(PlaygroundOperationReceipt receipt) : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(new PlaygroundControllerOperation(receipt.ToolCallId, receipt.RawInput, receipt.OperationRecordId));
        }
    }

    private sealed class SupplyController(PlaygroundOperationReceipt receipt) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation(receipt.ToolCallId, receipt.RawInput, receipt.OperationRecordId));
    }
}

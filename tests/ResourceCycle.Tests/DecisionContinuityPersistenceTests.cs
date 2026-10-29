using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class DecisionContinuityPersistenceTests {
    [Fact]
    public async Task State_only_cold_open_preserves_incoming_after_supply_and_new_draft_without_reopening_context() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        string saved;
        KernelCursor cursor;
        string incomingSnapshot;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, InitialCursor(initial.World))) {
            var source = Commands(CycleCommand.Handoff("回来喝水"), CycleCommand.Wait(1),
                CycleCommand.Use("item.water"), CycleCommand.Handoff("下一步检查路径"));
            var kernel = CycleEngine.CreateKernel(history, characterController: source);
            for (int count = 0; kernel.World.Business.InteractionStates["a"].CurrentContext?.Opening.PreviousAdvance is null; count++) {
                Assert.True(count < 20);
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
            }
            CharacterContextOpening incoming = kernel.World.Business.InteractionStates["a"].CurrentContext!.Opening;
            incomingSnapshot = MaterialSnapshot(incoming);
            Assert.Equal("call_fixture_2", incoming.PreviousAdvance!.ToolCallId);
            Assert.Equal("fixture/2", incoming.PreviousAdvance!.OperationRecordId);
            Assert.Equal("回来喝水", incoming.PreviousAdvance!.Handoff);
            Assert.Equal(2, kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
            Assert.Equal(2, source.Calls);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
            Assert.Same(incoming, kernel.World.Business.InteractionStates["a"].CurrentContext!.Opening);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
            Assert.Same(incoming, kernel.World.Business.InteractionStates["a"].CurrentContext!.Opening);
            Assert.Equal("下一步检查路径", kernel.World.Business.InteractionStates["a"].DraftHandoff);
            Assert.Equal(4, source.Calls);
            Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
            saved = kernel.World.DescribeState();
            cursor = kernel.Cursor;
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(saved, reopened.State.DescribeState());
        Assert.Equal(cursor, reopened.Cursor);
        CharacterInteractionState continuity = reopened.State.Business.InteractionStates["a"];
        Assert.Equal(2, continuity.CurrentContext!.ContextSequence);
        Assert.Null(continuity.PendingAdvance);
        Assert.Equal(incomingSnapshot, MaterialSnapshot(continuity.CurrentContext!.Opening));
        Assert.Equal("下一步检查路径", continuity.DraftHandoff);
        var resumed = CycleEngine.CreateKernel(reopened, characterController: Commands(CycleCommand.Handoff(null)));
        Assert.False(resumed.RecoverPending());
        Assert.Equal(StepStatus.Committed, await resumed.StepAsync(Hour(240)));
        Assert.Equal(CycleEventKind.HandoffEdited, Assert.Single(resumed.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(2, resumed.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
        Assert.Equal(incomingSnapshot, MaterialSnapshot(resumed.World.Business.InteractionStates["a"].CurrentContext!.Opening));
        Assert.Null(resumed.World.Business.InteractionStates["a"].DraftHandoff);
    }

    [Theory]
    [InlineData(CycleEventKind.HandoffEdited)]
    [InlineData(CycleEventKind.WaitStarted)]
    [InlineData(CycleEventKind.DecisionContextOpened)]
    [InlineData(CycleEventKind.SurvivalFailed)]
    public async Task Pending_event_restores_continuity_and_original_operation_identity_without_controller(CycleEventKind boundary) {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(enableWeather: false);
        var expected = CycleEngine.CreateKernel(Memory(initial.World), characterController: new ContinuityScript());
        for (int count = 0; ; count++) {
            Assert.True(count < 100, "The requested continuity boundary was not reached.");
            Assert.Equal(StepStatus.Committed, await expected.StepAsync(Hour(240)));
            if (Assert.Single(expected.LastCompletion!.Event.Facts).Kind == boundary &&
                (boundary != CycleEventKind.DecisionContextOpened || expected.World.Business.InteractionStates["a"].CurrentContext!.Opening.PreviousAdvance is not null)) { break; }
        }

        string before;
        KernelCursor cursor;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, InitialCursor(initial.World))) {
            var interrupted = CycleEngine.CreateKernel(new StopAfterEvent(history, boundary), characterController: new ContinuityScript());
            for (int count = 0; ; count++) {
                Assert.True(count < 100);
                before = history.State.DescribeState();
                cursor = history.Cursor;
                try { Assert.Equal(StepStatus.Committed, await interrupted.StepAsync(Hour(240))); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { break; }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(before, history.State.DescribeState());
            Assert.Equal(Describe(expected.LastCompletion!.Event), Describe(history.PendingEvent!));
        }

        using (var reopened = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(before, reopened.State.DescribeState());
            Assert.Equal(cursor, reopened.Cursor);
            long previousContextSequence = reopened.State.Business.InteractionStates["a"].CurrentContext?.ContextSequence ?? 0;
            CycleFact fact = Assert.Single(reopened.PendingEvent!.Facts);
            if (boundary is CycleEventKind.HandoffEdited or CycleEventKind.WaitStarted) {
                int ordinal = boundary == CycleEventKind.HandoffEdited ? 1 : 2;
                Assert.Equal($"endpoint.original/{ordinal}", fact.ToolCallId);
                Assert.Equal($"batch-1/operation-{ordinal}", fact.OperationRecordId);
            } else {
                Assert.Null(fact.ToolCallId);
                Assert.Null(fact.OperationRecordId);
            }
            var recovered = CycleEngine.CreateKernel(reopened, characterController: new ForbiddenController());
            Assert.True(recovered.RecoverPending());
            Assert.Null(reopened.PendingEvent);
            Assert.Equal(expected.World.DescribeState(), recovered.World.DescribeState());
            Assert.Equal(MaterialSnapshot(expected.World.Business.InteractionStates), MaterialSnapshot(recovered.World.Business.InteractionStates));
            Assert.Equal(expected.Cursor, recovered.Cursor);
            Assert.Equal(Describe(expected.LastCompletion!.Event), Describe(recovered.LastCompletion!.Event));
            CharacterInteractionState continuity = recovered.World.Business.InteractionStates["a"];
            switch (boundary) {
                case CycleEventKind.HandoffEdited:
                    Assert.Equal(1, continuity.CurrentContext!.ContextSequence);
                    Assert.Equal("观察等待后的身体变化", continuity.DraftHandoff);
                    Assert.Null(continuity.PendingAdvance);
                    break;
                case CycleEventKind.WaitStarted:
                    Assert.Equal(1, continuity.CurrentContext!.ContextSequence);
                    Assert.Null(continuity.DraftHandoff);
                    Assert.Equal("endpoint.original/2", continuity.PendingAdvance!.ToolCallId);
                    Assert.Equal("batch-1/operation-2", continuity.PendingAdvance.OperationRecordId);
                    Assert.Equal("观察等待后的身体变化", continuity.PendingAdvance.Handoff);
                    break;
                case CycleEventKind.DecisionContextOpened:
                    Assert.Equal(2, continuity.CurrentContext!.ContextSequence);
                    Assert.Null(continuity.PendingAdvance);
                    Assert.Equal("endpoint.original/2", continuity.CurrentContext!.Opening.PreviousAdvance!.ToolCallId);
                    Assert.Equal("batch-1/operation-2", continuity.CurrentContext!.Opening.PreviousAdvance!.OperationRecordId);
                    Assert.Equal("观察等待后的身体变化", continuity.CurrentContext!.Opening.PreviousAdvance!.Handoff);
                    break;
                case CycleEventKind.SurvivalFailed:
                    Assert.Null(continuity.PendingAdvance);
                    Assert.Equal(CharacterContextStatus.Terminal, continuity.CurrentContext!.Status);
                    Assert.Contains(continuity.CurrentContext.Opening.Experiences, e => e.Event.Kind == CycleObservedEventKind.SurvivalFailed);
                    Assert.Equal(previousContextSequence + 1, continuity.CurrentContext.ContextSequence);
                    Assert.Equal(CycleExpeditionOutcome.Dead, recovered.World.GetCharacter("a").Ending!.Outcome);
                    Assert.Equal(StepStatus.Exhausted, await recovered.StepAsync(Hour(240)));
                    break;
            }
            Assert.Equal(CycleEngine.Rebuild(recovered.World.Business, recovered.World.Time).Forecasts.ToString(),
                recovered.World.Forecasts.ToString());
            Assert.False(recovered.RecoverPending());
        }

        using var final = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(expected.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(expected.Cursor, final.Cursor);
    }

    private sealed class ContinuityScript : ICycleCharacterController {
        private int _calls;
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            int ordinal = ++_calls;
            CycleCommand command = ordinal switch {
                1 => CycleCommand.Handoff("观察等待后的身体变化"),
                2 => CycleCommand.Wait(24),
                _ => CycleCommand.Continue()
            };
            Assert.Null(context.Check(command));
            return ValueTask.FromResult(new CycleControllerOperation($"endpoint.original/{ordinal}", CycleCommandParser.Format(command),
                $"batch-1/operation-{ordinal}"));
        }
    }

    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending recovery must not invoke the controller.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner, CycleEventKind stopAt)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        private int _openings;
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == CycleEventKind.DecisionContextOpened)) { _openings++; }
            if (occurrence.Facts.Any(fact => fact.Kind == stopAt) &&
                (stopAt != CycleEventKind.DecisionContextOpened || _openings > 1)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

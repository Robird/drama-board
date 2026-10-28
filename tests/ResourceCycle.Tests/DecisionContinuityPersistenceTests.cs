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
            for (int count = 0; kernel.World.GetCharacter("a").Continuity!.Incoming is null; count++) {
                Assert.True(count < 20);
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
            }
            CycleActionOutcome incoming = kernel.World.GetCharacter("a").Continuity!.Incoming!;
            incomingSnapshot = incoming.ToString();
            Assert.Equal("call_fixture_2", incoming.Action.ToolCallId);
            Assert.Equal("fixture/2", incoming.Action.OperationRecordId);
            Assert.Equal("回来喝水", incoming.Handoff);
            Assert.Equal(2, kernel.World.GetCharacter("a").Continuity!.ContextSequence);
            Assert.Equal(2, source.Calls);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
            Assert.Same(incoming, kernel.World.GetCharacter("a").Continuity!.Incoming);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
            Assert.Same(incoming, kernel.World.GetCharacter("a").Continuity!.Incoming);
            Assert.Equal("下一步检查路径", kernel.World.GetCharacter("a").Continuity!.DraftHandoff);
            Assert.Equal(4, source.Calls);
            Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
            saved = kernel.World.DescribeState();
            cursor = kernel.Cursor;
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(saved, reopened.State.DescribeState());
        Assert.Equal(cursor, reopened.Cursor);
        CycleDecisionContinuity continuity = reopened.State.GetCharacter("a").Continuity!;
        Assert.Equal(2, continuity.ContextSequence);
        Assert.Null(continuity.Pending);
        Assert.Equal(incomingSnapshot, continuity.Incoming!.ToString());
        Assert.Equal("下一步检查路径", continuity.DraftHandoff);
        var resumed = CycleEngine.CreateKernel(reopened, characterController: Commands(CycleCommand.Handoff(null)));
        Assert.False(resumed.RecoverPending());
        Assert.Equal(StepStatus.Committed, await resumed.StepAsync(Hour(240)));
        Assert.Equal(CycleEventKind.HandoffEdited, Assert.Single(resumed.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(2, resumed.World.GetCharacter("a").Continuity!.ContextSequence);
        Assert.Equal(incomingSnapshot, resumed.World.GetCharacter("a").Continuity!.Incoming!.ToString());
        Assert.Null(resumed.World.GetCharacter("a").Continuity!.DraftHandoff);
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
            if (Assert.Single(expected.LastCompletion!.Event.Facts).Kind == boundary) { break; }
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
            long previousContextSequence = reopened.State.GetCharacter("a").Continuity!.ContextSequence;
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
            Assert.Equal(expected.Cursor, recovered.Cursor);
            Assert.Equal(Describe(expected.LastCompletion!.Event), Describe(recovered.LastCompletion!.Event));
            CycleDecisionContinuity continuity = recovered.World.GetCharacter("a").Continuity!;
            switch (boundary) {
                case CycleEventKind.HandoffEdited:
                    Assert.Equal(1, continuity.ContextSequence);
                    Assert.Equal("观察等待后的身体变化", continuity.DraftHandoff);
                    Assert.Null(continuity.Pending);
                    break;
                case CycleEventKind.WaitStarted:
                    Assert.Equal(1, continuity.ContextSequence);
                    Assert.Null(continuity.DraftHandoff);
                    Assert.Equal("endpoint.original/2", continuity.Pending!.Action.ToolCallId);
                    Assert.Equal("batch-1/operation-2", continuity.Pending.Action.OperationRecordId);
                    Assert.Equal("观察等待后的身体变化", continuity.Pending.Handoff);
                    break;
                case CycleEventKind.DecisionContextOpened:
                    Assert.Equal(2, continuity.ContextSequence);
                    Assert.Null(continuity.Pending);
                    Assert.Equal("endpoint.original/2", continuity.Incoming!.Action.ToolCallId);
                    Assert.Equal("batch-1/operation-2", continuity.Incoming.Action.OperationRecordId);
                    Assert.Equal("观察等待后的身体变化", continuity.Incoming.Handoff);
                    break;
                case CycleEventKind.SurvivalFailed:
                    Assert.Null(continuity.Pending);
                    Assert.NotNull(continuity.Incoming);
                    Assert.True(continuity.Incoming.Summary.Reasons.HasFlag(CycleWakeReason.Terminal));
                    Assert.Equal(previousContextSequence, continuity.ContextSequence);
                    Assert.Equal(CycleExpeditionOutcome.Dead, recovered.World.Expedition!.Outcome);
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
            return ValueTask.FromResult(new CycleControllerOperation($"endpoint.original/{ordinal}", command,
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
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == stopAt)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

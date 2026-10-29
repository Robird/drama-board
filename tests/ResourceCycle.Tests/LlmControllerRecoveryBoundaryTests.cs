using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Real durable game boundaries with a counted controller, not a Completion/provider integration test.</summary>
public sealed class LlmControllerRecoveryBoundaryTests {
    [Theory]
    [InlineData("before-event")]
    [InlineData("after-event")]
    [InlineData("after-state")]
    public async Task Completed_controller_response_obeys_durable_boundary_on_cold_open(string crashAt) {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var first = new CountedController(_ => new("provider/original", "use item.water", "operation/original"));
        CharacterContext opening;
        CharacterInteractionState? committed = null;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = CycleEngine.CreateKernel(new CrashAtPublication(history, crashAt), characterController: first);
            await Advance(kernel);
            opening = history.State.Business.InteractionStates["a"].CurrentContext!;
            Assert.Equal(0, first.Calls);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(kernel));
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.Equal(1, first.Calls);
            Assert.True(kernel.IsFaulted);
            if (crashAt == "after-state") { committed = history.State.Business.InteractionStates["a"]; }
        }

        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            var next = new CountedController(context => {
                Assert.Equal("before-event", crashAt);
                Assert.Equal(MaterialSnapshot(opening), MaterialSnapshot(context.Context));
                // A fresh external request may choose a different operation and tool-call identity.
                return new("provider/retry", "handoff retry chose a different action", "operation/retry");
            });
            var kernel = CycleEngine.CreateKernel(history, characterController: next);
            Assert.Equal(crashAt == "after-event", history.PendingEvent is not null);
            Assert.Equal(crashAt == "after-event", kernel.RecoverPending());
            Assert.Equal(0, next.Calls);
            Assert.False(kernel.RecoverPending());
            if (crashAt == "before-event") {
                Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
                Assert.Empty(history.ReadCommittedOperations(["operation/original"]));
                await Advance(kernel);
                Assert.Equal(1, next.Calls);
                Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
                CharacterContextItem item = Assert.Single(kernel.World.Business.InteractionStates["a"].CurrentContext!.Items);
                Assert.Equal("provider/retry", item.ToolCallId);
                Assert.Equal("operation/retry", item.OperationRecordId);
            } else {
                Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
                Assert.Equal(90 * CycleSurvival.Scale, kernel.World.GetCharacter("a").Survival!.WaterUnits);
                var restored = kernel.World.Business.InteractionStates["a"];
                Assert.Equal(MaterialSnapshot(opening.Opening), MaterialSnapshot(restored.CurrentContext!.Opening));
                CharacterContextItem item = Assert.Single(restored.CurrentContext.Items);
                Assert.Equal("provider/original", item.ToolCallId);
                Assert.Equal("operation/original", item.OperationRecordId);
                Assert.False(item.IsRejected);
                Assert.Single(history.ReadCommittedOperations(["operation/original"]));
                if (committed is not null) { Assert.Equal(MaterialSnapshot(committed), MaterialSnapshot(restored)); }
                committed = restored;
            }
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        if (committed is not null) {
            Assert.Equal(MaterialSnapshot(committed), MaterialSnapshot(reopened.State.Business.InteractionStates["a"]));
            Assert.Equal(0, reopened.State.GetCharacter("a").Backpack.Count("item.water"));
        }
    }

    [Fact]
    public async Task Rejected_raw_command_is_delivered_to_next_controller_after_cold_open() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        CharacterContext saved;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var invalid = new CountedController(_ => new("provider/invalid", "this is not an action", "operation/invalid"));
            var kernel = CycleEngine.CreateKernel(history, characterController: invalid);
            await Advance(kernel);
            await Advance(kernel);
            Assert.Equal(1, invalid.Calls);
            Assert.Equal(initial.World.Time, kernel.World.Time);
            Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
            saved = kernel.World.Business.InteractionStates["a"].CurrentContext!;
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        var correction = new CountedController(context => {
            Assert.Equal(MaterialSnapshot(saved), MaterialSnapshot(context.Context));
            CharacterContextItem rejected = Assert.Single(context.Context.Items);
            Assert.True(rejected.IsRejected);
            Assert.Equal("this is not an action", rejected.RawInput);
            Assert.Equal("provider/invalid", rejected.ToolCallId);
            Assert.False(string.IsNullOrWhiteSpace(rejected.Result));
            return new("provider/corrected", "use item.water", "operation/corrected");
        });
        var resumed = CycleEngine.CreateKernel(reopened, characterController: correction);
        Assert.False(resumed.RecoverPending());
        await Advance(resumed);
        Assert.Equal(1, correction.Calls);
        Assert.Equal(saved.ContextSequence, resumed.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
        Assert.Equal(0, resumed.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(initial.World.Time, resumed.World.Time);
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));

    private sealed class CountedController(Func<CycleDecisionContext, CycleControllerOperation> choose) : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(choose(context));
        }
    }

    private sealed class InjectedCrash : IOException { }

    private sealed class CrashAtPublication(IOccurrenceHistory<CycleWorld, CycleFact> inner, string boundary)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        private bool _operation;
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            _operation = occurrence.Facts.Any(fact => fact.OperationRecordId == "operation/original");
            if (_operation && boundary == "before-event") { throw new InjectedCrash(); }
            inner.CommitEvent(occurrence);
            if (_operation && boundary == "after-event") { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) {
            inner.CommitState(state, cursor);
            if (_operation && boundary == "after-state") { throw new InjectedCrash(); }
        }
    }
}

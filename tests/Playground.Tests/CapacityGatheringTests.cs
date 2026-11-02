using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class CapacityGatheringTests {
    [Fact]
    public async Task Full_capacity_rejects_start_even_when_the_requested_item_is_absent() {
        ItemInventory backpack = new(new Dictionary<string, int> { ["item.wood"] = 2 }, unitCapacity: 2);
        PlaygroundWorld initial = LimitedWorld(backpack);
        var history = Memory(initial);
        KernelCursor cursor = history.Cursor;
        PlaygroundCommand command = PlaygroundCommand.Gather("sunflower");

        string reason = Assert.IsType<string>(PlaygroundActions.Check(initial, "a", command));
        Assert.Contains("容量", reason);
        Assert.DoesNotContain("该物品", reason);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands(command));
        await Step(kernel); // Commit the initial Context before testing command rejection.
        PlaygroundWorld opened = kernel.World;
        cursor = kernel.Cursor;
        await Step(kernel);

        Assert.Equal(PlaygroundEventKind.OperationRejected, Assert.Single(history.CompletedEvents[^1].Facts).Kind);
        Assert.Null(history.PendingEvent);
        Assert.Equal(opened.Time, history.State.Time);
        Assert.Same(backpack, history.State.GetCharacter("a").Backpack);
        Assert.Null(history.State.GetCharacter("a").Activity);
        Assert.Equal(opened.Resources, history.State.Resources);
        Assert.Equal(cursor.Version.TransitionCount + 1, history.Cursor.Version.TransitionCount);
        Assert.Equal(reason, Assert.Single(history.State.Business.InteractionStates["a"].CurrentContext!.Items).Result);
        Assert.Equal(0, backpack.Count("item.sunflower"));
        Assert.Equal(2, backpack.Count("item.wood"));
        Assert.Equal(5, initial.GetResource("sunflower").Stock);
    }

    [Theory]
    [InlineData(0, 2, 9)]
    [InlineData(1, 1, 8)]
    public async Task Capacity_is_shared_across_items_and_failed_cycle_does_not_debit_resource(
        int initialOtherItems, int successfulYields, int finishHour) {
        ItemInventory backpack = new(initialOtherItems == 0 ? null :
            new Dictionary<string, int> { ["item.wood"] = initialOtherItems }, unitCapacity: 2);
        PlaygroundWorld initial = LimitedWorld(backpack);
        var history = Memory(initial);
        var controller = Commands(PlaygroundCommand.Gather("sunflower", 3));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);

        await StepWorldEvent(kernel);
        await Until(kernel, world => world.GetCharacter("a").Activity is null);

        PlaygroundCharacter actor = kernel.World.GetCharacter("a");
        Assert.Equal(Hour(finishHour), kernel.World.Time);
        Assert.Equal(1, controller.Calls);
        Assert.Equal(2L, actor.Backpack.UnitCapacity);
        Assert.Equal(2L, actor.Backpack.TotalCount);
        Assert.Equal(successfulYields, actor.Backpack.Count("item.sunflower"));
        Assert.Equal(initialOtherItems, actor.Backpack.Count("item.wood"));
        Assert.Equal(5 - successfulYields, kernel.World.GetResource("sunflower").Stock);
        Assert.Contains("容量", Assert.IsType<string>(actor.LastResult));
        Assert.DoesNotContain("该物品", actor.LastResult!);
        Assert.Equal(successfulYields, history.CompletedEvents.SelectMany(item => item.Facts)
            .Count(fact => fact.Kind == PlaygroundEventKind.GatherYielded));
        PlaygroundFact finished = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(PlaygroundEventKind.GatherFinished, finished.Kind);
        Assert.Equal(PlaygroundGatherStopReason.CapacityLimit, finished.GatherStopReason);
        Assert.Empty(finished.ChangedResources);
        Assert.Empty(actor.Notices);

        Assert.Equal(initialOtherItems, backpack.TotalCount);
        Assert.Equal(0, backpack.Count("item.sunflower"));
        Assert.Equal(5, initial.GetResource("sunflower").Stock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_backpack_boundary_cold_reopens_then_finishes_identically_without_replaying_yields(bool completeState) {
        PlaygroundWorld initial = LimitedWorld(new(unitCapacity: 2));
        var expected = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Gather("sunflower", 3)));
        using var directory = new TemporaryStore();
        string priorState;
        KernelCursor priorCursor;

        using (var history = PlaygroundDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) {
            var original = PlaygroundEngine.CreateKernel(history, characterController: Commands(PlaygroundCommand.Gather("sunflower", 3)));
            // Commit the command and first yield. The second yield fills the inventory.
            for (int step = 0; step < 2; step++) {
                await StepWorldEvent(original);
                await StepWorldEvent(expected);
            }
            priorState = original.World.DescribeState();
            priorCursor = original.Cursor;
            Assert.Equal(1L, original.World.GetCharacter("a").Backpack.TotalCount);
            await StepWorldEvent(expected);

            if (completeState) {
                await StepWorldEvent(original);
                Assert.Null(history.PendingEvent);
                Assert.Equal(expected.World.DescribeState(), original.World.DescribeState());
            } else {
                var interrupted = PlaygroundEngine.CreateKernel(new FailAfterPublishedEvent(history),
                    characterController: new ForbiddenController());
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.StepAsync(Hour(24)).AsTask());
                Assert.IsType<InjectedCrash>(error.InnerException);
                Assert.True(interrupted.IsFaulted);
                Assert.Equal(priorState, history.State.DescribeState());
                Assert.Equal(priorCursor, history.Cursor);
                Assert.Equal(Describe(expected.LastCompletion!.Event), Describe(Assert.IsType<OccurrenceEvent<PlaygroundFact>>(history.PendingEvent)));
            }
        }

        using (var reopened = PlaygroundDurableHistory.Open(directory.StorePath)) {
            ItemInventory restored = reopened.State.GetCharacter("a").Backpack;
            Assert.Equal(2L, restored.UnitCapacity);
            Assert.Equal(completeState ? 2L : 1L, restored.TotalCount);
            if (!completeState) {
                Assert.Equal(priorState, reopened.State.DescribeState());
                Assert.Equal(priorCursor, reopened.Cursor);
                // The recorded full inventory must be restored, not run through admission again.
                PlaygroundFact pending = Assert.Single(Assert.IsType<OccurrenceEvent<PlaygroundFact>>(reopened.PendingEvent).Facts);
                ItemInventory recorded = Assert.Single(pending.ChangedCharacters).Backpack;
                Assert.Equal(2L, recorded.UnitCapacity);
                Assert.Equal(2L, recorded.TotalCount);
            }

            var recovery = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (before, instant, facts, folded) => PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
                PlaygroundEngine.Validate);
            Assert.Equal(!completeState, recovery.RecoverPending());
            Assert.Equal(expected.World.DescribeState(), recovery.World.DescribeState());
            Assert.Equal(expected.Cursor, recovery.Cursor);
            Assert.Null(reopened.PendingEvent);
            Assert.False(recovery.RecoverPending());
            Assert.Equal(2L, recovery.World.GetCharacter("a").Backpack.TotalCount);
            Assert.Equal(3, recovery.World.GetResource("sunflower").Stock);
            AssertPredictions(recovery.World);

            // Capacity stays in the business graph after either recovery path. The existing
            // third cycle fails at its original deadline, with no new input or resource loss.
            var resumed = PlaygroundEngine.CreateKernel(reopened, characterController: new ForbiddenController());
            await StepWorldEvent(resumed);
            await StepWorldEvent(expected);
            Assert.Equal(Hour(9), resumed.World.Time);
            Assert.Null(resumed.World.GetCharacter("a").Activity);
            Assert.Equal(2L, resumed.World.GetCharacter("a").Backpack.TotalCount);
            Assert.Equal(3, resumed.World.GetResource("sunflower").Stock);
            Assert.Equal(expected.World.DescribeState(), resumed.World.DescribeState());
            Assert.Equal(expected.Cursor, resumed.Cursor);
            Assert.Equal(Describe(expected.LastCompletion!.Event), Describe(resumed.LastCompletion!.Event));
        }

        using var final = PlaygroundDurableHistory.Open(directory.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(2L, final.State.GetCharacter("a").Backpack.UnitCapacity);
        Assert.Equal(2L, final.State.GetCharacter("a").Backpack.TotalCount);
        Assert.Equal(expected.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(expected.Cursor, final.Cursor);
    }

    private static PlaygroundWorld LimitedWorld(ItemInventory backpack) => PlaygroundWorld.CreatePlayable(Hour(6),
        [new("a", "Alice", backpack: backpack)],
        [Sun(Hour(6), stock: 5, capacity: 5, regenerationTicks: 100 * PlaygroundTime.Hour)]);

    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The recorded activity must continue without another decision.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not discover or arbitrate candidates.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not plan or call a character controller.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class FailAfterPublishedEvent(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            throw new InjectedCrash();
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

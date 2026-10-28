using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CyclePersistenceTests {
    [Fact]
    public async Task Every_completed_boundary_can_cold_reopen_and_continue_the_identical_trace() {
        using var directory = new TemporaryStore();
        var initial = CycleWorld.Create();
        var baseline = Memory(initial);
        var uninterrupted = CycleEngine.CreateKernel(baseline);
        using (var created = CycleDurableHistory.Create(directory.Path, initial, InitialCursor(initial))) {
            Assert.Equal(initial.DescribeState(), created.State.DescribeState());
        }

        int completed = 0;
        for (; completed < 100; completed++) {
            using var reopened = CycleDurableHistory.Open(directory.Path);
            Assert.Null(reopened.PendingEvent);
            Assert.Equal(uninterrupted.World.DescribeState(), reopened.State.DescribeState());
            Assert.Equal(uninterrupted.Cursor, reopened.Cursor);
            var resumed = CycleEngine.CreateKernel(reopened);
            Assert.False(resumed.RecoverPending());

            StepStatus expected = await uninterrupted.StepAsync(Hour(96));
            StepStatus actual = await resumed.StepAsync(Hour(96));

            Assert.Equal(expected, actual);
            Assert.Equal(uninterrupted.World.DescribeState(), resumed.World.DescribeState());
            Assert.Equal(uninterrupted.Cursor, resumed.Cursor);
            if (expected != StepStatus.Committed) { break; }
            Assert.Equal(Describe(uninterrupted.LastCompletion!.Event), Describe(resumed.LastCompletion!.Event));
        }
        Assert.InRange(completed, 10, 99);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public async Task Event_only_cold_recovery_applies_recorded_facts_once_without_replanning(int priorSteps) {
        using var directory = new TemporaryStore();
        var initial = CycleWorld.Create();
        var baseline = Memory(initial);
        var uninterrupted = CycleEngine.CreateKernel(baseline);
        string pendingText;
        string oldWorld;
        KernelCursor oldCursor;

        using (var history = CycleDurableHistory.Create(directory.Path, initial, InitialCursor(initial))) {
            var original = CycleEngine.CreateKernel(history);
            for (int index = 0; index < priorSteps; index++) {
                Assert.Equal(StepStatus.Committed, await original.StepAsync(Hour(240)));
                Assert.Equal(StepStatus.Committed, await uninterrupted.StepAsync(Hour(240)));
            }
            oldWorld = original.World.DescribeState();
            oldCursor = original.Cursor;
            var interrupted = CycleEngine.CreateKernel(new FailAfterPublishedEvent(history));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => interrupted.StepAsync(Hour(240)).AsTask());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(oldWorld, history.State.DescribeState());
            Assert.Equal(oldCursor, history.Cursor);
            pendingText = Describe(Assert.IsType<OccurrenceEvent<CycleFact>>(history.PendingEvent));
            Assert.Equal(StepStatus.Committed, await uninterrupted.StepAsync(Hour(240)));
            Assert.Equal(Describe(uninterrupted.LastCompletion!.Event), pendingText);
        }

        using (var reopened = CycleDurableHistory.Open(directory.Path)) {
            Assert.Equal(oldWorld, reopened.State.DescribeState());
            Assert.Equal(oldCursor, reopened.Cursor);
            Assert.Equal(pendingText, Describe(Assert.IsType<OccurrenceEvent<CycleFact>>(reopened.PendingEvent)));
            var metrics = new CycleMetrics();
            var resumed = CycleEngine.CreateKernel(reopened, metrics: metrics);

            Assert.True(resumed.RecoverPending());

            Assert.True(metrics.Recalculations > 0);
            Assert.Equal(0, metrics.PredictionChecks);
            Assert.Equal(pendingText, Describe(resumed.LastCompletion!.Event));
            Assert.Equal(uninterrupted.World.DescribeState(), resumed.World.DescribeState());
            Assert.Equal(uninterrupted.Cursor, resumed.Cursor);
            Assert.Null(reopened.PendingEvent);
            Assert.False(resumed.RecoverPending());
            for (int index = 0; index < 4; index++) {
                Assert.Equal(StepStatus.Committed, await uninterrupted.StepAsync(Hour(240)));
                Assert.Equal(StepStatus.Committed, await resumed.StepAsync(Hour(240)));
                Assert.Equal(Describe(uninterrupted.LastCompletion!.Event), Describe(resumed.LastCompletion!.Event));
                Assert.Equal(uninterrupted.World.DescribeState(), resumed.World.DescribeState());
            }
        }

        using var final = CycleDurableHistory.Open(directory.Path);
        Assert.Null(final.PendingEvent);
        Assert.Equal(uninterrupted.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(uninterrupted.Cursor, final.Cursor);
    }

    [Fact]
    public void Initial_world_and_cursor_must_have_the_same_time() {
        using var directory = new TemporaryStore();
        var world = CycleWorld.Create();
        var cursor = new KernelCursor(new WorldVersion(17, 0), Hour(0), null, null);

        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Create(directory.Path, world, cursor));
    }

    [Fact]
    public async Task Rejected_state_publication_faults_adapter_but_preserves_recoverable_pending_event() {
        using var directory = new TemporaryStore();
        var world = CycleWorld.Create();
        var baseline = Memory(world);
        var kernel = CycleEngine.CreateKernel(baseline);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));

        using (var history = CycleDurableHistory.Create(directory.Path, world, InitialCursor(world))) {
            history.CommitEvent(kernel.LastCompletion!.Event);
            Assert.Throws<ArgumentException>(() => history.CommitState(kernel.World, InitialCursor(world)));
            Assert.True(history.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => history.State);
        }

        using var reopened = CycleDurableHistory.Open(directory.Path);
        var resumed = CycleEngine.CreateKernel(reopened);
        Assert.True(resumed.RecoverPending());
        Assert.Equal(kernel.World.DescribeState(), resumed.World.DescribeState());
        Assert.Equal(kernel.Cursor, resumed.Cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_runtime_wrapper_is_installed_only_after_successful_state_publication(bool failBeforeState) {
        using var directory = new TemporaryStore();
        CycleWorld initial = CycleWorld.Create();
        string expectedState;
        using (var history = CycleDurableHistory.Create(directory.Path, initial, InitialCursor(initial))) {
            CycleWorld old = history.State;
            string oldText = old.DescribeState();
            var observed = new ObservePublication(history, failBeforeState);
            var kernel = CycleEngine.CreateKernel(observed);
            if (failBeforeState) {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(Hour(48)).AsTask());
                Assert.IsType<InjectedCrash>(error.InnerException);
                Assert.Same(old, history.State);
                Assert.Same(old, kernel.World);
                Assert.NotNull(history.PendingEvent);
            } else {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
                Assert.Same(observed.Prepared, history.State);
                Assert.Same(observed.Prepared, kernel.World);
                Assert.Null(history.PendingEvent);
            }
            Assert.Equal(oldText, old.DescribeState());
            expectedState = Assert.IsType<CycleWorld>(observed.Prepared).DescribeState();
        }

        using var reopened = CycleDurableHistory.Open(directory.Path);
        var recovered = CycleEngine.CreateKernel(reopened);
        Assert.Equal(failBeforeState, recovered.RecoverPending());
        Assert.Equal(expectedState, recovered.World.DescribeState());
    }

    [Fact]
    public async Task Cold_pending_recovery_never_queries_candidates_or_player_and_repeats_only_pure_completion() {
        using var directory = new TemporaryStore();
        CycleWorld initial = CycleWorld.CreatePlayable();
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: new WaitingCharacterController());
        Assert.Equal(StepStatus.Committed, await baseline.StepAsync(Hour(48)));
        using (var history = CycleDurableHistory.Create(directory.Path, initial, InitialCursor(initial))) {
            history.CommitEvent(baseline.LastCompletion!.Event);
        }

        using var reopened = CycleDurableHistory.Open(directory.Path);
        var metrics = new CycleMetrics();
        int completions = 0;
        var kernel = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(reopened, new SimulationRules(0, 1024),
            [new ForbiddenPlanning()], CycleEngine.Fold,
            (before, instant, facts, folded) => {
                completions++;
                return CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental, metrics);
            }, CycleEngine.Validate);

        Assert.True(kernel.RecoverPending());
        Assert.Equal(1, completions);
        Assert.True(metrics.Recalculations > 0);
        Assert.Equal(0, metrics.PredictionChecks);
        Assert.Equal(baseline.World.DescribeState(), kernel.World.DescribeState());
        Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(kernel.LastCompletion!.Event));
        Assert.False(kernel.RecoverPending());
        Assert.Equal(1, completions);
    }

    private sealed class ObservePublication(CycleDurableHistory inner, bool failBeforeState)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        private readonly CycleWorld _old = inner.State;
        public CycleWorld? Prepared { get; private set; }
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            Assert.Same(_old, inner.State);
            inner.CommitEvent(occurrence);
            Assert.Same(_old, inner.State);
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) {
            Prepared = state;
            Assert.NotSame(_old, state);
            Assert.Same(_old, inner.State);
            if (failBeforeState) { throw new InjectedCrash(); }
            inner.CommitState(state, cursor);
            Assert.Same(state, inner.State);
        }
    }

    private sealed class WaitingCharacterController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Operation(CycleCommand.Wait(6)));
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not discover or arbitrate candidates.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not plan or ask the Player again.");
    }

    private sealed class InjectedCrash : IOException { }

    private sealed class FailAfterPublishedEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            throw new InjectedCrash();
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) =>
            throw new InvalidOperationException("The test must stop between durable E and S.");
    }

    private sealed class TemporaryStore : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "DramaBoard.ResourceCycle.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}

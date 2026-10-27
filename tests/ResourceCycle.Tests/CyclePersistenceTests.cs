using DramaBoard.Kernel.Journal;
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

            Assert.Equal(0, metrics.Recalculations);
            Assert.Equal(0, metrics.PredictionChecks);
            Assert.Equal(0, metrics.RoutedNotifications);
            Assert.Equal(0, metrics.Invalidations);
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

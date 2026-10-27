using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class PlayableCycleTests {
    [Fact]
    public async Task Gathering_and_waiting_follow_hand_calculated_day_and_night_growth() {
        var history = Memory(CycleWorld.CreatePlayable());
        var source = new ScriptedSource(CycleCommand.Wait(6), CycleCommand.Gather("moongrass"), CycleCommand.Wait(9));
        var kernel = CycleEngine.CreateKernel(history, decisionSource: source);

        await PerformWait(kernel);

        Assert.Equal(Hour(22), kernel.World.Time);
        Assert.True(kernel.World.GetPlant("moongrass").IsMature);
        Assert.False(kernel.World.GetPlant("sunflower").IsMature);
        Assert.Equal(0, kernel.World.Character!.Backpack.Count("moongrass"));
        string beforeGather = kernel.World.DescribeState();
        CycleWorld oldWorld = kernel.World;

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));

        Assert.Equal(Hour(22), kernel.World.Time);
        Assert.Equal(1, kernel.World.Character!.Backpack.Count("moongrass"));
        Assert.False(kernel.World.GetPlant("moongrass").IsMature);
        Assert.Equal(0, kernel.World.GetPlant("moongrass").AccumulatedTicks);
        Assert.Equal(Hour(22), kernel.World.GetPlant("moongrass").ActiveSince);
        Assert.Equal(beforeGather, oldWorld.DescribeState());

        await PerformWait(kernel);

        Assert.Equal(Hour(31), kernel.World.Time);
        Assert.All(kernel.World.Plants, plant => Assert.True(plant.IsMature));
        Assert.Equal(1, kernel.World.Character!.Backpack.Count("moongrass"));
        Assert.Equal(3, source.Calls);
        Assert.Equal(new[] { Hour(22), Hour(26) }, history.CompletedEvents.SelectMany(item => item.Facts)
            .Where(fact => fact.Kind == CycleEventKind.PlantMatured && fact.TargetId == "moongrass")
            .Select(fact => fact.At));
        CycleFact sun = Assert.Single(history.CompletedEvents.SelectMany(item => item.Facts),
            fact => fact.Kind == CycleEventKind.PlantMatured && fact.TargetId == "sunflower");
        Assert.Equal(Hour(31), sun.At);
    }

    [Fact]
    public async Task Environmental_events_at_wait_deadline_settle_before_wait_completes() {
        var history = Memory(CycleWorld.CreatePlayable(Hour(15)));
        var kernel = CycleEngine.CreateKernel(history, decisionSource: new ScriptedSource(CycleCommand.Wait(3)));

        await PerformWait(kernel);

        Assert.Equal(new[] { CycleEventKind.PlantMatured, CycleEventKind.LightChanged, CycleEventKind.WaitCompleted },
            history.CompletedEvents.Where(item => item.TargetInstant.ModelTime == Hour(18))
                .SelectMany(item => item.Facts).Select(fact => fact.Kind));
        Assert.False(kernel.World.Light.IsDay);
        Assert.True(kernel.World.GetPlant("sunflower").IsMature);
        Assert.Null(kernel.World.Character!.WaitUntil);
    }

    [Fact]
    public async Task Waiting_for_external_input_does_not_advance_world_or_publish_state() {
        var initial = CycleWorld.CreatePlayable();
        var history = Memory(initial);
        var source = new GatedSource();
        var kernel = CycleEngine.CreateKernel(history, decisionSource: source);
        string state = initial.DescribeState();
        KernelCursor cursor = kernel.Cursor;

        Task<StepStatus> operation = kernel.StepAsync(Hour(96)).AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(operation.IsCompleted);
        Assert.Equal(state, kernel.World.DescribeState());
        Assert.Equal(cursor, kernel.Cursor);
        Assert.Same(initial, history.State);
        Assert.Empty(history.CompletedEvents);
        Assert.Null(history.PendingEvent);

        source.Answer.SetResult(CycleCommand.Wait(1));
        Assert.Equal(StepStatus.Committed, await operation);
        Assert.Equal(Hour(16), kernel.World.Time);
        Assert.Equal(Hour(17), kernel.World.Character!.WaitUntil);
        Assert.Single(history.CompletedEvents);
    }

    [Fact]
    public async Task Cancelling_external_input_leaves_no_game_action_or_pending_event() {
        var history = Memory(CycleWorld.CreatePlayable());
        var source = new GatedSource();
        var kernel = CycleEngine.CreateKernel(history, decisionSource: source);
        string state = kernel.World.DescribeState();
        using var cancellation = new CancellationTokenSource();

        Task<StepStatus> operation = kernel.StepAsync(Hour(96), cancellation.Token).AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(state, kernel.World.DescribeState());
        Assert.Equal(0, kernel.Cursor.Version.TransitionCount);
        Assert.Empty(history.CompletedEvents);
        Assert.Null(history.PendingEvent);
    }

    [Theory]
    [InlineData("immature")]
    [InlineData("unknown")]
    [InlineData("zero")]
    [InlineData("too-long")]
    public async Task Invalid_actions_and_inspection_do_not_change_the_world(string invalid) {
        var history = Memory(CycleWorld.CreatePlayable());
        CycleCommand command = invalid switch {
            "immature" => CycleCommand.Gather("sunflower"),
            "unknown" => CycleCommand.Gather("missing-plant"),
            "zero" => CycleCommand.Wait(0),
            _ => CycleCommand.Wait(25),
        };
        string before = history.State.DescribeState();
        Assert.NotNull(CycleActions.Check(history.State, "gardener", command));
        Assert.NotNull(CycleActions.Check(history.State, "someone-else", CycleCommand.Wait(1)));
        Assert.Null(CycleActions.Check(history.State, "gardener", CycleCommand.Wait(1)));
        Assert.Null(CycleActions.Check(history.State, "gardener", CycleCommand.Wait(24)));
        Assert.NotEmpty(CycleDiagnostics.Render(history.State));
        var kernel = CycleEngine.CreateKernel(history, decisionSource: new ScriptedSource(command));

        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(Hour(96)).AsTask());

        Assert.Equal(before, history.State.DescribeState());
        Assert.Equal(0, kernel.Cursor.Version.TransitionCount);
        Assert.Empty(history.CompletedEvents);
        Assert.Null(history.PendingEvent);
    }

    [Fact]
    public async Task Incremental_and_full_modes_preserve_complete_playable_trace() {
        var incrementalHistory = Memory(CycleWorld.CreatePlayable());
        var fullHistory = Memory(CycleWorld.CreatePlayable());
        var incrementalMetrics = new CycleMetrics();
        var fullMetrics = new CycleMetrics();
        var incremental = CycleEngine.CreateKernel(incrementalHistory, PredictionMode.Incremental,
            incrementalMetrics, StandardScript());
        var full = CycleEngine.CreateKernel(fullHistory, PredictionMode.Full, fullMetrics, StandardScript());

        foreach (var kernel in new[] { incremental, full }) {
            await PerformWait(kernel);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
            await PerformWait(kernel);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
        }

        Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
        Assert.Equal(full.Cursor, incremental.Cursor);
        Assert.Equal(fullHistory.CompletedEvents.Select(Describe), incrementalHistory.CompletedEvents.Select(Describe));
        Assert.Equal(1, incremental.World.Character!.Backpack.Count("sunflower"));
        Assert.Equal(1, incremental.World.Character!.Backpack.Count("moongrass"));
        Assert.True(incrementalMetrics.Recalculations < fullMetrics.Recalculations);
    }

    [Fact]
    public async Task Cold_reopen_during_wait_finishes_without_asking_for_another_command() {
        using var directory = new TemporaryStore();
        var initial = CycleWorld.CreatePlayable();
        var memory = Memory(initial);
        var uninterrupted = CycleEngine.CreateKernel(memory, decisionSource: new ScriptedSource(CycleCommand.Wait(6)));
        await PerformWait(uninterrupted);

        using (var history = CycleDurableHistory.Create(directory.Path, initial, InitialCursor(initial))) {
            var kernel = CycleEngine.CreateKernel(history, decisionSource: new ScriptedSource(CycleCommand.Wait(6)));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
            Assert.Equal(Hour(22), kernel.World.Character!.WaitUntil);
        }

        using var reopened = CycleDurableHistory.Open(directory.Path);
        var source = new ScriptedSource();
        var resumed = CycleEngine.CreateKernel(reopened, decisionSource: source);
        await FinishWait(resumed);

        Assert.Equal(0, source.Calls);
        Assert.Equal(uninterrupted.World.DescribeState(), resumed.World.DescribeState());
        Assert.Equal(uninterrupted.Cursor, resumed.Cursor);
        Assert.Null(reopened.PendingEvent);
    }

    [Theory]
    [InlineData(CycleEventKind.WaitStarted)]
    [InlineData(CycleEventKind.WaitCompleted)]
    [InlineData(CycleEventKind.PlantGathered)]
    public async Task Published_player_event_recovers_once_without_calling_decision_source(CycleEventKind crashKind) {
        using var directory = new TemporaryStore();
        var initial = CycleWorld.CreatePlayable();
        string expectedState;
        KernelCursor expectedCursor;
        string expectedEvent;

        using (var history = CycleDurableHistory.Create(directory.Path, initial, InitialCursor(initial))) {
            if (crashKind == CycleEventKind.PlantGathered) {
                var preparation = CycleEngine.CreateKernel(history, decisionSource: new ScriptedSource(CycleCommand.Wait(6)));
                await PerformWait(preparation);
            } else if (crashKind == CycleEventKind.WaitCompleted) {
                var preparation = CycleEngine.CreateKernel(history, decisionSource: new ScriptedSource(CycleCommand.Wait(2)));
                Assert.Equal(StepStatus.Committed, await preparation.StepAsync(Hour(96)));
            }
            CycleCommand command = crashKind == CycleEventKind.PlantGathered
                ? CycleCommand.Gather("moongrass") : CycleCommand.Wait(2);
            var expectedHistory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(history.State, history.Cursor);
            var expected = CycleEngine.CreateKernel(expectedHistory, decisionSource: new ScriptedSource(command));
            for (int attempts = 0; attempts < 10; attempts++) {
                Assert.Equal(StepStatus.Committed, await expected.StepAsync(Hour(96)));
                if (expected.LastCompletion!.Event.Facts.Any(fact => fact.Kind == crashKind)) { break; }
            }
            Assert.Contains(expected.LastCompletion!.Event.Facts, fact => fact.Kind == crashKind);
            expectedState = expected.World.DescribeState();
            expectedCursor = expected.Cursor;
            expectedEvent = Describe(expected.LastCompletion.Event);

            var interrupted = CycleEngine.CreateKernel(new FailAfterEventKind(history, crashKind),
                decisionSource: new ScriptedSource(command));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => {
                for (int attempts = 0; attempts < 10; attempts++) {
                    Assert.Equal(StepStatus.Committed, await interrupted.StepAsync(Hour(96)));
                }
            });
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.Equal(expectedEvent, Describe(Assert.IsType<OccurrenceEvent<CycleFact>>(history.PendingEvent)));
        }

        using (var reopened = CycleDurableHistory.Open(directory.Path)) {
            var source = new ScriptedSource();
            var metrics = new CycleMetrics();
            var resumed = CycleEngine.CreateKernel(reopened, metrics: metrics, decisionSource: source);

            Assert.True(resumed.RecoverPending());

            Assert.Equal(0, source.Calls);
            Assert.Equal(0, metrics.Recalculations);
            Assert.Equal(0, metrics.PredictionChecks);
            Assert.Equal(expectedEvent, Describe(resumed.LastCompletion!.Event));
            Assert.Equal(expectedState, resumed.World.DescribeState());
            Assert.Equal(expectedCursor, resumed.Cursor);
            Assert.Equal(crashKind == CycleEventKind.PlantGathered ? 1 : 0,
                resumed.World.Character!.Backpack.Count("moongrass"));
            Assert.False(resumed.RecoverPending());
            Assert.Equal(expectedState, resumed.World.DescribeState());
        }

        using var final = CycleDurableHistory.Open(directory.Path);
        Assert.Null(final.PendingEvent);
        Assert.Equal(expectedState, final.State.DescribeState());
        Assert.Equal(expectedCursor, final.Cursor);
    }

    private static ScriptedSource StandardScript() => new(CycleCommand.Wait(6),
        CycleCommand.Gather("moongrass"), CycleCommand.Wait(9), CycleCommand.Gather("sunflower"));

    private static async Task PerformWait(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Null(kernel.World.Character!.WaitUntil);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
        Assert.NotNull(kernel.World.Character!.WaitUntil);
        await FinishWait(kernel);
    }

    private static async Task FinishWait(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        for (int attempts = 0; attempts < 100; attempts++) {
            if (kernel.World.Character!.WaitUntil is null) { return; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
        }
        Assert.Fail("A bounded wait did not complete within 100 world events.");
    }

    private sealed class ScriptedSource(params CycleCommand[] commands) : ICycleDecisionSource {
        private readonly Queue<CycleCommand> _commands = new(commands);
        public int Calls { get; private set; }
        public ValueTask<CycleCommand> ChooseAsync(CycleWorld world, string actorId, CancellationToken cancellationToken) {
            Calls++;
            if (_commands.Count == 0) { throw new InvalidOperationException("An unexpected extra decision was requested."); }
            return ValueTask.FromResult(_commands.Dequeue());
        }
    }

    private sealed class GatedSource : ICycleDecisionSource {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CycleCommand> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CycleCommand> ChooseAsync(CycleWorld world, string actorId, CancellationToken cancellationToken) {
            Entered.SetResult();
            return await Answer.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class InjectedCrash : IOException { }

    private sealed class FailAfterEventKind(IOccurrenceHistory<CycleWorld, CycleFact> inner, CycleEventKind kind)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == kind)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }

    private sealed class TemporaryStore : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "DramaBoard.PlayableCycle.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}

using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class CompletionBoundaryTests {
    [Fact]
    public async Task EveryWinnerCompletesOnceAndRecordedResultsReplayWithoutCompletion() {
        var history = History(0);
        var first = new StageRule(0, 5);
        var second = new StageRule(123, 8);
        var completed = new List<(int Before, LogicalInstant Instant)>();
        var validated = new List<(int World, ModelTime Time)>();
        TransitionDraft<int> Complete(int world, LogicalInstant instant, TransitionDraft<int> draft) {
            completed.Add((world, instant));
            Assert.Equal([1, 2], draft.Facts);
            return new([.. draft.Facts, 3]);
        }
        static int Fold(int world, LogicalInstant _, int fact) => world * 10 + fact;
        var kernel = new SimulationKernel<int, int, int>(history, new(0, 100), [first, second],
            Fold, Complete, (world, time) => validated.Add((world, time)));

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new(10)));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new(10)));
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(new(10)));
        Assert.Equal(1, first.PlanCalls);
        Assert.Equal(1, second.PlanCalls);
        Assert.Equal([(0, new LogicalInstant(new(5), 0)), (123, new LogicalInstant(new(8), 0))], completed);
        Assert.Equal([(0, new ModelTime(0)), (123, new ModelTime(5)), (123123, new ModelTime(8))], validated);
        Assert.All(history.CompletedEvents, occurrence => Assert.Equal([1, 2, 3], occurrence.Facts));

        JournalBatch<int>[] batches = history.CompletedEvents.Select(e =>
            new JournalBatch<int>(e.TargetInstant, e.CauseKey, e.Facts)).ToArray();
        validated.Clear();
        var replay = SimulationReplay.Replay(0, 1, ModelTime.Zero, batches,
            Fold, (world, time) => validated.Add((world, time)));
        Assert.Equal(kernel.World, replay.World);
        Assert.Equal([(0, new ModelTime(0)), (123, new ModelTime(5)), (123123, new ModelTime(8))], validated);
        validated.Clear();
        var audit = SchedulerConformance.Verify(0, 1, ModelTime.Zero, new(0, 100),
            new IOccurrenceRule<int, int, int>[] { new StageRule(0, 5, true), new StageRule(123, 8, true) },
            batches, Fold, (world, time) => validated.Add((world, time)));
        Assert.Equal(kernel.World, audit.World);
        Assert.Equal([(0, new ModelTime(0)), (123, new ModelTime(5)), (123123, new ModelTime(8))], validated);
        Assert.Equal(2, completed.Count);

        var pending = History(123, new(new(1, 1), ModelTime.Zero, new(new(5), 0), batches[0].CauseKey));
        pending.CommitEvent(history.CompletedEvents[1]);
        validated.Clear();
        var resumed = new SimulationKernel<int, int, int>(pending, new(0, 100), [], Fold,
            (_, _, _) => throw new InvalidOperationException("Recovery must not complete again."),
            (world, time) => validated.Add((world, time)));
        Assert.True(resumed.RecoverPending());
        Assert.False(resumed.RecoverPending());
        Assert.Equal(kernel.World, resumed.World);
        Assert.Equal([(123, new ModelTime(5)), (123123, new ModelTime(8))], validated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingOrNullCompletionDoesNotFoldPublishOrInstall(bool returnsNull) {
        var history = History(0);
        int completions = 0;
        var kernel = new SimulationKernel<int, int, int>(history, new(0, 100), [new StageRule(0, 5)],
            (_, _, _) => throw new Exception("Fold must not be reached."),
            (_, _, _) => {
                completions++;
                return returnsNull ? null! : throw new InvalidOperationException("Completion failed.");
            }, (_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(new(5)));
        Assert.Equal(1, completions);
        Assert.Equal(0, kernel.World);
        Assert.Equal(0, history.State);
        Assert.Empty(history.CompletedEvents);
        Assert.Null(history.PendingEvent);
        Assert.False(kernel.IsFaulted);
    }

    [Fact]
    public void ConstructorValidatesAgainstCompletedCursorTime() {
        var cursor = new KernelCursor(new(1, 1), ModelTime.Zero, new(new(10), 0), CandidateKey.FromUtf8("old"));
        Assert.Throws<InvalidOperationException>(() => new SimulationKernel<int, int, int>(
            History(5, cursor), new(0, 100), [], (world, _, _) => world,
            (_, _, draft) => draft, RejectExpired));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalAndPendingValidateAgainstTargetTimeBeforePublishingState(bool pending) {
        var history = History(5);
        if (pending) {
            history.CommitEvent(new(CandidateKey.FromUtf8("pending"), new(new(10), 0), [1]));
        }
        var kernel = new SimulationKernel<int, int, int>(history, new(0, 100), [new StageRule(5, 10)],
            (world, _, _) => world, (_, _, draft) => draft, RejectExpired);
        if (pending) {
            Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());
            Assert.NotNull(history.PendingEvent);
        } else {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(new(10)));
            Assert.Null(history.PendingEvent);
        }
        Assert.Equal(5, history.State);
        Assert.Empty(history.CompletedEvents);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReplayAndConformanceRejectExpiredGenesisAndFinalBoundary(bool conformance, bool empty) {
        ModelTime genesisTime = new(empty ? 10 : 0);
        var rule = new StageRule(5, 10, true);
        JournalBatch<int>[] batches = empty ? [] : [new(new(new(10), 0), rule.Key, [1])];
        if (conformance) {
            Assert.Throws<InvalidOperationException>(() => SchedulerConformance.Verify(
                5, 1, genesisTime, new(0, 100), new IOccurrenceRule<int, int, int>[] { rule }, batches,
                (world, _, _) => world, RejectExpired));
        } else {
            Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay(
                5, 1, genesisTime, batches, (world, _, _) => world, RejectExpired));
        }
        Assert.Equal(0, rule.PlanCalls);
    }

    [Fact]
    public void EmptyForkValidatesGenesisTime() {
        Assert.Throws<InvalidOperationException>(() => SimulationFork.Create(
            5, new(10), new InMemoryJournal<int>(1), 0, 2, new(0, 100),
            (world, _, _) => world, RejectExpired));
    }

    private static void RejectExpired(int due, ModelTime time) {
        if (new ModelTime(due) < time) { throw new InvalidOperationException("Prediction expired at boundary."); }
    }

    private static InMemoryOccurrenceHistory<int, int> History(int state, KernelCursor? cursor = null) =>
        new(state, cursor ?? new(new(1, 0), ModelTime.Zero, null, null));

    private sealed class StageRule(int stage, long due, bool rejectPlan = false) : IOccurrenceRule<int, int, int> {
        public CandidateKey Key { get; } = CandidateKey.FromUtf8($"stage/{stage}");
        public int PlanCalls { get; private set; }
        public IReadOnlyList<OccurrenceCandidate<int>> Forecast(int world, SimulationRules rules) =>
            world == stage ? [new(Key, new(new(due)), stage)] : [];
        public ValueTask<TransitionDraft<int>> PlanSelectedAsync(int world, OccurrenceCandidate<int> winner,
            CancellationToken cancellationToken) {
            PlanCalls++;
            if (rejectPlan) { throw new InvalidOperationException("Replay and conformance must not plan."); }
            return ValueTask.FromResult(new TransitionDraft<int>([1, 2]));
        }
    }
}

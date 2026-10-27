using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class CompletionBoundaryTests {
    [Fact]
    public async Task EveryExecutionPathPreparesDerivedStateAfterOrderedBusinessFold() {
        var genesis = new RuntimeWorld(0, 0);
        var history = RuntimeHistory(genesis);
        var first = new RuntimeRule(0, 5);
        var second = new RuntimeRule(12, 8);
        var completed = new List<(int Before, LogicalInstant Instant, int Folded)>();
        var validated = new List<(int World, ModelTime Time)>();
        RuntimeWorld Complete(RuntimeWorld before, LogicalInstant instant, IReadOnlyList<int> facts, RuntimeWorld folded) {
            completed.Add((before.Business, instant, folded.Business));
            Assert.Equal([1, 2], facts);
            Assert.Equal(before.Cache, folded.Cache);
            return folded with { Cache = folded.Business * 2 };
        }
        void Validate(RuntimeWorld world, ModelTime time) {
            Assert.Equal(world.Business * 2, world.Cache);
            validated.Add((world.Business, time));
        }
        static RuntimeWorld Fold(RuntimeWorld world, LogicalInstant _, int fact) =>
            world with { Business = world.Business * 10 + fact };
        var kernel = new SimulationKernel<RuntimeWorld, int, int>(history, new(0, 100), [first, second],
            Fold, Complete, Validate);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new(10)));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new(10)));
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(new(10)));
        Assert.Equal(1, first.PlanCalls);
        Assert.Equal(1, second.PlanCalls);
        Assert.Equal([(0, new LogicalInstant(new(5), 0), 12), (12, new LogicalInstant(new(8), 0), 1212)], completed);
        Assert.Equal([(0, new ModelTime(0)), (12, new ModelTime(5)), (1212, new ModelTime(8))], validated);
        Assert.All(history.CompletedEvents, occurrence => Assert.Equal([1, 2], occurrence.Facts));
        Assert.Equal(new RuntimeWorld(0, 0), genesis);

        JournalBatch<int>[] batches = history.CompletedEvents.Select(e =>
            new JournalBatch<int>(e.TargetInstant, e.CauseKey, e.Facts)).ToArray();
        validated.Clear();
        var replay = SimulationReplay.Replay(genesis, 1, ModelTime.Zero, batches, Fold, Complete, Validate);
        Assert.Equal(kernel.World, replay.World);
        Assert.Equal([(0, new ModelTime(0)), (12, new ModelTime(5)), (1212, new ModelTime(8))], validated);
        Assert.Equal(4, completed.Count);
        validated.Clear();
        var audit = SchedulerConformance.Verify(genesis, 1, ModelTime.Zero, new(0, 100),
            new IOccurrenceRule<RuntimeWorld, int, int>[] { new RuntimeRule(0, 5, true), new RuntimeRule(12, 8, true) },
            batches, Fold, Complete, Validate);
        Assert.Equal(kernel.World, audit.World);
        Assert.Equal([(0, new ModelTime(0)), (12, new ModelTime(5)), (1212, new ModelTime(8))], validated);
        Assert.Equal(6, completed.Count);

        var pending = RuntimeHistory(new(12, 24), new(new(1, 1), ModelTime.Zero, new(new(5), 0), batches[0].CauseKey));
        pending.CommitEvent(history.CompletedEvents[1]);
        validated.Clear();
        var resumed = new SimulationKernel<RuntimeWorld, int, int>(pending, new(0, 100),
            [new RuntimeRule(12, 8, true)], Fold, Complete, Validate);
        Assert.True(resumed.RecoverPending());
        Assert.False(resumed.RecoverPending());
        Assert.Equal(kernel.World, resumed.World);
        Assert.Equal([(12, new ModelTime(5)), (1212, new ModelTime(8))], validated);
        Assert.Equal(7, completed.Count);

        var journal = new InMemoryJournal<int>(1);
        foreach (var batch in batches) { journal.AppendBatch(batch); }
        var fork = SimulationFork.Create(genesis, ModelTime.Zero, journal, 2, 2, new(0, 100), Fold, Complete, Validate);
        Assert.Equal(kernel.World, fork.Replay.World);
        Assert.Equal(9, completed.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ThrowingOrNullCompletionDoesNotPublishOrInstall(bool returnsNull, bool pending) {
        var initial = new RuntimeWorld(0, 0);
        var history = RuntimeHistory(initial);
        if (pending) {
            history.CommitEvent(new(CandidateKey.FromUtf8("pending"), new(new(5), 0), [1, 2]));
        }
        int completions = 0;
        int folds = 0;
        var kernel = new SimulationKernel<RuntimeWorld, int, int>(history, new(0, 100), [new RuntimeRule(0, 5)],
            (world, _, fact) => { folds++; return world with { Business = world.Business + fact }; },
            (before, _, facts, folded) => {
                completions++;
                Assert.Same(initial, before);
                Assert.Equal([1, 2], facts);
                Assert.Equal(3, folded.Business);
                return returnsNull ? null! : throw new InvalidOperationException("Completion failed.");
            }, (_, _) => { });
        if (pending) {
            Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());
        } else {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(new(5)));
        }
        Assert.Equal(1, completions);
        Assert.Equal(2, folds);
        Assert.Same(initial, kernel.World);
        Assert.Same(initial, history.State);
        Assert.Equal(new RuntimeWorld(0, 0), initial);
        Assert.Empty(history.CompletedEvents);
        Assert.Equal(pending, history.PendingEvent is not null);
        Assert.Equal(pending, kernel.IsFaulted);
    }

    [Fact]
    public async Task PreparedCacheInstallsOnlyAfterSuccessfulStatePublication() {
        var initial = new RuntimeWorld(0, 0);
        var history = new RuntimePublicationHistory(initial);
        RuntimeWorld? prepared = null;
        SimulationKernel<RuntimeWorld, int, int>? kernel = null;
        history.BeforeEvent = occurrence => {
            Assert.Same(initial, kernel!.World);
            Assert.Same(initial, history.State);
            Assert.Equal([1, 2], occurrence.Facts);
        };
        history.BeforeState = world => {
            Assert.Same(initial, kernel!.World);
            Assert.Same(initial, history.State);
            Assert.Same(prepared, world);
        };
        kernel = new(history, new(0, 100), [new RuntimeRule(0, 5)],
            (world, _, fact) => world with { Business = world.Business + fact },
            (_, _, _, folded) => prepared = folded with { Cache = folded.Business * 2 },
            (world, _) => Assert.Equal(world.Business * 2, world.Cache));

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new(5)));
        Assert.Same(prepared, kernel.World);
        Assert.Same(prepared, history.State);
        Assert.Equal(new RuntimeWorld(0, 0), initial);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("event")]
    [InlineData("state")]
    public async Task FailedBoundaryDoesNotInstallPreparedCache(string failAt) {
        var initial = new RuntimeWorld(0, 0);
        var history = new RuntimePublicationHistory(initial);
        history.BeforeEvent = _ => { if (failAt == "event") { throw new IOException(); } };
        history.BeforeState = _ => { if (failAt == "state") { throw new IOException(); } };
        RuntimeWorld? prepared = null;
        var kernel = new SimulationKernel<RuntimeWorld, int, int>(history, new(0, 100), [new RuntimeRule(0, 5)],
            (world, _, fact) => world with { Business = world.Business + fact },
            (_, _, _, folded) => prepared = folded with { Cache = folded.Business * 2 },
            (world, _) => {
                if (failAt == "validate" && world.Business != 0) { throw new InvalidOperationException("Invalid prepared state."); }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(new(5)));
        Assert.Equal(new RuntimeWorld(3, 6), prepared);
        Assert.Same(initial, kernel.World);
        Assert.Same(initial, history.State);
        Assert.Equal(new RuntimeWorld(0, 0), initial);
        Assert.Equal(failAt == "state", history.PendingEvent is not null);
        Assert.Equal(failAt != "validate", kernel.IsFaulted);
    }

    [Fact]
    public void ConstructorValidatesAgainstCompletedCursorTime() {
        var cursor = new KernelCursor(new(1, 1), ModelTime.Zero, new(new(10), 0), CandidateKey.FromUtf8("old"));
        Assert.Throws<InvalidOperationException>(() => new SimulationKernel<int, int, int>(
            History(5, cursor), new(0, 100), [], (world, _, _) => world,
            (_, _, _, folded) => folded, RejectExpired));
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
            (world, _, _) => world, (_, _, _, folded) => folded, RejectExpired);
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
                (world, _, _) => world, (_, _, _, folded) => folded, RejectExpired));
        } else {
            Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay(
                5, 1, genesisTime, batches, (world, _, _) => world, (_, _, _, folded) => folded, RejectExpired));
        }
        Assert.Equal(0, rule.PlanCalls);
    }

    [Fact]
    public void EmptyForkValidatesGenesisTime() {
        Assert.Throws<InvalidOperationException>(() => SimulationFork.Create(
            5, new(10), new InMemoryJournal<int>(1), 0, 2, new(0, 100),
            (world, _, _) => world, (_, _, _, folded) => folded, RejectExpired));
    }

    private static void RejectExpired(int due, ModelTime time) {
        if (new ModelTime(due) < time) { throw new InvalidOperationException("Prediction expired at boundary."); }
    }

    private static InMemoryOccurrenceHistory<int, int> History(int state, KernelCursor? cursor = null) =>
        new(state, cursor ?? new(new(1, 0), ModelTime.Zero, null, null));

    private sealed record RuntimeWorld(int Business, int Cache);

    private static InMemoryOccurrenceHistory<RuntimeWorld, int> RuntimeHistory(RuntimeWorld world, KernelCursor? cursor = null) =>
        new(world, cursor ?? new(new(1, 0), ModelTime.Zero, null, null));

    private sealed class RuntimePublicationHistory(RuntimeWorld world) : IOccurrenceHistory<RuntimeWorld, int> {
        private readonly InMemoryOccurrenceHistory<RuntimeWorld, int> _history = RuntimeHistory(world);
        public RuntimeWorld State => _history.State;
        public KernelCursor Cursor => _history.Cursor;
        public OccurrenceEvent<int>? PendingEvent => _history.PendingEvent;
        public Action<OccurrenceEvent<int>>? BeforeEvent { get; set; }
        public Action<RuntimeWorld>? BeforeState { get; set; }
        public void CommitEvent(OccurrenceEvent<int> occurrence) {
            BeforeEvent?.Invoke(occurrence);
            _history.CommitEvent(occurrence);
        }
        public void CommitState(RuntimeWorld nextState, KernelCursor nextCursor) {
            BeforeState?.Invoke(nextState);
            _history.CommitState(nextState, nextCursor);
        }
    }

    private sealed class RuntimeRule(int stage, long due, bool rejectPlan = false) : IOccurrenceRule<RuntimeWorld, int, int> {
        public int PlanCalls { get; private set; }
        public IReadOnlyList<OccurrenceCandidate<int>> Forecast(RuntimeWorld world, SimulationRules rules) {
            Assert.Equal(world.Business * 2, world.Cache);
            return world.Business == stage ? [new(CandidateKey.FromUtf8($"stage/{stage}"), new(new(due)), stage)] : [];
        }
        public ValueTask<TransitionDraft<int>> PlanSelectedAsync(RuntimeWorld world, OccurrenceCandidate<int> winner,
            CancellationToken cancellationToken) {
            PlanCalls++;
            if (rejectPlan) { throw new InvalidOperationException("Recovery and audit must not plan."); }
            return ValueTask.FromResult(new TransitionDraft<int>([1, 2]));
        }
    }

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

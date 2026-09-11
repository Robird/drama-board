using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SimulationKernelTests
{
    [Fact]
    public async Task EmptyForecastAndBoundaryDoNotCreateEvents()
    {
        var history = History(7);
        var rule = new TestRule(_ => []);
        var kernel = Kernel(history, [rule]);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Time(100)));
        Assert.Equal(7, kernel.World);
        Assert.Null(kernel.LastCompletion);
        Assert.Null(history.PendingEvent);
        Assert.Empty(history.CompletedEvents);
        rule.Forecast = _ => [Candidate("future", 101)];
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(Time(100)));
        Assert.Equal(0, rule.PlanCalls);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Time(101)));
        Assert.Equal(Time(101), kernel.CurrentModelTime);
    }

    [Fact]
    public async Task OneOccurrencePerStepAndFullReforecast()
    {
        var history = History();
        var rule = new TestRule(world => world < 2 ? [Candidate($"next:{world}", 5)] : []);
        var kernel = Kernel(history, [rule]);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Time(5)));
        Assert.Single(history.CompletedEvents);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Time(5)));
        Assert.Equal(2, rule.ForecastCalls);
        Assert.Equal(2, rule.PlanCalls);
        Assert.Equal(2, kernel.World);
        Assert.Equal([0L, 1L], history.CompletedEvents.Select(e => e.TargetInstant.CausalOrdinal));
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Time(5)));
        Assert.Null(kernel.LastCompletion);
    }

    [Fact]
    public async Task GlobalWinnerAndRegistrationOrderChooseTheSameOwner()
    {
        async Task<(int World, CandidateKey Cause)> Run(bool reverse)
        {
            var a = new TestRule(_ => [Candidate("a", 10)], (_, _, _) => Planned(11));
            var b = new TestRule(_ => [Candidate("b", 10)], (_, _, _) => Planned(22));
            var history = History();
            var kernel = Kernel(history, reverse ? [b, a] : [a, b]);
            CandidateKey expected = OccurrenceScheduler.SelectWinner(
                [Candidate("a", 10), Candidate("b", 10)], 42).Key;
            await kernel.StepAsync(Time(10));
            Assert.Equal(expected == Key("a") ? 1 : 0, a.PlanCalls);
            Assert.Equal(expected == Key("b") ? 1 : 0, b.PlanCalls);
            return (kernel.World, Assert.Single(history.CompletedEvents).CauseKey);
        }
        Assert.Equal(await Run(false), await Run(true));
    }

    [Fact]
    public async Task DuplicateOrPastCandidateFailsBeforePlan()
    {
        var duplicate = new TestRule(_ => [Candidate("same", 10), Candidate("same", 11)]);
        var kernel = Kernel(History(), [duplicate]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(20)));
        Assert.Equal(0, duplicate.PlanCalls);
        var past = new TestRule(_ => [Candidate("past", 9)]);
        var resumed = Kernel(History(1, Cursor(4, 10, 0, "previous")), [past]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resumed.StepAsync(Time(20)));
        Assert.Equal(0, past.PlanCalls);
    }

    [Fact]
    public async Task InFlightPlanRejectsConcurrentStepAndRecovery()
    {
        var completion = new TaskCompletionSource<TransitionDraft<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new TestRule(_ => [Candidate("blocked", 1)], (_, _, _) => new(completion.Task));
        var kernel = Kernel(History(), [rule]);
        ValueTask<StepStatus> first = kernel.StepAsync(Time(1));
        Assert.Throws<InvalidOperationException>(() => kernel.StepAsync(Time(1)));
        Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());
        completion.SetResult(new TransitionDraft<int>([1]));
        Assert.Equal(StepStatus.Committed, await first);
    }

    [Fact]
    public async Task OrderedFactsShareOneInstantAndHotPathDoesNotFoldTwice()
    {
        var history = History();
        var rule = new TestRule(_ => [Candidate("many", 5)], (_, _, _) => Planned(1, 2, 3));
        var calls = new List<LogicalInstant>();
        var kernel = Kernel(history, [rule], (world, instant, fact) =>
        {
            calls.Add(instant);
            return world * 10 + fact;
        });
        await kernel.StepAsync(Time(5));
        Assert.Equal(123, kernel.World);
        Assert.Equal(3, calls.Count);
        Assert.All(calls, instant => Assert.Equal(new LogicalInstant(Time(5), 0), instant));
        Assert.Equal(new WorldVersion(1, 1), kernel.Version);
        Assert.Same(Assert.Single(history.CompletedEvents), kernel.LastCompletion!.Event);
        Assert.Equal(kernel.Cursor, kernel.LastCompletion.Cursor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryFactFailureLeavesOldStateAndNoEvent(int failAt)
    {
        var history = History();
        var rule = new TestRule(_ => [Candidate("many", 5)], (_, _, _) => Planned(1, 2, 3));
        var kernel = Kernel(history, [rule], (world, _, fact) =>
            fact == failAt ? throw new TestFailure() : world + fact);
        await Assert.ThrowsAsync<TestFailure>(async () => await kernel.StepAsync(Time(5)));
        Assert.Equal(0, kernel.World);
        Assert.Equal(0, history.State);
        Assert.Null(history.PendingEvent);
        Assert.Empty(history.CompletedEvents);
        Assert.False(kernel.IsFaulted);
    }

    [Fact]
    public async Task ValidationFailureDoesNotPublishEvent()
    {
        var history = History();
        var kernel = Kernel(history, [new(_ => [Candidate("bad", 0)])], validate: world =>
        {
            if (world != 0) { throw new TestFailure(); }
        });
        await Assert.ThrowsAsync<TestFailure>(async () => await kernel.StepAsync(Time(0)));
        Assert.Null(history.PendingEvent);
        Assert.Empty(history.CompletedEvents);
        Assert.Equal(0, kernel.World);
    }

    [Fact]
    public async Task CancellationAfterPlanBeforeEventLeavesCompletedBoundaryUnchanged()
    {
        using var cancellation = new CancellationTokenSource();
        var history = History();
        var rule = new TestRule(_ => [Candidate("cancel", 0)], (_, _, _) =>
        {
            cancellation.Cancel();
            return Planned(1);
        });
        var kernel = Kernel(history, [rule]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await kernel.StepAsync(Time(0), cancellation.Token));
        Assert.Equal(0, history.State);
        Assert.Null(history.PendingEvent);
        Assert.Null(kernel.LastCompletion);
    }

    [Theory]
    [InlineData("before-e", false, false)]
    [InlineData("after-e", true, false)]
    [InlineData("before-s", true, false)]
    [InlineData("after-s", false, true)]
    public async Task PublicationFailureStopsKernelAndResumeUsesActualBoundary(string failAt, bool hasPending, bool completed)
    {
        var history = new ControlledHistory { FailAt = failAt };
        var rule = new TestRule(_ => [Candidate("write", 1)]);
        var kernel = Kernel(history, [rule]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(1)));
        Assert.IsType<IOException>(error.InnerException);
        Assert.True(kernel.IsFaulted);
        Assert.Equal(0, kernel.World);
        Assert.Null(kernel.LastCompletion);
        Assert.Equal(hasPending, history.PendingEvent is not null);
        Assert.Equal(completed ? 1 : 0, history.State);
        Assert.Throws<InvalidOperationException>(() => kernel.StepAsync(Time(1)));
        Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());

        // A fresh history view simulates reopening at the persisted S/E boundary.
        var reopened = History(history.State, history.Cursor);
        if (history.PendingEvent is { } pending) { reopened.CommitEvent(pending); }
        var resumedRule = new TestRule(_ => throw new TestFailure());
        int folds = 0;
        var resumed = Kernel(reopened, [resumedRule], (world, _, fact) => { folds++; return world + fact; });
        Assert.Equal(hasPending, resumed.RecoverPending());
        Assert.Equal(hasPending ? 1 : 0, folds);
        Assert.Equal(0, resumedRule.ForecastCalls);
        Assert.Equal(0, resumedRule.PlanCalls);
        Assert.Equal(hasPending || completed ? 1 : 0, resumed.World);
        Assert.Null(reopened.PendingEvent);
    }

    [Fact]
    public async Task EventPublicationBeginsNonCancelableCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var history = new ControlledHistory { AfterEvent = cancellation.Cancel };
        var kernel = Kernel(history, [new(_ => [Candidate("write", 1)])]);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Time(1), cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, kernel.World);
        Assert.NotNull(kernel.LastCompletion);
    }

    [Fact]
    public async Task SubstitutedEventStopsBeforeStatePublication()
    {
        var history = new ControlledHistory { SubstituteEvent = true };
        var kernel = Kernel(history, [new(_ => [Candidate("write", 1)])]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(1)));
        Assert.True(kernel.IsFaulted);
        Assert.Equal([99], history.PendingEvent!.Facts);
        Assert.Equal(0, history.StateCalls);
    }

    [Fact]
    public async Task StateCommitThatDoesNotInstallItsWorldIsRejected()
    {
        var history = new ControlledHistory { IgnoreState = true };
        var kernel = Kernel(history, [new(_ => [Candidate("write", 1)])]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(1)));
        Assert.True(kernel.IsFaulted);
        Assert.Equal(0, kernel.World);
        Assert.Null(kernel.LastCompletion);
    }

    [Fact]
    public async Task HistoryStateMovedWithoutCursorIsRejectedBeforeForecast()
    {
        var history = new ControlledHistory();
        var rule = new TestRule(_ => []);
        var kernel = Kernel(history, [rule]);
        history.State = 99;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(1)));
        Assert.True(kernel.IsFaulted);
        Assert.Equal(0, rule.ForecastCalls);
    }

    [Fact]
    public async Task EqualButDifferentReferenceStateCannotReplaceInstalledWorld()
    {
        var state = new ValueWorld(7);
        var history = new ReferenceHistory(state);
        var kernel = new SimulationKernel<ValueWorld, int, int>(history, new(42, 100), [], (world, _, _) => world, _ => { });
        history.State = new ValueWorld(7);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(1)));
        Assert.Same(state, kernel.World);
        Assert.True(kernel.IsFaulted);
    }

    [Fact]
    public async Task ThrowingPendingGetterReleasesOperationGuard()
    {
        var history = new ControlledHistory();
        var kernel = Kernel(history, [new(_ => [])]);
        history.ThrowPendingGetter = true;
        await Assert.ThrowsAsync<TestFailure>(async () => await kernel.StepAsync(Time(1)));
        history.ThrowPendingGetter = false;
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Time(1)));
        Assert.False(kernel.RecoverPending());
    }

    [Fact]
    public async Task PendingMustBeRecoveredExplicitlyAndOnlyOnce()
    {
        var history = History(7, Cursor(12, 10, 2, "old"));
        history.CommitEvent(Event("pending", 10, 3, 1, 2));
        var rule = new TestRule(_ => throw new TestFailure());
        int folds = 0;
        var kernel = Kernel(history, [rule], (world, _, fact) => { folds++; return world + fact; });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(1)));
        Assert.True(kernel.RecoverPending());
        Assert.Equal(10, kernel.World);
        Assert.Equal(13, kernel.Version.TransitionCount);
        Assert.Equal(new LogicalInstant(Time(10), 3), kernel.LastCommittedInstant);
        Assert.Equal(2, folds);
        Assert.Equal(0, rule.ForecastCalls);
        Assert.Equal(0, rule.PlanCalls);
        Assert.False(kernel.RecoverPending());
        Assert.Equal(2, folds);
        Assert.Null(kernel.LastCompletion);
    }

    [Fact]
    public void CompletedHeadRestoresWithoutAnyHistoricalFold()
    {
        var history = History(123, Cursor(80, 500, 6, "last"));
        var kernel = Kernel(history, [new(_ => throw new TestFailure())], (_, _, _) => throw new TestFailure());
        Assert.Empty(history.CompletedEvents); // No journal count is needed to restore cursor 80.
        Assert.False(kernel.RecoverPending());
        Assert.Equal(123, kernel.World);
        Assert.Equal(80, kernel.Version.TransitionCount);
    }

    [Theory]
    [InlineData("old", 10, 3)]
    [InlineData("new", 9, 0)]
    [InlineData("new", 10, 4)]
    [InlineData("new", 11, 1)]
    public void InvalidPendingCauseOrInstantDoesNotFoldOrPublish(string cause, long time, long ordinal)
    {
        var history = History(7, Cursor(12, 10, 2, "old"));
        history.CommitEvent(Event(cause, time, ordinal, 1));
        var kernel = Kernel(history, [], (_, _, _) => throw new TestFailure());
        Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());
        Assert.Equal(7, history.State);
        Assert.NotNull(history.PendingEvent);
        Assert.True(kernel.IsFaulted);
    }

    [Fact]
    public void PendingFoldFailureStopsWithoutPublishingPartialState()
    {
        var history = History(7);
        history.CommitEvent(Event("pending", 1, 0, 1, 2));
        var kernel = Kernel(history, [], (world, _, fact) => fact == 2 ? throw new TestFailure() : world + fact);
        Assert.Throws<TestFailure>(() => kernel.RecoverPending());
        Assert.Equal(7, kernel.World);
        Assert.Equal(7, history.State);
        Assert.True(kernel.IsFaulted);
        Assert.NotNull(history.PendingEvent);
    }

    [Fact]
    public void PendingRecoveryCanBeCanceledBeforeItBegins()
    {
        var history = History();
        history.CommitEvent(Event("pending", 1, 0, 1));
        var kernel = Kernel(history, []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => kernel.RecoverPending(cancellation.Token));
        Assert.False(kernel.IsFaulted);
        Assert.True(kernel.RecoverPending());
    }

    [Fact]
    public async Task SameCauseNoProgressAndSameTimeBudgetStillApply()
    {
        var noop = new TestRule(_ => [Candidate("same", 0)], (_, _, _) => Planned(0));
        var history = History(7);
        var kernel = Kernel(history, [noop]);
        await kernel.StepAsync(Time(0));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(Time(0)));
        Assert.Equal(1, noop.PlanCalls);
        Assert.Equal(7, kernel.World);
        var advancing = new TestRule(world => [Candidate($"next:{world}", 0)]);
        var budget = Kernel(History(), [advancing], rules: new(42, 2));
        await budget.StepAsync(Time(0));
        await budget.StepAsync(Time(0));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await budget.StepAsync(Time(0)));
        Assert.Equal(2, advancing.PlanCalls);
    }

    [Fact]
    public async Task VersionOverflowFailsBeforePlan()
    {
        var rule = new TestRule(_ => [Candidate("new", 0)]);
        var kernel = Kernel(History(1, Cursor(long.MaxValue, 0, 0, "old")), [rule]);
        await Assert.ThrowsAsync<OverflowException>(async () => await kernel.StepAsync(Time(0)));
        Assert.Equal(0, rule.PlanCalls);
    }

    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(1L, 9L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void CursorRejectsCausalOrdinalImpossibleForCompletedCount(long count, long ordinal)
    {
        Assert.Throws<ArgumentException>(() => Cursor(count, 10, ordinal, "old"));
    }

    [Fact]
    public void CursorAcceptsLastOrdinalOfACompletedSameTimePrefix()
    {
        KernelCursor cursor = Cursor(10, 50, 9, "last");
        cursor.Validate();
        Assert.Equal(9, cursor.LastInstant!.Value.CausalOrdinal);
    }

    [Fact]
    public void CursorAndInitialWorldAreValidatedWithoutHistoryCounts()
    {
        Assert.Throws<ArgumentException>(() => new KernelCursor(new(1, 1), Time(0), null, null));
        Assert.Throws<ArgumentException>(() => new KernelCursor(new(1, 0), Time(0), new(Time(0), 0), Key("bad")));
        Assert.Throws<ArgumentException>(() => new KernelCursor(new(1, 1), Time(2), new(Time(1), 0), Key("bad")));
        Assert.Throws<TestFailure>(() => Kernel(History(), [], validate: _ => throw new TestFailure()));
    }

    private static ModelTime Time(long ticks) => new(ticks);
    private static CandidateKey Key(string value) => CandidateKey.FromUtf8(value);
    private static KernelCursor Cursor(long count = 0, long time = 0, long ordinal = 0, string? cause = null) =>
        new(new(1, count), Time(0), count == 0 ? null : new LogicalInstant(Time(time), ordinal), cause is null ? null : Key(cause));
    private static InMemoryOccurrenceHistory<int, int> History(int state = 0, KernelCursor? cursor = null) => new(state, cursor ?? Cursor());
    private static OccurrenceCandidate<int> Candidate(string key, long time) => new(Key(key), new(Time(time)), 0);
    private static OccurrenceEvent<int> Event(string key, long time, long ordinal, params int[] facts) => new(Key(key), new(Time(time), ordinal), facts);
    private static ValueTask<TransitionDraft<int>> Planned(params int[] facts) => ValueTask.FromResult(new TransitionDraft<int>(facts));
    private static SimulationKernel<int, int, int> Kernel(IOccurrenceHistory<int, int> history, IEnumerable<TestRule> occurrenceRules,
        Func<int, LogicalInstant, int, int>? fold = null, Action<int>? validate = null, SimulationRules? rules = null) =>
        new(history, rules ?? new(42, 100), occurrenceRules, fold ?? ((world, _, fact) => world + fact), validate ?? (_ => { }));

    private sealed class TestRule : IOccurrenceRule<int, int, int>
    {
        public TestRule(Func<int, IReadOnlyList<OccurrenceCandidate<int>>> forecast,
            Func<int, OccurrenceCandidate<int>, CancellationToken, ValueTask<TransitionDraft<int>>>? plan = null)
        {
            Forecast = forecast;
            _plan = plan ?? ((_, _, _) => Planned(1));
        }
        public Func<int, IReadOnlyList<OccurrenceCandidate<int>>> Forecast { get; set; }
        private readonly Func<int, OccurrenceCandidate<int>, CancellationToken, ValueTask<TransitionDraft<int>>> _plan;
        public int ForecastCalls { get; private set; }
        public int PlanCalls { get; private set; }
        IReadOnlyList<OccurrenceCandidate<int>> IOccurrenceRule<int, int, int>.Forecast(int world, SimulationRules rules)
        { ForecastCalls++; return Forecast(world); }
        public ValueTask<TransitionDraft<int>> PlanSelectedAsync(int world, OccurrenceCandidate<int> winner, CancellationToken cancellationToken)
        { PlanCalls++; return _plan(world, winner, cancellationToken); }
    }

    private sealed class ControlledHistory : IOccurrenceHistory<int, int>
    {
        private OccurrenceEvent<int>? _pending;
        public int State { get; set; }
        public KernelCursor Cursor { get; private set; } = SimulationKernelTests.Cursor();
        public OccurrenceEvent<int>? PendingEvent => ThrowPendingGetter ? throw new TestFailure() : _pending;
        public string? FailAt { get; init; }
        public bool SubstituteEvent { get; init; }
        public bool IgnoreState { get; init; }
        public bool ThrowPendingGetter { get; set; }
        public Action? AfterEvent { get; init; }
        public int StateCalls { get; private set; }
        public void CommitEvent(OccurrenceEvent<int> occurrence)
        {
            if (FailAt == "before-e") { throw new IOException(); }
            _pending = SubstituteEvent ? new(occurrence.CauseKey, occurrence.TargetInstant, [99]) : occurrence;
            AfterEvent?.Invoke();
            if (FailAt == "after-e") { throw new IOException(); }
        }
        public void CommitState(int nextState, KernelCursor nextCursor)
        {
            StateCalls++;
            if (FailAt == "before-s") { throw new IOException(); }
            if (!IgnoreState) { State = nextState; }
            Cursor = nextCursor;
            _pending = null;
            if (FailAt == "after-s") { throw new IOException(); }
        }
    }

    private sealed record ValueWorld(int Value);
    private sealed class ReferenceHistory(ValueWorld state) : IOccurrenceHistory<ValueWorld, int>
    {
        public ValueWorld State { get; set; } = state;
        public KernelCursor Cursor => SimulationKernelTests.Cursor();
        public OccurrenceEvent<int>? PendingEvent => null;
        public void CommitEvent(OccurrenceEvent<int> occurrence) => throw new TestFailure();
        public void CommitState(ValueWorld nextState, KernelCursor nextCursor) => throw new TestFailure();
    }
    private sealed class TestFailure : Exception;
}

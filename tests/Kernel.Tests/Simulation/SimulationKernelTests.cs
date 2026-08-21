using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SimulationKernelTests
{
    [Fact]
    public async Task StepAsync_EmptyForecastReturnsExhaustedWithoutChangingCommittedState()
    {
        var rule = Rule<int, string, int>(forecast: (_, _) => []);
        var journal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> kernel = Kernel(7, [rule], journal);

        StepStatus status = await kernel.StepAsync(new ModelTime(100));

        Assert.Equal(StepStatus.Exhausted, status);
        Assert.Equal(7, kernel.World);
        Assert.Equal(new WorldVersion(1, 0), kernel.Version);
        Assert.Null(kernel.LastCommittedInstant);
        Assert.Equal(ModelTime.Zero, kernel.CurrentModelTime);
        Assert.Empty(journal.Batches);
        Assert.Equal(0, rule.PlanCallCount);
    }

    [Fact]
    public async Task StepAsync_WinnerAfterBoundaryDoesNotPlanAndEqualBoundaryCanCommit()
    {
        OccurrenceCandidate<string> candidate = Candidate("future", due: 10, "fact");
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [candidate],
            plan: (_, _, _) => Planned(1));
        var journal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> kernel = Kernel(
            0, [rule], journal, fold: (world, _, fact) => world + fact);

        StepStatus before = await kernel.StepAsync(new ModelTime(9));

        Assert.Equal(StepStatus.BoundaryReached, before);
        Assert.Equal(0, rule.PlanCallCount);
        Assert.Equal(ModelTime.Zero, kernel.CurrentModelTime);
        Assert.Empty(journal.Batches);

        StepStatus atBoundary = await kernel.StepAsync(new ModelTime(10));

        Assert.Equal(StepStatus.Committed, atBoundary);
        Assert.Equal(1, rule.PlanCallCount);
        Assert.Equal(1, kernel.World);
        Assert.Single(journal.Batches);
    }

    [Fact]
    public async Task StepAsync_CommitsAtMostOneAndFullReforecastsNextStep()
    {
        var rule = Rule<int, int, int>(
            forecast: (world, _) => world < 2
                ? [Candidate($"count:{world}", due: 5, world)]
                : [],
            plan: (_, winner, _) => Planned(winner.Data + 1));
        var journal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, int, int> kernel = Kernel(0, [rule], journal, fold: (_, _, fact) => fact);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(5)));
        Assert.Equal(1, kernel.World);
        Assert.Single(journal.Batches);
        Assert.Equal(1, rule.ForecastCallCount);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(5)));
        Assert.Equal(2, kernel.World);
        Assert.Equal(2, journal.Batches.Count);
        Assert.Equal(2, rule.ForecastCallCount);
    }

    [Fact]
    public async Task StepAsync_GlobalWinnerInvokesOnlyItsOwningRule()
    {
        OccurrenceCandidate<string> firstCandidate = Candidate("first", due: 10, "first");
        OccurrenceCandidate<string> secondCandidate = Candidate("second", due: 10, "second");
        var first = Rule<int, string, string>(
            forecast: (_, _) => [firstCandidate],
            plan: (_, winner, _) => Planned(winner.Data));
        var second = Rule<int, string, string>(
            forecast: (_, _) => [secondCandidate],
            plan: (_, winner, _) => Planned(winner.Data));
        var journal = new InMemoryJournal<string>(lineageId: 1);
        var rules = new SimulationRules(worldSeed: 42, maxTransitionsPerModelTime: 10);
        CandidateKey expectedWinner = OccurrenceScheduler.SelectWinner(
            [firstCandidate, secondCandidate], rules.WorldSeed).Key;
        SimulationKernel<int, string, string> kernel = Kernel(
            0,
            [second, first],
            journal,
            simulationRules: rules,
            fold: (world, _, _) => world + 1);

        await kernel.StepAsync(new ModelTime(10));

        Assert.Equal(expectedWinner, journal.Batches[0].CauseKey);
        Assert.Equal(expectedWinner == firstCandidate.Key ? 1 : 0, first.PlanCallCount);
        Assert.Equal(expectedWinner == secondCandidate.Key ? 1 : 0, second.PlanCallCount);
    }

    [Fact]
    public async Task StepAsync_RuleRegistrationOrderProducesIdenticalOwnerFactAndBatch()
    {
        async Task<(string World, CandidateKey Cause, string Fact, LogicalInstant Instant, int APlans, int BPlans)>
            RunAsync(bool reverse)
        {
            OccurrenceCandidate<string> candidateA = Candidate("rule:a", due: 10, "fact-a");
            OccurrenceCandidate<string> candidateB = Candidate("rule:b", due: 10, "fact-b");
            var ruleA = Rule<string, string, string>(
                forecast: (_, _) => [candidateA],
                plan: (_, winner, _) => Planned(winner.Data));
            var ruleB = Rule<string, string, string>(
                forecast: (_, _) => [candidateB],
                plan: (_, winner, _) => Planned(winner.Data));
            IOccurrenceRule<string, string, string>[] registered = reverse
                ? [ruleB, ruleA]
                : [ruleA, ruleB];
            var journal = new InMemoryJournal<string>(lineageId: 1);
            SimulationKernel<string, string, string> kernel = Kernel(
                string.Empty,
                registered,
                journal,
                fold: (world, _, fact) => world + fact,
                simulationRules: new SimulationRules(123, 10));

            await kernel.StepAsync(new ModelTime(10));
            JournalBatch<string> batch = Assert.Single(journal.Batches);
            return (
                kernel.World,
                batch.CauseKey,
                Assert.Single(batch.Facts),
                batch.Instant,
                ruleA.PlanCallCount,
                ruleB.PlanCallCount);
        }

        Assert.Equal(await RunAsync(reverse: false), await RunAsync(reverse: true));
    }

    [Fact]
    public async Task StepAsync_DuplicateKeyOrPastDueCandidateFailsBeforePlan()
    {
        var duplicateOne = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("duplicate", due: 10, "a")]);
        var duplicateTwo = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("duplicate", due: 11, "b")]);
        var duplicateJournal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> duplicateKernel = Kernel(
            0,
            [duplicateOne, duplicateTwo],
            duplicateJournal);

        InvalidOperationException duplicateError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await duplicateKernel.StepAsync(new ModelTime(20)));
        Assert.Contains("Duplicate candidate key", duplicateError.Message);
        Assert.Equal(0, duplicateOne.PlanCallCount + duplicateTwo.PlanCallCount);
        Assert.Empty(duplicateJournal.Batches);

        var prefixJournal = new InMemoryJournal<int>(lineageId: 1);
        prefixJournal.AppendBatch(new JournalBatch<int>(
            new LogicalInstant(new ModelTime(10), 0),
            CandidateKey.FromUtf8("prefix"),
            [1]));
        var pastRule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("past", due: 9, "past")]);
        SimulationKernel<int, string, int> pastKernel = Kernel(
            1,
            [pastRule],
            prefixJournal,
            version: new WorldVersion(1, 1),
            lastCommittedInstant: new LogicalInstant(new ModelTime(10), 0));

        InvalidOperationException pastError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pastKernel.StepAsync(new ModelTime(20)));
        Assert.Contains("before current model time", pastError.Message);
        Assert.Equal(0, pastRule.PlanCallCount);
        Assert.Single(prefixJournal.Batches);
    }

    [Fact]
    public async Task StepAsync_PlanInFlightRejectsSecondStepImmediately()
    {
        var completion = new TaskCompletionSource<TransitionDraft<int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("blocked", due: 1, "blocked")],
            plan: (_, _, _) => new ValueTask<TransitionDraft<int>>(completion.Task));
        var journal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> kernel = Kernel(
            0, [rule], journal, fold: (world, _, fact) => world + fact);

        ValueTask<StepStatus> firstStep = kernel.StepAsync(new ModelTime(1));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            kernel.StepAsync(new ModelTime(1)));
        Assert.Contains("already in flight", error.Message);
        completion.SetResult(Draft(1));
        Assert.Equal(StepStatus.Committed, await firstStep);
    }

    [Fact]
    public async Task StepAsync_MultipleFactsShareOneInstantAndAdvanceVersionOnceInArrayOrder()
    {
        var observed = new List<(LogicalInstant Instant, string Fact)>();
        var validatedWorlds = new List<string>();
        var rule = Rule<string, string, string>(
            forecast: (_, _) => [Candidate("multi", due: 5, "multi")],
            plan: (_, _, _) => Planned("A", "B", "C"));
        var journal = new InMemoryJournal<string>(lineageId: 1);
        SimulationKernel<string, string, string> kernel = Kernel(
            string.Empty,
            [rule],
            journal,
            fold: (world, instant, fact) =>
            {
                observed.Add((instant, fact));
                return world + fact;
            },
            validate: world =>
            {
                Assert.True(world.Length is 0 or 3);
                validatedWorlds.Add(world);
            });

        await kernel.StepAsync(new ModelTime(5));

        JournalBatch<string> batch = Assert.Single(journal.Batches);
        Assert.Equal("ABC", kernel.World);
        Assert.Equal(["A", "B", "C"], batch.Facts);
        Assert.All(observed, item => Assert.Equal(batch.Instant, item.Instant));
        Assert.Equal([string.Empty, "ABC"], validatedWorlds);
        Assert.Equal(new WorldVersion(1, 1), kernel.Version);
        Assert.Equal(new LogicalInstant(new ModelTime(5), 0), kernel.LastCommittedInstant);
    }

    [Fact]
    public async Task StepAsync_FoldOrValidationFailureLeavesEveryCommittedAuthorityUnchanged()
    {
        var foldRule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("fold", due: 1, "fold")],
            plan: (_, _, _) => Planned(1, 2));
        var foldJournal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> foldKernel = Kernel(
            0,
            [foldRule],
            foldJournal,
            fold: (world, _, fact) => fact == 2
                ? throw new InvalidOperationException("fold failed")
                : world + fact);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await foldKernel.StepAsync(new ModelTime(1)));
        AssertUnchanged(foldKernel, foldJournal, expectedWorld: 0);

        var validateRule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("validate", due: 1, "validate")],
            plan: (_, _, _) => Planned(1));
        var validateJournal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> validateKernel = Kernel(
            0,
            [validateRule],
            validateJournal,
            fold: (world, _, fact) => world + fact,
            validate: world =>
            {
                if (world != 0)
                {
                    throw new InvalidOperationException("invalid world");
                }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await validateKernel.StepAsync(new ModelTime(1)));
        AssertUnchanged(validateKernel, validateJournal, expectedWorld: 0);
    }

    [Fact]
    public async Task StepAsync_CancellationAfterPlanButBeforePublishLeavesCommittedStateUnchanged()
    {
        var completion = new TaskCompletionSource<TransitionDraft<int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("cancel", due: 1, "cancel")],
            plan: (_, _, _) => new ValueTask<TransitionDraft<int>>(completion.Task));
        var journal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> kernel = Kernel(
            0, [rule], journal, fold: (world, _, fact) => world + fact);
        using var cancellation = new CancellationTokenSource();

        ValueTask<StepStatus> step = kernel.StepAsync(new ModelTime(1), cancellation.Token);
        cancellation.Cancel();
        completion.SetResult(Draft(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await step);
        AssertUnchanged(kernel, journal, expectedWorld: 0);
    }

    [Fact]
    public async Task StepAsync_AnyAppendExceptionLeavesMemoryUninstalledAndPermanentlyRequiresReplay()
    {
        var journal = new ControllableJournal<int> { ThrowBeforePublish = true };
        var rule = Rule<int, string, int>(
            forecast: (world, _) => world == 0 ? [Candidate("append", 1, "append")] : [],
            plan: (_, _, _) => Planned(1));
        SimulationKernel<int, string, int> kernel = Kernel(
            0, [rule], journal, fold: (world, _, fact) => world + fact);

        InvalidOperationException publicationError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.StepAsync(new ModelTime(1)));
        Assert.Contains("outcome cannot be safely determined", publicationError.Message);
        Assert.IsType<IOException>(publicationError.InnerException);
        AssertUnchanged(kernel, journal, expectedWorld: 0);

        journal.ThrowBeforePublish = false;
        InvalidOperationException stopped = Assert.Throws<InvalidOperationException>(() =>
            kernel.StepAsync(new ModelTime(1)));
        Assert.Contains("permanently requires Replay", stopped.Message);
    }

    [Fact]
    public async Task StepAsync_SinkThatPublishesThenThrowsPermanentlyRequiresReplay()
    {
        var journal = new ControllableJournal<int> { ThrowAfterPublish = true };
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("uncertain", 1, "uncertain")],
            plan: (_, _, _) => Planned(1));
        SimulationKernel<int, string, int> kernel = Kernel(
            0, [rule], journal, fold: (world, _, fact) => world + fact);

        InvalidOperationException publicationError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.StepAsync(new ModelTime(1)));

        Assert.Contains("must be rebuilt by Replay", publicationError.Message);
        Assert.Single(journal.Batches);
        Assert.Equal(0, kernel.World);
        Assert.Equal(new WorldVersion(1, 0), kernel.Version);
        Assert.Null(kernel.LastCommittedInstant);

        InvalidOperationException stopped = Assert.Throws<InvalidOperationException>(() =>
            kernel.StepAsync(new ModelTime(1)));
        Assert.Contains("permanently requires Replay", stopped.Message);
    }

    [Fact]
    public async Task StepAsync_SinkThatReturnsWithDifferentFactsPermanentlyRequiresReplay()
    {
        var journal = new ControllableJournal<int>
        {
            BatchTransform = batch => new JournalBatch<int>(
                batch.Instant,
                batch.CauseKey,
                [99]),
        };
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("substituted", 1, "substituted")],
            plan: (_, _, _) => Planned(1));
        SimulationKernel<int, string, int> kernel = Kernel(
            0, [rule], journal, fold: (world, _, fact) => world + fact);

        InvalidOperationException publicationError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.StepAsync(new ModelTime(1)));

        Assert.Contains("must be rebuilt by Replay", publicationError.Message);
        Assert.Equal([99], Assert.Single(journal.Batches).Facts);
        Assert.Equal(0, kernel.World);
        Assert.Equal(new WorldVersion(1, 0), kernel.Version);
        Assert.Throws<InvalidOperationException>(() => kernel.StepAsync(new ModelTime(1)));
    }

    [Fact]
    public async Task StepAsync_DoesNotObserveCancellationAfterAppendPublishes()
    {
        using var cancellation = new CancellationTokenSource();
        var journal = new ControllableJournal<int> { AfterPublish = cancellation.Cancel };
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("commit", 1, "commit")],
            plan: (_, _, _) => Planned(1));
        SimulationKernel<int, string, int> kernel = Kernel(
            0, [rule], journal, fold: (world, _, fact) => world + fact);

        StepStatus status = await kernel.StepAsync(new ModelTime(1), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(StepStatus.Committed, status);
        Assert.Equal(1, kernel.World);
        Assert.Equal(1, kernel.Version.TransitionCount);
    }

    [Fact]
    public async Task StepAsync_FirstNoOpCanCommitButRepeatedHeadKeyFailsBeforePlan()
    {
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("noop", 0, "noop")],
            plan: (_, _, _) => Planned(0));
        var journal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, string, int> kernel = Kernel(
            7,
            [rule],
            journal,
            fold: (world, _, _) => world);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        Assert.Equal(7, kernel.World);
        Assert.Equal(new WorldVersion(1, 1), kernel.Version);
        Assert.Single(journal.Batches);
        Assert.Equal(1, rule.PlanCallCount);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.StepAsync(ModelTime.Zero));

        Assert.Contains("repeated immediately", error.Message);
        Assert.Equal(1, rule.PlanCallCount);
        Assert.Single(journal.Batches);
        Assert.Equal(new WorldVersion(1, 1), kernel.Version);
    }

    [Fact]
    public async Task StepAsync_SameTimeBudgetAllowsConfiguredCountThenStopsDeterministically()
    {
        var rule = Rule<int, int, int>(
            forecast: (world, _) => [Candidate($"same:{world}", 0, world)],
            plan: (_, _, _) => Planned(1));
        var journal = new InMemoryJournal<int>(lineageId: 1);
        SimulationKernel<int, int, int> kernel = Kernel(
            0,
            [rule],
            journal,
            simulationRules: new SimulationRules(42, maxTransitionsPerModelTime: 2),
            fold: (world, _, fact) => world + fact);

        await kernel.StepAsync(ModelTime.Zero);
        await kernel.StepAsync(ModelTime.Zero);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.StepAsync(ModelTime.Zero));

        Assert.Contains("Transition budget of 2 exhausted", error.Message);
        Assert.Equal([0L, 1L], journal.Batches.Select(batch => batch.Instant.CausalOrdinal));
        Assert.Equal(2, kernel.World);
        Assert.Equal(2, kernel.Version.TransitionCount);
        Assert.Equal(2, rule.PlanCallCount);
    }

    [Fact]
    public async Task StepAsync_CausalOrdinalOverflowFailsBeforePlanOrPublish()
    {
        LogicalInstant priorInstant = new(ModelTime.Zero, long.MaxValue);
        var journal = new InMemoryJournal<int>(lineageId: 1);
        journal.AppendBatch(new JournalBatch<int>(
            priorInstant,
            CandidateKey.FromUtf8("prior"),
            [1]));
        var rule = Rule<int, string, int>(
            forecast: (_, _) => [Candidate("next", 0, "next")],
            plan: (_, _, _) => Planned(1));
        SimulationKernel<int, string, int> kernel = Kernel(
            1,
            [rule],
            journal,
            version: new WorldVersion(1, 1),
            lastCommittedInstant: priorInstant,
            simulationRules: new SimulationRules(42, int.MaxValue));

        await Assert.ThrowsAsync<OverflowException>(
            async () => await kernel.StepAsync(ModelTime.Zero));

        Assert.Equal(0, rule.PlanCallCount);
        Assert.Single(journal.Batches);
        Assert.Equal(new WorldVersion(1, 1), kernel.Version);
    }

    [Fact]
    public void Constructor_RequiresAConsistentCommittedBatchBoundary()
    {
        var journal = new InMemoryJournal<int>(lineageId: 1);
        var rule = Rule<int, string, int>(forecast: (_, _) => []);

        Assert.Throws<ArgumentException>(() => Kernel(
            0,
            [rule],
            journal,
            version: new WorldVersion(1, 1)));
        Assert.Throws<ArgumentException>(() => Kernel(
            0,
            [rule],
            journal,
            lastCommittedInstant: new LogicalInstant(ModelTime.Zero, 0)));
        var otherLineageJournal = new InMemoryJournal<int>(lineageId: 2);
        Assert.Throws<ArgumentException>(() => Kernel(
            0,
            [rule],
            otherLineageJournal,
            version: new WorldVersion(1, 0)));
        Assert.Throws<InvalidOperationException>(() => Kernel(
            0,
            [rule],
            journal,
            validate: _ => throw new InvalidOperationException("invalid committed world")));
    }

    private static void AssertUnchanged<TData>(
        SimulationKernel<int, TData, int> kernel,
        IJournalSink<int> journal,
        int expectedWorld)
    {
        Assert.Equal(expectedWorld, kernel.World);
        Assert.Equal(new WorldVersion(1, 0), kernel.Version);
        Assert.Null(kernel.LastCommittedInstant);
        Assert.Equal(ModelTime.Zero, kernel.CurrentModelTime);
        Assert.Empty(journal.Batches);
    }

    private static SimulationKernel<TWorld, TData, TFact> Kernel<TWorld, TData, TFact>(
        TWorld world,
        IEnumerable<IOccurrenceRule<TWorld, TData, TFact>> rules,
        IJournalSink<TFact> journal,
        Func<TWorld, LogicalInstant, TFact, TWorld>? fold = null,
        Action<TWorld>? validate = null,
        SimulationRules? simulationRules = null,
        WorldVersion? version = null,
        LogicalInstant? lastCommittedInstant = null) =>
        new(
            world,
            version ?? new WorldVersion(1, journal.Batches.Count),
            ModelTime.Zero,
            lastCommittedInstant,
            simulationRules ?? new SimulationRules(42, 100),
            rules,
            journal,
            fold ?? ((current, _, _) => current),
            validate ?? (_ => { }));

    private static DelegateRule<TWorld, TData, TFact> Rule<TWorld, TData, TFact>(
        Func<TWorld, SimulationRules, IReadOnlyList<OccurrenceCandidate<TData>>> forecast,
        Func<TWorld, OccurrenceCandidate<TData>, CancellationToken, ValueTask<TransitionDraft<TFact>>>? plan = null) =>
        new(forecast, plan ?? ((_, _, _) => throw new InvalidOperationException("Plan was not expected.")));

    private static OccurrenceCandidate<TData> Candidate<TData>(
        string key,
        long due,
        TData data) =>
        new(CandidateKey.FromUtf8(key), new CandidateDue(new ModelTime(due)), data);

    private static TransitionDraft<TFact> Draft<TFact>(params TFact[] facts) => new(facts);

    private static ValueTask<TransitionDraft<TFact>> Planned<TFact>(params TFact[] facts) =>
        ValueTask.FromResult(Draft(facts));

    private sealed class DelegateRule<TWorld, TData, TFact> :
        IOccurrenceRule<TWorld, TData, TFact>
    {
        private readonly Func<
            TWorld,
            SimulationRules,
            IReadOnlyList<OccurrenceCandidate<TData>>> _forecast;
        private readonly Func<
            TWorld,
            OccurrenceCandidate<TData>,
            CancellationToken,
            ValueTask<TransitionDraft<TFact>>> _plan;

        public DelegateRule(
            Func<TWorld, SimulationRules, IReadOnlyList<OccurrenceCandidate<TData>>> forecast,
            Func<TWorld, OccurrenceCandidate<TData>, CancellationToken, ValueTask<TransitionDraft<TFact>>> plan)
        {
            _forecast = forecast;
            _plan = plan;
        }

        public int ForecastCallCount { get; private set; }

        public int PlanCallCount { get; private set; }

        public IReadOnlyList<OccurrenceCandidate<TData>> Forecast(
            TWorld world,
            SimulationRules rules)
        {
            ForecastCallCount++;
            return _forecast(world, rules);
        }

        public ValueTask<TransitionDraft<TFact>> PlanSelectedAsync(
            TWorld world,
            OccurrenceCandidate<TData> winner,
            CancellationToken cancellationToken)
        {
            PlanCallCount++;
            return _plan(world, winner, cancellationToken);
        }
    }

    private sealed class ControllableJournal<TFact> : IJournalSink<TFact>
    {
        private readonly List<JournalBatch<TFact>> _batches = [];

        public long LineageId { get; init; } = 1;

        public IReadOnlyList<JournalBatch<TFact>> Batches => _batches;

        public bool ThrowBeforePublish { get; set; }

        public bool ThrowAfterPublish { get; set; }

        public Action? AfterPublish { get; set; }

        public Func<JournalBatch<TFact>, JournalBatch<TFact>>? BatchTransform { get; set; }

        public void AppendBatch(JournalBatch<TFact> batch)
        {
            if (ThrowBeforePublish)
            {
                throw new IOException("pre-publication failure");
            }

            _batches.Add(BatchTransform?.Invoke(batch) ?? batch);
            AfterPublish?.Invoke();
            if (ThrowAfterPublish)
            {
                throw new IOException("post-publication failure");
            }
        }
    }
}

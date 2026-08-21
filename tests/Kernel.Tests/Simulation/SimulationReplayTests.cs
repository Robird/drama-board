using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Tests.ToyModels;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SimulationReplayTests
{
    [Fact]
    public void Replay_FoldsWholeBatchesInFactOrderAndCountsTransitionsNotFacts()
    {
        JournalBatch<string>[] batches =
        [
            Batch(10, 0, "first", "A", "B"),
            Batch(10, 1, "second", "C"),
        ];
        var validatedWorlds = new List<string>();

        ReplayResult<string> result = SimulationReplay.Replay(
            genesisWorld: string.Empty,
            lineageId: 7,
            genesisTime: ModelTime.Zero,
            batches,
            fold: (world, instant, fact) => world + fact,
            validate: world => validatedWorlds.Add(world));

        Assert.Equal("ABC", result.World);
        Assert.Equal(new WorldVersion(7, 2), result.Version);
        Assert.Equal(new LogicalInstant(new ModelTime(10), 1), result.LastCommittedInstant);
        Assert.Equal(new ModelTime(10), result.CurrentModelTime);
        Assert.Equal(["AB", "ABC"], validatedWorlds);
    }

    [Fact]
    public void Replay_EmptyHistoryReturnsGenesisBoundary()
    {
        ReplayResult<int> result = SimulationReplay.Replay<int, string>(
            genesisWorld: 5,
            lineageId: 9,
            genesisTime: new ModelTime(3),
            batches: [],
            fold: (world, _, _) => world,
            validate: _ => { });

        Assert.Equal(5, result.World);
        Assert.Equal(new WorldVersion(9, 0), result.Version);
        Assert.Null(result.LastCommittedInstant);
        Assert.Equal(new ModelTime(3), result.CurrentModelTime);
    }

    [Theory]
    [MemberData(nameof(InvalidInstantSequences))]
    public void Replay_InvalidCausalInstantSequenceIsRejected(JournalBatch<string>[] batches)
    {
        Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay(
            string.Empty,
            lineageId: 1,
            genesisTime: ModelTime.Zero,
            batches,
            fold: (world, _, fact) => world + fact,
            validate: _ => { }));
    }

    public static TheoryData<JournalBatch<string>[]> InvalidInstantSequences
    {
        get
        {
            var data = new TheoryData<JournalBatch<string>[]>();
            data.Add([Batch(10, 1, "first-not-zero", "A")]);
            data.Add([Batch(10, 0, "a", "A"), Batch(10, 2, "gap", "B")]);
            data.Add([Batch(10, 0, "a", "A"), Batch(11, 1, "new-time-not-zero", "B")]);
            data.Add([Batch(10, 0, "a", "A"), Batch(9, 0, "decrease", "B")]);
            data.Add([Batch(-1, 0, "before-genesis", "A")]);
            return data;
        }
    }

    [Fact]
    public async Task Fork_CopiesCommittedPrefixIntoDistinctLineageAndContinuesIndependently()
    {
        TimerWorld genesis = TimerWorld.Start(
            new TimerEntity(1, "A", new ModelTime(10)),
            new TimerEntity(2, "B", new ModelTime(20)),
            new TimerEntity(3, "C", new ModelTime(30)));
        var sourceRule = new TimerRule();
        var sourceJournal = new InMemoryJournal<TimerFact>(lineageId: 1);
        var simulationRules = new SimulationRules(42, 100);
        SimulationKernel<TimerWorld, string, TimerFact> source =
            TimerModel.CreateKernel(genesis, sourceRule, sourceJournal, simulationRules);
        await source.StepAsync(new ModelTime(30));
        await source.StepAsync(new ModelTime(30));
        await source.StepAsync(new ModelTime(30));

        InMemoryForkResult<TimerWorld, TimerFact> fork = SimulationFork.Create(
            genesis,
            ModelTime.Zero,
            sourceJournal,
            prefixTransitionCount: 2,
            newLineageId: 99,
            simulationRules,
            TimerModel.Fold,
            TimerModel.Validate);

        Assert.Equal(new WorldVersion(99, 2), fork.Replay.Version);
        Assert.Equal(["A", "B"], fork.Replay.World.FiredTimers);
        Assert.Equal(sourceJournal.Batches[1].Instant, fork.Replay.LastCommittedInstant);
        Assert.Equal(simulationRules, fork.SimulationRules);
        Assert.Equal(2, fork.Journal.Batches.Count);

        var forkRule = new TimerRule();
        SimulationKernel<TimerWorld, string, TimerFact> forkKernel = TimerModel.CreateKernel(
            fork.Replay.World,
            forkRule,
            fork.Journal,
            fork.SimulationRules,
            fork.Replay.Version,
            fork.Replay.LastCommittedInstant);
        await forkKernel.StepAsync(new ModelTime(30));

        Assert.Equal(3, fork.Journal.Batches.Count);
        Assert.Equal(3, sourceJournal.Batches.Count);
        Assert.NotSame(sourceJournal, fork.Journal);
        Assert.Throws<ArgumentException>(() => SimulationFork.Create(
            genesis,
            ModelTime.Zero,
            sourceJournal,
            prefixTransitionCount: 1,
            newLineageId: 1,
            simulationRules,
            TimerModel.Fold,
            TimerModel.Validate));
    }

    [Fact]
    public async Task SchedulerConformance_RecomputesWinnersWithoutCallingPlan()
    {
        TimerWorld genesis = TimerWorld.Start(
            new TimerEntity(1, "A", new ModelTime(10)),
            new TimerEntity(2, "B", new ModelTime(10)),
            new TimerEntity(3, "C", new ModelTime(20)));
        var liveRule = new TimerRule(reverseForecast: true);
        var journal = new InMemoryJournal<TimerFact>(lineageId: 1);
        var rules = new SimulationRules(73, 100);
        SimulationKernel<TimerWorld, string, TimerFact> kernel =
            TimerModel.CreateKernel(genesis, liveRule, journal, rules);
        while (await kernel.StepAsync(new ModelTime(20)) == StepStatus.Committed)
        {
        }

        var forwardAuditRule = new TimerRule(reverseForecast: false, throwIfPlanCalled: true);
        var reverseAuditRule = new TimerRule(reverseForecast: true, throwIfPlanCalled: true);
        ReplayResult<TimerWorld> forward = SchedulerConformance.Verify(
            genesis,
            lineageId: 1,
            genesisTime: ModelTime.Zero,
            rules,
            [forwardAuditRule],
            journal.Batches,
            TimerModel.Fold,
            TimerModel.Validate);
        ReplayResult<TimerWorld> reverse = SchedulerConformance.Verify(
            genesis,
            lineageId: 1,
            genesisTime: ModelTime.Zero,
            rules,
            [reverseAuditRule],
            journal.Batches,
            TimerModel.Fold,
            TimerModel.Validate);

        Assert.Equal(kernel.World.FiredTimers, forward.World.FiredTimers);
        Assert.Equal(forward.World.FiredTimers, reverse.World.FiredTimers);
        Assert.Equal(forward.Version, reverse.Version);
        Assert.Equal(forward.LastCommittedInstant, reverse.LastCommittedInstant);
        Assert.Equal(forward.CurrentModelTime, reverse.CurrentModelTime);
        Assert.Equal(kernel.Version, forward.Version);
        Assert.Equal(0, forwardAuditRule.PlanCallCount);
        Assert.Equal(0, reverseAuditRule.PlanCallCount);
        Assert.Equal(journal.Batches.Count, forwardAuditRule.ForecastCallCount);
        Assert.Equal(journal.Batches.Count, reverseAuditRule.ForecastCallCount);
    }

    [Fact]
    public async Task SchedulerConformance_TamperedCauseOrDueIsRejected()
    {
        TimerWorld genesis = TimerWorld.Start(new TimerEntity(1, "A", new ModelTime(10)));
        var liveRule = new TimerRule();
        var journal = new InMemoryJournal<TimerFact>(lineageId: 1);
        var rules = new SimulationRules(42, 100);
        SimulationKernel<TimerWorld, string, TimerFact> kernel =
            TimerModel.CreateKernel(genesis, liveRule, journal, rules);
        await kernel.StepAsync(new ModelTime(10));
        JournalBatch<TimerFact> committed = journal.Batches[0];
        var wrongCause = new JournalBatch<TimerFact>(
            committed.Instant,
            CandidateKey.FromUtf8("wrong"),
            committed.Facts);
        var wrongDue = new JournalBatch<TimerFact>(
            new LogicalInstant(new ModelTime(11), 0),
            committed.CauseKey,
            committed.Facts);

        Assert.Throws<InvalidOperationException>(() => SchedulerConformance.Verify(
            genesis, 1, ModelTime.Zero, rules, [new TimerRule()], [wrongCause],
            TimerModel.Fold, TimerModel.Validate));
        Assert.Throws<InvalidOperationException>(() => SchedulerConformance.Verify(
            genesis, 1, ModelTime.Zero, rules, [new TimerRule()], [wrongDue],
            TimerModel.Fold, TimerModel.Validate));
    }

    [Fact]
    public void SchedulerConformance_RejectsAdjacentSameCauseWhileOrdinaryReplayDoesNotAuditIt()
    {
        TimerWorld genesis = TimerWorld.Start(new TimerEntity(1, "A", new ModelTime(10)));
        CandidateKey repeatedKey = CandidateKey.FromUtf8("timer:1");
        JournalBatch<TimerFact>[] batches =
        [
            new(
                new LogicalInstant(new ModelTime(10), 0),
                repeatedKey,
                [new TimerFact("A")]),
            new(
                new LogicalInstant(new ModelTime(10), 1),
                repeatedKey,
                [new TimerFact("A")]),
        ];

        ReplayResult<TimerWorld> ordinary = SimulationReplay.Replay(
            genesis,
            lineageId: 1,
            genesisTime: ModelTime.Zero,
            batches,
            TimerModel.Fold,
            validate: _ => { });
        InvalidOperationException conformanceError = Assert.Throws<InvalidOperationException>(() =>
            SchedulerConformance.Verify(
                genesis,
                lineageId: 1,
                genesisTime: ModelTime.Zero,
                new SimulationRules(42, 100),
                [new TimerRule(throwIfPlanCalled: true)],
                batches,
                TimerModel.Fold,
                validate: _ => { }));

        Assert.Equal(["A", "A"], ordinary.World.FiredTimers);
        Assert.Contains("adjacent transitions", conformanceError.Message);
    }

    private static JournalBatch<string> Batch(
        long modelTime,
        long causalOrdinal,
        string cause,
        params string[] facts) =>
        new(
            new LogicalInstant(new ModelTime(modelTime), causalOrdinal),
            CandidateKey.FromUtf8(cause),
            facts);
}

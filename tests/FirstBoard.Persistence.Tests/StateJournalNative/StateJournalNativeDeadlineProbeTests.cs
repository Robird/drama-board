using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

public sealed class StateJournalNativeDeadlineProbeTests
{
    private const long LineageId = 73_001;
    private const long LeftLineageId = 73_101;
    private const long RightLineageId = 73_102;
    private const long NonzeroForkLineageId = 73_103;
    private const long FailedForkLineageId = 73_104;
    private const long ResumedForkLineageId = 73_105;
    private const ulong WorldSeed = 42;

    [Fact]
    public async Task Deadline_CoCommitsGraphFrontierAndSummary_ThenReopensDirectly()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "source");
        DeadlineProbeCommitReceipt receipt;
        DeadlineProbeSemanticSnapshot committed;

        using (DeadlineProbeSession session = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            var genesisVersion = new WorldVersion(LineageId, 0);
            Assert.Equal(genesisVersion, session.Head.Version);
            Assert.False(session.Head.CellarSealed);
            Assert.Equal(
                new PassageEntryAccess(true, true),
                session.Head.GetPassageEntryAccess(
                    new PassageId(BoardIds.CellarGatePassage)));
            AssertGenesis(session.Snapshot(), genesisVersion);

            DeadlineTransactionProbeV1 transaction = DeadlineProbePlanner.Plan(session.Head);
            await AssertMatchesCurrentRuleAsync(instance, transaction);
            DeadlineProbeView expiredView = session.Head;
            receipt = session.Commit(transaction);
            committed = session.Snapshot();

            Assert.Equal(receipt.Parent, session.HeadParentAddress);
            Assert.Equal(receipt.Head, session.HeadAddress);
            Assert.NotEqual(receipt.Parent, receipt.Head);
            Assert.Throws<InvalidOperationException>(() => _ = expiredView.CellarSealed);
            AssertCommittedDeadline(committed, transaction, parentWorldVersion: null);
        }

        using DeadlineProbeSession reopened = DeadlineProbeSession.Open(repositoryPath, instance);
        Assert.Equal(committed, reopened.Snapshot());
        Assert.Equal(receipt.Head, reopened.HeadAddress);
        Assert.Equal(receipt.Parent, reopened.HeadParentAddress);
    }

    [Theory]
    [InlineData(DeadlineProbeFault.AfterFirstFact)]
    [InlineData(DeadlineProbeFault.AtBatchEndValidation)]
    internal void WorkingFailure_PoisonsSession_AndReopenReturnsExactParent(
        DeadlineProbeFault fault)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "failure");
        var expectedVersion = new WorldVersion(LineageId, 0);
        Atelia.StateJournal.CommitAddress expectedParent;
        DeadlineProbeSemanticSnapshot expectedParentSnapshot;

        using (DeadlineProbeSession session = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            DeadlineTransactionProbeV1 proposed = DeadlineProbePlanner.Plan(session.Head);
            expectedParent = session.HeadAddress;
            expectedParentSnapshot = session.Snapshot();
            DeadlineProbeView expiredView = session.Head;

            Assert.Throws<InvalidOperationException>(() => session.Commit(proposed, fault));
            Assert.Throws<InvalidOperationException>(() => _ = expiredView.CellarSealed);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        using DeadlineProbeSession reopened = DeadlineProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(expectedParent, reopened.HeadAddress);
        Assert.Equal(expectedParentSnapshot, reopened.Snapshot());
        AssertGenesis(reopened.Snapshot(), expectedVersion);
    }

    [Fact]
    public void ReflogFailure_AfterPrimaryPublication_ResolvesExactCommittedChild()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "ambiguous");
        string reflogPath = Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            "main.reflog.jsonl");
        DeadlineProbeCommitFailedException failure;
        DeadlineTransactionProbeV1 transaction;

        using (DeadlineProbeSession session = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            transaction = DeadlineProbePlanner.Plan(session.Head);
            Atelia.StateJournal.CommitAddress expectedParent = session.HeadAddress;

            File.Delete(reflogPath);
            Directory.CreateDirectory(reflogPath);
            failure = Assert.Throws<DeadlineProbeCommitFailedException>(
                () => session.Commit(transaction));

            Assert.Equal(expectedParent, failure.ExpectedParent);
            Assert.Equal(
                DeadlineProbeOperationKind.ObjectiveTransition,
                failure.OperationKind);
            Assert.Equal(DeadlineProbeSession.MainBranch, failure.BranchName);
            Assert.Empty(failure.ParentAuthority.PassageOverrides);
            Assert.Single(failure.ChildAuthority.PassageOverrides);
            AssertStructuredCommitMetadata(failure);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        Directory.Delete(reflogPath);
        DeadlineProbeResolution resolution = DeadlineProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            failure);

        Assert.Equal(DeadlineProbeOutcome.Committed, resolution.Outcome);
        AssertCommittedDeadline(
            resolution.Snapshot,
            transaction,
            parentWorldVersion: null);
    }

    [Fact]
    public void Open_AllowsMissingSummary_ButRejectsMismatchedScenarioAuthority()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "authority");

        using (DeadlineProbeSession _ = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
        }

        Atelia.AteliaResult<Atelia.StateJournal.Repository> repositoryResult =
            Atelia.StateJournal.Repository.Open(repositoryPath);
        Assert.False(repositoryResult.IsFailure);
        using (Atelia.StateJournal.Repository repository = repositoryResult.Value!)
        {
            Atelia.AteliaResult<Atelia.StateJournal.Revision> revisionResult =
                repository.CheckoutBranch(DeadlineProbeSession.MainBranch);
            Assert.False(revisionResult.IsFailure);
            Atelia.StateJournal.DurableDict<string> root =
                Assert.IsAssignableFrom<Atelia.StateJournal.DurableDict<string>>(
                    revisionResult.Value!.GraphRoot);
            root.Upsert("commitKind", DeadlineProbeCommitKinds.MetadataOnly);
            Assert.True(root.Remove("commitSummary"));
            Assert.False(repository.Commit(root).IsFailure);
        }

        using (DeadlineProbeSession summaryless = DeadlineProbeSession.Open(
                   repositoryPath,
                   instance))
        {
            Assert.Null(summaryless.Snapshot().CommitSummary);
        }

        ScenarioDefinition wrongDefinition = ScenarioDefinition.Default with
        {
            Revision = checked(ScenarioDefinition.Default.Revision + 1),
        };
        var wrongInstance = new ScenarioInstance(wrongDefinition, WorldSeed);
        Assert.Throws<InvalidDataException>(() =>
        {
            using DeadlineProbeSession _ = DeadlineProbeSession.Open(
                repositoryPath,
                wrongInstance);
        });
    }

    [Fact]
    public void HistoricalGenesis_ForksTwoFreshLineages_WithoutMovingMain()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "forks");
        Atelia.StateJournal.CommitAddress genesisAddress;
        Atelia.StateJournal.CommitAddress mainHead;
        DeadlineProbeSemanticSnapshot mainSnapshot;

        using (DeadlineProbeSession main = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            genesisAddress = main.HeadAddress;
            main.Commit(DeadlineProbePlanner.Plan(main.Head));
            mainHead = main.HeadAddress;
            mainSnapshot = main.Snapshot();
        }

        const string leftBranch = "rewind/left";
        const string rightBranch = "rewind/right";
        DeadlineProbeSemanticSnapshot leftSnapshot = CommitForkSuffix(
            repositoryPath,
            instance,
            leftBranch,
            genesisAddress,
            LeftLineageId);
        DeadlineProbeSemanticSnapshot rightSnapshot = CommitForkSuffix(
            repositoryPath,
            instance,
            rightBranch,
            genesisAddress,
            RightLineageId);

        using (DeadlineProbeSession reopenedMain = DeadlineProbeSession.Open(
                   repositoryPath,
                   instance))
        {
            Assert.Equal(mainHead, reopenedMain.HeadAddress);
            Assert.Equal(mainSnapshot, reopenedMain.Snapshot());
        }

        var genesisVersion = new WorldVersion(LineageId, 0);
        AssertCommittedFork(leftSnapshot, LeftLineageId, genesisVersion);
        AssertCommittedFork(rightSnapshot, RightLineageId, genesisVersion);

        IReadOnlyList<DeadlineProbeHistoryRow> leftHistory =
            DeadlineProbeSession.ReadEffectiveHistory(
                repositoryPath,
                instance,
                leftBranch);
        IReadOnlyList<DeadlineProbeHistoryRow> rightHistory =
            DeadlineProbeSession.ReadEffectiveHistory(
                repositoryPath,
                instance,
                rightBranch);

        AssertForkHistory(leftHistory, genesisAddress, LeftLineageId, genesisVersion);
        AssertForkHistory(rightHistory, genesisAddress, RightLineageId, genesisVersion);
        Assert.NotEqual(leftHistory[^1].Address, rightHistory[^1].Address);
    }

    [Fact]
    public void HistoricalNonzeroFrontier_ForkPreservesGraphAndObjectiveCount()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "nonzero-fork");
        Atelia.StateJournal.CommitAddress mainHead;
        DeadlineProbeSemanticSnapshot mainSnapshot;

        using (DeadlineProbeSession main = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            main.Commit(DeadlineProbePlanner.Plan(main.Head));
            mainHead = main.HeadAddress;
            mainSnapshot = main.Snapshot();
        }

        const string forkBranch = "rewind/nonzero";
        using (DeadlineProbeSession fork = DeadlineProbeSession.CreateForkBranch(
                   repositoryPath,
                   instance,
                   forkBranch,
                   mainHead,
                   NonzeroForkLineageId))
        {
            DeadlineProbeSemanticSnapshot forkSnapshot = fork.Snapshot();
            Assert.Equal(mainHead, fork.HeadParentAddress);
            Assert.Equal(
                new WorldVersion(NonzeroForkLineageId, 1),
                forkSnapshot.Version);
            Assert.Equal(
                new WorldVersion(LineageId, 1),
                forkSnapshot.ParentWorldVersion);
            Assert.Equal(mainSnapshot.LastInstant, forkSnapshot.LastInstant);
            Assert.Equal(mainSnapshot.LastCause, forkSnapshot.LastCause);
            Assert.Equal(mainSnapshot.Now, forkSnapshot.Now);
            Assert.Equal(mainSnapshot.CellarSealed, forkSnapshot.CellarSealed);
            Assert.Equal(
                mainSnapshot.CellarGateAccess,
                forkSnapshot.CellarGateAccess);
            Assert.Equal(
                DeadlineProbeCommitKinds.LineageStart,
                forkSnapshot.CommitKind);
        }

        using DeadlineProbeSession reopenedMain = DeadlineProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(mainHead, reopenedMain.HeadAddress);
        Assert.Equal(mainSnapshot, reopenedMain.Snapshot());
    }

    [Fact]
    public void LineageReflogFailure_ResolvesExactCommittedBoundary_AndKeepsMain()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "lineage-ambiguous");
        const string forkBranch = "rewind/lineage-ambiguous";
        Atelia.StateJournal.CommitAddress mainHead;
        DeadlineProbeSemanticSnapshot mainSnapshot;

        using (DeadlineProbeSession main = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            mainHead = main.HeadAddress;
            mainSnapshot = main.Snapshot();
        }

        DeadlineProbeCommitFailedException failure =
            Assert.Throws<DeadlineProbeCommitFailedException>(() =>
            {
                using DeadlineProbeSession _ = DeadlineProbeSession.CreateForkBranch(
                    repositoryPath,
                    instance,
                    forkBranch,
                    mainHead,
                    FailedForkLineageId,
                    injectLineageReflogFailureForTest: true);
            });
        Assert.Equal(forkBranch, failure.BranchName);
        Assert.Equal(mainHead, failure.ExpectedParent);
        Assert.Equal(DeadlineProbeOperationKind.LineageStart, failure.OperationKind);
        Assert.Equal(
            new WorldVersion(LineageId, 0),
            failure.ParentAuthority.Version);
        Assert.Equal(
            new WorldVersion(FailedForkLineageId, 0),
            failure.ChildAuthority.Version);
        Assert.Equal(
            failure.ParentAuthority.Version,
            failure.ChildAuthority.ParentWorldVersion);
        AssertStructuredCommitMetadata(failure);

        Directory.Delete(GetReflogPath(repositoryPath, forkBranch));
        Assert.Throws<InvalidOperationException>(() =>
            DeadlineProbeSession.ResumeForkBranch(
                repositoryPath,
                instance,
                failure));
        DeadlineProbeResolution resolution = DeadlineProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            failure);
        Assert.Equal(DeadlineProbeOutcome.Committed, resolution.Outcome);
        Assert.Equal(
            new WorldVersion(FailedForkLineageId, 0),
            resolution.Snapshot.Version);
        Assert.Equal(
            new WorldVersion(LineageId, 0),
            resolution.Snapshot.ParentWorldVersion);
        Assert.Equal(
            DeadlineProbeCommitKinds.LineageStart,
            resolution.Snapshot.CommitKind);

        using (DeadlineProbeSession reopenedFork = DeadlineProbeSession.Open(
                   repositoryPath,
                   instance,
                   forkBranch))
        {
            Assert.Equal(resolution.Snapshot, reopenedFork.Snapshot());
        }

        using DeadlineProbeSession reopenedMain = DeadlineProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(mainHead, reopenedMain.HeadAddress);
        Assert.Equal(mainSnapshot, reopenedMain.Snapshot());
    }

    [Fact]
    public void LineagePreCommitFailure_ResolvesParent_ThenSafelyResumesFork()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "lineage-not-published");
        const string forkBranch = "rewind/lineage-not-published";
        Atelia.StateJournal.CommitAddress mainHead;
        DeadlineProbeSemanticSnapshot mainSnapshot;

        using (DeadlineProbeSession main = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            mainHead = main.HeadAddress;
            mainSnapshot = main.Snapshot();
        }

        DeadlineProbeCommitFailedException failure =
            Assert.Throws<DeadlineProbeCommitFailedException>(() =>
            {
                using DeadlineProbeSession _ = DeadlineProbeSession.CreateForkBranch(
                    repositoryPath,
                    instance,
                    forkBranch,
                    mainHead,
                    ResumedForkLineageId,
                    injectLineagePreCommitFailureForTest: true);
            });
        Assert.Equal(DeadlineProbeOperationKind.LineageStart, failure.OperationKind);
        Assert.Null(failure.CandidateAddress);
        Assert.Equal("BeforeRepositoryCommit", failure.FailurePhase);
        Assert.Equal("NotPublished", failure.PublicationState);

        DeadlineProbeResolution notCommitted =
            DeadlineProbeSession.ResolveUnknownOutcome(
                repositoryPath,
                instance,
                failure);
        Assert.Equal(DeadlineProbeOutcome.NotCommitted, notCommitted.Outcome);
        Assert.Equal(mainSnapshot, notCommitted.Snapshot);

        using (DeadlineProbeSession resumed = DeadlineProbeSession.ResumeForkBranch(
                   repositoryPath,
                   instance,
                   failure))
        {
            DeadlineProbeSemanticSnapshot boundary = resumed.Snapshot();
            Assert.Equal(mainHead, resumed.HeadParentAddress);
            Assert.Equal(
                new WorldVersion(ResumedForkLineageId, 0),
                boundary.Version);
            Assert.Equal(
                new WorldVersion(LineageId, 0),
                boundary.ParentWorldVersion);
            Assert.Equal(DeadlineProbeCommitKinds.LineageStart, boundary.CommitKind);
            Assert.False(string.IsNullOrWhiteSpace(boundary.CommitSummary));

            resumed.Commit(DeadlineProbePlanner.Plan(resumed.Head));
            Assert.Equal(
                new WorldVersion(ResumedForkLineageId, 1),
                resumed.Snapshot().Version);
        }

        using DeadlineProbeSession reopenedMain = DeadlineProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(mainHead, reopenedMain.HeadAddress);
        Assert.Equal(mainSnapshot, reopenedMain.Snapshot());
    }

    private static DeadlineProbeSemanticSnapshot CommitForkSuffix(
        string repositoryPath,
        ScenarioInstance instance,
        string branchName,
        Atelia.StateJournal.CommitAddress genesisAddress,
        long childLineageId)
    {
        using DeadlineProbeSession branch = DeadlineProbeSession.CreateForkBranch(
            repositoryPath,
            instance,
            branchName,
            genesisAddress,
            childLineageId);
        Assert.NotEqual(genesisAddress, branch.HeadAddress);
        Assert.Equal(genesisAddress, branch.HeadParentAddress);

        DeadlineProbeSemanticSnapshot lineageStart = branch.Snapshot();
        Assert.Equal(new WorldVersion(childLineageId, 0), lineageStart.Version);
        Assert.Equal(new WorldVersion(LineageId, 0), lineageStart.ParentWorldVersion);
        Assert.Equal(DeadlineProbeCommitKinds.LineageStart, lineageStart.CommitKind);
        Assert.False(lineageStart.CellarSealed);
        Assert.Null(lineageStart.LastInstant);

        branch.Commit(DeadlineProbePlanner.Plan(branch.Head));
        return branch.Snapshot();
    }

    private static void AssertForkHistory(
        IReadOnlyList<DeadlineProbeHistoryRow> history,
        Atelia.StateJournal.CommitAddress genesisAddress,
        long childLineageId,
        WorldVersion parentWorldVersion)
    {
        Assert.Equal(3, history.Count);
        Assert.Equal(genesisAddress, history[0].Address);
        Assert.Null(history[0].ParentAddress);
        Assert.Equal(genesisAddress, history[1].ParentAddress);
        Assert.Equal(history[1].Address, history[2].ParentAddress);
        Assert.Equal(
            [
                DeadlineProbeCommitKinds.Genesis,
                DeadlineProbeCommitKinds.LineageStart,
                DeadlineProbeCommitKinds.ObjectiveTransition,
            ],
            history.Select(row => row.Snapshot.CommitKind).ToArray());
        Assert.Equal(
            [
                new WorldVersion(LineageId, 0),
                new WorldVersion(childLineageId, 0),
                new WorldVersion(childLineageId, 1),
            ],
            history.Select(row => row.Snapshot.Version).ToArray());
        Assert.All(history, row => Assert.False(string.IsNullOrWhiteSpace(
            row.Snapshot.CommitSummary)));
        Assert.Null(history[0].Snapshot.ParentWorldVersion);
        Assert.Equal(parentWorldVersion, history[1].Snapshot.ParentWorldVersion);
        Assert.Equal(parentWorldVersion, history[2].Snapshot.ParentWorldVersion);
    }

    private static void AssertStructuredCommitMetadata(
        DeadlineProbeCommitFailedException failure)
    {
        bool hasCandidate = failure.CandidateAddress is not null;
        Assert.Equal(hasCandidate, failure.FailurePhase is not null);
        Assert.Equal(hasCandidate, failure.PublicationState is not null);
        if (hasCandidate)
        {
            Assert.Equal("AppendReflog", failure.FailurePhase);
            Assert.Equal("Published", failure.PublicationState);
        }
    }

    private static string GetReflogPath(
        string repositoryPath,
        string branchName) => Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            branchName.Replace('/', Path.DirectorySeparatorChar) +
                ".reflog.jsonl");

    private static void AssertGenesis(
        DeadlineProbeSemanticSnapshot snapshot,
        WorldVersion expectedVersion)
    {
        Assert.Equal("firstboard.statejournal-deadline-root/2", snapshot.SchemaId);
        Assert.Equal(expectedVersion, snapshot.Version);
        Assert.Null(snapshot.ParentWorldVersion);
        Assert.Null(snapshot.LastInstant);
        Assert.Null(snapshot.LastCause);
        Assert.Equal(DeadlineProbeCommitKinds.Genesis, snapshot.CommitKind);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CommitSummary));
        Assert.Equal(ModelTime.Zero, snapshot.Now);
        Assert.False(snapshot.CellarSealed);
        Assert.Equal(new PassageEntryAccess(true, true), snapshot.CellarGateAccess);
    }

    private static void AssertCommittedFork(
        DeadlineProbeSemanticSnapshot snapshot,
        long childLineageId,
        WorldVersion parentWorldVersion)
    {
        Assert.Equal(new WorldVersion(childLineageId, 1), snapshot.Version);
        Assert.Equal(parentWorldVersion, snapshot.ParentWorldVersion);
        Assert.Equal(DeadlineProbeCommitKinds.ObjectiveTransition, snapshot.CommitKind);
        Assert.True(snapshot.CellarSealed);
    }

    private static async Task AssertMatchesCurrentRuleAsync(
        ScenarioInstance instance,
        DeadlineTransactionProbeV1 actual)
    {
        FirstBoardWorld immutableGenesis = instance.CreateInitialWorld();
        var rule = new CellarDeadlineRule(
            instance.Graph,
            instance.Definition.CellarDeadlineMs);
        OccurrenceCandidate<BoardCandidate> candidate = Assert.Single(
            rule.Forecast(
                immutableGenesis,
                new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)));
        TransitionDraft<FirstBoardFact> draft = await rule.PlanSelectedAsync(
            immutableGenesis,
            candidate,
            CancellationToken.None);

        Assert.Equal(candidate.Key, actual.Batch.CauseKey);
        Assert.Equal(candidate.Due.ModelTime, actual.Batch.Instant.ModelTime);
        Assert.Equal(0, actual.Batch.Instant.CausalOrdinal);
        Assert.Equal(draft.Facts, actual.Batch.Facts);

        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld expected = immutableGenesis;
        foreach (FirstBoardFact fact in draft.Facts)
        {
            expected = reducer.Apply(expected, actual.Batch.Instant, fact);
        }

        reducer.Validate(expected);
        Assert.True(expected.CellarSealed);
        Assert.Equal(
            new PassageEntryAccess(false, true),
            new SpatialQueries(instance.Graph).GetPassageEntryAccess(
                expected.Spatial,
                new PassageId(BoardIds.CellarGatePassage)));
    }

    private static void AssertCommittedDeadline(
        DeadlineProbeSemanticSnapshot snapshot,
        DeadlineTransactionProbeV1 transaction,
        WorldVersion? parentWorldVersion)
    {
        Assert.Equal(
            new WorldVersion(
                transaction.ParentVersion.LineageId,
                checked(transaction.ParentVersion.TransitionCount + 1)),
            snapshot.Version);
        Assert.Equal(parentWorldVersion, snapshot.ParentWorldVersion);
        Assert.Equal(transaction.Batch.Instant, snapshot.LastInstant);
        Assert.Equal(transaction.Batch.CauseKey, snapshot.LastCause);
        Assert.Equal(DeadlineProbeCommitKinds.ObjectiveTransition, snapshot.CommitKind);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CommitSummary));
        Assert.Equal(transaction.Batch.Instant.ModelTime, snapshot.Now);
        Assert.True(snapshot.CellarSealed);
        PassageEntryAccessChangedFact changed = Assert.IsType<PassageEntryAccessChangedFact>(
            Assert.IsType<SpatialBoardFact>(transaction.Batch.Facts[1]).Value);
        Assert.Equal(changed.ResultAccess, snapshot.CellarGateAccess);
    }
}

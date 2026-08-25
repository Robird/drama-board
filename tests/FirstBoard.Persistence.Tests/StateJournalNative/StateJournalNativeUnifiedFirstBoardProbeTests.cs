using System.Threading.Channels;
using Atelia.StateJournal;
using DramaBoard.FirstBoard;
using DramaBoard.FirstBoard.Demo.Live;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

internal enum UnifiedResponseMutation
{
    CrossIntent,
    CrossActor,
    CrossEntity,
    CoordinatedRequest,
}

internal enum UnifiedOccurrenceMutation
{
    ForgedCause,
    LaterInstant,
}

public sealed class StateJournalNativeUnifiedFirstBoardProbeTests
{
    private const long MainLineageId = 120_001;
    private const long ChildLineageId = 120_002;
    private const ulong WorldSeed = 403;
    private const string ChildBranch = "unified-child";
    private const string ReverseMemory = "我在路上遇到鲍勃，决定折返酒馆重新评估线索。";
    private const string ContinueMemory = "我在路上遇到鲍勃，决定继续前往集市。";

    [Fact]
    public void ImportedFullTravelingRoot_ReopensWithDeepExactAuthority()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-baseline");
        UnifiedFullAuthoritySnapshot committed;
        CommitAddress head;

        using (UnifiedFirstBoardProbeSession session =
               UnifiedFirstBoardProbeSession.CreateImportedTravelingBaseline(
                   repositoryPath,
                   instance,
                   MainLineageId))
        {
            committed = session.Snapshot().Authority;
            head = session.HeadAddress;
            AssertTravelingBaseline(committed, instance);
        }

        using UnifiedFirstBoardProbeSession reopened = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(head, reopened.HeadAddress);
        Assert.True(committed.Matches(reopened.Snapshot().Authority));
        UnifiedClosedObjectiveBaseline baseline = reopened.ExportClosedBaseline();
        Assert.True(committed.Objective.Matches(
            UnifiedAuthorityFreeze.Objective(baseline.World)));
        Assert.Equal(committed.Version, baseline.Version);
        Assert.Null(baseline.LastInstant);
    }

    [Fact]
    public async Task Opening_UsesProductionExactBatch_AndReopensOneCompleteChild()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-opening");
        UnifiedCommitReceipt receipt;
        UnifiedSemanticSnapshot committed;

        using (UnifiedFirstBoardProbeSession session =
               UnifiedFirstBoardProbeSession.CreateImportedTravelingBaseline(
                   repositoryPath,
                   instance,
                   MainLineageId))
        {
            UnifiedPreparedTransaction opening = await session.PrepareOpeningAsync();
            AssertOpeningBatch(opening.Batch);
            receipt = session.Commit(opening);
            committed = session.Snapshot();
            Assert.Equal(receipt.Parent, session.HeadParentAddress);
            Assert.Equal(new WorldVersion(MainLineageId, 1), receipt.Version);
            Assert.Equal(opening.Batch.Instant, committed.Authority.LastInstant);
            Assert.Equal(opening.Batch.CauseKey, committed.Authority.LastCause);
            AssertOpened(committed.Authority);
        }

        using UnifiedFirstBoardProbeSession reopened = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(receipt.Head, reopened.HeadAddress);
        Assert.True(committed.Authority.Matches(reopened.Snapshot().Authority));
    }

    [Fact]
    public async Task ReverseResponse_CoCommitsPreparedCognitionGameThenSpatial_AndReopens()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-reverse");
        UnifiedPreparedTransaction response;
        UnifiedSemanticSnapshot committed;
        UnifiedCommitReceipt receipt;

        using (UnifiedFirstBoardProbeSession session = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            response = await session.PrepareResponseAsync(
                new Intent(ActionKinds.ReverseTravel),
                ReverseMemory);
            AssertReverseResponse(response);
            receipt = session.Commit(response);
            committed = session.Snapshot();
            AssertReversePost(committed, response, ReverseMemory);
        }

        using UnifiedFirstBoardProbeSession reopened = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(receipt.Head, reopened.HeadAddress);
        Assert.True(committed.Authority.Matches(reopened.Snapshot().Authority));
        Assert.DoesNotContain(
            ReverseMemory,
            reopened.Snapshot().CommitSummary ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCognitionBinding_CommitsNothing_AndKeepsSessionUsable()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-invalid-effect");
        using UnifiedFirstBoardProbeSession session = await CreateOpenedAsync(
            repositoryPath,
            instance);
        UnifiedFullAuthoritySnapshot parent = session.Snapshot().Authority;
        CommitAddress parentAddress = session.HeadAddress;
        UnifiedPreparedTransaction response = await session.PrepareResponseAsync(
            new Intent(ActionKinds.ReverseTravel),
            ReverseMemory);
        UnifiedPreparedPlayerEffect invalid = response.PlayerEffect! with
        {
            PlayerProfileId = "wrong-profile",
        };

        Assert.Throws<InvalidDataException>(() => session.Commit(response with
        {
            PlayerEffect = invalid,
        }));

        Assert.Equal(parentAddress, session.HeadAddress);
        Assert.True(parent.Matches(session.Snapshot().Authority));
    }

    [Theory]
    [InlineData(UnifiedResponseMutation.CrossIntent)]
    [InlineData(UnifiedResponseMutation.CrossActor)]
    [InlineData(UnifiedResponseMutation.CrossEntity)]
    [InlineData(UnifiedResponseMutation.CoordinatedRequest)]
    internal async Task InvalidResponseBinding_CommitsNothing_AndKeepsParentUsable(
        UnifiedResponseMutation mutation)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, $"unified-invalid-{mutation}");
        using UnifiedFirstBoardProbeSession session = await CreateOpenedAsync(
            repositoryPath,
            instance);
        CommitAddress parentAddress = session.HeadAddress;
        UnifiedFullAuthoritySnapshot parent = session.Snapshot().Authority;
        UnifiedPreparedTransaction response = await session.PrepareResponseAsync(
            new Intent(ActionKinds.ReverseTravel),
            ReverseMemory);

        UnifiedPreparedTransaction invalid = MutateResponse(response, mutation);
        Assert.Throws<InvalidDataException>(() => session.Commit(invalid));
        Assert.Equal(parentAddress, session.HeadAddress);
        Assert.True(parent.Matches(session.Snapshot().Authority));

        UnifiedCommitReceipt valid = session.Commit(response);
        Assert.Equal(parentAddress, valid.Parent);
    }

    [Theory]
    [InlineData(UnifiedOccurrenceMutation.ForgedCause)]
    [InlineData(UnifiedOccurrenceMutation.LaterInstant)]
    internal async Task InvalidOccurrenceBinding_CommitsNothing_AndKeepsParentUsable(
        UnifiedOccurrenceMutation mutation)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, $"unified-occurrence-{mutation}");
        using UnifiedFirstBoardProbeSession session = await CreateOpenedAsync(
            repositoryPath,
            instance);
        CommitAddress parentAddress = session.HeadAddress;
        UnifiedFullAuthoritySnapshot parent = session.Snapshot().Authority;
        FirstBoardWorld preWorld = session.ExportClosedBaseline().World;
        UnifiedPreparedTransaction response = await session.PrepareResponseAsync(
            new Intent(ActionKinds.ReverseTravel),
            ReverseMemory);

        UnifiedPreparedTransaction invalid = MutateOccurrence(
            response,
            mutation,
            instance,
            preWorld);
        Assert.Throws<InvalidDataException>(() => session.Commit(invalid));
        Assert.Equal(parentAddress, session.HeadAddress);
        Assert.True(parent.Matches(session.Snapshot().Authority));

        UnifiedCommitReceipt valid = session.Commit(response);
        Assert.Equal(parentAddress, valid.Parent);
    }

    [Theory]
    [InlineData(UnifiedProbeFault.AfterCognition)]
    [InlineData(UnifiedProbeFault.AfterGame)]
    [InlineData(UnifiedProbeFault.AfterSpatial)]
    [InlineData(UnifiedProbeFault.AtCompleteValidation)]
    internal async Task WorkingFailure_Poisons_AndReopensExactFullParent(
        UnifiedProbeFault fault)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, $"unified-{fault}");
        UnifiedFullAuthoritySnapshot parent;
        CommitAddress parentAddress;

        using (UnifiedFirstBoardProbeSession session = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            parent = session.Snapshot().Authority;
            parentAddress = session.HeadAddress;
            UnifiedPreparedTransaction response = await session.PrepareResponseAsync(
                new Intent(ActionKinds.ReverseTravel),
                ReverseMemory);
            Assert.Throws<InvalidOperationException>(() => session.Commit(response, fault));
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        using UnifiedFirstBoardProbeSession reopened = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(parentAddress, reopened.HeadAddress);
        Assert.True(parent.Matches(reopened.Snapshot().Authority));
    }

    [Fact]
    public async Task ReflogFailure_AfterPublication_ResolvesExactFullChild()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-ambiguous");
        string reflogPath = Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            "main.reflog.jsonl");
        UnifiedCommitFailedException failure;

        using (UnifiedFirstBoardProbeSession session = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            UnifiedPreparedTransaction response = await session.PrepareResponseAsync(
                new Intent(ActionKinds.ReverseTravel),
                ReverseMemory);
            File.Delete(reflogPath);
            Directory.CreateDirectory(reflogPath);
            failure = Assert.Throws<UnifiedCommitFailedException>(
                () => session.Commit(response));
            RepositoryCommitError error = Assert.IsType<RepositoryCommitError>(failure.Error);

            Assert.Equal(
                RepositoryCommitFailurePhase.AppendReflog,
                error.FailurePhase);
            Assert.Equal(
                RepositoryCommitPublicationState.Published,
                error.PublicationState);
            Assert.True(error.RequiresRepositoryReopen);
            Assert.False(error.CanRetryTransparently);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        Directory.Delete(reflogPath);
        UnifiedResolution resolution = UnifiedFirstBoardProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            failure);
        Assert.Equal(UnifiedProbeOutcome.Committed, resolution.Outcome);
        Assert.True(failure.ChildAuthority.Matches(resolution.Snapshot.Authority));
    }

    [Fact]
    public void PublicationClassifier_RejectsStructuredStateContradictions()
    {
        Assert.Throws<InvalidDataException>(() =>
            UnifiedFirstBoardProbeSession.ClassifyPublication(
                RepositoryCommitPublicationState.Published,
                exactParent: true,
                exactChild: false));
        Assert.Throws<InvalidDataException>(() =>
            UnifiedFirstBoardProbeSession.ClassifyPublication(
                RepositoryCommitPublicationState.NotPublished,
                exactParent: false,
                exactChild: true));
        Assert.Equal(
            UnifiedProbeOutcome.NotCommitted,
            UnifiedFirstBoardProbeSession.ClassifyPublication(
                RepositoryCommitPublicationState.Unknown,
                exactParent: true,
                exactChild: false));
        Assert.Equal(
            UnifiedProbeOutcome.Committed,
            UnifiedFirstBoardProbeSession.ClassifyPublication(
                RepositoryCommitPublicationState.MayHavePublished,
                exactParent: false,
                exactChild: true));
    }

    [Fact]
    public void ScalarCodec_UsesOverflowSafeCeiling_AndRejectsNoncanonicalPatchMask()
    {
        Assert.Equal(
            2,
            UnifiedProbeScalarCodec.CeilingDividePositive(long.MaxValue, long.MaxValue - 1));
        Assert.Equal(
            1,
            UnifiedProbeScalarCodec.CeilingDividePositive(long.MaxValue, long.MaxValue));
        var expected = new PassageEntryPatch(enterableFromA: false, enterableFromB: true);
        Assert.Equal(expected, UnifiedProbeScalarCodec.DecodePatch(13));
        Assert.Equal(13, UnifiedProbeScalarCodec.EncodePatch(expected));
        Assert.Throws<InvalidDataException>(() => UnifiedProbeScalarCodec.DecodePatch(6));
    }

    [Fact]
    public async Task ForkPreflightFailures_LeaveSameBranchNameAvailable()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-fork-preflight");
        CommitAddress openingAddress;
        UnifiedFullAuthoritySnapshot openingAuthority;
        using (UnifiedFirstBoardProbeSession main = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            openingAddress = main.HeadAddress;
            openingAuthority = main.Snapshot().Authority;
        }

        Assert.Throws<InvalidDataException>(() => UnifiedFirstBoardProbeSession.ForkAtCommit(
            repositoryPath,
            instance,
            openingAddress,
            new WorldVersion(
                openingAuthority.Version.LineageId,
                checked(openingAuthority.Version.TransitionCount + 1)),
            ChildBranch,
            ChildLineageId));
        Assert.Throws<ArgumentException>(() => UnifiedFirstBoardProbeSession.ForkAtCommit(
            repositoryPath,
            instance,
            openingAddress,
            openingAuthority.Version,
            ChildBranch,
            openingAuthority.Version.LineageId));

        using UnifiedFirstBoardProbeSession child = UnifiedFirstBoardProbeSession.ForkAtCommit(
            repositoryPath,
            instance,
            openingAddress,
            openingAuthority.Version,
            ChildBranch,
            ChildLineageId);
        Assert.Equal(openingAddress, child.HeadParentAddress);
        Assert.Equal(new WorldVersion(ChildLineageId, 1), child.Snapshot().Authority.Version);
    }

    [Fact]
    public async Task ForkLineageKnownNotPublished_ClassifiesRawParent_ThenResumesExactBoundary()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-lineage-not-published");
        const string branchName = "unified-child-not-published";
        CommitAddress openingAddress;
        UnifiedFullAuthoritySnapshot openingAuthority;
        using (UnifiedFirstBoardProbeSession main = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            openingAddress = main.HeadAddress;
            openingAuthority = main.Snapshot().Authority;
        }

        UnifiedCommitFailedException failure = Assert.Throws<UnifiedCommitFailedException>(() =>
        {
            using UnifiedFirstBoardProbeSession unexpected =
                UnifiedFirstBoardProbeSession.ForkAtCommit(
                    repositoryPath,
                    instance,
                    openingAddress,
                    openingAuthority.Version,
                    branchName,
                    ChildLineageId,
                    failLineageBeforeRepositoryCommit: true);
        });
        Assert.Equal(UnifiedOperationKind.LineageStart, failure.OperationKind);
        Assert.Null(failure.Error);
        Assert.Null(failure.CandidateAddress);
        Assert.Null(failure.FailurePhase);
        Assert.Equal(
            RepositoryCommitPublicationState.NotPublished,
            failure.PublicationState);
        Assert.True(failure.RequiresRepositoryReopen);
        Assert.False(failure.CanRetryTransparently);
        Assert.False(failure.MayHavePublished);

        UnifiedResolution notCommitted = UnifiedFirstBoardProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            failure);
        Assert.Equal(UnifiedProbeOutcome.NotCommitted, notCommitted.Outcome);
        Assert.True(openingAuthority.Matches(notCommitted.Snapshot.Authority));
        Assert.False(failure.ChildAuthority.Matches(notCommitted.Snapshot.Authority));

        using (UnifiedFirstBoardProbeSession raw = UnifiedFirstBoardProbeSession.Open(
                   repositoryPath,
                   instance,
                   branchName))
        {
            Assert.Equal(openingAddress, raw.HeadAddress);
            Assert.True(openingAuthority.Matches(raw.Snapshot().Authority));
        }

        using (UnifiedFirstBoardProbeSession resumed =
               UnifiedFirstBoardProbeSession.ResumeForkBranch(
                   repositoryPath,
                   instance,
                   failure))
        {
            Assert.Equal(openingAddress, resumed.HeadParentAddress);
            Assert.True(failure.ChildAuthority.Matches(resumed.Snapshot().Authority));
        }

        using UnifiedFirstBoardProbeSession reopenedMain = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(openingAddress, reopenedMain.HeadAddress);
        Assert.True(openingAuthority.Matches(reopenedMain.Snapshot().Authority));
    }

    [Fact]
    public async Task ForkLineageGenericResultFailure_PreservesError_AndResumesExactBoundary()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-lineage-generic-result");
        const string branchName = "unified-child-generic-result";
        CommitAddress openingAddress;
        UnifiedFullAuthoritySnapshot openingAuthority;
        using (UnifiedFirstBoardProbeSession main = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            openingAddress = main.HeadAddress;
            openingAuthority = main.Snapshot().Authority;
        }

        var injected = new UnifiedTestCommitError("generic-result");
        UnifiedCommitFailedException failure = Assert.Throws<UnifiedCommitFailedException>(() =>
        {
            using UnifiedFirstBoardProbeSession unexpected =
                UnifiedFirstBoardProbeSession.ForkAtCommit(
                    repositoryPath,
                    instance,
                    openingAddress,
                    openingAuthority.Version,
                    branchName,
                    ChildLineageId,
                    lineageCommitErrorForTest: injected);
        });
        Assert.Equal(UnifiedOperationKind.LineageStart, failure.OperationKind);
        Assert.Null(failure.Error);
        Assert.Same(injected, failure.UnderlyingError);
        Assert.Null(failure.CandidateAddress);
        Assert.Null(failure.FailurePhase);
        Assert.Equal(
            RepositoryCommitPublicationState.NotPublished,
            failure.PublicationState);
        Assert.False(failure.MayHavePublished);

        UnifiedResolution notCommitted = UnifiedFirstBoardProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            failure);
        Assert.Equal(UnifiedProbeOutcome.NotCommitted, notCommitted.Outcome);
        Assert.True(openingAuthority.Matches(notCommitted.Snapshot.Authority));

        using (UnifiedFirstBoardProbeSession resumed =
               UnifiedFirstBoardProbeSession.ResumeForkBranch(
                   repositoryPath,
                   instance,
                   failure))
        {
            Assert.Equal(openingAddress, resumed.HeadParentAddress);
            Assert.True(failure.ChildAuthority.Matches(resumed.Snapshot().Authority));
        }

        using UnifiedFirstBoardProbeSession reopenedMain = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(openingAddress, reopenedMain.HeadAddress);
        Assert.True(openingAuthority.Matches(reopenedMain.Snapshot().Authority));
    }

    [Fact]
    public async Task ForkLineageBoundary_ReflogFailure_ClassifiesPhysicalParentAndFullChild()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-fork-ambiguous");
        CommitAddress openingAddress;
        UnifiedFullAuthoritySnapshot openingAuthority;
        using (UnifiedFirstBoardProbeSession main = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            openingAddress = main.HeadAddress;
            openingAuthority = main.Snapshot().Authority;
        }

        string reflogPath = Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            $"{ChildBranch}.reflog.jsonl");
        UnifiedCommitFailedException failure = Assert.Throws<UnifiedCommitFailedException>(() =>
        {
            using UnifiedFirstBoardProbeSession unexpected =
                UnifiedFirstBoardProbeSession.ForkAtCommit(
                    repositoryPath,
                    instance,
                    openingAddress,
                    openingAuthority.Version,
                    ChildBranch,
                    ChildLineageId,
                    failLineageReflogAfterPublication: true);
        });
        Assert.Equal(UnifiedOperationKind.LineageStart, failure.OperationKind);
        Assert.Equal(openingAddress, failure.ExpectedParent);
        Assert.True(openingAuthority.Matches(failure.ParentAuthority));
        Assert.Equal(new WorldVersion(ChildLineageId, 1), failure.ChildAuthority.Version);
        Assert.Equal(
            openingAuthority.Version,
            failure.ChildAuthority.ParentWorldVersion);
        Assert.True(openingAuthority.Objective.Matches(failure.ChildAuthority.Objective));
        Assert.True(openingAuthority.PlayerSlots[0].Matches(
            failure.ChildAuthority.PlayerSlots[0]));

        Directory.Delete(reflogPath);
        UnifiedResolution resolution = UnifiedFirstBoardProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            failure);
        Assert.Equal(UnifiedProbeOutcome.Committed, resolution.Outcome);
        Assert.True(failure.ChildAuthority.Matches(resolution.Snapshot.Authority));
        using UnifiedFirstBoardProbeSession reopenedChild = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance,
            ChildBranch);
        Assert.Equal(failure.CandidateAddress, reopenedChild.HeadAddress);
        Assert.Equal(openingAddress, reopenedChild.HeadParentAddress);
    }

    [Fact]
    public async Task HistoricalOpeningFork_MainReverses_ChildContinues_WithoutMovingMain()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-fork");
        CommitAddress openingAddress;
        UnifiedFullAuthoritySnapshot openingAuthority;
        CommitAddress mainHead;
        UnifiedSemanticSnapshot mainCommitted;

        using (UnifiedFirstBoardProbeSession main = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            openingAddress = main.HeadAddress;
            openingAuthority = main.Snapshot().Authority;
            UnifiedPreparedTransaction reverse = await main.PrepareResponseAsync(
                new Intent(ActionKinds.ReverseTravel),
                ReverseMemory);
            mainHead = main.Commit(reverse).Head;
            mainCommitted = main.Snapshot();
        }

        UnifiedSemanticSnapshot childCommitted;
        CommitAddress lineageBoundary;
        using (UnifiedFirstBoardProbeSession child = UnifiedFirstBoardProbeSession.ForkAtCommit(
                   repositoryPath,
                   instance,
                   openingAddress,
                   openingAuthority.Version,
                   ChildBranch,
                   ChildLineageId))
        {
            lineageBoundary = child.HeadAddress;
            UnifiedSemanticSnapshot lineageStart = child.Snapshot();
            Assert.Equal(openingAddress, child.HeadParentAddress);
            Assert.Equal(new WorldVersion(ChildLineageId, 1), lineageStart.Authority.Version);
            Assert.Equal(openingAuthority.Version, lineageStart.Authority.ParentWorldVersion);
            Assert.Equal(openingAuthority.LastInstant, lineageStart.Authority.LastInstant);
            Assert.Equal(openingAuthority.LastCause, lineageStart.Authority.LastCause);
            Assert.True(openingAuthority.Objective.Matches(lineageStart.Authority.Objective));
            Assert.True(openingAuthority.PlayerSlots[0].Matches(
                lineageStart.Authority.PlayerSlots[0]));

            UnifiedPreparedTransaction continued = await child.PrepareResponseAsync(
                new Intent(ActionKinds.ContinueTravel),
                ContinueMemory);
            AssertContinueResponse(continued);
            UnifiedCommitReceipt childReceipt = child.Commit(continued);
            Assert.Equal(lineageBoundary, childReceipt.Parent);
            childCommitted = child.Snapshot();
        }

        using (UnifiedFirstBoardProbeSession main = UnifiedFirstBoardProbeSession.Open(
                   repositoryPath,
                   instance))
        {
            Assert.Equal(mainHead, main.HeadAddress);
            Assert.True(mainCommitted.Authority.Matches(main.Snapshot().Authority));
        }

        using UnifiedFirstBoardProbeSession reopenedChild = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance,
            ChildBranch);
        Assert.True(childCommitted.Authority.Matches(reopenedChild.Snapshot().Authority));
        Assert.Equal(lineageBoundary, reopenedChild.HeadParentAddress);
        Assert.False(mainCommitted.Authority.Objective.Matches(
            childCommitted.Authority.Objective));
        Assert.False(mainCommitted.Authority.PlayerSlots[0].Memory.SequenceEqual(
            childCommitted.Authority.PlayerSlots[0].Memory));
        Assert.Equal(
            ReverseMemory,
            WorkingContext(mainCommitted.Authority.PlayerSlots[0]));
        Assert.Equal(
            ContinueMemory,
            WorkingContext(childCommitted.Authority.PlayerSlots[0]));
    }

    [Theory]
    [InlineData(UnifiedProbeFault.AfterCognition)]
    [InlineData(UnifiedProbeFault.AfterGame)]
    [InlineData(UnifiedProbeFault.AfterSpatial)]
    [InlineData(UnifiedProbeFault.AtCompleteValidation)]
    internal async Task ChildPrepublicationWorkingFailure_ReopensCommittedBoundary_AndLeavesMain(
        UnifiedProbeFault fault)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, $"unified-child-fault-{fault}");
        CommitAddress openingAddress;
        UnifiedFullAuthoritySnapshot mainAuthority;
        using (UnifiedFirstBoardProbeSession main = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            openingAddress = main.HeadAddress;
            mainAuthority = main.Snapshot().Authority;
        }

        string branchName = $"{ChildBranch}-{fault}";
        CommitAddress boundaryAddress;
        UnifiedFullAuthoritySnapshot boundaryAuthority;
        using (UnifiedFirstBoardProbeSession child = UnifiedFirstBoardProbeSession.ForkAtCommit(
                   repositoryPath,
                   instance,
                   openingAddress,
                   mainAuthority.Version,
                   branchName,
                   ChildLineageId))
        {
            boundaryAddress = child.HeadAddress;
            boundaryAuthority = child.Snapshot().Authority;
            Assert.Equal(openingAddress, child.HeadParentAddress);
            UnifiedPreparedTransaction reverse = await child.PrepareResponseAsync(
                new Intent(ActionKinds.ReverseTravel),
                ReverseMemory);
            Assert.Throws<InvalidOperationException>(() => child.Commit(reverse, fault));
            Assert.Throws<InvalidOperationException>(() => child.Snapshot());
        }

        using (UnifiedFirstBoardProbeSession reopenedChild = UnifiedFirstBoardProbeSession.Open(
                   repositoryPath,
                   instance,
                   branchName))
        {
            Assert.Equal(boundaryAddress, reopenedChild.HeadAddress);
            Assert.Equal(openingAddress, reopenedChild.HeadParentAddress);
            Assert.True(boundaryAuthority.Matches(reopenedChild.Snapshot().Authority));
        }

        using UnifiedFirstBoardProbeSession reopenedMain = UnifiedFirstBoardProbeSession.Open(
            repositoryPath,
            instance);
        Assert.Equal(openingAddress, reopenedMain.HeadAddress);
        Assert.True(mainAuthority.Matches(reopenedMain.Snapshot().Authority));
    }

    [Fact]
    public async Task HistoricalStateJournalBaseline_FeedsPresentationOnlyExactReverseSuffix()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "unified-presentation");
        CommitAddress openingAddress;
        UnifiedPreparedTransaction reverse;
        UnifiedCommitReceipt receipt;
        UnifiedFullAuthoritySnapshot committed;

        using (UnifiedFirstBoardProbeSession session = await CreateOpenedAsync(
                   repositoryPath,
                   instance))
        {
            openingAddress = session.HeadAddress;
            reverse = await session.PrepareResponseAsync(
                new Intent(ActionKinds.ReverseTravel),
                ReverseMemory);
            receipt = session.Commit(reverse);
            committed = session.Snapshot().Authority;
        }

        UnifiedClosedObjectiveBaseline baseline =
            UnifiedFirstBoardProbeSession.ExportHistoricalBaseline(
                repositoryPath,
                instance,
                openingAddress);
        var coordination = new LiveSessionCoordination(baseline.Version);
        var terminal = new RecordingTerminal();
        var loop = new FirstBoardPresentationLoop(
            instance,
            baseline.World,
            baseline.LastInstant,
            coordination,
            terminal,
            new NoDelayPacer(),
            PresentationMode.Developer,
            humanActorId: null);
        Channel<CommittedTransition> channel = Channel.CreateUnbounded<CommittedTransition>();
        coordination.PublishCommitted(receipt.Version);
        Assert.True(channel.Writer.TryWrite(new CommittedTransition(
            receipt.Version,
            receipt.Batch)));
        channel.Writer.Complete();

        await loop.RunAsync(channel.Reader, CancellationToken.None);

        Assert.True(committed.Objective.Matches(
            UnifiedAuthorityFreeze.Objective(loop.ReplayWorld)));
        Assert.Equal(reverse.Batch.Instant, loop.LastPresentedInstant);
        string visible = string.Join('\n', terminal.Text);
        Assert.Contains("passage-encounter.resolved", visible, StringComparison.Ordinal);
        Assert.Contains("spatial.traversal-reversed", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("passage-encounter.opened", visible, StringComparison.Ordinal);
        Assert.DoesNotContain(ReverseMemory, visible, StringComparison.Ordinal);
    }

    private static async Task<UnifiedFirstBoardProbeSession> CreateOpenedAsync(
        string repositoryPath,
        ScenarioInstance instance)
    {
        UnifiedFirstBoardProbeSession session =
            UnifiedFirstBoardProbeSession.CreateImportedTravelingBaseline(
                repositoryPath,
                instance,
                MainLineageId);
        try
        {
            UnifiedPreparedTransaction opening = await session.PrepareOpeningAsync();
            _ = session.Commit(opening);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static UnifiedPreparedTransaction MutateResponse(
        UnifiedPreparedTransaction response,
        UnifiedResponseMutation mutation)
    {
        if (mutation == UnifiedResponseMutation.CrossIntent)
        {
            return response with
            {
                Decision = new PlayerDecision(
                    response.Decision!.DecisionId,
                    new Intent(ActionKinds.ContinueTravel)),
            };
        }

        if (mutation == UnifiedResponseMutation.CoordinatedRequest)
        {
            DecisionRequest original = response.BasisRequest!;
            var fakeId = new DecisionId("decision.alice.999");
            var fakeObservation = new Observation(
                original.Observation.ActorId,
                original.Observation.LocationId,
                checked(original.Observation.ModelTimeMs + 1),
                original.Observation.Exits,
                original.Observation.VisibleActorIds,
                original.Observation.VisibleObjectIds,
                original.Observation.KnownFacts);
            var fakeRequest = new DecisionRequest(
                fakeId,
                original.ActorId,
                checked(original.ModelTimeMs + 1),
                fakeObservation,
                original.AvailableActions);
            return response with
            {
                BasisRequest = fakeRequest,
                Decision = new PlayerDecision(fakeId, response.Decision!.Intent),
                PlayerEffect = response.PlayerEffect! with
                {
                    DecisionId = fakeId,
                    PreviousKnownFacts =
                    [
                        .. fakeObservation.KnownFacts.Select(UnifiedAuthorityFreeze.Fact),
                    ],
                },
            };
        }

        FirstBoardFact[] facts = [.. response.Batch.Facts];
        if (mutation == UnifiedResponseMutation.CrossActor)
        {
            PassageEncounterResolvedEvent resolved = Assert.IsType<PassageEncounterResolvedEvent>(
                Assert.IsType<GameBoardFact>(facts[0]).Value);
            facts[0] = new GameBoardFact(resolved with
            {
                RespondingActorId = BoardIds.Bob,
            });
        }
        else
        {
            TraversalReversedFact reversed = Assert.IsType<TraversalReversedFact>(
                Assert.IsType<SpatialBoardFact>(facts[1]).Value);
            facts[1] = new SpatialBoardFact(reversed with
            {
                EntityId = new EntityId(BoardIds.Bob),
            });
        }

        return response with
        {
            Batch = new JournalBatch<FirstBoardFact>(
                response.Batch.Instant,
                response.Batch.CauseKey,
                facts),
        };
    }

    private static UnifiedPreparedTransaction MutateOccurrence(
        UnifiedPreparedTransaction response,
        UnifiedOccurrenceMutation mutation,
        ScenarioInstance instance,
        FirstBoardWorld preWorld)
    {
        if (mutation == UnifiedOccurrenceMutation.ForgedCause)
        {
            return response with
            {
                Batch = new JournalBatch<FirstBoardFact>(
                    response.Batch.Instant,
                    CandidateKey.FromUtf8("forged.unified.occurrence"),
                    response.Batch.Facts),
            };
        }

        var later = new LogicalInstant(
            response.Batch.Instant.ModelTime,
            checked(response.Batch.Instant.CausalOrdinal + 1));
        var batch = new JournalBatch<FirstBoardFact>(
            later,
            response.Batch.CauseKey,
            response.Batch.Facts);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld expected = preWorld;
        foreach (FirstBoardFact fact in batch.Facts)
        {
            expected = reducer.Apply(expected, batch.Instant, fact);
        }

        return response with
        {
            Batch = batch,
            ExpectedPostObjective = UnifiedAuthorityFreeze.Objective(expected),
        };
    }

    private static void AssertTravelingBaseline(
        UnifiedFullAuthoritySnapshot authority,
        ScenarioInstance instance)
    {
        UnifiedObjectiveAuthoritySnapshot expectedObjective = UnifiedAuthorityFreeze.Objective(
            UnifiedProbeFixture.CreateTravelingWorld(instance));
        Assert.True(expectedObjective.Matches(authority.Objective));
        Assert.Equal(new WorldVersion(MainLineageId, 0), authority.Version);
        Assert.Null(authority.LastInstant);
        Assert.Null(authority.LastCause);
        Assert.Null(authority.ParentWorldVersion);
        Assert.Equal(instance.DefinitionSha256, authority.DefinitionSha256);
        Assert.Equal(PlayerClosureCompositionV1.CurrentId, authority.PlayerCompositionId);
        Assert.Equal(instance.CreateInitialWorld().Game.NextPersistentId,
            authority.Objective.NextPersistentId);
        Assert.Equal(instance.CreateInitialWorld().Objects.Count, authority.Objective.Objects.Count);
        Assert.Equal(instance.CreateInitialWorld().Spatial.Entities.Count,
            authority.Objective.Entities.Count);
        UnifiedActorAuthority alice = authority.Objective.Actors.Single(actor =>
            actor.Key == BoardIds.Alice);
        Assert.Equal(BoardIds.Cellar, alice.TravelGoalPlaceId);
        UnifiedEntityAuthority aliceEntity = authority.Objective.Entities.Single(entity =>
            entity.Id == BoardIds.Alice);
        Assert.Equal("traversing/1", aliceEntity.Location.Kind);
        Assert.Empty(authority.Objective.ConsumedContacts);
        Assert.Null(authority.Objective.PendingEncounter);
        UnifiedPlayerSlotAuthority player = Assert.Single(authority.PlayerSlots);
        Assert.Equal(BoardIds.Alice, player.ActorId);
        Assert.Equal(PlayerClosureCompositionV1.CurrentId, player.ProfileId);
        Assert.Equal(
            instance.Definition.Actor(BoardIds.Alice).Role.InitialMemoryShards.Select(
                shard => new PlayerClosureMemoryValue(shard.Key, shard.InitialContent)),
            player.Memory);
        Assert.Empty(player.PreviousKnownFacts);
    }

    private static void AssertOpeningBatch(JournalBatch<FirstBoardFact> batch)
    {
        Assert.Equal(new ModelTime(150_000), batch.Instant.ModelTime);
        Assert.Equal(0, batch.Instant.CausalOrdinal);
        Assert.Equal(2, batch.Facts.Count);
        PassageContactOccurredFact contact = Assert.IsType<PassageContactOccurredFact>(
            Assert.IsType<SpatialBoardFact>(batch.Facts[0]).Value);
        PassageEncounterOpenedEvent opened = Assert.IsType<PassageEncounterOpenedEvent>(
            Assert.IsType<GameBoardFact>(batch.Facts[1]).Value);
        var expectedKey = new PassageContactKey(
            new PassageId(BoardIds.TavernMarketRoad),
            new EntityId(BoardIds.Alice),
            1,
            new EntityId(BoardIds.Bob),
            1);
        Assert.Equal(expectedKey, contact.ContactKey);
        Assert.Equal(PassageContactKind.HeadOnMeeting, contact.Kind);
        Assert.Equal(contact.ContactKey, opened.ContactKey);
        Assert.Equal(contact.Kind, opened.Kind);
    }

    private static void AssertOpened(UnifiedFullAuthoritySnapshot authority)
    {
        Assert.Equal(new WorldVersion(MainLineageId, 1), authority.Version);
        Assert.Equal(new ModelTime(150_000), authority.LastInstant?.ModelTime);
        Assert.NotNull(authority.LastCause);
        Assert.Single(authority.Objective.ConsumedContacts);
        Assert.NotNull(authority.Objective.PendingEncounter);
        Assert.Equal(new ModelTime(150_000), authority.Objective.Now);
    }

    private static void AssertReverseResponse(UnifiedPreparedTransaction response)
    {
        Assert.Equal(UnifiedOperationKind.Response, response.OperationKind);
        Assert.Equal(1, response.DriverCallCount);
        DecisionRequest request = Assert.IsType<DecisionRequest>(response.BasisRequest);
        PlayerDecision decision = Assert.IsType<PlayerDecision>(response.Decision);
        UnifiedPreparedPlayerEffect effect = Assert.IsType<UnifiedPreparedPlayerEffect>(
            response.PlayerEffect);
        Assert.Equal(new LogicalInstant(new ModelTime(150_000), 1), response.Batch.Instant);
        Assert.Equal(BoardIds.Alice, request.ActorId);
        Assert.Equal(150_000, request.ModelTimeMs);
        Assert.Equal(2, response.Batch.Facts.Count);
        PassageEncounterResolvedEvent resolved = Assert.IsType<PassageEncounterResolvedEvent>(
            Assert.IsType<GameBoardFact>(response.Batch.Facts[0]).Value);
        TraversalReversedFact reversed = Assert.IsType<TraversalReversedFact>(
            Assert.IsType<SpatialBoardFact>(response.Batch.Facts[1]).Value);
        Assert.Equal(PassageEncounterResolution.Reversed, resolved.Resolution);
        Assert.Equal(BoardIds.Alice, resolved.RespondingActorId);
        Assert.Equal(new EntityId(BoardIds.Alice), reversed.EntityId);
        Assert.Equal(1, reversed.ExpectedMovementGeneration);
        Assert.Equal(request.DecisionId, decision.DecisionId);
        Assert.Equal(ActionKinds.ReverseTravel, decision.Intent.ActionKind);
        Assert.Equal(request.DecisionId, effect.DecisionId);
        Assert.Equal(1, effect.ExpectedDecisionSequence);
        Assert.Equal(2, effect.NextDecisionSequence);
        Assert.Equal(PlayerClosureCompositionV1.CurrentId, effect.PlayerProfileId);
        Assert.Equal(
            request.Observation.KnownFacts.Select(UnifiedAuthorityFreeze.Fact),
            effect.PreviousKnownFacts);
    }

    private static void AssertContinueResponse(UnifiedPreparedTransaction response)
    {
        Assert.Equal(ActionKinds.ContinueTravel, response.Decision!.Intent.ActionKind);
        PassageEncounterResolvedEvent resolved = Assert.IsType<PassageEncounterResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(response.Batch.Facts)).Value);
        Assert.Equal(PassageEncounterResolution.Continued, resolved.Resolution);
    }

    private static void AssertReversePost(
        UnifiedSemanticSnapshot snapshot,
        UnifiedPreparedTransaction transaction,
        string expectedMemory)
    {
        Assert.Equal(new WorldVersion(MainLineageId, 2), snapshot.Authority.Version);
        Assert.Equal(transaction.Batch.Instant, snapshot.Authority.LastInstant);
        Assert.Equal(transaction.Batch.CauseKey, snapshot.Authority.LastCause);
        Assert.Null(snapshot.Authority.Objective.PendingEncounter);
        Assert.Empty(snapshot.Authority.Objective.ConsumedContacts);
        UnifiedActorAuthority alice = snapshot.Authority.Objective.Actors.Single(actor =>
            actor.Key == BoardIds.Alice);
        Assert.Equal(2, alice.DecisionSequence);
        Assert.Null(alice.TravelGoalPlaceId);
        UnifiedEntityAuthority entity = snapshot.Authority.Objective.Entities.Single(value =>
            value.Id == BoardIds.Alice);
        Assert.Equal(2, entity.MovementGeneration);
        Assert.Equal(BoardIds.Tavern, entity.Location.TargetPlaceId);
        UnifiedPlayerSlotAuthority player = Assert.Single(snapshot.Authority.PlayerSlots);
        Assert.Equal(expectedMemory, WorkingContext(player));
        Assert.Equal(transaction.PlayerEffect!.PreviousKnownFacts, player.PreviousKnownFacts);
    }

    private static string WorkingContext(UnifiedPlayerSlotAuthority player) =>
        player.Memory.Single(value => value.Key == "working_context").Content;

    private sealed class RecordingTerminal : ITerminalUi
    {
        public List<string> Text { get; } = [];

        public ValueTask ShowCueAsync(PresentationCue cue, CancellationToken cancellationToken)
        {
            Text.Add(cue.Code + " " + cue.Text);
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowDeveloperOverlayAsync(
            DeveloperOverlay overlay,
            CancellationToken cancellationToken)
        {
            Text.Add(overlay.Code + " " + overlay.Text);
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowStatusAsync(TerminalStatus status, CancellationToken cancellationToken)
        {
            Text.Add(status.Kind + " " + status.Text);
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowPromptAsync(DecisionRequest request, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask ShowInputErrorAsync(string message, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<string?> ReadCommandAsync(
            DecisionId decisionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
    }

    private sealed class NoDelayPacer : IPresentationPacer
    {
        public ValueTask PaceAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}

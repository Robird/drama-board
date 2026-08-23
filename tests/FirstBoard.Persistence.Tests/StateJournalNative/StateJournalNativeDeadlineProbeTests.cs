using System.Text;
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
    private const ulong WorldSeed = 42;

    [Fact]
    public async Task Deadline_CoCommitsExactLedgerAndGraph_ThenReopensWithoutReplay()
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

            DeadlineTransactionProbeV1 transaction = DeadlineProbePlanner.Plan(session.Head);
            await AssertMatchesCurrentRuleAsync(instance, transaction);
            DeadlineProbeView expiredView = session.Head;
            receipt = session.Commit(transaction);
            committed = session.Snapshot();

            Assert.Equal(receipt.Parent, session.HeadParentAddress);
            Assert.Equal(receipt.Head, session.HeadAddress);
            Assert.NotEqual(receipt.Parent, receipt.Head);
            Assert.Throws<InvalidOperationException>(() => _ = expiredView.CellarSealed);
            Assert.Equal(
                DeadlineTransactionProbeV1Codec.ComputeDigest(receipt.CanonicalEnvelope),
                receipt.TransactionDigest);
            AssertCommittedDeadline(committed, transaction, receipt.CanonicalEnvelope);
        }

        using DeadlineProbeSession reopened = DeadlineProbeSession.Open(repositoryPath, instance);
        Assert.Equal(committed, reopened.Snapshot());
        Assert.Equal(receipt.Head, reopened.HeadAddress);
        Assert.Equal(receipt.Parent, reopened.HeadParentAddress);
    }

    [Fact]
    public void LedgerOnlyRebuild_FromDeterministicGenesis_MatchesMaterializedHead()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string sourcePath = Path.Combine(directory.Path, "source");
        string rebuiltPath = Path.Combine(directory.Path, "rebuilt");
        byte[][] canonicalLedger;

        using (DeadlineProbeSession source = DeadlineProbeSession.Create(
                   sourcePath,
                   instance,
                   LineageId))
        {
            source.Commit(DeadlineProbePlanner.Plan(source.Head));
            canonicalLedger = source.CopyCanonicalLedger();
        }

        DeadlineProbeSemanticSnapshot rebuiltSnapshot;
        using (DeadlineProbeSession rebuilt = DeadlineProbeSession.Create(
                   rebuiltPath,
                   instance,
                   LineageId))
        {
            foreach (byte[] envelope in canonicalLedger)
            {
                rebuilt.Commit(DeadlineTransactionProbeV1Codec.Decode(envelope));
            }

            rebuiltSnapshot = rebuilt.Snapshot();
        }

        using DeadlineProbeSession reopenedSource = DeadlineProbeSession.Open(sourcePath, instance);
        Assert.Equal(reopenedSource.Snapshot(), rebuiltSnapshot);
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
        byte[] proposedEnvelope;
        Atelia.StateJournal.CommitAddress expectedParent;

        using (DeadlineProbeSession session = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            DeadlineTransactionProbeV1 transaction = DeadlineProbePlanner.Plan(session.Head);
            proposedEnvelope = DeadlineTransactionProbeV1Codec.Encode(transaction);
            expectedParent = session.HeadAddress;
            DeadlineProbeView expiredView = session.Head;

            Assert.Throws<InvalidOperationException>(() => session.Commit(transaction, fault));
            Assert.Throws<InvalidOperationException>(() => _ = expiredView.CellarSealed);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        DeadlineProbeResolution resolution = DeadlineProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            expectedParent,
            proposedEnvelope);
        Assert.Equal(DeadlineProbeOutcome.NotCommitted, resolution.Outcome);
        Assert.Equal(expectedVersion, resolution.Snapshot.Version);
        Assert.False(resolution.Snapshot.CellarSealed);
        Assert.Equal(new PassageEntryAccess(true, true), resolution.Snapshot.CellarGateAccess);
        Assert.Equal(string.Empty, resolution.Snapshot.CanonicalLedgerBase64);
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
            byte[] proposedEnvelope = DeadlineTransactionProbeV1Codec.Encode(transaction);
            Atelia.StateJournal.CommitAddress expectedParent = session.HeadAddress;

            File.Delete(reflogPath);
            Directory.CreateDirectory(reflogPath);
            failure = Assert.Throws<DeadlineProbeCommitFailedException>(
                () => session.Commit(transaction));

            Assert.Equal(expectedParent, failure.ExpectedParent);
            Assert.Equal(proposedEnvelope, failure.ProposedEnvelope);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        Directory.Delete(reflogPath);
        DeadlineProbeResolution resolution = DeadlineProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            failure.ExpectedParent,
            failure.ProposedEnvelope);

        Assert.Equal(DeadlineProbeOutcome.Committed, resolution.Outcome);
        AssertCommittedDeadline(
            resolution.Snapshot,
            transaction,
            failure.ProposedEnvelope);
    }

    [Fact]
    public void CodecAndOpen_RejectNonCanonicalOrMismatchedAuthority()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "authority");
        byte[] canonical;

        using (DeadlineProbeSession session = DeadlineProbeSession.Create(
                   repositoryPath,
                   instance,
                   LineageId))
        {
            canonical = DeadlineTransactionProbeV1Codec.Encode(
                DeadlineProbePlanner.Plan(session.Head));
        }

        string withUnknownProperty = Encoding.UTF8.GetString(canonical).Replace(
            "{\"schema\"",
            "{\"unknown\":1,\"schema\"",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            DeadlineTransactionProbeV1Codec.Decode(
                Encoding.UTF8.GetBytes(withUnknownProperty)));

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
        byte[] canonicalEnvelope)
    {
        Assert.Equal(
            new WorldVersion(
                transaction.ParentVersion.LineageId,
                checked(transaction.ParentVersion.TransitionCount + 1)),
            snapshot.Version);
        Assert.Equal(transaction.Batch.Instant, snapshot.LastInstant);
        Assert.Equal(transaction.Batch.Instant.ModelTime, snapshot.Now);
        Assert.True(snapshot.CellarSealed);
        PassageEntryAccessChangedFact changed = Assert.IsType<PassageEntryAccessChangedFact>(
            Assert.IsType<SpatialBoardFact>(transaction.Batch.Facts[1]).Value);
        Assert.Equal(changed.ResultAccess, snapshot.CellarGateAccess);
        Assert.Equal(
            Convert.ToBase64String(canonicalEnvelope),
            snapshot.CanonicalLedgerBase64);
    }
}

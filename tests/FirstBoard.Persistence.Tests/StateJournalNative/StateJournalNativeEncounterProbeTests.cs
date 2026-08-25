using Atelia.StateJournal;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

public sealed class StateJournalNativeEncounterProbeTests
{
    private const long MainLineageId = 91_001;
    private const long ChildLineageId = 91_002;
    private const ulong WorldSeed = 401;
    private const string ChildBranch = "encounter-child";

    private static readonly string[] DriverActorIds = [BoardIds.Alice];

    [Fact]
    public async Task Encounter_UsesProductionOrder_AndReopensExactGraphWithoutReplay()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "encounter-reopen");
        EncounterProbeSemanticSnapshot baseline;
        EncounterProbeSemanticSnapshot committed;
        EncounterProbeCommitReceipt receipt;
        EncounterTransactionProbeV1 transaction;

        using (EncounterProbeSession session = EncounterProbeSession.CreateTravelingBaseline(
                   repositoryPath,
                   instance,
                   MainLineageId,
                   DriverActorIds))
        {
            baseline = session.Snapshot();
            AssertTravelingBaseline(baseline, MainLineageId);
            transaction = await PlanAndAssertProductionExactAsync(session.Head, instance);
            EncounterProbeView expiredView = session.Head;

            receipt = session.Commit(transaction);
            committed = session.Snapshot();

            Assert.Equal(receipt.Parent, session.HeadParentAddress);
            Assert.Equal(receipt.Head, session.HeadAddress);
            Assert.NotEqual(receipt.Parent, receipt.Head);
            Assert.Throws<InvalidOperationException>(() => _ = expiredView.Version);
            AssertEncounterOpened(committed, baseline, transaction);
        }

        using EncounterProbeSession reopened = EncounterProbeSession.Open(
            repositoryPath,
            instance,
            DriverActorIds);
        EncounterProbeSemanticSnapshot actual = reopened.Snapshot();
        Assert.True(committed.Authority.Matches(actual.Authority));
        Assert.Equal(committed.CommitSummary, actual.CommitSummary);
        Assert.Equal(receipt.Head, reopened.HeadAddress);
        Assert.Equal(receipt.Parent, reopened.HeadParentAddress);
    }

    [Fact]
    public async Task Encounter_ReversedFacts_TriggerRealGuard_AndReopenExactParent()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "encounter-reversed");
        EncounterProbeAuthoritySnapshot parent;
        CommitAddress parentAddress;

        using (EncounterProbeSession session = EncounterProbeSession.CreateTravelingBaseline(
                   repositoryPath,
                   instance,
                   MainLineageId,
                   DriverActorIds))
        {
            parent = session.Snapshot().Authority;
            parentAddress = session.HeadAddress;
            EncounterTransactionProbeV1 planned = await EncounterProbePlanner.PlanAsync(
                session.Head);
            var reversed = new EncounterTransactionProbeV1(
                planned.ParentVersion,
                new JournalBatch<FirstBoardFact>(
                    planned.Batch.Instant,
                    planned.Batch.CauseKey,
                    [planned.Batch.Facts[1], planned.Batch.Facts[0]]));

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => session.Commit(reversed));

            Assert.Contains(
                "only after its Spatial contact was consumed",
                failure.ToString(),
                StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        using EncounterProbeSession reopened = EncounterProbeSession.Open(
            repositoryPath,
            instance,
            DriverActorIds);
        Assert.Equal(parentAddress, reopened.HeadAddress);
        Assert.True(parent.Matches(reopened.Snapshot().Authority));
    }

    [Theory]
    [InlineData(EncounterProbeFault.AfterFirstFact)]
    [InlineData(EncounterProbeFault.AtBatchEndValidation)]
    internal async Task Encounter_WorkingFailure_Poisons_AndReopensExactParent(
        EncounterProbeFault fault)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, $"encounter-{fault}");
        EncounterProbeAuthoritySnapshot parent;
        CommitAddress parentAddress;

        using (EncounterProbeSession session = EncounterProbeSession.CreateTravelingBaseline(
                   repositoryPath,
                   instance,
                   MainLineageId,
                   DriverActorIds))
        {
            parent = session.Snapshot().Authority;
            parentAddress = session.HeadAddress;
            EncounterTransactionProbeV1 transaction =
                await EncounterProbePlanner.PlanAsync(session.Head);

            _ = Assert.Throws<InvalidOperationException>(
                () => session.Commit(transaction, fault));
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        using EncounterProbeSession reopened = EncounterProbeSession.Open(
            repositoryPath,
            instance,
            DriverActorIds);
        Assert.Equal(parentAddress, reopened.HeadAddress);
        Assert.True(parent.Matches(reopened.Snapshot().Authority));
    }

    [Fact]
    public async Task HistoricalFork_UsesSameRepository_AndPreservesTraversalClosure()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "encounter-fork");
        EncounterProbeSemanticSnapshot mainBaseline;
        CommitAddress baselineAddress;

        using (EncounterProbeSession main = EncounterProbeSession.CreateTravelingBaseline(
                   repositoryPath,
                   instance,
                   MainLineageId,
                   DriverActorIds))
        {
            mainBaseline = main.Snapshot();
            baselineAddress = main.HeadAddress;
        }

        EncounterProbeSemanticSnapshot childCommitted;
        using (EncounterProbeSession child = EncounterProbeSession.CreateChildBranch(
                   repositoryPath,
                   instance,
                   DriverActorIds,
                   ChildBranch,
                   baselineAddress,
                   mainBaseline.Authority.Version,
                   ChildLineageId))
        {
            EncounterProbeSemanticSnapshot lineageStart = child.Snapshot();
            Assert.Equal(baselineAddress, child.HeadParentAddress);
            Assert.Equal(
                new WorldVersion(ChildLineageId, 0),
                lineageStart.Authority.Version);
            Assert.Equal(
                mainBaseline.Authority.Version,
                lineageStart.Authority.ParentWorldVersion);
            Assert.Equal(
                EncounterProbeCommitKinds.LineageStart,
                lineageStart.Authority.CommitKind);
            Assert.Equal(
                mainBaseline.Authority.Traversals,
                lineageStart.Authority.Traversals);
            Assert.Empty(lineageStart.Authority.ConsumedContacts);
            Assert.Null(lineageStart.Authority.PendingEncounter);

            EncounterTransactionProbeV1 transaction =
                await PlanAndAssertProductionExactAsync(child.Head, instance);
            _ = child.Commit(transaction);
            childCommitted = child.Snapshot();
            AssertEncounterOpened(childCommitted, lineageStart, transaction);
        }

        using (EncounterProbeSession main = EncounterProbeSession.Open(
                   repositoryPath,
                   instance,
                   DriverActorIds))
        {
            Assert.Equal(baselineAddress, main.HeadAddress);
            Assert.True(mainBaseline.Authority.Matches(main.Snapshot().Authority));
        }

        using EncounterProbeSession reopenedChild = EncounterProbeSession.Open(
            repositoryPath,
            instance,
            DriverActorIds,
            ChildBranch);
        Assert.True(childCommitted.Authority.Matches(
            reopenedChild.Snapshot().Authority));
        Assert.Equal(
            mainBaseline.Authority.Version,
            reopenedChild.Snapshot().Authority.ParentWorldVersion);
    }

    [Fact]
    public async Task ReflogFailure_AfterPublication_ResolvesExactNestedAuthority()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        string repositoryPath = Path.Combine(directory.Path, "encounter-ambiguous");
        string reflogPath = Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            "main.reflog.jsonl");
        EncounterProbeCommitFailedException failure;
        EncounterTransactionProbeV1 transaction;

        using (EncounterProbeSession session = EncounterProbeSession.CreateTravelingBaseline(
                   repositoryPath,
                   instance,
                   MainLineageId,
                   DriverActorIds))
        {
            transaction = await EncounterProbePlanner.PlanAsync(session.Head);
            CommitAddress expectedParent = session.HeadAddress;

            File.Delete(reflogPath);
            Directory.CreateDirectory(reflogPath);
            failure = Assert.Throws<EncounterProbeCommitFailedException>(
                () => session.Commit(transaction));

            Assert.Equal(expectedParent, failure.ExpectedParent);
            Assert.Equal(
                RepositoryCommitFailurePhase.AppendReflog,
                failure.FailurePhase);
            Assert.Equal(
                RepositoryCommitPublicationState.Published,
                failure.PublicationState);
            Assert.True(failure.RequiresRepositoryReopen);
            Assert.False(failure.CanRetryTransparently);
            Assert.True(failure.MayHavePublished);
            Assert.NotEqual(failure.ExpectedParent, failure.CandidateAddress);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }

        Directory.Delete(reflogPath);
        EncounterProbeResolution resolution = EncounterProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            instance,
            DriverActorIds,
            failure);

        Assert.Equal(EncounterProbeOutcome.Committed, resolution.Outcome);
        Assert.True(failure.ChildAuthority.Matches(resolution.Snapshot.Authority));
        Assert.Equal(
            AssertContact(transaction),
            Assert.Single(resolution.Snapshot.Authority.ConsumedContacts));
        Assert.NotNull(resolution.Snapshot.Authority.PendingEncounter);
    }

    private static async Task<EncounterTransactionProbeV1> PlanAndAssertProductionExactAsync(
        EncounterProbeView view,
        ScenarioInstance instance)
    {
        FirstBoardWorld world = view.CreateOracleWorld();
        var rules = new SimulationRules(
            instance.WorldSeed,
            maxTransitionsPerModelTime: 100);
        var inner = new SpatialContactOccurrenceRule(instance.Graph);
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new NoCallPlayerDriver(),
        };
        var outer = new FirstBoardPassageEncounterRule(instance.Graph, drivers);
        OccurrenceCandidate<PassageContactOccurrenceData> innerCandidate =
            Assert.Single(inner.Forecast(world.Spatial, rules));
        OccurrenceCandidate<BoardCandidate> outerCandidate =
            Assert.Single(outer.Forecast(world, rules));
        PassageEncounterOpeningCandidate opening =
            Assert.IsType<PassageEncounterOpeningCandidate>(outerCandidate.Data);

        Assert.Equal(new ModelTime(150_000), innerCandidate.Due.ModelTime);
        Assert.Equal(PassageContactKind.HeadOnMeeting, innerCandidate.Data.Kind);
        Assert.Equal(innerCandidate.Key, outerCandidate.Key);
        Assert.Equal(innerCandidate.Due, outerCandidate.Due);
        Assert.Equal(innerCandidate.Data, opening.Value);

        TransitionDraft<GraphSpatialFact> innerDraft = await inner.PlanSelectedAsync(
            world.Spatial,
            innerCandidate,
            CancellationToken.None);
        TransitionDraft<FirstBoardFact> outerDraft = await outer.PlanSelectedAsync(
            world,
            outerCandidate,
            CancellationToken.None);
        Assert.Equal(2, outerDraft.Facts.Count);
        Assert.Equal(
            Assert.Single(innerDraft.Facts),
            Assert.IsType<SpatialBoardFact>(outerDraft.Facts[0]).Value);
        PassageEncounterOpenedEvent opened = Assert.IsType<PassageEncounterOpenedEvent>(
            Assert.IsType<GameBoardFact>(outerDraft.Facts[1]).Value);
        Assert.Equal(innerCandidate.Data.ContactKey, opened.ContactKey);
        Assert.Equal(innerCandidate.Data.Kind, opened.Kind);

        EncounterTransactionProbeV1 transaction =
            await EncounterProbePlanner.PlanAsync(view);
        Assert.Equal(view.Version, transaction.ParentVersion);
        Assert.Equal(
            new LogicalInstant(outerCandidate.Due.ModelTime, causalOrdinal: 0),
            transaction.Batch.Instant);
        Assert.Equal(outerCandidate.Key, transaction.Batch.CauseKey);
        Assert.Equal(outerDraft.Facts, transaction.Batch.Facts);
        return transaction;
    }

    private static void AssertTravelingBaseline(
        EncounterProbeSemanticSnapshot snapshot,
        long expectedLineageId)
    {
        EncounterProbeAuthoritySnapshot authority = snapshot.Authority;
        Assert.Equal(
            "firstboard.statejournal-encounter-root/1",
            authority.SchemaId);
        Assert.Equal(EncounterProbeFixture.FixtureId, authority.FixtureId);
        Assert.Equal(DriverActorIds, authority.DriverActorIds);
        Assert.Equal(new WorldVersion(expectedLineageId, 0), authority.Version);
        Assert.Null(authority.ParentWorldVersion);
        Assert.Null(authority.LastInstant);
        Assert.Null(authority.LastCause);
        Assert.Equal(EncounterProbeCommitKinds.FixtureBaseline, authority.CommitKind);
        Assert.Equal(ModelTime.Zero, authority.Now);
        Assert.Collection(
            authority.Actors,
            actor =>
            {
                Assert.Equal(BoardIds.Alice, actor.Key);
                Assert.Equal(1, actor.PersistentId);
            },
            actor =>
            {
                Assert.Equal(BoardIds.Bob, actor.Key);
                Assert.Equal(2, actor.PersistentId);
            });
        AssertTraversalClosure(authority.Traversals);
        Assert.Empty(authority.ConsumedContacts);
        Assert.Null(authority.PendingEncounter);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CommitSummary));
    }

    private static void AssertEncounterOpened(
        EncounterProbeSemanticSnapshot committed,
        EncounterProbeSemanticSnapshot parent,
        EncounterTransactionProbeV1 transaction)
    {
        EncounterProbeAuthoritySnapshot authority = committed.Authority;
        Assert.Equal(
            new WorldVersion(
                transaction.ParentVersion.LineageId,
                checked(transaction.ParentVersion.TransitionCount + 1)),
            authority.Version);
        Assert.Equal(parent.Authority.ParentWorldVersion, authority.ParentWorldVersion);
        Assert.Equal(transaction.Batch.Instant, authority.LastInstant);
        Assert.Equal(transaction.Batch.CauseKey, authority.LastCause);
        Assert.Equal(transaction.Batch.Instant.ModelTime, authority.Now);
        Assert.Equal(
            EncounterProbeCommitKinds.ObjectiveTransition,
            authority.CommitKind);
        Assert.Equal(parent.Authority.Actors, authority.Actors);
        Assert.Equal(parent.Authority.Traversals, authority.Traversals);
        EncounterProbeContactSnapshot expectedContact = AssertContact(transaction);
        Assert.Equal(expectedContact, Assert.Single(authority.ConsumedContacts));
        EncounterProbePendingSnapshot pending = Assert.IsType<EncounterProbePendingSnapshot>(
            authority.PendingEncounter);
        Assert.Equal(expectedContact, pending.Contact);
        Assert.Equal(PassageContactKind.HeadOnMeeting, pending.Kind);
        Assert.False(string.IsNullOrWhiteSpace(committed.CommitSummary));
    }

    private static EncounterProbeContactSnapshot AssertContact(
        EncounterTransactionProbeV1 transaction)
    {
        PassageContactOccurredFact contact = Assert.IsType<PassageContactOccurredFact>(
            Assert.IsType<SpatialBoardFact>(transaction.Batch.Facts[0]).Value);
        PassageContactKey key = contact.ContactKey;
        Assert.Equal(BoardIds.TavernMarketRoad, key.PassageId.Value);
        Assert.Equal(BoardIds.Alice, key.EntityA.Value);
        Assert.Equal(1, key.MovementGenerationA);
        Assert.Equal(BoardIds.Bob, key.EntityB.Value);
        Assert.Equal(1, key.MovementGenerationB);
        Assert.Equal(PassageContactKind.HeadOnMeeting, contact.Kind);
        return new EncounterProbeContactSnapshot(
            key.PassageId.Value,
            key.EntityA.Value,
            key.MovementGenerationA,
            key.EntityB.Value,
            key.MovementGenerationB);
    }

    private static void AssertTraversalClosure(
        IReadOnlyList<EncounterProbeTraversalSnapshot> traversals)
    {
        Assert.Collection(
            traversals,
            alice =>
            {
                Assert.Equal(BoardIds.Alice, alice.EntityId);
                Assert.Equal(1, alice.MovementGeneration);
                Assert.Equal(BoardIds.TavernMarketRoad, alice.PassageId);
                Assert.Equal(0, alice.AnchorOffset);
                Assert.Equal(ModelTime.Zero, alice.AnchorTime);
                Assert.Equal(BoardIds.Market, alice.TargetPlaceId);
                Assert.Equal(BoardTiming.TravelSpeed, alice.SpeedSnapshot);
                Assert.Equal(new ModelTime(300_000), alice.ArrivalDue);
            },
            bob =>
            {
                Assert.Equal(BoardIds.Bob, bob.EntityId);
                Assert.Equal(1, bob.MovementGeneration);
                Assert.Equal(BoardIds.TavernMarketRoad, bob.PassageId);
                Assert.Equal(300_000, bob.AnchorOffset);
                Assert.Equal(ModelTime.Zero, bob.AnchorTime);
                Assert.Equal(BoardIds.Tavern, bob.TargetPlaceId);
                Assert.Equal(BoardTiming.TravelSpeed, bob.SpeedSnapshot);
                Assert.Equal(new ModelTime(300_000), bob.ArrivalDue);
            });
    }

    private sealed class NoCallPlayerDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Encounter opening planning must not call a Player driver.");
    }
}

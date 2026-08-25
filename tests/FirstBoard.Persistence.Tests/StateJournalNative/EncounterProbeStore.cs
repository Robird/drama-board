using Atelia;
using Atelia.StateJournal;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

using EncounterContactStorageKey =
    (string PassageId, string EntityA, long GenerationA, string EntityB, long GenerationB);

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

internal sealed record EncounterTransactionProbeV1(
    WorldVersion ParentVersion,
    JournalBatch<FirstBoardFact> Batch);

internal enum EncounterProbeFault
{
    None,
    AfterFirstFact,
    AtBatchEndValidation,
}

internal enum EncounterProbeOutcome
{
    NotCommitted,
    Committed,
}

internal enum EncounterProbeOperationKind
{
    ObjectiveTransition,
    LineageStart,
}

internal static class EncounterProbeCommitKinds
{
    public const string FixtureBaseline = "fixture-baseline";
    public const string LineageStart = "lineage-start";
    public const string ObjectiveTransition = "objective-transition";
}

internal readonly record struct EncounterProbeActorIdentity(
    string Key,
    long PersistentId);

internal readonly record struct EncounterProbeContactSnapshot(
    string PassageId,
    string EntityA,
    long GenerationA,
    string EntityB,
    long GenerationB);

internal readonly record struct EncounterProbeTraversalSnapshot(
    string EntityId,
    long MovementGeneration,
    string PassageId,
    long AnchorOffset,
    ModelTime AnchorTime,
    string TargetPlaceId,
    long SpeedSnapshot,
    ModelTime ArrivalDue);

internal sealed record EncounterProbePendingSnapshot(
    EncounterProbeContactSnapshot Contact,
    PassageContactKind Kind);

internal sealed record EncounterProbeAuthoritySnapshot(
    string SchemaId,
    string DefinitionSha256,
    string RulesetId,
    ulong WorldSeed,
    string FixtureId,
    IReadOnlyList<string> DriverActorIds,
    WorldVersion Version,
    WorldVersion? ParentWorldVersion,
    LogicalInstant? LastInstant,
    CandidateKey? LastCause,
    string CommitKind,
    ModelTime Now,
    IReadOnlyList<EncounterProbeActorIdentity> Actors,
    IReadOnlyList<EncounterProbeTraversalSnapshot> Traversals,
    IReadOnlyList<EncounterProbeContactSnapshot> ConsumedContacts,
    EncounterProbePendingSnapshot? PendingEncounter)
{
    public bool Matches(EncounterProbeAuthoritySnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal) &&
            string.Equals(
                DefinitionSha256,
                other.DefinitionSha256,
                StringComparison.Ordinal) &&
            string.Equals(RulesetId, other.RulesetId, StringComparison.Ordinal) &&
            WorldSeed == other.WorldSeed &&
            string.Equals(FixtureId, other.FixtureId, StringComparison.Ordinal) &&
            DriverActorIds.SequenceEqual(other.DriverActorIds, StringComparer.Ordinal) &&
            Version == other.Version &&
            ParentWorldVersion == other.ParentWorldVersion &&
            LastInstant == other.LastInstant &&
            LastCause == other.LastCause &&
            string.Equals(CommitKind, other.CommitKind, StringComparison.Ordinal) &&
            Now == other.Now &&
            Actors.SequenceEqual(other.Actors) &&
            Traversals.SequenceEqual(other.Traversals) &&
            ConsumedContacts.SequenceEqual(other.ConsumedContacts) &&
            PendingEncounter == other.PendingEncounter;
    }
}

internal sealed record EncounterProbeSemanticSnapshot(
    EncounterProbeAuthoritySnapshot Authority,
    string? CommitSummary);

internal sealed record EncounterProbeCommitReceipt(
    CommitAddress Parent,
    CommitAddress Head);

internal sealed record EncounterProbeResolution(
    EncounterProbeOutcome Outcome,
    EncounterProbeSemanticSnapshot Snapshot);

internal sealed class EncounterProbeBaselineCommitFailedException : InvalidOperationException
{
    public EncounterProbeBaselineCommitFailedException(
        RepositoryCommitError error,
        EncounterProbeAuthoritySnapshot proposedAuthority)
        : base(
            "StateJournal encounter baseline commit failed; the repository path must be " +
            "reopened and classified before it is reused.")
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(proposedAuthority);
        CommitError = error;
        ProposedAuthority = proposedAuthority;
    }

    public RepositoryCommitError CommitError { get; }

    public EncounterProbeAuthoritySnapshot ProposedAuthority { get; }
}

internal sealed class EncounterProbeCommitFailedException : InvalidOperationException
{
    public EncounterProbeCommitFailedException(
        RepositoryCommitError error,
        string branchName,
        CommitAddress expectedParent,
        EncounterProbeAuthoritySnapshot parentAuthority,
        EncounterProbeAuthoritySnapshot childAuthority,
        EncounterProbeOperationKind operationKind)
        : base($"StateJournal encounter commit outcome is unknown: {error}")
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentNullException.ThrowIfNull(parentAuthority);
        ArgumentNullException.ThrowIfNull(childAuthority);

        if (!string.Equals(error.BranchName, branchName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Structured StateJournal commit error reported branch " +
                $"'{error.BranchName}', but caller captured '{branchName}'.");
        }

        if (error.ExpectedHeadAddress != expectedParent)
        {
            throw new InvalidDataException(
                $"Structured StateJournal commit error reported expected HEAD " +
                $"'{error.ExpectedHeadAddress}', but caller captured '{expectedParent}'.");
        }

        BranchName = branchName;
        ExpectedParent = expectedParent;
        CandidateAddress = error.CandidateAddress;
        FailurePhase = error.FailurePhase;
        PublicationState = error.PublicationState;
        RequiresRepositoryReopen = error.RequiresRepositoryReopen;
        CanRetryTransparently = error.CanRetryTransparently;
        MayHavePublished = error.MayHavePublished;
        ParentAuthority = parentAuthority;
        ChildAuthority = childAuthority;
        OperationKind = operationKind;
    }

    public string BranchName { get; }

    public CommitAddress ExpectedParent { get; }

    public CommitAddress CandidateAddress { get; }

    public RepositoryCommitFailurePhase FailurePhase { get; }

    public RepositoryCommitPublicationState PublicationState { get; }

    public bool RequiresRepositoryReopen { get; }

    public bool CanRetryTransparently { get; }

    public bool MayHavePublished { get; }

    public EncounterProbeAuthoritySnapshot ParentAuthority { get; }

    public EncounterProbeAuthoritySnapshot ChildAuthority { get; }

    public EncounterProbeOperationKind OperationKind { get; }
}

/// <summary>
/// Test-only StateJournal-native session for the real two-fact encounter opening.
/// Its imported traveling baseline is deliberately not called FirstBoard Genesis.
/// </summary>
internal sealed class EncounterProbeSession : IDisposable
{
    public const string MainBranch = "main";

    private readonly ScenarioInstance _instance;
    private readonly string[] _driverActorIds;
    private readonly string _branchName;
    private readonly Repository _repository;
    private readonly DurableEncounterProbeRootV1 _root;
    private bool _disposed;
    private bool _poisoned;
    private int _viewEpoch;

    private EncounterProbeSession(
        ScenarioInstance instance,
        IEnumerable<string> driverActorIds,
        string branchName,
        Repository repository,
        DurableEncounterProbeRootV1 root)
    {
        _instance = instance;
        _driverActorIds = CanonicalDriverActorIds(driverActorIds);
        _branchName = branchName;
        _repository = repository;
        _root = root;
        _root.ValidateComplete();
    }

    public EncounterProbeView Head
    {
        get
        {
            RequireActive();
            return new EncounterProbeView(this, _viewEpoch);
        }
    }

    public CommitAddress HeadAddress
    {
        get
        {
            RequireActive();
            return _root.HeadAddress;
        }
    }

    public CommitAddress? HeadParentAddress
    {
        get
        {
            RequireActive();
            return _root.HeadParentAddress;
        }
    }

    public static EncounterProbeSession CreateTravelingBaseline(
        string repositoryPath,
        ScenarioInstance instance,
        long lineageId,
        IEnumerable<string> driverActorIds)
    {
        ValidateArguments(repositoryPath, instance, driverActorIds);
        string[] canonicalDrivers = CanonicalDriverActorIds(driverActorIds);
        FirstBoardWorld fixture = EncounterProbeFixture.CreateTravelingPrefix(instance);

        Repository repository = Require(
            Repository.Create(repositoryPath),
            "create StateJournal encounter repository");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(MainBranch),
                "create encounter main branch");
            DurableEncounterProbeRootV1 root = DurableEncounterProbeRootV1.Create(
                revision,
                instance,
                lineageId,
                canonicalDrivers,
                fixture);
            root.ValidateComplete();
            EncounterProbeAuthoritySnapshot proposedAuthority = root.AuthoritySnapshot();
            AteliaResult<CommitAddress> commit = repository.Commit(root.GraphRoot);
            if (commit.IsFailure)
            {
                repository.Dispose();
                if (commit.Error is RepositoryCommitError structured)
                {
                    throw new EncounterProbeBaselineCommitFailedException(
                        structured,
                        proposedAuthority);
                }

                throw new InvalidOperationException(
                    "StateJournal encounter baseline commit failed before producing a " +
                    $"structured candidate: {commit.Error}");
            }

            return new EncounterProbeSession(
                instance,
                canonicalDrivers,
                MainBranch,
                repository,
                root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static EncounterProbeSession Open(
        string repositoryPath,
        ScenarioInstance instance,
        IEnumerable<string> driverActorIds,
        string branchName = MainBranch)
    {
        ValidateArguments(repositoryPath, instance, driverActorIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        string[] canonicalDrivers = CanonicalDriverActorIds(driverActorIds);

        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open StateJournal encounter repository");
        try
        {
            Revision revision = Require(
                repository.CheckoutBranch(branchName),
                $"checkout encounter branch '{branchName}'");
            DurableEncounterProbeRootV1 root = DurableEncounterProbeRootV1.Open(
                revision,
                instance,
                canonicalDrivers);
            return new EncounterProbeSession(
                instance,
                canonicalDrivers,
                branchName,
                repository,
                root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static EncounterProbeSession CreateChildBranch(
        string repositoryPath,
        ScenarioInstance instance,
        IEnumerable<string> driverActorIds,
        string branchName,
        CommitAddress fromCommit,
        WorldVersion expectedSourceVersion,
        long childLineageId)
    {
        ValidateArguments(repositoryPath, instance, driverActorIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        string[] canonicalDrivers = CanonicalDriverActorIds(driverActorIds);

        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open StateJournal encounter repository for historical fork");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(branchName, fromCommit),
                $"create encounter branch '{branchName}' from '{fromCommit}'");
            DurableEncounterProbeRootV1 root = DurableEncounterProbeRootV1.Open(
                revision,
                instance,
                canonicalDrivers);
            if (root.Version != expectedSourceVersion)
            {
                throw new InvalidDataException(
                    $"Historical commit '{fromCommit}' has WorldVersion '{root.Version}', " +
                    $"not expected '{expectedSourceVersion}'.");
            }

            CommitAddress expectedParent = root.HeadAddress;
            EncounterProbeAuthoritySnapshot parentAuthority = root.AuthoritySnapshot();
            root.ApplyChildLineageBoundary(childLineageId);
            root.ValidateComplete();
            EncounterProbeAuthoritySnapshot childAuthority = root.AuthoritySnapshot();
            AteliaResult<CommitAddress> commit = repository.Commit(root.GraphRoot);
            if (commit.IsFailure)
            {
                repository.Dispose();
                if (commit.Error is not RepositoryCommitError error)
                {
                    throw new InvalidOperationException(
                        "StateJournal lineage commit failed before producing a structured " +
                        $"candidate: {commit.Error}");
                }

                throw new EncounterProbeCommitFailedException(
                    error,
                    branchName,
                    expectedParent,
                    parentAuthority,
                    childAuthority,
                    EncounterProbeOperationKind.LineageStart);
            }

            return new EncounterProbeSession(
                instance,
                canonicalDrivers,
                branchName,
                repository,
                root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public EncounterProbeCommitReceipt Commit(
        EncounterTransactionProbeV1 transaction,
        EncounterProbeFault fault = EncounterProbeFault.None)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        RequireActive();
        RequireProductionOpeningTransaction(transaction);
        CommitAddress expectedParent = _root.HeadAddress;
        _root.RequireExpectedParent(transaction.ParentVersion);
        EncounterProbeAuthoritySnapshot parentAuthority = _root.AuthoritySnapshot();
        AdvanceViewEpoch();

        try
        {
            for (int index = 0; index < transaction.Batch.Facts.Count; index++)
            {
                _root.Apply(
                    transaction.Batch.Instant,
                    transaction.Batch.Facts[index]);
                if (fault == EncounterProbeFault.AfterFirstFact && index == 0)
                {
                    throw new InvalidOperationException(
                        "Injected failure after the Spatial contact mutated the private graph.");
                }
            }

            _root.ValidateBatchEnd(
                injectFailure: fault == EncounterProbeFault.AtBatchEndValidation);
            _root.AdvanceFrontier(transaction);
            _root.ValidateComplete();
            EncounterProbeAuthoritySnapshot childAuthority = _root.AuthoritySnapshot();

            CommitAddress head = CommitGraph(
                expectedParent,
                parentAuthority,
                childAuthority,
                EncounterProbeOperationKind.ObjectiveTransition);
            return new EncounterProbeCommitReceipt(expectedParent, head);
        }
        catch (EncounterProbeCommitFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Poison();
            throw new InvalidOperationException(
                "The encounter transaction failed after private working mutation; " +
                "the Session is poisoned and must be reopened from HEAD.",
                exception);
        }
    }

    public EncounterProbeSemanticSnapshot Snapshot()
    {
        RequireActive();
        return _root.Snapshot();
    }

    public static EncounterProbeResolution ResolveUnknownOutcome(
        string repositoryPath,
        ScenarioInstance instance,
        IEnumerable<string> driverActorIds,
        EncounterProbeCommitFailedException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        using EncounterProbeSession reopened = Open(
            repositoryPath,
            instance,
            driverActorIds,
            failure.BranchName);
        EncounterProbeSemanticSnapshot snapshot = reopened.Snapshot();
        EncounterProbeAuthoritySnapshot actualAuthority = snapshot.Authority;
        if (reopened.HeadAddress == failure.ExpectedParent)
        {
            if (actualAuthority.Matches(failure.ParentAuthority))
            {
                return new EncounterProbeResolution(
                    EncounterProbeOutcome.NotCommitted,
                    snapshot);
            }

            throw new InvalidDataException(
                "Reopened encounter HEAD has the expected parent address but not its " +
                "captured authority state.");
        }

        if (reopened.HeadAddress != failure.CandidateAddress)
        {
            throw new InvalidDataException(
                $"Reopened encounter HEAD '{reopened.HeadAddress}' is neither the " +
                $"expected parent nor candidate '{failure.CandidateAddress}'.");
        }

        bool exactChild = reopened.HeadParentAddress == failure.ExpectedParent &&
            actualAuthority.Matches(failure.ChildAuthority);
        if (exactChild)
        {
            return new EncounterProbeResolution(
                EncounterProbeOutcome.Committed,
                snapshot);
        }

        throw new InvalidDataException(
            "Reopened encounter HEAD is not the exact proposed child authority state.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _repository.Dispose();
    }

    internal void RequireView(int epoch)
    {
        RequireActive();
        if (epoch != _viewEpoch)
        {
            throw new InvalidOperationException(
                "The committed encounter view expired when a transaction attempt began.");
        }
    }

    internal WorldVersion Version(int epoch)
    {
        RequireView(epoch);
        return _root.Version;
    }

    internal LogicalInstant? LastInstant(int epoch)
    {
        RequireView(epoch);
        return _root.LastInstant;
    }

    internal FirstBoardWorld CreateOracleWorld(int epoch)
    {
        RequireView(epoch);
        return _root.CreateOracleWorldForOpening();
    }

    internal IReadOnlyList<string> DriverActorIds(int epoch)
    {
        RequireView(epoch);
        return Array.AsReadOnly([.. _driverActorIds]);
    }

    internal ScenarioInstance Instance(int epoch)
    {
        RequireView(epoch);
        return _instance;
    }

    private CommitAddress CommitGraph(
        CommitAddress expectedParent,
        EncounterProbeAuthoritySnapshot parentAuthority,
        EncounterProbeAuthoritySnapshot childAuthority,
        EncounterProbeOperationKind operationKind)
    {
        AteliaResult<CommitAddress> commit = _repository.Commit(_root.GraphRoot);
        if (commit.IsFailure)
        {
            Poison();
            if (commit.Error is not RepositoryCommitError error)
            {
                throw new InvalidOperationException(
                    "StateJournal encounter commit failed before producing a structured " +
                    "candidate; reopen durable HEAD.",
                    new InvalidOperationException(commit.Error!.ToString()));
            }

            throw new EncounterProbeCommitFailedException(
                error,
                _branchName,
                expectedParent,
                parentAuthority,
                childAuthority,
                operationKind);
        }

        return commit.Value;
    }

    private void RequireProductionOpeningTransaction(
        EncounterTransactionProbeV1 transaction)
    {
        EncounterTransactionProbeV1 expected = EncounterProbePlanner.PlanAsync(
                new EncounterProbeView(this, _viewEpoch))
            .AsTask()
            .GetAwaiter()
            .GetResult();
        bool exactEnvelope = transaction.ParentVersion == expected.ParentVersion &&
            transaction.Batch.Instant == expected.Batch.Instant &&
            transaction.Batch.CauseKey == expected.Batch.CauseKey &&
            transaction.Batch.Facts.Count == expected.Batch.Facts.Count;
        bool exactOrder = exactEnvelope &&
            transaction.Batch.Facts.SequenceEqual(expected.Batch.Facts);
        bool exactReverseForGuardProof = exactEnvelope &&
            transaction.Batch.Facts.SequenceEqual(expected.Batch.Facts.Reverse());
        if (!exactOrder && !exactReverseForGuardProof)
        {
            throw new InvalidOperationException(
                "The encounter probe accepts only the production exact opening batch. " +
                "The exact reversed pair is admitted solely to exercise the real " +
                "Apply-time order guard.");
        }
    }

    private void AdvanceViewEpoch()
    {
        checked
        {
            _viewEpoch++;
        }
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned)
        {
            throw new InvalidOperationException(
                $"The StateJournal encounter probe Session for branch '{_branchName}' is " +
                "poisoned; reopen durable HEAD.");
        }
    }

    private void Poison()
    {
        if (_poisoned)
        {
            return;
        }

        _poisoned = true;
        _repository.Dispose();
    }

    private static void ValidateArguments(
        string repositoryPath,
        ScenarioInstance instance,
        IEnumerable<string> driverActorIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(driverActorIds);
    }

    private static string[] CanonicalDriverActorIds(IEnumerable<string> driverActorIds)
    {
        ArgumentNullException.ThrowIfNull(driverActorIds);
        string[] result =
        [
            .. driverActorIds
                .Select(actorId =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
                    return actorId;
                })
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
        if (result.Length == 0)
        {
            throw new ArgumentException(
                "The encounter fixture requires at least one participant driver.",
                nameof(driverActorIds));
        }

        return result;
    }

    private static T Require<T>(AteliaResult<T> result, string operation)
        where T : notnull
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Failed to {operation}: {result.Error}");
        }

        return result.Value!;
    }
}

internal sealed class EncounterProbeView
{
    private readonly EncounterProbeSession _session;
    private readonly int _epoch;

    internal EncounterProbeView(EncounterProbeSession session, int epoch)
    {
        _session = session;
        _epoch = epoch;
    }

    public WorldVersion Version => _session.Version(_epoch);

    public LogicalInstant? LastInstant => _session.LastInstant(_epoch);

    public IReadOnlyList<string> DriverActorIds => _session.DriverActorIds(_epoch);

    public ScenarioInstance Instance => _session.Instance(_epoch);

    public FirstBoardWorld CreateOracleWorld() => _session.CreateOracleWorld(_epoch);
}

internal static class EncounterProbePlanner
{
    public static async ValueTask<EncounterTransactionProbeV1> PlanAsync(
        EncounterProbeView world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Version.TransitionCount != 0 || world.LastInstant is not null)
        {
            throw new InvalidOperationException(
                "The narrow encounter probe plans only from its imported traveling baseline.");
        }

        FirstBoardWorld oracle = world.CreateOracleWorld();
        IReadOnlyDictionary<string, IPlayerDriver> drivers = world.DriverActorIds.ToDictionary(
            actorId => actorId,
            _ => (IPlayerDriver)new NoCallPlayerDriver(),
            StringComparer.Ordinal);
        var rule = new FirstBoardPassageEncounterRule(world.Instance.Graph, drivers);
        var simulationRules = new SimulationRules(
            oracle.WorldSeed,
            maxTransitionsPerModelTime: 100);
        OccurrenceCandidate<BoardCandidate> winner = rule.Forecast(oracle, simulationRules)
            .Single();
        TransitionDraft<FirstBoardFact> draft = await rule.PlanSelectedAsync(
            oracle,
            winner,
            CancellationToken.None);
        var instant = new LogicalInstant(winner.Due.ModelTime, causalOrdinal: 0);
        return new EncounterTransactionProbeV1(
            world.Version,
            new JournalBatch<FirstBoardFact>(instant, winner.Key, draft.Facts));
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

internal static class EncounterProbeFixture
{
    public const string FixtureId = "firstboard.alice-bob-road-contact/1";

    public static FirstBoardWorld CreateTravelingPrefix(ScenarioInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld world = instance.CreateInitialWorld();
        var instant = new LogicalInstant(ModelTime.Zero, causalOrdinal: 0);
        world = StartTraversal(instance, reducer, world, BoardIds.Alice, instant);
        world = StartTraversal(instance, reducer, world, BoardIds.Bob, instant);
        reducer.Validate(world);
        return world;
    }

    private static FirstBoardWorld StartTraversal(
        ScenarioInstance instance,
        FirstBoardReducer reducer,
        FirstBoardWorld world,
        string actorId,
        LogicalInstant instant)
    {
        SpatialPlanResult movement = new SpatialPlanner(instance.Graph).TryStartTraversal(
            world.Spatial,
            new EntityId(actorId),
            new PassageId(BoardIds.TavernMarketRoad),
            BoardTiming.TravelSpeed,
            instant.ModelTime);
        if (movement is not SpatialPlanAccepted accepted)
        {
            throw new InvalidOperationException(
                $"The deterministic encounter fixture could not start '{actorId}': " +
                ((SpatialPlanRejected)movement).Reason);
        }

        foreach (GraphSpatialFact fact in accepted.Facts)
        {
            world = reducer.Apply(world, instant, new SpatialBoardFact(fact));
        }

        return world;
    }
}

internal sealed class DurableEncounterProbeRootV1
{
    private const string SchemaId = "firstboard.statejournal-encounter-root/1";
    private const string BaselineSummary =
        "Imported deterministic Alice/Bob opposing traversal fixture.";
    private const string EncounterSummary =
        "Alice and Bob made head-on passage contact and opened an encounter.";
    private const string TraversingLocationKind = "traversing/1";
    private const string HeadOnKind = "head-on-meeting/1";
    private const string OvertakeKind = "overtake/1";

    private static class Fields
    {
        public const string Schema = "schemaId";
        public const string Definition = "definitionSha256";
        public const string Ruleset = "rulesetId";
        public const string WorldSeed = "worldSeed";
        public const string Fixture = "fixtureId";
        public const string DriverActors = "driverActorIds";
        public const string Lineage = "lineageId";
        public const string TransitionCount = "transitionCount";
        public const string ParentWorldVersion = "parentWorldVersion";
        public const string LastTransition = "lastTransition";
        public const string CommitKind = "commitKind";
        public const string CommitSummary = "commitSummary";
        public const string Game = "game";
        public const string Spatial = "spatial";
        public const string Now = "nowMs";
        public const string Actors = "actorIdsByKey";
        public const string PendingEncounter = "pendingEncounter";
        public const string Entities = "entitiesById";
        public const string ConsumedContacts = "consumedContacts";
        public const string ModelTime = "modelTimeMs";
        public const string CausalOrdinal = "causalOrdinal";
        public const string CauseKey = "causeKeyBase64";
        public const string PassageId = "passageId";
        public const string EntityA = "entityA";
        public const string GenerationA = "generationA";
        public const string EntityB = "entityB";
        public const string GenerationB = "generationB";
        public const string ContactKind = "contactKind";
    }

    private readonly ScenarioInstance _instance;
    private readonly string[] _expectedDriverActorIds;
    private readonly DurableDict<string> _graphRoot;
    private readonly DurableHashSet<string> _driverActorIds;
    private readonly DurableDict<string> _game;
    private readonly DurableDict<string, long> _actorIds;
    private readonly DurableDict<string> _spatial;
    private readonly DurableDict<string, DurableDict<string>> _entities;
    private readonly DurableHashSet<EncounterContactStorageKey> _consumedContacts;
    private DurableDict<string>? _parentWorldVersion;
    private DurableDict<string>? _lastTransition;
    private DurableDict<string>? _pendingEncounter;

    private DurableEncounterProbeRootV1(
        ScenarioInstance instance,
        IEnumerable<string> expectedDriverActorIds,
        DurableDict<string> graphRoot,
        DurableHashSet<string> driverActorIds,
        DurableDict<string> game,
        DurableDict<string, long> actorIds,
        DurableDict<string> spatial,
        DurableDict<string, DurableDict<string>> entities,
        DurableHashSet<EncounterContactStorageKey> consumedContacts,
        DurableDict<string>? parentWorldVersion,
        DurableDict<string>? lastTransition,
        DurableDict<string>? pendingEncounter)
    {
        _instance = instance;
        _expectedDriverActorIds =
        [
            .. expectedDriverActorIds.Order(StringComparer.Ordinal),
        ];
        _graphRoot = graphRoot;
        _driverActorIds = driverActorIds;
        _game = game;
        _actorIds = actorIds;
        _spatial = spatial;
        _entities = entities;
        _consumedContacts = consumedContacts;
        _parentWorldVersion = parentWorldVersion;
        _lastTransition = lastTransition;
        _pendingEncounter = pendingEncounter;
    }

    public DurableObject GraphRoot => _graphRoot;

    public CommitAddress HeadAddress =>
        _graphRoot.Revision.HeadAddress ??
        throw new InvalidOperationException("The encounter probe root has no committed HEAD.");

    public CommitAddress? HeadParentAddress => _graphRoot.Revision.HeadParentAddress;

    public WorldVersion Version => new(
        _graphRoot.GetOrThrow<long>(Fields.Lineage),
        _graphRoot.GetOrThrow<long>(Fields.TransitionCount));

    public WorldVersion? ParentWorldVersion => _parentWorldVersion is null
        ? null
        : ReadWorldVersion(_parentWorldVersion);

    public LogicalInstant? LastInstant => _lastTransition is null
        ? null
        : new LogicalInstant(
            new ModelTime(_lastTransition.GetOrThrow<long>(Fields.ModelTime)),
            _lastTransition.GetOrThrow<long>(Fields.CausalOrdinal));

    public CandidateKey? LastCause => _lastTransition is null
        ? null
        : CandidateKey.FromBytes(Convert.FromBase64String(
            _lastTransition.GetOrThrow<string>(Fields.CauseKey)!));

    public string CommitKind => _graphRoot.GetOrThrow<string>(Fields.CommitKind)!;

    public string? CommitSummary => ReadOptionalString(
        _graphRoot,
        Fields.CommitSummary);

    public ModelTime Now => new(_game.GetOrThrow<long>(Fields.Now));

    public static DurableEncounterProbeRootV1 Create(
        Revision revision,
        ScenarioInstance instance,
        long lineageId,
        IEnumerable<string> driverActorIds,
        FirstBoardWorld travelingFixture)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(driverActorIds);
        ArgumentNullException.ThrowIfNull(travelingFixture);

        string[] expectedDrivers =
        [
            .. driverActorIds.Order(StringComparer.Ordinal),
        ];
        DurableDict<string> root = revision.CreateDict<string>();
        DurableHashSet<string> drivers = revision.CreateHashSet<string>();
        DurableDict<string> game = revision.CreateDict<string>();
        DurableDict<string, long> actors = revision.CreateDict<string, long>();
        DurableDict<string> spatial = revision.CreateDict<string>();
        DurableDict<string, DurableDict<string>> entities =
            revision.CreateDict<string, DurableDict<string>>();
        DurableHashSet<EncounterContactStorageKey> contacts =
            revision.CreateHashSet<EncounterContactStorageKey>();

        root.Upsert(Fields.Schema, SchemaId);
        root.Upsert(Fields.Definition, instance.DefinitionSha256);
        root.Upsert(Fields.Ruleset, instance.Definition.RulesetId);
        root.Upsert(Fields.WorldSeed, instance.WorldSeed);
        root.Upsert(Fields.Fixture, EncounterProbeFixture.FixtureId);
        root.Upsert(Fields.DriverActors, drivers);
        root.Upsert(Fields.Lineage, lineageId);
        root.Upsert(Fields.TransitionCount, 0L);
        root.Upsert(Fields.CommitKind, EncounterProbeCommitKinds.FixtureBaseline);
        root.Upsert(Fields.CommitSummary, BaselineSummary);
        root.Upsert(Fields.Game, game);
        root.Upsert(Fields.Spatial, spatial);
        game.Upsert(Fields.Now, travelingFixture.Now.Ticks);
        game.Upsert(Fields.Actors, actors);
        spatial.Upsert(Fields.Entities, entities);
        spatial.Upsert(Fields.ConsumedContacts, contacts);

        foreach (string driverActorId in expectedDrivers)
        {
            _ = drivers.Add(driverActorId);
        }

        foreach (BoardActor actor in travelingFixture.Actors.OrderBy(actor => actor.Key))
        {
            actors.Upsert(actor.Key, actor.Id);
            if (!travelingFixture.Spatial.TryGetEntity(
                    new EntityId(actor.Key),
                    out SpatialEntity? entity))
            {
                throw new InvalidOperationException(
                    $"Encounter fixture actor '{actor.Key}' has no Spatial entity.");
            }

            TraversingLocation traversal = entity!.Location as TraversingLocation ??
                throw new InvalidOperationException(
                    $"Encounter fixture actor '{actor.Key}' is not traversing.");
            DurableDict<string> entityData = revision.CreateDict<string>();
            DurableEncounterEntity.Create(entityData, entity, traversal);
            entities.Upsert(actor.Key, entityData);
        }

        return new DurableEncounterProbeRootV1(
            instance,
            expectedDrivers,
            root,
            drivers,
            game,
            actors,
            spatial,
            entities,
            contacts,
            parentWorldVersion: null,
            lastTransition: null,
            pendingEncounter: null);
    }

    public static DurableEncounterProbeRootV1 Open(
        Revision revision,
        ScenarioInstance instance,
        IEnumerable<string> expectedDriverActorIds)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(expectedDriverActorIds);
        DurableDict<string> root = revision.GraphRoot as DurableDict<string> ??
            throw new InvalidDataException(
                "StateJournal branch HEAD does not contain an encounter probe root.");
        DurableDict<string> game = root.GetOrThrow<DurableDict<string>>(Fields.Game)!;
        DurableDict<string> spatial = root.GetOrThrow<DurableDict<string>>(Fields.Spatial)!;
        var result = new DurableEncounterProbeRootV1(
            instance,
            expectedDriverActorIds,
            root,
            root.GetOrThrow<DurableHashSet<string>>(Fields.DriverActors)!,
            game,
            game.GetOrThrow<DurableDict<string, long>>(Fields.Actors)!,
            spatial,
            spatial.GetOrThrow<DurableDict<string, DurableDict<string>>>(Fields.Entities)!,
            spatial.GetOrThrow<DurableHashSet<EncounterContactStorageKey>>(
                Fields.ConsumedContacts)!,
            GetOptionalDict(root, Fields.ParentWorldVersion),
            GetOptionalDict(root, Fields.LastTransition),
            GetOptionalDict(game, Fields.PendingEncounter));
        result.ValidateComplete();
        return result;
    }

    public void RequireExpectedParent(WorldVersion parent)
    {
        if (Version != parent)
        {
            throw new InvalidOperationException(
                $"Transaction parent '{parent}' does not match durable frontier '{Version}'.");
        }
    }

    public void ApplyChildLineageBoundary(long childLineageId)
    {
        WorldVersion sourceVersion = Version;
        if (childLineageId == sourceVersion.LineageId)
        {
            throw new ArgumentException(
                "A child encounter lineage must have a fresh identity.",
                nameof(childLineageId));
        }

        DurableDict<string> parent = _graphRoot.Revision.CreateDict<string>();
        WriteWorldVersion(parent, sourceVersion);
        _graphRoot.Upsert(Fields.ParentWorldVersion, parent);
        _parentWorldVersion = parent;
        _graphRoot.Upsert(Fields.Lineage, childLineageId);
        _graphRoot.Upsert(Fields.CommitKind, EncounterProbeCommitKinds.LineageStart);
        _graphRoot.Upsert(
            Fields.CommitSummary,
            $"Started encounter lineage {childLineageId} from {sourceVersion}.");
    }

    public void Apply(LogicalInstant instant, FirstBoardFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        switch (fact)
        {
            case SpatialBoardFact { Value: PassageContactOccurredFact contact }:
                ApplyContact(instant, contact);
                break;

            case GameBoardFact { Value: PassageEncounterOpenedEvent opened }:
                ApplyEncounterOpened(opened);
                break;

            default:
                throw new NotSupportedException(
                    $"Encounter probe fact '{fact.GetType().Name}' is not supported.");
        }

        _game.Upsert(Fields.Now, instant.ModelTime.Ticks);
    }

    public void ValidateBatchEnd(bool injectFailure)
    {
        ValidateBindings();
        _ = BuildFixtureSpatialState();
        if (injectFailure)
        {
            throw new InvalidOperationException(
                "Injected encounter batch-end validation failure after both facts mutated " +
                "the private graph.");
        }
    }

    public void AdvanceFrontier(EncounterTransactionProbeV1 transaction)
    {
        RequireExpectedParent(transaction.ParentVersion);
        if (LastInstant is LogicalInstant previous && transaction.Batch.Instant <= previous)
        {
            throw new InvalidOperationException(
                "Encounter transaction instants must advance strictly.");
        }

        _graphRoot.Upsert(
            Fields.TransitionCount,
            checked(Version.TransitionCount + 1));
        DurableDict<string> lastTransition =
            _lastTransition ?? _graphRoot.Revision.CreateDict<string>();
        lastTransition.Upsert(
            Fields.ModelTime,
            transaction.Batch.Instant.ModelTime.Ticks);
        lastTransition.Upsert(
            Fields.CausalOrdinal,
            transaction.Batch.Instant.CausalOrdinal);
        lastTransition.Upsert(
            Fields.CauseKey,
            Convert.ToBase64String(transaction.Batch.CauseKey.ToByteArray()));
        if (_lastTransition is null)
        {
            _graphRoot.Upsert(Fields.LastTransition, lastTransition);
            _lastTransition = lastTransition;
        }

        _graphRoot.Upsert(
            Fields.CommitKind,
            EncounterProbeCommitKinds.ObjectiveTransition);
        _graphRoot.Upsert(Fields.CommitSummary, EncounterSummary);
    }

    public void ValidateComplete()
    {
        ValidateBindings();
        WorldVersion version = Version;
        WorldVersion? parent = ParentWorldVersion;
        LogicalInstant? lastInstant = LastInstant;
        CandidateKey? lastCause = LastCause;

        if (parent is WorldVersion parentVersion &&
            (parentVersion.LineageId == version.LineageId ||
             parentVersion.TransitionCount > version.TransitionCount))
        {
            throw new InvalidDataException(
                "Encounter child-lineage provenance must name a different lineage at an " +
                "equal or earlier frontier.");
        }

        if ((lastInstant is null) != (lastCause is null))
        {
            throw new InvalidDataException(
                "The encounter frontier must persist last LogicalInstant and cause together.");
        }

        if (version.TransitionCount == 0 &&
            (lastInstant is not null || Now != ModelTime.Zero))
        {
            throw new InvalidDataException(
                "The encounter fixture baseline cannot carry a last transition.");
        }

        if (version.TransitionCount > 0 &&
            (lastInstant is null || Now != lastInstant.Value.ModelTime))
        {
            throw new InvalidDataException(
                "The encounter frontier's Now and last LogicalInstant are inconsistent.");
        }

        string commitKind = CommitKind;
        if (commitKind is not EncounterProbeCommitKinds.FixtureBaseline and
            not EncounterProbeCommitKinds.LineageStart and
            not EncounterProbeCommitKinds.ObjectiveTransition)
        {
            throw new InvalidDataException(
                $"Encounter probe commit kind '{commitKind}' is not supported.");
        }

        if (commitKind == EncounterProbeCommitKinds.FixtureBaseline && parent is not null)
        {
            throw new InvalidDataException(
                "An encounter fixture baseline cannot carry child-lineage provenance.");
        }

        if (commitKind == EncounterProbeCommitKinds.FixtureBaseline &&
            version.TransitionCount != 0)
        {
            throw new InvalidDataException(
                "An encounter fixture baseline must remain at transition count zero.");
        }

        if (commitKind == EncounterProbeCommitKinds.LineageStart &&
            (parent is null ||
             parent.Value.TransitionCount != version.TransitionCount))
        {
            throw new InvalidDataException(
                "An encounter lineage-start commit requires ParentWorldVersion at the " +
                "same Objective transition count.");
        }

        if (commitKind == EncounterProbeCommitKinds.ObjectiveTransition &&
            version.TransitionCount == 0)
        {
            throw new InvalidDataException(
                "An encounter Objective transition must advance the frontier.");
        }

        GraphSpatialState fixtureSpatial = BuildFixtureSpatialState();
        ValidateContactsAgainstCurrentSegments(fixtureSpatial);
        ValidatePendingEncounter();
    }

    public FirstBoardWorld CreateOracleWorldForOpening()
    {
        ValidateComplete();
        if (_consumedContacts.Count != 0 || _pendingEncounter is not null)
        {
            throw new InvalidOperationException(
                "The narrow encounter planner requires the unconsumed traveling baseline.");
        }

        BoardActor[] actors =
        [
            .. CopyActors().Select(actor => new BoardActor(
                actor.PersistentId,
                actor.Key,
                Generation: 0,
                DecisionSequence: 0,
                Activity: null,
                TravelGoalPlaceId: null,
                KnownFacts: [])),
        ];
        long nextPersistentId = checked(actors.Max(actor => actor.Id) + 1);
        var game = new FirstBoardGameState(
            _instance.WorldSeed,
            nextPersistentId,
            Now,
            Array.AsReadOnly(actors),
            Objects: [],
            CellarSealed: false,
            ChestOpened: false,
            PendingEncounter: null);
        var result = new FirstBoardWorld(game, BuildFixtureSpatialState());
        new FirstBoardReducer(_instance.Graph).Validate(result);
        return result;
    }

    public EncounterProbeAuthoritySnapshot AuthoritySnapshot() => new(
        SchemaId,
        _instance.DefinitionSha256,
        _instance.Definition.RulesetId,
        _instance.WorldSeed,
        EncounterProbeFixture.FixtureId,
        CopyDriverActorIds(),
        Version,
        ParentWorldVersion,
        LastInstant,
        LastCause,
        CommitKind,
        Now,
        CopyActors(),
        CopyTraversals(),
        CopyContacts(),
        ReadPendingSnapshot());

    public EncounterProbeSemanticSnapshot Snapshot() => new(
        AuthoritySnapshot(),
        CommitSummary);

    private void ApplyContact(
        LogicalInstant instant,
        PassageContactOccurredFact contact)
    {
        ArgumentNullException.ThrowIfNull(contact.ContactKey);
        EncounterContactStorageKey storageKey = ToStorageKey(contact.ContactKey);
        if (_consumedContacts.Contains(storageKey))
        {
            throw new InvalidOperationException("The passage contact has already been consumed.");
        }

        FirstBoardWorld oracle = CreateOracleWorldForOpening();
        _ = new GraphSpatialReducer(_instance.Graph).Apply(
            oracle.Spatial,
            instant,
            contact);
        if (!_consumedContacts.Add(storageKey))
        {
            throw new InvalidOperationException(
                "The passage contact could not be inserted into the durable set.");
        }
    }

    private void ApplyEncounterOpened(PassageEncounterOpenedEvent opened)
    {
        ArgumentNullException.ThrowIfNull(opened.ContactKey);
        if (_pendingEncounter is not null)
        {
            throw new InvalidOperationException(
                "FirstBoard supports only one pending passage encounter.");
        }

        if (!Enum.IsDefined(opened.Kind))
        {
            throw new InvalidOperationException(
                $"Unknown passage contact kind '{opened.Kind}'.");
        }

        EncounterContactStorageKey storageKey = ToStorageKey(opened.ContactKey);
        if (!_consumedContacts.Contains(storageKey))
        {
            throw new InvalidOperationException(
                "A passage encounter can open only after its Spatial contact was consumed.");
        }

        if (!_actorIds.ContainsKey(opened.ContactKey.EntityA.Value) ||
            !_actorIds.ContainsKey(opened.ContactKey.EntityB.Value))
        {
            throw new InvalidOperationException(
                "A passage encounter requires two FirstBoard actors.");
        }

        DurableDict<string> pending = _graphRoot.Revision.CreateDict<string>();
        WriteContact(pending, opened.ContactKey);
        pending.Upsert(Fields.ContactKind, WriteContactKind(opened.Kind));
        _game.Upsert(Fields.PendingEncounter, pending);
        _pendingEncounter = pending;
    }

    private GraphSpatialState BuildFixtureSpatialState()
    {
        EncounterProbeActorIdentity[] actors = CopyActors();
        EntityPlacement[] placements =
        [
            .. actors.Select(actor => new EntityPlacement(
                new EntityId(actor.Key),
                new PlaceId(_instance.Definition.Actor(actor.Key).InitialPlaceId))),
        ];
        GraphSpatialState spatial = GraphSpatialState.Create(_instance.Graph, placements);
        var reducer = new GraphSpatialReducer(_instance.Graph);
        foreach (EncounterProbeTraversalSnapshot expected in CopyTraversals())
        {
            PassageDefinition passage = _instance.Graph.GetPassage(
                new PassageId(expected.PassageId));
            PlaceId target = new(expected.TargetPlaceId);
            PlaceId origin = target == passage.EndpointA
                ? passage.EndpointB
                : target == passage.EndpointB
                    ? passage.EndpointA
                    : throw new InvalidDataException(
                        $"Traversal target '{target}' is not an endpoint of '{passage.Id}'.");
            spatial = reducer.Apply(
                spatial,
                new LogicalInstant(expected.AnchorTime, causalOrdinal: 0),
                new TraversalStartedFact(
                    new EntityId(expected.EntityId),
                    passage.Id,
                    origin,
                    expected.SpeedSnapshot));
            if (!spatial.TryGetEntity(
                    new EntityId(expected.EntityId),
                    out SpatialEntity? actualEntity))
            {
                throw new InvalidDataException(
                    $"Fixture traversal entity '{expected.EntityId}' was not reconstructed.");
            }

            TraversingLocation actual = actualEntity!.Location as TraversingLocation ??
                throw new InvalidDataException(
                    $"Fixture entity '{expected.EntityId}' did not reconstruct as traversing.");
            EncounterProbeTraversalSnapshot actualSnapshot = ToTraversalSnapshot(
                actualEntity,
                actual);
            if (actualSnapshot != expected)
            {
                throw new InvalidDataException(
                    $"Durable traversal for '{expected.EntityId}' does not match the " +
                    "deterministic fixture segment.");
            }
        }

        return spatial;
    }

    private void ValidateContactsAgainstCurrentSegments(GraphSpatialState fixtureSpatial)
    {
        ArgumentNullException.ThrowIfNull(fixtureSpatial);
        foreach (EncounterContactStorageKey stored in _consumedContacts.Items)
        {
            PassageContactKey contact = FromStorageKey(stored);
            if (!fixtureSpatial.TryGetEntity(contact.EntityA, out SpatialEntity? entityA) ||
                entityA!.MovementGeneration != contact.MovementGenerationA ||
                entityA.Location is not TraversingLocation traversalA ||
                traversalA.PassageId != contact.PassageId ||
                !fixtureSpatial.TryGetEntity(contact.EntityB, out SpatialEntity? entityB) ||
                entityB!.MovementGeneration != contact.MovementGenerationB ||
                entityB.Location is not TraversingLocation traversalB ||
                traversalB.PassageId != contact.PassageId)
            {
                throw new InvalidDataException(
                    "A durable consumed contact references a non-current traversal segment.");
            }
        }
    }

    private void ValidatePendingEncounter()
    {
        EncounterProbePendingSnapshot? pending = ReadPendingSnapshot();
        if (pending is null)
        {
            return;
        }

        if (!_actorIds.ContainsKey(pending.Contact.EntityA) ||
            !_actorIds.ContainsKey(pending.Contact.EntityB))
        {
            throw new InvalidDataException(
                "A durable pending passage encounter must reference two FirstBoard actors.");
        }

        // Deliberately do not require membership in _consumedContacts. Production permits
        // stale pending encounters after a traversal changes and clears segment contacts.
    }

    private void ValidateBindings()
    {
        var expectedRootKeys = new List<string>
        {
            Fields.Schema,
            Fields.Definition,
            Fields.Ruleset,
            Fields.WorldSeed,
            Fields.Fixture,
            Fields.DriverActors,
            Fields.Lineage,
            Fields.TransitionCount,
            Fields.CommitKind,
            Fields.Game,
            Fields.Spatial,
        };
        if (_parentWorldVersion is not null)
        {
            expectedRootKeys.Add(Fields.ParentWorldVersion);
        }

        if (_lastTransition is not null)
        {
            expectedRootKeys.Add(Fields.LastTransition);
        }

        if (CommitSummary is not null)
        {
            expectedRootKeys.Add(Fields.CommitSummary);
        }

        RequireExactKeys(_graphRoot.Keys, [.. expectedRootKeys]);
        RequireExactKeys(
            _game.Keys,
            _pendingEncounter is null
                ? [Fields.Now, Fields.Actors]
                : [Fields.Now, Fields.Actors, Fields.PendingEncounter]);
        RequireExactKeys(
            _spatial.Keys,
            Fields.Entities,
            Fields.ConsumedContacts);

        if (!string.Equals(
                _graphRoot.GetOrThrow<string>(Fields.Schema),
                SchemaId,
                StringComparison.Ordinal) ||
            !string.Equals(
                _graphRoot.GetOrThrow<string>(Fields.Definition),
                _instance.DefinitionSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                _graphRoot.GetOrThrow<string>(Fields.Ruleset),
                _instance.Definition.RulesetId,
                StringComparison.Ordinal) ||
            _graphRoot.GetOrThrow<ulong>(Fields.WorldSeed) != _instance.WorldSeed ||
            !string.Equals(
                _graphRoot.GetOrThrow<string>(Fields.Fixture),
                EncounterProbeFixture.FixtureId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Encounter probe root binding does not match the supplied fixture.");
        }

        string[] actualDrivers = CopyDriverActorIds();
        if (!actualDrivers.SequenceEqual(_expectedDriverActorIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Encounter driver composition does not match the persisted binding.");
        }

        string[] expectedActors =
        [
            .. _instance.Definition.Actors
                .Select(actor => actor.Id)
                .Order(StringComparer.Ordinal),
        ];
        string[] actualActors = [.. _actorIds.Keys.Order(StringComparer.Ordinal)];
        string[] actualEntities = [.. _entities.Keys.Order(StringComparer.Ordinal)];
        if (!actualActors.SequenceEqual(expectedActors, StringComparer.Ordinal) ||
            !actualEntities.SequenceEqual(expectedActors, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Encounter actor/entity identities do not match Definition actors.");
        }

        if (_expectedDriverActorIds.Any(actorId => !_actorIds.ContainsKey(actorId)))
        {
            throw new InvalidDataException(
                "Encounter driver composition references a non-actor entity.");
        }

        long[] persistentIds = [.. _actorIds.Keys.Select(key => _actorIds.GetOrThrow(key))];
        if (persistentIds.Any(id => id <= 0) || persistentIds.Distinct().Count() != persistentIds.Length)
        {
            throw new InvalidDataException(
                "Encounter actor persistent IDs must be positive and unique.");
        }

        foreach (string entityId in _entities.Keys)
        {
            _ = DurableEncounterEntity.Open(
                entityId,
                _entities.GetOrThrow(entityId)!);
        }

        if (_parentWorldVersion is not null)
        {
            RequireExactKeys(
                _parentWorldVersion.Keys,
                Fields.Lineage,
                Fields.TransitionCount);
        }

        if (_lastTransition is not null)
        {
            RequireExactKeys(
                _lastTransition.Keys,
                Fields.ModelTime,
                Fields.CausalOrdinal,
                Fields.CauseKey);
        }

        if (_pendingEncounter is not null)
        {
            RequireExactKeys(
                _pendingEncounter.Keys,
                Fields.PassageId,
                Fields.EntityA,
                Fields.GenerationA,
                Fields.EntityB,
                Fields.GenerationB,
                Fields.ContactKind);
        }
    }

    private string[] CopyDriverActorIds() =>
    [
        .. _driverActorIds.Items.Order(StringComparer.Ordinal),
    ];

    private EncounterProbeActorIdentity[] CopyActors() =>
    [
        .. _actorIds.Keys
            .Order(StringComparer.Ordinal)
            .Select(key => new EncounterProbeActorIdentity(
                key,
                _actorIds.GetOrThrow(key))),
    ];

    private EncounterProbeTraversalSnapshot[] CopyTraversals() =>
    [
        .. _entities.Keys
            .Order(StringComparer.Ordinal)
            .Select(entityId => DurableEncounterEntity.Open(
                entityId,
                _entities.GetOrThrow(entityId)!).Snapshot()),
    ];

    private EncounterProbeContactSnapshot[] CopyContacts() =>
    [
        .. _consumedContacts.Items
            .OrderBy(value => value.PassageId, StringComparer.Ordinal)
            .ThenBy(value => value.EntityA, StringComparer.Ordinal)
            .ThenBy(value => value.GenerationA)
            .ThenBy(value => value.EntityB, StringComparer.Ordinal)
            .ThenBy(value => value.GenerationB)
            .Select(ToContactSnapshot),
    ];

    private EncounterProbePendingSnapshot? ReadPendingSnapshot()
    {
        if (_pendingEncounter is null)
        {
            return null;
        }

        PassageContactKey contact = ReadContact(_pendingEncounter);
        PassageContactKind kind = ReadContactKind(
            _pendingEncounter.GetOrThrow<string>(Fields.ContactKind)!);
        return new EncounterProbePendingSnapshot(
            ToContactSnapshot(ToStorageKey(contact)),
            kind);
    }

    private static EncounterContactStorageKey ToStorageKey(PassageContactKey contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return (
            contact.PassageId.Value,
            contact.EntityA.Value,
            contact.MovementGenerationA,
            contact.EntityB.Value,
            contact.MovementGenerationB);
    }

    private static PassageContactKey FromStorageKey(EncounterContactStorageKey value)
    {
        var result = new PassageContactKey(
            new PassageId(value.PassageId),
            new EntityId(value.EntityA),
            value.GenerationA,
            new EntityId(value.EntityB),
            value.GenerationB);
        if (ToStorageKey(result) != value)
        {
            throw new InvalidDataException(
                "Durable passage contact is not in canonical entity order.");
        }

        return result;
    }

    private static EncounterProbeContactSnapshot ToContactSnapshot(
        EncounterContactStorageKey value) =>
        new(
            value.PassageId,
            value.EntityA,
            value.GenerationA,
            value.EntityB,
            value.GenerationB);

    private static EncounterProbeTraversalSnapshot ToTraversalSnapshot(
        SpatialEntity entity,
        TraversingLocation traversal) =>
        new(
            entity.Id.Value,
            entity.MovementGeneration,
            traversal.PassageId.Value,
            traversal.AnchorOffset,
            traversal.AnchorTime,
            traversal.TargetPlaceId.Value,
            traversal.SpeedSnapshot,
            traversal.ArrivalDue);

    private static void WriteContact(
        DurableDict<string> destination,
        PassageContactKey contact)
    {
        EncounterContactStorageKey value = ToStorageKey(contact);
        destination.Upsert(Fields.PassageId, value.PassageId);
        destination.Upsert(Fields.EntityA, value.EntityA);
        destination.Upsert(Fields.GenerationA, value.GenerationA);
        destination.Upsert(Fields.EntityB, value.EntityB);
        destination.Upsert(Fields.GenerationB, value.GenerationB);
    }

    private static PassageContactKey ReadContact(DurableDict<string> source) =>
        FromStorageKey((
            source.GetOrThrow<string>(Fields.PassageId)!,
            source.GetOrThrow<string>(Fields.EntityA)!,
            source.GetOrThrow<long>(Fields.GenerationA),
            source.GetOrThrow<string>(Fields.EntityB)!,
            source.GetOrThrow<long>(Fields.GenerationB)));

    private static string WriteContactKind(PassageContactKind kind) => kind switch
    {
        PassageContactKind.HeadOnMeeting => HeadOnKind,
        PassageContactKind.Overtake => OvertakeKind,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown contact kind."),
    };

    private static PassageContactKind ReadContactKind(string value) => value switch
    {
        HeadOnKind => PassageContactKind.HeadOnMeeting,
        OvertakeKind => PassageContactKind.Overtake,
        _ => throw new InvalidDataException(
            $"Durable pending encounter has unknown kind tag '{value}'."),
    };

    private static DurableDict<string>? GetOptionalDict(
        DurableDict<string> source,
        string key)
    {
        GetIssue issue = source.Get<DurableDict<string>>(
            key,
            out DurableDict<string>? value);
        return issue switch
        {
            GetIssue.None => value ?? throw new InvalidDataException(
                $"Optional durable object '{key}' resolved to null."),
            GetIssue.NotFound => null,
            _ => throw new InvalidDataException(
                $"Optional durable object '{key}' has the wrong stored type."),
        };
    }

    private static string? ReadOptionalString(
        DurableDict<string> source,
        string key)
    {
        GetIssue issue = source.Get<string>(key, out string? value);
        return issue switch
        {
            GetIssue.None when !string.IsNullOrWhiteSpace(value) => value,
            GetIssue.None => null,
            GetIssue.NotFound => null,
            _ => null,
        };
    }

    private static WorldVersion ReadWorldVersion(DurableDict<string> source) => new(
        source.GetOrThrow<long>(Fields.Lineage),
        source.GetOrThrow<long>(Fields.TransitionCount));

    private static void WriteWorldVersion(
        DurableDict<string> destination,
        WorldVersion version)
    {
        destination.Upsert(Fields.Lineage, version.LineageId);
        destination.Upsert(Fields.TransitionCount, version.TransitionCount);
    }

    private static void RequireExactKeys(
        IEnumerable<string> actual,
        params string[] expected)
    {
        string[] actualArray = [.. actual.Order(StringComparer.Ordinal)];
        string[] expectedArray = [.. expected.Order(StringComparer.Ordinal)];
        if (!actualArray.SequenceEqual(expectedArray, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Durable encounter schema keys mismatch. Expected " +
                $"[{string.Join(", ", expectedArray)}], actual " +
                $"[{string.Join(", ", actualArray)}].");
        }
    }

    private sealed class DurableEncounterEntity
    {
        private static class EntityFields
        {
            public const string MovementGeneration = "movementGeneration";
            public const string LocationKind = "locationKind";
            public const string PassageId = "passageId";
            public const string AnchorOffset = "anchorOffset";
            public const string AnchorTime = "anchorTimeMs";
            public const string TargetPlaceId = "targetPlaceId";
            public const string SpeedSnapshot = "speedSnapshot";
            public const string ArrivalDue = "arrivalDueMs";
        }

        private readonly string _entityId;
        private readonly DurableDict<string> _data;

        private DurableEncounterEntity(string entityId, DurableDict<string> data)
        {
            _entityId = entityId;
            _data = data;
        }

        public static void Create(
            DurableDict<string> data,
            SpatialEntity entity,
            TraversingLocation traversal)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(entity);
            ArgumentNullException.ThrowIfNull(traversal);
            data.Upsert(EntityFields.MovementGeneration, entity.MovementGeneration);
            data.Upsert(EntityFields.LocationKind, TraversingLocationKind);
            data.Upsert(EntityFields.PassageId, traversal.PassageId.Value);
            data.Upsert(EntityFields.AnchorOffset, traversal.AnchorOffset);
            data.Upsert(EntityFields.AnchorTime, traversal.AnchorTime.Ticks);
            data.Upsert(EntityFields.TargetPlaceId, traversal.TargetPlaceId.Value);
            data.Upsert(EntityFields.SpeedSnapshot, traversal.SpeedSnapshot);
            data.Upsert(EntityFields.ArrivalDue, traversal.ArrivalDue.Ticks);
        }

        public static DurableEncounterEntity Open(
            string entityId,
            DurableDict<string> data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
            ArgumentNullException.ThrowIfNull(data);
            RequireExactKeys(
                data.Keys,
                EntityFields.MovementGeneration,
                EntityFields.LocationKind,
                EntityFields.PassageId,
                EntityFields.AnchorOffset,
                EntityFields.AnchorTime,
                EntityFields.TargetPlaceId,
                EntityFields.SpeedSnapshot,
                EntityFields.ArrivalDue);
            if (!string.Equals(
                    data.GetOrThrow<string>(EntityFields.LocationKind),
                    TraversingLocationKind,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Encounter entity '{entityId}' is not a supported traversing wrapper.");
            }

            return new DurableEncounterEntity(entityId, data);
        }

        public EncounterProbeTraversalSnapshot Snapshot() => new(
            _entityId,
            _data.GetOrThrow<long>(EntityFields.MovementGeneration),
            _data.GetOrThrow<string>(EntityFields.PassageId)!,
            _data.GetOrThrow<long>(EntityFields.AnchorOffset),
            new ModelTime(_data.GetOrThrow<long>(EntityFields.AnchorTime)),
            _data.GetOrThrow<string>(EntityFields.TargetPlaceId)!,
            _data.GetOrThrow<long>(EntityFields.SpeedSnapshot),
            new ModelTime(_data.GetOrThrow<long>(EntityFields.ArrivalDue)));
    }
}

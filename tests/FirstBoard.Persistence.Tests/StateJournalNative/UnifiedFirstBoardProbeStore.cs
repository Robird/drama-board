using Atelia;
using Atelia.StateJournal;
using DramaBoard.Decision.Validation;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

using UnifiedContactKey =
    (string PassageId, string EntityA, long GenerationA, string EntityB, long GenerationB);
using UnifiedScheduleKey = (string PassageId, long DueTicks);

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

internal enum UnifiedProbeFault
{
    None,
    AfterCognition,
    AfterGame,
    AfterSpatial,
    AtCompleteValidation,
}

internal enum UnifiedProbeOutcome
{
    NotCommitted,
    Committed,
}

internal sealed record UnifiedTestCommitError(string Token) : AteliaError(
    "UnifiedProbe.InjectedCommitFailure",
    $"Injected generic lineage commit failure '{Token}'.",
    Details: new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(Token)] = Token,
    });

internal enum UnifiedOperationKind
{
    Opening,
    Response,
    LineageStart,
}

internal sealed record UnifiedPreparedPlayerEffect(
    string ActorId,
    DecisionId DecisionId,
    long ExpectedDecisionSequence,
    long NextDecisionSequence,
    string PlayerProfileId,
    IReadOnlyList<PlayerClosureMemoryValue> MemoryReplacements,
    IReadOnlyList<PlayerClosureFactValue> PreviousKnownFacts);

internal sealed record UnifiedPreparedTransaction(
    UnifiedOperationKind OperationKind,
    WorldVersion ParentVersion,
    JournalBatch<FirstBoardFact> Batch,
    DecisionRequest? BasisRequest,
    PlayerDecision? Decision,
    UnifiedPreparedPlayerEffect? PlayerEffect,
    UnifiedObjectiveAuthoritySnapshot ExpectedPostObjective,
    int DriverCallCount);

internal readonly record struct UnifiedActorAuthority(
    long Id,
    string Key,
    long Generation,
    long DecisionSequence,
    long? WaitDueTicks,
    string? TravelGoalPlaceId,
    IReadOnlyList<PlayerClosureFactValue> KnownFacts)
{
    public bool Matches(UnifiedActorAuthority other) =>
        Id == other.Id &&
        string.Equals(Key, other.Key, StringComparison.Ordinal) &&
        Generation == other.Generation &&
        DecisionSequence == other.DecisionSequence &&
        WaitDueTicks == other.WaitDueTicks &&
        string.Equals(TravelGoalPlaceId, other.TravelGoalPlaceId, StringComparison.Ordinal) &&
        KnownFacts.SequenceEqual(other.KnownFacts);
}

internal readonly record struct UnifiedObjectAuthority(
    long Id,
    string Key,
    long? OwnerActorId);

internal readonly record struct UnifiedLocationAuthority(
    string Kind,
    string? PlaceId,
    string? PassageId,
    long? AnchorOffset,
    long? AnchorTimeTicks,
    string? TargetPlaceId,
    long? SpeedSnapshot,
    long? ArrivalDueTicks);

internal readonly record struct UnifiedEntityAuthority(
    string Id,
    long MovementGeneration,
    UnifiedLocationAuthority Location);

internal readonly record struct UnifiedOverrideAuthority(
    string PassageId,
    bool EnterableFromA,
    bool EnterableFromB);

internal readonly record struct UnifiedScheduleAuthority(
    string PassageId,
    long DueTicks,
    bool? EnterableFromA,
    bool? EnterableFromB);

internal readonly record struct UnifiedContactAuthority(
    string PassageId,
    string EntityA,
    long GenerationA,
    string EntityB,
    long GenerationB);

internal sealed record UnifiedPendingAuthority(
    UnifiedContactAuthority Contact,
    PassageContactKind Kind);

internal sealed record UnifiedObjectiveAuthoritySnapshot(
    ulong WorldSeed,
    long NextPersistentId,
    ModelTime Now,
    bool CellarSealed,
    bool ChestOpened,
    IReadOnlyList<UnifiedActorAuthority> Actors,
    IReadOnlyList<UnifiedObjectAuthority> Objects,
    IReadOnlyList<UnifiedEntityAuthority> Entities,
    IReadOnlyList<UnifiedOverrideAuthority> EntryOverrides,
    IReadOnlyList<UnifiedScheduleAuthority> Schedules,
    IReadOnlyList<UnifiedContactAuthority> ConsumedContacts,
    UnifiedPendingAuthority? PendingEncounter)
{
    public bool Matches(UnifiedObjectiveAuthoritySnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return WorldSeed == other.WorldSeed &&
            NextPersistentId == other.NextPersistentId &&
            Now == other.Now &&
            CellarSealed == other.CellarSealed &&
            ChestOpened == other.ChestOpened &&
            SequenceMatches(Actors, other.Actors, static (left, right) => left.Matches(right)) &&
            Objects.SequenceEqual(other.Objects) &&
            Entities.SequenceEqual(other.Entities) &&
            EntryOverrides.SequenceEqual(other.EntryOverrides) &&
            Schedules.SequenceEqual(other.Schedules) &&
            ConsumedContacts.SequenceEqual(other.ConsumedContacts) &&
            PendingEncounter == other.PendingEncounter;
    }

    private static bool SequenceMatches<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, T, bool> matches)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!matches(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record UnifiedPlayerSlotAuthority(
    string ActorId,
    string ProfileId,
    IReadOnlyList<PlayerClosureMemoryValue> Memory,
    IReadOnlyList<PlayerClosureFactValue> PreviousKnownFacts)
{
    public bool Matches(UnifiedPlayerSlotAuthority other) =>
        string.Equals(ActorId, other.ActorId, StringComparison.Ordinal) &&
        string.Equals(ProfileId, other.ProfileId, StringComparison.Ordinal) &&
        Memory.SequenceEqual(other.Memory) &&
        PreviousKnownFacts.SequenceEqual(other.PreviousKnownFacts);
}

internal sealed record UnifiedFullAuthoritySnapshot(
    string SchemaId,
    string DefinitionSha256,
    string RulesetId,
    ulong WorldSeed,
    string PlayerCompositionId,
    WorldVersion Version,
    WorldVersion? ParentWorldVersion,
    LogicalInstant? LastInstant,
    CandidateKey? LastCause,
    UnifiedObjectiveAuthoritySnapshot Objective,
    IReadOnlyList<UnifiedPlayerSlotAuthority> PlayerSlots)
{
    public bool Matches(UnifiedFullAuthoritySnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal) &&
            string.Equals(DefinitionSha256, other.DefinitionSha256, StringComparison.Ordinal) &&
            string.Equals(RulesetId, other.RulesetId, StringComparison.Ordinal) &&
            WorldSeed == other.WorldSeed &&
            string.Equals(PlayerCompositionId, other.PlayerCompositionId, StringComparison.Ordinal) &&
            Version == other.Version &&
            ParentWorldVersion == other.ParentWorldVersion &&
            LastInstant == other.LastInstant &&
            LastCause == other.LastCause &&
            Objective.Matches(other.Objective) &&
            PlayerSlots.Count == other.PlayerSlots.Count &&
            PlayerSlots.Zip(other.PlayerSlots).All(pair => pair.First.Matches(pair.Second));
    }
}

internal sealed record UnifiedSemanticSnapshot(
    UnifiedFullAuthoritySnapshot Authority,
    string? CommitSummary);

internal sealed record UnifiedClosedObjectiveBaseline(
    FirstBoardWorld World,
    WorldVersion Version,
    LogicalInstant? LastInstant);

internal sealed record UnifiedCommitReceipt(
    CommitAddress Parent,
    CommitAddress Head,
    WorldVersion Version,
    JournalBatch<FirstBoardFact> Batch);

internal sealed record UnifiedResolution(
    UnifiedProbeOutcome Outcome,
    UnifiedSemanticSnapshot Snapshot);

internal sealed class UnifiedCommitFailedException : InvalidOperationException
{
    public UnifiedCommitFailedException(
        RepositoryCommitError error,
        UnifiedOperationKind operationKind,
        string branchName,
        CommitAddress expectedParent,
        UnifiedFullAuthoritySnapshot parentAuthority,
        UnifiedFullAuthoritySnapshot childAuthority)
        : base($"StateJournal unified commit outcome is unknown: {error}")
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!string.Equals(error.BranchName, branchName, StringComparison.Ordinal) ||
            error.ExpectedHeadAddress != expectedParent)
        {
            throw new InvalidDataException(
                "Structured StateJournal failure does not match the captured branch/parent.");
        }

        OperationKind = operationKind;
        BranchName = branchName;
        ExpectedParent = expectedParent;
        ParentAuthority = parentAuthority;
        ChildAuthority = childAuthority;
        Error = error;
        UnderlyingError = error;
        CandidateAddress = error.CandidateAddress;
        FailurePhase = error.FailurePhase;
        PublicationState = error.PublicationState;
        RequiresRepositoryReopen = error.RequiresRepositoryReopen;
        CanRetryTransparently = error.CanRetryTransparently;
        MayHavePublished = error.MayHavePublished;
    }

    private UnifiedCommitFailedException(
        string message,
        Exception? failure,
        AteliaError? underlyingError,
        UnifiedOperationKind operationKind,
        string branchName,
        CommitAddress expectedParent,
        UnifiedFullAuthoritySnapshot parentAuthority,
        UnifiedFullAuthoritySnapshot childAuthority)
        : base(message, failure)
    {
        OperationKind = operationKind;
        BranchName = branchName;
        ExpectedParent = expectedParent;
        ParentAuthority = parentAuthority;
        ChildAuthority = childAuthority;
        UnderlyingError = underlyingError;
        PublicationState = RepositoryCommitPublicationState.NotPublished;
        RequiresRepositoryReopen = true;
        CanRetryTransparently = false;
        MayHavePublished = false;
    }

    public RepositoryCommitError? Error { get; }

    public AteliaError? UnderlyingError { get; }

    public CommitAddress? CandidateAddress { get; }

    public RepositoryCommitFailurePhase? FailurePhase { get; }

    public RepositoryCommitPublicationState PublicationState { get; }

    public bool RequiresRepositoryReopen { get; }

    public bool CanRetryTransparently { get; }

    public bool MayHavePublished { get; }

    public UnifiedOperationKind OperationKind { get; }

    public string BranchName { get; }

    public CommitAddress ExpectedParent { get; }

    public UnifiedFullAuthoritySnapshot ParentAuthority { get; }

    public UnifiedFullAuthoritySnapshot ChildAuthority { get; }

    public static UnifiedCommitFailedException CreateKnownNotPublished(
        Exception failure,
        UnifiedOperationKind operationKind,
        string branchName,
        CommitAddress expectedParent,
        UnifiedFullAuthoritySnapshot parentAuthority,
        UnifiedFullAuthoritySnapshot childAuthority) => new(
            "Unified lineage commit failed before Repository.Commit; no candidate was written.",
            failure,
            null,
            operationKind,
            branchName,
            expectedParent,
            parentAuthority,
            childAuthority);

    public static UnifiedCommitFailedException CreateKnownNotPublished(
        AteliaError underlyingError,
        UnifiedOperationKind operationKind,
        string branchName,
        CommitAddress expectedParent,
        UnifiedFullAuthoritySnapshot parentAuthority,
        UnifiedFullAuthoritySnapshot childAuthority) => new(
            "Unified lineage commit returned a pre-candidate failure; no candidate was published.",
            null,
            underlyingError,
            operationKind,
            branchName,
            expectedParent,
            parentAuthority,
            childAuthority);
}

internal sealed class UnifiedFirstBoardProbeSession : IDisposable
{
    public const string MainBranch = "main";

    private readonly Repository _repository;
    private readonly ScenarioInstance _instance;
    private readonly string _branchName;
    private readonly DurableUnifiedFirstBoardRootV1 _root;
    private bool _disposed;
    private bool _poisoned;

    private UnifiedFirstBoardProbeSession(
        Repository repository,
        ScenarioInstance instance,
        string branchName,
        DurableUnifiedFirstBoardRootV1 root)
    {
        _repository = repository;
        _instance = instance;
        _branchName = branchName;
        _root = root;
        _root.ValidateComplete();
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

    public UnifiedSemanticSnapshot Snapshot()
    {
        RequireActive();
        return _root.Snapshot();
    }

    public UnifiedClosedObjectiveBaseline ExportClosedBaseline()
    {
        RequireActive();
        return _root.ExportClosedBaseline();
    }

    public static UnifiedFirstBoardProbeSession CreateImportedTravelingBaseline(
        string repositoryPath,
        ScenarioInstance instance,
        long lineageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        FirstBoardWorld imported = UnifiedProbeFixture.CreateTravelingWorld(instance);
        Repository repository = Require(
            Repository.Create(repositoryPath),
            "create unified StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(MainBranch),
                "create unified main branch");
            DurableUnifiedFirstBoardRootV1 root = DurableUnifiedFirstBoardRootV1.Create(
                revision,
                instance,
                PlayerClosureCompositionV1.Deterministic.PlayerCompositionId,
                lineageId,
                imported);
            root.ValidateComplete();
            _ = Require(repository.Commit(root.GraphRoot), "commit imported traveling baseline");
            return new UnifiedFirstBoardProbeSession(repository, instance, MainBranch, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static UnifiedFirstBoardProbeSession Open(
        string repositoryPath,
        ScenarioInstance instance,
        string branchName = MainBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open unified StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CheckoutBranch(branchName),
                $"checkout unified branch '{branchName}'");
            DurableUnifiedFirstBoardRootV1 root = DurableUnifiedFirstBoardRootV1.Open(
                revision,
                instance);
            return new UnifiedFirstBoardProbeSession(repository, instance, branchName, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static UnifiedFirstBoardProbeSession ForkAtCommit(
        string repositoryPath,
        ScenarioInstance instance,
        CommitAddress sourceCommit,
        WorldVersion expectedSourceVersion,
        string branchName,
        long childLineageId,
        bool failLineageReflogAfterPublication = false,
        bool failLineageBeforeRepositoryCommit = false,
        AteliaError? lineageCommitErrorForTest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        (UnifiedFullAuthoritySnapshot physicalParentAuthority,
         UnifiedFullAuthoritySnapshot expectedLineageAuthority) = PreflightLineageBoundary(
            repositoryPath,
            instance,
            sourceCommit,
            expectedSourceVersion,
            childLineageId);
        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open unified StateJournal repository for fork");
        bool branchCreated = false;
        bool repositoryCommitStarted = false;
        try
        {
            Revision revision = Require(
                repository.CreateBranch(branchName, sourceCommit),
                $"create unified branch '{branchName}'");
            branchCreated = true;
            DurableUnifiedFirstBoardRootV1 root = DurableUnifiedFirstBoardRootV1.Open(
                revision,
                instance);
            if (!root.AuthoritySnapshot().Matches(physicalParentAuthority))
            {
                throw new InvalidDataException(
                    "Unified branch checkout differs from the preflight source authority.");
            }

            root.StartChildLineage(childLineageId);
            root.ValidateComplete();
            UnifiedFullAuthoritySnapshot lineageAuthority = root.AuthoritySnapshot();
            if (!lineageAuthority.Matches(expectedLineageAuthority))
            {
                throw new InvalidDataException(
                    "Unified lineage authority changed between preflight and branch checkout.");
            }

            if (failLineageReflogAfterPublication)
            {
                CorruptBranchReflog(repositoryPath, branchName);
            }

            if (failLineageBeforeRepositoryCommit)
            {
                throw new InvalidOperationException(
                    "Injected unified lineage failure before Repository.Commit.");
            }

            repositoryCommitStarted = true;
            AteliaResult<CommitAddress> commit = lineageCommitErrorForTest is null
                ? repository.Commit(root.GraphRoot)
                : AteliaResult<CommitAddress>.Failure(lineageCommitErrorForTest);
            _ = RequireLineageCommit(
                commit,
                branchName,
                sourceCommit,
                physicalParentAuthority,
                lineageAuthority);

            return new UnifiedFirstBoardProbeSession(
                repository,
                instance,
                branchName,
                root);
        }
        catch (Exception exception) when (branchCreated && !repositoryCommitStarted)
        {
            repository.Dispose();
            throw UnifiedCommitFailedException.CreateKnownNotPublished(
                exception,
                UnifiedOperationKind.LineageStart,
                branchName,
                sourceCommit,
                physicalParentAuthority,
                expectedLineageAuthority);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static UnifiedFirstBoardProbeSession ResumeForkBranch(
        string repositoryPath,
        ScenarioInstance instance,
        UnifiedCommitFailedException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.OperationKind != UnifiedOperationKind.LineageStart)
        {
            throw new ArgumentException(
                "Only a failed unified lineage boundary can resume fork initialization.",
                nameof(failure));
        }

        if (failure.PublicationState != RepositoryCommitPublicationState.NotPublished)
        {
            throw new InvalidOperationException(
                "Unified fork resume requires a known-NotPublished failure receipt.");
        }

        UnifiedFirstBoardProbeSession session = Open(
            repositoryPath,
            instance,
            failure.BranchName);
        try
        {
            if (session.HeadAddress != failure.ExpectedParent ||
                !session._root.AuthoritySnapshot().Matches(failure.ParentAuthority))
            {
                throw new InvalidDataException(
                    "Unified fork branch no longer has the captured physical parent authority.");
            }

            session._root.StartChildLineage(failure.ChildAuthority.Version.LineageId);
            session._root.ValidateComplete();
            UnifiedFullAuthoritySnapshot reapplied = session._root.AuthoritySnapshot();
            if (!reapplied.Matches(failure.ChildAuthority))
            {
                throw new InvalidDataException(
                    "Reapplied unified lineage boundary differs from the captured child authority.");
            }

            AteliaResult<CommitAddress> commit = session._repository.Commit(
                session._root.GraphRoot);
            _ = RequireLineageCommit(
                commit,
                failure.BranchName,
                failure.ExpectedParent,
                failure.ParentAuthority,
                failure.ChildAuthority);

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static (
        UnifiedFullAuthoritySnapshot PhysicalParent,
        UnifiedFullAuthoritySnapshot LineageBoundary) PreflightLineageBoundary(
        string repositoryPath,
        ScenarioInstance instance,
        CommitAddress sourceCommit,
        WorldVersion expectedSourceVersion,
        long childLineageId)
    {
        using Repository repository = Require(
            Repository.Open(repositoryPath),
            "open unified StateJournal repository for lineage preflight");
        DurableObject graphRoot = Require(
            repository.LoadRootAtCommit(sourceCommit),
            $"load unified fork source '{sourceCommit}'");
        DurableUnifiedFirstBoardRootV1 root = DurableUnifiedFirstBoardRootV1.Open(
            graphRoot.Revision,
            instance);
        if (root.Version != expectedSourceVersion)
        {
            throw new InvalidDataException(
                $"Historical source '{sourceCommit}' has version '{root.Version}', " +
                $"not '{expectedSourceVersion}'.");
        }

        UnifiedFullAuthoritySnapshot physicalParent = root.AuthoritySnapshot();
        root.StartChildLineage(childLineageId);
        root.ValidateComplete();
        return (physicalParent, root.AuthoritySnapshot());
    }

    public static UnifiedClosedObjectiveBaseline ExportHistoricalBaseline(
        string repositoryPath,
        ScenarioInstance instance,
        CommitAddress address)
    {
        using Repository repository = Require(
            Repository.Open(repositoryPath),
            "open unified repository for historical baseline");
        DurableObject graphRoot = Require(
            repository.LoadRootAtCommit(address),
            $"load unified historical root '{address}'");
        DurableUnifiedFirstBoardRootV1 root = DurableUnifiedFirstBoardRootV1.Open(
            graphRoot.Revision,
            instance);
        return root.ExportClosedBaseline();
    }

    public async ValueTask<UnifiedPreparedTransaction> PrepareOpeningAsync()
    {
        RequireActive();
        FirstBoardWorld preWorld = _root.MaterializeWorld();
        IReadOnlyDictionary<string, IPlayerDriver> drivers = new Dictionary<string, IPlayerDriver>(
            new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
            {
                [BoardIds.Alice] = new NoCallPlayerDriver(),
            },
            StringComparer.Ordinal);
        var rule = new FirstBoardPassageEncounterRule(_instance.Graph, drivers);
        OccurrenceCandidate<BoardCandidate> winner = Single(
            rule.Forecast(preWorld, new SimulationRules(_instance.WorldSeed, 100)));
        TransitionDraft<FirstBoardFact> draft = await rule.PlanSelectedAsync(
            preWorld,
            winner,
            CancellationToken.None);
        var batch = new JournalBatch<FirstBoardFact>(
            NextInstant(winner.Due.ModelTime),
            winner.Key,
            draft.Facts);
        FirstBoardWorld expected = Fold(preWorld, batch);
        return new UnifiedPreparedTransaction(
            UnifiedOperationKind.Opening,
            _root.Version,
            batch,
            BasisRequest: null,
            Decision: null,
            PlayerEffect: null,
            UnifiedAuthorityFreeze.Objective(expected),
            DriverCallCount: 0);
    }

    public async ValueTask<UnifiedPreparedTransaction> PrepareResponseAsync(
        Intent response,
        string updatedWorkingContext)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedWorkingContext);
        RequireActive();
        FirstBoardWorld preWorld = _root.MaterializeWorld();
        var driver = new CapturingResponseDriver(response);
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = driver,
        };
        var rule = new FirstBoardPassageEncounterResponseRule(_instance.Graph, drivers);
        OccurrenceCandidate<BoardCandidate> winner = Single(
            rule.Forecast(preWorld, new SimulationRules(_instance.WorldSeed, 100)));
        TransitionDraft<FirstBoardFact> draft = await rule.PlanSelectedAsync(
            preWorld,
            winner,
            CancellationToken.None);
        DecisionRequest request = driver.Request ??
            throw new InvalidOperationException("The response driver did not capture its request.");
        PlayerDecision decision = driver.Decision ??
            throw new InvalidOperationException("The response driver did not capture its decision.");
        var batch = new JournalBatch<FirstBoardFact>(
            NextInstant(winner.Due.ModelTime),
            winner.Key,
            draft.Facts);
        FirstBoardWorld expected = Fold(preWorld, batch);
        BoardActor preActor = preWorld.Actor(BoardIds.Alice);
        BoardActor postActor = expected.Actor(BoardIds.Alice);
        IReadOnlyList<PlayerClosureMemoryValue> memory = _root.MemoryContents(BoardIds.Alice)
            .Select(value => string.Equals(value.Key, "working_context", StringComparison.Ordinal)
                ? value with { Content = updatedWorkingContext }
                : value)
            .ToArray();
        var effect = new UnifiedPreparedPlayerEffect(
            BoardIds.Alice,
            request.DecisionId,
            preActor.DecisionSequence,
            postActor.DecisionSequence,
            _root.PlayerCompositionId,
            memory,
            [
                .. request.Observation.KnownFacts.Select(UnifiedAuthorityFreeze.Fact),
            ]);
        return new UnifiedPreparedTransaction(
            UnifiedOperationKind.Response,
            _root.Version,
            batch,
            request,
            decision,
            effect,
            UnifiedAuthorityFreeze.Objective(expected),
            driver.CallCount);
    }

    public UnifiedCommitReceipt Commit(
        UnifiedPreparedTransaction transaction,
        UnifiedProbeFault fault = UnifiedProbeFault.None)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        RequireActive();
        _root.ValidatePreparedTransaction(transaction);
        FirstBoardWorld preWorld = _root.MaterializeWorld();
        FirstBoardWorld expectedPost = Fold(preWorld, transaction.Batch);
        UnifiedObjectiveAuthoritySnapshot expectedPostAuthority =
            UnifiedAuthorityFreeze.Objective(expectedPost);
        if (!expectedPostAuthority.Matches(transaction.ExpectedPostObjective))
        {
            throw new InvalidDataException(
                "Prepared unified transaction does not match the production reducer oracle.");
        }

        CommitAddress expectedParent = _root.HeadAddress;
        UnifiedFullAuthoritySnapshot parentAuthority = _root.AuthoritySnapshot();
        try
        {
            if (transaction.PlayerEffect is UnifiedPreparedPlayerEffect effect)
            {
                _root.ApplyPlayerEffect(effect);
                if (fault == UnifiedProbeFault.AfterCognition)
                {
                    throw new InvalidOperationException(
                        "Injected failure after unified Player cognition mutation.");
                }
            }

            foreach (FirstBoardFact fact in transaction.Batch.Facts)
            {
                _root.ApplyFact(transaction.Batch.Instant, fact);
                if (fault == UnifiedProbeFault.AfterGame && fact is GameBoardFact)
                {
                    throw new InvalidOperationException(
                        "Injected failure after unified Game mutation.");
                }

                if (fault == UnifiedProbeFault.AfterSpatial && fact is SpatialBoardFact)
                {
                    throw new InvalidOperationException(
                        "Injected failure after unified Spatial mutation.");
                }
            }

            _root.ValidateDomainClosure();
            UnifiedObjectiveAuthoritySnapshot actualPost =
                UnifiedAuthorityFreeze.Objective(_root.MaterializeWorld());
            if (!actualPost.Matches(expectedPostAuthority))
            {
                throw new InvalidDataException(
                    "Direct durable mutation differs from the production reducer post-world.");
            }

            _root.AdvanceFrontier(transaction);
            _root.ValidateComplete();
            if (fault == UnifiedProbeFault.AtCompleteValidation)
            {
                throw new InvalidOperationException(
                    "Injected failure after unified complete validation.");
            }

            UnifiedFullAuthoritySnapshot childAuthority = _root.AuthoritySnapshot();
            AteliaResult<CommitAddress> commit = _repository.Commit(_root.GraphRoot);
            if (commit.IsFailure)
            {
                Poison();
                if (commit.Error is not RepositoryCommitError error)
                {
                    throw new InvalidOperationException(
                        $"Unified commit failed before a structured candidate: {commit.Error}");
                }

                throw new UnifiedCommitFailedException(
                    error,
                    transaction.OperationKind,
                    _branchName,
                    expectedParent,
                    parentAuthority,
                    childAuthority);
            }

            CommitAddress head = commit.Value;
            return new UnifiedCommitReceipt(
                expectedParent,
                head,
                _root.Version,
                transaction.Batch);
        }
        catch (UnifiedCommitFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Poison();
            throw new InvalidOperationException(
                "Unified transaction failed after private mutation; reopen durable HEAD.",
                exception);
        }
    }

    public static UnifiedResolution ResolveUnknownOutcome(
        string repositoryPath,
        ScenarioInstance instance,
        UnifiedCommitFailedException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        using UnifiedFirstBoardProbeSession reopened = Open(
            repositoryPath,
            instance,
            failure.BranchName);
        UnifiedSemanticSnapshot actual = reopened.Snapshot();
        bool exactParent = reopened.HeadAddress == failure.ExpectedParent &&
            actual.Authority.Matches(failure.ParentAuthority);
        bool exactChild = failure.CandidateAddress is CommitAddress candidate &&
            reopened.HeadAddress == candidate &&
            reopened.HeadParentAddress == failure.ExpectedParent &&
            actual.Authority.Matches(failure.ChildAuthority);
        UnifiedProbeOutcome outcome = ClassifyPublication(
            failure.PublicationState,
            exactParent,
            exactChild);
        return new UnifiedResolution(outcome, actual);
    }

    public static UnifiedProbeOutcome ClassifyPublication(
        RepositoryCommitPublicationState publicationState,
        bool exactParent,
        bool exactChild)
    {
        if (exactParent == exactChild)
        {
            throw new InvalidDataException(
                "Unified reopened HEAD must match exactly one captured authority.");
        }

        return publicationState switch
        {
            RepositoryCommitPublicationState.Published when exactChild =>
                UnifiedProbeOutcome.Committed,
            RepositoryCommitPublicationState.NotPublished when exactParent =>
                UnifiedProbeOutcome.NotCommitted,
            RepositoryCommitPublicationState.Unknown or
            RepositoryCommitPublicationState.MayHavePublished => exactChild
                ? UnifiedProbeOutcome.Committed
                : UnifiedProbeOutcome.NotCommitted,
            _ => throw new InvalidDataException(
                $"Unified reopened authority contradicts publication state '{publicationState}'."),
        };
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

    private LogicalInstant NextInstant(ModelTime due)
    {
        LogicalInstant? previous = _root.LastInstant;
        long ordinal = previous is LogicalInstant last && last.ModelTime == due
            ? checked(last.CausalOrdinal + 1)
            : 0;
        return new LogicalInstant(due, ordinal);
    }

    private FirstBoardWorld Fold(
        FirstBoardWorld preWorld,
        JournalBatch<FirstBoardFact> batch)
    {
        var reducer = new FirstBoardReducer(_instance.Graph);
        FirstBoardWorld world = preWorld;
        foreach (FirstBoardFact fact in batch.Facts)
        {
            world = reducer.Apply(world, batch.Instant, fact);
        }

        reducer.Validate(world);
        return world;
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned)
        {
            throw new InvalidOperationException(
                $"Unified session '{_branchName}' is poisoned; reopen durable HEAD.");
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

    private static T Single<T>(IReadOnlyList<T> values)
    {
        if (values.Count != 1)
        {
            throw new InvalidOperationException(
                $"Unified fixture expected one candidate, found {values.Count}.");
        }

        return values[0];
    }

    private static T Require<T>(AteliaResult<T> result, string operation)
        where T : notnull
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Failed to {operation}: {result.Error}");
        }

        return result.Value!;
    }

    private static CommitAddress RequireLineageCommit(
        AteliaResult<CommitAddress> commit,
        string branchName,
        CommitAddress expectedParent,
        UnifiedFullAuthoritySnapshot parentAuthority,
        UnifiedFullAuthoritySnapshot childAuthority)
    {
        if (commit.IsSuccess)
        {
            return commit.Value;
        }

        AteliaError error = commit.Error!;
        if (error is RepositoryCommitError structured)
        {
            throw new UnifiedCommitFailedException(
                structured,
                UnifiedOperationKind.LineageStart,
                branchName,
                expectedParent,
                parentAuthority,
                childAuthority);
        }

        throw UnifiedCommitFailedException.CreateKnownNotPublished(
            error,
            UnifiedOperationKind.LineageStart,
            branchName,
            expectedParent,
            parentAuthority,
            childAuthority);
    }

    private static void CorruptBranchReflog(string repositoryPath, string branchName)
    {
        string reflogPath = Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            $"{branchName}.reflog.jsonl");
        File.Delete(reflogPath);
        Directory.CreateDirectory(reflogPath);
    }

    private sealed class NoCallPlayerDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Opening planning must not call a Player.");
    }

    private sealed class CapturingResponseDriver : IPlayerDriver
    {
        private readonly Intent _response;

        public CapturingResponseDriver(Intent response)
        {
            _response = response;
        }

        public DecisionRequest? Request { get; private set; }

        public PlayerDecision? Decision { get; private set; }

        public int CallCount { get; private set; }

        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            checked
            {
                CallCount++;
            }

            var decision = new PlayerDecision(request.DecisionId, _response);
            Decision = decision;
            return ValueTask.FromResult(decision);
        }
    }
}

internal static class UnifiedProbeFixture
{
    public static FirstBoardWorld CreateTravelingWorld(ScenarioInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld world = instance.CreateInitialWorld();
        var instant = new LogicalInstant(ModelTime.Zero, 0);
        world = reducer.Apply(
            world,
            instant,
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar))));
        world = Start(instance, reducer, world, BoardIds.Alice, instant);
        world = Start(instance, reducer, world, BoardIds.Bob, instant);
        reducer.Validate(world);
        return world;
    }

    private static FirstBoardWorld Start(
        ScenarioInstance instance,
        FirstBoardReducer reducer,
        FirstBoardWorld world,
        string actorId,
        LogicalInstant instant)
    {
        SpatialPlanResult plan = new SpatialPlanner(instance.Graph).TryStartTraversal(
            world.Spatial,
            new EntityId(actorId),
            new PassageId(BoardIds.TavernMarketRoad),
            BoardTiming.TravelSpeed,
            instant.ModelTime);
        SpatialPlanAccepted accepted = plan as SpatialPlanAccepted ??
            throw new InvalidOperationException(
                $"Could not create unified traveling fixture for '{actorId}'.");
        foreach (GraphSpatialFact fact in accepted.Facts)
        {
            world = reducer.Apply(world, instant, new SpatialBoardFact(fact));
        }

        return world;
    }
}

internal static class UnifiedProbeScalarCodec
{
    public static long CeilingDividePositive(long numerator, long denominator)
    {
        if (numerator <= 0 || denominator <= 0)
        {
            throw new InvalidOperationException("Traversal distance and speed must be positive.");
        }

        long quotient = numerator / denominator;
        long remainder = numerator % denominator;
        return checked(quotient + (remainder == 0 ? 0 : 1));
    }

    public static byte EncodePatch(PassageEntryPatch patch) => (byte)(
        (patch.EnterableFromA is not null ? 1 : 0) |
        (patch.EnterableFromA == true ? 2 : 0) |
        (patch.EnterableFromB is not null ? 4 : 0) |
        (patch.EnterableFromB == true ? 8 : 0));

    public static PassageEntryPatch DecodePatch(byte mask)
    {
        if (mask > 15 || (mask & 5) == 0)
        {
            throw new InvalidDataException("Passage patch mask is invalid.");
        }

        var patch = new PassageEntryPatch(
            (mask & 1) == 0 ? null : (mask & 2) != 0,
            (mask & 4) == 0 ? null : (mask & 8) != 0);
        if (EncodePatch(patch) != mask)
        {
            throw new InvalidDataException("Passage patch mask is noncanonical.");
        }

        return patch;
    }
}

internal static class UnifiedAuthorityFreeze
{
    public static PlayerClosureFactValue Fact(KnownFact fact) =>
        new(fact.FactKind.Id, fact.RelatedId, fact.Text);

    public static PlayerClosureFactValue Fact(BoardFact fact) =>
        new(fact.Kind, fact.RelatedId, fact.Text);

    public static UnifiedObjectiveAuthoritySnapshot Objective(FirstBoardWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new UnifiedObjectiveAuthoritySnapshot(
            world.WorldSeed,
            world.Game.NextPersistentId,
            world.Now,
            world.CellarSealed,
            world.ChestOpened,
            [
                .. world.Actors.OrderBy(actor => actor.Id).Select(actor =>
                    new UnifiedActorAuthority(
                        actor.Id,
                        actor.Key,
                        actor.Generation,
                        actor.DecisionSequence,
                        actor.Activity?.Due.Ticks,
                        actor.TravelGoalPlaceId?.Value,
                        [.. actor.KnownFacts.Select(Fact)])),
            ],
            [
                .. world.Objects.OrderBy(item => item.Id).Select(item =>
                    new UnifiedObjectAuthority(item.Id, item.Key, item.OwnerActorId)),
            ],
            [
                .. world.Spatial.Entities.Select(entity => new UnifiedEntityAuthority(
                    entity.Id.Value,
                    entity.MovementGeneration,
                    Location(entity.Location))),
            ],
            [
                .. world.Spatial.PassageEntryAccessOverrides.Select(value =>
                    new UnifiedOverrideAuthority(
                        value.PassageId.Value,
                        value.Access.EnterableFromA,
                        value.Access.EnterableFromB)),
            ],
            [
                .. world.Spatial.ScheduledPassageEntryChanges.Select(value =>
                    new UnifiedScheduleAuthority(
                        value.PassageId.Value,
                        value.Due.Ticks,
                        value.Patch.EnterableFromA,
                        value.Patch.EnterableFromB)),
            ],
            [
                .. world.Spatial.ConsumedContacts.Select(Contact),
            ],
            world.Game.PendingEncounter is PendingPassageEncounter pending
                ? new UnifiedPendingAuthority(Contact(pending.ContactKey), pending.Kind)
                : null);
    }

    public static UnifiedContactAuthority Contact(PassageContactKey key) => new(
        key.PassageId.Value,
        key.EntityA.Value,
        key.MovementGenerationA,
        key.EntityB.Value,
        key.MovementGenerationB);

    private static UnifiedLocationAuthority Location(SpatialLocation location) => location switch
    {
        AtPlaceLocation at => new UnifiedLocationAuthority(
            "at-place/1",
            at.PlaceId.Value,
            null,
            null,
            null,
            null,
            null,
            null),
        TraversingLocation traversal => new UnifiedLocationAuthority(
            "traversing/1",
            null,
            traversal.PassageId.Value,
            traversal.AnchorOffset,
            traversal.AnchorTime.Ticks,
            traversal.TargetPlaceId.Value,
            traversal.SpeedSnapshot,
            traversal.ArrivalDue.Ticks),
        _ => throw new InvalidOperationException(
            $"Unknown Spatial location '{location.GetType().Name}'."),
    };
}

internal sealed class DurableUnifiedFirstBoardRootV1
{
    private const string SchemaId = "firstboard.statejournal-unified-root/1";
    private const string PlayerStateSchemaId = "firstboard.statejournal-unified-player/1";
    private const string AtPlaceKind = "at-place/1";
    private const string TraversingKind = "traversing/1";
    private const string HeadOnKind = "head-on-meeting/1";
    private const string OvertakeKind = "overtake/1";

    private static class F
    {
        public const string Schema = "schemaId";
        public const string Definition = "definitionSha256";
        public const string Ruleset = "rulesetId";
        public const string WorldSeed = "worldSeed";
        public const string Composition = "playerCompositionId";
        public const string Lineage = "lineageId";
        public const string Count = "transitionCount";
        public const string Parent = "parentWorldVersion";
        public const string Last = "lastTransition";
        public const string Summary = "commitSummary";
        public const string Game = "game";
        public const string Spatial = "spatial";
        public const string Players = "playerStatesByActor";
        public const string ModelTime = "modelTimeMs";
        public const string Ordinal = "causalOrdinal";
        public const string Cause = "causeKeyBase64";

        public const string NextId = "nextPersistentId";
        public const string Now = "nowMs";
        public const string CellarSealed = "cellarSealed";
        public const string ChestOpened = "chestOpened";
        public const string Actors = "actorsByPersistentId";
        public const string Objects = "objectsByPersistentId";
        public const string Pending = "pendingEncounter";

        public const string Key = "key";
        public const string Generation = "generation";
        public const string DecisionSequence = "decisionSequence";
        public const string WaitDue = "waitDueMs";
        public const string TravelGoal = "travelGoalPlaceId";
        public const string KnownFacts = "knownFacts";
        public const string OwnerActorId = "ownerActorId";

        public const string Entities = "entitiesById";
        public const string Overrides = "passageEntryOverrides";
        public const string Schedules = "scheduledEntryChanges";
        public const string Contacts = "consumedContacts";
        public const string MovementGeneration = "movementGeneration";
        public const string LocationKind = "locationKind";
        public const string PlaceId = "placeId";
        public const string PassageId = "passageId";
        public const string AnchorOffset = "anchorOffset";
        public const string AnchorTime = "anchorTimeMs";
        public const string TargetPlace = "targetPlaceId";
        public const string Speed = "speedSnapshot";
        public const string ArrivalDue = "arrivalDueMs";

        public const string EntityA = "entityA";
        public const string GenerationA = "generationA";
        public const string EntityB = "entityB";
        public const string GenerationB = "generationB";
        public const string ContactKind = "contactKind";

        public const string PlayerSchema = "playerStateSchemaId";
        public const string Profile = "profileId";
        public const string Memory = "memoryByShardKey";
        public const string PreviousFacts = "previousKnownFacts";
        public const string FactKind = "factKind";
        public const string RelatedId = "relatedId";
        public const string Text = "text";
    }

    private readonly ScenarioInstance _instance;
    private readonly DurableDict<string> _data;
    private readonly DurableUnifiedGame _game;
    private readonly DurableUnifiedSpatial _spatial;
    private readonly DurableUnifiedPlayers _players;
    private DurableDict<string>? _parent;
    private DurableDict<string>? _last;

    private DurableUnifiedFirstBoardRootV1(
        ScenarioInstance instance,
        DurableDict<string> data,
        DurableUnifiedGame game,
        DurableUnifiedSpatial spatial,
        DurableUnifiedPlayers players,
        DurableDict<string>? parent,
        DurableDict<string>? last)
    {
        _instance = instance;
        _data = data;
        _game = game;
        _spatial = spatial;
        _players = players;
        _parent = parent;
        _last = last;
    }

    public DurableObject GraphRoot => _data;

    public CommitAddress HeadAddress =>
        _data.Revision.HeadAddress ??
        throw new InvalidOperationException("Unified root has no committed HEAD.");

    public CommitAddress? HeadParentAddress => _data.Revision.HeadParentAddress;

    public string PlayerCompositionId => _data.GetOrThrow<string>(F.Composition)!;

    public WorldVersion Version => new(
        _data.GetOrThrow<long>(F.Lineage),
        _data.GetOrThrow<long>(F.Count));

    public WorldVersion? ParentWorldVersion => _parent is null
        ? null
        : new WorldVersion(
            _parent.GetOrThrow<long>(F.Lineage),
            _parent.GetOrThrow<long>(F.Count));

    public LogicalInstant? LastInstant => _last is null
        ? null
        : new LogicalInstant(
            new ModelTime(_last.GetOrThrow<long>(F.ModelTime)),
            _last.GetOrThrow<long>(F.Ordinal));

    public CandidateKey? LastCause => _last is null
        ? null
        : CandidateKey.FromBytes(Convert.FromBase64String(
            _last.GetOrThrow<string>(F.Cause)!));

    public static DurableUnifiedFirstBoardRootV1 Create(
        Revision revision,
        ScenarioInstance instance,
        string playerCompositionId,
        long lineageId,
        FirstBoardWorld imported)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(playerCompositionId);
        ArgumentNullException.ThrowIfNull(imported);
        DurableDict<string> root = revision.CreateDict<string>();
        DurableDict<string> gameData = revision.CreateDict<string>();
        DurableDict<string> spatialData = revision.CreateDict<string>();
        DurableDict<string> playersData = revision.CreateDict<string>();
        DurableUnifiedGame game = DurableUnifiedGame.Create(gameData, imported);
        DurableUnifiedSpatial spatial = DurableUnifiedSpatial.Create(spatialData, imported.Spatial);
        DurableUnifiedPlayers players = DurableUnifiedPlayers.Create(
            playersData,
            instance,
            playerCompositionId);

        root.Upsert(F.Schema, SchemaId);
        root.Upsert(F.Definition, instance.DefinitionSha256);
        root.Upsert(F.Ruleset, instance.Definition.RulesetId);
        root.Upsert(F.WorldSeed, instance.WorldSeed);
        root.Upsert(F.Composition, playerCompositionId);
        root.Upsert(F.Lineage, lineageId);
        root.Upsert(F.Count, 0L);
        root.Upsert(F.Summary, "Imported complete traveling FirstBoard baseline.");
        root.Upsert(F.Game, gameData);
        root.Upsert(F.Spatial, spatialData);
        root.Upsert(F.Players, playersData);
        var result = new DurableUnifiedFirstBoardRootV1(
            instance,
            root,
            game,
            spatial,
            players,
            parent: null,
            last: null);
        result.ValidateComplete();
        return result;
    }

    public static DurableUnifiedFirstBoardRootV1 Open(
        Revision revision,
        ScenarioInstance instance)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        DurableDict<string> root = revision.GraphRoot as DurableDict<string> ??
            throw new InvalidDataException("StateJournal root is not a unified FirstBoard root.");
        DurableDict<string> gameData = root.GetOrThrow<DurableDict<string>>(F.Game)!;
        DurableDict<string> spatialData = root.GetOrThrow<DurableDict<string>>(F.Spatial)!;
        DurableDict<string> playersData = root.GetOrThrow<DurableDict<string>>(F.Players)!;
        var result = new DurableUnifiedFirstBoardRootV1(
            instance,
            root,
            DurableUnifiedGame.Open(gameData),
            DurableUnifiedSpatial.Open(spatialData),
            DurableUnifiedPlayers.Open(playersData, instance),
            OptionalDict(root, F.Parent),
            OptionalDict(root, F.Last));
        result.ValidateComplete();
        return result;
    }

    public FirstBoardWorld MaterializeWorld() => new(
        _game.Materialize(_instance.WorldSeed),
        _spatial.Materialize(_instance.Graph));

    public IReadOnlyList<PlayerClosureMemoryValue> MemoryContents(string actorId) =>
        _players.MemoryContents(actorId);

    public UnifiedClosedObjectiveBaseline ExportClosedBaseline()
    {
        FirstBoardWorld world = MaterializeWorld();
        ValidateComplete();
        return new UnifiedClosedObjectiveBaseline(world, Version, LastInstant);
    }

    public UnifiedSemanticSnapshot Snapshot() => new(
        AuthoritySnapshot(),
        OptionalString(_data, F.Summary, diagnostic: true));

    public UnifiedFullAuthoritySnapshot AuthoritySnapshot() => new(
        SchemaId,
        _instance.DefinitionSha256,
        _instance.Definition.RulesetId,
        _instance.WorldSeed,
        PlayerCompositionId,
        Version,
        ParentWorldVersion,
        LastInstant,
        LastCause,
        UnifiedAuthorityFreeze.Objective(MaterializeWorld()),
        _players.Authority());

    public void ValidatePreparedTransaction(UnifiedPreparedTransaction transaction)
    {
        if (transaction.ParentVersion != Version)
        {
            throw new InvalidDataException(
                $"Prepared parent '{transaction.ParentVersion}' differs from '{Version}'.");
        }

        ValidateOccurrence(transaction);

        if (LastCause is CandidateKey previous && previous == transaction.Batch.CauseKey)
        {
            throw new InvalidOperationException(
                "The prepared candidate repeats the immediately committed cause key.");
        }

        switch (transaction.OperationKind)
        {
            case UnifiedOperationKind.Opening:
                ValidateOpening(transaction);
                break;

            case UnifiedOperationKind.Response:
                ValidateResponse(transaction);
                break;

            default:
                throw new InvalidDataException(
                    $"Unified transaction operation '{transaction.OperationKind}' is not committable.");
        }
    }

    public void ApplyPlayerEffect(UnifiedPreparedPlayerEffect effect) =>
        _players.Apply(effect, _game.ActorDecisionSequence(effect.ActorId));

    public void ApplyFact(LogicalInstant instant, FirstBoardFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        switch (fact)
        {
            case SpatialBoardFact { Value: PassageContactOccurredFact contact }:
                _spatial.ApplyContact(_instance.Graph, instant, contact);
                break;
            case GameBoardFact { Value: PassageEncounterOpenedEvent opened }:
                _game.ApplyOpened(opened, _spatial.ContainsContact(opened.ContactKey));
                break;
            case GameBoardFact { Value: PassageEncounterResolvedEvent resolved }:
                _game.ApplyResolved(resolved);
                break;
            case SpatialBoardFact { Value: TraversalReversedFact reversed }:
                _spatial.ApplyReversed(_instance.Graph, instant, reversed);
                break;
            default:
                throw new NotSupportedException(
                    $"Unified probe does not support fact '{fact.GetType().Name}'.");
        }

        _game.SetNow(instant.ModelTime);
    }

    public void ValidateDomainClosure()
    {
        FirstBoardWorld world = MaterializeWorld();
        new FirstBoardReducer(_instance.Graph).Validate(world);
        _players.ValidateAgainstObjective(world);
    }

    public void AdvanceFrontier(UnifiedPreparedTransaction transaction)
    {
        if (transaction.ParentVersion != Version)
        {
            throw new InvalidOperationException("Unified frontier parent changed during mutation.");
        }

        if (LastInstant is LogicalInstant previous && transaction.Batch.Instant <= previous)
        {
            throw new InvalidOperationException("Unified LogicalInstant must advance strictly.");
        }

        _data.Upsert(F.Count, checked(Version.TransitionCount + 1));
        DurableDict<string> last = _last ?? _data.Revision.CreateDict<string>();
        last.Upsert(F.ModelTime, transaction.Batch.Instant.ModelTime.Ticks);
        last.Upsert(F.Ordinal, transaction.Batch.Instant.CausalOrdinal);
        last.Upsert(
            F.Cause,
            Convert.ToBase64String(transaction.Batch.CauseKey.ToByteArray()));
        if (_last is null)
        {
            _data.Upsert(F.Last, last);
            _last = last;
        }

        _data.Upsert(
            F.Summary,
            transaction.OperationKind == UnifiedOperationKind.Opening
                ? "Opened a passage encounter."
                : "Resolved a passage encounter with prepared Player cognition.");
    }

    public void StartChildLineage(long childLineageId)
    {
        WorldVersion source = Version;
        if (childLineageId == source.LineageId)
        {
            throw new ArgumentException("Unified child lineage must be fresh.", nameof(childLineageId));
        }

        DurableDict<string> parent = _data.Revision.CreateDict<string>();
        parent.Upsert(F.Lineage, source.LineageId);
        parent.Upsert(F.Count, source.TransitionCount);
        _data.Upsert(F.Parent, parent);
        _parent = parent;
        _data.Upsert(F.Lineage, childLineageId);
        _data.Upsert(
            F.Summary,
            $"Started child lineage {childLineageId} from {source}.");
    }

    public void ValidateComplete()
    {
        ValidateBindings();
        WorldVersion version = Version;
        LogicalInstant? instant = LastInstant;
        CandidateKey? cause = LastCause;
        if ((instant is null) != (cause is null) ||
            (version.TransitionCount == 0) != (instant is null))
        {
            throw new InvalidDataException(
                "Unified frontier count, last instant, and last cause have incompatible shapes.");
        }

        FirstBoardWorld world = MaterializeWorld();
        if (version.TransitionCount > 0 && world.Now != instant!.Value.ModelTime)
        {
            throw new InvalidDataException(
                "Unified Objective Now differs from the last committed instant.");
        }

        WorldVersion? parent = ParentWorldVersion;
        if (parent is WorldVersion origin &&
            (origin.LineageId == version.LineageId ||
             origin.TransitionCount > version.TransitionCount))
        {
            throw new InvalidDataException("Unified child lineage provenance is invalid.");
        }

        new FirstBoardReducer(_instance.Graph).Validate(world);
        _players.ValidateAgainstObjective(world);
    }

    private void ValidateOccurrence(UnifiedPreparedTransaction transaction)
    {
        FirstBoardWorld world = MaterializeWorld();
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new ForecastNoCallPlayerDriver(),
        };
        IReadOnlyList<OccurrenceCandidate<BoardCandidate>> candidates =
            transaction.OperationKind switch
            {
                UnifiedOperationKind.Opening =>
                [
                    .. new FirstBoardPassageEncounterRule(_instance.Graph, drivers).Forecast(
                        world,
                        new SimulationRules(_instance.WorldSeed, 100)),
                ],
                UnifiedOperationKind.Response =>
                [
                    .. new FirstBoardPassageEncounterResponseRule(_instance.Graph, drivers).Forecast(
                        world,
                        new SimulationRules(_instance.WorldSeed, 100)),
                ],
                _ => throw new InvalidDataException(
                    $"Unified operation '{transaction.OperationKind}' has no occurrence forecast."),
            };
        if (candidates.Count != 1)
        {
            throw new InvalidDataException(
                $"Unified occurrence forecast expected one candidate, found {candidates.Count}.");
        }

        OccurrenceCandidate<BoardCandidate> winner = candidates[0];
        LogicalInstant? previous = LastInstant;
        var expectedInstant = new LogicalInstant(
            winner.Due.ModelTime,
            previous is LogicalInstant last && last.ModelTime == winner.Due.ModelTime
                ? checked(last.CausalOrdinal + 1)
                : 0);
        if (winner.Key != transaction.Batch.CauseKey ||
            winner.Due.ModelTime != transaction.Batch.Instant.ModelTime ||
            transaction.Batch.Instant != expectedInstant)
        {
            throw new InvalidDataException(
                "Prepared batch is not bound to the current production occurrence winner.");
        }
    }

    private static void ValidateOpening(UnifiedPreparedTransaction transaction)
    {
        if (transaction.PlayerEffect is not null ||
            transaction.BasisRequest is not null ||
            transaction.Decision is not null ||
            transaction.DriverCallCount != 0 ||
            transaction.Batch.Facts.Count != 2 ||
            transaction.Batch.Facts[0] is not SpatialBoardFact
            {
                Value: PassageContactOccurredFact contact,
            } ||
            transaction.Batch.Facts[1] is not GameBoardFact
            {
                Value: PassageEncounterOpenedEvent opened,
            } ||
            contact.ContactKey != opened.ContactKey ||
            contact.Kind != opened.Kind)
        {
            throw new InvalidDataException(
                "Unified opening requires one paired Spatial-contact then Game-opening.");
        }
    }

    private void ValidateResponse(UnifiedPreparedTransaction transaction)
    {
        UnifiedPreparedPlayerEffect effect = transaction.PlayerEffect ??
            throw new InvalidDataException("Unified response requires a prepared Player effect.");
        DecisionRequest request = transaction.BasisRequest ??
            throw new InvalidDataException("Unified response requires its exact DecisionRequest.");
        PlayerDecision decision = transaction.Decision ??
            throw new InvalidDataException("Unified response requires its exact PlayerDecision.");
        PlayerDecisionValidationResult validation = PlayerDecisionValidator.Validate(decision, request);
        if (!string.Equals(effect.ActorId, BoardIds.Alice, StringComparison.Ordinal) ||
            !string.Equals(request.ActorId, effect.ActorId, StringComparison.Ordinal) ||
            request.DecisionId != effect.DecisionId ||
            decision.DecisionId != request.DecisionId ||
            !validation.IsValid ||
            effect.ExpectedDecisionSequence != _game.ActorDecisionSequence(effect.ActorId) ||
            effect.NextDecisionSequence != checked(effect.ExpectedDecisionSequence + 1) ||
            !string.Equals(effect.PlayerProfileId, PlayerCompositionId, StringComparison.Ordinal) ||
            transaction.DriverCallCount != 1)
        {
            throw new InvalidDataException("Unified prepared cognition binding is invalid.");
        }

        FirstBoardWorld world = MaterializeWorld();
        PendingPassageEncounter pending = world.Game.PendingEncounter ??
            throw new InvalidDataException("Unified response requires one pending encounter.");
        BoardActor actor = world.Actor(effect.ActorId);
        var actorEntityId = new EntityId(effect.ActorId);
        bool canReverse = new SpatialPlanner(_instance.Graph).TryReverseTraversal(
            world.Spatial,
            actorEntityId,
            world.Now) is SpatialPlanAccepted;
        DecisionRequest canonicalRequest = FirstBoardScenario.BuildPassageEncounterRequest(
            _instance.Graph,
            world,
            actor,
            pending,
            world.Now,
            canReverse);
        if (!RequestsMatch(request, canonicalRequest))
        {
            throw new InvalidDataException(
                "Prepared DecisionRequest differs from the canonical pre-world request.");
        }

        PlayerClosureFactValue[] requestFacts =
        [
            .. request.Observation.KnownFacts.Select(UnifiedAuthorityFreeze.Fact),
        ];
        if (!effect.PreviousKnownFacts.SequenceEqual(requestFacts))
        {
            throw new InvalidDataException(
                "Unified previous-known-facts differ from the pre-request observation.");
        }

        _players.ValidateEffectShape(effect);
        if (transaction.Batch.Facts.Count == 0 ||
            transaction.Batch.Facts[0] is not GameBoardFact
            {
                Value: PassageEncounterResolvedEvent resolved,
            } ||
            !string.Equals(resolved.RespondingActorId, effect.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Unified response must begin with the prepared actor's resolved event.");
        }

        if (resolved.ContactKey != pending.ContactKey)
        {
            throw new InvalidDataException("Unified resolved event names the wrong pending contact.");
        }

        UnifiedActorAuthority[] expectedActors =
        [
            .. transaction.ExpectedPostObjective.Actors.Where(actor =>
                string.Equals(actor.Key, effect.ActorId, StringComparison.Ordinal)),
        ];
        if (expectedActors.Length != 1 ||
            expectedActors[0].DecisionSequence != effect.NextDecisionSequence)
        {
            throw new InvalidDataException(
                "Prepared next sequence differs from the expected post Objective actor.");
        }

        if (decision.Intent.ActionKind == ActionKinds.ContinueTravel)
        {
            if (transaction.Batch.Facts.Count != 1 ||
                resolved.Resolution != PassageEncounterResolution.Continued)
            {
                throw new InvalidDataException(
                    "Continue intent must map only to one Continued Game fact.");
            }

            return;
        }

        if (decision.Intent.ActionKind != ActionKinds.ReverseTravel ||
            resolved.Resolution != PassageEncounterResolution.Reversed ||
            transaction.Batch.Facts.Count != 2 ||
            transaction.Batch.Facts[1] is not SpatialBoardFact
            {
                Value: TraversalReversedFact reversed,
            })
        {
            throw new InvalidDataException(
                "Reverse intent must map only to Reversed Game then Spatial reverse facts.");
        }

        long contactGeneration;
        if (pending.ContactKey.EntityA == actorEntityId)
        {
            contactGeneration = pending.ContactKey.MovementGenerationA;
        }
        else if (pending.ContactKey.EntityB == actorEntityId)
        {
            contactGeneration = pending.ContactKey.MovementGenerationB;
        }
        else
        {
            throw new InvalidDataException("Prepared actor is not a pending-contact participant.");
        }

        if (reversed.EntityId != actorEntityId ||
            reversed.ExpectedMovementGeneration != contactGeneration ||
            !world.Spatial.TryGetEntity(actorEntityId, out SpatialEntity? entity) ||
            entity is null ||
            entity.MovementGeneration != contactGeneration ||
            entity.Location is not TraversingLocation)
        {
            throw new InvalidDataException(
                "Spatial reverse does not name the prepared actor's current contact segment.");
        }
    }

    private static bool RequestsMatch(DecisionRequest actual, DecisionRequest expected) =>
        actual.DecisionId == expected.DecisionId &&
        string.Equals(actual.ActorId, expected.ActorId, StringComparison.Ordinal) &&
        actual.ModelTimeMs == expected.ModelTimeMs &&
        ObservationsMatch(actual.Observation, expected.Observation) &&
        ActionListsMatch(actual.AvailableActions, expected.AvailableActions);

    private static bool ObservationsMatch(Observation actual, Observation expected) =>
        string.Equals(actual.ActorId, expected.ActorId, StringComparison.Ordinal) &&
        string.Equals(actual.LocationId, expected.LocationId, StringComparison.Ordinal) &&
        actual.ModelTimeMs == expected.ModelTimeMs &&
        actual.Exits.SequenceEqual(expected.Exits) &&
        actual.VisibleActorIds.SequenceEqual(expected.VisibleActorIds, StringComparer.Ordinal) &&
        actual.VisibleObjectIds.SequenceEqual(expected.VisibleObjectIds, StringComparer.Ordinal) &&
        actual.KnownFacts.SequenceEqual(expected.KnownFacts);

    private static bool ActionListsMatch(
        IReadOnlyList<AvailableAction> actual,
        IReadOnlyList<AvailableAction> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (int index = 0; index < actual.Count; index++)
        {
            AvailableAction left = actual[index];
            AvailableAction right = expected[index];
            if (left.ActionKind != right.ActionKind ||
                !OptionalStringsMatch(left.CandidateActorIds, right.CandidateActorIds) ||
                !OptionalStringsMatch(left.CandidateObjectIds, right.CandidateObjectIds) ||
                !OptionalStringsMatch(left.CandidateExitIds, right.CandidateExitIds) ||
                !OptionalStringsMatch(
                    left.CandidateDestinationIds,
                    right.CandidateDestinationIds))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OptionalStringsMatch(
        IReadOnlyList<string>? actual,
        IReadOnlyList<string>? expected) =>
        actual is null
            ? expected is null
            : expected is not null && actual.SequenceEqual(expected, StringComparer.Ordinal);

    private sealed class ForecastNoCallPlayerDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Occurrence Forecast must not invoke a Player.");
    }

    private void ValidateBindings()
    {
        var rootKeys = new List<string>
        {
            F.Schema,
            F.Definition,
            F.Ruleset,
            F.WorldSeed,
            F.Composition,
            F.Lineage,
            F.Count,
            F.Game,
            F.Spatial,
            F.Players,
        };
        if (_parent is not null)
        {
            rootKeys.Add(F.Parent);
        }

        if (_last is not null)
        {
            rootKeys.Add(F.Last);
        }

        if (OptionalString(_data, F.Summary, diagnostic: true) is not null)
        {
            rootKeys.Add(F.Summary);
        }

        ExactKeys(_data.Keys, "unified root", [.. rootKeys]);
        if (!string.Equals(_data.GetOrThrow<string>(F.Schema), SchemaId, StringComparison.Ordinal) ||
            !string.Equals(
                _data.GetOrThrow<string>(F.Definition),
                _instance.DefinitionSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                _data.GetOrThrow<string>(F.Ruleset),
                _instance.Definition.RulesetId,
                StringComparison.Ordinal) ||
            _data.GetOrThrow<ulong>(F.WorldSeed) != _instance.WorldSeed ||
            !string.Equals(
                PlayerCompositionId,
                PlayerClosureCompositionV1.Deterministic.PlayerCompositionId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unified root binding mismatch.");
        }

        if (_parent is not null)
        {
            ExactKeys(_parent.Keys, "unified parent version", F.Lineage, F.Count);
        }

        if (_last is not null)
        {
            ExactKeys(_last.Keys, "unified last transition", F.ModelTime, F.Ordinal, F.Cause);
        }
    }

    private static DurableDict<string>? OptionalDict(DurableDict<string> source, string key)
    {
        GetIssue issue = source.Get(key, out DurableDict<string>? value);
        return issue switch
        {
            GetIssue.None => value ?? throw new InvalidDataException($"'{key}' is null."),
            GetIssue.NotFound => null,
            _ => throw new InvalidDataException($"'{key}' has wrong type: {issue}."),
        };
    }

    private static string? OptionalString(
        DurableDict<string> source,
        string key,
        bool diagnostic)
    {
        GetIssue issue = source.Get(key, out string? value);
        if (issue == GetIssue.NotFound || diagnostic && issue != GetIssue.None)
        {
            return null;
        }

        if (issue != GetIssue.None || string.IsNullOrWhiteSpace(value))
        {
            return diagnostic ? null : throw new InvalidDataException($"'{key}' is invalid.");
        }

        return value;
    }

    private static void ExactKeys(
        IEnumerable<string> actual,
        string context,
        params string[] expected)
    {
        string[] left = [.. actual.Order(StringComparer.Ordinal)];
        string[] right = [.. expected.Order(StringComparer.Ordinal)];
        if (!left.SequenceEqual(right, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{context} keys [{string.Join(",", left)}] != [{string.Join(",", right)}].");
        }
    }

    private sealed class DurableUnifiedGame
    {
        private readonly DurableDict<string> _data;
        private readonly DurableDict<long, DurableDict<string>> _actors;
        private readonly DurableDict<long, DurableDict<string>> _objects;
        private DurableDict<string>? _pending;

        private DurableUnifiedGame(
            DurableDict<string> data,
            DurableDict<long, DurableDict<string>> actors,
            DurableDict<long, DurableDict<string>> objects,
            DurableDict<string>? pending)
        {
            _data = data;
            _actors = actors;
            _objects = objects;
            _pending = pending;
        }

        public static DurableUnifiedGame Create(
            DurableDict<string> data,
            FirstBoardWorld world)
        {
            Revision revision = data.Revision;
            DurableDict<long, DurableDict<string>> actors =
                revision.CreateDict<long, DurableDict<string>>();
            DurableDict<long, DurableDict<string>> objects =
                revision.CreateDict<long, DurableDict<string>>();
            data.Upsert(F.NextId, world.Game.NextPersistentId);
            data.Upsert(F.Now, world.Now.Ticks);
            data.Upsert(F.CellarSealed, world.CellarSealed);
            data.Upsert(F.ChestOpened, world.ChestOpened);
            data.Upsert(F.Actors, actors);
            data.Upsert(F.Objects, objects);
            foreach (BoardActor actor in world.Actors)
            {
                DurableDict<string> actorData = revision.CreateDict<string>();
                WriteActor(actorData, actor);
                actors.Upsert(actor.Id, actorData);
            }

            foreach (BoardObject item in world.Objects)
            {
                DurableDict<string> objectData = revision.CreateDict<string>();
                objectData.Upsert(F.Key, item.Key);
                if (item.OwnerActorId is long owner)
                {
                    objectData.Upsert(F.OwnerActorId, owner);
                }

                objects.Upsert(item.Id, objectData);
            }

            DurableDict<string>? pending = null;
            if (world.Game.PendingEncounter is PendingPassageEncounter encounter)
            {
                pending = revision.CreateDict<string>();
                WritePending(pending, encounter);
                data.Upsert(F.Pending, pending);
            }

            return new DurableUnifiedGame(data, actors, objects, pending);
        }

        public static DurableUnifiedGame Open(DurableDict<string> data) => new(
            data,
            data.GetOrThrow<DurableDict<long, DurableDict<string>>>(F.Actors)!,
            data.GetOrThrow<DurableDict<long, DurableDict<string>>>(F.Objects)!,
            OptionalDict(data, F.Pending));

        public FirstBoardGameState Materialize(ulong worldSeed)
        {
            ExactKeys(
                _data.Keys,
                "unified Game",
                _pending is null
                    ? [F.NextId, F.Now, F.CellarSealed, F.ChestOpened, F.Actors, F.Objects]
                    : [F.NextId, F.Now, F.CellarSealed, F.ChestOpened, F.Actors, F.Objects, F.Pending]);
            BoardActor[] actors =
            [
                .. _actors.Keys.Order().Select(id => ReadActor(id, _actors.GetOrThrow(id)!)),
            ];
            BoardObject[] objects =
            [
                .. _objects.Keys.Order().Select(id => ReadObject(id, _objects.GetOrThrow(id)!)),
            ];
            return new FirstBoardGameState(
                worldSeed,
                _data.GetOrThrow<long>(F.NextId),
                new ModelTime(_data.GetOrThrow<long>(F.Now)),
                Array.AsReadOnly(actors),
                Array.AsReadOnly(objects),
                _data.GetOrThrow<bool>(F.CellarSealed),
                _data.GetOrThrow<bool>(F.ChestOpened),
                _pending is null ? null : ReadPending(_pending));
        }

        public long ActorDecisionSequence(string actorKey)
        {
            (long _, DurableDict<string> data) = FindActor(actorKey);
            return data.GetOrThrow<long>(F.DecisionSequence);
        }

        public void SetNow(ModelTime now) => _data.Upsert(F.Now, now.Ticks);

        public void ApplyOpened(PassageEncounterOpenedEvent opened, bool contactConsumed)
        {
            ArgumentNullException.ThrowIfNull(opened.ContactKey);
            if (_pending is not null)
            {
                throw new InvalidOperationException("FirstBoard supports one pending encounter.");
            }

            if (!contactConsumed)
            {
                throw new InvalidOperationException(
                    "A passage encounter can open only after its Spatial contact was consumed.");
            }

            _ = FindActor(opened.ContactKey.EntityA.Value);
            _ = FindActor(opened.ContactKey.EntityB.Value);
            DurableDict<string> pending = _data.Revision.CreateDict<string>();
            WritePending(
                pending,
                new PendingPassageEncounter(opened.ContactKey, opened.Kind));
            _data.Upsert(F.Pending, pending);
            _pending = pending;
        }

        public void ApplyResolved(PassageEncounterResolvedEvent resolved)
        {
            ArgumentNullException.ThrowIfNull(resolved.ContactKey);
            PendingPassageEncounter pending = _pending is null
                ? throw new InvalidOperationException("There is no pending encounter to resolve.")
                : ReadPending(_pending);
            if (pending.ContactKey != resolved.ContactKey)
            {
                throw new InvalidOperationException("Encounter resolution key mismatch.");
            }

            if (resolved.Resolution is not (
                    PassageEncounterResolution.Continued or
                    PassageEncounterResolution.Reversed) ||
                resolved.RespondingActorId is not string responderId)
            {
                throw new InvalidOperationException(
                    "Unified response supports only Player Continue or Reverse.");
            }

            var responderEntity = new EntityId(responderId);
            if (pending.ContactKey.EntityA != responderEntity &&
                pending.ContactKey.EntityB != responderEntity)
            {
                throw new InvalidOperationException("Responder is not an encounter participant.");
            }

            (_, DurableDict<string> actorData) = FindActor(responderId);
            actorData.Upsert(F.Generation, checked(actorData.GetOrThrow<long>(F.Generation) + 1));
            actorData.Upsert(
                F.DecisionSequence,
                checked(actorData.GetOrThrow<long>(F.DecisionSequence) + 1));
            if (resolved.Resolution == PassageEncounterResolution.Reversed)
            {
                _ = actorData.Remove(F.TravelGoal);
                IReadOnlyList<PlayerClosureFactValue> known = ReadFacts(
                    actorData.GetOrThrow<DurableDeque<DurableDict<string>>>(F.KnownFacts)!);
                var added = new PlayerClosureFactValue(
                    BoardIds.LastActionOutcome,
                    RelatedId: null,
                    "Your delegated travel was interrupted when you reversed after a passage encounter.");
                PlayerClosureFactValue[] merged =
                [
                    .. known.Append(added)
                        .GroupBy(fact => (fact.Kind, fact.RelatedId))
                        .Select(group => group.Last())
                        .OrderBy(fact => fact.Kind, StringComparer.Ordinal)
                        .ThenBy(fact => fact.RelatedId, StringComparer.Ordinal),
                ];
                WriteFacts(
                    actorData.GetOrThrow<DurableDeque<DurableDict<string>>>(F.KnownFacts)!,
                    merged);
            }

            _ = _data.Remove(F.Pending);
            _pending = null;
        }

        private (long Id, DurableDict<string> Data) FindActor(string key)
        {
            foreach (long id in _actors.Keys)
            {
                DurableDict<string> data = _actors.GetOrThrow(id)!;
                if (string.Equals(data.GetOrThrow<string>(F.Key), key, StringComparison.Ordinal))
                {
                    return (id, data);
                }
            }

            throw new InvalidDataException($"Unified actor '{key}' does not exist.");
        }

        private static void WriteActor(DurableDict<string> data, BoardActor actor)
        {
            data.Upsert(F.Key, actor.Key);
            data.Upsert(F.Generation, actor.Generation);
            data.Upsert(F.DecisionSequence, actor.DecisionSequence);
            if (actor.Activity is BoardWaitActivity wait)
            {
                data.Upsert(F.WaitDue, wait.Due.Ticks);
            }

            if (actor.TravelGoalPlaceId is PlaceId goal)
            {
                data.Upsert(F.TravelGoal, goal.Value);
            }

            DurableDeque<DurableDict<string>> facts =
                data.Revision.CreateDeque<DurableDict<string>>();
            data.Upsert(F.KnownFacts, facts);
            WriteFacts(facts, actor.KnownFacts.Select(UnifiedAuthorityFreeze.Fact));
        }

        private static BoardActor ReadActor(long id, DurableDict<string> data)
        {
            GetIssue waitIssue = data.Get(F.WaitDue, out long waitTicks);
            if (waitIssue is not GetIssue.None and not GetIssue.NotFound)
            {
                throw new InvalidDataException("Actor waitDue has wrong type.");
            }

            string? goal = OptionalString(data, F.TravelGoal, diagnostic: false);
            var expectedKeys = new List<string>
            {
                F.Key,
                F.Generation,
                F.DecisionSequence,
                F.KnownFacts,
            };
            if (waitIssue == GetIssue.None)
            {
                expectedKeys.Add(F.WaitDue);
            }

            if (goal is not null)
            {
                expectedKeys.Add(F.TravelGoal);
            }

            ExactKeys(data.Keys, "unified actor", [.. expectedKeys]);
            return new BoardActor(
                id,
                data.GetOrThrow<string>(F.Key)!,
                data.GetOrThrow<long>(F.Generation),
                data.GetOrThrow<long>(F.DecisionSequence),
                waitIssue == GetIssue.None ? new BoardWaitActivity(new ModelTime(waitTicks)) : null,
                goal is null ? null : new PlaceId(goal),
                [
                    .. ReadFacts(data.GetOrThrow<DurableDeque<DurableDict<string>>>(F.KnownFacts)!)
                        .Select(fact => new BoardFact(fact.Kind, fact.RelatedId, fact.Text)),
                ]);
        }

        private static BoardObject ReadObject(long id, DurableDict<string> data)
        {
            GetIssue issue = data.Get(F.OwnerActorId, out long owner);
            if (issue is not GetIssue.None and not GetIssue.NotFound)
            {
                throw new InvalidDataException("Object owner has wrong type.");
            }

            ExactKeys(
                data.Keys,
                "unified object",
                issue == GetIssue.None ? [F.Key, F.OwnerActorId] : [F.Key]);
            return new BoardObject(
                id,
                data.GetOrThrow<string>(F.Key)!,
                issue == GetIssue.None ? owner : null);
        }

        private static void WritePending(
            DurableDict<string> data,
            PendingPassageEncounter pending)
        {
            UnifiedContactKey key = ToContactKey(pending.ContactKey);
            data.Upsert(F.PassageId, key.PassageId);
            data.Upsert(F.EntityA, key.EntityA);
            data.Upsert(F.GenerationA, key.GenerationA);
            data.Upsert(F.EntityB, key.EntityB);
            data.Upsert(F.GenerationB, key.GenerationB);
            data.Upsert(F.ContactKind, WriteContactKind(pending.Kind));
        }

        private static PendingPassageEncounter ReadPending(DurableDict<string> data)
        {
            ExactKeys(
                data.Keys,
                "unified pending encounter",
                F.PassageId,
                F.EntityA,
                F.GenerationA,
                F.EntityB,
                F.GenerationB,
                F.ContactKind);
            return new PendingPassageEncounter(
                ReadContact(data),
                ReadContactKind(data.GetOrThrow<string>(F.ContactKind)!));
        }
    }

    private sealed class DurableUnifiedSpatial
    {
        private readonly DurableDict<string> _data;
        private readonly DurableDict<string, DurableDict<string>> _entities;
        private readonly DurableDict<string, byte> _overrides;
        private readonly DurableDict<UnifiedScheduleKey, byte> _schedules;
        private readonly DurableHashSet<UnifiedContactKey> _contacts;

        private DurableUnifiedSpatial(
            DurableDict<string> data,
            DurableDict<string, DurableDict<string>> entities,
            DurableDict<string, byte> overrides,
            DurableDict<UnifiedScheduleKey, byte> schedules,
            DurableHashSet<UnifiedContactKey> contacts)
        {
            _data = data;
            _entities = entities;
            _overrides = overrides;
            _schedules = schedules;
            _contacts = contacts;
        }

        public static DurableUnifiedSpatial Create(
            DurableDict<string> data,
            GraphSpatialState state)
        {
            Revision revision = data.Revision;
            DurableDict<string, DurableDict<string>> entities =
                revision.CreateDict<string, DurableDict<string>>();
            DurableDict<string, byte> overrides = revision.CreateDict<string, byte>();
            DurableDict<UnifiedScheduleKey, byte> schedules =
                revision.CreateDict<UnifiedScheduleKey, byte>();
            DurableHashSet<UnifiedContactKey> contacts =
                revision.CreateHashSet<UnifiedContactKey>();
            data.Upsert(F.Entities, entities);
            data.Upsert(F.Overrides, overrides);
            data.Upsert(F.Schedules, schedules);
            data.Upsert(F.Contacts, contacts);
            foreach (SpatialEntity entity in state.Entities)
            {
                DurableDict<string> entityData = revision.CreateDict<string>();
                WriteEntity(entityData, entity);
                entities.Upsert(entity.Id.Value, entityData);
            }

            foreach (PassageEntryAccessOverride value in state.PassageEntryAccessOverrides)
            {
                overrides.Upsert(value.PassageId.Value, AccessMask(value.Access));
            }

            foreach (ScheduledPassageEntryChange value in state.ScheduledPassageEntryChanges)
            {
                schedules.Upsert(
                    (value.PassageId.Value, value.Due.Ticks),
                    UnifiedProbeScalarCodec.EncodePatch(value.Patch));
            }

            foreach (PassageContactKey contact in state.ConsumedContacts)
            {
                _ = contacts.Add(ToContactKey(contact));
            }

            return new DurableUnifiedSpatial(data, entities, overrides, schedules, contacts);
        }

        public static DurableUnifiedSpatial Open(DurableDict<string> data) => new(
            data,
            data.GetOrThrow<DurableDict<string, DurableDict<string>>>(F.Entities)!,
            data.GetOrThrow<DurableDict<string, byte>>(F.Overrides)!,
            data.GetOrThrow<DurableDict<UnifiedScheduleKey, byte>>(F.Schedules)!,
            data.GetOrThrow<DurableHashSet<UnifiedContactKey>>(F.Contacts)!);

        public GraphSpatialState Materialize(GraphDefinition definition) =>
            MaterializeCore(definition);

        private GraphSpatialState MaterializeCore(GraphDefinition definition)
        {
            ExactKeys(_data.Keys, "unified Spatial", F.Entities, F.Overrides, F.Schedules, F.Contacts);
            return GraphSpatialState.Restore(
                definition,
                _entities.Keys.Order(StringComparer.Ordinal).Select(id =>
                    ReadEntity(id, _entities.GetOrThrow(id)!)),
                _overrides.Keys.Order(StringComparer.Ordinal).Select(id =>
                    new PassageEntryAccessOverride(
                        new PassageId(id),
                        ReadAccess(_overrides.GetOrThrow(id)))),
                _schedules.Keys
                    .OrderBy(key => key.PassageId, StringComparer.Ordinal)
                    .ThenBy(key => key.DueTicks)
                    .Select(key => new ScheduledPassageEntryChange(
                        new PassageId(key.PassageId),
                        new ModelTime(key.DueTicks),
                        UnifiedProbeScalarCodec.DecodePatch(_schedules.GetOrThrow(key)))),
                _contacts.Items.Select(FromContactKey));
        }

        public bool ContainsContact(PassageContactKey key) =>
            _contacts.Contains(ToContactKey(key));

        public void ApplyContact(
            GraphDefinition definition,
            LogicalInstant instant,
            PassageContactOccurredFact contact)
        {
            UnifiedContactKey key = ToContactKey(contact.ContactKey);
            if (_contacts.Contains(key))
            {
                throw new InvalidOperationException("Passage contact was already consumed.");
            }

            _ = new GraphSpatialReducer(definition).Apply(Materialize(definition), instant, contact);
            if (!_contacts.Add(key))
            {
                throw new InvalidOperationException("Could not add unified consumed contact.");
            }
        }

        public void ApplyReversed(
            GraphDefinition definition,
            LogicalInstant instant,
            TraversalReversedFact reversed)
        {
            string entityId = reversed.EntityId.Value;
            DurableDict<string> data = _entities.GetOrThrow(entityId) ??
                throw new InvalidOperationException($"Spatial entity '{entityId}' is absent.");
            SpatialEntity currentEntity = ReadEntity(entityId, data);
            if (currentEntity.MovementGeneration != reversed.ExpectedMovementGeneration ||
                currentEntity.Location is not TraversingLocation traversal)
            {
                throw new InvalidOperationException(
                    $"Spatial reverse for '{entityId}' does not match current segment.");
            }

            ModelTime at = instant.ModelTime;
            if (at <= traversal.AnchorTime || at >= traversal.ArrivalDue)
            {
                throw new InvalidOperationException("Spatial reverse must occur inside traversal.");
            }

            PassageDefinition passage = definition.GetPassage(traversal.PassageId);
            long elapsed = checked(at.Ticks - traversal.AnchorTime.Ticks);
            long advanced = checked(elapsed * traversal.SpeedSnapshot);
            bool targetsB = traversal.TargetPlaceId == passage.EndpointB;
            bool targetsA = traversal.TargetPlaceId == passage.EndpointA;
            if (!targetsA && !targetsB)
            {
                throw new InvalidOperationException("Traversal target is not a passage endpoint.");
            }

            long offset = targetsB
                ? checked(traversal.AnchorOffset + advanced)
                : checked(traversal.AnchorOffset - advanced);
            if (offset <= 0 || offset >= passage.Length)
            {
                throw new InvalidOperationException("Reverse point must be inside passage.");
            }

            PlaceId reverseTarget = targetsB ? passage.EndpointA : passage.EndpointB;
            PassageEntryAccess access = EffectiveAccess(passage);
            bool entryAllowed = targetsB
                ? access.EnterableFromB
                : access.EnterableFromA;
            if (!entryAllowed)
            {
                throw new InvalidOperationException("Reverse entry is currently closed.");
            }

            long distance = reverseTarget == passage.EndpointB
                ? checked(passage.Length - offset)
                : offset;
            long travelTicks = UnifiedProbeScalarCodec.CeilingDividePositive(
                distance,
                traversal.SpeedSnapshot);
            var updated = new SpatialEntity(
                currentEntity.Id,
                checked(currentEntity.MovementGeneration + 1),
                new TraversingLocation(
                    passage.Id,
                    offset,
                    at,
                    reverseTarget,
                    traversal.SpeedSnapshot,
                    new ModelTime(checked(at.Ticks + travelTicks))));
            WriteEntity(data, updated);

            UnifiedContactKey[] stale =
            [
                .. _contacts.Items.Where(contact =>
                    (string.Equals(contact.EntityA, entityId, StringComparison.Ordinal) &&
                     contact.GenerationA == currentEntity.MovementGeneration) ||
                    (string.Equals(contact.EntityB, entityId, StringComparison.Ordinal) &&
                     contact.GenerationB == currentEntity.MovementGeneration)),
            ];
            foreach (UnifiedContactKey contact in stale)
            {
                _ = _contacts.Remove(contact);
            }
        }

        private PassageEntryAccess EffectiveAccess(PassageDefinition passage)
        {
            GetIssue issue = _overrides.Get(passage.Id.Value, out byte mask);
            return issue switch
            {
                GetIssue.None => ReadAccess(mask),
                GetIssue.NotFound => passage.InitialEntryAccess,
                _ => throw new InvalidDataException("Passage override has wrong type."),
            };
        }

        private static void WriteEntity(DurableDict<string> data, SpatialEntity entity)
        {
            foreach (string key in data.Keys.ToArray())
            {
                _ = data.Remove(key);
            }

            data.Upsert(F.MovementGeneration, entity.MovementGeneration);
            switch (entity.Location)
            {
                case AtPlaceLocation at:
                    data.Upsert(F.LocationKind, AtPlaceKind);
                    data.Upsert(F.PlaceId, at.PlaceId.Value);
                    break;
                case TraversingLocation traversal:
                    data.Upsert(F.LocationKind, TraversingKind);
                    data.Upsert(F.PassageId, traversal.PassageId.Value);
                    data.Upsert(F.AnchorOffset, traversal.AnchorOffset);
                    data.Upsert(F.AnchorTime, traversal.AnchorTime.Ticks);
                    data.Upsert(F.TargetPlace, traversal.TargetPlaceId.Value);
                    data.Upsert(F.Speed, traversal.SpeedSnapshot);
                    data.Upsert(F.ArrivalDue, traversal.ArrivalDue.Ticks);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported Spatial location.");
            }
        }

        private static SpatialEntity ReadEntity(string id, DurableDict<string> data)
        {
            string kind = data.GetOrThrow<string>(F.LocationKind)!;
            ExactKeys(
                data.Keys,
                "unified Spatial entity",
                kind == AtPlaceKind
                    ? [F.MovementGeneration, F.LocationKind, F.PlaceId]
                    : [
                        F.MovementGeneration,
                        F.LocationKind,
                        F.PassageId,
                        F.AnchorOffset,
                        F.AnchorTime,
                        F.TargetPlace,
                        F.Speed,
                        F.ArrivalDue,
                    ]);
            SpatialLocation location = kind switch
            {
                AtPlaceKind => new AtPlaceLocation(new PlaceId(data.GetOrThrow<string>(F.PlaceId)!)),
                TraversingKind => new TraversingLocation(
                    new PassageId(data.GetOrThrow<string>(F.PassageId)!),
                    data.GetOrThrow<long>(F.AnchorOffset),
                    new ModelTime(data.GetOrThrow<long>(F.AnchorTime)),
                    new PlaceId(data.GetOrThrow<string>(F.TargetPlace)!),
                    data.GetOrThrow<long>(F.Speed),
                    new ModelTime(data.GetOrThrow<long>(F.ArrivalDue))),
                _ => throw new InvalidDataException($"Unknown location kind '{kind}'."),
            };
            return new SpatialEntity(
                new EntityId(id),
                data.GetOrThrow<long>(F.MovementGeneration),
                location);
        }

        private static byte AccessMask(PassageEntryAccess access) => (byte)(
            (access.EnterableFromA ? 1 : 0) |
            (access.EnterableFromB ? 2 : 0));

        private static PassageEntryAccess ReadAccess(byte mask)
        {
            if (mask > 3)
            {
                throw new InvalidDataException("Passage access mask is invalid.");
            }

            return new PassageEntryAccess((mask & 1) != 0, (mask & 2) != 0);
        }

    }

    private sealed class DurableUnifiedPlayers
    {
        private readonly DurableDict<string> _data;
        private readonly ScenarioInstance _instance;
        private readonly DurableDict<string, DurableDict<string>> _slots;

        private DurableUnifiedPlayers(
            DurableDict<string> data,
            ScenarioInstance instance,
            DurableDict<string, DurableDict<string>> slots)
        {
            _data = data;
            _instance = instance;
            _slots = slots;
        }

        public static DurableUnifiedPlayers Create(
            DurableDict<string> data,
            ScenarioInstance instance,
            string profileId)
        {
            DurableDict<string, DurableDict<string>> slots =
                data.Revision.CreateDict<string, DurableDict<string>>();
            data.Upsert(F.Players, slots);
            DurableDict<string> alice = data.Revision.CreateDict<string>();
            DurableDict<string, string> memory = data.Revision.CreateDict<string, string>();
            DurableDeque<DurableDict<string>> previous =
                data.Revision.CreateDeque<DurableDict<string>>();
            alice.Upsert(F.PlayerSchema, PlayerStateSchemaId);
            alice.Upsert(F.Profile, profileId);
            alice.Upsert(F.Memory, memory);
            alice.Upsert(F.PreviousFacts, previous);
            foreach (ScenarioMemoryShardDefinition shard in instance.Definition
                         .Actor(BoardIds.Alice).Role.InitialMemoryShards)
            {
                memory.Upsert(shard.Key, shard.InitialContent);
            }

            slots.Upsert(BoardIds.Alice, alice);
            return new DurableUnifiedPlayers(data, instance, slots);
        }

        public static DurableUnifiedPlayers Open(
            DurableDict<string> data,
            ScenarioInstance instance) => new(
            data,
            instance,
            data.GetOrThrow<DurableDict<string, DurableDict<string>>>(F.Players)!);

        public IReadOnlyList<PlayerClosureMemoryValue> MemoryContents(string actorId)
        {
            DurableDict<string> slot = Slot(actorId);
            DurableDict<string, string> memory =
                slot.GetOrThrow<DurableDict<string, string>>(F.Memory)!;
            return
            [
                .. memory.Keys.Order(StringComparer.Ordinal).Select(key =>
                    new PlayerClosureMemoryValue(key, RequiredString(memory, key))),
            ];
        }

        public IReadOnlyList<UnifiedPlayerSlotAuthority> Authority() =>
        [
            .. _slots.Keys.Order(StringComparer.Ordinal).Select(actorId =>
            {
                DurableDict<string> slot = Slot(actorId);
                return new UnifiedPlayerSlotAuthority(
                    actorId,
                    slot.GetOrThrow<string>(F.Profile)!,
                    MemoryContents(actorId),
                    ReadFacts(slot.GetOrThrow<DurableDeque<DurableDict<string>>>(
                        F.PreviousFacts)!));
            }),
        ];

        public void ValidateEffectShape(UnifiedPreparedPlayerEffect effect)
        {
            DurableDict<string> slot = Slot(effect.ActorId);
            if (!string.Equals(
                    slot.GetOrThrow<string>(F.Profile),
                    effect.PlayerProfileId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Prepared Player profile does not match slot.");
            }

            HashSet<string> expected = _instance.Definition.Actor(effect.ActorId)
                .Role.InitialMemoryShards.Select(shard => shard.Key)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> actual = effect.MemoryReplacements
                .Select(value => value.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (actual.Count != effect.MemoryReplacements.Count || !actual.SetEquals(expected) ||
                effect.MemoryReplacements.Any(value => string.IsNullOrWhiteSpace(value.Content)))
            {
                throw new InvalidDataException(
                    "Prepared cognition must replace every Definition Memory shard exactly once.");
            }
        }

        public void Apply(UnifiedPreparedPlayerEffect effect, long objectiveSequence)
        {
            if (effect.ExpectedDecisionSequence != objectiveSequence)
            {
                throw new InvalidDataException(
                    "Prepared cognition expected the wrong Objective decision sequence.");
            }

            ValidateEffectShape(effect);
            DurableDict<string> slot = Slot(effect.ActorId);
            DurableDict<string, string> memory =
                slot.GetOrThrow<DurableDict<string, string>>(F.Memory)!;
            foreach (PlayerClosureMemoryValue value in effect.MemoryReplacements)
            {
                memory.Upsert(value.Key, value.Content);
            }

            WriteFacts(
                slot.GetOrThrow<DurableDeque<DurableDict<string>>>(F.PreviousFacts)!,
                effect.PreviousKnownFacts);
        }

        public void ValidateAgainstObjective(FirstBoardWorld world)
        {
            ExactKeys(_data.Keys, "unified player roster", F.Players);
            string[] slotActors = [.. _slots.Keys.Order(StringComparer.Ordinal)];
            if (!slotActors.SequenceEqual([BoardIds.Alice], StringComparer.Ordinal))
            {
                throw new InvalidDataException("Unified V1 requires exactly the Alice Player slot.");
            }

            foreach (string actorId in slotActors)
            {
                _ = world.Actor(actorId);
                DurableDict<string> slot = Slot(actorId);
                ExactKeys(
                    slot.Keys,
                    "unified Player slot",
                    F.PlayerSchema,
                    F.Profile,
                    F.Memory,
                    F.PreviousFacts);
                if (!string.Equals(
                        slot.GetOrThrow<string>(F.PlayerSchema),
                        PlayerStateSchemaId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        slot.GetOrThrow<string>(F.Profile),
                        PlayerClosureCompositionV1.Deterministic.PlayerCompositionId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Unified Player slot schema/profile mismatch.");
                }

                DurableDict<string, string> memory =
                    slot.GetOrThrow<DurableDict<string, string>>(F.Memory)!;
                HashSet<string> expectedKeys = _instance.Definition.Actor(actorId)
                    .Role.InitialMemoryShards.Select(shard => shard.Key)
                    .ToHashSet(StringComparer.Ordinal);
                if (!memory.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys) ||
                    memory.Keys.Any(key => string.IsNullOrWhiteSpace(RequiredString(memory, key))))
                {
                    throw new InvalidDataException("Unified Player Memory closure is invalid.");
                }

                _ = ReadFacts(slot.GetOrThrow<DurableDeque<DurableDict<string>>>(
                    F.PreviousFacts)!);
            }
        }

        private DurableDict<string> Slot(string actorId) =>
            _slots.GetOrThrow(actorId) ??
            throw new InvalidDataException($"Unified Player slot '{actorId}' is missing.");

        private static string RequiredString(
            DurableDict<string, string> data,
            string key)
        {
            GetIssue issue = data.Get(key, out string? value);
            return issue == GetIssue.None && value is not null
                ? value
                : throw new InvalidDataException($"Required Player string '{key}' is missing.");
        }
    }

    private static IReadOnlyList<PlayerClosureFactValue> ReadFacts(
        DurableDeque<DurableDict<string>> source)
    {
        var result = new List<PlayerClosureFactValue>(source.Count);
        for (int index = 0; index < source.Count; index++)
        {
            DurableDict<string> item = source.GetAtOrThrow(index) ??
                throw new InvalidDataException("Unified fact row is null.");
            string? related = OptionalString(item, F.RelatedId, diagnostic: false);
            ExactKeys(
                item.Keys,
                "unified fact row",
                related is null
                    ? [F.FactKind, F.Text]
                    : [F.FactKind, F.RelatedId, F.Text]);
            string kind = item.GetOrThrow<string>(F.FactKind)!;
            string text = item.GetOrThrow<string>(F.Text)!;
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException("Unified fact kind/text is blank.");
            }

            result.Add(new PlayerClosureFactValue(kind, related, text));
        }

        return result;
    }

    private static void WriteFacts(
        DurableDeque<DurableDict<string>> destination,
        IEnumerable<PlayerClosureFactValue> facts)
    {
        while (destination.TryPopBack(out _))
        {
        }

        foreach (PlayerClosureFactValue fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Kind) || string.IsNullOrWhiteSpace(fact.Text) ||
                fact.RelatedId is not null && fact.RelatedId.Length == 0)
            {
                throw new InvalidDataException("Unified fact contains invalid values.");
            }

            DurableDict<string> row = destination.Revision.CreateDict<string>();
            row.Upsert(F.FactKind, fact.Kind);
            if (fact.RelatedId is not null)
            {
                row.Upsert(F.RelatedId, fact.RelatedId);
            }

            row.Upsert(F.Text, fact.Text);
            destination.PushBack(row);
        }
    }

    private static UnifiedContactKey ToContactKey(PassageContactKey key) => (
        key.PassageId.Value,
        key.EntityA.Value,
        key.MovementGenerationA,
        key.EntityB.Value,
        key.MovementGenerationB);

    private static PassageContactKey FromContactKey(UnifiedContactKey key)
    {
        var result = new PassageContactKey(
            new PassageId(key.PassageId),
            new EntityId(key.EntityA),
            key.GenerationA,
            new EntityId(key.EntityB),
            key.GenerationB);
        if (ToContactKey(result) != key)
        {
            throw new InvalidDataException("Unified contact tuple is not canonical.");
        }

        return result;
    }

    private static PassageContactKey ReadContact(DurableDict<string> data) =>
        FromContactKey((
            data.GetOrThrow<string>(F.PassageId)!,
            data.GetOrThrow<string>(F.EntityA)!,
            data.GetOrThrow<long>(F.GenerationA),
            data.GetOrThrow<string>(F.EntityB)!,
            data.GetOrThrow<long>(F.GenerationB)));

    private static string WriteContactKind(PassageContactKind kind) => kind switch
    {
        PassageContactKind.HeadOnMeeting => HeadOnKind,
        PassageContactKind.Overtake => OvertakeKind,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PassageContactKind ReadContactKind(string kind) => kind switch
    {
        HeadOnKind => PassageContactKind.HeadOnMeeting,
        OvertakeKind => PassageContactKind.Overtake,
        _ => throw new InvalidDataException($"Unknown unified contact kind '{kind}'."),
    };
}

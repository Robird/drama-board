using System.Security.Cryptography;
using System.Text;
using Atelia;
using Atelia.StateJournal;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Player.Llm;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

internal sealed record PlayerClosureFactValue(
    string Kind,
    string? RelatedId,
    string Text);

internal sealed record PlayerClosureMemoryValue(string Key, string Content);

internal sealed record PlayerClosureObjectiveActorSnapshot(
    long Generation,
    long DecisionSequence,
    IReadOnlyList<PlayerClosureFactValue> KnownFacts);

internal sealed record PlayerClosureCompositionV1(
    string PlayerCompositionId,
    string DriverKind,
    string DecisionBackendKind,
    string DecisionModel,
    string DecisionEffort,
    string DecisionEndpointIdentity,
    string MemoryBackendKind,
    string MemoryModel,
    string MemoryEffort,
    string MemoryEndpointIdentity,
    string MemoryMaintenanceMode,
    string Wrappers)
{
    public const string CurrentId = "firstboard.statejournal-player-closure-composition/1";

    public static PlayerClosureCompositionV1 Deterministic { get; } = new(
        CurrentId,
        DriverKind: "llm",
        DecisionBackendKind: "deterministic-no-call",
        DecisionModel: "observe-fixture/1",
        DecisionEffort: "provider-default",
        DecisionEndpointIdentity: "local://deterministic-no-call/decision",
        MemoryBackendKind: "deterministic-no-call",
        MemoryModel: "memory-fixture/1",
        MemoryEffort: "provider-default",
        MemoryEndpointIdentity: "local://deterministic-no-call/memory",
        MemoryMaintenanceMode: "blocking",
        Wrappers: "none");

    public string SlotBindingSha256(string definitionSha256, string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        string canonical = string.Join(
            '\n',
            "dramaboard.player-slot-binding/1",
            PlayerCompositionId,
            definitionSha256,
            actorId,
            DriverKind,
            DecisionBackendKind,
            DecisionModel,
            DecisionEffort,
            DecisionEndpointIdentity,
            MemoryBackendKind,
            MemoryModel,
            MemoryEffort,
            MemoryEndpointIdentity,
            MemoryMaintenanceMode,
            Wrappers);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public void ValidateForPersistence()
    {
        string[] values =
        [
            PlayerCompositionId,
            DriverKind,
            DecisionBackendKind,
            DecisionModel,
            DecisionEffort,
            MemoryBackendKind,
            MemoryModel,
            MemoryEffort,
            MemoryMaintenanceMode,
            Wrappers,
        ];
        if (values.Any(value => string.IsNullOrWhiteSpace(value) ||
                value.Contains('\n', StringComparison.Ordinal) ||
                value.Contains('\r', StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Player composition identifiers must be nonblank single-line values.");
        }

        ValidateSafeEndpoint(DecisionEndpointIdentity, nameof(DecisionEndpointIdentity));
        ValidateSafeEndpoint(MemoryEndpointIdentity, nameof(MemoryEndpointIdentity));
    }

    private static void ValidateSafeEndpoint(string value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException(
                $"Player composition {fieldName} must be an absolute safe identity without " +
                "userinfo, query, or fragment.");
        }
    }
}

internal sealed record PlayerClosureCognitiveEffectV1(
    string ActorId,
    DecisionId DecisionId,
    long ExpectedDecisionSequence,
    long NextDecisionSequence,
    string SlotBindingSha256,
    IReadOnlyList<PlayerClosureMemoryValue> MemoryReplacements,
    IReadOnlyList<PlayerClosureFactValue> PreviousKnownFacts);

internal sealed record PlayerClosureTransactionV1(
    WorldVersion ParentVersion,
    JournalBatch<FirstBoardFact> Batch,
    DecisionRequest BasisRequest,
    PlayerClosureCognitiveEffectV1 PlayerEffect,
    PlayerClosureObjectiveActorSnapshot ExpectedPostActor);

public enum PlayerClosureProbeFault
{
    None,
    AfterPlayerMemory,
    AfterObjectiveActor,
    AtCompleteValidation,
    AtRepositoryReflog,
}

public enum PlayerClosureMalformedState
{
    SlotBinding,
    ExtraMemoryShard,
    EmptyRelatedId,
}

internal enum PlayerClosureProbeOutcome
{
    NotCommitted,
    Committed,
}

internal sealed record PlayerClosureCommitReceipt(
    CommitAddress Parent,
    CommitAddress Head);

internal sealed record PlayerClosureResolution(
    PlayerClosureProbeOutcome Outcome,
    PlayerClosureSemanticSnapshot Snapshot);

internal sealed record PlayerClosureSemanticSnapshot(
    string SchemaId,
    string DefinitionSha256,
    string RulesetId,
    ulong WorldSeed,
    WorldVersion Version,
    WorldVersion? ParentWorldVersion,
    LogicalInstant? LastInstant,
    CandidateKey? LastCause,
    string CommitKind,
    string? CommitSummary,
    string ActorId,
    PlayerClosureObjectiveActorSnapshot ObjectiveActor,
    PlayerClosureCompositionV1 Composition,
    string SlotBindingSha256,
    long LastAppliedDecisionSequence,
    IReadOnlyList<PlayerClosureMemoryValue> MemoryContents,
    IReadOnlyList<PlayerClosureFactValue> PreviousKnownFacts)
{
    public bool AuthorityMatches(PlayerClosureSemanticSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal) &&
            string.Equals(DefinitionSha256, other.DefinitionSha256, StringComparison.Ordinal) &&
            string.Equals(RulesetId, other.RulesetId, StringComparison.Ordinal) &&
            WorldSeed == other.WorldSeed &&
            Version == other.Version &&
            ParentWorldVersion == other.ParentWorldVersion &&
            LastInstant == other.LastInstant &&
            LastCause == other.LastCause &&
            string.Equals(CommitKind, other.CommitKind, StringComparison.Ordinal) &&
            string.Equals(ActorId, other.ActorId, StringComparison.Ordinal) &&
            ObjectiveActor.Generation == other.ObjectiveActor.Generation &&
            ObjectiveActor.DecisionSequence == other.ObjectiveActor.DecisionSequence &&
            ObjectiveActor.KnownFacts.SequenceEqual(other.ObjectiveActor.KnownFacts) &&
            Composition == other.Composition &&
            string.Equals(SlotBindingSha256, other.SlotBindingSha256, StringComparison.Ordinal) &&
            LastAppliedDecisionSequence == other.LastAppliedDecisionSequence &&
            MemoryContents.SequenceEqual(other.MemoryContents) &&
            PreviousKnownFacts.SequenceEqual(other.PreviousKnownFacts);
    }
}

internal sealed class PlayerClosureCommitFailedException : InvalidOperationException
{
    public PlayerClosureCommitFailedException(
        RepositoryCommitError error,
        string branchName,
        CommitAddress expectedParent,
        PlayerClosureSemanticSnapshot parentAuthority,
        PlayerClosureSemanticSnapshot childAuthority)
        : base($"StateJournal Player closure commit outcome is unknown: {error}")
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (!string.Equals(error.BranchName, branchName, StringComparison.Ordinal) ||
            error.ExpectedHeadAddress != expectedParent)
        {
            throw new InvalidDataException(
                $"StateJournal reported branch/expected HEAD '{error.BranchName}'/" +
                $"'{error.ExpectedHeadAddress}', but the Player closure transaction captured " +
                $"'{branchName}'/'{expectedParent}'.");
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
    }

    public string BranchName { get; }

    public CommitAddress ExpectedParent { get; }

    public CommitAddress CandidateAddress { get; }

    public RepositoryCommitFailurePhase FailurePhase { get; }

    public RepositoryCommitPublicationState PublicationState { get; }

    public bool RequiresRepositoryReopen { get; }

    public bool CanRetryTransparently { get; }

    public bool MayHavePublished { get; }

    public PlayerClosureSemanticSnapshot ParentAuthority { get; }

    public PlayerClosureSemanticSnapshot ChildAuthority { get; }
}

internal sealed record PlayerClosureObserveFixture(
    ScenarioInstance Instance,
    PlayerClosureCompositionV1 Composition,
    FirstBoardWorld PreWorld,
    FirstBoardWorld PostWorld,
    DecisionRequest BasisRequest,
    DecisionRequest NextRequest,
    PlayerClosureTransactionV1 Transaction,
    LlmChatRequest ExpectedNextPrompt)
{
    private const string UpdatedWorkingContext =
        "我检查了酒馆；目前没有看到鲍勃或黄铜钥匙。";

    public static async Task<PlayerClosureObserveFixture> CreateAsync(
        ulong worldSeed,
        long lineageId)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        var driver = new CapturingObserveDriver();
        var rule = new DecisionPointRule(
            new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
            {
                [BoardIds.Alice] = driver,
            },
            instance);
        OccurrenceCandidate<BoardCandidate> winner = AssertSingle(
            rule.Forecast(genesis, new SimulationRules(worldSeed, 10_000)));
        TransitionDraft<FirstBoardFact> draft = await rule
            .PlanSelectedAsync(genesis, winner, CancellationToken.None)
            .ConfigureAwait(false);
        DecisionRequest basisRequest = driver.Request ??
            throw new InvalidOperationException("The Observe oracle did not capture a request.");
        var instant = new LogicalInstant(winner.Due.ModelTime, causalOrdinal: 0);
        var batch = new JournalBatch<FirstBoardFact>(instant, winner.Key, draft.Facts);

        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld postWorld = genesis;
        foreach (FirstBoardFact fact in batch.Facts)
        {
            postWorld = reducer.Apply(postWorld, instant, fact);
        }

        reducer.Validate(postWorld);
        BoardActor postActor = postWorld.Actor(BoardIds.Alice);
        DecisionRequest nextRequest = FirstBoardScenario.BuildRequest(
            instance,
            postWorld,
            postActor,
            postWorld.Now,
            DramaBoard.Player.Agency.Spatial.FullMapPlayerSpatialKnowledgeGetter<FirstBoardWorld>
                .Instance
                .GetKnownGraph(postWorld, postActor.Key, instance.Graph));

        PlayerClosureCompositionV1 composition = PlayerClosureCompositionV1.Deterministic;
        ScenarioActorDefinition actorDefinition = instance.Definition.Actor(BoardIds.Alice);
        IReadOnlyList<PlayerClosureMemoryValue> updatedMemory =
        [
            .. actorDefinition.Role.InitialMemoryShards.Select(shard =>
                new PlayerClosureMemoryValue(
                    shard.Key,
                    string.Equals(shard.Key, "working_context", StringComparison.Ordinal)
                        ? UpdatedWorkingContext
                        : shard.InitialContent)),
        ];
        IReadOnlyList<PlayerClosureFactValue> previousKnownFacts =
        [
            .. basisRequest.Observation.KnownFacts.Select(FromKnownFact),
        ];
        var effect = new PlayerClosureCognitiveEffectV1(
            BoardIds.Alice,
            basisRequest.DecisionId,
            ExpectedDecisionSequence: genesis.Actor(BoardIds.Alice).DecisionSequence,
            NextDecisionSequence: postActor.DecisionSequence,
            composition.SlotBindingSha256(instance.DefinitionSha256, BoardIds.Alice),
            updatedMemory,
            previousKnownFacts);
        var expectedActor = new PlayerClosureObjectiveActorSnapshot(
            postActor.Generation,
            postActor.DecisionSequence,
            [.. postActor.KnownFacts.Select(FromBoardFact)]);
        var transaction = new PlayerClosureTransactionV1(
            new WorldVersion(lineageId, 0),
            batch,
            basisRequest,
            effect,
            expectedActor);
        MemoryBank memory = CreateMemoryBank(actorDefinition, updatedMemory);
        LlmChatRequest expectedPrompt = RenderPrompt(
            actorDefinition,
            memory,
            nextRequest,
            [.. previousKnownFacts.Select(ToKnownFact)]);
        return new PlayerClosureObserveFixture(
            instance,
            composition,
            genesis,
            postWorld,
            basisRequest,
            nextRequest,
            transaction,
            expectedPrompt);
    }

    public static async Task<PlayerClosureObserveFixture> CreateFollowupAsync(
        PlayerClosureObserveFixture previous,
        WorldVersion parentVersion,
        string updatedWorkingContext)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedWorkingContext);
        ScenarioInstance instance = previous.Instance;
        FirstBoardWorld preWorld = previous.PostWorld;
        var driver = new CapturingObserveDriver();
        var rule = new DecisionPointRule(
            new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
            {
                [BoardIds.Alice] = driver,
            },
            instance);
        OccurrenceCandidate<BoardCandidate> winner = AssertSingle(
            rule.Forecast(preWorld, new SimulationRules(instance.WorldSeed, 10_000)));
        TransitionDraft<FirstBoardFact> draft = await rule
            .PlanSelectedAsync(preWorld, winner, CancellationToken.None)
            .ConfigureAwait(false);
        DecisionRequest basisRequest = driver.Request ??
            throw new InvalidOperationException(
                "The follow-up Observe oracle did not capture a request.");
        var instant = new LogicalInstant(
            winner.Due.ModelTime,
            causalOrdinal: parentVersion.TransitionCount);
        var batch = new JournalBatch<FirstBoardFact>(instant, winner.Key, draft.Facts);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld postWorld = preWorld;
        foreach (FirstBoardFact fact in batch.Facts)
        {
            postWorld = reducer.Apply(postWorld, instant, fact);
        }

        reducer.Validate(postWorld);
        BoardActor postActor = postWorld.Actor(BoardIds.Alice);
        DecisionRequest nextRequest = FirstBoardScenario.BuildRequest(
            instance,
            postWorld,
            postActor,
            postWorld.Now,
            DramaBoard.Player.Agency.Spatial.FullMapPlayerSpatialKnowledgeGetter<FirstBoardWorld>
                .Instance
                .GetKnownGraph(postWorld, postActor.Key, instance.Graph));
        IReadOnlyList<PlayerClosureMemoryValue> updatedMemory =
        [
            .. previous.Transaction.PlayerEffect.MemoryReplacements.Select(value =>
                string.Equals(value.Key, "working_context", StringComparison.Ordinal)
                    ? value with { Content = updatedWorkingContext }
                    : value),
        ];
        IReadOnlyList<PlayerClosureFactValue> previousKnownFacts =
        [
            .. basisRequest.Observation.KnownFacts.Select(FromKnownFact),
        ];
        var effect = new PlayerClosureCognitiveEffectV1(
            BoardIds.Alice,
            basisRequest.DecisionId,
            ExpectedDecisionSequence: preWorld.Actor(BoardIds.Alice).DecisionSequence,
            NextDecisionSequence: postActor.DecisionSequence,
            previous.Composition.SlotBindingSha256(
                instance.DefinitionSha256,
                BoardIds.Alice),
            updatedMemory,
            previousKnownFacts);
        var expectedActor = new PlayerClosureObjectiveActorSnapshot(
            postActor.Generation,
            postActor.DecisionSequence,
            [.. postActor.KnownFacts.Select(FromBoardFact)]);
        var transaction = new PlayerClosureTransactionV1(
            parentVersion,
            batch,
            basisRequest,
            effect,
            expectedActor);
        ScenarioActorDefinition actorDefinition = instance.Definition.Actor(BoardIds.Alice);
        MemoryBank memory = CreateMemoryBank(actorDefinition, updatedMemory);
        LlmChatRequest expectedPrompt = RenderPrompt(
            actorDefinition,
            memory,
            nextRequest,
            [.. previousKnownFacts.Select(ToKnownFact)]);
        return new PlayerClosureObserveFixture(
            instance,
            previous.Composition,
            preWorld,
            postWorld,
            basisRequest,
            nextRequest,
            transaction,
            expectedPrompt);
    }

    internal static MemoryBank CreateMemoryBank(
        ScenarioActorDefinition actor,
        IReadOnlyList<PlayerClosureMemoryValue> contents)
    {
        IReadOnlyDictionary<string, string> byKey = contents.ToDictionary(
            value => value.Key,
            value => value.Content,
            StringComparer.Ordinal);
        return new MemoryBank(actor.Role.InitialMemoryShards.Select(shard => new MemoryShard(
            shard.Key,
            shard.Title,
            shard.MaintenanceInstructions,
            byKey.TryGetValue(shard.Key, out string? content)
                ? content
                : throw new InvalidDataException(
                    $"Player state is missing Definition memory shard '{shard.Key}'."))));
    }

    internal static LlmChatRequest RenderPrompt(
        ScenarioActorDefinition actor,
        MemoryBank memory,
        DecisionRequest request,
        IReadOnlyList<KnownFact> previousKnownFacts) =>
        PromptRenderer.Render(
            new CharacterCard(
                actor.Role.Name,
                actor.Role.Traits,
                actor.Role.Goal,
                actor.Role.Voice),
            memory,
            request,
            previousKnownFacts,
            [
                .. actor.Role.ReferenceMaterials.Select(material => new ReferenceMaterial(
                    material.Id,
                    material.Source,
                    material.Content)),
            ]);

    internal static PlayerClosureFactValue FromKnownFact(KnownFact fact) =>
        new(fact.FactKind.Id, fact.RelatedId, fact.Text);

    internal static PlayerClosureFactValue FromBoardFact(BoardFact fact) =>
        new(fact.Kind, fact.RelatedId, fact.Text);

    internal static KnownFact ToKnownFact(PlayerClosureFactValue fact) =>
        new(new FactKind(fact.Kind), fact.RelatedId, fact.Text);

    private static T AssertSingle<T>(IReadOnlyList<T> values) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException(
                $"The Observe oracle expected one Alice candidate, but found {values.Count}.");

    private sealed class CapturingObserveDriver : IPlayerDriver
    {
        public DecisionRequest? Request { get; private set; }

        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Request is not null)
            {
                throw new InvalidOperationException("The Observe oracle requested two decisions.");
            }

            Request = request;
            return ValueTask.FromResult(
                new PlayerDecision(request.DecisionId, new Intent(ActionKinds.Observe)));
        }
    }
}

internal sealed class PlayerClosurePromptState
{
    private readonly ScenarioActorDefinition _actor;

    public PlayerClosurePromptState(
        ScenarioActorDefinition actor,
        long decisionSequence,
        MemoryBank memory,
        IReadOnlyList<KnownFact> previousKnownFacts)
    {
        _actor = actor;
        DecisionSequence = decisionSequence;
        Memory = memory;
        PreviousKnownFacts = previousKnownFacts;
    }

    public long DecisionSequence { get; }

    public MemoryBank Memory { get; }

    public IReadOnlyList<KnownFact> PreviousKnownFacts { get; }

    public LlmChatRequest Render(DecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ActorId, _actor.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Prompt request actor '{request.ActorId}' does not match slot '{_actor.Id}'.",
                nameof(request));
        }

        return PlayerClosureObserveFixture.RenderPrompt(
            _actor,
            Memory,
            request,
            PreviousKnownFacts);
    }
}

internal sealed class PlayerClosureProbeSession : IDisposable
{
    public const string MainBranch = "main";

    private readonly Repository _repository;
    private readonly ScenarioInstance _instance;
    private readonly string _branchName;
    private readonly DurablePlayerClosureProbeRootV1 _root;
    private bool _disposed;
    private bool _poisoned;

    private PlayerClosureProbeSession(
        Repository repository,
        ScenarioInstance instance,
        string branchName,
        DurablePlayerClosureProbeRootV1 root)
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

    public static PlayerClosureProbeSession Create(
        string repositoryPath,
        ScenarioInstance instance,
        PlayerClosureCompositionV1 composition,
        long genesisLineageId)
    {
        ValidateArguments(repositoryPath, instance, composition);
        Repository repository = Require(
            Repository.Create(repositoryPath),
            "create Player closure StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(MainBranch),
                "create Player closure main branch");
            DurablePlayerClosureProbeRootV1 root = DurablePlayerClosureProbeRootV1.Create(
                revision,
                instance,
                composition,
                genesisLineageId);
            root.ValidateComplete();
            _ = Require(
                repository.Commit(root.GraphRoot),
                "commit Player closure Genesis root");
            return new PlayerClosureProbeSession(repository, instance, MainBranch, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static PlayerClosureProbeSession Open(
        string repositoryPath,
        ScenarioInstance instance,
        string branchName = MainBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open Player closure StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CheckoutBranch(branchName),
                $"checkout Player closure branch '{branchName}'");
            DurablePlayerClosureProbeRootV1 root = DurablePlayerClosureProbeRootV1.Open(
                revision,
                instance);
            return new PlayerClosureProbeSession(repository, instance, branchName, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static PlayerClosureProbeSession ForkAtCommit(
        string repositoryPath,
        ScenarioInstance instance,
        CommitAddress sourceCommit,
        string branchName,
        long childLineageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open Player closure StateJournal repository for fork");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(branchName, sourceCommit),
                $"create Player closure branch '{branchName}'");
            DurablePlayerClosureProbeRootV1 root = DurablePlayerClosureProbeRootV1.Open(
                revision,
                instance);
            var session = new PlayerClosureProbeSession(
                repository,
                instance,
                branchName,
                root);
            session.CommitLineageStart(childLineageId);
            return session;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public PlayerClosureCommitReceipt Commit(
        PlayerClosureTransactionV1 transaction,
        PlayerClosureProbeFault fault = PlayerClosureProbeFault.None)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        RequireActive();
        CommitAddress expectedParent = _root.HeadAddress;
        _root.RequireExpectedParent(transaction.ParentVersion);
        PlayerClosureSemanticSnapshot parentAuthority = _root.Snapshot();

        try
        {
            _root.ValidateTransaction(transaction);
            _root.ApplyPlayerMemory(transaction.PlayerEffect);
            if (fault == PlayerClosureProbeFault.AfterPlayerMemory)
            {
                throw new InvalidOperationException(
                    "Injected failure after Player Memory mutated but before closure completed.");
            }

            _root.ApplyPlayerFrontier(transaction.PlayerEffect);
            _root.ApplyObservedObjective(transaction);
            if (fault == PlayerClosureProbeFault.AfterObjectiveActor)
            {
                throw new InvalidOperationException(
                    "Injected failure after Objective actor mutation but before complete validation.");
            }

            _root.AdvanceFrontier(transaction);
            _root.ValidateComplete(
                injectFailure: fault == PlayerClosureProbeFault.AtCompleteValidation);
            PlayerClosureSemanticSnapshot childAuthority = _root.Snapshot();
            if (fault == PlayerClosureProbeFault.AtRepositoryReflog)
            {
                InstallReflogDirectoryFault(_repository.DirectoryPath, _branchName);
            }

            CommitAddress head = CommitGraph(
                expectedParent,
                parentAuthority,
                childAuthority);
            return new PlayerClosureCommitReceipt(expectedParent, head);
        }
        catch (PlayerClosureCommitFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Poison();
            throw new InvalidOperationException(
                "The Player closure transaction failed after private working mutation; " +
                "the Session is poisoned and must be reopened from HEAD.",
                exception);
        }
    }

    public PlayerClosureSemanticSnapshot Snapshot()
    {
        RequireActive();
        return _root.Snapshot();
    }

    public PlayerClosurePromptState AttachPromptState(
        PlayerClosureCompositionV1 runtimeComposition)
    {
        ArgumentNullException.ThrowIfNull(runtimeComposition);
        RequireActive();
        _root.ValidateComplete();
        if (_root.Composition != runtimeComposition)
        {
            throw new InvalidDataException(
                "Runtime Player composition differs from the durable closed composition.");
        }

        ScenarioActorDefinition actor = _instance.Definition.Actor(_root.ActorId);
        return new PlayerClosurePromptState(
            actor,
            _root.ObjectiveActor.DecisionSequence,
            PlayerClosureObserveFixture.CreateMemoryBank(actor, _root.MemoryContents()),
            [.. _root.PreviousKnownFacts().Select(PlayerClosureObserveFixture.ToKnownFact)]);
    }

    public void CommitMalformedForTest(PlayerClosureMalformedState malformed)
    {
        RequireActive();
        _root.ApplyMalformedForTest(malformed);
        _ = Require(
            _repository.Commit(_root.GraphRoot),
            "commit malformed Player closure fixture");
    }

    public static PlayerClosureResolution ResolveUnknownOutcome(
        string repositoryPath,
        ScenarioInstance instance,
        PlayerClosureCommitFailedException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        using PlayerClosureProbeSession reopened = Open(
            repositoryPath,
            instance,
            failure.BranchName);
        PlayerClosureSemanticSnapshot actual = reopened.Snapshot();
        if (reopened.HeadAddress == failure.ExpectedParent)
        {
            if (actual.AuthorityMatches(failure.ParentAuthority))
            {
                return new PlayerClosureResolution(PlayerClosureProbeOutcome.NotCommitted, actual);
            }

            throw new InvalidDataException(
                "Reopened Player closure HEAD has the parent address but not its authority state.");
        }

        if (reopened.HeadAddress != failure.CandidateAddress)
        {
            throw new InvalidDataException(
                $"Reopened Player closure HEAD '{reopened.HeadAddress}' is neither parent nor " +
                $"reported candidate '{failure.CandidateAddress}'.");
        }

        if (_ExactChild(reopened, failure))
        {
            return new PlayerClosureResolution(PlayerClosureProbeOutcome.Committed, actual);
        }

        throw new InvalidDataException(
            "Reopened Player closure HEAD is neither the exact parent nor proposed child.");

        static bool _ExactChild(
            PlayerClosureProbeSession reopenedSession,
            PlayerClosureCommitFailedException failed) =>
            reopenedSession._root.HeadParentAddress == failed.ExpectedParent &&
            reopenedSession.Snapshot().AuthorityMatches(failed.ChildAuthority);
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

    private void CommitLineageStart(long childLineageId)
    {
        CommitAddress expectedParent = _root.HeadAddress;
        PlayerClosureSemanticSnapshot parentAuthority = _root.Snapshot();
        try
        {
            _root.ApplyChildLineageBoundary(childLineageId);
            _root.ValidateComplete();
            PlayerClosureSemanticSnapshot childAuthority = _root.Snapshot();
            _ = CommitGraph(expectedParent, parentAuthority, childAuthority);
        }
        catch (PlayerClosureCommitFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Poison();
            throw new InvalidOperationException(
                "The Player closure lineage-start failed after private working mutation.",
                exception);
        }
    }

    private CommitAddress CommitGraph(
        CommitAddress expectedParent,
        PlayerClosureSemanticSnapshot parentAuthority,
        PlayerClosureSemanticSnapshot childAuthority)
    {
        AteliaResult<CommitAddress> result = _repository.Commit(_root.GraphRoot);
        if (result.IsFailure)
        {
            AteliaError error = result.Error!;
            Poison();
            if (error is not RepositoryCommitError commitError)
            {
                throw new InvalidOperationException(
                    "StateJournal Player closure commit failed before returning a structured " +
                    $"candidate outcome: {error}");
            }

            throw new PlayerClosureCommitFailedException(
                commitError,
                _branchName,
                expectedParent,
                parentAuthority,
                childAuthority);
        }

        return result.Value;
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned)
        {
            throw new InvalidOperationException(
                $"Player closure branch '{_branchName}' is poisoned; reopen durable HEAD.");
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

    private static void InstallReflogDirectoryFault(
        string repositoryPath,
        string branchName)
    {
        string relative = branchName.Replace('/', Path.DirectorySeparatorChar);
        string reflogPath = Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            relative + ".reflog.jsonl");
        File.Delete(reflogPath);
        Directory.CreateDirectory(reflogPath);
    }

    private static void ValidateArguments(
        string repositoryPath,
        ScenarioInstance instance,
        PlayerClosureCompositionV1 composition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(composition);
        composition.ValidateForPersistence();
        if (composition.MemoryMaintenanceMode != "blocking")
        {
            throw new ArgumentException(
                "The Player closure V1 probe accepts only Blocking maintenance.",
                nameof(composition));
        }
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
}

internal sealed class DurablePlayerClosureProbeRootV1
{
    private const string SchemaId = "firstboard.statejournal-player-closure-root/1";
    private const string PlayerStateSchemaId = "firstboard.statejournal-llm-player-state/1";
    private const string GenesisKind = "genesis";
    private const string LineageStartKind = "lineage-start";
    private const string ObjectiveTransitionKind = "objective-transition";

    private static class Fields
    {
        public const string Schema = "schemaId";
        public const string Definition = "definitionSha256";
        public const string Ruleset = "rulesetId";
        public const string WorldSeed = "worldSeed";
        public const string Lineage = "lineageId";
        public const string TransitionCount = "transitionCount";
        public const string ParentWorldVersion = "parentWorldVersion";
        public const string LastTransition = "lastTransition";
        public const string CommitKind = "commitKind";
        public const string CommitSummary = "commitSummary";
        public const string ObjectiveActor = "objectiveActor";
        public const string Composition = "playerComposition";
        public const string PlayerState = "playerState";
        public const string ActorId = "actorId";
        public const string ActorKey = "actorKey";
        public const string Generation = "generation";
        public const string DecisionSequence = "decisionSequence";
        public const string KnownFacts = "knownFacts";
        public const string PlayerCompositionId = "playerCompositionId";
        public const string DriverKind = "driverKind";
        public const string DecisionBackendKind = "decisionBackendKind";
        public const string DecisionModel = "decisionModel";
        public const string DecisionEffort = "decisionEffort";
        public const string DecisionEndpoint = "decisionEndpointIdentity";
        public const string MemoryBackendKind = "memoryBackendKind";
        public const string MemoryModel = "memoryModel";
        public const string MemoryEffort = "memoryEffort";
        public const string MemoryEndpoint = "memoryEndpointIdentity";
        public const string MaintenanceMode = "memoryMaintenanceMode";
        public const string Wrappers = "wrappers";
        public const string PlayerStateSchema = "playerStateSchemaId";
        public const string SlotBinding = "slotBindingSha256";
        public const string LastAppliedDecisionSequence = "lastAppliedDecisionSequence";
        public const string MemoryContents = "memoryContentsByShardKey";
        public const string PreviousKnownFacts = "previousKnownFacts";
        public const string FactKind = "factKind";
        public const string RelatedId = "relatedId";
        public const string Text = "text";
        public const string ModelTime = "modelTimeMs";
        public const string CausalOrdinal = "causalOrdinal";
        public const string CauseKey = "causeKeyBase64";
    }

    private readonly ScenarioInstance _instance;
    private readonly DurableDict<string> _root;
    private readonly DurableDict<string> _objectiveActor;
    private readonly DurableDeque<DurableDict<string>> _objectiveKnownFacts;
    private readonly DurableDict<string> _composition;
    private readonly DurableDict<string> _playerState;
    private readonly DurableDict<string, string> _memoryContents;
    private readonly DurableDeque<DurableDict<string>> _previousKnownFacts;
    private DurableDict<string>? _parentWorldVersion;
    private DurableDict<string>? _lastTransition;

    private DurablePlayerClosureProbeRootV1(
        ScenarioInstance instance,
        DurableDict<string> root,
        DurableDict<string> objectiveActor,
        DurableDeque<DurableDict<string>> objectiveKnownFacts,
        DurableDict<string> composition,
        DurableDict<string> playerState,
        DurableDict<string, string> memoryContents,
        DurableDeque<DurableDict<string>> previousKnownFacts,
        DurableDict<string>? parentWorldVersion,
        DurableDict<string>? lastTransition)
    {
        _instance = instance;
        _root = root;
        _objectiveActor = objectiveActor;
        _objectiveKnownFacts = objectiveKnownFacts;
        _composition = composition;
        _playerState = playerState;
        _memoryContents = memoryContents;
        _previousKnownFacts = previousKnownFacts;
        _parentWorldVersion = parentWorldVersion;
        _lastTransition = lastTransition;
    }

    public DurableObject GraphRoot => _root;

    public CommitAddress HeadAddress => _root.Revision.HeadAddress ??
        throw new InvalidOperationException("Player closure root has no committed HEAD.");

    public CommitAddress? HeadParentAddress => _root.Revision.HeadParentAddress;

    public string ActorId => _objectiveActor.GetOrThrow<string>(Fields.ActorId)!;

    public PlayerClosureObjectiveActorSnapshot ObjectiveActor => new(
        _objectiveActor.GetOrThrow<long>(Fields.Generation),
        _objectiveActor.GetOrThrow<long>(Fields.DecisionSequence),
        ReadFacts(_objectiveKnownFacts));

    public PlayerClosureCompositionV1 Composition => ReadComposition();

    private WorldVersion Version => new(
        _root.GetOrThrow<long>(Fields.Lineage),
        _root.GetOrThrow<long>(Fields.TransitionCount));

    private WorldVersion? ParentWorldVersion => _parentWorldVersion is null
        ? null
        : ReadWorldVersion(_parentWorldVersion);

    private LogicalInstant? LastInstant => _lastTransition is null
        ? null
        : new LogicalInstant(
            new ModelTime(_lastTransition.GetOrThrow<long>(Fields.ModelTime)),
            _lastTransition.GetOrThrow<long>(Fields.CausalOrdinal));

    private CandidateKey? LastCause => _lastTransition is null
        ? null
        : CandidateKey.FromBytes(Convert.FromBase64String(
            _lastTransition.GetOrThrow<string>(Fields.CauseKey)!));

    public static DurablePlayerClosureProbeRootV1 Create(
        Revision revision,
        ScenarioInstance instance,
        PlayerClosureCompositionV1 composition,
        long genesisLineageId)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(composition);
        BoardActor actor = instance.CreateInitialWorld().Actor(BoardIds.Alice);
        ScenarioActorDefinition actorDefinition = instance.Definition.Actor(BoardIds.Alice);

        DurableDict<string> root = revision.CreateDict<string>();
        DurableDict<string> objectiveActor = revision.CreateDict<string>();
        DurableDeque<DurableDict<string>> objectiveKnownFacts =
            revision.CreateDeque<DurableDict<string>>();
        DurableDict<string> durableComposition = revision.CreateDict<string>();
        DurableDict<string> playerState = revision.CreateDict<string>();
        DurableDict<string, string> memoryContents = revision.CreateDict<string, string>();
        DurableDeque<DurableDict<string>> previousKnownFacts =
            revision.CreateDeque<DurableDict<string>>();

        root.Upsert(Fields.Schema, SchemaId);
        root.Upsert(Fields.Definition, instance.DefinitionSha256);
        root.Upsert(Fields.Ruleset, instance.Definition.RulesetId);
        root.Upsert(Fields.WorldSeed, instance.WorldSeed);
        root.Upsert(Fields.Lineage, genesisLineageId);
        root.Upsert(Fields.TransitionCount, 0L);
        root.Upsert(Fields.CommitKind, GenesisKind);
        root.Upsert(Fields.CommitSummary, "Created deterministic Alice Player closure Genesis.");
        root.Upsert(Fields.ObjectiveActor, objectiveActor);
        root.Upsert(Fields.Composition, durableComposition);
        root.Upsert(Fields.PlayerState, playerState);

        objectiveActor.Upsert(Fields.ActorId, BoardIds.Alice);
        objectiveActor.Upsert(Fields.ActorKey, actor.Key);
        objectiveActor.Upsert(Fields.Generation, actor.Generation);
        objectiveActor.Upsert(Fields.DecisionSequence, actor.DecisionSequence);
        objectiveActor.Upsert(Fields.KnownFacts, objectiveKnownFacts);
        WriteFacts(objectiveKnownFacts, actor.KnownFacts.Select(
            PlayerClosureObserveFixture.FromBoardFact));

        WriteComposition(durableComposition, composition);
        playerState.Upsert(Fields.PlayerStateSchema, PlayerStateSchemaId);
        playerState.Upsert(
            Fields.SlotBinding,
            composition.SlotBindingSha256(instance.DefinitionSha256, BoardIds.Alice));
        playerState.Upsert(Fields.LastAppliedDecisionSequence, actor.DecisionSequence);
        playerState.Upsert(Fields.MemoryContents, memoryContents);
        playerState.Upsert(Fields.PreviousKnownFacts, previousKnownFacts);
        foreach (ScenarioMemoryShardDefinition shard in actorDefinition.Role.InitialMemoryShards)
        {
            memoryContents.Upsert(shard.Key, shard.InitialContent);
        }

        var result = new DurablePlayerClosureProbeRootV1(
            instance,
            root,
            objectiveActor,
            objectiveKnownFacts,
            durableComposition,
            playerState,
            memoryContents,
            previousKnownFacts,
            parentWorldVersion: null,
            lastTransition: null);
        result.ValidateComplete();
        return result;
    }

    public static DurablePlayerClosureProbeRootV1 Open(
        Revision revision,
        ScenarioInstance instance)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        DurableDict<string> root = revision.GraphRoot as DurableDict<string> ??
            throw new InvalidDataException(
                "StateJournal branch HEAD does not contain a Player closure root.");
        DurableDict<string> objectiveActor =
            root.GetOrThrow<DurableDict<string>>(Fields.ObjectiveActor)!;
        DurableDict<string> composition =
            root.GetOrThrow<DurableDict<string>>(Fields.Composition)!;
        DurableDict<string> playerState =
            root.GetOrThrow<DurableDict<string>>(Fields.PlayerState)!;
        var result = new DurablePlayerClosureProbeRootV1(
            instance,
            root,
            objectiveActor,
            objectiveActor.GetOrThrow<DurableDeque<DurableDict<string>>>(Fields.KnownFacts)!,
            composition,
            playerState,
            playerState.GetOrThrow<DurableDict<string, string>>(Fields.MemoryContents)!,
            playerState.GetOrThrow<DurableDeque<DurableDict<string>>>(Fields.PreviousKnownFacts)!,
            GetOptionalDict(root, Fields.ParentWorldVersion),
            GetOptionalDict(root, Fields.LastTransition));
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

    public void ValidateTransaction(PlayerClosureTransactionV1 transaction)
    {
        RequireExpectedParent(transaction.ParentVersion);
        PlayerClosureCognitiveEffectV1 effect = transaction.PlayerEffect;
        PlayerClosureObjectiveActorSnapshot actor = ObjectiveActor;
        if (!string.Equals(effect.ActorId, ActorId, StringComparison.Ordinal) ||
            !string.Equals(transaction.BasisRequest.ActorId, ActorId, StringComparison.Ordinal) ||
            !string.Equals(
                transaction.BasisRequest.Observation.ActorId,
                ActorId,
                StringComparison.Ordinal) ||
            effect.DecisionId != transaction.BasisRequest.DecisionId)
        {
            throw new InvalidDataException(
                "Player cognitive effect does not match its Actor/DecisionRequest binding.");
        }

        if (effect.ExpectedDecisionSequence != actor.DecisionSequence ||
            effect.NextDecisionSequence != checked(actor.DecisionSequence + 1) ||
            effect.NextDecisionSequence != transaction.ExpectedPostActor.DecisionSequence)
        {
            throw new InvalidDataException(
                "Player cognitive effect decision sequence does not match Objective authority.");
        }

        var expectedDecisionId = new DecisionId(
            $"decision.{ActorId}.{effect.NextDecisionSequence}");
        if (transaction.BasisRequest.DecisionId != expectedDecisionId ||
            transaction.BasisRequest.ModelTimeMs != transaction.Batch.Instant.ModelTime.Ticks ||
            !transaction.BasisRequest.AvailableActions.Any(action =>
                action.ActionKind == ActionKinds.Observe))
        {
            throw new InvalidDataException(
                "Player closure request is not the current production Observe decision point.");
        }

        CandidateKey expectedCause = CandidateKey.FromUtf8(
            $"firstboard/decision/{ActorId}/{effect.NextDecisionSequence}");
        if (transaction.Batch.CauseKey != expectedCause)
        {
            throw new InvalidDataException(
                "Player closure transaction has the wrong production DecisionPoint cause key.");
        }

        string expectedBinding = Composition.SlotBindingSha256(
            _instance.DefinitionSha256,
            ActorId);
        if (!string.Equals(effect.SlotBindingSha256, expectedBinding, StringComparison.Ordinal) ||
            !string.Equals(
                _playerState.GetOrThrow<string>(Fields.SlotBinding),
                expectedBinding,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Player cognitive effect has the wrong slot binding.");
        }

        PlayerClosureFactValue[] requestFacts =
        [
            .. transaction.BasisRequest.Observation.KnownFacts.Select(
                PlayerClosureObserveFixture.FromKnownFact),
        ];
        if (!effect.PreviousKnownFacts.SequenceEqual(requestFacts))
        {
            throw new InvalidDataException(
                "Player previous-known-facts must equal the exact pre-decision observation.");
        }

        var replacementKeys = effect.MemoryReplacements
            .Select(value => value.Key)
            .ToHashSet(StringComparer.Ordinal);
        var expectedKeys = _instance.Definition.Actor(ActorId)
            .Role.InitialMemoryShards
            .Select(shard => shard.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (replacementKeys.Count != effect.MemoryReplacements.Count ||
            !replacementKeys.SetEquals(expectedKeys))
        {
            throw new InvalidDataException(
                "Player cognitive effect must provide exactly one content for every Definition shard.");
        }

        if (transaction.Batch.Facts.Count != 1 ||
            transaction.Batch.Facts[0] is not GameBoardFact
            {
                Value: ActorObservedEvent observed,
            } ||
            !string.Equals(observed.ActorId, ActorId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Player closure V1 accepts exactly one real Alice ActorObserved outcome.");
        }
    }

    public void ApplyPlayerMemory(PlayerClosureCognitiveEffectV1 effect)
    {
        foreach (PlayerClosureMemoryValue replacement in effect.MemoryReplacements)
        {
            if (string.IsNullOrWhiteSpace(replacement.Content))
            {
                throw new InvalidDataException(
                    $"Memory shard '{replacement.Key}' has blank replacement content.");
            }

            _memoryContents.Upsert(replacement.Key, replacement.Content);
        }
    }

    public void ApplyPlayerFrontier(PlayerClosureCognitiveEffectV1 effect)
    {
        WriteFacts(_previousKnownFacts, effect.PreviousKnownFacts);
        _playerState.Upsert(
            Fields.LastAppliedDecisionSequence,
            effect.NextDecisionSequence);
    }

    public void ApplyObservedObjective(PlayerClosureTransactionV1 transaction)
    {
        var observed = (ActorObservedEvent)((GameBoardFact)transaction.Batch.Facts[0]).Value;
        PlayerClosureObjectiveActorSnapshot current = ObjectiveActor;
        var learned = new List<PlayerClosureFactValue>(current.KnownFacts);
        learned.AddRange(observed.LearnedFacts.Select(PlayerClosureObserveFixture.FromBoardFact));
        learned.Add(new PlayerClosureFactValue(
            BoardIds.LastActionOutcome,
            RelatedId: null,
            observed.TargetObjectId is null
                ? $"You successfully observed the current place; the event reported " +
                  $"{observed.LearnedFacts.Count} visible facts."
                : $"You successfully inspected {observed.TargetObjectId}; the event reported " +
                  $"{observed.LearnedFacts.Count} inspection facts."));
        PlayerClosureFactValue[] merged =
        [
            .. learned
                .GroupBy(fact => (fact.Kind, fact.RelatedId))
                .Select(group => group.Last())
                .OrderBy(fact => fact.Kind, StringComparer.Ordinal)
                .ThenBy(fact => fact.RelatedId, StringComparer.Ordinal),
        ];
        var actual = new PlayerClosureObjectiveActorSnapshot(
            checked(current.Generation + 1),
            checked(current.DecisionSequence + 1),
            merged);
        if (actual.Generation != transaction.ExpectedPostActor.Generation ||
            actual.DecisionSequence != transaction.ExpectedPostActor.DecisionSequence ||
            !actual.KnownFacts.SequenceEqual(transaction.ExpectedPostActor.KnownFacts))
        {
            throw new InvalidDataException(
                "Direct durable ActorObserved mutation differs from the production reducer oracle.");
        }

        _objectiveActor.Upsert(Fields.Generation, actual.Generation);
        _objectiveActor.Upsert(Fields.DecisionSequence, actual.DecisionSequence);
        WriteFacts(_objectiveKnownFacts, actual.KnownFacts);
    }

    public void AdvanceFrontier(PlayerClosureTransactionV1 transaction)
    {
        RequireExpectedParent(transaction.ParentVersion);
        if (LastInstant is LogicalInstant previous && transaction.Batch.Instant <= previous)
        {
            throw new InvalidOperationException(
                "Player closure Objective transaction instants must advance strictly.");
        }

        _root.Upsert(
            Fields.TransitionCount,
            checked(Version.TransitionCount + 1));
        DurableDict<string> last = _lastTransition ?? _root.Revision.CreateDict<string>();
        last.Upsert(Fields.ModelTime, transaction.Batch.Instant.ModelTime.Ticks);
        last.Upsert(Fields.CausalOrdinal, transaction.Batch.Instant.CausalOrdinal);
        last.Upsert(
            Fields.CauseKey,
            Convert.ToBase64String(transaction.Batch.CauseKey.ToByteArray()));
        if (_lastTransition is null)
        {
            _root.Upsert(Fields.LastTransition, last);
            _lastTransition = last;
        }

        _root.Upsert(Fields.CommitKind, ObjectiveTransitionKind);
        _root.Upsert(
            Fields.CommitSummary,
            "Alice observed the current place; Objective and Player closure advanced together.");
    }

    public void ApplyChildLineageBoundary(long childLineageId)
    {
        WorldVersion source = Version;
        if (source.LineageId == childLineageId)
        {
            throw new ArgumentException(
                "Player closure child lineage must be fresh.",
                nameof(childLineageId));
        }

        DurableDict<string> parent = _root.Revision.CreateDict<string>();
        WriteWorldVersion(parent, source);
        _root.Upsert(Fields.ParentWorldVersion, parent);
        _parentWorldVersion = parent;
        _root.Upsert(Fields.Lineage, childLineageId);
        _root.Upsert(Fields.CommitKind, LineageStartKind);
        _root.Upsert(
            Fields.CommitSummary,
            $"Started Player closure lineage {childLineageId} from " +
            $"{source.LineageId}/{source.TransitionCount}.");
    }

    public void ValidateComplete(bool injectFailure = false)
    {
        ValidateBindings();
        PlayerClosureObjectiveActorSnapshot actor = ObjectiveActor;
        long playerSequence = _playerState.GetOrThrow<long>(
            Fields.LastAppliedDecisionSequence);
        if (playerSequence != actor.DecisionSequence)
        {
            throw new InvalidDataException(
                "Player last-applied sequence must equal Objective actor decision sequence.");
        }

        WorldVersion version = Version;
        if ((LastInstant is null) != (LastCause is null))
        {
            throw new InvalidDataException(
                "Player closure frontier must persist last instant and cause together.");
        }

        if (version.TransitionCount == 0 && LastInstant is not null)
        {
            throw new InvalidDataException(
                "Zero-transition Player closure frontier cannot carry a last transition.");
        }

        if (version.TransitionCount > 0 && LastInstant is null)
        {
            throw new InvalidDataException(
                "Nonzero Player closure frontier requires a last transition.");
        }

        WorldVersion? parent = ParentWorldVersion;
        if (parent is WorldVersion parentVersion &&
            (parentVersion.LineageId == version.LineageId ||
             parentVersion.TransitionCount > version.TransitionCount))
        {
            throw new InvalidDataException(
                "Player closure child provenance names an invalid parent frontier.");
        }

        string commitKind = _root.GetOrThrow<string>(Fields.CommitKind)!;
        if (commitKind is not GenesisKind and not LineageStartKind and
            not ObjectiveTransitionKind)
        {
            throw new InvalidDataException(
                $"Unsupported Player closure commit kind '{commitKind}'.");
        }

        if (commitKind == GenesisKind && parent is not null)
        {
            throw new InvalidDataException("Player closure Genesis cannot have parent lineage.");
        }

        if (commitKind == LineageStartKind && parent is null)
        {
            throw new InvalidDataException(
                "Player closure lineage-start requires ParentWorldVersion.");
        }

        if (commitKind == ObjectiveTransitionKind && version.TransitionCount == 0)
        {
            throw new InvalidDataException(
                "Player closure Objective commit must advance transition count.");
        }

        if (injectFailure)
        {
            throw new InvalidOperationException(
                "Injected complete Player/Objective closure validation failure.");
        }
    }

    public PlayerClosureSemanticSnapshot Snapshot() => new(
        SchemaId,
        _instance.DefinitionSha256,
        _instance.Definition.RulesetId,
        _instance.WorldSeed,
        Version,
        ParentWorldVersion,
        LastInstant,
        LastCause,
        _root.GetOrThrow<string>(Fields.CommitKind)!,
        ReadOptionalString(_root, Fields.CommitSummary),
        ActorId,
        ObjectiveActor,
        Composition,
        _playerState.GetOrThrow<string>(Fields.SlotBinding)!,
        _playerState.GetOrThrow<long>(Fields.LastAppliedDecisionSequence),
        MemoryContents(),
        PreviousKnownFacts());

    public IReadOnlyList<PlayerClosureMemoryValue> MemoryContents()
    {
        ScenarioActorDefinition actor = _instance.Definition.Actor(ActorId);
        return
        [
            .. actor.Role.InitialMemoryShards.Select(shard => new PlayerClosureMemoryValue(
                shard.Key,
                ReadRequiredString(_memoryContents, shard.Key))),
        ];
    }

    public IReadOnlyList<PlayerClosureFactValue> PreviousKnownFacts() =>
        ReadFacts(_previousKnownFacts);

    public void ApplyMalformedForTest(PlayerClosureMalformedState malformed)
    {
        switch (malformed)
        {
            case PlayerClosureMalformedState.SlotBinding:
                _playerState.Upsert(Fields.SlotBinding, new string('0', 64));
                break;
            case PlayerClosureMalformedState.ExtraMemoryShard:
                _memoryContents.Upsert("unexpected", "must be rejected");
                break;
            case PlayerClosureMalformedState.EmptyRelatedId:
                DurableDict<string> firstFact = _objectiveKnownFacts.GetAtOrThrow(0) ??
                    throw new InvalidOperationException(
                        "Empty-related-id corruption requires a committed Objective fact.");
                firstFact.Upsert(Fields.RelatedId, string.Empty);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(malformed));
        }
    }

    private void ValidateBindings()
    {
        var expectedRootKeys = new List<string>
        {
            Fields.Schema,
            Fields.Definition,
            Fields.Ruleset,
            Fields.WorldSeed,
            Fields.Lineage,
            Fields.TransitionCount,
            Fields.CommitKind,
            Fields.ObjectiveActor,
            Fields.Composition,
            Fields.PlayerState,
        };
        if (ReadOptionalString(_root, Fields.CommitSummary) is not null)
        {
            expectedRootKeys.Add(Fields.CommitSummary);
        }

        if (_parentWorldVersion is not null)
        {
            expectedRootKeys.Add(Fields.ParentWorldVersion);
        }

        if (_lastTransition is not null)
        {
            expectedRootKeys.Add(Fields.LastTransition);
        }

        RequireExactKeys(_root.Keys, [.. expectedRootKeys]);
        RequireExactKeys(
            _objectiveActor.Keys,
            Fields.ActorId,
            Fields.ActorKey,
            Fields.Generation,
            Fields.DecisionSequence,
            Fields.KnownFacts);
        RequireExactKeys(
            _playerState.Keys,
            Fields.PlayerStateSchema,
            Fields.SlotBinding,
            Fields.LastAppliedDecisionSequence,
            Fields.MemoryContents,
            Fields.PreviousKnownFacts);
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

        if (!string.Equals(
                _root.GetOrThrow<string>(Fields.Schema),
                SchemaId,
                StringComparison.Ordinal) ||
            !string.Equals(
                _root.GetOrThrow<string>(Fields.Definition),
                _instance.DefinitionSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                _root.GetOrThrow<string>(Fields.Ruleset),
                _instance.Definition.RulesetId,
                StringComparison.Ordinal) ||
            _root.GetOrThrow<ulong>(Fields.WorldSeed) != _instance.WorldSeed)
        {
            throw new InvalidDataException(
                "Player closure root does not match the supplied Scenario authority.");
        }

        if (!string.Equals(ActorId, BoardIds.Alice, StringComparison.Ordinal) ||
            !string.Equals(
                _objectiveActor.GetOrThrow<string>(Fields.ActorKey),
                BoardIds.Alice,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Player closure root is not bound to Alice.");
        }

        _ = ReadFacts(_objectiveKnownFacts);
        _ = ReadFacts(_previousKnownFacts);
        PlayerClosureCompositionV1 composition = Composition;
        composition.ValidateForPersistence();
        if (!string.Equals(composition.PlayerCompositionId, PlayerClosureCompositionV1.CurrentId,
                StringComparison.Ordinal) ||
            !string.Equals(composition.DriverKind, "llm", StringComparison.Ordinal) ||
            !string.Equals(composition.MemoryMaintenanceMode, "blocking", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Player closure composition is not the supported closed Blocking LLM variant.");
        }

        string expectedBinding = composition.SlotBindingSha256(
            _instance.DefinitionSha256,
            ActorId);
        if (!string.Equals(
                _playerState.GetOrThrow<string>(Fields.PlayerStateSchema),
                PlayerStateSchemaId,
                StringComparison.Ordinal) ||
            !string.Equals(
                _playerState.GetOrThrow<string>(Fields.SlotBinding),
                expectedBinding,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Player state schema or slot binding does not match its closed composition.");
        }

        var expectedMemoryKeys = _instance.Definition.Actor(ActorId)
            .Role.InitialMemoryShards
            .Select(shard => shard.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!_memoryContents.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedMemoryKeys))
        {
            throw new InvalidDataException(
                "Player Memory contents must match the Definition-owned shard key set exactly.");
        }

        foreach (string key in _memoryContents.Keys)
        {
            if (string.IsNullOrWhiteSpace(ReadRequiredString(_memoryContents, key)))
            {
                throw new InvalidDataException($"Player Memory shard '{key}' is blank.");
            }
        }
    }

    private PlayerClosureCompositionV1 ReadComposition()
    {
        RequireExactKeys(
            _composition.Keys,
            Fields.PlayerCompositionId,
            Fields.DriverKind,
            Fields.DecisionBackendKind,
            Fields.DecisionModel,
            Fields.DecisionEffort,
            Fields.DecisionEndpoint,
            Fields.MemoryBackendKind,
            Fields.MemoryModel,
            Fields.MemoryEffort,
            Fields.MemoryEndpoint,
            Fields.MaintenanceMode,
            Fields.Wrappers);
        return new PlayerClosureCompositionV1(
            _composition.GetOrThrow<string>(Fields.PlayerCompositionId)!,
            _composition.GetOrThrow<string>(Fields.DriverKind)!,
            _composition.GetOrThrow<string>(Fields.DecisionBackendKind)!,
            _composition.GetOrThrow<string>(Fields.DecisionModel)!,
            _composition.GetOrThrow<string>(Fields.DecisionEffort)!,
            _composition.GetOrThrow<string>(Fields.DecisionEndpoint)!,
            _composition.GetOrThrow<string>(Fields.MemoryBackendKind)!,
            _composition.GetOrThrow<string>(Fields.MemoryModel)!,
            _composition.GetOrThrow<string>(Fields.MemoryEffort)!,
            _composition.GetOrThrow<string>(Fields.MemoryEndpoint)!,
            _composition.GetOrThrow<string>(Fields.MaintenanceMode)!,
            _composition.GetOrThrow<string>(Fields.Wrappers)!);
    }

    private static void WriteComposition(
        DurableDict<string> destination,
        PlayerClosureCompositionV1 composition)
    {
        destination.Upsert(Fields.PlayerCompositionId, composition.PlayerCompositionId);
        destination.Upsert(Fields.DriverKind, composition.DriverKind);
        destination.Upsert(Fields.DecisionBackendKind, composition.DecisionBackendKind);
        destination.Upsert(Fields.DecisionModel, composition.DecisionModel);
        destination.Upsert(Fields.DecisionEffort, composition.DecisionEffort);
        destination.Upsert(Fields.DecisionEndpoint, composition.DecisionEndpointIdentity);
        destination.Upsert(Fields.MemoryBackendKind, composition.MemoryBackendKind);
        destination.Upsert(Fields.MemoryModel, composition.MemoryModel);
        destination.Upsert(Fields.MemoryEffort, composition.MemoryEffort);
        destination.Upsert(Fields.MemoryEndpoint, composition.MemoryEndpointIdentity);
        destination.Upsert(Fields.MaintenanceMode, composition.MemoryMaintenanceMode);
        destination.Upsert(Fields.Wrappers, composition.Wrappers);
    }

    private static IReadOnlyList<PlayerClosureFactValue> ReadFacts(
        DurableDeque<DurableDict<string>> source)
    {
        var result = new List<PlayerClosureFactValue>(source.Count);
        for (int index = 0; index < source.Count; index++)
        {
            DurableDict<string> item = source.GetAtOrThrow(index) ??
                throw new InvalidDataException("Player closure fact item cannot be null.");
            string? relatedId = ReadOptionalString(item, Fields.RelatedId);
            RequireExactKeys(
                item.Keys,
                relatedId is null
                    ? [Fields.FactKind, Fields.Text]
                    : [Fields.FactKind, Fields.RelatedId, Fields.Text]);
            string kind = item.GetOrThrow<string>(Fields.FactKind)!;
            string text = item.GetOrThrow<string>(Fields.Text)!;
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(text) ||
                relatedId is not null && relatedId.Length == 0)
            {
                throw new InvalidDataException(
                    "Player closure KnownFact contains blank kind/text or empty relatedId.");
            }

            result.Add(new PlayerClosureFactValue(kind, relatedId, text));
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
                throw new InvalidDataException(
                    "Player closure KnownFact cannot contain blank values.");
            }

            DurableDict<string> item = destination.Revision.CreateDict<string>();
            item.Upsert(Fields.FactKind, fact.Kind);
            if (fact.RelatedId is not null)
            {
                item.Upsert(Fields.RelatedId, fact.RelatedId);
            }

            item.Upsert(Fields.Text, fact.Text);
            destination.PushBack(item);
        }
    }

    private static DurableDict<string>? GetOptionalDict(
        DurableDict<string> source,
        string key)
    {
        GetIssue issue = source.Get(key, out DurableDict<string>? value);
        return issue switch
        {
            GetIssue.None => value ?? throw new InvalidDataException(
                $"Durable Player closure field '{key}' cannot be null."),
            GetIssue.NotFound => null,
            _ => throw new InvalidDataException(
                $"Durable Player closure field '{key}' could not be read: {issue}."),
        };
    }

    private static string? ReadOptionalString(DurableDict<string> source, string key)
    {
        GetIssue issue = source.Get(key, out string? value);
        return issue switch
        {
            GetIssue.None => value ?? throw new InvalidDataException(
                $"Durable Player closure string '{key}' cannot be null."),
            GetIssue.NotFound => null,
            _ => throw new InvalidDataException(
                $"Durable Player closure string '{key}' could not be read: {issue}."),
        };
    }

    private static string ReadRequiredString(
        DurableDict<string, string> source,
        string key)
    {
        GetIssue issue = source.Get(key, out string? value);
        return issue switch
        {
            GetIssue.None => value ?? throw new InvalidDataException(
                $"Durable Player closure string '{key}' cannot be null."),
            GetIssue.NotFound => throw new InvalidDataException(
                $"Durable Player closure string '{key}' is missing."),
            _ => throw new InvalidDataException(
                $"Durable Player closure string '{key}' could not be read: {issue}."),
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
        string[] actualKeys = [.. actual.Order(StringComparer.Ordinal)];
        string[] expectedKeys = [.. expected.Order(StringComparer.Ordinal)];
        if (!actualKeys.SequenceEqual(expectedKeys, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Durable Player closure keys [{string.Join(", ", actualKeys)}] do not " +
                $"match expected [{string.Join(", ", expectedKeys)}].");
        }
    }
}

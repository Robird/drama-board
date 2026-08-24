using Atelia;
using Atelia.StateJournal;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

internal sealed record DeadlineTransactionProbeV1(
    WorldVersion ParentVersion,
    JournalBatch<FirstBoardFact> Batch);

internal enum DeadlineProbeFault
{
    None,
    AfterFirstFact,
    AtBatchEndValidation,
}

internal enum DeadlineProbeOutcome
{
    NotCommitted,
    Committed,
}

internal enum DeadlineProbeOperationKind
{
    ObjectiveTransition,
    LineageStart,
}

internal static class DeadlineProbeCommitKinds
{
    public const string Genesis = "genesis";
    public const string LineageStart = "lineage-start";
    public const string ObjectiveTransition = "objective-transition";
    public const string MetadataOnly = "metadata-only";
}

internal sealed record DeadlineProbeCommitReceipt(
    CommitAddress Parent,
    CommitAddress Head);

internal sealed record DeadlineProbeResolution(
    DeadlineProbeOutcome Outcome,
    DeadlineProbeSemanticSnapshot Snapshot);

internal sealed record DeadlineProbeSemanticSnapshot(
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
    ModelTime Now,
    bool CellarSealed,
    PassageEntryAccess CellarGateAccess);

internal sealed record DeadlineProbeHistoryRow(
    CommitAddress Address,
    CommitAddress? ParentAddress,
    DeadlineProbeSemanticSnapshot Snapshot);

internal readonly record struct DeadlineProbePassageOverride(
    string PassageId,
    byte AccessMask);

internal sealed record DeadlineProbeAuthoritySnapshot(
    string SchemaId,
    string DefinitionSha256,
    string RulesetId,
    ulong WorldSeed,
    WorldVersion Version,
    WorldVersion? ParentWorldVersion,
    LogicalInstant? LastInstant,
    CandidateKey? LastCause,
    string CommitKind,
    ModelTime Now,
    bool CellarSealed,
    IReadOnlyList<DeadlineProbePassageOverride> PassageOverrides)
{
    public bool Matches(DeadlineProbeAuthoritySnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal) &&
            string.Equals(
                DefinitionSha256,
                other.DefinitionSha256,
                StringComparison.Ordinal) &&
            string.Equals(RulesetId, other.RulesetId, StringComparison.Ordinal) &&
            WorldSeed == other.WorldSeed &&
            Version == other.Version &&
            ParentWorldVersion == other.ParentWorldVersion &&
            LastInstant == other.LastInstant &&
            LastCause == other.LastCause &&
            string.Equals(CommitKind, other.CommitKind, StringComparison.Ordinal) &&
            Now == other.Now &&
            CellarSealed == other.CellarSealed &&
            PassageOverrides.SequenceEqual(other.PassageOverrides);
    }
}

internal sealed class DeadlineProbeCommitFailedException : InvalidOperationException
{
    private const string StructuredCommitErrorCode = "SJ.Repository.CommitFailed";

    public DeadlineProbeCommitFailedException(
        AteliaError error,
        string branchName,
        CommitAddress expectedParent,
        DeadlineProbeAuthoritySnapshot parentAuthority,
        DeadlineProbeAuthoritySnapshot childAuthority,
        DeadlineProbeOperationKind operationKind)
        : base($"StateJournal commit outcome is unknown: {error}")
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentNullException.ThrowIfNull(parentAuthority);
        ArgumentNullException.ThrowIfNull(childAuthority);
        BranchName = branchName;
        ExpectedParent = expectedParent;
        ParentAuthority = parentAuthority;
        ChildAuthority = childAuthority;
        OperationKind = operationKind;

        if (!string.Equals(
                error.ErrorCode,
                StructuredCommitErrorCode,
                StringComparison.Ordinal))
        {
            return;
        }

        IReadOnlyDictionary<string, string> details = error.Details
            ?? throw new InvalidDataException(
                $"Structured StateJournal commit error '{StructuredCommitErrorCode}' has no Details.");
        CommitAddress reportedExpected = ParseRequiredAddress(
            details,
            "ExpectedHeadAddress");
        if (reportedExpected != expectedParent)
        {
            throw new InvalidDataException(
                $"Structured StateJournal commit error reported expected HEAD " +
                $"'{reportedExpected}', but caller captured '{expectedParent}'.");
        }

        CandidateAddress = ParseRequiredAddress(details, "CandidateAddress");
        FailurePhase = ReadRequiredDetail(details, "FailurePhase");
        PublicationState = ReadRequiredDetail(details, "PublicationState");
    }

    private DeadlineProbeCommitFailedException(
        string message,
        string branchName,
        CommitAddress expectedParent,
        DeadlineProbeAuthoritySnapshot parentAuthority,
        DeadlineProbeAuthoritySnapshot childAuthority,
        DeadlineProbeOperationKind operationKind,
        string failurePhase,
        string publicationState)
        : base(message)
    {
        BranchName = branchName;
        ExpectedParent = expectedParent;
        ParentAuthority = parentAuthority;
        ChildAuthority = childAuthority;
        OperationKind = operationKind;
        FailurePhase = failurePhase;
        PublicationState = publicationState;
    }

    public string BranchName { get; }

    public CommitAddress ExpectedParent { get; }

    public DeadlineProbeAuthoritySnapshot ParentAuthority { get; }

    public DeadlineProbeAuthoritySnapshot ChildAuthority { get; }

    public DeadlineProbeOperationKind OperationKind { get; }

    public CommitAddress? CandidateAddress { get; }

    public string? FailurePhase { get; }

    public string? PublicationState { get; }

    public static DeadlineProbeCommitFailedException CreateKnownNotPublished(
        string branchName,
        CommitAddress expectedParent,
        DeadlineProbeAuthoritySnapshot parentAuthority,
        DeadlineProbeAuthoritySnapshot childAuthority,
        DeadlineProbeOperationKind operationKind) => new(
            "Injected failure before Repository.Commit; no candidate was written or published.",
            branchName,
            expectedParent,
            parentAuthority,
            childAuthority,
            operationKind,
            failurePhase: "BeforeRepositoryCommit",
            publicationState: "NotPublished");

    private static CommitAddress ParseRequiredAddress(
        IReadOnlyDictionary<string, string> details,
        string key)
    {
        string text = ReadRequiredDetail(details, key);
        CommitAddress? parsed = CommitAddress.TryParse(text);
        return parsed ?? throw new InvalidDataException(
            $"Structured StateJournal commit detail '{key}' is not a valid address: '{text}'.");
    }

    private static string ReadRequiredDetail(
        IReadOnlyDictionary<string, string> details,
        string key)
    {
        if (!details.TryGetValue(key, out string? value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Structured StateJournal commit error is missing detail '{key}'.");
        }

        return value;
    }
}

/// <summary>
/// Test-only StateJournal-native session for one real FirstBoard deadline transition.
/// The durable object graph and its direct frontier are authoritative. The exact
/// JournalBatch exists only as the in-memory transaction contract.
/// </summary>
internal sealed class DeadlineProbeSession : IDisposable
{
    internal const string MainBranch = "main";

    private readonly string _branchName;
    private readonly ScenarioInstance _instance;
    private readonly Repository _repository;
    private readonly DurableDeadlineProbeRootV2 _root;
    private bool _disposed;
    private bool _poisoned;
    private int _viewEpoch;

    private DeadlineProbeSession(
        string branchName,
        ScenarioInstance instance,
        Repository repository,
        DurableDeadlineProbeRootV2 root)
    {
        _branchName = branchName;
        _instance = instance;
        _repository = repository;
        _root = root;
        _root.ValidateComplete();
    }

    public DeadlineProbeView Head
    {
        get
        {
            RequireActive();
            return new DeadlineProbeView(this, _viewEpoch);
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

    public static DeadlineProbeSession Create(
        string repositoryPath,
        ScenarioInstance instance,
        long genesisLineageId)
    {
        ValidateArguments(repositoryPath, instance);

        Repository repository = Require(
            Repository.Create(repositoryPath),
            "create StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(MainBranch),
                "create main branch");
            DurableDeadlineProbeRootV2 root = DurableDeadlineProbeRootV2.Create(
                revision,
                instance,
                genesisLineageId);
            root.ValidateComplete();
            Require(
                repository.Commit(root.GraphRoot),
                "commit deterministic Genesis root");
            return new DeadlineProbeSession(MainBranch, instance, repository, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a physical branch and commits its business-lineage boundary before
    /// exposing the Session. The caller/allocator owns repository-wide lineage
    /// uniqueness; this narrow store verifies only that the child differs from its source.
    /// </summary>
    public static DeadlineProbeSession CreateForkBranch(
        string repositoryPath,
        ScenarioInstance instance,
        string branchName,
        CommitAddress fromCommit,
        long callerAllocatedChildLineageId,
        bool injectLineagePreCommitFailureForTest = false,
        bool injectLineageReflogFailureForTest = false)
    {
        ValidateArguments(repositoryPath, instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(branchName, fromCommit),
                $"create branch '{branchName}' from commit '{fromCommit}'");
            DurableDeadlineProbeRootV2 root = DurableDeadlineProbeRootV2.Open(
                revision,
                instance);
            var session = new DeadlineProbeSession(
                branchName,
                instance,
                repository,
                root);
            if (injectLineageReflogFailureForTest)
            {
                InstallReflogDirectoryFault(repositoryPath, branchName);
            }

            session.CommitLineageStart(
                callerAllocatedChildLineageId,
                injectLineagePreCommitFailureForTest);
            return session;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an initialized branch. If <see cref="CreateForkBranch"/> failed, its
    /// failure must be classified and, when NotCommitted, completed through
    /// <see cref="ResumeForkBranch"/> before this raw branch state is exposed.
    /// </summary>
    public static DeadlineProbeSession Open(
        string repositoryPath,
        ScenarioInstance instance,
        string branchName = MainBranch)
    {
        ValidateArguments(repositoryPath, instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CheckoutBranch(branchName),
                $"checkout branch '{branchName}'");
            DurableDeadlineProbeRootV2 root = DurableDeadlineProbeRootV2.Open(
                revision,
                instance);
            return new DeadlineProbeSession(branchName, instance, repository, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static DeadlineProbeSession ResumeForkBranch(
        string repositoryPath,
        ScenarioInstance instance,
        DeadlineProbeCommitFailedException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.OperationKind != DeadlineProbeOperationKind.LineageStart)
        {
            throw new ArgumentException(
                "Only a failed lineage-start operation can resume fork initialization.",
                nameof(failure));
        }

        if (!string.Equals(
                failure.PublicationState,
                "NotPublished",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Fork initialization can resume only from a receipt whose candidate is " +
                "known not to have been published.");
        }

        DeadlineProbeSession session = Open(
            repositoryPath,
            instance,
            failure.BranchName);
        try
        {
            if (session.HeadAddress != failure.ExpectedParent ||
                !session._root.AuthoritySnapshot().Matches(failure.ParentAuthority))
            {
                throw new InvalidDataException(
                    "Fork branch no longer points at the captured parent authority.");
            }

            session.AdvanceViewEpoch();
            session._root.ApplyChildLineageBoundary(
                failure.ChildAuthority.Version.LineageId);
            session._root.ValidateComplete();
            DeadlineProbeAuthoritySnapshot resumedChild =
                session._root.AuthoritySnapshot();
            if (!resumedChild.Matches(failure.ChildAuthority))
            {
                throw new InvalidDataException(
                    "Reapplied lineage boundary does not match the captured child authority.");
            }

            _ = session.CommitGraph(
                failure.ExpectedParent,
                failure.ParentAuthority,
                failure.ChildAuthority,
                DeadlineProbeOperationKind.LineageStart);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void CommitLineageStart(
        long childLineageId,
        bool injectPreCommitFailureForTest)
    {
        RequireActive();
        CommitAddress expectedParent = _root.HeadAddress;
        DeadlineProbeAuthoritySnapshot parentAuthority = _root.AuthoritySnapshot();
        AdvanceViewEpoch();

        try
        {
            _root.ApplyChildLineageBoundary(childLineageId);
            _root.ValidateComplete();
            DeadlineProbeAuthoritySnapshot childAuthority = _root.AuthoritySnapshot();
            if (injectPreCommitFailureForTest)
            {
                throw DeadlineProbeCommitFailedException.CreateKnownNotPublished(
                    _branchName,
                    expectedParent,
                    parentAuthority,
                    childAuthority,
                    DeadlineProbeOperationKind.LineageStart);
            }

            _ = CommitGraph(
                expectedParent,
                parentAuthority,
                childAuthority,
                DeadlineProbeOperationKind.LineageStart);
        }
        catch (DeadlineProbeCommitFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Poison();
            throw new InvalidOperationException(
                "The lineage-start commit failed after private working mutation; " +
                "the Session is poisoned and must be reopened from HEAD.",
                exception);
        }
    }

    public DeadlineProbeCommitReceipt Commit(
        DeadlineTransactionProbeV1 transaction,
        DeadlineProbeFault fault = DeadlineProbeFault.None)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        RequireActive();

        CommitAddress expectedParent = _root.HeadAddress;
        _root.RequireExpectedParent(transaction.ParentVersion);
        DeadlineProbeAuthoritySnapshot parentAuthority = _root.AuthoritySnapshot();
        AdvanceViewEpoch();

        try
        {
            for (int index = 0; index < transaction.Batch.Facts.Count; index++)
            {
                _root.Apply(
                    transaction.Batch.Instant,
                    transaction.Batch.Facts[index]);
                if (fault == DeadlineProbeFault.AfterFirstFact && index == 0)
                {
                    throw new InvalidOperationException(
                        "Injected failure after the first fact mutated the private working graph.");
                }
            }

            _root.ValidateBatchEnd(
                injectFailure: fault == DeadlineProbeFault.AtBatchEndValidation);
            _root.AdvanceFrontier(transaction);
            _root.ValidateComplete();
            DeadlineProbeAuthoritySnapshot childAuthority = _root.AuthoritySnapshot();

            CommitAddress head = CommitGraph(
                expectedParent,
                parentAuthority,
                childAuthority,
                DeadlineProbeOperationKind.ObjectiveTransition);
            return new DeadlineProbeCommitReceipt(expectedParent, head);
        }
        catch (DeadlineProbeCommitFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Poison();
            throw new InvalidOperationException(
                "The deadline transaction failed after private working mutation; " +
                "the Session is poisoned and must be reopened from HEAD.",
                exception);
        }
    }

    public DeadlineProbeSemanticSnapshot Snapshot()
    {
        RequireActive();
        return _root.Snapshot();
    }

    public static DeadlineProbeResolution ResolveUnknownOutcome(
        string repositoryPath,
        ScenarioInstance instance,
        DeadlineProbeCommitFailedException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        using DeadlineProbeSession reopened = Open(
            repositoryPath,
            instance,
            failure.BranchName);
        DeadlineProbeSemanticSnapshot snapshot = reopened.Snapshot();
        DeadlineProbeAuthoritySnapshot actualAuthority =
            reopened._root.AuthoritySnapshot();
        if (reopened.HeadAddress == failure.ExpectedParent)
        {
            if (actualAuthority.Matches(failure.ParentAuthority))
            {
                return new DeadlineProbeResolution(
                    DeadlineProbeOutcome.NotCommitted,
                    snapshot);
            }

            throw new InvalidDataException(
                "Reopened StateJournal HEAD has the expected parent address but not its " +
                "captured authority state.");
        }

        if (failure.CandidateAddress is CommitAddress candidate &&
            reopened.HeadAddress != candidate)
        {
            throw new InvalidDataException(
                $"Reopened StateJournal HEAD '{reopened.HeadAddress}' is neither the " +
                $"expected parent nor reported candidate '{candidate}'.");
        }

        bool exactChild = reopened._root.HeadParentAddress == failure.ExpectedParent &&
            actualAuthority.Matches(failure.ChildAuthority);
        if (exactChild)
        {
            return new DeadlineProbeResolution(DeadlineProbeOutcome.Committed, snapshot);
        }

        throw new InvalidDataException(
            "Reopened StateJournal HEAD is neither the expected parent nor the exact proposed child.");
    }

    public static IReadOnlyList<DeadlineProbeHistoryRow> ReadEffectiveHistory(
        string repositoryPath,
        ScenarioInstance instance,
        string branchName)
    {
        ValidateArguments(repositoryPath, instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        using Repository repository = Require(
            Repository.Open(repositoryPath),
            "open StateJournal repository for history inspection");
        BranchHistoryScanResult history =
            RepositoryHistoryReader.EnumerateBranchEffectiveCommitAddresses(
                repository,
                branchName);
        if (history.Warnings.Count != 0)
        {
            throw new InvalidDataException(
                $"Effective history for branch '{branchName}' is incomplete: " +
                string.Join("; ", history.Warnings));
        }

        var rows = new List<DeadlineProbeHistoryRow>(history.Addresses.Count);
        foreach (BranchHistoryAddress entry in history.Addresses.Reverse())
        {
            DurableObject graphRoot = Require(
                repository.LoadRootAtCommit(entry.Address),
                $"load historical root '{entry.Address}'");
            DurableDeadlineProbeRootV2 root = DurableDeadlineProbeRootV2.Open(
                graphRoot.Revision,
                instance);
            rows.Add(new DeadlineProbeHistoryRow(
                entry.Address,
                root.HeadParentAddress,
                root.Snapshot()));
        }

        return rows;
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
                "The committed view expired when a transaction attempt began.");
        }
    }

    internal ScenarioInstance Instance(int epoch)
    {
        RequireView(epoch);
        return _instance;
    }

    internal WorldVersion Version(int epoch)
    {
        RequireView(epoch);
        return _root.Version;
    }

    internal ModelTime Now(int epoch)
    {
        RequireView(epoch);
        return _root.Now;
    }

    internal bool CellarSealed(int epoch)
    {
        RequireView(epoch);
        return _root.CellarSealed;
    }

    internal PassageEntryAccess GetPassageEntryAccess(int epoch, PassageId passageId)
    {
        RequireView(epoch);
        return _root.GetPassageEntryAccess(passageId);
    }

    private CommitAddress CommitGraph(
        CommitAddress expectedParent,
        DeadlineProbeAuthoritySnapshot parentAuthority,
        DeadlineProbeAuthoritySnapshot childAuthority,
        DeadlineProbeOperationKind operationKind)
    {
        AteliaResult<CommitAddress> commit = _repository.Commit(_root.GraphRoot);
        if (commit.IsFailure)
        {
            AteliaError error = commit.Error!;
            Poison();
            throw new DeadlineProbeCommitFailedException(
                error,
                _branchName,
                expectedParent,
                parentAuthority,
                childAuthority,
                operationKind);
        }

        return commit.Value;
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
                $"The StateJournal deadline probe Session for branch '{_branchName}' is " +
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
        ScenarioInstance instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Definition.Actors.Any(actor =>
                string.Equals(actor.InitialPlaceId, BoardIds.Cellar, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The narrow deadline probe cannot omit Cellar witness KnownFacts.",
                nameof(instance));
        }
    }

    private static void InstallReflogDirectoryFault(
        string repositoryPath,
        string branchName)
    {
        string relativeBranchPath = branchName.Replace(
            '/',
            Path.DirectorySeparatorChar);
        string reflogPath = Path.Combine(
            repositoryPath,
            "refs",
            "branches",
            relativeBranchPath + ".reflog.jsonl");
        File.Delete(reflogPath);
        Directory.CreateDirectory(reflogPath);
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

internal sealed class DeadlineProbeView
{
    private readonly DeadlineProbeSession _session;
    private readonly int _epoch;

    internal DeadlineProbeView(DeadlineProbeSession session, int epoch)
    {
        _session = session;
        _epoch = epoch;
    }

    public ScenarioInstance Instance => _session.Instance(_epoch);

    public WorldVersion Version => _session.Version(_epoch);

    public ModelTime Now => _session.Now(_epoch);

    public bool CellarSealed => _session.CellarSealed(_epoch);

    public PassageEntryAccess GetPassageEntryAccess(PassageId passageId) =>
        _session.GetPassageEntryAccess(_epoch, passageId);
}

internal static class DeadlineProbePlanner
{
    public static DeadlineTransactionProbeV1 Plan(DeadlineProbeView world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Version.TransitionCount != 0 ||
            world.Now != ModelTime.Zero ||
            world.CellarSealed)
        {
            throw new InvalidOperationException(
                "The narrow deadline probe plans only the first default-Genesis deadline.");
        }

        var passageId = new PassageId(BoardIds.CellarGatePassage);
        PassageEntryAccess current = world.GetPassageEntryAccess(passageId);
        var instant = new LogicalInstant(
            new ModelTime(world.Instance.Definition.CellarDeadlineMs),
            causalOrdinal: 0);
        var batch = new JournalBatch<FirstBoardFact>(
            instant,
            CandidateKey.FromUtf8("firstboard/deadline/cellar-seal"),
            [
                new GameBoardFact(new CellarSealedEvent()),
                new SpatialBoardFact(new PassageEntryAccessChangedFact(
                    passageId,
                    new PassageEntryAccess(
                        EnterableFromA: false,
                        current.EnterableFromB))),
            ]);
        return new DeadlineTransactionProbeV1(world.Version, batch);
    }
}

internal sealed class DurableDeadlineProbeRootV2
{
    private const string SchemaId = "firstboard.statejournal-deadline-root/2";
    private const string CellarGate = BoardIds.CellarGatePassage;
    private const string GenesisSummary = "Created deterministic Genesis state.";
    private const string DeadlineSummary =
        "Cellar deadline sealed the cellar and updated cellar-gate entry access.";

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
        public const string Game = "game";
        public const string EntryOverrides = "passageEntryOverrides";
        public const string CellarSealed = "cellarSealed";
        public const string Now = "nowMs";
        public const string ModelTime = "modelTimeMs";
        public const string CausalOrdinal = "causalOrdinal";
        public const string CauseKey = "causeKeyBase64";
    }

    private readonly ScenarioInstance _instance;
    private readonly DurableDict<string> _graphRoot;
    private readonly DurableDict<string> _game;
    private readonly DurableDict<string, byte> _entryOverrides;
    private DurableDict<string>? _parentWorldVersion;
    private DurableDict<string>? _lastTransition;

    private DurableDeadlineProbeRootV2(
        ScenarioInstance instance,
        DurableDict<string> graphRoot,
        DurableDict<string> game,
        DurableDict<string, byte> entryOverrides,
        DurableDict<string>? parentWorldVersion,
        DurableDict<string>? lastTransition)
    {
        _instance = instance;
        _graphRoot = graphRoot;
        _game = game;
        _entryOverrides = entryOverrides;
        _parentWorldVersion = parentWorldVersion;
        _lastTransition = lastTransition;
    }

    public DurableObject GraphRoot => _graphRoot;

    public CommitAddress HeadAddress =>
        _graphRoot.Revision.HeadAddress ??
        throw new InvalidOperationException("The deadline probe root has no committed HEAD.");

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

    public bool CellarSealed => _game.GetOrThrow<bool>(Fields.CellarSealed);

    public static DurableDeadlineProbeRootV2 Create(
        Revision revision,
        ScenarioInstance instance,
        long genesisLineageId)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);

        DurableDict<string> root = revision.CreateDict<string>();
        DurableDict<string> game = revision.CreateDict<string>();
        DurableDict<string, byte> entryOverrides = revision.CreateDict<string, byte>();

        root.Upsert(Fields.Schema, SchemaId);
        root.Upsert(Fields.Definition, instance.DefinitionSha256);
        root.Upsert(Fields.Ruleset, instance.Definition.RulesetId);
        root.Upsert(Fields.WorldSeed, instance.WorldSeed);
        root.Upsert(Fields.Lineage, genesisLineageId);
        root.Upsert(Fields.TransitionCount, 0L);
        root.Upsert(Fields.CommitKind, DeadlineProbeCommitKinds.Genesis);
        root.Upsert(Fields.CommitSummary, GenesisSummary);
        root.Upsert(Fields.Game, game);
        root.Upsert(Fields.EntryOverrides, entryOverrides);
        game.Upsert(Fields.CellarSealed, false);
        game.Upsert(Fields.Now, ModelTime.Zero.Ticks);

        return new DurableDeadlineProbeRootV2(
            instance,
            root,
            game,
            entryOverrides,
            parentWorldVersion: null,
            lastTransition: null);
    }

    public static DurableDeadlineProbeRootV2 Open(
        Revision revision,
        ScenarioInstance instance)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        DurableDict<string> root = revision.GraphRoot as DurableDict<string>
            ?? throw new InvalidDataException(
                "StateJournal branch HEAD does not contain a deadline probe root.");
        var result = new DurableDeadlineProbeRootV2(
            instance,
            root,
            root.GetOrThrow<DurableDict<string>>(Fields.Game)!,
            root.GetOrThrow<DurableDict<string, byte>>(Fields.EntryOverrides)!,
            GetOptionalDict(root, Fields.ParentWorldVersion),
            GetOptionalDict(root, Fields.LastTransition));
        result.ValidateComplete();
        return result;
    }

    public PassageEntryAccess GetPassageEntryAccess(PassageId passageId)
    {
        PassageDefinition passage = _instance.Graph.GetPassage(passageId);
        GetIssue issue = _entryOverrides.Get(passageId.Value, out byte mask);
        if (issue == GetIssue.NotFound)
        {
            return passage.InitialEntryAccess;
        }

        if (issue != GetIssue.None || mask > 3)
        {
            throw new InvalidDataException(
                $"Passage entry override '{passageId}' is malformed.");
        }

        return new PassageEntryAccess(
            EnterableFromA: (mask & 1) != 0,
            EnterableFromB: (mask & 2) != 0);
    }

    public void RequireExpectedParent(WorldVersion parent)
    {
        if (Version != parent)
        {
            throw new InvalidOperationException(
                $"Transaction parent '{parent}' does not match durable frontier '{Version}'.");
        }
    }

    internal void ApplyChildLineageBoundary(long childLineageId)
    {
        WorldVersion sourceVersion = Version;
        if (childLineageId == sourceVersion.LineageId)
        {
            throw new ArgumentException(
                "A child lineage must have a fresh lineage identity.",
                nameof(childLineageId));
        }

        DurableDict<string> parent = _graphRoot.Revision.CreateDict<string>();
        WriteWorldVersion(parent, sourceVersion);
        _graphRoot.Upsert(Fields.ParentWorldVersion, parent);
        _parentWorldVersion = parent;
        _graphRoot.Upsert(Fields.Lineage, childLineageId);
        _graphRoot.Upsert(Fields.CommitKind, DeadlineProbeCommitKinds.LineageStart);
        _graphRoot.Upsert(
            Fields.CommitSummary,
            $"Started lineage {childLineageId} from {FormatVersion(sourceVersion)}.");
    }

    public void Apply(LogicalInstant instant, FirstBoardFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        switch (fact)
        {
            case GameBoardFact { Value: CellarSealedEvent }:
                if (CellarSealed)
                {
                    throw new InvalidOperationException("The Cellar is already sealed.");
                }

                _game.Upsert(Fields.CellarSealed, true);
                break;

            case SpatialBoardFact { Value: PassageEntryAccessChangedFact changed }:
                ApplyEntryAccess(changed);
                break;

            default:
                throw new NotSupportedException(
                    $"Deadline probe fact '{fact.GetType().Name}' is not supported.");
        }

        _game.Upsert(Fields.Now, instant.ModelTime.Ticks);
    }

    public void ValidateBatchEnd(bool injectFailure)
    {
        ValidateBindings();
        _ = CellarSealed;
        _ = Now;
        _ = GetPassageEntryAccess(new PassageId(CellarGate));
        if (injectFailure)
        {
            throw new InvalidOperationException(
                "Injected failure from batch-end validation after the complete private graph mutation.");
        }
    }

    public void AdvanceFrontier(DeadlineTransactionProbeV1 transaction)
    {
        RequireExpectedParent(transaction.ParentVersion);
        if (LastInstant is LogicalInstant previous && transaction.Batch.Instant <= previous)
        {
            throw new InvalidOperationException(
                "Deadline transaction instants must advance strictly.");
        }

        long nextTransitionCount = checked(Version.TransitionCount + 1);
        _graphRoot.Upsert(Fields.TransitionCount, nextTransitionCount);

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
            DeadlineProbeCommitKinds.ObjectiveTransition);
        _graphRoot.Upsert(Fields.CommitSummary, DeadlineSummary);
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
                "Child-lineage provenance must name a different lineage at an equal or earlier frontier.");
        }

        if ((lastInstant is null) != (lastCause is null))
        {
            throw new InvalidDataException(
                "The direct frontier must persist last LogicalInstant and cause together.");
        }

        _ = CellarSealed;
        foreach (string passageId in _entryOverrides.Keys)
        {
            _ = GetPassageEntryAccess(new PassageId(passageId));
        }

        if (version.TransitionCount == 0 &&
            (lastInstant is not null || Now != ModelTime.Zero))
        {
            throw new InvalidDataException(
                "A zero-transition direct frontier cannot carry a last transition.");
        }

        if (version.TransitionCount > 0 &&
            (lastInstant is null || Now != lastInstant.Value.ModelTime))
        {
            throw new InvalidDataException(
                "The direct frontier's Now and last LogicalInstant are inconsistent.");
        }

        string commitKind = CommitKind;
        if (commitKind is not DeadlineProbeCommitKinds.Genesis and
            not DeadlineProbeCommitKinds.LineageStart and
            not DeadlineProbeCommitKinds.ObjectiveTransition and
            not DeadlineProbeCommitKinds.MetadataOnly)
        {
            throw new InvalidDataException(
                $"Deadline probe commit kind '{commitKind}' is not supported.");
        }

        if (commitKind == DeadlineProbeCommitKinds.Genesis && parent is not null)
        {
            throw new InvalidDataException(
                "A Genesis commit cannot carry child-lineage provenance.");
        }

        if (commitKind == DeadlineProbeCommitKinds.LineageStart && parent is null)
        {
            throw new InvalidDataException(
                "A lineage-start commit requires exact ParentWorldVersion provenance.");
        }

        if (commitKind == DeadlineProbeCommitKinds.ObjectiveTransition &&
            version.TransitionCount == 0)
        {
            throw new InvalidDataException(
                "An Objective transition commit must advance the transition count.");
        }

    }

    public DeadlineProbeAuthoritySnapshot AuthoritySnapshot() => new(
        SchemaId,
        _instance.DefinitionSha256,
        _instance.Definition.RulesetId,
        _instance.WorldSeed,
        Version,
        ParentWorldVersion,
        LastInstant,
        LastCause,
        CommitKind,
        Now,
        CellarSealed,
        CopyPassageOverrides());

    public DeadlineProbeSemanticSnapshot Snapshot() => new(
        SchemaId,
        _instance.DefinitionSha256,
        _instance.Definition.RulesetId,
        _instance.WorldSeed,
        Version,
        ParentWorldVersion,
        LastInstant,
        LastCause,
        CommitKind,
        CommitSummary,
        Now,
        CellarSealed,
        GetPassageEntryAccess(new PassageId(CellarGate)));

    private void ApplyEntryAccess(PassageEntryAccessChangedFact changed)
    {
        PassageDefinition passage = _instance.Graph.GetPassage(changed.PassageId);
        if (changed.ResultAccess == passage.InitialEntryAccess)
        {
            _entryOverrides.Remove(changed.PassageId.Value);
            return;
        }

        byte mask = 0;
        if (changed.ResultAccess.EnterableFromA)
        {
            mask |= 1;
        }

        if (changed.ResultAccess.EnterableFromB)
        {
            mask |= 2;
        }

        _entryOverrides.Upsert(changed.PassageId.Value, mask);
    }

    private IReadOnlyList<DeadlineProbePassageOverride> CopyPassageOverrides()
    {
        var result = new List<DeadlineProbePassageOverride>(_entryOverrides.Count);
        foreach (string passageId in _entryOverrides.Keys.Order(StringComparer.Ordinal))
        {
            GetIssue issue = _entryOverrides.Get(passageId, out byte mask);
            if (issue != GetIssue.None || mask > 3)
            {
                throw new InvalidDataException(
                    $"Passage entry override '{passageId}' is malformed.");
            }

            result.Add(new DeadlineProbePassageOverride(passageId, mask));
        }

        return result.AsReadOnly();
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
            Fields.Game,
            Fields.EntryOverrides,
        };
        if (CommitSummary is not null)
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

        RequireExactKeys(_graphRoot.Keys, [.. expectedRootKeys]);
        RequireExactKeys(_game.Keys, Fields.CellarSealed, Fields.Now);
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
            _graphRoot.GetOrThrow<ulong>(Fields.WorldSeed) != _instance.WorldSeed)
        {
            throw new InvalidDataException(
                "Deadline probe root binding does not match the supplied ScenarioInstance.");
        }
    }

    private static DurableDict<string>? GetOptionalDict(
        DurableDict<string> root,
        string key)
    {
        GetIssue issue = root.Get<DurableDict<string>>(key, out DurableDict<string>? value);
        if (issue == GetIssue.NotFound)
        {
            return null;
        }

        if (issue != GetIssue.None || value is null)
        {
            throw new InvalidDataException(
                $"Optional durable dictionary '{key}' is malformed.");
        }

        return value;
    }

    private static string? ReadOptionalString(
        DurableDict<string> root,
        string key)
    {
        GetIssue issue = root.Get<string>(key, out string? value);
        if (issue == GetIssue.NotFound)
        {
            return null;
        }

        if (issue != GetIssue.None || value is null)
        {
            throw new InvalidDataException(
                $"Optional string '{key}' is malformed.");
        }

        return value;
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

    private static string FormatVersion(WorldVersion version) =>
        $"{version.LineageId}:{version.TransitionCount}";

    private static void RequireExactKeys(
        IEnumerable<string> actual,
        params string[] expected)
    {
        string[] actualArray = [.. actual.Order(StringComparer.Ordinal)];
        string[] expectedArray = [.. expected.Order(StringComparer.Ordinal)];
        if (!actualArray.SequenceEqual(expectedArray, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Durable schema keys mismatch. Expected [{string.Join(", ", expectedArray)}], " +
                $"actual [{string.Join(", ", actualArray)}].");
        }
    }
}

using Atelia;
using Atelia.StateJournal;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

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

internal sealed record DeadlineProbeCommitReceipt(
    CommitAddress Parent,
    CommitAddress Head,
    string TransactionDigest,
    byte[] CanonicalEnvelope);

internal sealed record DeadlineProbeResolution(
    DeadlineProbeOutcome Outcome,
    DeadlineProbeSemanticSnapshot Snapshot);

internal sealed record DeadlineProbeSemanticSnapshot(
    string SchemaId,
    string DefinitionSha256,
    string RulesetId,
    ulong WorldSeed,
    WorldVersion Version,
    LogicalInstant? LastInstant,
    ModelTime Now,
    bool CellarSealed,
    PassageEntryAccess CellarGateAccess,
    string CanonicalLedgerBase64);

internal sealed class DeadlineProbeCommitFailedException : InvalidOperationException
{
    public DeadlineProbeCommitFailedException(
        string message,
        CommitAddress expectedParent,
        byte[] proposedEnvelope)
        : base(message)
    {
        ExpectedParent = expectedParent;
        ProposedEnvelope = [.. proposedEnvelope];
    }

    public CommitAddress ExpectedParent { get; }

    public byte[] ProposedEnvelope { get; }
}

/// <summary>
/// Test-only StateJournal-native session for one real FirstBoard deadline transition.
/// It deliberately exposes no DurableObject or mutable domain object.
/// </summary>
internal sealed class DeadlineProbeSession : IDisposable
{
    private const string MainBranch = "main";

    private readonly ScenarioInstance _instance;
    private readonly Repository _repository;
    private readonly DurableDeadlineProbeRootV1 _root;
    private bool _disposed;
    private bool _poisoned;
    private int _viewEpoch;

    private DeadlineProbeSession(
        ScenarioInstance instance,
        Repository repository,
        DurableDeadlineProbeRootV1 root)
    {
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
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Definition.Actors.Any(actor =>
                string.Equals(actor.InitialPlaceId, BoardIds.Cellar, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The narrow deadline probe cannot omit Cellar witness KnownFacts.",
                nameof(instance));
        }

        Repository repository = Require(
            Repository.Create(repositoryPath),
            "create StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CreateBranch(MainBranch),
                "create main branch");
            DurableDeadlineProbeRootV1 root = DurableDeadlineProbeRootV1.Create(
                revision,
                instance,
                genesisLineageId);
            root.ValidateComplete();
            Require(repository.Commit(root.GraphRoot), "commit deterministic Genesis root");
            return new DeadlineProbeSession(instance, repository, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public static DeadlineProbeSession Open(
        string repositoryPath,
        ScenarioInstance instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(instance);

        Repository repository = Require(
            Repository.Open(repositoryPath),
            "open StateJournal repository");
        try
        {
            Revision revision = Require(
                repository.CheckoutBranch(MainBranch),
                "checkout main branch");
            DurableDeadlineProbeRootV1 root = DurableDeadlineProbeRootV1.Open(
                revision,
                instance);
            return new DeadlineProbeSession(instance, repository, root);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public DeadlineProbeCommitReceipt Commit(
        DeadlineTransactionProbeV1 transaction,
        DeadlineProbeFault fault = DeadlineProbeFault.None)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        RequireActive();

        byte[] canonicalEnvelope = DeadlineTransactionProbeV1Codec.Encode(transaction);
        CommitAddress expectedParent = _root.HeadAddress;
        _root.RequireExpectedParent(transaction.ParentVersion);
        checked
        {
            _viewEpoch++;
        }

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
            _root.AppendCanonicalTransaction(canonicalEnvelope);
            _root.ValidateComplete();

            AteliaResult<CommitAddress> commit = _repository.Commit(_root.GraphRoot);
            if (commit.IsFailure)
            {
                string message = commit.Error!.ToString();
                Poison();
                throw new DeadlineProbeCommitFailedException(
                    $"StateJournal commit outcome is unknown: {message}",
                    expectedParent,
                    canonicalEnvelope);
            }

            CommitAddress head = commit.Value;
            return new DeadlineProbeCommitReceipt(
                expectedParent,
                head,
                DeadlineTransactionProbeV1Codec.ComputeDigest(canonicalEnvelope),
                [.. canonicalEnvelope]);
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

    public byte[][] CopyCanonicalLedger()
    {
        RequireActive();
        return _root.CopyCanonicalLedger();
    }

    public DeadlineProbeSemanticSnapshot Snapshot()
    {
        RequireActive();
        return _root.Snapshot();
    }

    public static DeadlineProbeResolution ResolveUnknownOutcome(
        string repositoryPath,
        ScenarioInstance instance,
        CommitAddress expectedParent,
        ReadOnlySpan<byte> proposedEnvelope)
    {
        DeadlineTransactionProbeV1 proposed =
            DeadlineTransactionProbeV1Codec.Decode(proposedEnvelope);
        using DeadlineProbeSession reopened = Open(repositoryPath, instance);
        DeadlineProbeSemanticSnapshot snapshot = reopened.Snapshot();
        if (reopened.HeadAddress == expectedParent && snapshot.Version == proposed.ParentVersion)
        {
            return new DeadlineProbeResolution(DeadlineProbeOutcome.NotCommitted, snapshot);
        }

        bool exactChild = reopened._root.HeadParentAddress == expectedParent &&
            snapshot.Version == new WorldVersion(
                proposed.ParentVersion.LineageId,
                checked(proposed.ParentVersion.TransitionCount + 1)) &&
            reopened._root.LedgerTailEquals(proposedEnvelope) &&
            reopened._root.MatchesPostState(proposed);
        if (exactChild)
        {
            return new DeadlineProbeResolution(DeadlineProbeOutcome.Committed, snapshot);
        }

        throw new InvalidDataException(
            "Reopened StateJournal HEAD is neither the expected parent nor the exact proposed child.");
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

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned)
        {
            throw new InvalidOperationException(
                "The StateJournal deadline probe Session is poisoned; reopen durable HEAD.");
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

internal sealed class DurableDeadlineProbeRootV1
{
    private const string SchemaId = "firstboard.statejournal-deadline-root/1";
    private const string CellarGate = BoardIds.CellarGatePassage;

    private static class Fields
    {
        public const string Schema = "schemaId";
        public const string Definition = "definitionSha256";
        public const string Ruleset = "rulesetId";
        public const string WorldSeed = "worldSeed";
        public const string GenesisLineage = "genesisLineageId";
        public const string Ledger = "transactionLedger";
        public const string Game = "game";
        public const string EntryOverrides = "passageEntryOverrides";
        public const string CellarSealed = "cellarSealed";
        public const string Now = "nowMs";
    }

    private readonly ScenarioInstance _instance;
    private readonly DurableDict<string> _graphRoot;
    private readonly DurableDeque _ledger;
    private readonly DurableDict<string> _game;
    private readonly DurableDict<string, byte> _entryOverrides;

    private DurableDeadlineProbeRootV1(
        ScenarioInstance instance,
        DurableDict<string> graphRoot,
        DurableDeque ledger,
        DurableDict<string> game,
        DurableDict<string, byte> entryOverrides)
    {
        _instance = instance;
        _graphRoot = graphRoot;
        _ledger = ledger;
        _game = game;
        _entryOverrides = entryOverrides;
    }

    public DurableObject GraphRoot => _graphRoot;

    public CommitAddress HeadAddress =>
        _graphRoot.Revision.HeadAddress ??
        throw new InvalidOperationException("The deadline probe root has no committed HEAD.");

    public CommitAddress? HeadParentAddress => _graphRoot.Revision.HeadParentAddress;

    public WorldVersion Version => ScanHistory().Version;

    public LogicalInstant? LastInstant => ScanHistory().LastInstant;

    public ModelTime Now => new(_game.GetOrThrow<long>(Fields.Now));

    public bool CellarSealed => _game.GetOrThrow<bool>(Fields.CellarSealed);

    public static DurableDeadlineProbeRootV1 Create(
        Revision revision,
        ScenarioInstance instance,
        long genesisLineageId)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);

        DurableDict<string> root = revision.CreateDict<string>();
        DurableDeque ledger = revision.CreateDeque();
        DurableDict<string> game = revision.CreateDict<string>();
        DurableDict<string, byte> entryOverrides = revision.CreateDict<string, byte>();

        root.Upsert(Fields.Schema, SchemaId);
        root.Upsert(Fields.Definition, instance.DefinitionSha256);
        root.Upsert(Fields.Ruleset, instance.Definition.RulesetId);
        root.Upsert(Fields.WorldSeed, instance.WorldSeed);
        root.Upsert(Fields.GenesisLineage, genesisLineageId);
        root.Upsert(Fields.Ledger, ledger);
        root.Upsert(Fields.Game, game);
        root.Upsert(Fields.EntryOverrides, entryOverrides);
        game.Upsert(Fields.CellarSealed, false);
        game.Upsert(Fields.Now, ModelTime.Zero.Ticks);

        return new DurableDeadlineProbeRootV1(
            instance,
            root,
            ledger,
            game,
            entryOverrides);
    }

    public static DurableDeadlineProbeRootV1 Open(
        Revision revision,
        ScenarioInstance instance)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(instance);
        DurableDict<string> root = revision.GraphRoot as DurableDict<string>
            ?? throw new InvalidDataException(
                "StateJournal main HEAD does not contain a deadline probe root.");
        var result = new DurableDeadlineProbeRootV1(
            instance,
            root,
            root.GetOrThrow<DurableDeque>(Fields.Ledger)!,
            root.GetOrThrow<DurableDict<string>>(Fields.Game)!,
            root.GetOrThrow<DurableDict<string, byte>>(Fields.EntryOverrides)!);
        result.ValidateBindings();
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

    public void AppendCanonicalTransaction(ReadOnlySpan<byte> canonicalEnvelope)
    {
        DeadlineTransactionProbeV1 transaction =
            DeadlineTransactionProbeV1Codec.Decode(canonicalEnvelope);
        RequireExpectedParent(transaction.ParentVersion);
        _ledger.PushBack(new ByteString(canonicalEnvelope));
    }

    public void ValidateComplete()
    {
        ValidateBindings();
        DerivedHistory history = ScanHistory();
        ModelTime expectedNow = history.LastInstant?.ModelTime ?? ModelTime.Zero;
        if (Now != expectedNow)
        {
            throw new InvalidDataException(
                $"Materialized Game.Now '{Now}' does not match ledger frontier '{expectedNow}'.");
        }

        if (history.Version.TransitionCount == 0 &&
            (CellarSealed || _entryOverrides.Count != 0))
        {
            throw new InvalidDataException(
                "Empty deadline ledger must describe the deterministic unsealed Genesis graph.");
        }

        if (history.Version.TransitionCount > 0)
        {
            DeadlineTransactionProbeV1 tail = DeadlineTransactionProbeV1Codec.Decode(
                ReadLedgerItem(_ledger.Count - 1).AsSpan());
            if (!MatchesPostState(tail))
            {
                throw new InvalidDataException(
                    "Materialized deadline graph does not match its canonical ledger tail.");
            }
        }
    }

    public byte[][] CopyCanonicalLedger()
    {
        byte[][] result = new byte[_ledger.Count][];
        for (int index = 0; index < _ledger.Count; index++)
        {
            ByteString item = ReadLedgerItem(index);
            result[index] = item.AsSpan().ToArray();
        }

        return result;
    }

    public bool LedgerTailEquals(ReadOnlySpan<byte> proposedEnvelope)
    {
        return _ledger.Count > 0 &&
            ReadLedgerItem(_ledger.Count - 1).AsSpan().SequenceEqual(proposedEnvelope);
    }

    public bool MatchesPostState(DeadlineTransactionProbeV1 transaction)
    {
        PassageEntryAccessChangedFact changed = (PassageEntryAccessChangedFact)
            ((SpatialBoardFact)transaction.Batch.Facts[1]).Value;
        return CellarSealed &&
            Now == transaction.Batch.Instant.ModelTime &&
            GetPassageEntryAccess(changed.PassageId) == changed.ResultAccess;
    }

    public DeadlineProbeSemanticSnapshot Snapshot()
    {
        DerivedHistory history = ScanHistory();
        string ledger = string.Join(
            '\n',
            CopyCanonicalLedger().Select(Convert.ToBase64String));
        return new DeadlineProbeSemanticSnapshot(
            SchemaId,
            _instance.DefinitionSha256,
            _instance.Definition.RulesetId,
            _instance.WorldSeed,
            history.Version,
            history.LastInstant,
            Now,
            CellarSealed,
            GetPassageEntryAccess(new PassageId(CellarGate)),
            ledger);
    }

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

    private DerivedHistory ScanHistory()
    {
        long genesisLineageId = _graphRoot.GetOrThrow<long>(Fields.GenesisLineage);
        var version = new WorldVersion(genesisLineageId, 0);
        LogicalInstant? lastInstant = null;
        for (int index = 0; index < _ledger.Count; index++)
        {
            DeadlineTransactionProbeV1 transaction =
                DeadlineTransactionProbeV1Codec.Decode(ReadLedgerItem(index).AsSpan());
            if (transaction.ParentVersion != version)
            {
                throw new InvalidDataException(
                    $"Ledger transaction {index} does not extend derived frontier '{version}'.");
            }

            if (lastInstant is LogicalInstant previous && transaction.Batch.Instant <= previous)
            {
                throw new InvalidDataException(
                    "Deadline ledger instants must advance strictly.");
            }

            version = new WorldVersion(
                version.LineageId,
                checked(version.TransitionCount + 1));
            lastInstant = transaction.Batch.Instant;
        }

        return new DerivedHistory(version, lastInstant);
    }

    private ByteString ReadLedgerItem(int index)
    {
        GetIssue issue = _ledger.GetAt<ByteString>(index, out ByteString value);
        if (issue != GetIssue.None || value.IsEmpty)
        {
            throw new InvalidDataException(
                $"Deadline ledger item {index} is not a non-empty canonical blob.");
        }

        return value;
    }

    private void ValidateBindings()
    {
        RequireExactKeys(
            _graphRoot.Keys,
            Fields.Schema,
            Fields.Definition,
            Fields.Ruleset,
            Fields.WorldSeed,
            Fields.GenesisLineage,
            Fields.Ledger,
            Fields.Game,
            Fields.EntryOverrides);
        RequireExactKeys(_game.Keys, Fields.CellarSealed, Fields.Now);

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

    private static void RequireExactKeys(IEnumerable<string> actual, params string[] expected)
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

    private readonly record struct DerivedHistory(
        WorldVersion Version,
        LogicalInstant? LastInstant);
}

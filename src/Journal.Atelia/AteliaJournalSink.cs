using Atelia.EventJournal;
using DramaBoard.Kernel.Journal;

namespace DramaBoard.Journal.Atelia;

/// <summary>Persists complete journal batches to one Atelia EventJournal branch.</summary>
public sealed class AteliaJournalSink<TPayload> : IJournalSink<TPayload>, IDisposable
{
    private readonly global::Atelia.EventJournal.EventJournal _journal;
    private readonly Func<TPayload, byte[]> _serializePayload;
    private readonly Func<byte[], TPayload> _deserializePayload;
    private readonly string _payloadCodec;
    private readonly RefId _refId;
    private readonly List<EventAddress> _batchAddresses = [];
    private readonly List<JournalBatch<TPayload>> _batches = [];
    private readonly IReadOnlyList<JournalBatch<TPayload>> _batchesView;
    private LineageMetadata _lineageMetadata;
    private EventAddress? _head;
    private bool _disposed;

    /// <summary>Opens or creates a journal and selects its branch.</summary>
    public AteliaJournalSink(
        string journalPath,
        long lineageId,
        string payloadCodec,
        Func<TPayload, byte[]> serializePayload,
        Func<byte[], TPayload> deserializePayload,
        string branchName = "main",
        EventJournalOptions? journalOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadCodec);
        ArgumentNullException.ThrowIfNull(serializePayload);
        ArgumentNullException.ThrowIfNull(deserializePayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        _payloadCodec = payloadCodec;
        _serializePayload = serializePayload;
        _deserializePayload = deserializePayload;
        BranchName = branchName;
        _batchesView = _batches.AsReadOnly();
        _journal = global::Atelia.EventJournal.EventJournal.OpenOrCreate(journalPath, journalOptions);

        try
        {
            if (_journal.ListBranches().Contains(branchName, StringComparer.Ordinal))
            {
                _refId = OpenBranch(branchName);
                ReplayBranch(lineageId);
            }
            else if (_journal.ListBranches().Count == 0)
            {
                _lineageMetadata = new LineageMetadata(
                    lineageId,
                    ParentLineageId: null,
                    ForkPrefixTransitionCount: null);
                _head = AppendFrame(
                    parent: null,
                    LineageMetadataCodec.Serialize(_lineageMetadata),
                    AteliaJournalFrameKinds.LineageCreated);
                _refId = CreateBranch(branchName, _head);
            }
            else
            {
                throw new InvalidOperationException($"Journal branch '{branchName}' does not exist.");
            }
        }
        catch
        {
            _journal.Dispose();
            throw;
        }
    }

    /// <summary>Gets the selected branch name.</summary>
    public string BranchName { get; }

    /// <summary>Gets the persisted lineage identity of the selected branch.</summary>
    public long LineageId => _lineageMetadata.LineageId;

    /// <summary>Gets the persisted parent lineage identity, if this branch was forked.</summary>
    public long? ParentLineageId => _lineageMetadata.ParentLineageId;

    /// <summary>Gets the persisted fork prefix transition count, if this branch was forked.</summary>
    public int? ForkPrefixTransitionCount => _lineageMetadata.ForkPrefixTransitionCount;

    /// <summary>Gets the journal directory path.</summary>
    public string JournalPath => _journal.JournalPath;

    /// <inheritdoc />
    public IReadOnlyList<JournalBatch<TPayload>> Batches => _batchesView;

    /// <summary>Opens a persisted branch, replays it, and returns the writable sink and batch view.</summary>
    public static (AteliaJournalSink<TPayload> Sink, IReadOnlyList<JournalBatch<TPayload>> Batches) OpenAndReplay(
        string journalPath,
        string branchName,
        long lineageId,
        string payloadCodec,
        Func<TPayload, byte[]> serializePayload,
        Func<byte[], TPayload> deserializePayload)
    {
        var sink = new AteliaJournalSink<TPayload>(
            journalPath,
            lineageId,
            payloadCodec,
            serializePayload,
            deserializePayload,
            branchName);
        return (sink, sink.Batches);
    }

    /// <inheritdoc />
    public void AppendBatch(JournalBatch<TPayload> batch)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(batch);
        if (_batches.Count > 0 && batch.Instant <= _batches[^1].Instant)
        {
            throw new InvalidOperationException("Journal batch instants must be strictly increasing.");
        }

        byte[] framePayload = JournalBatchEnvelopeCodec.Serialize(
            batch,
            _payloadCodec,
            _serializePayload);

        // Reserve local mirror capacity before entering the non-cancellable publish section.
        int resultingTransitionCount = checked(_batches.Count + 1);
        _batchAddresses.EnsureCapacity(resultingTransitionCount);
        _batches.EnsureCapacity(resultingTransitionCount);

        // Advancing the ref is the commit point. Before it, active history is unchanged; after it,
        // the frame is authoritative and reopening the branch replays it.
        EventAddress address = AppendFrameAndAdvance(
            _refId,
            _head,
            framePayload,
            AteliaJournalFrameKinds.JournalBatch,
            BranchName);

        _head = address;
        _batchAddresses.Add(address);
        _batches.Add(batch);
    }

    /// <summary>Creates a child branch at a complete committed transition boundary.</summary>
    public void ForkBranch(string branchName, int prefixTransitionCount, long lineageId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (prefixTransitionCount < 0 || prefixTransitionCount > _batches.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixTransitionCount));
        }

        if (lineageId == LineageId)
        {
            throw new ArgumentException("A fork must use a new lineage identity.", nameof(lineageId));
        }

        if (_journal.ListBranches().Contains(branchName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Journal branch '{branchName}' already exists.");
        }

        EventAddress? startPoint = prefixTransitionCount == _batches.Count
            ? _head
            : prefixTransitionCount == 0
                ? null
                : _batchAddresses[prefixTransitionCount - 1];
        var metadata = new LineageMetadata(
            lineageId,
            ParentLineageId: LineageId,
            ForkPrefixTransitionCount: prefixTransitionCount);
        EventAddress metadataAddress = AppendFrame(
            startPoint,
            LineageMetadataCodec.Serialize(metadata),
            AteliaJournalFrameKinds.LineageCreated);
        _ = CreateBranch(branchName, metadataAddress);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _journal.Dispose();
        _disposed = true;
    }

    private RefId OpenBranch(string branchName)
    {
        var result = _journal.OpenBranch(branchName);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Atelia failed to open branch '{branchName}': {result.Error!.Message}");
        }

        return result.Unwrap();
    }

    private RefId CreateBranch(string branchName, EventAddress? startPoint)
    {
        var result = _journal.CreateBranch(branchName, startPoint);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Atelia failed to create branch '{branchName}': {result.Error!.Message}");
        }

        return result.Unwrap();
    }

    private void ReplayBranch(long expectedLineageId)
    {
        var chainResult = _journal.ReadChronologicalChain(_refId, checkedRead: true);
        if (chainResult.IsFailure)
        {
            throw new InvalidDataException(
                $"Atelia failed to read branch '{BranchName}': {chainResult.Error!.Message}");
        }

        IReadOnlyList<EventAddress> chain = chainResult.Unwrap();
        EventAddress? persistedHead = _journal.GetHead(_refId);
        LineageMetadata? latestMetadata = null;
        foreach (EventAddress address in chain)
        {
            var readResult = _journal.ReadEvent(address);
            if (readResult.IsFailure)
            {
                throw new InvalidDataException(
                    $"Atelia failed to read a journal frame: {readResult.Error!.Message}");
            }

            using EventFrame frame = readResult.Unwrap();
            switch (frame.Header.OpaqueEventKind)
            {
                case AteliaJournalFrameKinds.LineageCreated:
                    LineageMetadata metadata = LineageMetadataCodec.Deserialize(frame.Payload);
                    ValidateLineageMetadata(metadata);
                    latestMetadata = metadata;
                    break;
                case AteliaJournalFrameKinds.JournalBatch:
                    ReplayBatchFrame(frame.Payload, address);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Persisted journal frame has unknown opaque kind " +
                        $"{frame.Header.OpaqueEventKind}.");
            }
        }

        _head = persistedHead;
        _lineageMetadata = latestMetadata
            ?? throw new InvalidDataException(
                $"Journal branch '{BranchName}' does not contain lineage metadata.");
        if (_lineageMetadata.LineageId != expectedLineageId)
        {
            throw new InvalidOperationException(
                $"LineageId mismatch for branch '{BranchName}': journal has " +
                $"{_lineageMetadata.LineageId}, caller supplied {expectedLineageId}.");
        }
    }

    private void ReplayBatchFrame(ReadOnlySpan<byte> framePayload, EventAddress address)
    {
        JournalBatch<TPayload> batch = JournalBatchEnvelopeCodec.Deserialize(
            framePayload,
            _payloadCodec,
            _deserializePayload);
        if (_batches.Count > 0 && batch.Instant <= _batches[^1].Instant)
        {
            throw new InvalidDataException("Persisted journal batch instants are not strictly increasing.");
        }

        _batchAddresses.Add(address);
        _batches.Add(batch);
    }

    private void ValidateLineageMetadata(LineageMetadata metadata)
    {
        if (metadata.ParentLineageId is null)
        {
            if (metadata.ForkPrefixTransitionCount is not null || _batches.Count != 0)
            {
                throw new InvalidDataException("Root lineage metadata must precede all journal batches.");
            }

            return;
        }

        if (metadata.ForkPrefixTransitionCount != _batches.Count)
        {
            throw new InvalidDataException(
                $"Fork lineage {metadata.LineageId} declares prefix " +
                $"{metadata.ForkPrefixTransitionCount}, but the physical prefix contains " +
                $"{_batches.Count} transitions.");
        }
    }

    private EventAddress AppendFrameAndAdvance(
        RefId refId,
        EventAddress? expectedHead,
        byte[] payload,
        uint opaqueEventKind,
        string branchName)
    {
        EventAddress address = AppendFrame(expectedHead, payload, opaqueEventKind);
        var advanceResult = _journal.AdvanceRef(refId, expectedHead, address);
        if (advanceResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"Atelia appended an orphan frame but failed to advance branch '{branchName}': " +
                advanceResult.Error!.Message);
        }

        return address;
    }

    private EventAddress AppendFrame(
        EventAddress? parent,
        byte[] payload,
        uint opaqueEventKind)
    {
        var appendResult = _journal.AppendEventFrame(
            parent,
            payload,
            opaqueEventKind,
            utcUnixTimeMilliseconds: 0);
        if (appendResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"Atelia failed to append a journal frame: {appendResult.Error!.Message}");
        }

        return appendResult.Unwrap();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

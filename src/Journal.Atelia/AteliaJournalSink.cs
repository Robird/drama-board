using Atelia.EventJournal;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Journal.Atelia;

/// <summary>Persists domain events to an Atelia EventJournal branch and mirrors them for synchronous reads.</summary>
public sealed class AteliaJournalSink<TPayload> : IJournalSink<TPayload>, IDisposable
{
    private readonly global::Atelia.EventJournal.EventJournal _journal;
    private readonly Func<TPayload, byte[]> _serializePayload;
    private readonly Func<EventKind, byte[], TPayload> _deserializePayload;
    private readonly string _payloadCodec;
    private readonly RefId _refId;
    private readonly List<EventAddress> _addresses = [];
    private readonly List<byte[]> _storedPayloads = [];
    private readonly List<int> _batchIndices = [];
    private readonly List<int> _batchCounts = [];
    private readonly List<DomainEvent<TPayload>> _events = [];
    private readonly IReadOnlyList<DomainEvent<TPayload>> _eventsView;
    private LineageMetadata _lineageMetadata;
    private EventAddress? _head;
    private bool _disposed;

    /// <summary>Opens or creates a journal and selects its branch.</summary>
    public AteliaJournalSink(
        string journalPath,
        long lineageId,
        string payloadCodec,
        Func<TPayload, byte[]> serializePayload,
        Func<EventKind, byte[], TPayload> deserializePayload,
        string branchName = "main")
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
        _eventsView = _events.AsReadOnly();
        _journal = global::Atelia.EventJournal.EventJournal.OpenOrCreate(journalPath);

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
                    ForkPrefixEventCount: null,
                    DomainEventEnvelopeCodec.FormatVersion);
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

    /// <summary>Gets the persisted fork prefix event count, if this branch was forked.</summary>
    public int? ForkPrefixEventCount => _lineageMetadata.ForkPrefixEventCount;

    /// <summary>Gets details when replay defensively removed an incomplete visible tail batch.</summary>
    public ReplayRecoveryInfo? ReplayRecovery { get; private set; }

    /// <summary>Gets the journal directory path.</summary>
    public string JournalPath => _journal.JournalPath;

    /// <inheritdoc />
    public IReadOnlyList<DomainEvent<TPayload>> Events => _eventsView;

    /// <summary>Opens a persisted branch, replays it, and returns both the writable sink and event view.</summary>
    public static (AteliaJournalSink<TPayload> Sink, IReadOnlyList<DomainEvent<TPayload>> Events) OpenAndReplay(
        string journalPath,
        string branchName,
        long lineageId,
        string payloadCodec,
        Func<TPayload, byte[]> serializePayload,
        Func<EventKind, byte[], TPayload> deserializePayload)
    {
        var sink = new AteliaJournalSink<TPayload>(
            journalPath,
            lineageId,
            payloadCodec,
            serializePayload,
            deserializePayload,
            branchName);
        return (sink, sink.Events);
    }

    /// <inheritdoc />
    public void Append(DomainEvent<TPayload> domainEvent) => AppendBatch([domainEvent]);

    /// <inheritdoc />
    public void AppendBatch(IReadOnlyList<DomainEvent<TPayload>> batch)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        LogicalTimestamp? previousTimestamp = _events.Count == 0 ? null : _events[^1].Timestamp;
        EventCause expectedCause = batch[0]?.Cause
            ?? throw new ArgumentException("A journal batch cannot contain null events.", nameof(batch));
        var envelopes = new List<byte[]>(batch.Count);
        for (int index = 0; index < batch.Count; index++)
        {
            DomainEvent<TPayload> domainEvent = batch[index]
                ?? throw new ArgumentException("A journal batch cannot contain null events.", nameof(batch));
            if (domainEvent.Cause != expectedCause)
            {
                throw new InvalidOperationException("All events in a journal batch must have the same cause.");
            }

            if (previousTimestamp is LogicalTimestamp previous && domainEvent.Timestamp <= previous)
            {
                throw new InvalidOperationException("Journal event timestamps must be strictly increasing.");
            }

            envelopes.Add(DomainEventEnvelopeCodec.Serialize(
                domainEvent,
                _payloadCodec,
                index,
                batch.Count,
                _serializePayload));
            previousTimestamp = domainEvent.Timestamp;
        }

        byte[] framePayload = DomainEventBatchFrameCodec.Serialize(envelopes);
        EventAddress address = AppendFrameAndAdvance(
            _refId,
            _head,
            framePayload,
            AteliaJournalFrameKinds.DomainEventBatch,
            BranchName);

        _head = address;
        for (int index = 0; index < batch.Count; index++)
        {
            _addresses.Add(address);
            _storedPayloads.Add(envelopes[index]);
            _batchIndices.Add(index);
            _batchCounts.Add(batch.Count);
            _events.Add(batch[index]);
        }
    }

    /// <summary>Creates a branch at an event-count boundary, requiring complete commit batches.</summary>
    public void ForkBranch(string branchName, int prefixEventCount, long lineageId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (prefixEventCount < 0 || prefixEventCount > _events.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixEventCount));
        }

        if (lineageId == LineageId)
        {
            throw new ArgumentException("A fork must use a new lineage identity.", nameof(lineageId));
        }

        if (_journal.ListBranches().Contains(branchName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Journal branch '{branchName}' already exists.");
        }

        if ((prefixEventCount > 0 &&
             _batchIndices[prefixEventCount - 1] != _batchCounts[prefixEventCount - 1] - 1) ||
            (prefixEventCount < _events.Count && _batchIndices[prefixEventCount] != 0))
        {
            throw new InvalidOperationException("A branch can only fork between complete event batches.");
        }

        EventAddress? startPoint = prefixEventCount == _events.Count
            ? _head
            : prefixEventCount == 0
                ? null
                : _addresses[prefixEventCount - 1];
        var metadata = new LineageMetadata(
            lineageId,
            ParentLineageId: LineageId,
            ForkPrefixEventCount: prefixEventCount,
            DomainEventEnvelopeCodec.FormatVersion);
        EventAddress metadataAddress = AppendFrame(
            startPoint,
            LineageMetadataCodec.Serialize(metadata),
            AteliaJournalFrameKinds.LineageCreated);
        _ = CreateBranch(branchName, metadataAddress);
    }

    /// <summary>Reads copies of the logical envelope bytes stored on the selected branch.</summary>
    public IReadOnlyList<byte[]> ReadStoredPayloads()
    {
        ThrowIfDisposed();
        return [.. _storedPayloads.Select(payload => payload.ToArray())];
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
        EventAddress? lastValidAddress = null;
        LineageMetadata? latestMetadata = null;
        bool recovered = false;
        for (int frameIndex = 0; frameIndex < chain.Count; frameIndex++)
        {
            EventAddress address = chain[frameIndex];
            var readResult = _journal.ReadEvent(address);
            if (readResult.IsFailure)
            {
                throw new InvalidDataException(
                    $"Atelia failed to read an event frame: {readResult.Error!.Message}");
            }

            using EventFrame frame = readResult.Unwrap();
            switch (frame.Header.OpaqueEventKind)
            {
                case AteliaJournalFrameKinds.LineageCreated:
                    LineageMetadata metadata = LineageMetadataCodec.Deserialize(frame.Payload);
                    ValidateLineageMetadata(metadata);
                    latestMetadata = metadata;
                    break;
                case AteliaJournalFrameKinds.DomainEventBatch:
                    try
                    {
                        ReplayBatchFrame(frame.Payload, address);
                    }
                    catch (IncompleteEventBatchException exception)
                    {
                        RecoverIncompleteTail(
                            persistedHead,
                            lastValidAddress,
                            chain.Count - frameIndex,
                            exception.Message);
                        recovered = true;
                    }

                    break;
                default:
                    throw new InvalidDataException(
                        $"Persisted journal frame has unknown opaque kind " +
                        $"{frame.Header.OpaqueEventKind}.");
            }

            if (recovered)
            {
                break;
            }

            lastValidAddress = address;
        }

        _head = recovered ? lastValidAddress : persistedHead;
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
        IReadOnlyList<byte[]> envelopes = DomainEventBatchFrameCodec.Deserialize(framePayload);
        var batchEvents = new List<DomainEvent<TPayload>>(envelopes.Count);
        LogicalTimestamp? previousTimestamp = _events.Count == 0 ? null : _events[^1].Timestamp;
        EventCause? expectedCause = null;
        for (int index = 0; index < envelopes.Count; index++)
        {
            DomainEvent<TPayload> domainEvent = DomainEventEnvelopeCodec.Deserialize(
                envelopes[index],
                _payloadCodec,
                _deserializePayload,
                out int batchIndex,
                out int batchCount);
            if (batchIndex != index || batchCount != envelopes.Count)
            {
                throw new IncompleteEventBatchException(
                    $"Persisted batch is incomplete: event {index} declares " +
                    $"bi={batchIndex}, bc={batchCount}, frame count={envelopes.Count}.");
            }

            if (expectedCause is EventCause cause && domainEvent.Cause != cause)
            {
                throw new InvalidDataException("Persisted events in one batch do not share the same cause.");
            }

            if (previousTimestamp is LogicalTimestamp previous && domainEvent.Timestamp <= previous)
            {
                throw new InvalidDataException("Persisted journal event timestamps are not strictly increasing.");
            }

            expectedCause = domainEvent.Cause;
            previousTimestamp = domainEvent.Timestamp;
            batchEvents.Add(domainEvent);
        }

        for (int index = 0; index < batchEvents.Count; index++)
        {
            _addresses.Add(address);
            _storedPayloads.Add(envelopes[index]);
            _batchIndices.Add(index);
            _batchCounts.Add(batchEvents.Count);
            _events.Add(batchEvents[index]);
        }
    }

    private void ValidateLineageMetadata(LineageMetadata metadata)
    {
        if (metadata.EnvelopeFormatVersion != DomainEventEnvelopeCodec.FormatVersion)
        {
            throw new NotSupportedException(
                $"Lineage {metadata.LineageId} was created for domain event envelope version " +
                $"{metadata.EnvelopeFormatVersion}, but this adapter requires " +
                $"{DomainEventEnvelopeCodec.FormatVersion}.");
        }

        if (metadata.ParentLineageId is null)
        {
            if (metadata.ForkPrefixEventCount is not null || _events.Count != 0)
            {
                throw new InvalidDataException("Root lineage metadata must precede all domain events.");
            }

            return;
        }

        if (metadata.ForkPrefixEventCount != _events.Count)
        {
            throw new InvalidDataException(
                $"Fork lineage {metadata.LineageId} declares prefix " +
                $"{metadata.ForkPrefixEventCount}, but the physical prefix contains {_events.Count} events.");
        }
    }

    private void RecoverIncompleteTail(
        EventAddress? persistedHead,
        EventAddress? lastValidAddress,
        int truncatedFrameCount,
        string reason)
    {
        var moveResult = _journal.MoveRef(_refId, persistedHead, lastValidAddress);
        if (moveResult.IsFailure)
        {
            throw new InvalidDataException(
                $"Detected an incomplete event batch but failed to truncate branch '{BranchName}': " +
                moveResult.Error!.Message);
        }

        ReplayRecovery = new ReplayRecoveryInfo(truncatedFrameCount, reason);
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

/// <summary>Reports a defensive ref rewind that removed an incomplete visible tail batch.</summary>
public sealed record ReplayRecoveryInfo(int TruncatedFrameCount, string Reason);

using Atelia.EventJournal;
using DramaBoard.Kernel.Journal;

namespace DramaBoard.Journal.Atelia;

/// <summary>Persists domain events to an Atelia EventJournal branch and mirrors them for synchronous reads.</summary>
public sealed class AteliaJournalSink<TPayload> : IJournalSink<TPayload>, IDisposable
{
    private readonly global::Atelia.EventJournal.EventJournal _journal;
    private readonly Func<TPayload, byte[]> _serializePayload;
    private readonly Func<byte[], TPayload> _deserializePayload;
    private readonly RefId _refId;
    private readonly List<EventAddress> _addresses = [];
    private readonly List<DomainEvent<TPayload>> _events = [];
    private readonly IReadOnlyList<DomainEvent<TPayload>> _eventsView;
    private EventAddress? _head;
    private bool _disposed;

    /// <summary>Opens or creates a journal and selects its branch.</summary>
    public AteliaJournalSink(
        string journalPath,
        Func<TPayload, byte[]> serializePayload,
        Func<byte[], TPayload> deserializePayload,
        string branchName = "main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(serializePayload);
        ArgumentNullException.ThrowIfNull(deserializePayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

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
                ReplayBranch();
            }
            else if (_journal.ListBranches().Count == 0)
            {
                _refId = CreateBranch(branchName, startPoint: null);
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

    /// <summary>Gets the journal directory path.</summary>
    public string JournalPath => _journal.JournalPath;

    /// <inheritdoc />
    public IReadOnlyList<DomainEvent<TPayload>> Events => _eventsView;

    /// <summary>Opens a persisted branch, replays it, and returns both the writable sink and event view.</summary>
    public static (AteliaJournalSink<TPayload> Sink, IReadOnlyList<DomainEvent<TPayload>> Events) OpenAndReplay(
        string journalPath,
        string branchName,
        Func<TPayload, byte[]> serializePayload,
        Func<byte[], TPayload> deserializePayload)
    {
        var sink = new AteliaJournalSink<TPayload>(
            journalPath,
            serializePayload,
            deserializePayload,
            branchName);
        return (sink, sink.Events);
    }

    /// <inheritdoc />
    public void Append(DomainEvent<TPayload> domainEvent)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (_events.Count > 0 && domainEvent.Timestamp <= _events[^1].Timestamp)
        {
            throw new InvalidOperationException("Journal event timestamps must be strictly increasing.");
        }

        byte[] envelope = DomainEventEnvelopeCodec.Serialize(domainEvent, _serializePayload);
        var appendResult = _journal.AppendEventFrame(
            _head,
            envelope,
            opaqueEventKind: 0,
            utcUnixTimeMilliseconds: 0);
        if (appendResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"Atelia failed to append an event frame: {appendResult.Error!.Message}");
        }

        EventAddress address = appendResult.Unwrap();
        var advanceResult = _journal.AdvanceRef(_refId, _head, address);
        if (advanceResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"Atelia appended an orphan frame but failed to advance branch '{BranchName}': " +
                advanceResult.Error!.Message);
        }

        _head = address;
        _addresses.Add(address);
        _events.Add(domainEvent);
    }

    /// <summary>Creates a branch at an event-count boundary, requiring complete commit batches.</summary>
    public void ForkBranch(string branchName, int prefixEventCount)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (prefixEventCount < 0 || prefixEventCount > _events.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixEventCount));
        }

        if (prefixEventCount > 0 &&
            prefixEventCount < _events.Count &&
            _events[prefixEventCount - 1].Cause.BatchOrdinal == _events[prefixEventCount].Cause.BatchOrdinal)
        {
            throw new InvalidOperationException("A branch can only fork between complete event batches.");
        }

        EventAddress? startPoint = prefixEventCount == 0 ? null : _addresses[prefixEventCount - 1];
        if (prefixEventCount == _events.Count && startPoint is EventAddress sourceHead)
        {
            var result = _journal.ForkBranch(branchName, _refId, sourceHead);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Atelia failed to fork branch '{branchName}': {result.Error!.Message}");
            }

            return;
        }

        var createResult = _journal.CreateBranch(branchName, startPoint);
        if (createResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"Atelia failed to create branch '{branchName}': {createResult.Error!.Message}");
        }
    }

    /// <summary>Reads copies of the logical envelope bytes stored on the selected branch.</summary>
    public IReadOnlyList<byte[]> ReadStoredPayloads()
    {
        ThrowIfDisposed();
        var payloads = new byte[_addresses.Count][];
        for (int index = 0; index < _addresses.Count; index++)
        {
            var readResult = _journal.ReadEvent(_addresses[index]);
            if (readResult.IsFailure)
            {
                throw new InvalidDataException(
                    $"Atelia failed to read an event frame: {readResult.Error!.Message}");
            }

            using EventFrame frame = readResult.Unwrap();
            payloads[index] = frame.Payload.ToArray();
        }

        return payloads;
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

    private void ReplayBranch()
    {
        var chainResult = _journal.ReadChronologicalChain(_refId, checkedRead: true);
        if (chainResult.IsFailure)
        {
            throw new InvalidDataException(
                $"Atelia failed to read branch '{BranchName}': {chainResult.Error!.Message}");
        }

        foreach (EventAddress address in chainResult.Unwrap())
        {
            var readResult = _journal.ReadEvent(address);
            if (readResult.IsFailure)
            {
                throw new InvalidDataException(
                    $"Atelia failed to read an event frame: {readResult.Error!.Message}");
            }

            using EventFrame frame = readResult.Unwrap();
            DomainEvent<TPayload> domainEvent = DomainEventEnvelopeCodec.Deserialize(
                frame.Payload,
                _deserializePayload);
            if (_events.Count > 0 && domainEvent.Timestamp <= _events[^1].Timestamp)
            {
                throw new InvalidDataException("Persisted journal event timestamps are not strictly increasing.");
            }

            _addresses.Add(address);
            _events.Add(domainEvent);
        }

        _head = _journal.GetHead(_refId);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
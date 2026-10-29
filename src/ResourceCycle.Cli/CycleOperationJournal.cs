using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DramaBoard.ResourceCycle;

internal enum CycleOperationStatus { Succeeded, Rejected, Interrupted }

internal sealed record CycleOperationReceipt(string OperationRecordId, string BatchId, string ToolCallId,
    string ActorId, long? ContextSequence, string ToolName, string RawInput);

internal sealed record CycleOperationResult(string ToolCallId, CycleOperationStatus Status, string Result,
    string? WorldVersion, string? DomainEvent, bool ExactResult);

internal sealed record CycleOperationEntry(CycleOperationReceipt Received, CycleOperationResult? Completed);

/// <summary>A completed domain E/S boundary, supplied after pending-event recovery. Never an execution request.</summary>
internal sealed record CycleOperationCommit(string OperationRecordId, string ToolCallId, string ActorId,
    string Result, string WorldVersion, string DomainEvent, CycleOperationDisposition Disposition) {
    internal static CycleOperationStatus StatusOf(CycleOperationDisposition disposition) => disposition switch {
        CycleOperationDisposition.Succeeded => CycleOperationStatus.Succeeded,
        CycleOperationDisposition.Rejected => CycleOperationStatus.Rejected,
        _ => throw new InvalidDataException("The game history contains an unknown operation disposition."),
    };
}

/// <summary>Host-owned receipt/result audit. The game history alone authorizes gameplay recovery.</summary>
/// <remarks>A durable append is flushed before it becomes visible in memory. An I/O failure retires this
/// instance; dispose and reopen. Complete newline-terminated records are checksummed and chained. Only an
/// unfinished final line may be discarded on reopen; corruption in a completed line fails closed.</remarks>
internal sealed class CycleOperationJournal : IDisposable {
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Stream? _stream;
    private readonly Dictionary<string, CycleOperationEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string BatchId, string ToolCallId), string> _calls = [];
    private long _sequence;
    private string _lastHash = string.Empty;
    private bool _disposed;
    private bool _faulted;

    private CycleOperationJournal(Stream? stream) {
        _stream = stream;
        if (stream is not null) { ReadExisting(stream); }
    }

    public static CycleOperationJournal CreateInMemory() => new(null);

    public static CycleOperationJournal Open(string filePath) {
        // FileShare.Read permits diagnostics, while excluding a second writer for this entire lifetime.
        var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try { return new(stream); }
        catch { stream.Dispose(); throw; }
    }

    internal static CycleOperationJournal CreateForTesting(Stream stream) => new(stream);

    public long DiscardedTailBytes { get; private set; }
    public bool IsFaulted => _faulted;
    public IReadOnlyList<CycleOperationEntry> Entries {
        get { RequireUsable(); return _entries.Values.ToArray(); }
    }
    public IReadOnlyList<string> PendingOperationRecordIds {
        get { RequireUsable(); return _entries.Values.Where(x => x.Completed is null).Select(x => x.Received.OperationRecordId).ToArray(); }
    }

    /// <summary>Call before parsing, validating or executing submitted input. Explicit record IDs support
    /// transport retransmission; a new invocation must use a new record ID even when its arguments match.</summary>
    public CycleOperationReceipt Receive(string batchId, string toolCallId, string actorId, long? contextSequence,
        string toolName, string rawInput, string? operationRecordId = null) {
        RequireUsable();
        var received = new CycleOperationReceipt(operationRecordId ?? Guid.NewGuid().ToString("N"), batchId,
            toolCallId, actorId, contextSequence, toolName, rawInput);
        ValidateReceipt(received);
        if (_entries.TryGetValue(received.OperationRecordId, out var existing)) {
            if (existing.Received != received) { throw new InvalidOperationException("The operation record was reused with different input or identity."); }
            return existing.Received;
        }
        if (_calls.ContainsKey((batchId, toolCallId))) {
            throw new InvalidOperationException("A tool-call ID already exists in this invocation batch.");
        }
        Append(new(received, null, null));
        return received;
    }

    /// <summary>Persist before delivering a result. Repeated delivery uses the saved result unchanged.</summary>
    public CycleOperationResult Complete(string operationRecordId, CycleOperationStatus status, string result,
        string? worldVersion = null, string? domainEvent = null, bool exactResult = true) {
        RequireUsable();
        if (!_entries.TryGetValue(operationRecordId, out var entry)) { throw new InvalidOperationException("No receipt exists for this operation."); }
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(status)) { throw new ArgumentOutOfRangeException(nameof(status)); }
        var completed = new CycleOperationResult(entry.Received.ToolCallId, status, result, worldVersion, domainEvent, exactResult);
        if (entry.Completed is not null) {
            if (entry.Completed != completed) { throw new InvalidOperationException("A saved operation result cannot be rewritten."); }
            return entry.Completed;
        }
        Append(new(null, operationRecordId, completed));
        return completed;
    }

    /// <summary>Invoke only after the authoritative game history has completed pending E recovery.
    /// Missing query/unpublished results become Interrupted; this method never repeats an operation.</summary>
    public void Reconcile(IEnumerable<CycleOperationCommit> committedOperations) {
        RequireUsable();
        ArgumentNullException.ThrowIfNull(committedOperations);
        var commits = new Dictionary<string, CycleOperationCommit>(StringComparer.Ordinal);
        foreach (var commit in committedOperations) {
            _ = CycleOperationCommit.StatusOf(commit.Disposition);
            if (!commits.TryAdd(commit.OperationRecordId, commit) && commits[commit.OperationRecordId] != commit) {
                throw new InvalidDataException("The game history contains conflicting operation references.");
            }
            if (_entries.TryGetValue(commit.OperationRecordId, out var entry) &&
                (entry.Received.ToolCallId != commit.ToolCallId || entry.Received.ActorId != commit.ActorId)) {
                throw new InvalidDataException("The game history does not match the saved operation identity.");
            }
        }
        foreach (string recordId in PendingOperationRecordIds) {
            if (commits.TryGetValue(recordId, out var commit)) {
                Complete(recordId, CycleOperationCommit.StatusOf(commit.Disposition), commit.Result, commit.WorldVersion, commit.DomainEvent, exactResult: false);
            } else {
                Complete(recordId, CycleOperationStatus.Interrupted,
                    "操作结果未保存，且没有已提交的游戏事实；未自动重做。", exactResult: false);
            }
        }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _stream?.Dispose();
    }

    private void Append(JournalChange change) {
        string payload = JsonSerializer.Serialize(change);
        long next = checked(_sequence + 1);
        string hash = Hash(next, _lastHash, payload);
        byte[] bytes = Utf8.GetBytes(JsonSerializer.Serialize(new JournalLine(next, _lastHash, payload, hash)) + "\n");
        try {
            if (_stream is not null) {
                _stream.Write(bytes);
                FlushDurable(_stream);
            }
        } catch {
            _faulted = true;
            throw;
        }
        Apply(change);
        _sequence = next;
        _lastHash = hash;
    }

    private void ReadExisting(Stream stream) {
        stream.Position = 0;
        using var line = new MemoryStream();
        byte[] buffer = new byte[4096];
        long position = 0;
        long completeEnd = 0;
        int count;
        while ((count = stream.Read(buffer)) != 0) {
            for (int index = 0; index < count; index++) {
                position++;
                if (buffer[index] == (byte)'\n') {
                    ReadLine(line.ToArray());
                    line.SetLength(0);
                    completeEnd = position;
                } else {
                    line.WriteByte(buffer[index]);
                }
            }
        }
        DiscardedTailBytes = position - completeEnd;
        if (DiscardedTailBytes != 0) {
            stream.SetLength(completeEnd);
            FlushDurable(stream);
        }
        stream.Position = completeEnd;
    }

    private void ReadLine(byte[] bytes) {
        try {
            var line = JsonSerializer.Deserialize<JournalLine>(Utf8.GetString(bytes))
                ?? throw new InvalidDataException("An operation journal line is null.");
            if (line.Sequence != checked(_sequence + 1) || line.PreviousHash != _lastHash ||
                line.Payload is null || line.Hash != Hash(line.Sequence, line.PreviousHash, line.Payload)) {
                throw new InvalidDataException("An operation journal line has an invalid sequence or checksum.");
            }
            var change = JsonSerializer.Deserialize<JournalChange>(line.Payload)
                ?? throw new InvalidDataException("An operation journal payload is null.");
            Apply(change);
            _sequence = line.Sequence;
            _lastHash = line.Hash;
        } catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException or InvalidOperationException) {
            throw new InvalidDataException("A completed operation journal line is corrupt.", exception);
        }
    }

    private void Apply(JournalChange change) {
        if (change.Received is { } received && change.OperationRecordId is null && change.Completed is null) {
            ValidateReceipt(received);
            if (!_entries.TryAdd(received.OperationRecordId, new(received, null)) ||
                !_calls.TryAdd((received.BatchId, received.ToolCallId), received.OperationRecordId)) {
                throw new InvalidDataException("Duplicate operation receipt in the journal.");
            }
        } else if (change.Received is null && change.OperationRecordId is { } recordId && change.Completed is { } completed) {
            if (!_entries.TryGetValue(recordId, out var entry) || entry.Completed is not null ||
                completed.ToolCallId != entry.Received.ToolCallId || !Enum.IsDefined(completed.Status) || completed.Result is null) {
                throw new InvalidDataException("Invalid operation result pairing in the journal.");
            }
            _entries[recordId] = entry with { Completed = completed };
        } else {
            throw new InvalidDataException("Unknown operation journal record shape.");
        }
    }

    private static void ValidateReceipt(CycleOperationReceipt received) {
        ArgumentException.ThrowIfNullOrWhiteSpace(received.OperationRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.BatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.ToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.ToolName);
        ArgumentNullException.ThrowIfNull(received.RawInput);
        if (received.ContextSequence is <= 0) { throw new ArgumentOutOfRangeException(nameof(received.ContextSequence)); }
    }

    private static string Hash(long sequence, string previousHash, string payload) => Convert.ToHexString(
        SHA256.HashData(Utf8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previousHash + "\n" + payload)));

    private static void FlushDurable(Stream stream) {
        if (stream is FileStream file) { file.Flush(flushToDisk: true); }
        else { stream.Flush(); }
    }

    private void RequireUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("Operation journal is faulted; dispose and reopen it."); }
    }

    private sealed record JournalChange(CycleOperationReceipt? Received, string? OperationRecordId, CycleOperationResult? Completed);
    private sealed record JournalLine(long Sequence, string PreviousHash, string Payload, string Hash);
}

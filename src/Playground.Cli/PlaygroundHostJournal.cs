using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

internal sealed record PlaygroundHostRecord(int FormatVersion, long Sequence, string Kind, string? RunId,
    string? BranchId, string? ActorId, long? ContextSequence, JsonElement Payload) {
    public T ReadPayload<T>() => HostHistoryPayloads.Read<T>(this);
}

internal sealed record PlaygroundHostJournalSnapshot(string? JournalId, IReadOnlyList<PlaygroundHostRecord> Records,
    long DiscardedTailBytes);

/// <summary>The single serial host timeline. The game repository remains the authority for world recovery.</summary>
/// <remarks>Only a complete, checksummed line followed by a durable flush is installed in memory. A failed
/// write retires this writer. Read-only snapshots never repair files; reopening a writer may discard only an
/// unfinished final line. The operation index is branch-local and updated after each successful append.</remarks>
internal sealed class PlaygroundHostJournal : IDisposable {
    public const int CurrentFormatVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Stream? _stream;
    private readonly JournalState _state = new();
    private readonly IReadOnlyList<PlaygroundHostRecord> _records;
    private bool _disposed;
    private bool _faulted;
    private long _appendTicks;

    private PlaygroundHostJournal(Stream? stream) {
        _stream = stream;
        _records = _state.Records.AsReadOnly();
        if (stream is not null) {
            ReadLines(stream, _state.Accept, out long tail);
            DiscardedTailBytes = tail;
            if (tail > 0) {
                stream.SetLength(stream.Length - tail);
                FlushDurable(stream);
            }
            stream.Position = stream.Length;
        }
        if (_state.Records.Count == 0) {
            AppendCore("metadata", null, null, null, null,
                new { FormatVersion = CurrentFormatVersion, JournalId = Guid.NewGuid().ToString("N") });
        }
    }

    public static string JournalPath(string store) => Path.Combine(store, "host", "events.jsonl");

    public static PlaygroundHostJournal CreateInMemory() => new(null);

    public static PlaygroundHostJournal Open(string path) {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try { return new(stream); }
        catch {
            try { stream.Dispose(); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Freezes and validates the complete prefix without creating, repairing or reconciling anything.</summary>
    public static PlaygroundHostJournalSnapshot Read(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var state = new JournalState();
        ReadLines(stream, state.Accept, out long tail);
        return new(state.JournalId, state.Records.AsReadOnly(), tail);
    }

    internal static PlaygroundHostJournal CreateForTesting(Stream stream) => new(stream);

    public string JournalId { get { RequireUsable(); return _state.JournalId!; } }
    public IReadOnlyList<PlaygroundHostRecord> Records { get { RequireUsable(); return _records; } }
    public bool IsFaulted => _faulted;
    public long DiscardedTailBytes { get; }
    /// <summary>Cumulative append projection, validation, hashing, flush and index installation.</summary>
    public TimeSpan TotalAppendTime => TimeSpan.FromSeconds((double)_appendTicks / System.Diagnostics.Stopwatch.Frequency);

    public PlaygroundHostRecord Append(string kind, string? runId, string? branchId, string? actorId,
        long? contextSequence, object payload) {
        RequireUsable();
        if (kind == "metadata") { throw new ArgumentException("Host metadata can only be written during initialization.", nameof(kind)); }
        return AppendCore(kind, runId, branchId, actorId, contextSequence, payload);
    }

    internal PlaygroundOperationIndex Operations(string branchId) {
        RequireUsable();
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        return _state.GetOperations(branchId);
    }

    internal void RequireUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("Host journal is faulted; dispose and reopen it."); }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        try { _stream?.Dispose(); }
        catch (IOException) when (_faulted) { /* Preserve the append/flush failure that retired this writer. */ }
    }

    private PlaygroundHostRecord AppendCore(string kind, string? runId, string? branchId, string? actorId,
        long? contextSequence, object payload) {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return AppendMeasured(kind, runId, branchId, actorId, contextSequence, payload); }
        finally { _appendTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
    }

    private PlaygroundHostRecord AppendMeasured(string kind, string? runId, string? branchId, string? actorId,
        long? contextSequence, object payload) {
        ArgumentNullException.ThrowIfNull(payload);
        long sequence = checked((long)_state.Records.Count + 1);
        var record = new PlaygroundHostRecord(CurrentFormatVersion, sequence, kind, runId, branchId, actorId,
            contextSequence, JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions));
        _state.Validate(record);
        string serialized = JsonSerializer.Serialize(record, JsonOptions);
        string hash = Hash(sequence, _state.LastHash, serialized);
        byte[] bytes = Utf8.GetBytes(JsonSerializer.Serialize(new JournalLine(sequence, _state.LastHash, serialized, hash), JsonOptions) + "\n");
        try {
            if (_stream is not null) {
                _stream.Write(bytes);
                FlushDurable(_stream);
            }
        } catch {
            _faulted = true;
            throw;
        }
        _state.Install(record, hash);
        return record;
    }

    private static void ReadLines(Stream stream, Action<byte[]> accept, out long tail) {
        stream.Position = 0;
        // A reader sees one prefix even while a diagnostic reader shares an active writer's file.
        long remaining = stream.Length;
        using var line = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (remaining > 0) {
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) { throw new IOException("The host journal changed length while being read."); }
            remaining -= count;
            for (int index = 0; index < count; index++) {
                if (buffer[index] == (byte)'\n') {
                    accept(line.ToArray());
                    line.SetLength(0);
                } else { line.WriteByte(buffer[index]); }
            }
        }
        tail = line.Length;
    }

    private sealed class JournalState {
        private readonly Dictionary<string, PlaygroundOperationIndex> _operations = new(StringComparer.Ordinal);
        public List<PlaygroundHostRecord> Records { get; } = [];
        public string? JournalId { get; private set; }
        public string LastHash { get; private set; } = "";

        public PlaygroundOperationIndex GetOperations(string branchId) {
            if (!_operations.TryGetValue(branchId, out var index)) {
                index = new();
                _operations.Add(branchId, index);
            }
            return index;
        }

        public void Accept(byte[] bytes) {
            try {
                var line = JsonSerializer.Deserialize<JournalLine>(Utf8.GetString(bytes))
                    ?? throw new InvalidDataException("A host journal line is null.");
                // Hash the saved payload verbatim rather than reserializing it with today's encoder.
                if (line.Sequence != checked((long)Records.Count + 1) || line.PreviousHash != LastHash ||
                    line.Payload is null || line.Hash != Hash(line.Sequence, LastHash, line.Payload)) {
                    throw new InvalidDataException("A host journal line has an invalid sequence or checksum.");
                }
                var record = JsonSerializer.Deserialize<PlaygroundHostRecord>(line.Payload)
                    ?? throw new InvalidDataException("A host journal record is null.");
                if (record.Sequence != line.Sequence) { throw new InvalidDataException("The host record sequence does not match its envelope."); }
                Validate(record);
                Install(record, line.Hash);
            } catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException or InvalidOperationException or OverflowException) {
                throw new InvalidDataException("A completed host journal line is corrupt.", error);
            }
        }

        public void Validate(PlaygroundHostRecord record) {
            if (record.FormatVersion != CurrentFormatVersion) {
                throw new InvalidDataException($"Unsupported host journal format version: {record.FormatVersion}.");
            }
            if (record.Sequence != checked((long)Records.Count + 1)) { throw new InvalidDataException("Invalid host record sequence."); }
            if (record.Kind is not ("metadata" or "run-start" or "run-end" or "operation-received" or "operation-completed" or
                "character-material" or "model-request" or "model-response" or "model-failure" or "feedback-saved" or "branch-fork")) {
                throw new InvalidDataException($"Unknown host record kind: {record.Kind}.");
            }
            foreach (string? identity in new[] { record.RunId, record.BranchId, record.ActorId }) {
                if (identity is not null && string.IsNullOrWhiteSpace(identity)) { throw new InvalidDataException("Host identities must be nonempty when supplied."); }
            }
            if (record.ContextSequence is <= 0 || record.Payload.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException("Host records require an object payload and a positive optional ContextSequence.");
            }
            if ((Records.Count == 0) != (record.Kind == "metadata")) { throw new InvalidDataException("Host metadata must appear exactly once at the beginning."); }
            if (record.Kind == "metadata") { _ = ReadJournalId(record); }
            HostHistoryPayloads.Validate(record);
            if (record.Kind is "operation-received" or "operation-completed") {
                if (record.RunId is null || record.BranchId is null || record.ActorId is null) {
                    throw new InvalidDataException("Operation records require run, branch and actor identities.");
                }
                // Validation does not install any operation state before the durable append succeeds.
                var index = _operations.TryGetValue(record.BranchId, out var existing) ? existing : new PlaygroundOperationIndex();
                index.Validate(record);
            }
        }

        public void Install(PlaygroundHostRecord record, string hash) {
            if (record.Kind == "metadata") { JournalId = ReadJournalId(record); }
            if (record.Kind is "operation-received" or "operation-completed") { GetOperations(record.BranchId!).Install(record); }
            Records.Add(record);
            LastHash = hash;
        }

        private static string ReadJournalId(PlaygroundHostRecord record) {
            if (record.RunId is not null || record.BranchId is not null || record.ActorId is not null || record.ContextSequence is not null ||
                !record.Payload.TryGetProperty("FormatVersion", out var version) || !version.TryGetInt32(out int formatVersion) || formatVersion != CurrentFormatVersion ||
                !record.Payload.TryGetProperty("JournalId", out var identity) || identity.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(identity.GetString(), "N", out _)) {
                throw new InvalidDataException("Invalid host journal metadata identity or version.");
            }
            return identity.GetString()!;
        }
    }

    private static string Hash(long sequence, string previousHash, string payload) => Convert.ToHexString(
        SHA256.HashData(Utf8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previousHash + "\n" + payload)));

    private static void FlushDurable(Stream stream) {
        if (stream is FileStream file) { file.Flush(flushToDisk: true); }
        else { stream.Flush(); }
    }

    private sealed record JournalLine(long Sequence, string PreviousHash, string Payload, string Hash);
}

/// <summary>Incremental operation projection for one branch. It never incorporates ancestor history prefixes.</summary>
internal sealed class PlaygroundOperationIndex {
    private readonly List<PlaygroundOperationEntry> _entries = [];
    private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);
    private readonly HashSet<(string BatchId, string ToolCallId)> _calls = [];
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<PlaygroundOperationEntry> _view;

    public PlaygroundOperationIndex() => _view = _entries.AsReadOnly();
    public IReadOnlyList<PlaygroundOperationEntry> Entries => _view;
    public IReadOnlyList<string> PendingOperationRecordIds => _pending.ToArray();
    public bool HasCall(string batchId, string toolCallId) => _calls.Contains((batchId, toolCallId));
    public bool TryGet(string recordId, out PlaygroundOperationEntry? entry) {
        if (_positions.TryGetValue(recordId, out int position)) { entry = _entries[position]; return true; }
        entry = null;
        return false;
    }

    public void Validate(PlaygroundHostRecord record) {
        if (record.Kind == "operation-received") {
            var received = record.ReadPayload<PlaygroundOperationReceipt>();
            PlaygroundOperationAudit.ValidateReceipt(received);
            if (record.ActorId != received.ActorId || record.ContextSequence != received.ContextSequence ||
                _positions.ContainsKey(received.OperationRecordId) || HasCall(received.BatchId, received.ToolCallId)) {
                throw new InvalidDataException("Duplicate or mismatched operation receipt in the host journal.");
            }
        } else {
            var completion = record.ReadPayload<PlaygroundOperationCompletion>();
            if (completion.OperationRecordId is null || !TryGet(completion.OperationRecordId, out var entry) ||
                entry!.Completed is not null || completion.Completed is not { } completed ||
                completed.ToolCallId != entry.Received.ToolCallId || record.ActorId != entry.Received.ActorId ||
                record.ContextSequence != entry.Received.ContextSequence || !Enum.IsDefined(completed.Status) || completed.Result is null) {
                throw new InvalidDataException("Invalid operation result pairing in the host journal.");
            }
        }
    }

    public void Install(PlaygroundHostRecord record) {
        if (record.Kind == "operation-received") {
            var received = record.ReadPayload<PlaygroundOperationReceipt>();
            _positions.Add(received.OperationRecordId, _entries.Count);
            _entries.Add(new(received, null));
            _calls.Add((received.BatchId, received.ToolCallId));
            _pending.Add(received.OperationRecordId);
        } else {
            var completion = record.ReadPayload<PlaygroundOperationCompletion>();
            int position = _positions[completion.OperationRecordId];
            _entries[position] = _entries[position] with { Completed = completion.Completed };
            _pending.Remove(completion.OperationRecordId);
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.CharacterGym;

internal sealed record LlmRawAuditRecord(int FormatVersion, long Sequence, string Kind, string RunId, JsonElement Payload);
internal sealed record LlmRawAuditSnapshot(string RunId, IReadOnlyList<LlmRawAuditRecord> Records, long DiscardedTailBytes);

/// <summary>Independent, lazily created full model evidence. It never saves host or game events.</summary>
/// <remarks>Only complete hash-chained lines are evidence. Flush succeeds before sequence installation.
/// Failed writes retire this writer; no retry or repair takes place during this run.</remarks>
internal sealed class LlmRawAudit : IDisposable {
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly string _runId;
    private readonly Func<Stream>? _openForTesting;
    private Stream? _stream;
    private long _sequence;
    private string _lastHash = "";
    private bool _disposed;
    private bool _faulted;

    public LlmRawAudit(string? path, string runId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _path = path is null ? null : Path.GetFullPath(path);
        _runId = runId;
    }

    private LlmRawAudit(Stream stream, string runId) : this((string?)null, runId) => _openForTesting = () => stream;

    internal static LlmRawAudit CreateForTesting(Stream stream, string runId) => new(stream, runId);

    public bool IsFaulted { get { lock (_gate) { return _faulted; } } }

    public long? Trace(LlmControllerTrace trace) {
        lock (_gate) { return TraceCore(trace); }
    }

    private long? TraceCore(LlmControllerTrace trace) {
        RequireUsable();
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.Kind == LlmControllerTraceKind.Feedback || _path is null && _openForTesting is null) { return null; }
        object payload;
        string kind;
        if (trace.Kind == LlmControllerTraceKind.Request) {
            kind = "model-request";
            payload = new {
                trace.RequestId, trace.CallNumber, trace.CharacterId, trace.ContextSequence,
                trace.ConnectionId, trace.ApiSpecId, trace.ModelId, trace.Request.PromptPrefix.SystemPrompt,
                OutputContract = GymMessageProjection.Contract(trace.Request.PromptPrefix.OutputContract),
                Shared = trace.Request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
                Tail = trace.Request.TailMessages.Select(GymMessageProjection.Message).ToArray()
            };
        } else {
            kind = trace.Kind == LlmControllerTraceKind.Response ? "model-response" : "model-failure";
            payload = new {
                trace.RequestId, trace.CallNumber, trace.CharacterId, trace.ContextSequence,
                trace.ConnectionId, trace.ApiSpecId, trace.ModelId,
                Stage = trace.Kind.ToString(), trace.ProtocolError, trace.Operation,
                Invocation = trace.Result?.Invocation, Termination = trace.Result?.Termination, Usage = trace.Result?.Usage,
                Errors = trace.Result?.Errors, Failure = trace.Result?.Failure,
                Blocks = trace.Result?.Message.Blocks.Select(block => GymMessageProjection.Runtime(block)).ToArray()
            };
        }
        if (_stream is null) {
            if (trace.Kind != LlmControllerTraceKind.Request) {
                throw new InvalidOperationException("Raw model evidence must start with a constructed request.");
            }
            EnsureOpened();
        }
        return Append(kind, payload);
    }

    /// <summary>Records physical lookahead evidence independently of actual character delivery.</summary>
    public long? RecordLookahead(string kind, object payload) {
        lock (_gate) {
            RequireUsable();
            if (!IsLookaheadKind(kind)) {
                throw new ArgumentException("Lookahead evidence kinds must start with lookahead- and include a name.", nameof(kind));
            }
            ArgumentNullException.ThrowIfNull(payload);
            if (_path is null && _openForTesting is null) { return null; }
            EnsureOpened();
            return Append("lookahead-event", new { Kind = kind, Data = payload });
        }
    }

    private static bool IsLookaheadKind(string? kind) => kind is not null &&
        kind.StartsWith("lookahead-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kind[10..]);

    private void EnsureOpened() {
        if (_stream is not null) { return; }
        try {
            if (_openForTesting is not null) { _stream = _openForTesting(); }
            else {
                Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
                _stream = new FileStream(_path!, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            Append("metadata", new { FormatVersion = 1, RunId = _runId, Purpose = "completion-raw-audit" });
        } catch {
            _faulted = true;
            throw;
        }
    }

    private long Append(string kind, object payload) {
        long sequence = checked(_sequence + 1);
        var record = new LlmRawAuditRecord(1, sequence, kind, _runId,
            JsonSerializer.SerializeToElement(payload, payload.GetType(), Json));
        string serialized = JsonSerializer.Serialize(record, Json);
        string hash = Hash(sequence, _lastHash, serialized);
        byte[] bytes = Utf8.GetBytes(JsonSerializer.Serialize(new Line(sequence, _lastHash, serialized, hash), Json) + "\n");
        try {
            _stream!.Write(bytes);
            if (_stream is FileStream file) { file.Flush(flushToDisk: true); }
            else { _stream.Flush(); }
        } catch {
            _faulted = true;
            throw;
        }
        _sequence = sequence;
        _lastHash = hash;
        return sequence;
    }

    /// <summary>Read-only validation for dedicated raw-audit tools. Ordinary character history does not call this.</summary>
    public static LlmRawAuditSnapshot Read(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var records = new List<LlmRawAuditRecord>();
        string lastHash = "";
        string? runId = null;
        long remaining = stream.Length;
        using var pending = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (remaining > 0) {
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) { throw new IOException("The raw audit changed length during reading."); }
            remaining -= count;
            for (int index = 0; index < count; index++) {
                if (buffer[index] != (byte)'\n') { pending.WriteByte(buffer[index]); continue; }
                try {
                    Line line = JsonSerializer.Deserialize<Line>(Utf8.GetString(pending.ToArray()), Json)
                        ?? throw new InvalidDataException("A raw audit line is null.");
                    if (line.Sequence != records.Count + 1L || line.PreviousHash != lastHash || line.Payload is null ||
                        line.Hash != Hash(line.Sequence, line.PreviousHash, line.Payload)) {
                        throw new InvalidDataException("A raw audit line has an invalid sequence or checksum.");
                    }
                    LlmRawAuditRecord record = JsonSerializer.Deserialize<LlmRawAuditRecord>(line.Payload, Json)
                        ?? throw new InvalidDataException("A raw audit record is null.");
                    if (record.FormatVersion != 1 || record.Sequence != line.Sequence || string.IsNullOrWhiteSpace(record.RunId) ||
                        record.Payload.ValueKind != JsonValueKind.Object ||
                        (records.Count == 0) != (record.Kind == "metadata") ||
                        record.Kind is not ("metadata" or "model-request" or "model-response" or "model-failure" or "lookahead-event")) {
                        throw new InvalidDataException("Invalid raw audit record metadata.");
                    }
                    if (record.Kind == "lookahead-event" &&
                        (!record.Payload.TryGetProperty("Kind", out JsonElement eventKind) ||
                         eventKind.ValueKind != JsonValueKind.String || !IsLookaheadKind(eventKind.GetString()) ||
                         !record.Payload.TryGetProperty("Data", out _))) {
                        throw new InvalidDataException("Invalid lookahead raw audit event.");
                    }
                    if (runId is null) {
                        runId = record.RunId;
                        if (!record.Payload.TryGetProperty("FormatVersion", out JsonElement version) || version.GetInt32() != 1 ||
                            !record.Payload.TryGetProperty("RunId", out JsonElement identity) || identity.GetString() != runId ||
                            !record.Payload.TryGetProperty("Purpose", out JsonElement purpose) || purpose.GetString() != "completion-raw-audit") {
                            throw new InvalidDataException("Invalid raw audit identity.");
                        }
                    } else if (record.RunId != runId) { throw new InvalidDataException("A raw audit mixes run identities."); }
                    records.Add(record);
                    lastHash = line.Hash;
                } catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException or InvalidOperationException or FormatException) {
                    throw new InvalidDataException("A completed raw audit line is corrupt.", error);
                }
                pending.SetLength(0);
            }
        }
        if (runId is null) { throw new InvalidDataException("The raw audit has no complete metadata record."); }
        return new(runId, records.AsReadOnly(), pending.Length);
    }

    private static string Hash(long sequence, string previousHash, string payload) => Convert.ToHexString(
        SHA256.HashData(Utf8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previousHash + "\n" + payload)));

    private void RequireUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("Raw audit writer is faulted; this run cannot retry it."); }
    }

    public void Dispose() {
        lock (_gate) { DisposeCore(); }
    }

    private void DisposeCore() {
        if (_disposed) { return; }
        _disposed = true;
        // A failed flush already determines the outcome. Disposal must not replace that original error.
        try { _stream?.Dispose(); }
        catch when (_faulted) { }
    }

    private sealed record Line(long Sequence, string PreviousHash, string Payload, string Hash);
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli;

/// <summary>Serial host writer. Failed writes have unknown outcome and retire this instance.</summary>
internal sealed class FeedbackJournal : IDisposable {
    internal sealed record Snapshot(IReadOnlyList<FeedbackRecord> Records, long IgnoredTailBytes);
    private static readonly JsonSerializerOptions Json = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true
    };
    private readonly string _path;
    private readonly Func<string, FileStream> _openStream;
    private FileStream? _stream;
    private bool _retired;
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    private FeedbackJournal(string path, Func<string, FileStream>? openStream = null) {
        _path = Path.GetFullPath(path);
        _openStream = openStream ?? (static path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read));
    }

    public static FeedbackJournal Open(string path) => new(path);
    internal static FeedbackJournal OpenForTesting(string path, Func<string, FileStream> openStream) => new(path, openStream);

    /// <summary>Ignores an unterminated tail; never creates, truncates or repairs a file.</summary>
    public static IReadOnlyList<FeedbackRecord> Read(string path) => ReadCore(path).Records;
    public static Snapshot ReadSnapshot(string path) {
        var parsed = ReadCore(path);
        return new(parsed.Records, parsed.TailBytes);
    }

    private static (IReadOnlyList<FeedbackRecord> Records, long CompleteLength, long TailBytes) ReadCore(string path) {
        if (!File.Exists(path)) { return (Array.Empty<FeedbackRecord>(), 0, 0); }
        // Share write access with the serial host's already-open append handle.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        var records = new List<FeedbackRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int start = 0;
        for (int i = 0; i < bytes.Length; i++) {
            if (bytes[i] != (byte)'\n') { continue; }
            try {
                FeedbackRecord record = JsonSerializer.Deserialize<FeedbackRecord>(bytes.AsSpan(start, i - start), Json)
                    ?? throw new InvalidDataException("Null feedback record.");
                Validate(record);
                if (!ids.Add(record.FeedbackId)) { throw new InvalidDataException("Duplicate feedback ID."); }
                records.Add(record);
            } catch (Exception error) when (error is JsonException or ArgumentException or FormatException) {
                throw new InvalidDataException($"Invalid complete feedback line {records.Count + 1}.", error);
            }
            start = i + 1;
        }
        return (records.AsReadOnly(), start, bytes.Length - start);
    }

    public string Append(FeedbackRecord record) {
        ObjectDisposedException.ThrowIf(_retired, this);
        Validate(record);
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        try {
            if (_stream is null) {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                _stream = _openStream(_path);
                var existing = ReadCore(_path);
                foreach (FeedbackRecord item in existing.Records) { _ids.Add(item.FeedbackId); }
                _stream.SetLength(existing.CompleteLength);
                _stream.Position = existing.CompleteLength;
            }
            if (!_ids.Add(record.FeedbackId)) { throw new InvalidDataException("Duplicate feedback ID."); }
            _stream.Write(line);
            _stream.WriteByte((byte)'\n');
            _stream.Flush(flushToDisk: true);
            return record.FeedbackId;
        } catch {
            _retired = true;
            FileStream? stream = _stream;
            _stream = null;
            try { stream?.Dispose(); }
            catch { /* Preserve the original append failure, including its stack. */ }
            throw;
        }
    }

    private static void Validate(FeedbackRecord record) {
        if (record.Version != 1 || !Guid.TryParseExact(record.FeedbackId, "N", out _) ||
            string.IsNullOrWhiteSpace(record.RunId) || string.IsNullOrWhiteSpace(record.ActorId) ||
            string.IsNullOrWhiteSpace(record.Text) || record.Text.Length > FeedbackRecord.MaxTextCharacters || record.CreatedAt == default || record.ContextSequence <= 0 ||
            !Enum.IsDefined(record.Source) || (record.Source == FeedbackSource.Llm) != (record.Model is not null)) {
            throw new InvalidDataException("Invalid feedback metadata.");
        }
        if (record.Model is { } model && (string.IsNullOrWhiteSpace(model.RequestId) || model.CallNumber <= 0 || string.IsNullOrWhiteSpace(model.ToolCallId))) {
            throw new InvalidDataException("Invalid feedback model metadata.");
        }
        if (record.Boundary is { } boundary) {
            PlaygroundDurableHistory.ParseBranchId(boundary.BranchId);
            PlaygroundDurableHistory.ParseCommitId(boundary.StateCommitId);
            if (boundary.ActorId != record.ActorId || boundary.ContextSequence != record.ContextSequence ||
                boundary.OperationSequence < 0 || boundary.TransitionCount < 0 || boundary.GameRevision <= 0) {
                throw new InvalidDataException("Invalid feedback decision boundary.");
            }
        }
    }

    public void Dispose() {
        _retired = true;
        FileStream? stream = _stream;
        _stream = null;
        stream?.Dispose();
    }
}

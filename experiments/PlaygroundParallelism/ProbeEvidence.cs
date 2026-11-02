using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterGym;
using DramaBoard.Playground;

namespace DramaBoard.PlaygroundParallelism;

internal sealed class ProbeEvidence : IDisposable {
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly Action<string>? _beforeFlush;
    private bool _faulted;
    private long _sequence;
    private readonly Dictionary<string, (long Count, long Ticks, long Bytes)> _metrics = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public static readonly JsonSerializerOptions Options = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new VisibleValueConverter() }
    };
    public double ElapsedMilliseconds => _clock.Elapsed.TotalMilliseconds;
    public bool IsFaulted { get { lock (_gate) { return _faulted; } } }
    public ProbeEvidence(string path, Action<string>? beforeFlush = null) {
        _beforeFlush = beforeFlush;
        _writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }
    public T Measure<T>(string phase, Func<T> operation) {
        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        try { return operation(); }
        finally {
            long ticks = Stopwatch.GetTimestamp() - started;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            lock (_gate) {
                _metrics.TryGetValue(phase, out var previous);
                _metrics[phase] = (previous.Count + 1, previous.Ticks + ticks, previous.Bytes + bytes);
            }
        }
    }
    public void Measure(string phase, Action operation) => Measure(phase, () => { operation(); return 0; });
    public object MetricsSnapshot() {
        lock (_gate) {
            return _metrics.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key,
                pair => new { Calls = pair.Value.Count, Milliseconds = pair.Value.Ticks * 1000d / Stopwatch.Frequency,
                    AllocatedBytes = pair.Value.Bytes }, StringComparer.Ordinal);
        }
    }
    public string Fingerprint(string value) => Measure("fingerprint", () => Hash(value));
    public void Write(string kind, object detail) => Measure("evidence-write", () => WriteCore(kind, detail));
    private void WriteCore(string kind, object detail) {
        lock (_gate) {
            if (_faulted) { throw new InvalidOperationException("Evidence writer is retired after an uncertain append outcome."); }
            try {
                _writer.WriteLine(JsonSerializer.Serialize(new { Sequence = ++_sequence, ElapsedMilliseconds, Kind = kind, Detail = detail }, Options));
                _beforeFlush?.Invoke(kind);
                _writer.Flush();
            } catch { _faulted = true; throw; }
        }
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static string Request(CompletionRequest request) => JsonSerializer.Serialize(new {
        request.ModelId, request.PromptPrefix.SystemPrompt,
        Contract = GymMessageProjection.Contract(request.PromptPrefix.OutputContract),
        Shared = request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
        Tail = request.TailMessages.Select(GymMessageProjection.Message).ToArray()
    }, Options);
    public static object Result(CompletionResult result) => new {
        Message = GymMessageProjection.Message(result.Message), result.Invocation, result.Termination,
        result.Usage, result.Failure, result.Errors,
        Reasoning = result.Message.Blocks.OfType<ActionBlock.ReasoningBlock>()
            .Select(block => new { block.Origin, HasPlainText = !string.IsNullOrEmpty(block.PlainText) }).ToArray()
    };
    public static JsonElement Fact(PlaygroundFact fact) => JsonSerializer.SerializeToElement(fact, Options);
    public static void SaveSummary(string output, object value) => File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(value, Options));
    public void Dispose() {
        // Do not ask StreamWriter to replay its buffer after an uncertain write/flush failure.
        if (_faulted) { _writer.BaseStream.Dispose(); } else { _writer.Dispose(); }
    }
    private sealed class VisibleValueConverter : JsonConverter<PlaygroundVisibleValue> {
        public override PlaygroundVisibleValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Write-only diagnostic projection.");
        public override void Write(Utf8JsonWriter writer, PlaygroundVisibleValue value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new { value.Kind, value.Units, value.Scale, value.Unit, value.Text, value.Boolean }, options);
    }
}

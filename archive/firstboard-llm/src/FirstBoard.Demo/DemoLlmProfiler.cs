using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DramaBoard.Player.Llm;

namespace DramaBoard.FirstBoard.Demo;

internal sealed record DemoLlmCallDescriptor(
    string ActorId,
    string Purpose,
    string? ShardKey,
    string Backend,
    string Model,
    string ThinkingEffort);

internal sealed class DemoLlmProfiler
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _sync = new();
    private readonly long _profileStarted = Stopwatch.GetTimestamp();
    private readonly string _jsonlPath;
    private readonly string _summaryPath;
    private readonly List<DemoLlmCallTrace> _traces = [];
    private long _nextCallId;

    public DemoLlmProfiler(string outputDirectory)
    {
        _jsonlPath = Path.Combine(outputDirectory, "llm-runtime.jsonl");
        _summaryPath = Path.Combine(outputDirectory, "llm-runtime-summary.md");
        File.WriteAllText(_jsonlPath, string.Empty, Utf8NoBom);
    }

    public ILlmChatBackend Wrap(ILlmChatBackend inner, DemoLlmCallDescriptor descriptor) =>
        new ProfiledBackend(this, inner, descriptor);

    public void WriteSummary(DemoOptions options)
    {
        DemoLlmCallTrace[] traces;
        lock (_sync)
        {
            traces = [.. _traces.OrderBy(trace => trace.CallId)];
        }

        var text = new StringBuilder()
            .AppendLine("# LLM runtime profile")
            .AppendLine()
            .Append("- Memory maintenance: ").AppendLine(
                options.MemoryMaintenanceMode.ToString().ToLowerInvariant())
            .Append("- Calls: ").Append(traces.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" (success ").Append(traces.Count(trace => trace.Status == "success"))
            .Append(", cancelled ").Append(traces.Count(trace => trace.Status == "cancelled"))
            .Append(", error ").Append(traces.Count(trace => trace.Status == "error"))
            .AppendLine(")");

        if (traces.Length == 0)
        {
            text.AppendLine("- No completed calls were observed.");
            File.WriteAllText(_summaryPath, text.ToString(), Utf8NoBom);
            return;
        }

        double wallMs = traces.Max(trace => trace.EndOffsetMs) -
            traces.Min(trace => trace.StartOffsetMs);
        double summedMs = traces.Sum(trace => trace.TotalMs);
        text.Append("- Observed call wall span: ").Append(FormatMs(wallMs)).AppendLine()
            .Append("- Summed call time: ").Append(FormatMs(summedMs)).AppendLine()
            .Append("- Call-time / wall-span overlap factor: ").Append(
                wallMs <= 0
                    ? "n/a"
                    : (summedMs / wallMs).ToString("0.00", CultureInfo.InvariantCulture))
            .AppendLine()
            .Append("- Peak concurrent calls: ").AppendLine(
                PeakConcurrency(traces).ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .AppendLine("| scope | calls | mean | p50 | p95 | max | mean queue | mean service | prompt tokens | completion tokens | reasoning tokens | cache read | cache miss |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (IGrouping<string, DemoLlmCallTrace> group in traces.GroupBy(Scope))
        {
            DemoLlmCallTrace[] calls = [.. group.OrderBy(trace => trace.TotalMs)];
            text.Append("| ").Append(group.Key)
                .Append(" | ").Append(calls.Length)
                .Append(" | ").Append(FormatMs(calls.Average(trace => trace.TotalMs)))
                .Append(" | ").Append(FormatMs(Percentile(calls, 0.50)))
                .Append(" | ").Append(FormatMs(Percentile(calls, 0.95)))
                .Append(" | ").Append(FormatMs(calls[^1].TotalMs))
                .Append(" | ").Append(Average(calls, trace => trace.QueueMs))
                .Append(" | ").Append(Average(calls, trace => trace.ServiceMs))
                .Append(" | ").Append(Sum(calls, trace => trace.PromptTokens))
                .Append(" | ").Append(Sum(calls, trace => trace.CompletionTokens))
                .Append(" | ").Append(Sum(calls, trace => trace.ReasoningTokens))
                .Append(" | ").Append(Sum(calls, trace => trace.CacheReadTokens))
                .Append(" | ").Append(Sum(calls, trace => trace.CacheMissTokens))
                .AppendLine(" |");
        }

        text.AppendLine()
            .AppendLine("The JSONL file is written after every call, so cancelled runs retain partial measurements.");
        File.WriteAllText(_summaryPath, text.ToString(), Utf8NoBom);
    }

    private async Task<LlmChatResponse> CompleteAsync(
        ILlmChatBackend inner,
        DemoLlmCallDescriptor descriptor,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        long callId = Interlocked.Increment(ref _nextCallId);
        double startOffsetMs = Stopwatch.GetElapsedTime(_profileStarted).TotalMilliseconds;
        long started = Stopwatch.GetTimestamp();
        try
        {
            LlmChatResponse response = await inner.CompleteAsync(request, cancellationToken);
            Record(new DemoLlmCallTrace(
                callId,
                descriptor,
                startOffsetMs,
                Stopwatch.GetElapsedTime(_profileStarted).TotalMilliseconds,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                response.QueueDuration?.TotalMilliseconds,
                response.ServiceDuration?.TotalMilliseconds,
                request.System.Length,
                request.User.Length,
                response.Content.Length,
                "success",
                ErrorType: null,
                response.Usage?.PromptTokens,
                response.Usage?.CompletionTokens,
                response.Usage?.TotalTokens,
                response.Usage?.ReasoningTokens,
                response.Usage?.CacheReadTokens,
                response.Usage?.CacheMissTokens));
            return response;
        }
        catch (Exception exception)
        {
            Record(new DemoLlmCallTrace(
                callId,
                descriptor,
                startOffsetMs,
                Stopwatch.GetElapsedTime(_profileStarted).TotalMilliseconds,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                QueueMs: null,
                ServiceMs: null,
                request.System.Length,
                request.User.Length,
                CompletionChars: 0,
                exception is OperationCanceledException ? "cancelled" : "error",
                exception.GetType().Name,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                ReasoningTokens: null,
                CacheReadTokens: null,
                CacheMissTokens: null));
            throw;
        }
    }

    private void Record(DemoLlmCallTrace trace)
    {
        lock (_sync)
        {
            _traces.Add(trace);
            File.AppendAllText(
                _jsonlPath,
                JsonSerializer.Serialize(trace, JsonOptions) + Environment.NewLine,
                Utf8NoBom);
        }
    }

    private static string Scope(DemoLlmCallTrace trace) =>
        $"{trace.Descriptor.ActorId}/{trace.Descriptor.Purpose}" +
        (trace.Descriptor.ShardKey is null ? string.Empty : $"/{trace.Descriptor.ShardKey}") +
        $" ({trace.Descriptor.Backend}/{trace.Descriptor.Model}; {trace.Descriptor.ThinkingEffort})";

    private static string FormatMs(double milliseconds) =>
        $"{milliseconds.ToString("0", CultureInfo.InvariantCulture)}ms";

    private static double Percentile(DemoLlmCallTrace[] sorted, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(sorted.Length * percentile) - 1,
            0,
            sorted.Length - 1);
        return sorted[index].TotalMs;
    }

    private static string Sum(
        IEnumerable<DemoLlmCallTrace> traces,
        Func<DemoLlmCallTrace, long?> selector)
    {
        long? sum = null;
        foreach (DemoLlmCallTrace trace in traces)
        {
            if (selector(trace) is long value)
            {
                sum = checked((sum ?? 0) + value);
            }
        }

        return sum?.ToString(CultureInfo.InvariantCulture) ?? "-";
    }

    private static string Average(
        IEnumerable<DemoLlmCallTrace> traces,
        Func<DemoLlmCallTrace, double?> selector)
    {
        double[] values = [.. traces.Select(selector).Where(value => value.HasValue).Select(value => value!.Value)];
        return values.Length == 0 ? "-" : FormatMs(values.Average());
    }

    private static int PeakConcurrency(IEnumerable<DemoLlmCallTrace> traces)
    {
        var edges = traces
            .SelectMany(trace => new[]
            {
                (Time: trace.StartOffsetMs, Delta: 1),
                (Time: trace.EndOffsetMs, Delta: -1),
            })
            .OrderBy(edge => edge.Time)
            .ThenBy(edge => edge.Delta);
        int active = 0;
        int peak = 0;
        foreach ((double _, int delta) in edges)
        {
            active += delta;
            peak = Math.Max(peak, active);
        }

        return peak;
    }

    private sealed class ProfiledBackend : ILlmChatBackend
    {
        private readonly DemoLlmProfiler _owner;
        private readonly ILlmChatBackend _inner;
        private readonly DemoLlmCallDescriptor _descriptor;

        public ProfiledBackend(
            DemoLlmProfiler owner,
            ILlmChatBackend inner,
            DemoLlmCallDescriptor descriptor)
        {
            _owner = owner;
            _inner = inner;
            _descriptor = descriptor;
        }

        public Task<LlmChatResponse> CompleteAsync(
            LlmChatRequest request,
            CancellationToken cancellationToken) =>
            _owner.CompleteAsync(_inner, _descriptor, request, cancellationToken);
    }

    private sealed record DemoLlmCallTrace(
        long CallId,
        DemoLlmCallDescriptor Descriptor,
        double StartOffsetMs,
        double EndOffsetMs,
        double TotalMs,
        double? QueueMs,
        double? ServiceMs,
        int SystemChars,
        int UserChars,
        int CompletionChars,
        string Status,
        string? ErrorType,
        long? PromptTokens,
        long? CompletionTokens,
        long? TotalTokens,
        long? ReasoningTokens,
        long? CacheReadTokens,
        long? CacheMissTokens);
}

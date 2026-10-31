using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace DramaBoard.CharacterControl.Completion;

/// <summary>A game-independent, bounded act/feedback tool loop over one frozen character input.</summary>
/// <remarks>Only operational counters survive calls. Native assistant/tool-result history is local to
/// one decision and is discarded before another actor is served. No world access or game validation is
/// performed here. Sequential use is required; the injected Completion client is borrowed.</remarks>
public sealed class LlmDecisionLoop {
    public const string ToolName = "act";
    public const string FeedbackToolName = "report-feedback";
    private const string PromptOpening = "你在一个持续运行的游戏中控制当前角色。仅使用当前角色可知的观察、保留经历和明确提供的角色记录；不要假定未提供的地图或资源。";
    private const string ActProtocol = "每次调用都是新的独立决策，请根据提供的完整角色上下文行动。调用一次 act 工具，command 为界面列出的一个游戏行动字符串。";
    private const string FeedbackProtocol = "每次角色服务从提供的完整角色上下文开始。每次响应只调用一个工具：可先用 report-feedback 报告问题或需求，收到回执后继续；最终调用一次 act，command 为界面列出的一个游戏行动字符串。";
    private readonly ICompletionClient _client;
    private readonly string _modelId;
    private readonly LlmControllerOptions _options;
    private readonly Action<LlmControllerTrace>? _trace;
    private readonly Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? _feedbackSink;
    private readonly CompletionOutputContract _outputContract;
    private int _active;
    private int _consecutiveProtocolFailures;
    public int CallsMade { get; private set; }

    public LlmDecisionLoop(ICompletionClient client, string modelId, LlmControllerOptions? options = null,
        Action<LlmControllerTrace>? trace = null,
        Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? feedbackSink = null) {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _modelId = modelId;
        _options = options ?? new();
        _options.Validate();
        _trace = trace;
        _feedbackSink = feedbackSink;
        var tool = new ToolDefinition(ToolName, "提交当前角色的一条游戏行动；由游戏权威解析与校验。",
            new ToolSchema.Object([new("command", new ToolSchema.Value(ToolParamType.String,
                maxLength: _options.MaxCommandCharacters), isRequired: true)]));
        // Codex supports Auto; enforce exactly one call locally even if a provider ignores the parallel hint.
        var tools = new List<ToolDefinition> { tool };
        if (feedbackSink is not null) {
            tools.Add(new(FeedbackToolName, "向开发者报告遇到的障碍或所需支持。不是世界行动；收到保存回执后继续选择 act。kind 只能为 issue 或 feature-request。",
                new ToolSchema.Object([
                    new("kind", new ToolSchema.Value(ToolParamType.String, stringEnumValues: ["issue", "feature-request"], maxLength: 15), isRequired: true),
                    new("summary", new ToolSchema.Value(ToolParamType.String, maxLength: _options.MaxFeedbackCharacters), isRequired: true),
                    new("obstacle", new ToolSchema.Value(ToolParamType.String, maxLength: _options.MaxFeedbackCharacters), isRequired: true),
                    new("desired_support", new ToolSchema.Value(ToolParamType.String, maxLength: _options.MaxFeedbackCharacters), isRequired: false)
                ])));
        }
        _outputContract = new([.. tools], CompletionToolChoice.Auto, allowParallelToolCalls: false);
    }

    public async ValueTask<CommandSubmission> ChooseCommandAsync(LlmDecisionInput context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) {
            throw new InvalidOperationException("The LLM controller requires sequential invocation.");
        }
        try {
            string material = context.Material;
            if (material.Length > _options.MaxContextCharacters) { throw Pause("context-too-large"); }
            string? protocolError = null;
            var tail = new List<IHistoryMessage>();
            var feedbackCallIds = new HashSet<string>(StringComparer.Ordinal);
            long tailCharacters = 0;
            int feedbackCount = 0;
            for (;;) {
                cancellationToken.ThrowIfCancellationRequested();
                if (CallsMade >= _options.MaxCalls) { throw Pause("call-budget-exhausted"); }
                if (_consecutiveProtocolFailures >= _options.MaxConsecutiveProtocolFailures) { throw Pause("protocol-failure-limit"); }
                // The correction is host protocol feedback, not a fabricated game action or tool result.
                string correction = _feedbackSink is null
                    ? $"上次响应未成为游戏操作（协议错误：{protocolError}）。请只调用一次 act，参数仅含字符串 command。"
                    : $"上次响应未被执行（协议错误：{protocolError}）。每次响应只调用一个已提供的工具，参数须符合其 schema；反馈额度耗尽时请调用 act。";
                IHistoryMessage[] messages = protocolError is null || _feedbackSink is not null
                    ? [new ObservationMessage(material)]
                    : [new ObservationMessage(material), new ObservationMessage(correction)];
                if (material.Length + tailCharacters + (protocolError is null ? 0 : correction.Length) > _options.MaxContextCharacters) {
                    throw Pause("context-too-large");
                }
                string prompt = PromptOpening + (_feedbackSink is null ? ActProtocol : FeedbackProtocol) + context.GameGuidance;
                if (_feedbackSink is not null) {
                    prompt += $"本次角色服务可选用 report-feedback 向开发者报告 issue 或 feature-request，最多 {_options.MaxFeedbackPerDecision} 条，字段总长不超过 {_options.MaxFeedbackCharacters} 字符。"
                        + "每次响应恰好调用一个工具。反馈不改变世界、不成为角色记忆；收到保存回执后继续，最终调用 act。无需每回合提交反馈。";
                }
                IHistoryMessage[] requestTail = protocolError is not null && _feedbackSink is not null
                    ? [.. tail, new ObservationMessage(correction)] : tail.ToArray();
                var request = new CompletionRequest(_modelId, new(prompt, _outputContract, messages), requestTail);
                int call = ++CallsMade;
                string requestId = Guid.NewGuid().ToString("N");
                void Trace(LlmControllerTraceKind kind, CompletionResult? result = null,
                    string? error = null, CommandSubmission? operation = null, string? feedbackId = null) =>
                    _trace?.Invoke(new(kind, call, context.CharacterId, context.ContextSequence,
                        request, result, error, operation, requestId, feedbackId));
                Trace(LlmControllerTraceKind.Request);
                CompletionResult result;
                try {
                    result = await _client.StreamCompletionAsync(request, observer: null, cancellationToken).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    Trace(LlmControllerTraceKind.Failure, error: "cancelled");
                    throw;
                } catch (CompletionFailureException exception) {
                    string code = exception.Failure.Kind == CompletionFailureKind.Http ? "http-failure" : "transport-failure";
                    Trace(LlmControllerTraceKind.Failure, error: code);
                    throw Pause(code, exception);
                } catch (CompletionRequestRejectedException exception) {
                    Trace(LlmControllerTraceKind.Failure, error: "request-rejected");
                    throw Pause("request-rejected", exception);
                } catch (Exception exception) when (exception is InvalidDataException or JsonException or NotSupportedException) {
                    Trace(LlmControllerTraceKind.Failure, error: "completion-protocol-failure");
                    throw Pause("completion-protocol-failure", exception);
                } catch (Exception) {
                    // Unknown failures are not guessed to be retryable or translated into game rejections.
                    Trace(LlmControllerTraceKind.Failure, error: "client-failure");
                    throw;
                }
                if (cancellationToken.IsCancellationRequested) {
                    Trace(LlmControllerTraceKind.Response, result, "cancelled");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (result.Termination.Kind != CompletionTerminationKind.Completed) {
                    string code = result.Termination.Kind == CompletionTerminationKind.Incomplete ? "completion-incomplete" : "completion-failed";
                    Trace(LlmControllerTraceKind.Response, result, code);
                    throw Pause(code);
                }
                protocolError = Validate(result, out RawToolCall? toolCall, out string? command, out FeedbackFields? feedback);
                if (protocolError is null && feedbackCallIds.Contains(toolCall!.ToolCallId)) { protocolError = "duplicate-call-id"; }
                if (protocolError is null && feedback is not null && feedbackCount >= _options.MaxFeedbackPerDecision) {
                    protocolError = "feedback-budget-exhausted";
                }
                if (protocolError is null) {
                    _consecutiveProtocolFailures = 0;
                    if (feedback is not null) {
                        Trace(LlmControllerTraceKind.Response, result);
                        string receipt;
                        try {
                            receipt = await _feedbackSink!(new(requestId, call, context.CharacterId,
                                context.ContextSequence, toolCall!.ToolCallId, feedback.Kind, feedback.Summary,
                                feedback.Obstacle, feedback.DesiredSupport), cancellationToken).ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(receipt) || receipt.Length > 256) {
                                throw new InvalidDataException("The feedback sink returned an invalid receipt identity.");
                            }
                        } catch (OperationCanceledException) {
                            Trace(LlmControllerTraceKind.Failure, error: "feedback-cancelled");
                            throw;
                        } catch (Exception exception) {
                            Trace(LlmControllerTraceKind.Failure, error: "feedback-save-failed");
                            throw Pause("feedback-save-failed", exception);
                        }
                        Trace(LlmControllerTraceKind.Feedback, feedbackId: receipt);
                        cancellationToken.ThrowIfCancellationRequested();
                        string content = JsonSerializer.Serialize(new { saved = true, feedback_id = receipt,
                            message = "反馈已保存；未改变世界。请继续选择行动。" });
                        var response = new ToolResultsMessage(null, [ToolResult.FromText(FeedbackToolName,
                            toolCall!.ToolCallId, ToolExecutionStatus.Success, content)]);
                        tail.Add(result.Message);
                        tail.Add(response);
                        // Count full native action blocks (including opaque replay data), not only flattened text.
                        tailCharacters += result.Message.Blocks.Sum(block => (long)JsonSerializer.Serialize(block, block.GetType()).Length)
                            + content.Length + toolCall.ToolCallId.Length + FeedbackToolName.Length;
                        feedbackCallIds.Add(toolCall.ToolCallId);
                        feedbackCount++;
                        continue;
                    }
                    var operation = new CommandSubmission(toolCall!.ToolCallId, command!, Guid.NewGuid().ToString("N"));
                    Trace(LlmControllerTraceKind.Response, result, operation: operation);
                    return operation;
                }
                _consecutiveProtocolFailures++;
                Trace(LlmControllerTraceKind.Response, result, protocolError);
            }
        } finally { Volatile.Write(ref _active, 0); }
    }

    private LlmControllerPausedException Pause(string reason, Exception? inner = null) => new(reason, CallsMade, inner);

    private sealed record FeedbackFields(string Kind, string Summary, string Obstacle, string? DesiredSupport);

    private string? Validate(CompletionResult result, out RawToolCall? call, out string? command, out FeedbackFields? feedback) {
        call = null;
        command = null;
        feedback = null;
        var calls = result.Message.ToolCalls;
        if (calls.Count != 1) { return "expected-single-call"; }
        call = calls[0];
        bool isFeedback = call.ToolName == FeedbackToolName && _feedbackSink is not null;
        if (call.ToolName != ToolName && !isFeedback) { return "unexpected-tool"; }
        if (string.IsNullOrWhiteSpace(call.ToolCallId)) { return "missing-call-id"; }
        if (call.InputKind != ToolInputKind.JsonObject) { return "expected-json-input"; }
        // Permit worst-case JSON escaping while bounding parsing allocation and whitespace abuse.
        int limit = isFeedback ? _options.MaxFeedbackCharacters : _options.MaxCommandCharacters;
        if (call.RawInput.Length > limit * 6 + 1024) { return "arguments-too-large"; }
        try {
            using JsonDocument document = JsonDocument.Parse(call.RawInput, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { return "expected-object"; }
            JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
            if (isFeedback) {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonProperty property in properties) {
                    if (property.Name is not ("kind" or "summary" or "obstacle" or "desired_support") ||
                        property.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(property.Name, property.Value.GetString()!)) {
                        return "invalid-feedback-fields";
                    }
                }
                if (!fields.TryGetValue("kind", out string? kind) || kind is not ("issue" or "feature-request") ||
                    !fields.TryGetValue("summary", out string? summary) || string.IsNullOrWhiteSpace(summary) ||
                    !fields.TryGetValue("obstacle", out string? obstacle) || string.IsNullOrWhiteSpace(obstacle)) {
                    return "invalid-feedback-fields";
                }
                if (fields.Values.Sum(value => (long)value.Length) > limit) { return "feedback-too-large"; }
                feedback = new(kind, summary, obstacle, fields.GetValueOrDefault("desired_support"));
                return null;
            }
            if (properties.Length != 1 || properties[0].Name != "command" || properties[0].Value.ValueKind != JsonValueKind.String) {
                return "expected-command-string";
            }
            command = properties[0].Value.GetString()!;
            return command.Length > _options.MaxCommandCharacters ? "command-too-large" : null;
        } catch (JsonException) { return "invalid-json"; }
    }
}

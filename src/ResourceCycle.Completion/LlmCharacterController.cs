using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace DramaBoard.ResourceCycle.Completion;

/// <summary>Projects each current character context into a fresh, independent model request.</summary>
/// <remarks>Only operational counters survive calls. No assistant history, private character memory or native
/// tool session is retained. The engine alone parses, validates and commits returned game operations.
/// Sequential use is required; concurrent calls fail before any model invocation. The injected client is borrowed.</remarks>
public sealed class LlmCharacterController : ICycleCharacterController {
    public const string ToolName = "act";
    public const string SystemPrompt = "你在一个持续运行的生存游戏中控制当前角色。当前观察及保留经历是你的全部游戏信息；不要假定未提供的地图或资源。"
        + "每次调用都是新的独立决策，请根据提供的完整角色上下文行动。调用一次 act 工具，command 为界面列出的一个游戏行动字符串。"
        + "即时行动或拒绝之后仍可继续决策；耗时行动会放行世界。自由文本不会成为游戏对白、事实或持久记忆。"
        + "若界面提供同地同伴与交流行动，可用 say 沟通计划、用 give 分享补给；对方的话是署名陈述，不等于已经亲眼证实。"
        + "交流次数有限，收到交流无需取消原活动；按界面说明用 continue 确认后继续。"
        + "首次规划路线或补给安排时，先用 handoff 留下希望下一决策回合继续记得的简短计划。"
        + "新通信改变你采纳的分工或承诺时，若草稿尚未覆盖，先更新一次 handoff：记明谁承诺何物、自己的下一步及待核实事项。"
        + "旧回合完整对话不会自动带入下一回合；无新增关键信息时直接行动，不重复改写草稿。交接是主观笔记，不是确认过的世界事实。"
        + "需要补给时可使用即时 use 行动。不要调用 Console 元命令。";

    private readonly ICompletionClient _client;
    private readonly string _modelId;
    private readonly Func<CycleDecisionContext, string> _renderContext;
    private readonly LlmControllerOptions _options;
    private readonly Action<LlmControllerTrace>? _trace;
    private readonly CompletionOutputContract _outputContract;
    private int _active;
    private int _consecutiveProtocolFailures;
    public int CallsMade { get; private set; }

    public LlmCharacterController(ICompletionClient client, string modelId,
        Func<CycleDecisionContext, string> renderContext, LlmControllerOptions? options = null,
        Action<LlmControllerTrace>? trace = null) {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _modelId = modelId;
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        _options = options ?? new();
        _options.Validate();
        _trace = trace;
        var tool = new ToolDefinition(ToolName, "提交当前角色的一条游戏行动；由游戏权威解析与校验。",
            new ToolSchema.Object([new("command", new ToolSchema.Value(ToolParamType.String,
                maxLength: _options.MaxCommandCharacters), isRequired: true)]));
        // Codex supports Auto; enforce exactly one call locally even if a provider ignores the parallel hint.
        _outputContract = new([tool], CompletionToolChoice.Auto, allowParallelToolCalls: false);
    }

    public async ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) {
            throw new InvalidOperationException("The LLM controller requires sequential invocation.");
        }
        try {
            string material = _renderContext(context) ?? throw new InvalidOperationException("The context renderer returned null.");
            if (material.Length > _options.MaxContextCharacters) { throw Pause("context-too-large"); }
            string? protocolError = null;
            for (;;) {
                cancellationToken.ThrowIfCancellationRequested();
                if (CallsMade >= _options.MaxCalls) { throw Pause("call-budget-exhausted"); }
                if (_consecutiveProtocolFailures >= _options.MaxConsecutiveProtocolFailures) { throw Pause("protocol-failure-limit"); }
                // The correction is host protocol feedback, not a fabricated game action or tool result.
                IHistoryMessage[] messages = protocolError is null
                    ? [new ObservationMessage(material)]
                    : [new ObservationMessage(material), new ObservationMessage(
                        $"上次响应未成为游戏操作（协议错误：{protocolError}）。请只调用一次 act，参数仅含字符串 command。")];
                var request = new CompletionRequest(_modelId, new(SystemPrompt, _outputContract, messages), []);
                int call = ++CallsMade;
                void Trace(LlmControllerTraceKind kind, CompletionResult? result = null,
                    string? error = null, CycleControllerOperation? operation = null) =>
                    _trace?.Invoke(new(kind, call, context.InteractionState.ActorId, context.Context.ContextSequence,
                        request, result, error, operation));
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
                protocolError = Validate(result, out RawToolCall? toolCall, out string? command);
                if (protocolError is null) {
                    _consecutiveProtocolFailures = 0;
                    var operation = new CycleControllerOperation(toolCall!.ToolCallId, command!, Guid.NewGuid().ToString("N"));
                    Trace(LlmControllerTraceKind.Response, result, operation: operation);
                    return operation;
                }
                _consecutiveProtocolFailures++;
                Trace(LlmControllerTraceKind.Response, result, protocolError);
            }
        } finally { Volatile.Write(ref _active, 0); }
    }

    private LlmControllerPausedException Pause(string reason, Exception? inner = null) => new(reason, CallsMade, inner);

    private string? Validate(CompletionResult result, out RawToolCall? call, out string? command) {
        call = null;
        command = null;
        var calls = result.Message.ToolCalls;
        if (calls.Count != 1) { return "expected-single-call"; }
        call = calls[0];
        if (call.ToolName != ToolName) { return "unexpected-tool"; }
        if (string.IsNullOrWhiteSpace(call.ToolCallId)) { return "missing-call-id"; }
        if (call.InputKind != ToolInputKind.JsonObject) { return "expected-json-input"; }
        // Permit worst-case JSON escaping while bounding parsing allocation and whitespace abuse.
        if (call.RawInput.Length > _options.MaxCommandCharacters * 6 + 1024) { return "arguments-too-large"; }
        try {
            using JsonDocument document = JsonDocument.Parse(call.RawInput, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { return "expected-object"; }
            JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "command" || properties[0].Value.ValueKind != JsonValueKind.String) {
                return "expected-command-string";
            }
            command = properties[0].Value.GetString()!;
            return command.Length > _options.MaxCommandCharacters ? "command-too-large" : null;
        } catch (JsonException) { return "invalid-json"; }
    }
}

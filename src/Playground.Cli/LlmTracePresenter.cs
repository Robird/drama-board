using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Playground;

/// <summary>Console observation of a host run. File ownership and audit failures belong to other components.</summary>
internal sealed class LlmTracePresenter(TextWriter output, bool showModelDetails = true) {
    private static readonly JsonSerializerOptions Json = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    public TextWriter Output { get; } = output;
    public bool ShowModelDetails { get; } = showModelDetails;

    public void Write(string kind, object payload, string? summary = null) {
        // Mixed Human/LLM runs only expose each Human's committed perception through its Console controller.
        if (!ShowModelDetails && kind is "model-input" or "model-output" or "terminal" or "committed" or "feedback-receipt") { return; }
        Output.WriteLine(summary is not null ? $"[{kind}] {summary}"
            : kind is "model-input" or "model-output" or "terminal" ? $"[{kind}]"
            : $"[{kind}] {JsonSerializer.Serialize(payload, Json)}");
        Output.Flush();
    }

    public void Trace(LlmControllerTrace trace) {
        if (trace.Kind == LlmControllerTraceKind.Feedback) {
            Write("feedback-receipt", new { trace.RequestId, trace.FeedbackId },
                $"反馈已保存：{trace.FeedbackId}。继续选择行动；后续构造请求可证明包含回执。");
            return;
        }
        if (trace.Kind == LlmControllerTraceKind.Request) {
            Write("model-input", new { trace.RequestId, trace.CallNumber });
            if (!ShowModelDetails) { return; }
            Output.WriteLine(trace.Request.PromptPrefix.SystemPrompt);
            foreach (ObservationMessage material in trace.Request.PromptPrefix.SharedContextMessages.Concat(trace.Request.TailMessages).OfType<ObservationMessage>()) {
                Output.WriteLine(material.Content);
            }
        } else {
            Write("model-output", new { trace.RequestId, trace.CallNumber });
            if (!ShowModelDetails) { return; }
            Output.WriteLine($"调用 {trace.CallNumber}：{trace.Result?.Termination.Kind.ToString() ?? trace.Kind.ToString()}；"
                + $"{trace.ProtocolError ?? (trace.Operation is null ? "未形成行动" : "待游戏校验，尚未提交")}");
            if (trace.Result is { } result) {
                foreach (ActionBlock.Text text in result.Message.Blocks.OfType<ActionBlock.Text>()) {
                    Output.WriteLine("模型说明（非游戏对白）：" + CharacterContextRenderer.DataLiteral(text.Content));
                }
                foreach (RawToolCall call in result.Message.ToolCalls) {
                    Output.WriteLine($"工具：{CharacterContextRenderer.DataLiteral(call.ToolName)}；ID：{CharacterContextRenderer.DataLiteral(call.ToolCallId)}；"
                        + (trace.Operation is { } operation ? $"待校验行动：{CharacterContextRenderer.DataLiteral(operation.RawInput)}"
                            : $"参数：{CharacterContextRenderer.DataLiteral(call.RawInput)}"));
                }
            }
        }
        Output.Flush();
    }

    public void Terminal(PlaygroundWorld world, string actorId) {
        string text = CharacterContextRenderer.RenderFull(world.Business.InteractionStates[actorId], new DecisionPlayLayout());
        Write("terminal", new { ActorId = actorId });
        if (ShowModelDetails) { Output.WriteLine(text); Output.Flush(); }
    }
}

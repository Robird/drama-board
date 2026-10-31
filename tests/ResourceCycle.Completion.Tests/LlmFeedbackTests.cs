using System.Text.Json;
using System.Net;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle.Completion.Tests;

public sealed class LlmFeedbackTests {
    [Fact]
    public async Task Responses_wire_replays_native_reasoning_and_feedback_receipt_before_final_act() {
        using var handler = new FeedbackWireHandler();
        using var http = CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://fixture.invalid/"), handler);
        var controller = new LlmCharacterController(new OpenAIResponsesClient(null, http), "fixture-model", _ => "材料",
            feedbackSink: (_, _) => ValueTask.FromResult("durable-receipt"));
        Assert.Equal("act-call", (await controller.ChooseCommandAsync(await Context(), default)).ToolCallId);
        Assert.Equal(2, handler.Requests.Count);
        using var second = JsonDocument.Parse(handler.Requests[1]);
        JsonElement[] input = second.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal("opaque-fixture", Assert.Single(input, item => item.TryGetProperty("type", out var type) &&
            type.GetString() == "reasoning").GetProperty("encrypted_content").GetString());
        var call = Assert.Single(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
        var receipt = Assert.Single(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Assert.Equal("feedback-call", call.GetProperty("call_id").GetString());
        Assert.Equal("feedback-call", receipt.GetProperty("call_id").GetString());
        Assert.Contains("durable-receipt", receipt.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Feedback_is_saved_before_continuing_with_original_action_and_receipt_then_history_is_discarded() {
        CompletionResult feedback = Feedback("feedback-call");
        var client = new Client(feedback, Act("act-1"), Act("act-2"));
        var submissions = new List<LlmFeedbackSubmission>();
        var traces = new List<LlmControllerTrace>();
        var context = await Context();
        var before = context.InteractionState;
        var controller = new LlmCharacterController(client, "fixture-model", _ => "材料", trace: traces.Add,
            feedbackSink: (submission, _) => {
                Assert.Single(client.Requests);
                submissions.Add(submission);
                return ValueTask.FromResult("receipt-1");
            });

        Assert.Equal("act-1", (await controller.ChooseCommandAsync(context, default)).ToolCallId);
        Assert.Same(before, context.InteractionState);
        Assert.Empty(context.Context.Items);
        var submission = Assert.Single(submissions);
        Assert.Equal("a", submission.CharacterId);
        Assert.Equal(1, submission.ContextSequence);
        Assert.Equal(1, submission.CallNumber);
        Assert.Equal("feedback-call", submission.ToolCallId);
        Assert.Equal("issue", submission.Kind);
        Assert.Equal("summary", submission.Summary);
        Assert.Null(submission.DesiredSupport);
        Assert.Equal(traces[0].RequestId, submission.RequestId);
        Assert.Same(feedback.Message, client.Requests[1].TailMessages[0]);
        var receipt = Assert.Single(Assert.IsType<ToolResultsMessage>(client.Requests[1].TailMessages[1]).Results);
        Assert.Equal("feedback-call", receipt.ToolCallId);
        Assert.Equal("report-feedback", receipt.ToolName);
        Assert.Equal(ToolExecutionStatus.Success, receipt.Status);
        Assert.Contains("receipt-1", receipt.GetFlattenedText());
        Assert.Equal("receipt-1", Assert.Single(traces, t => t.Kind == LlmControllerTraceKind.Feedback).FeedbackId);
        Assert.Equal(2, client.Requests[0].PromptPrefix.OutputContract.Tools.Length);

        await controller.ChooseCommandAsync(context, default);
        Assert.Empty(client.Requests[2].TailMessages);
        var requests = traces.Where(t => t.Kind == LlmControllerTraceKind.Request).ToArray();
        Assert.Equal(3, requests.Select(t => t.RequestId).Distinct().Count());
        Assert.All(traces, trace => Assert.Equal(requests.Single(r => r.CallNumber == trace.CallNumber).RequestId, trace.RequestId));
    }

    [Fact]
    public async Task Disabled_feedback_keeps_single_act_contract_and_rejects_feedback() {
        var client = new Client(Feedback("f"));
        var controller = new LlmCharacterController(client, "model", _ => "material",
            new() { MaxConsecutiveProtocolFailures = 1 });
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("protocol-failure-limit", error.ReasonCode);
        Assert.Equal("act", Assert.Single(client.Requests[0].PromptPrefix.OutputContract.Tools).Name);
        Assert.DoesNotContain("report-feedback", client.Requests[0].PromptPrefix.SystemPrompt);
    }

    [Fact]
    public async Task Save_failure_is_a_pause_without_success_receipt_or_second_model_call() {
        var client = new Client(Feedback("f"));
        var traces = new List<LlmControllerTrace>();
        var controller = new LlmCharacterController(client, "model", _ => "material", trace: traces.Add,
            feedbackSink: (_, _) => throw new IOException("private storage details"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("feedback-save-failed", error.ReasonCode);
        Assert.DoesNotContain("private", error.Message);
        Assert.Single(client.Requests);
        Assert.DoesNotContain(traces, t => t.Kind == LlmControllerTraceKind.Feedback || t.Operation is not null);
        Assert.Equal("feedback-save-failed", traces.Last().ProtocolError);
    }

    [Fact]
    public async Task Feedback_limit_and_protocol_correction_retain_only_successful_history() {
        var client = new Client(Feedback("f1"), Feedback("f2"), Act("act"));
        int saved = 0;
        var traces = new List<LlmControllerTrace>();
        var controller = new LlmCharacterController(client, "model", _ => "material",
            new() { MaxFeedbackPerDecision = 1 }, traces.Add,
            (_, _) => ValueTask.FromResult($"saved-{++saved}"));
        Assert.Equal("act", (await controller.ChooseCommandAsync(await Context(), default)).ToolCallId);
        Assert.Equal(1, saved);
        Assert.Equal(3, controller.CallsMade);
        Assert.Equal("feedback-budget-exhausted", traces.Single(t => t.Kind == LlmControllerTraceKind.Response && t.CallNumber == 2).ProtocolError);
        Assert.Equal(3, client.Requests[2].TailMessages.Length);
        Assert.Equal("f1", Assert.Single(Assert.IsType<ActionMessage>(client.Requests[2].TailMessages[0]).ToolCalls).ToolCallId);
        Assert.Contains("feedback-budget-exhausted", Assert.IsType<ObservationMessage>(client.Requests[2].TailMessages[2]).Content);
    }

    [Fact]
    public async Task Saved_feedback_does_not_refund_total_calls() {
        var client = new Client(Feedback("f"));
        int saved = 0;
        var controller = new LlmCharacterController(client, "model", _ => "material", new() { MaxCalls = 1 },
            feedbackSink: (_, _) => ValueTask.FromResult($"saved-{++saved}"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("call-budget-exhausted", error.ReasonCode);
        Assert.Equal(1, saved);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Native_feedback_tail_counts_toward_context_budget_without_truncation() {
        var result = Feedback("f");
        result = result with { Message = new ActionMessage([new ActionBlock.Text(new string('x', 500)), .. result.Message.Blocks]) };
        var client = new Client(result);
        int saved = 0;
        var controller = new LlmCharacterController(client, "model", _ => "material", new() { MaxContextCharacters = 100 },
            feedbackSink: (_, _) => ValueTask.FromResult($"saved-{++saved}"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("context-too-large", error.ReasonCode);
        Assert.Equal(1, saved);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("{\"kind\":\"issue\",\"summary\":\"s\",\"obstacle\":\"o\",\"extra\":1}")]
    [InlineData("{\"kind\":\"issue\",\"summary\":\"s\",\"summary\":\"s2\",\"obstacle\":\"o\"}")]
    [InlineData("{\"kind\":\"bug\",\"summary\":\"s\",\"obstacle\":\"o\"}")]
    [InlineData("{\"kind\":\"issue\",\"summary\":\" \",\"obstacle\":\"o\"}")]
    [InlineData("{\"kind\":\"issue\",\"summary\":\"s\",\"obstacle\":\"o\",\"desired_support\":null}")]
    public async Task Malformed_feedback_never_reaches_sink(string json) {
        var client = new Client(Result(new("report-feedback", "f", json)));
        var controller = new LlmCharacterController(client, "model", _ => "material", new() { MaxConsecutiveProtocolFailures = 1 },
            feedbackSink: (_, _) => throw new InvalidOperationException("must not save"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("protocol-failure-limit", error.ReasonCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Multiple_tools_or_incomplete_response_cannot_partially_save(bool multipleTools) {
        var result = Feedback("f");
        result = multipleTools
            ? result with { Message = new ActionMessage([.. result.Message.Blocks, .. Act("act").Message.Blocks]) }
            : result with { Termination = new(CompletionTerminationKind.Incomplete) };
        var client = new Client(result);
        var controller = new LlmCharacterController(client, "model", _ => "material", new() { MaxConsecutiveProtocolFailures = 1 },
            feedbackSink: (_, _) => throw new InvalidOperationException("must not save"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal(multipleTools ? "protocol-failure-limit" : "completion-incomplete", error.ReasonCode);
    }

    [Fact]
    public async Task Cancellation_after_durable_save_keeps_saved_trace_but_never_continues() {
        using var source = new CancellationTokenSource();
        var traces = new List<LlmControllerTrace>();
        var client = new Client(Feedback("f"));
        var controller = new LlmCharacterController(client, "model", _ => "material", trace: traces.Add,
            feedbackSink: (_, _) => { source.Cancel(); return ValueTask.FromResult("saved"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.ChooseCommandAsync(await Context(), source.Token));
        Assert.Single(client.Requests);
        Assert.Equal("saved", Assert.Single(traces, t => t.Kind == LlmControllerTraceKind.Feedback).FeedbackId);
    }

    private static CompletionResult Feedback(string id) => Result(new("report-feedback", id,
        JsonSerializer.Serialize(new { kind = "issue", summary = "summary", obstacle = "obstacle" })));
    private static CompletionResult Act(string id) => Result(new("act", id, "{\"command\":\"wait 1\"}"));
    private static CompletionResult Result(RawToolCall call) => new(new ActionMessage([new ActionBlock.ToolCall(call)]), new("fixture", "fixture-api", "fixture-model"));
    private static async Task<CycleDecisionContext> Context() {
        var initial = CycleGameFactory.CreateSurvival(enableWeather: false);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * CycleTime.Hour)));
        return CycleDecisionContext.Create(kernel.World, "a");
    }
    private sealed class Client(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture-api";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FeedbackWireHandler : HttpMessageHandler {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            bool first = Requests.Count == 1;
            object call = new { type = "function_call", id = first ? "feedback-item" : "act-item",
                call_id = first ? "feedback-call" : "act-call", name = first ? "report-feedback" : "act",
                arguments = first ? "{\"kind\":\"issue\",\"summary\":\"s\",\"obstacle\":\"o\"}" : "{\"command\":\"wait 1\"}" };
            object[] items = first
                ? [new { type = "reasoning", id = "reasoning-item", encrypted_content = "opaque-fixture", summary = Array.Empty<object>() }, call]
                : [call];
            var stream = new StringBuilder();
            for (int i = 0; i < items.Length; i++) {
                stream.Append("data: ").Append(JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = i, item = items[i] })).Append("\n\n");
            }
            stream.Append("data: ").Append(JsonSerializer.Serialize(new { type = "response.completed", response = new { output = items } })).Append("\n\n");
            return new(HttpStatusCode.OK) { Content = new StringContent(stream.ToString(), Encoding.UTF8, "text/event-stream") };
        }
    }
}

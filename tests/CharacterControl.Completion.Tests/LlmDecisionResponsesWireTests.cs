using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;
using static DramaBoard.CharacterControl.Completion.Tests.LlmDecisionTestSupport;

namespace DramaBoard.CharacterControl.Completion.Tests;

/// <summary>Public Responses transport fixtures. Codex shares the protocol core, but its authentication is not exercised here.</summary>
public sealed class LlmDecisionResponsesWireTests {
    [Fact]
    public async Task Real_public_client_projects_single_json_tool_and_delivers_original_provider_id() {
        var handler = new FixtureHandler("data: " + JsonSerializer.Serialize(new {
            type = "response.output_item.done", output_index = 0,
            item = new { type = "function_call", id = "item-1", call_id = "wire-call-1", name = "act", arguments = "{\"command\":\"use item.water\"}" }
        }) + "\n\ndata: " + JsonSerializer.Serialize(new {
            type = "response.completed",
            response = new { output = new[] { new {
                type = "function_call", id = "item-1", call_id = "wire-call-1", name = "act",
                arguments = "{\"command\":\"use item.water\"}"
            } } }
        }) + "\n\n");
        using var http = CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://fixture.invalid/"), handler);
        var loop = new LlmDecisionLoop(new OpenAIResponsesClient(null, http), "fixture-model");
        CommandSubmission operation = await loop.ChooseCommandAsync(Input(material: "完整材料"), default);
        Assert.Equal("wire-call-1", operation.ToolCallId);
        Assert.Equal("use item.water", operation.RawInput);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("auto", body.RootElement.GetProperty("tool_choice").GetString());
        Assert.False(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        JsonElement tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("act", tool.GetProperty("name").GetString());
        Assert.Equal("string", tool.GetProperty("parameters").GetProperty("properties").GetProperty("command").GetProperty("type").GetString());
        Assert.Equal("完整材料", body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Complete_looking_function_call_without_terminal_never_becomes_a_submission() {
        var handler = new FixtureHandler("data: " + JsonSerializer.Serialize(new {
            type = "response.output_item.done", output_index = 0,
            item = new { type = "function_call", id = "item-1", call_id = "wire-call-1", name = "act", arguments = "{\"command\":\"use item.water\"}" }
        }) + "\n\n");
        using var http = CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://fixture.invalid/"), handler);
        var loop = new LlmDecisionLoop(new OpenAIResponsesClient(null, http), "fixture-model");
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("transport-failure", exception.ReasonCode);
        Assert.IsType<CompletionStreamInterruptedException>(exception.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Responses_wire_replays_native_reasoning_and_feedback_receipt_before_final_act() {
        using var handler = new FeedbackWireHandler();
        using var http = CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://fixture.invalid/"), handler);
        var loop = new LlmDecisionLoop(new OpenAIResponsesClient(null, http), "fixture-model",
            feedbackSink: (_, _) => ValueTask.FromResult("durable-receipt"));
        Assert.Equal("act-call", (await loop.ChooseCommandAsync(Input(material: "材料"), default)).ToolCallId);
        Assert.Equal(2, handler.Requests.Count);
        using (var first = JsonDocument.Parse(handler.Requests[0])) {
            var tool = Assert.Single(first.RootElement.GetProperty("tools").EnumerateArray(),
                item => item.GetProperty("name").GetString() == "feedback");
            var parameters = tool.GetProperty("parameters");
            Assert.Equal("text", Assert.Single(parameters.GetProperty("properties").EnumerateObject()).Name);
            Assert.Equal("text", Assert.Single(parameters.GetProperty("required").EnumerateArray()).GetString());
        }
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

    private sealed class FixtureHandler(string stream) : HttpMessageHandler {
        public string? RequestBody { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Calls++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class FeedbackWireHandler : HttpMessageHandler {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            bool first = Requests.Count == 1;
            object call = new { type = "function_call", id = first ? "feedback-item" : "act-item",
                call_id = first ? "feedback-call" : "act-call", name = first ? "feedback" : "act",
                arguments = first ? "{\"text\":\"feedback text\"}" : "{\"command\":\"wait 1\"}" };
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

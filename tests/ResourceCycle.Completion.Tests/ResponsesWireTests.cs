using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle.Completion.Tests;

/// <summary>Public Responses transport fixtures. Codex shares the protocol core, but its authentication is not exercised here.</summary>
public sealed class ResponsesWireTests {
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
        var controller = new LlmCharacterController(new OpenAIResponsesClient(null, http), "fixture-model", _ => "完整材料");
        CycleControllerOperation operation = await controller.ChooseCommandAsync(await Context(), default);
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
    public async Task Complete_looking_function_call_without_terminal_never_becomes_a_game_operation() {
        var handler = new FixtureHandler("data: " + JsonSerializer.Serialize(new {
            type = "response.output_item.done", output_index = 0,
            item = new { type = "function_call", id = "item-1", call_id = "wire-call-1", name = "act", arguments = "{\"command\":\"use item.water\"}" }
        }) + "\n\n");
        using var http = CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://fixture.invalid/"), handler);
        var controller = new LlmCharacterController(new OpenAIResponsesClient(null, http), "fixture-model", _ => "material");
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("transport-failure", exception.ReasonCode);
        Assert.IsType<CompletionStreamInterruptedException>(exception.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    private static async Task<CycleDecisionContext> Context() {
        var initial = CycleGameFactory.CreateSurvival(enableWeather: false);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * CycleTime.Hour)));
        return CycleDecisionContext.Create(kernel.World, "a");
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
}

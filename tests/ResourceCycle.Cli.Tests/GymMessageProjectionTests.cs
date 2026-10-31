using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class GymMessageProjectionTests {
    [Fact]
    public void ActionMessagePreservesOrderedTextAndBothToolInputProtocols() {
        const string jsonInput = " {\"command\":\"goal create 返回营地\"} ";
        const string textInput = "goal note g1 \"路口\\河边\"\n保留原文";
        IHistoryMessage message = new ActionMessage([
            new ActionBlock.Text("先检查水量。"),
            new ActionBlock.ToolCall(new RawToolCall("act", "call-json", jsonInput)),
            new ActionBlock.Text("然后行动。"),
            new ActionBlock.ToolCall(RawToolCall.FromText("console", "call-text", textInput))
        ]);

        JsonElement projected = JsonSerializer.SerializeToElement(GymMessageProjection.Message(message));
        Assert.Equal("Action", projected.GetProperty("Kind").GetString());
        JsonElement blocks = projected.GetProperty("Blocks");
        Assert.Equal(4, blocks.GetArrayLength());
        Assert.Equal("先检查水量。", blocks[0].GetProperty("Content").GetString());
        Assert.Equal("Text", blocks[0].GetProperty("Kind").GetString());
        Assert.Equal("然后行动。", blocks[2].GetProperty("Content").GetString());
        JsonElement jsonCall = blocks[1].GetProperty("Call");
        Assert.Equal("ToolCall", blocks[1].GetProperty("Kind").GetString());
        Assert.Equal("act", jsonCall.GetProperty("ToolName").GetString());
        Assert.Equal("call-json", jsonCall.GetProperty("ToolCallId").GetString());
        Assert.Equal("JsonObject", jsonCall.GetProperty("InputKind").GetString());
        Assert.Equal(jsonInput, jsonCall.GetProperty("RawInput").GetString());
        Assert.Equal(jsonInput, jsonCall.GetProperty("RawArgumentsJson").GetString());
        JsonElement textCall = blocks[3].GetProperty("Call");
        Assert.Equal("console", textCall.GetProperty("ToolName").GetString());
        Assert.Equal("call-text", textCall.GetProperty("ToolCallId").GetString());
        Assert.Equal("Text", textCall.GetProperty("InputKind").GetString());
        Assert.Equal(textInput, textCall.GetProperty("RawInput").GetString());
        Assert.False(textCall.TryGetProperty("RawArgumentsJson", out _));
    }

    [Fact]
    public void ToolResultsMessagePreservesResultIdentityStatusAndEveryNestedBlock() {
        IHistoryMessage message = new ToolResultsMessage("环境补充", [
            new ToolResult("act", "call-1", ToolExecutionStatus.Failed, [
                new ToolResultBlock.Text("资源已耗尽。"),
                new ToolResultBlock.Text("\n可以继续等待。")
            ]),
            ToolResult.FromText("inspect", "call-2", ToolExecutionStatus.Success, "{\"water\":2}")
        ]);

        JsonElement projected = GymMessageProjection.Runtime(message);
        Assert.Equal("ToolResults", projected.GetProperty("Kind").GetString());
        Assert.Equal("环境补充", projected.GetProperty("Content").GetString());
        JsonElement results = projected.GetProperty("Results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("act", results[0].GetProperty("ToolName").GetString());
        Assert.Equal("call-1", results[0].GetProperty("ToolCallId").GetString());
        Assert.Equal("Failed", results[0].GetProperty("Status").GetString());
        JsonElement blocks = results[0].GetProperty("Blocks");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("Text", blocks[0].GetProperty("Kind").GetString());
        Assert.Equal("资源已耗尽。", blocks[0].GetProperty("Content").GetString());
        Assert.Equal("\n可以继续等待。", blocks[1].GetProperty("Content").GetString());
        Assert.Equal("Success", results[1].GetProperty("Status").GetString());
        Assert.Equal("{\"water\":2}", results[1].GetProperty("Blocks")[0].GetProperty("Content").GetString());
    }

    [Fact]
    public void ProviderReplayFieldsSurviveNestedMessageProjectionWithoutCodecRegistration() {
        var origin = new CompletionDescriptor("fixture-provider", "fixture-api", "fixture-model");
        byte[] nativeBytes = Encoding.UTF8.GetBytes("{\"signature\":\"fixture-signature\",\"parts\":[1,2]}");
        const string responsesItem = "{\"type\":\"reasoning\",\"encrypted_content\":\"fixture-replay\"}";
        IHistoryMessage message = new ActionMessage([
            new AnthropicReasoningBlock(nativeBytes, origin, "anthropic-readable"),
            new GeminiReplayBlock(nativeBytes, origin),
            new OpenAIChatReasoningBlock("chat-native", origin),
            new OpenAIResponsesReasoningBlock(responsesItem, origin),
            new ActionBlock.OpaqueReasoningBlock("future-codec", nativeBytes, origin),
            new ActionBlock.TextReasoningBlock("text-native", origin)
        ]);

        JsonElement blocks = GymMessageProjection.Runtime(message).GetProperty("Blocks");
        Assert.Equal(6, blocks.GetArrayLength());
        foreach (JsonElement block in blocks.EnumerateArray()) {
            Assert.Equal("Thinking", block.GetProperty("Kind").GetString());
            JsonElement descriptor = block.GetProperty("Origin");
            Assert.Equal(origin.ProviderId, descriptor.GetProperty("ProviderId").GetString());
            Assert.Equal(origin.ApiSpecId, descriptor.GetProperty("ApiSpecId").GetString());
            Assert.Equal(origin.Model, descriptor.GetProperty("Model").GetString());
        }
        Assert.Equal(nativeBytes, blocks[0].GetProperty("OpaquePayload").GetBytesFromBase64());
        Assert.Equal("anthropic-readable", blocks[0].GetProperty("PlainText").GetString());
        Assert.Equal(nativeBytes, blocks[1].GetProperty("OpaquePayload").GetBytesFromBase64());
        Assert.Equal("chat-native", blocks[2].GetProperty("Content").GetString());
        Assert.Equal(responsesItem, blocks[3].GetProperty("RawItemJson").GetString());
        Assert.Equal("future-codec", blocks[4].GetProperty("CodecId").GetString());
        Assert.Equal(nativeBytes, blocks[4].GetProperty("OpaquePayload").GetBytesFromBase64());
        Assert.Equal("text-native", blocks[5].GetProperty("Content").GetString());
    }

    [Fact]
    public void ContractPreservesPolicySchemaConstraintsDefaultsAndTextGrammar() {
        var root = new ToolSchema.Object([
            new ToolSchema.Property("command", new ToolSchema.Value(ToolParamType.String,
                description: "Action", example: "wait", stringEnumValues: ["wait", "look"],
                minLength: 1, maxLength: 4, pattern: "^[a-z]+$"), true),
            new ToolSchema.Property("count", new ToolSchema.Value(ToolParamType.Int32,
                isNullable: true, defaultValue: new ParamDefault(2), minimum: 1, maximum: 5), false),
            new ToolSchema.Property("note", new ToolSchema.Value(ToolParamType.String,
                isNullable: true, defaultValue: new ParamDefault(null)), false)
        ], description: "Action arguments", example: "{\"command\":\"wait\"}");
        var contract = new CompletionOutputContract([
            new ToolDefinition("act", "Perform one action", root),
            ToolDefinition.FromText("console", "Raw command", ToolTextFormat.Grammar("regex", "wait [0-9]+"))
        ], CompletionToolChoice.RequiredNamed("act"), allowParallelToolCalls: false);

        JsonElement projected = JsonSerializer.SerializeToElement(GymMessageProjection.Contract(contract));
        Assert.Equal("RequiredNamed", projected.GetProperty("ToolChoice").GetProperty("Kind").GetString());
        Assert.Equal("act", projected.GetProperty("ToolChoice").GetProperty("RequiredToolName").GetString());
        Assert.False(projected.GetProperty("AllowParallelToolCalls").GetBoolean());
        Assert.Equal(contract.SemanticFingerprint, projected.GetProperty("SemanticFingerprint").GetString());
        JsonElement tools = projected.GetProperty("Tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.Equal("act", tools[0].GetProperty("Name").GetString());
        Assert.Equal("Perform one action", tools[0].GetProperty("Description").GetString());
        JsonElement schema = tools[0].GetProperty("InputSchema");
        Assert.Equal(root.Description, schema.GetProperty("Description").GetString());
        Assert.Equal(root.Example, schema.GetProperty("Example").GetString());
        Assert.False(schema.GetProperty("AdditionalProperties").GetBoolean());
        Assert.False(schema.GetProperty("IsNullable").GetBoolean());
        JsonElement properties = schema.GetProperty("Properties");
        Assert.True(properties[0].GetProperty("IsRequired").GetBoolean());
        JsonElement command = properties[0].GetProperty("Schema");
        Assert.Equal("String", command.GetProperty("ValueKind").GetString());
        Assert.Equal("Action", command.GetProperty("Description").GetString());
        Assert.Equal("wait", command.GetProperty("Example").GetString());
        Assert.Equal("look", command.GetProperty("StringEnumValues")[1].GetString());
        Assert.Equal(1, command.GetProperty("MinLength").GetInt32());
        Assert.Equal(4, command.GetProperty("MaxLength").GetInt32());
        Assert.Equal("^[a-z]+$", command.GetProperty("Pattern").GetString());
        Assert.Equal(JsonValueKind.Null, command.GetProperty("Default").ValueKind);
        JsonElement count = properties[1].GetProperty("Schema");
        Assert.True(count.GetProperty("IsNullable").GetBoolean());
        Assert.Equal(2, count.GetProperty("Default").GetProperty("Value").GetInt32());
        Assert.Equal(1, count.GetProperty("Minimum").GetInt32());
        Assert.Equal(5, count.GetProperty("Maximum").GetInt32());
        Assert.Equal(JsonValueKind.Null, properties[2].GetProperty("Schema").GetProperty("Default").GetProperty("Value").ValueKind);
        Assert.Equal("Text", tools[1].GetProperty("InputKind").GetString());
        Assert.Equal("regex", tools[1].GetProperty("TextFormat").GetProperty("Syntax").GetString());
        Assert.Equal("wait [0-9]+", tools[1].GetProperty("TextFormat").GetProperty("Definition").GetString());
    }

    [Fact]
    public void UnsupportedNestedSchemaFailsInsteadOfDroppingItsFields() {
        var contract = CompletionOutputContract.ProviderDefault([
            new ToolDefinition("nested", "Unsupported shape", new ToolSchema.Object([
                new ToolSchema.Property("values", new ToolSchema.Array(new ToolSchema.Value(ToolParamType.String)), true)
            ]))
        ]);
        Assert.Throws<NotSupportedException>(() => GymMessageProjection.Contract(contract));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("### 环境\n没有裁剪。")]
    public void ObservationPreservesNullOrExactContent(string? content) {
        IHistoryMessage message = new ObservationMessage(content);
        JsonElement projected = GymMessageProjection.Runtime(message);
        Assert.Equal("Observation", projected.GetProperty("Kind").GetString());
        Assert.Equal(content, projected.GetProperty("Content").GetString());
    }
}

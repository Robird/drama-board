using System.Reflection;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadInputTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_act_and_optional_feedback_first_request_is_comparable(bool feedback) {
        var client = new CaptureClient();
        var loop = new LlmDecisionLoop(client, "model", feedbackSink: feedback
            ? (_, _) => throw new InvalidOperationException("Capture must not execute feedback.") : null);
        await Assert.ThrowsAsync<CapturedException>(async () => {
            await loop.ChooseCommandAsync(new("actor", 1, "material", "guidance"), CancellationToken.None);
        });
        var request = Assert.IsType<CompletionRequest>(client.Request);
        Assert.Equal(feedback ? new[] { "act", "feedback" } : new[] { "act" },
            request.PromptPrefix.OutputContract.Tools.Select(tool => tool.Name).ToArray());
        Assert.NotEmpty(Key(request));
    }

    [Fact]
    public void Equivalent_independent_first_requests_have_identical_keys() {
        Assert.Equal(Key(Request()), Key(Request()));
    }

    [Fact]
    public void Model_system_and_ordered_shared_messages_preserve_original_values() {
        string original = Key(Request());
        CompletionRequest[] changed = [
            Request(model: "model "), Request(system: "system "), Request(system: "System"),
            Request(shared: [new ObservationMessage("first "), new ObservationMessage("second")]),
            Request(shared: [new ObservationMessage("second"), new ObservationMessage("first")]),
            Request(shared: [new ObservationMessage("firstsecond")]), Request(shared: []),
            Request(shared: [new ObservationMessage(null)]), Request(shared: [new ObservationMessage("")])
        ];
        foreach (var request in changed) { Assert.NotEqual(original, Key(request)); }
        Assert.NotEqual(Key(Request(shared: [new ObservationMessage(null)])), Key(Request(shared: [new ObservationMessage("")])));
    }

    [Fact]
    public void Tool_names_descriptions_order_choice_and_nullable_parallel_policy_are_distinct() {
        var a = Tool();
        var b = Tool(name: "other");
        Assert.NotEqual(Key(Request(tools: [a, b])), Key(Request(tools: [b, a])));
        Assert.NotEqual(Key(Request()), Key(Request(tools: [Tool(name: "command ")])));
        Assert.NotEqual(Key(Request()), Key(Request(tools: [Tool(description: "description ")])));
        var choices = new[] { CompletionToolChoice.ProviderDefault, CompletionToolChoice.Auto,
            CompletionToolChoice.None, CompletionToolChoice.RequiredAny, CompletionToolChoice.RequiredNamed("command"),
            CompletionToolChoice.RequiredNamed("other") };
        Assert.Equal(choices.Length, choices.Select(choice => Key(Request(tools: [a, b], choice: choice))).Distinct().Count());
        Assert.Equal(3, new bool?[] { null, false, true }.Select(parallel => Key(Request(parallel: parallel))).Distinct().Count());
    }

    [Fact]
    public void Flat_schema_metadata_property_identity_and_order_are_compared() {
        var value = new ToolSchema.Value(ToolParamType.String);
        ToolSchema.Object[] schemas = [
            new([new("command", value, false)]), new([new("renamed", value, true)]),
            new([new("command", value, true)], additionalProperties: true),
            new([new("command", value, true)], description: "object description"),
            new([new("command", value, true)], example: "object example"), new([])
        ];
        string original = Key(Request());
        foreach (var schema in schemas) { Assert.NotEqual(original, Key(Request(tools: [Tool(schema: schema)]))); }
        var a = new ToolSchema.Property("a", value, true);
        var b = new ToolSchema.Property("b", value, false);
        Assert.NotEqual(Key(Request(tools: [Tool(schema: new ToolSchema.Object([a, b]))])),
            Key(Request(tools: [Tool(schema: new ToolSchema.Object([b, a]))])));
    }

    [Fact]
    public void Every_supported_scalar_constraint_affects_the_key() {
        ToolSchema.Value[] strings = [
            new(ToolParamType.String, isNullable: true),
            new(ToolParamType.String, defaultValue: new ParamDefault("default")),
            new(ToolParamType.String, description: "value description"),
            new(ToolParamType.String, example: "value example"),
            new(ToolParamType.String, stringEnumValues: ["a", "b"]),
            new(ToolParamType.String, minLength: 1), new(ToolParamType.String, maxLength: 100),
            new(ToolParamType.String, pattern: "^[a-z]+$"), new(ToolParamType.Boolean)
        ];
        string original = Key(Request());
        foreach (var value in strings) { Assert.NotEqual(original, ValueKey(value)); }
        Assert.NotEqual(ValueKey(new(ToolParamType.String, stringEnumValues: ["a", "b"])),
            ValueKey(new(ToolParamType.String, stringEnumValues: ["b", "a"])));
        Assert.NotEqual(ValueKey(new(ToolParamType.String, isNullable: true)),
            ValueKey(new(ToolParamType.String, isNullable: true, defaultValue: new ParamDefault(null))));
        var number = new ToolSchema.Value(ToolParamType.Int32);
        Assert.NotEqual(ValueKey(number), ValueKey(new(ToolParamType.Int32, minimum: 0)));
        Assert.NotEqual(ValueKey(number), ValueKey(new(ToolParamType.Int32, maximum: 10)));
        Assert.NotEqual(ValueKey(new(ToolParamType.Int32, minimum: 0)), ValueKey(new(ToolParamType.Int32, minimum: 0L)));
        Assert.NotEqual(ValueKey(new(ToolParamType.Int32, defaultValue: new ParamDefault(1))),
            ValueKey(new(ToolParamType.Int32, defaultValue: new ParamDefault(1L))));
    }

    [Fact]
    public void Nonempty_tail_unknown_messages_nested_schema_and_text_tools_fail_closed() {
        Assert.True(PlaygroundLookaheadController.TryInput(Request(), out _));
        CompletionRequest[] unsupported = [
            Request(tail: [new ObservationMessage("tail")]),
            Request(shared: [new UnknownMessage()]), Request(shared: [new DerivedObservation("first")]),
            Request(shared: [new ActionMessage([])]), Request(shared: [new ToolResultsMessage("results", [])]),
            Request(tools: [Tool(schema: new ToolSchema.Object([new("nested", new ToolSchema.Object(), true)]))]),
            Request(tools: [Tool(schema: new ToolSchema.Object([new("array", new ToolSchema.Array(new ToolSchema.Value(ToolParamType.String)), true)]))]),
            Request(tools: [ToolDefinition.FromText("text", "text tool")])
        ];
        foreach (var request in unsupported) {
            Assert.False(PlaygroundLookaheadController.TryInput(request, out var input));
            Assert.Null(input);
        }
    }

    [Fact]
    public void Upstream_public_instance_property_inventory_requires_explicit_upgrade_review() {
        // This is an upgrade gate, not proof that future fields are automatically supported.
        Properties<CompletionRequest>("ModelId", "PromptPrefix", "TailMessages");
        Properties<CompletionPromptPrefix>("SystemPrompt", "OutputContract", "SharedContextMessages");
        Properties<CompletionOutputContract>("Tools", "ToolChoice", "AllowParallelToolCalls", "SemanticFingerprint", "IsProviderDefault");
        Properties<CompletionToolChoice>("Kind", "RequiredToolName");
        Properties<ToolDefinition>("Name", "Description", "InputKind", "InputSchema", "TextFormat");
        Properties<ToolSchema>("Description", "Example");
        Properties<ToolSchema.Object>("Description", "Example", "Properties", "AdditionalProperties", "IsNullable");
        Properties<ToolSchema.Value>("Description", "Example", "ValueKind", "IsNullable", "Default", "StringEnumValues",
            "MinLength", "MaxLength", "Pattern", "Minimum", "Maximum");
        Properties<ToolSchema.Property>("Name", "Schema", "IsRequired");
        Properties<ToolSchema.Array>("Description", "Example", "ItemSchema", "IsNullable");
        Properties<ParamDefault>("Value");
        Properties<ToolTextFormat>("Syntax", "Definition");
        Properties<ObservationMessage>("Content", "Kind");
        Properties<IHistoryMessage>("Kind");
    }

    private static void Properties<T>(params string[] expected) => Assert.Equal(
        expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray());

    private static string ValueKey(ToolSchema.Value value) => Key(Request(tools: [Tool(schema:
        new ToolSchema.Object([new("command", value, true)]))]));

    private static string Key(CompletionRequest request) {
        Assert.True(PlaygroundLookaheadController.TryInput(request, out var input));
        return Assert.IsType<string>(input);
    }

    private static ToolDefinition Tool(string name = "command", string description = "description", ToolSchema.Object? schema = null) =>
        new(name, description, schema ?? new ToolSchema.Object([new("command", new ToolSchema.Value(ToolParamType.String), true)]));

    private static CompletionRequest Request(string model = "model", string system = "system",
        IReadOnlyList<IHistoryMessage>? shared = null, IReadOnlyList<IHistoryMessage>? tail = null,
        IReadOnlyList<ToolDefinition>? tools = null, CompletionToolChoice? choice = null, bool? parallel = null) =>
        new(model, new(system, new([.. (tools ?? [Tool()])], choice ?? CompletionToolChoice.ProviderDefault, parallel),
            shared ?? [new ObservationMessage("first"), new ObservationMessage("second")]), tail ?? []);

    private sealed class UnknownMessage : IHistoryMessage {
        public HistoryMessageKind Kind => HistoryMessageKind.Observation;
    }
    private sealed record DerivedObservation(string? Text) : ObservationMessage(Text);
    private sealed class CapturedException : Exception { }
    private sealed class CaptureClient : ICompletionClient {
        public string Name => "capture";
        public string ApiSpecId => "capture";
        public CompletionRequest? Request { get; private set; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Request = request;
            throw new CapturedException();
        }
    }
}

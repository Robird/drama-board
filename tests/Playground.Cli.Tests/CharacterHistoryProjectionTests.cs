using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class CharacterHistoryProjectionTests {
    private static readonly CompletionDescriptor Origin = new("fixture", "fixture-api", "fixture-model");

    [Fact]
    public void Readable_model_blocks_keep_order_and_plaintext_without_native_reasoning_payloads() {
        ActionBlock[] blocks = [
            new ActionBlock.TextReasoningBlock("first thought", Origin),
            new ActionBlock.Text("visible one"),
            new ActionBlock.OpaqueReasoningBlock("secret-codec", Encoding.UTF8.GetBytes("secret-native-payload"), Origin, "readable summary"),
            new ActionBlock.OpaqueReasoningBlock("another-codec", Encoding.UTF8.GetBytes("encrypted-no-summary"), Origin),
            new ActionBlock.ToolCall(new RawToolCall("act", "call-1", " {\"command\":\"wait 1\"} ")),
            new ActionBlock.Text("visible two")
        ];
        var trace = Trace(LlmControllerTraceKind.Response, result: new(new(blocks), Origin));

        ModelOutcomeSnapshot projected = ModelHistoryProjection.Outcome(trace, 7);
        Assert.Equal(new[] { "Reasoning", "Text", "Reasoning", "Reasoning", "ToolCall", "Text" }, projected.Result!.Blocks.Select(block => block.Kind));
        Assert.Equal(Enumerable.Range(0, blocks.Length), projected.Result.Blocks.Select(block => block.BlockIndex));
        Assert.Equal("first thought", projected.Result.Blocks[0].PlainText);
        Assert.Equal("readable summary", projected.Result.Blocks[2].PlainText);
        Assert.Null(projected.Result.Blocks[3].PlainText);
        Assert.Equal(" {\"command\":\"wait 1\"} ", projected.Result.Blocks[4].ToolCall!.RawInput);
        Assert.Equal(projected.Result.Blocks[4].ToolCall!.RawInput, projected.Result.Blocks[4].ToolCall!.RawArgumentsJson);
        string json = JsonSerializer.Serialize(projected);
        Assert.DoesNotContain("secret-native-payload", json);
        Assert.DoesNotContain("encrypted-no-summary", json);
        Assert.DoesNotContain("secret-codec", json);
        Assert.DoesNotContain("OpaquePayload", json);
        Assert.DoesNotContain("Origin", json);
        Assert.Null(projected.Usage!.OutputTokens);
        Assert.Equal(7, projected.RawRecordSequence);
    }

    [Fact]
    public void Input_projects_tool_results_before_observation_and_unknown_messages_only_as_gaps() {
        var request = Request([
            new ObservationMessage("actual observation"),
            new ToolResultsMessage("receipt wrapper", [
                ToolResult.FromText("feedback", "feedback-call", ToolExecutionStatus.Success, "saved: F1"),
                new ToolResult("inspect", "inspect-call", ToolExecutionStatus.Failed,
                    [new ToolResultBlock.Text("first"), new ToolResultBlock.Text("second")])
            ]),
            new VendorObservation("secret-inherited-content", "secret-future-native")
        ], [new ActionMessage([new ActionBlock.Text("carried assistant")])]);
        var trace = Trace(LlmControllerTraceKind.Request, request: request);

        ModelRequestSnapshot projected = ModelHistoryProjection.Request(trace, null, 3);
        Assert.Equal(new[] { "Observation", "ToolResults", "Unsupported" }, projected.Shared.Select(message => message.Kind));
        Assert.Equal("receipt wrapper", projected.Shared[1].Content);
        Assert.Equal("feedback", projected.Shared[1].Results[0].ToolName);
        Assert.Equal("feedback-call", projected.Shared[1].Results[0].ToolCallId);
        Assert.Equal("Success", projected.Shared[1].Results[0].Status);
        Assert.Equal("saved: F1", projected.Shared[1].Results[0].Blocks[0].Text);
        Assert.Equal(new[] { "first", "second" }, projected.Shared[1].Results[1].Blocks.Select(block => block.Text));
        Assert.Contains(nameof(VendorObservation), projected.Shared[2].ProjectionGap);
        Assert.Equal("Tail", projected.Tail[0].Section);
        Assert.Equal("carried assistant", projected.Tail[0].Blocks[0].Text);
        string json = JsonSerializer.Serialize(projected);
        Assert.DoesNotContain("secret-inherited-content", json);
        Assert.DoesNotContain("secret-future-native", json);
        Assert.DoesNotContain("secret-system-prompt", json);
    }

    [Fact]
    public void Material_preserves_thin_identities_each_item_text_and_previous_advance_without_undelivered_data() {
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
        var advance = new PendingAdvance("a", 1, 8, PlaygroundCommandParser.Parse("wait 1")!, "wait 1",
            "advance-call", "advance-operation", observation.Time, "accepted wait", activityId: 1);
        var opening = new CharacterContextOpening(PlaygroundBackground.For(observation), observation, advance,
            observation.Time, PlaygroundOutcomeActivityState.Completed);
        var context = new CharacterContext(2, CharacterContextStatus.Closed, opening, [
            new CharacterContextItem(1, 1, "first-invalid", "call-1", "operation-1", "first rejection", isRejected: true),
            new CharacterContextItem(2, 2, "second-invalid", "call-2", "operation-2", "second rejection", isRejected: true)
        ], 3, 2);
        var state = new CharacterInteractionState("a", context, draftHandoff: "undelivered-secret");

        CharacterMaterialSnapshot original = CharacterHistoryProjection.Capture(state, "capture-S");
        long capturedAt = observation.Time.Ticks + 1234;
        CharacterMaterialSnapshot material = CharacterHistoryProjection.Capture(state, "capture-S", 7, 19, capturedAt);
        Assert.Equal("Closed", material.Status);
        Assert.Equal("capture-S", material.StateCommitId);
        Assert.Equal(7, material.LineageId);
        Assert.Equal(19, material.TransitionCount);
        Assert.Equal(capturedAt, material.TimeTicks);
        Assert.NotEqual(observation.Time.Ticks, material.TimeTicks);
        Assert.Equal(original.ObservationText, material.ObservationText);
        Assert.Equal(original.Opening.Text, material.Opening.Text);
        Assert.Equal(new CharacterAdvanceSnapshot(1, 8, "advance-operation"), material.Opening.PreviousAdvance);
        Assert.Equal(new long[] { 1, 2 }, material.Items.Select(item => item.ItemSequence));
        Assert.Equal("operation-1", material.Items[0].OperationRecordId);
        Assert.Contains("first-invalid", material.Items[0].Text);
        Assert.DoesNotContain("second-invalid", material.Items[0].Text);
        Assert.Contains("second-invalid", material.Items[1].Text);
        Assert.DoesNotContain("first-invalid", material.Items[1].Text);
        string json = JsonSerializer.Serialize(material);
        Assert.DoesNotContain("undelivered-secret", json);
        Assert.DoesNotContain("CurrentContext", json);
        Assert.DoesNotContain("RawInput", json);
        Assert.DoesNotContain("Background", json);
    }

    [Fact]
    public void Failure_without_result_preserves_unknown_remote_outcome_and_candidate_is_not_a_receipt() {
        var failure = Trace(LlmControllerTraceKind.Failure) with {
            ProtocolError = "transport-failure",
            Operation = new("candidate-call", "wait 1", "candidate-operation")
        };
        ModelOutcomeSnapshot projected = ModelHistoryProjection.Outcome(failure, 4);
        Assert.Null(projected.Result);
        Assert.Null(projected.Termination);
        Assert.Equal("Failure", projected.Stage);
        Assert.Equal("transport-failure", projected.ProtocolError);
        Assert.Equal("candidate-operation", projected.ProposedOperation!.OperationRecordId);
    }

    private sealed record VendorObservation(string? Body, string Native) : ObservationMessage(Body);
    private static CompletionRequest Request(IReadOnlyList<IHistoryMessage> shared, IReadOnlyList<IHistoryMessage>? tail = null) =>
        new("fixture-model", new("secret-system-prompt", CompletionOutputContract.ProviderDefault([]), shared), tail ?? []);
    private static LlmControllerTrace Trace(LlmControllerTraceKind kind, CompletionResult? result = null, CompletionRequest? request = null) =>
        new(kind, 1, "a", 2, request ?? Request([new ObservationMessage("world observation")]), result,
            RequestId: "request-1", ConnectionId: "fixture-connection", ApiSpecId: "fixture-api");
}

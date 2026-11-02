using Atelia.Completion.Abstractions;
using DramaBoard.Playground;

internal sealed record ModelToolCallSnapshot(string ToolName, string ToolCallId, string InputKind,
    string RawInput, string? RawArgumentsJson);
internal sealed record ModelBlockSnapshot(int BlockIndex, string Kind, string? Text = null,
    string? PlainText = null, ModelToolCallSnapshot? ToolCall = null, string? ProjectionGap = null);
internal sealed record ModelToolResultSnapshot(int ResultIndex, string ToolName, string ToolCallId, string Status,
    IReadOnlyList<ModelBlockSnapshot> Blocks);
internal sealed record ModelMessageSnapshot(string Section, int MessageIndex, string Kind, string? Content,
    IReadOnlyList<ModelBlockSnapshot> Blocks, IReadOnlyList<ModelToolResultSnapshot> Results, string? ProjectionGap = null);
internal sealed record ModelRequestSnapshot(string RequestId, int CallNumber, string? ConnectionId, string ModelId,
    string? ApiSpecId, long? RawRecordSequence, PlaygroundDecisionBoundary? Boundary,
    IReadOnlyList<ModelMessageSnapshot> Shared, IReadOnlyList<ModelMessageSnapshot> Tail);
internal sealed record ModelInvocationSnapshot(string ProviderId, string ApiSpecId, string Model);
internal sealed record ModelTerminationSnapshot(string Kind, string? ProviderReason, string? Detail);
internal sealed record ModelUsageSnapshot(long? UncachedInputTokens, long? CacheCreationInputTokens,
    long? CacheReadInputTokens, long? OutputTokens, string CacheRequestStatus, string CacheSupportStatus,
    string CacheObservationStatus);
internal sealed record ModelFailureSnapshot(string Kind, int? HttpStatusCode, string? ProviderCode, TimeSpan? RetryAfter);
internal sealed record ModelResultSnapshot(IReadOnlyList<ModelBlockSnapshot> Blocks, IReadOnlyList<string>? Errors,
    ModelFailureSnapshot? Failure);
internal sealed record ModelProposedOperation(string OperationRecordId, string ToolCallId, string RawInput);
internal sealed record ModelOutcomeSnapshot(string RequestId, int CallNumber, string? ConnectionId, string ModelId,
    string? ApiSpecId, long? RawRecordSequence, string Stage, string? ProtocolError,
    ModelInvocationSnapshot? Invocation, ModelTerminationSnapshot? Termination, ModelUsageSnapshot? Usage,
    ModelResultSnapshot? Result, ModelProposedOperation? ProposedOperation);

/// <summary>A whitelist reading view. Provider-native reasoning, signatures and replay payloads never enter it.</summary>
internal static class ModelHistoryProjection {
    public static ModelRequestSnapshot Request(LlmControllerTrace trace, PlaygroundDecisionBoundary? boundary,
        long? rawRecordSequence) {
        ArgumentNullException.ThrowIfNull(trace);
        return new(RequireRequestId(trace), trace.CallNumber, trace.ConnectionId, trace.ModelId, trace.ApiSpecId,
            rawRecordSequence, boundary, Messages("Shared", trace.Request.PromptPrefix.SharedContextMessages),
            Messages("Tail", trace.Request.TailMessages));
    }

    public static ModelOutcomeSnapshot Outcome(LlmControllerTrace trace, long? rawRecordSequence) {
        ArgumentNullException.ThrowIfNull(trace);
        CompletionResult? result = trace.Result;
        return new(RequireRequestId(trace), trace.CallNumber, trace.ConnectionId, trace.ModelId, trace.ApiSpecId,
            rawRecordSequence, trace.Kind.ToString(), trace.ProtocolError,
            result is null ? null : new(result.Invocation.ProviderId, result.Invocation.ApiSpecId, result.Invocation.Model),
            result is null ? null : new(result.Termination.Kind.ToString(), result.Termination.ProviderReason, result.Termination.Detail),
            result is null ? null : new(result.Usage.UncachedInputTokens, result.Usage.CacheCreationInputTokens,
                result.Usage.CacheReadInputTokens, result.Usage.OutputTokens, result.Usage.PromptCache.RequestStatus.ToString(),
                result.Usage.PromptCache.SupportStatus.ToString(), result.Usage.PromptCache.ObservationStatus.ToString()),
            result is null ? null : new(Blocks(result.Message.Blocks), result.Errors?.ToArray(),
                result.Failure is { } failure ? new(failure.Kind.ToString(), failure.HttpStatusCode, failure.ProviderCode, failure.RetryAfter) : null),
            trace.Operation is { } operation ? new(operation.OperationRecordId, operation.ToolCallId, operation.RawInput) : null);
    }

    internal static IReadOnlyList<ModelMessageSnapshot> Messages(string section, IReadOnlyList<IHistoryMessage> messages) =>
        messages.Select((message, index) => Message(section, index, message)).ToArray();

    private static ModelMessageSnapshot Message(string section, int index, IHistoryMessage message) => message switch {
        // ToolResults inherits ObservationMessage: preserve its structured receipts before considering the base class.
        ToolResultsMessage results => new(section, index, "ToolResults", results.Content, [],
            results.Results.Select((result, resultIndex) => new ModelToolResultSnapshot(resultIndex, result.ToolName,
                result.ToolCallId, result.Status.ToString(), result.Blocks.Select((block, blockIndex) => block switch {
                    ToolResultBlock.Text text => new ModelBlockSnapshot(blockIndex, "Text", Text: text.Content),
                    _ => new ModelBlockSnapshot(blockIndex, "Unsupported", ProjectionGap: TypeGap(block))
                }).ToArray())).ToArray()),
        ObservationMessage observation when message.GetType() == typeof(ObservationMessage) =>
            new(section, index, "Observation", observation.Content, [], []),
        ActionMessage action => new(section, index, "Action", null, Blocks(action.Blocks), []),
        _ => new(section, index, "Unsupported", null, [], [], TypeGap(message))
    };

    internal static IReadOnlyList<ModelBlockSnapshot> Blocks(IReadOnlyList<ActionBlock> blocks) =>
        blocks.Select((block, index) => block switch {
            ActionBlock.Text text => new ModelBlockSnapshot(index, "Text", Text: text.Content),
            ActionBlock.ReasoningBlock reasoning => new ModelBlockSnapshot(index, "Reasoning", PlainText: reasoning.PlainText),
            ActionBlock.ToolCall tool => new ModelBlockSnapshot(index, "ToolCall", ToolCall: new(tool.Call.ToolName,
                tool.Call.ToolCallId, tool.Call.InputKind.ToString(), tool.Call.RawInput,
                tool.Call.InputKind == ToolInputKind.JsonObject ? tool.Call.RawArgumentsJson : null)),
            _ => new ModelBlockSnapshot(index, "Unsupported", ProjectionGap: TypeGap(block))
        }).ToArray();

    private static string TypeGap(object value) => "No readable projection for " + value.GetType().FullName;
    private static string RequireRequestId(LlmControllerTrace trace) => !string.IsNullOrWhiteSpace(trace.RequestId)
        ? trace.RequestId : throw new InvalidOperationException("Character history requires a stable model RequestId.");
}

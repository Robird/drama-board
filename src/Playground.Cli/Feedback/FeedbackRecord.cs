using DramaBoard.Playground;
using System.Text.Json.Serialization;

namespace DramaBoard.Playground.Cli;

[JsonConverter(typeof(JsonStringEnumConverter<FeedbackSource>))]
internal enum FeedbackSource { HumanConsole, Llm }

internal sealed record FeedbackModel(string RequestId, int CallNumber, string ToolCallId);

/// <summary>Independent host evidence. StorePath is a location hint, not a global store identity.</summary>
internal sealed record FeedbackRecord(int Version, string FeedbackId, DateTimeOffset CreatedAt, string RunId,
    string ActorId, FeedbackSource Source, long ContextSequence, string Text,
    PlaygroundDecisionBoundary? Boundary, FeedbackModel? Model, string? StorePath) {
    public const int MaxTextCharacters = 4000;
}

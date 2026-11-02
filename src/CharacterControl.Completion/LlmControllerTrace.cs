using Atelia.Completion.Abstractions;

namespace DramaBoard.CharacterControl.Completion;

public enum LlmControllerTraceKind { Request, Response, Failure, Feedback }

/// <summary>Host-only evidence of generation, not proof of a committed game operation.</summary>
/// <remarks>Includes model-visible materials and outputs, never connection configuration or credentials.
/// The host owns storage, access and presentation. Callback failures propagate rather than silently losing evidence.</remarks>
public sealed record LlmControllerTrace(
    LlmControllerTraceKind Kind,
    int CallNumber,
    string CharacterId,
    long ContextSequence,
    CompletionRequest Request,
    CompletionResult? Result = null,
    string? ProtocolError = null,
    CommandSubmission? Operation = null,
    string? RequestId = null,
    string? FeedbackId = null,
    string? ConnectionId = null,
    string? ApiSpecId = null) {
    public string ModelId => Request.ModelId;
}

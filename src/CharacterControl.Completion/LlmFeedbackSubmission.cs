namespace DramaBoard.CharacterControl.Completion;

/// <summary>A host experiment report, never a game command or character memory.</summary>
/// <remarks>The host connects RequestId to the recorded input and pre-decision checkpoint.
/// A sink must durably record the report before returning its stable receipt ID. Throwing leaves
/// delivery unconfirmed; callers do not infer that a failed save necessarily wrote nothing.</remarks>
public sealed record LlmFeedbackSubmission(
    string RequestId,
    int CallNumber,
    string CharacterId,
    long ContextSequence,
    string ToolCallId,
    string Kind,
    string Summary,
    string Obstacle,
    string? DesiredSupport);

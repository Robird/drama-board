namespace DramaBoard.CharacterControl.Completion;

/// <summary>A frozen, actor-visible projection supplied by a game's strongly typed adapter.</summary>
/// <remarks>Material contains the current character's complete decision context. GameGuidance describes
/// available capabilities and behavior policy; the shared loop supplies the act/feedback protocol.
/// This transport input is neither a persistent character model nor authority to mutate the world.</remarks>
public sealed record LlmDecisionInput(string CharacterId, long ContextSequence, string Material, string GameGuidance) {
    internal void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(CharacterId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ContextSequence);
        ArgumentNullException.ThrowIfNull(Material);
        ArgumentNullException.ThrowIfNull(GameGuidance);
    }
}

/// <summary>Unparsed model output with original provider call identity and a host operation identity.</summary>
/// <remarks>A submission is not an accepted command or committed game effect. The receiving game must
/// parse and validate RawInput before publishing its own operation result.</remarks>
public sealed record CommandSubmission(string ToolCallId, string RawInput, string OperationRecordId);

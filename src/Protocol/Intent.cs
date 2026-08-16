namespace DramaBoard.Protocol;

/// <summary>Describes what a Player wants an actor to attempt.</summary>
/// <param name="ActionKind">The stable action contract identifier.</param>
/// <param name="TargetActorId">The optional actor involved in the action.</param>
/// <param name="TargetObjectId">The optional object involved in the action.</param>
/// <param name="DestinationId">The optional destination for a travel action.</param>
/// <param name="FreeText">The optional natural-language content of the action.</param>
/// <param name="DurationMs">The optional duration in model-time milliseconds.</param>
/// <param name="UntilModelTimeMs">The optional absolute model time in milliseconds at which waiting ends.</param>
public sealed record Intent(
    ActionKind ActionKind,
    string? TargetActorId = null,
    string? TargetObjectId = null,
    string? DestinationId = null,
    string? FreeText = null,
    long? DurationMs = null,
    long? UntilModelTimeMs = null);

/// <summary>Captures the outcome a Player expects without asserting that it will occur.</summary>
/// <param name="FreeText">The Player's free-text description of the expected outcome.</param>
/// <param name="ExpectedCompletionModelTimeMs">The optional expected completion time in model-time milliseconds.</param>
public sealed record ExpectedOutcome(
    string FreeText,
    long? ExpectedCompletionModelTimeMs = null);

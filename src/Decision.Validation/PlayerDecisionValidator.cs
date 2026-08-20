using DramaBoard.Protocol;

namespace DramaBoard.Decision.Validation;

/// <summary>Describes why a Player answer does not match its decision request.</summary>
public enum PlayerDecisionValidationError
{
    None = 0,
    DecisionIdMismatch,
    WorldVersionMismatch,
    LineageMismatch,
}

/// <summary>Contains the pure correlation-validation result for one Player answer.</summary>
public readonly record struct PlayerDecisionValidationResult(
    PlayerDecisionValidationError Error,
    string? Message)
{
    public bool IsValid => Error == PlayerDecisionValidationError.None;

    public static PlayerDecisionValidationResult Valid { get; } =
        new(PlayerDecisionValidationError.None, null);
}

/// <summary>Validates Player-boundary correlation without depending on Host or Kernel.</summary>
public static class PlayerDecisionValidator
{
    public static PlayerDecisionValidationResult Validate(
        PlayerDecision decision,
        DecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(request);

        if (decision.DecisionId != request.DecisionId)
        {
            return new(
                PlayerDecisionValidationError.DecisionIdMismatch,
                "The Player decision does not match the requested DecisionId.");
        }

        if (decision.BasedOnWorldVersion != request.BasedOnWorldVersion)
        {
            return new(
                PlayerDecisionValidationError.WorldVersionMismatch,
                "The Player decision is based on a stale world version.");
        }

        if (decision.LineageId != request.LineageId)
        {
            return new(
                PlayerDecisionValidationError.LineageMismatch,
                "The Player decision belongs to a different world lineage.");
        }

        return PlayerDecisionValidationResult.Valid;
    }
}

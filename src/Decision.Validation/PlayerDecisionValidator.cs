using DramaBoard.Protocol;

namespace DramaBoard.Decision.Validation;

/// <summary>Describes why a Player answer does not match its decision request.</summary>
public enum PlayerDecisionValidationError
{
    None = 0,
    DecisionIdMismatch,
    ActionNotAvailable,
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

        if (!request.AvailableActions.Any(action =>
            Matches(decision.Intent, action)))
        {
            return new(
                PlayerDecisionValidationError.ActionNotAvailable,
                "The Player intent is not one of the request's advertised affordances.");
        }

        return PlayerDecisionValidationResult.Valid;
    }

    private static bool Matches(Intent intent, AvailableAction available)
    {
        if (intent.ActionKind != available.ActionKind ||
            !MatchesOptional(intent.TargetActorId, available.CandidateActorIds) ||
            !MatchesOptional(intent.TargetObjectId, available.CandidateObjectIds) ||
            !MatchesOptional(intent.ExitId, available.CandidateExitIds) ||
            !MatchesOptional(intent.DestinationId, available.CandidateDestinationIds))
        {
            return false;
        }

        if (intent.ActionKind != ActionKinds.Travel && intent.ExitId is not null)
        {
            return false;
        }

        return intent.ActionKind.Id switch
        {
            "action.travel" => intent.ExitId is not null && intent.DestinationId is null,
            "action.talk" => intent.TargetActorId is not null,
            "action.take" or "action.put" or "action.use" => intent.TargetObjectId is not null,
            "action.give" or "action.show" =>
                intent.TargetActorId is not null && intent.TargetObjectId is not null,
            "action.wait" =>
                intent.TargetActorId is null &&
                intent.TargetObjectId is null &&
                intent.ExitId is null &&
                intent.DestinationId is null,
            "action.observe" => intent.TargetActorId is null && intent.DestinationId is null,
            _ => false,
        };
    }

    private static bool MatchesOptional(string? selected, IReadOnlyList<string>? advertised) =>
        selected is null
            ? true
            : advertised?.Contains(selected, StringComparer.Ordinal) == true;
}

namespace DramaBoard.Protocol;

/// <summary>Returns a Player's intent for the exact request and world version observed.</summary>
/// <param name="DecisionId">The identifier of the request being answered.</param>
/// <param name="BasedOnWorldVersion">The world version observed when choosing the intent.</param>
/// <param name="Intent">The action the Player wants the actor to attempt.</param>
/// <param name="ExpectedOutcome">The optional subjective outcome expected by the Player.</param>
public sealed record PlayerDecision(
    DecisionId DecisionId,
    long BasedOnWorldVersion,
    Intent Intent,
    ExpectedOutcome? ExpectedOutcome = null);
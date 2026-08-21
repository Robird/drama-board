namespace DramaBoard.Protocol;

/// <summary>Returns a Player's intent for the exact in-flight request observed.</summary>
/// <param name="DecisionId">The identifier of the request being answered.</param>
/// <param name="Intent">The action the Player wants the actor to attempt.</param>
public sealed record PlayerDecision(
    DecisionId DecisionId,
    Intent Intent);

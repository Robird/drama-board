namespace DramaBoard.Protocol;

/// <summary>Returns a Player's intent for the exact in-flight request observed.</summary>
/// <param name="DecisionId">The identifier of the request being answered.</param>
/// <param name="Intent">The action the Player wants the actor to attempt.</param>
public sealed record PlayerDecision
{
    /// <summary>Creates an answer for one initialized decision request.</summary>
    public PlayerDecision(DecisionId DecisionId, Intent Intent)
    {
        if (string.IsNullOrWhiteSpace(DecisionId.Value))
        {
            throw new ArgumentException("Decision identifier must be initialized.", nameof(DecisionId));
        }

        ArgumentNullException.ThrowIfNull(Intent);
        this.DecisionId = DecisionId;
        this.Intent = Intent;
    }

    public DecisionId DecisionId { get; }

    public Intent Intent { get; }
}

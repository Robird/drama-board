using DramaBoard.Protocol;

namespace DramaBoard.Player;

/// <summary>Obtains one Player decision without exposing the objective world.</summary>
public interface IPlayerDriver
{
    /// <summary>Chooses an intent for a version-bound decision request.</summary>
    ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken);
}

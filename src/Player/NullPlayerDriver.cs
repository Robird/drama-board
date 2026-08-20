using DramaBoard.Protocol;

namespace DramaBoard.Player;

/// <summary>Answers every request with a wait intent.</summary>
public sealed class NullPlayerDriver : IPlayerDriver
{
    /// <inheritdoc />
    public ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new PlayerDecision(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            new Intent(ActionKinds.Wait)));
    }
}

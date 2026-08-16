using DramaBoard.Protocol;

namespace DramaBoard.Host;

/// <summary>Chooses uniformly from available affordances using a stable SplitMix64 stream.</summary>
public sealed class RandomPlayerDriver : IPlayerDriver
{
    private ulong _state;

    /// <summary>Initializes a deterministic random Player from an explicit seed.</summary>
    public RandomPlayerDriver(ulong seed)
    {
        _state = seed;
    }

    /// <inheritdoc />
    public ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Intent intent = request.AvailableActions.Count == 0
            ? new Intent(ActionKinds.Wait)
            : CreateIntent(request.AvailableActions[NextIndex(request.AvailableActions.Count)]);
        return ValueTask.FromResult(new PlayerDecision(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            intent));
    }

    private Intent CreateIntent(AvailableAction availableAction) =>
        new(
            availableAction.ActionKind,
            TargetActorId: ChooseCandidate(availableAction.CandidateActorIds),
            TargetObjectId: ChooseCandidate(availableAction.CandidateObjectIds),
            DestinationId: ChooseCandidate(availableAction.CandidateDestinationIds));

    private string? ChooseCandidate(IReadOnlyList<string>? candidates) =>
        candidates is { Count: > 0 }
            ? candidates[NextIndex(candidates.Count)]
            : null;

    private int NextIndex(int exclusiveUpperBound)
    {
        ulong bound = (uint)exclusiveUpperBound;
        ulong rejectionThreshold = unchecked(0UL - bound) % bound;

        while (true)
        {
            ulong sample = NextUInt64();
            if (sample >= rejectionThreshold)
            {
                return (int)(sample % bound);
            }
        }
    }

    private ulong NextUInt64()
    {
        _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
        ulong mixed = _state;
        mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
        mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;
        return mixed ^ (mixed >> 31);
    }
}
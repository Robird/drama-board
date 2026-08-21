using DramaBoard.Kernel.Random;
using DramaBoard.Protocol;

namespace DramaBoard.Player;

/// <summary>Chooses uniformly from available affordances using stable request-addressed samples.</summary>
public sealed class RandomPlayerDriver : IPlayerDriver
{
    private readonly long _seed;

    /// <summary>Initializes a deterministic random Player from an explicit seed.</summary>
    public RandomPlayerDriver(long seed)
    {
        _seed = seed;
    }

    /// <inheritdoc />
    public ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<AvailableAction> availableActions = request.AvailableActions;
        Intent intent = availableActions.Count == 0
            ? new Intent(ActionKinds.Wait)
            : CreateIntent(
                request,
                availableActions[SampleIndex(request, availableActions.Count, choiceIndex: 0)]);
        return ValueTask.FromResult(new PlayerDecision(
            request.DecisionId,
            intent));
    }

    private Intent CreateIntent(DecisionRequest request, AvailableAction availableAction) =>
        new(
            availableAction.ActionKind,
            TargetActorId: ChooseCandidate(request, availableAction.CandidateActorIds, choiceIndex: 1),
            TargetObjectId: ChooseCandidate(request, availableAction.CandidateObjectIds, choiceIndex: 2),
            ExitId: ChooseCandidate(request, availableAction.CandidateExitIds, choiceIndex: 3),
            DestinationId: ChooseCandidate(request, availableAction.CandidateDestinationIds, choiceIndex: 4));

    private string? ChooseCandidate(
        DecisionRequest request,
        IReadOnlyList<string>? candidates,
        ulong choiceIndex) =>
        candidates is { Count: > 0 }
            ? candidates[SampleIndex(request, candidates.Count, choiceIndex)]
            : null;

    private int SampleIndex(DecisionRequest request, int exclusiveUpperBound, ulong choiceIndex) =>
        DeterministicRandom.SampleInt32(
            unchecked((ulong)_seed),
            DeterministicRandom.DeriveStreamId(request.ActorId),
            DeterministicRandom.DeriveStreamId(request.DecisionId.Value),
            minInclusive: 0,
            exclusiveUpperBound,
            choiceIndex);
}

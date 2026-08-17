using DramaBoard.Kernel.Random;
using DramaBoard.Protocol;

namespace DramaBoard.Host;

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

        Intent intent = request.AvailableActions.Count == 0
            ? new Intent(ActionKinds.Wait)
            : CreateIntent(
                request,
                request.AvailableActions[SampleIndex(request, request.AvailableActions.Count, choiceIndex: 0)]);
        return ValueTask.FromResult(new PlayerDecision(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            intent));
    }

    private Intent CreateIntent(DecisionRequest request, AvailableAction availableAction) =>
        new(
            availableAction.ActionKind,
            TargetActorId: ChooseCandidate(request, availableAction.CandidateActorIds, choiceIndex: 1),
            TargetObjectId: ChooseCandidate(request, availableAction.CandidateObjectIds, choiceIndex: 2),
            DestinationId: ChooseCandidate(request, availableAction.CandidateDestinationIds, choiceIndex: 3));

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
            unchecked((ulong)request.BasedOnWorldVersion),
            minInclusive: 0,
            exclusiveUpperBound,
            choiceIndex);
}

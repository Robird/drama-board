using DramaBoard.Protocol;

namespace DramaBoard.Decision.Validation.Tests;

public sealed class PlayerDecisionValidatorTests
{
    [Fact]
    public void Validate_MatchingAnswer_IsValid()
    {
        DecisionRequest request = Request();

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            Decision(request),
            request);

        Assert.True(result.IsValid);
        Assert.Equal(PlayerDecisionValidationError.None, result.Error);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Validate_MismatchedDecisionId_ReturnsSpecificError()
    {
        DecisionRequest request = Request();
        var decision = new PlayerDecision(
            new DecisionId("other"),
            new Intent(ActionKinds.Wait));

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(decision, request);

        Assert.False(result.IsValid);
        Assert.Equal(PlayerDecisionValidationError.DecisionIdMismatch, result.Error);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Validate_ExitOutsideAdvertisedAffordance_IsInvalid()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(ActionKinds.Travel, CandidateExitIds: ["exit.market.bridge"]),
            ],
            observation: ObservationWithBridge());

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Travel, ExitId: "exit.market.ferry")),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    [Fact]
    public void Validate_MissingRequiredTarget_IsInvalid()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(ActionKinds.Travel, CandidateExitIds: ["exit.market.bridge"]),
            ],
            observation: ObservationWithBridge());

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(request.DecisionId, new Intent(ActionKinds.Travel)),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    [Fact]
    public void Validate_ParallelExitsToSameDestination_SelectsExactAdvertisedExit()
    {
        var observation = new Observation(
            "actor.alice",
            "place.square",
            10,
            [
                new ObservedExit("exit.market.bridge", "market", 60_000, true),
                new ObservedExit("exit.market.ferry", "market", 90_000, true),
            ],
            [],
            [],
            []);
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(
                    ActionKinds.Travel,
                    CandidateExitIds: ["exit.market.bridge", "exit.market.ferry"]),
            ],
            observation: observation);

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Travel, ExitId: "exit.market.ferry")),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_TravelDestination_IsInvalidEvenWithValidExit()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(ActionKinds.Travel, CandidateExitIds: ["exit.market.bridge"]),
            ],
            observation: ObservationWithBridge());

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(
                    ActionKinds.Travel,
                    ExitId: "exit.market.bridge",
                    DestinationId: "market")),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    [Fact]
    public void Validate_NonTravelExit_IsInvalidEvenIfAdvertised()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(ActionKinds.Wait, CandidateExitIds: ["exit.market.bridge"]),
            ],
            observation: ObservationWithBridge());

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Wait, ExitId: "exit.market.bridge")),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    private static DecisionRequest Request(
        IReadOnlyList<AvailableAction>? actions = null,
        Observation? observation = null) =>
        new(
            new DecisionId("decision-1"),
            ActorId: "actor.alice",
            ModelTimeMs: 10,
            observation ?? new Observation("actor.alice", "place.square", 10, [], [], [], []),
            actions ?? [new AvailableAction(ActionKinds.Wait)]);

    private static Observation ObservationWithBridge() =>
        new(
            "actor.alice",
            "place.square",
            10,
            [new ObservedExit("exit.market.bridge", "market", 60_000, true)],
            [],
            [],
            []);

    private static PlayerDecision Decision(DecisionRequest request) =>
        new(
            request.DecisionId,
            new Intent(ActionKinds.Wait));
}

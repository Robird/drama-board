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
    public void Validate_TargetOutsideAdvertisedAffordance_IsInvalid()
    {
        DecisionRequest request = Request() with
        {
            AvailableActions =
            [
                new AvailableAction(ActionKinds.Travel, CandidateDestinationIds: ["market"]),
            ],
        };

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.Travel, DestinationId: "cellar")),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    [Fact]
    public void Validate_MissingRequiredTarget_IsInvalid()
    {
        DecisionRequest request = Request() with
        {
            AvailableActions =
            [
                new AvailableAction(ActionKinds.Travel, CandidateDestinationIds: ["market"]),
            ],
        };

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(request.DecisionId, new Intent(ActionKinds.Travel)),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    private static DecisionRequest Request() =>
        new(
            new DecisionId("decision-1"),
            ActorId: "actor.alice",
            ModelTimeMs: 10,
            new Observation("actor.alice", "place.square", 10, [], [], []),
            [new AvailableAction(ActionKinds.Wait)]);

    private static PlayerDecision Decision(DecisionRequest request) =>
        new(
            request.DecisionId,
            new Intent(ActionKinds.Wait));
}

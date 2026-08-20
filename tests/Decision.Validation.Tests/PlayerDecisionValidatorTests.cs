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

    [Theory]
    [InlineData("other", 7, 42, PlayerDecisionValidationError.DecisionIdMismatch)]
    [InlineData("decision-1", 8, 42, PlayerDecisionValidationError.WorldVersionMismatch)]
    [InlineData("decision-1", 7, 43, PlayerDecisionValidationError.LineageMismatch)]
    public void Validate_MismatchedCorrelation_ReturnsSpecificError(
        string decisionId,
        long worldVersion,
        long lineageId,
        PlayerDecisionValidationError expected)
    {
        DecisionRequest request = Request();
        var decision = new PlayerDecision(
            new DecisionId(decisionId),
            worldVersion,
            lineageId,
            new Intent(ActionKinds.Wait));

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(decision, request);

        Assert.False(result.IsValid);
        Assert.Equal(expected, result.Error);
        Assert.NotNull(result.Message);
    }

    private static DecisionRequest Request() =>
        new(
            new DecisionId("decision-1"),
            BasedOnWorldVersion: 7,
            LineageId: 42,
            ModelTimeMs: 10,
            Microstep: 2,
            ActorId: "actor.alice",
            new Observation("actor.alice", "place.square", 10, 2, [], [], []),
            DecisionReasons.Scheduled,
            [new AvailableAction(ActionKinds.Wait)]);

    private static PlayerDecision Decision(DecisionRequest request) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            new Intent(ActionKinds.Wait));
}

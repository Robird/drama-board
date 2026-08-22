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
                new AvailableAction(
                    ActionKinds.Travel,
                    CandidateExitIds: ["exit.market.bridge"],
                    CandidateDestinationIds: ["market"]),
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
    public void Validate_TravelToAdvertisedDestination_WithOptionalFreeText_IsValid()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(
                    ActionKinds.TravelTo,
                    CandidateDestinationIds: ["place.old-harbor", "place.market"]),
            ]);

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(
                    ActionKinds.TravelTo,
                    DestinationId: "place.old-harbor",
                    FreeText: "Take me to the old harbor.")),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_TravelToMissingDestination_IsInvalid()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(
                    ActionKinds.TravelTo,
                    CandidateDestinationIds: ["place.old-harbor"]),
            ]);

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(request.DecisionId, new Intent(ActionKinds.TravelTo)),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    [Fact]
    public void Validate_TravelToUnadvertisedDestination_IsInvalid()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(
                    ActionKinds.TravelTo,
                    CandidateDestinationIds: ["place.old-harbor"]),
            ]);

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.TravelTo, DestinationId: "place.forged")),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    [Fact]
    public void Validate_TravelToCurrentLocation_IsInvalidEvenIfAdvertised()
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(
                    ActionKinds.TravelTo,
                    CandidateDestinationIds: ["place.square"]),
            ]);

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(ActionKinds.TravelTo, DestinationId: "place.square")),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    public static TheoryData<Intent> TravelToIntentsWithForbiddenFields => new()
    {
        new Intent(
            ActionKinds.TravelTo,
            TargetActorId: "actor.bob",
            DestinationId: "place.old-harbor"),
        new Intent(
            ActionKinds.TravelTo,
            TargetObjectId: "object.map",
            DestinationId: "place.old-harbor"),
        new Intent(
            ActionKinds.TravelTo,
            ExitId: "exit.market.bridge",
            DestinationId: "place.old-harbor"),
        new Intent(
            ActionKinds.TravelTo,
            DestinationId: "place.old-harbor",
            DurationMs: 1),
        new Intent(
            ActionKinds.TravelTo,
            DestinationId: "place.old-harbor",
            UntilModelTimeMs: 10),
    };

    [Theory]
    [MemberData(nameof(TravelToIntentsWithForbiddenFields))]
    public void Validate_TravelToForbiddenField_IsInvalidEvenIfAdvertised(Intent intent)
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(
                    ActionKinds.TravelTo,
                    CandidateActorIds: ["actor.bob"],
                    CandidateObjectIds: ["object.map"],
                    CandidateExitIds: ["exit.market.bridge"],
                    CandidateDestinationIds: ["place.old-harbor"]),
            ],
            observation: ObservationWithBridge());

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(request.DecisionId, intent),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    public static TheoryData<ActionKind> EncounterResponseActionKinds => new()
    {
        ActionKinds.ContinueTravel,
        ActionKinds.ReverseTravel,
    };

    [Theory]
    [MemberData(nameof(EncounterResponseActionKinds))]
    public void Validate_EncounterResponse_WithOptionalFreeText_IsValid(ActionKind actionKind)
    {
        DecisionRequest request = Request(
            actions: [new AvailableAction(actionKind)]);

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(
                request.DecisionId,
                new Intent(actionKind, FreeText: "I have made my choice.")),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    public static IEnumerable<object[]> EncounterResponsesWithForbiddenFields()
    {
        foreach (ActionKind actionKind in new[]
                 {
                     ActionKinds.ContinueTravel,
                     ActionKinds.ReverseTravel,
                 })
        {
            yield return [new Intent(actionKind, TargetActorId: "actor.bob")];
            yield return [new Intent(actionKind, TargetObjectId: "object.map")];
            yield return [new Intent(actionKind, ExitId: "exit.market.bridge")];
            yield return [new Intent(actionKind, DestinationId: "place.market")];
            yield return [new Intent(actionKind, DurationMs: 1)];
            yield return [new Intent(actionKind, UntilModelTimeMs: 10)];
        }
    }

    [Theory]
    [MemberData(nameof(EncounterResponsesWithForbiddenFields))]
    public void Validate_EncounterResponseForbiddenField_IsInvalidEvenIfAdvertised(Intent intent)
    {
        DecisionRequest request = Request(
            actions:
            [
                new AvailableAction(
                    intent.ActionKind,
                    CandidateActorIds: ["actor.bob"],
                    CandidateObjectIds: ["object.map"],
                    CandidateExitIds: ["exit.market.bridge"],
                    CandidateDestinationIds: ["place.market"]),
            ],
            observation: ObservationWithBridge());

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(request.DecisionId, intent),
            request);

        Assert.Equal(PlayerDecisionValidationError.ActionNotAvailable, result.Error);
    }

    [Fact]
    public void Validate_ReverseTravel_WhenOnlyContinueIsAdvertised_IsInvalid()
    {
        DecisionRequest request = Request(
            actions: [new AvailableAction(ActionKinds.ContinueTravel)]);

        PlayerDecisionValidationResult result = PlayerDecisionValidator.Validate(
            new PlayerDecision(request.DecisionId, new Intent(ActionKinds.ReverseTravel)),
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

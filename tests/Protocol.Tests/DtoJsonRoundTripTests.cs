using System.Text.Json;

namespace DramaBoard.Protocol.Tests;

public sealed class DtoJsonRoundTripTests
{
    [Fact]
    public void Serialize_EachDto_RoundTripsWithoutInformationLoss()
    {
        KnownFact fact = new(new FactKind("fact.secret.known"), "secret.letter", "Alice knows about the letter.");
        Observation observation = CreateObservation(fact);
        AvailableAction availableAction = new(
            ActionKinds.Give,
            ["actor.bob"],
            ["object.letter"],
            ["place.square"]);
        Intent intent = new(
            ActionKinds.Give,
            TargetActorId: "actor.bob",
            TargetObjectId: "object.letter",
            DestinationId: "place.square",
            FreeText: "Please keep this safe.",
            DurationMs: 1_500,
            UntilModelTimeMs: 80_000);
        ExpectedOutcome expectedOutcome = new("Bob keeps the letter safe.", 80_000);
        DecisionRequest request = CreateCompleteRequest(observation);
        PlayerDecision decision = new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            intent,
            expectedOutcome);

        AssertRoundTrip(fact);
        AssertRoundTrip(observation);
        AssertRoundTrip(availableAction);
        AssertRoundTrip(intent);
        AssertRoundTrip(expectedOutcome);
        DecisionRequest roundTrippedRequest = AssertRoundTrip(request);
        AssertRoundTrip(decision);

        Assert.Equal(6, roundTrippedRequest.AvailableActions.Count);
        Assert.Equal("fact.secret.known", roundTrippedRequest.Observation.KnownFacts[0].FactKind.Id);
        Assert.Equal("place.square", roundTrippedRequest.Observation.LocationId);
        Assert.Equal(DecisionReasons.ActionRejected, roundTrippedRequest.Reason);
        Assert.Equal(
            new Intent(ActionKinds.Travel, DestinationId: "place.cellar"),
            roundTrippedRequest.RejectedIntent);
    }

    [Fact]
    public void Serialize_DefaultOptions_IncludeAndPreserveNullOptionalProperties()
    {
        Intent intent = new(ActionKinds.Observe);
        AvailableAction availableAction = new(ActionKinds.Observe);
        KnownFact fact = new(new FactKind("fact.weather"), null, "It is raining.");
        ExpectedOutcome outcome = new("Learn what is nearby.");
        PlayerDecision decision = new(new DecisionId("decision-1"), 12, 3, intent);
        DecisionRequest request = CreateCompleteRequest(CreateObservation(fact)) with
        {
            Reason = DecisionReasons.Scheduled,
            RejectedIntent = null,
        };

        AssertNullProperties(intent, nameof(Intent.TargetActorId), nameof(Intent.TargetObjectId),
            nameof(Intent.DestinationId), nameof(Intent.FreeText), nameof(Intent.DurationMs), nameof(Intent.UntilModelTimeMs));
        AssertNullProperties(availableAction, nameof(AvailableAction.CandidateActorIds),
            nameof(AvailableAction.CandidateObjectIds), nameof(AvailableAction.CandidateDestinationIds));
        AssertNullProperties(fact, nameof(KnownFact.RelatedId));
        AssertNullProperties(outcome, nameof(ExpectedOutcome.ExpectedCompletionModelTimeMs));
        AssertNullProperties(decision, nameof(PlayerDecision.ExpectedOutcome));
        AssertNullProperties(request, nameof(DecisionRequest.RejectedIntent));

        Assert.Equal(intent, AssertRoundTrip(intent));
        Assert.Equal(availableAction, AssertRoundTrip(availableAction));
        Assert.Equal(fact, AssertRoundTrip(fact));
        Assert.Equal(outcome, AssertRoundTrip(outcome));
        Assert.Equal(decision, AssertRoundTrip(decision));
        AssertRoundTrip(request);
    }

    private static Observation CreateObservation(KnownFact fact) => new(
        "actor.alice",
        "place.square",
        72_000,
        4,
        ["actor.bob"],
        ["object.letter"],
        [fact]);

    private static DecisionRequest CreateCompleteRequest(Observation observation) => new(
        new DecisionId("decision-42"),
        17,
        3,
        72_000,
        4,
        "actor.alice",
        observation,
        DecisionReasons.ActionRejected,
        [
            new(ActionKinds.Travel, CandidateDestinationIds: ["place.inn"]),
            new(ActionKinds.Wait),
            new(ActionKinds.Talk, CandidateActorIds: ["actor.bob"]),
            new(ActionKinds.Observe, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Take, CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Give, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
        ],
        new Intent(ActionKinds.Travel, DestinationId: "place.cellar"));

    private static T AssertRoundTrip<T>(T value)
    {
        string json = JsonSerializer.Serialize(value);
        T? roundTripped = JsonSerializer.Deserialize<T>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped));
        return roundTripped!;
    }

    private static void AssertNullProperties<T>(T value, params string[] propertyNames)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));

        foreach (string propertyName in propertyNames)
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty(propertyName).ValueKind);
        }
    }
}

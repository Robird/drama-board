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
        DecisionRequest request = CreateCompleteRequest(observation);
        PlayerDecision decision = new(
            request.DecisionId,
            intent);

        AssertRoundTrip(fact);
        AssertRoundTrip(observation);
        AssertRoundTrip(availableAction);
        AssertRoundTrip(intent);
        DecisionRequest roundTrippedRequest = AssertRoundTrip(request);
        AssertRoundTrip(decision);

        Assert.Equal(8, roundTrippedRequest.AvailableActions.Count);
        Assert.Equal("fact.secret.known", roundTrippedRequest.Observation.KnownFacts[0].FactKind.Id);
        Assert.Equal("place.square", roundTrippedRequest.Observation.LocationId);
    }

    [Fact]
    public void Serialize_DefaultOptions_IncludeAndPreserveNullOptionalProperties()
    {
        Intent intent = new(ActionKinds.Observe);
        AvailableAction availableAction = new(ActionKinds.Observe);
        KnownFact fact = new(new FactKind("fact.weather"), null, "It is raining.");
        PlayerDecision decision = new(new DecisionId("decision-1"), intent);
        DecisionRequest request = CreateCompleteRequest(CreateObservation(fact));

        AssertNullProperties(intent, nameof(Intent.TargetActorId), nameof(Intent.TargetObjectId),
            nameof(Intent.DestinationId), nameof(Intent.FreeText), nameof(Intent.DurationMs), nameof(Intent.UntilModelTimeMs));
        AssertNullProperties(availableAction, nameof(AvailableAction.CandidateActorIds),
            nameof(AvailableAction.CandidateObjectIds), nameof(AvailableAction.CandidateDestinationIds));
        AssertNullProperties(fact, nameof(KnownFact.RelatedId));

        Assert.Equal(intent, AssertRoundTrip(intent));
        Assert.Equal(availableAction, AssertRoundTrip(availableAction));
        Assert.Equal(fact, AssertRoundTrip(fact));
        Assert.Equal(decision, AssertRoundTrip(decision));
        AssertRoundTrip(request);
    }

    private static Observation CreateObservation(KnownFact fact) => new(
        "actor.alice",
        "place.square",
        72_000,
        ["actor.bob"],
        ["object.letter"],
        [fact]);

    private static DecisionRequest CreateCompleteRequest(Observation observation) => new(
        new DecisionId("decision-42"),
        "actor.alice",
        72_000,
        observation,
        [
            new(ActionKinds.Travel, CandidateDestinationIds: ["place.inn"]),
            new(ActionKinds.Wait),
            new(ActionKinds.Talk, CandidateActorIds: ["actor.bob"]),
            new(ActionKinds.Observe, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Take, CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Put, CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Give, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Show, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
        ]);

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

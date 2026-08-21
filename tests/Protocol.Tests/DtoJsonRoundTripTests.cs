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
            ActionKinds.Travel,
            CandidateExitIds: ["exit.bridge", "exit.ferry"]);
        Intent intent = new(
            ActionKinds.Travel,
            ExitId: "exit.bridge",
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
        Assert.Equal("exit.bridge", roundTrippedRequest.Observation.Exits[0].ExitId);
        Assert.Equal("place.inn", roundTrippedRequest.Observation.Exits[1].DestinationId);
    }

    [Fact]
    public void Serialize_DefaultOptions_IncludeAndPreserveNullOptionalProperties()
    {
        Intent intent = new(ActionKinds.Observe);
        AvailableAction availableAction = new(ActionKinds.Observe);
        KnownFact fact = new(new FactKind("fact.weather"), null, "It is raining.");
        PlayerDecision decision = new(new DecisionId("decision-1"), intent);
        DecisionRequest request = CreateCompleteRequest(CreateObservation(fact));

        AssertNullProperties(intent, nameof(Intent.TargetActorId), nameof(Intent.TargetObjectId), nameof(Intent.ExitId),
            nameof(Intent.DestinationId), nameof(Intent.FreeText), nameof(Intent.DurationMs), nameof(Intent.UntilModelTimeMs));
        AssertNullProperties(availableAction, nameof(AvailableAction.CandidateActorIds),
            nameof(AvailableAction.CandidateObjectIds), nameof(AvailableAction.CandidateExitIds),
            nameof(AvailableAction.CandidateDestinationIds));
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
        [
            new ObservedExit("exit.bridge", "place.inn", 30_000, true),
            new ObservedExit("exit.ferry", "place.inn", 60_000, false),
        ],
        ["actor.bob"],
        ["object.letter"],
        [fact]);

    private static DecisionRequest CreateCompleteRequest(Observation observation) => new(
        new DecisionId("decision-42"),
        "actor.alice",
        72_000,
        observation,
        [
            new(ActionKinds.Travel, CandidateExitIds: ["exit.bridge"]),
            new(ActionKinds.Wait),
            new(ActionKinds.Talk, CandidateActorIds: ["actor.bob"]),
            new(ActionKinds.Observe, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Take, CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Put, CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Give, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
            new(ActionKinds.Show, CandidateActorIds: ["actor.bob"], CandidateObjectIds: ["object.letter"]),
        ]);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ObservedExit_NonPositiveExpectedDuration_Throws(long expectedDurationMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ObservedExit("exit.bridge", "place.inn", expectedDurationMs, true));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ObservedExit_BlankStableIdentifier_Throws(string identifier)
    {
        Assert.Throws<ArgumentException>(() =>
            new ObservedExit(identifier, "place.inn", 1, true));
        Assert.Throws<ArgumentException>(() =>
            new ObservedExit("exit.bridge", identifier, 1, true));
    }

    [Fact]
    public void DecisionRequest_CopiesNestedAffordanceCollections()
    {
        var candidateExitIds = new List<string> { "exit.bridge" };
        var availableActions = new List<AvailableAction>
        {
            new(ActionKinds.Travel, CandidateExitIds: candidateExitIds),
        };
        DecisionRequest request = new(
            new DecisionId("decision-1"),
            "actor.alice",
            10,
            new Observation(
                "actor.alice",
                "place.square",
                10,
                [new ObservedExit("exit.bridge", "place.inn", 1, true)],
                [],
                [],
                []),
            availableActions);

        candidateExitIds[0] = "exit.forged";
        availableActions.Clear();

        Assert.Equal(["exit.bridge"], request.AvailableActions[0].CandidateExitIds);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)request.AvailableActions[0].CandidateExitIds!)[0] = "exit.forged");
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AvailableAction>)request.AvailableActions).Clear());
    }

    [Fact]
    public void DecisionRequest_UnavailableOrUnobservedExitCandidate_Throws()
    {
        var observation = new Observation(
            "actor.alice",
            "place.square",
            10,
            [new ObservedExit("exit.closed", "place.inn", 1, false)],
            [],
            [],
            []);

        Assert.Throws<ArgumentException>(() => new DecisionRequest(
            new DecisionId("decision-1"),
            "actor.alice",
            10,
            observation,
            [new AvailableAction(ActionKinds.Travel, CandidateExitIds: ["exit.closed"])]));
        Assert.Throws<ArgumentException>(() => new DecisionRequest(
            new DecisionId("decision-1"),
            "actor.alice",
            10,
            observation,
            [new AvailableAction(ActionKinds.Travel, CandidateExitIds: ["exit.unobserved"])]));
    }

    [Fact]
    public void Observation_DuplicateExitIdentifier_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Observation(
            "actor.alice",
            "place.square",
            10,
            [
                new ObservedExit("exit.bridge", "place.inn", 1, true),
                new ObservedExit("exit.bridge", "place.ferry", 2, true),
            ],
            [],
            [],
            []));
    }

    [Fact]
    public void DecisionBoundaries_DefaultDecisionId_Throws()
    {
        Observation observation = new(
            "actor.alice",
            "place.square",
            10,
            [],
            [],
            [],
            []);

        Assert.Throws<ArgumentException>(() => new DecisionRequest(
            default,
            "actor.alice",
            10,
            observation,
            [new AvailableAction(ActionKinds.Wait)]));
        Assert.Throws<ArgumentException>(() => new PlayerDecision(
            default,
            new Intent(ActionKinds.Wait)));
    }

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

using System.Text.Json;

namespace DramaBoard.Protocol.Tests;

public sealed class StableIdentifierTests
{
    [Fact]
    public void Equality_SameDecisionId_HasValueSemantics()
    {
        DecisionId first = new("decision-42");
        DecisionId second = new("decision-42");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new DecisionId("decision-43"));
    }

    [Fact]
    public void Equality_SameKindIdentifier_HasValueSemantics()
    {
        Assert.Equal(new ActionKind("action.travel"), ActionKinds.Travel);
        Assert.Equal(new DecisionReason("decision.scheduled"), DecisionReasons.Scheduled);
        Assert.Equal(new FactKind("fact.secret.known"), new FactKind("fact.secret.known"));
    }

    [Fact]
    public void Serialize_StableIdentifiers_UseStringWireShapeAndRoundTrip()
    {
        AssertStringRoundTrip(new DecisionId("decision-42"), "decision-42");
        AssertStringRoundTrip(new ActionKind("action.travel"), "action.travel");
        AssertStringRoundTrip(new DecisionReason("decision.scheduled"), "decision.scheduled");
        AssertStringRoundTrip(new FactKind("fact.secret.known"), "fact.secret.known");
    }

    private static void AssertStringRoundTrip<T>(T value, string expectedJsonValue)
    {
        string json = JsonSerializer.Serialize(value);

        Assert.Equal(JsonSerializer.Serialize(expectedJsonValue), json);
        Assert.Equal(value, JsonSerializer.Deserialize<T>(json));
    }
}
using System.Text.Json;

namespace DramaBoard.Protocol.Tests;

public sealed class IntentJsonTests
{
    public static TheoryData<Intent> FirstBoardActions => new()
    {
        new Intent(ActionKinds.Travel, DestinationId: "place.inn"),
        new Intent(ActionKinds.Wait, DurationMs: 15_000),
        new Intent(ActionKinds.Talk, TargetActorId: "actor.bob", FreeText: "Meet me at the inn."),
        new Intent(ActionKinds.Observe, TargetObjectId: "object.letter"),
        new Intent(ActionKinds.Take, TargetActorId: "actor.bob", TargetObjectId: "object.letter"),
        new Intent(ActionKinds.Give, TargetActorId: "actor.bob", TargetObjectId: "object.letter"),
    };

    [Theory]
    [MemberData(nameof(FirstBoardActions))]
    public void Serialize_FirstBoardAction_RoundTrips(Intent intent)
    {
        string json = JsonSerializer.Serialize(intent);
        Intent? roundTripped = JsonSerializer.Deserialize<Intent>(json);

        Assert.Equal(intent, roundTripped);
    }

    [Fact]
    public void Serialize_WaitUntilModelTime_RoundTrips()
    {
        Intent intent = new(ActionKinds.Wait, UntilModelTimeMs: 120_000);

        string json = JsonSerializer.Serialize(intent);

        Assert.Equal(intent, JsonSerializer.Deserialize<Intent>(json));
    }
}
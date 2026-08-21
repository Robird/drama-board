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
        new Intent(ActionKinds.Put, TargetObjectId: "object.letter"),
        new Intent(ActionKinds.Give, TargetActorId: "actor.bob", TargetObjectId: "object.letter"),
        new Intent(ActionKinds.Show, TargetActorId: "actor.bob", TargetObjectId: "object.letter"),
        new Intent(ActionKinds.Use, TargetObjectId: "object.locked-chest"),
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

    [Theory]
    [InlineData(0)]
    [InlineData(315_360_000_001)]
    public void Constructor_DurationOutsideSupportedRange_Throws(long durationMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Intent(ActionKinds.Wait, DurationMs: durationMs));
    }

    [Fact]
    public void Constructor_DurationAtSupportedBounds_Succeeds()
    {
        Assert.Equal(1, new Intent(ActionKinds.Wait, DurationMs: 1).DurationMs);
        Assert.Equal(
            315_360_000_000,
            new Intent(ActionKinds.Wait, DurationMs: 315_360_000_000).DurationMs);
    }

    [Fact]
    public void Deserialize_NegativeDuration_Throws()
    {
        const string json = "{\"ActionKind\":\"action.wait\",\"DurationMs\":-1}";

        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Deserialize<Intent>(json));
    }

    [Fact]
    public void Deserialize_OverlongFreeText_Throws()
    {
        string json = JsonSerializer.Serialize(new
        {
            ActionKind = "action.talk",
            FreeText = new string('x', 4_097),
        });

        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<Intent>(json));
    }
}

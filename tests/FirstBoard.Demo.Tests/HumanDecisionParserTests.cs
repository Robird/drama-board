using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class HumanDecisionParserTests
{
    public static TheoryData<string, Intent> SupportedCommands => new()
    {
        {
            "travel road.north hurry north",
            new Intent(ActionKinds.Travel, ExitId: "road.north", FreeText: "hurry north")
        },
        {
            "travel-to market stay alert",
            new Intent(ActionKinds.TravelTo, DestinationId: "market", FreeText: "stay alert")
        },
        {
            "continue press onward",
            new Intent(ActionKinds.ContinueTravel, FreeText: "press onward")
        },
        {
            "reverse go back",
            new Intent(ActionKinds.ReverseTravel, FreeText: "go back")
        },
        { "wait", new Intent(ActionKinds.Wait) },
        { "wait 60000", new Intent(ActionKinds.Wait, DurationMs: 60_000) },
        { "wait until 90000", new Intent(ActionKinds.Wait, UntilModelTimeMs: 90_000) },
        {
            "talk bob The gate is closing.",
            new Intent(ActionKinds.Talk, TargetActorId: "bob", FreeText: "The gate is closing.")
        },
        { "observe", new Intent(ActionKinds.Observe) },
        {
            "observe brass-key",
            new Intent(ActionKinds.Observe, TargetObjectId: "brass-key")
        },
        { "take brass-key", new Intent(ActionKinds.Take, TargetObjectId: "brass-key") },
        { "put brass-key", new Intent(ActionKinds.Put, TargetObjectId: "brass-key") },
        {
            "give bob brass-key",
            new Intent(ActionKinds.Give, TargetActorId: "bob", TargetObjectId: "brass-key")
        },
        {
            "show bob brass-key",
            new Intent(ActionKinds.Show, TargetActorId: "bob", TargetObjectId: "brass-key")
        },
        { "use chest", new Intent(ActionKinds.Use, TargetObjectId: "chest") },
    };

    [Theory]
    [MemberData(nameof(SupportedCommands))]
    public void Parse_SupportedCommandProducesExactProtocolIntent(
        string command,
        Intent expected)
    {
        HumanDecisionParseResult result = HumanDecisionParser.Parse(command);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Intent);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("travel")]
    [InlineData("talk bob")]
    [InlineData("observe first second")]
    [InlineData("give bob")]
    [InlineData("wait tomorrow")]
    [InlineData("wait 0")]
    [InlineData("wait until -1")]
    public void Parse_MalformedCommandReturnsLocalError(string? command)
    {
        HumanDecisionParseResult result = HumanDecisionParser.Parse(command);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}

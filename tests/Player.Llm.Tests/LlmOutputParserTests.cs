using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm.Tests;

public sealed class LlmOutputParserTests
{
    [Fact]
    public void Parse_StandardFourSections_MapsIntentAndUsesDialogueAsFreeText()
    {
        const string response = """
            【独白】
            我需要先稳住鲍勃。
            【行动】
            {"action":"action.talk","targetActor":"bob","freeText":"JSON 中的草稿"}
            【台词】
            鲍勃，我们谈谈。
            【记忆】
            鲍勃可能知道钥匙的位置。
            """;

        LlmOutputParseResult result = LlmOutputParser.Parse(response);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("我需要先稳住鲍勃。", result.Monologue);
        Assert.Equal("鲍勃，我们谈谈。", result.Dialogue);
        Assert.Equal("鲍勃，我们谈谈。", result.Intent!.FreeText);
        Assert.Equal(ActionKinds.Talk, result.Intent.ActionKind);
        Assert.Equal("bob", result.Intent.TargetActorId);
        Assert.Equal("鲍勃可能知道钥匙的位置。", result.Memory);
    }

    [Fact]
    public void Parse_MissingDialogue_PreservesActionFreeText()
    {
        const string response = """
            【独白】等待。
            【行动】{"action":"action.wait","freeText":"保持警觉","durationMs":60000}
            【记忆】继续观察。
            """;

        LlmOutputParseResult result = LlmOutputParser.Parse(response);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Null(result.Dialogue);
        Assert.Equal("保持警觉", result.Intent!.FreeText);
        Assert.Equal(60_000, result.Intent.DurationMs);
    }

    [Fact]
    public void Parse_JsonFenceAndWhitespaceAroundMarkers_Succeeds()
    {
        const string response = """
            【 独白 】 前往市场。
              【 行动 】
            ```json
            {"action":"action.travel","exit":"exit.market.bridge"}
            ```
              【 记忆 】 去市场找钥匙。
            """;

        LlmOutputParseResult result = LlmOutputParser.Parse(response);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ActionKinds.Travel, result.Intent!.ActionKind);
        Assert.Equal("exit.market.bridge", result.Intent.ExitId);
        Assert.Null(result.Intent.DestinationId);
    }

    [Fact]
    public void Parse_TravelTo_PreservesDestinationAndAppliesExistingFreeTextRules()
    {
        const string withoutDialogue = """
            【独白】先把目的地交给旅行能力。
            【行动】{"action":"action.travel-to","destination":"cellar","freeText":"去地窖"}
            【记忆】目的地是地窖。
            """;
        const string withDialogue = """
            【独白】告诉同行者我的打算。
            【行动】{"action":"action.travel-to","destination":"market","freeText":"JSON 草稿"}
            【台词】我们去市场。
            【记忆】先去市场。
            """;

        LlmOutputParseResult first = LlmOutputParser.Parse(withoutDialogue);
        LlmOutputParseResult second = LlmOutputParser.Parse(withDialogue);

        Assert.True(first.IsSuccess, first.Error);
        Assert.Equal(ActionKinds.TravelTo, first.Intent!.ActionKind);
        Assert.Equal("cellar", first.Intent.DestinationId);
        Assert.Null(first.Intent.ExitId);
        Assert.Null(first.Dialogue);
        Assert.Equal("去地窖", first.Intent.FreeText);

        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(ActionKinds.TravelTo, second.Intent!.ActionKind);
        Assert.Equal("market", second.Intent.DestinationId);
        Assert.Null(second.Intent.ExitId);
        Assert.Equal("我们去市场。", second.Dialogue);
        Assert.Equal("我们去市场。", second.Intent.FreeText);
    }

    [Fact]
    public void Parse_ProseAroundJson_Succeeds()
    {
        const string response = """
            【行动】好的，唯一的行动对象如下：
            {"action":"action.take","targetObject":"brass-key"}
            以上是我的选择。
            【记忆】我看见了钥匙。
            """;

        LlmOutputParseResult result = LlmOutputParser.Parse(response);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ActionKinds.Take, result.Intent!.ActionKind);
        Assert.Equal("brass-key", result.Intent.TargetObjectId);
    }

    [Fact]
    public void Parse_MissingActionSection_ReturnsFailure()
    {
        LlmOutputParseResult result = LlmOutputParser.Parse("【独白】我还没想好。【记忆】保持冷静。");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Intent);
        Assert.Contains("缺少【行动】", result.Error);
    }

    [Fact]
    public void Parse_InvalidActionJson_ReturnsFailure()
    {
        LlmOutputParseResult result = LlmOutputParser.Parse("【行动】{not-json}【记忆】保持冷静。");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Intent);
        Assert.NotNull(result.Error);
    }
}

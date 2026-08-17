using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm.Tests;

public sealed class LlmPlayerDriverTests
{
    private static readonly CharacterCard Character = new(
        "爱丽丝",
        "谨慎但好奇",
        "找到公爵夫人的信",
        "简短、克制");

    [Fact]
    public async Task DecideAsync_SuccessUpdatesMemoryAndReturnsCorrelatedDecision()
    {
        var backend = new FakeLlmBackend(
        [
            Response("action.observe", "第一次观察后，我记住了钥匙。"),
        ]);
        var driver = new LlmPlayerDriver(Character, "初始记忆", backend);
        DecisionRequest request = CreateRequest();

        PlayerDecision decision = await driver.DecideAsync(request, CancellationToken.None);

        Assert.Equal(request.DecisionId, decision.DecisionId);
        Assert.Equal(request.BasedOnWorldVersion, decision.BasedOnWorldVersion);
        Assert.Equal(request.LineageId, decision.LineageId);
        Assert.Equal(ActionKinds.Observe, decision.Intent.ActionKind);
        Assert.Equal("第一次观察后，我记住了钥匙。", driver.CurrentMemory);
        Assert.Single(backend.Requests);
        Assert.Contains("初始记忆", backend.Requests[0].User);
    }

    [Fact]
    public async Task DecideAsync_ParseFailureRetriesOnceWithCorrectionInstruction()
    {
        var backend = new FakeLlmBackend(
        [
            "我选择等待。",
            Response("action.wait", "重试后保持冷静。", "\"durationMs\":60000"),
        ]);
        var driver = new LlmPlayerDriver(Character, "初始记忆", backend);

        PlayerDecision decision = await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(ActionKinds.Wait, decision.Intent.ActionKind);
        Assert.Equal(60_000, decision.Intent.DurationMs);
        Assert.Equal("重试后保持冷静。", driver.CurrentMemory);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Contains("上次回复无法解析，请严格按格式", backend.Requests[1].User);
        Assert.Equal(backend.Requests[0].System, backend.Requests[1].System);
    }

    [Fact]
    public async Task DecideAsync_TwoParseFailuresReturnBareWaitWithoutChangingMemory()
    {
        var backend = new FakeLlmBackend(["无分节回复", "【行动】{bad-json}"]);
        var driver = new LlmPlayerDriver(Character, "不可丢失的记忆", backend);

        PlayerDecision decision = await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(new Intent(ActionKinds.Wait), decision.Intent);
        Assert.Null(decision.Intent.DurationMs);
        Assert.Equal("不可丢失的记忆", driver.CurrentMemory);
        Assert.Equal(2, backend.Requests.Count);
    }

    private static DecisionRequest CreateRequest() =>
        new(
            new DecisionId("decision.alice.1"),
            BasedOnWorldVersion: 12,
            LineageId: 10_001,
            ModelTimeMs: 300_000,
            Microstep: 2,
            ActorId: "alice",
            new Observation(
                "alice",
                "tavern",
                ModelTimeMs: 300_000,
                Microstep: 2,
                VisibleActorIds: [],
                VisibleObjectIds: [],
                KnownFacts: []),
            DecisionReasons.Scheduled,
            [new AvailableAction(ActionKinds.Wait), new AvailableAction(ActionKinds.Observe)]);

    private static string Response(string action, string memory, string? extraJson = null) =>
        $$"""
        【独白】测试独白。
        【行动】{"action":"{{action}}"{{(extraJson is null ? string.Empty : $",{extraJson}")}}}
        【台词】
        【记忆】{{memory}}
        """;
}

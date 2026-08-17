using DramaBoard.Protocol;
using System.Text.Json;

namespace DramaBoard.Player.Llm.Tests;

public sealed class LlmPlayerDriverTests
{
    private static readonly CharacterCard Character = new(
        "爱丽丝",
        "谨慎但好奇",
        "找到公爵夫人的信",
        "简短、克制");

    [Fact]
    public async Task DecideAsync_SuccessMaintainsMemoryAndReturnsCorrelatedDecision()
    {
        var actorBackend = new FakeLlmBackend(
        [
            Response("action.observe", "我希望记住钥匙。"),
        ]);
        var memoryBackend = new FakeLlmBackend(
        [
            Replace("## 当前处境 [working]\n第一次观察后，我记住了钥匙。"),
        ]);
        var driver = CreateSingleShardDriver("初始记忆", actorBackend, memoryBackend);
        DecisionRequest request = CreateRequest();

        PlayerDecision decision = await driver.DecideAsync(request, CancellationToken.None);

        Assert.Equal(request.DecisionId, decision.DecisionId);
        Assert.Equal(request.BasedOnWorldVersion, decision.BasedOnWorldVersion);
        Assert.Equal(request.LineageId, decision.LineageId);
        Assert.Equal(ActionKinds.Observe, decision.Intent.ActionKind);
        Assert.Equal("第一次观察后，我记住了钥匙。", driver.CurrentMemoryBank["working"].Content);
        Assert.DoesNotContain("## 当前处境", driver.CurrentMemoryBank["working"].Content);
        Assert.Single(actorBackend.Requests);
        Assert.Single(memoryBackend.Requests);
        Assert.Contains("初始记忆", actorBackend.Requests[0].User);
        Assert.Contains("角色希望记住的要点: 我希望记住钥匙。", memoryBackend.Requests[0].User);
    }

    [Fact]
    public async Task DecideAsync_ParseFailureRetriesOnceBeforeMaintainingMemory()
    {
        var actorBackend = new FakeLlmBackend(
        [
            "我选择等待。",
            Response("action.wait", "重试后想保持冷静。", "\"durationMs\":60000"),
        ]);
        var memoryBackend = new FakeLlmBackend([Replace("重试后保持冷静。")]);
        var driver = CreateSingleShardDriver("初始记忆", actorBackend, memoryBackend);

        PlayerDecision decision = await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(ActionKinds.Wait, decision.Intent.ActionKind);
        Assert.Equal(60_000, decision.Intent.DurationMs);
        Assert.Equal("重试后保持冷静。", driver.CurrentMemoryBank["working"].Content);
        Assert.Equal(2, actorBackend.Requests.Count);
        Assert.Single(memoryBackend.Requests);
        Assert.Contains("上次回复无法解析，请严格按格式", actorBackend.Requests[1].User);
        Assert.Equal(actorBackend.Requests[0].System, actorBackend.Requests[1].System);
    }

    [Fact]
    public async Task DecideAsync_TwoParseFailuresReturnBareWaitWithoutMaintainingMemory()
    {
        var actorBackend = new FakeLlmBackend(["无分节回复", "【行动】{bad-json}"]);
        var memoryBackend = new FakeLlmBackend([]);
        var driver = CreateSingleShardDriver("不可丢失的记忆", actorBackend, memoryBackend);

        PlayerDecision decision = await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(new Intent(ActionKinds.Wait), decision.Intent);
        Assert.Null(decision.Intent.DurationMs);
        Assert.Equal("不可丢失的记忆", driver.CurrentMemoryBank["working"].Content);
        Assert.Equal(2, actorBackend.Requests.Count);
        Assert.Empty(memoryBackend.Requests);
    }

    [Fact]
    public async Task DecideAsync_TraceIncludesProposalCommittedBankAndShardOperation()
    {
        var traces = new List<LlmTurnTrace>();
        var actorBackend = new FakeLlmBackend(
        [
            "无法解析",
            """
            【独白】我先观察四周。
            【行动】{"action":"action.observe"}
            【台词】有人在吗？
            【记忆】记下我已经主动询问过。
            """,
        ]);
        var memoryBackend = new FakeLlmBackend([Replace("我已观察并主动询问。")]);
        var driver = CreateSingleShardDriver("初始记忆", actorBackend, memoryBackend, traces.Add);

        PlayerDecision decision = await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        LlmTurnTrace trace = Assert.Single(traces);
        Assert.Equal(decision, trace.Decision);
        Assert.Equal("我先观察四周。", trace.Monologue);
        Assert.Equal("有人在吗？", trace.Dialogue);
        Assert.Equal("记下我已经主动询问过。", trace.MemoryProposal);
        Assert.Contains("我已观察并主动询问。", trace.Memory);
        MemoryShardMaintenanceTrace operation = Assert.Single(trace.MemoryMaintenance);
        Assert.Equal("working", operation.ShardKey);
        Assert.Equal(MemoryMaintenanceOperation.Replace, operation.Operation);
        Assert.Null(operation.Error);
        Assert.Equal(2, trace.AttemptCount);
        Assert.Equal(trace.Memory, driver.CurrentMemory);
    }

    [Fact]
    public async Task DecideAsync_ReferenceMaterialSurvivesIndependentMemoryReplacements()
    {
        var actorBackend = new FakeLlmBackend(
        [
            Response("action.observe", "我暂时相信纸条。"),
            Response("action.observe", "新证据让我怀疑纸条。"),
        ]);
        var memoryBackend = new FakeLlmBackend(
        [
            Replace("我暂时相信纸条。"),
            Replace("新证据让我认为纸条在说谎。"),
        ]);
        var driver = CreateSingleShardDriver(
            "尚未判断。",
            actorBackend,
            memoryBackend,
            referenceMaterials:
            [
                new("anonymous-note", "匿名纸条", "鲍勃声称钥匙在地窖。"),
            ]);

        await driver.DecideAsync(CreateRequest(), CancellationToken.None);
        await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("新证据让我认为纸条在说谎。", driver.CurrentMemoryBank["working"].Content);
        Assert.All(actorBackend.Requests, request =>
        {
            Assert.Contains("[anonymous-note] 来源: 匿名纸条", request.User);
            Assert.Contains("鲍勃声称钥匙在地窖。", request.User);
        });
        Assert.All(memoryBackend.Requests, request =>
            Assert.Contains("[anonymous-note] 来源: 匿名纸条", request.User));
        Assert.Contains("我暂时相信纸条。", actorBackend.Requests[1].User);
    }

    [Fact]
    public async Task DecideAsync_IndependentMaintainersSeeFullOldBankAndUpdateOnlyOwnedShard()
    {
        MemoryBank initial = new(
        [
            new MemoryShard("working", "当前处境", "快速维护近期处境。", "我在酒馆。"),
            new MemoryShard("commitments", "承诺与计划", "未完成时默认保留。", "去集市等鲍勃五分钟。"),
        ]);
        var actorBackend = new FakeLlmBackend(
        [
            Response("action.travel", "已经动身。", "\"destination\":\"market\""),
        ]);
        var workingBackend = new FakeLlmBackend([Keep]);
        var commitmentsBackend = new FakeLlmBackend(
        [
            Replace("继续去集市，并在那里等鲍勃五分钟。"),
        ]);
        var traces = new List<LlmTurnTrace>();
        var driver = new LlmPlayerDriver(
            Character,
            initial,
            actorBackend,
            [
                new LlmMemoryShardMaintainer("working", workingBackend),
                new LlmMemoryShardMaintainer("commitments", commitmentsBackend),
            ],
            traces.Add);

        await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("我在酒馆。", driver.CurrentMemoryBank["working"].Content);
        Assert.Equal(
            "继续去集市，并在那里等鲍勃五分钟。",
            driver.CurrentMemoryBank["commitments"].Content);
        Assert.Contains("我在酒馆。", workingBackend.Requests[0].User);
        Assert.Contains("去集市等鲍勃五分钟。", workingBackend.Requests[0].User);
        Assert.Contains("我在酒馆。", commitmentsBackend.Requests[0].User);
        Assert.Contains("去集市等鲍勃五分钟。", commitmentsBackend.Requests[0].User);
        Assert.Collection(
            Assert.Single(traces).MemoryMaintenance,
            trace => Assert.Equal(MemoryMaintenanceOperation.Keep, trace.Operation),
            trace => Assert.Equal(MemoryMaintenanceOperation.Replace, trace.Operation));
    }

    [Fact]
    public async Task DecideAsync_MalformedMaintainerOutputFallsBackToKeepingOnlyThatShard()
    {
        MemoryBank initial = new(
        [
            new MemoryShard("working", "当前处境", "维护当前处境。", "旧处境"),
            new MemoryShard("beliefs", "判断", "维护可疑判断。", "旧判断"),
        ]);
        var actorBackend = new FakeLlmBackend([Response("action.observe", "获得了新线索。")]);
        var traces = new List<LlmTurnTrace>();
        var driver = new LlmPlayerDriver(
            Character,
            initial,
            actorBackend,
            [
                new LlmMemoryShardMaintainer("working", new FakeLlmBackend(["不是 JSON"])),
                new LlmMemoryShardMaintainer("beliefs", new FakeLlmBackend([Replace("新判断")])),
            ],
            traces.Add);

        PlayerDecision decision = await driver.DecideAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(ActionKinds.Observe, decision.Intent.ActionKind);
        Assert.Equal("旧处境", driver.CurrentMemoryBank["working"].Content);
        Assert.Equal("新判断", driver.CurrentMemoryBank["beliefs"].Content);
        Assert.Collection(
            Assert.Single(traces).MemoryMaintenance,
            trace =>
            {
                Assert.Equal(MemoryMaintenanceOperation.FallbackKeep, trace.Operation);
                Assert.Contains("FormatException", trace.Error);
            },
            trace => Assert.Equal(MemoryMaintenanceOperation.Replace, trace.Operation));
    }

    private static LlmPlayerDriver CreateSingleShardDriver(
        string initialMemory,
        ILlmChatBackend actorBackend,
        ILlmChatBackend memoryBackend,
        Action<LlmTurnTrace>? traceSink = null,
        IReadOnlyList<ReferenceMaterial>? referenceMaterials = null) =>
        new(
            Character,
            new MemoryBank(
            [
                new MemoryShard("working", "当前处境", "维护当前处境与近期线索。", initialMemory),
            ]),
            actorBackend,
            [new LlmMemoryShardMaintainer("working", memoryBackend)],
            traceSink,
            referenceMaterials);

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

    private static string Response(string action, string memoryProposal, string? extraJson = null) =>
        $$"""
        【独白】测试独白。
        【行动】{"action":"{{action}}"{{(extraJson is null ? string.Empty : $",{extraJson}")}}}
        【台词】
        【记忆】{{memoryProposal}}
        """;

    private static string Replace(string content) =>
        JsonSerializer.Serialize(new { operation = "replace", content });

    private const string Keep = "{\"operation\":\"keep\"}";
}

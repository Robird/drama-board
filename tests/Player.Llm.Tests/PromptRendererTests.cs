using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm.Tests;

public sealed class PromptRendererTests
{
    private static readonly CharacterCard Character = new(
        "爱丽丝",
        "谨慎但好奇",
        "找到公爵夫人的信",
        "简短、克制");

    [Fact]
    public void Render_IncludesSixSectionsCharacterAndAvailableActions()
    {
        DecisionRequest request = CreateRequest();

        LlmChatRequest rendered = PromptRenderer.Render(Character, Memory("我正在寻找钥匙。"), request, []);

        Assert.Contains("[角色卡]", rendered.System);
        Assert.Contains("名字: 爱丽丝", rendered.System);
        Assert.Contains("[世界规则]", rendered.System);
        Assert.Contains("重复观察不会发现暗格或更深线索", rendered.System);
        Assert.Contains("put 会把自持物放到当前公共环境并失去所有权", rendered.System);
        Assert.Contains("把已知事实当作权威结果", rendered.System);
        Assert.Contains("【独白】", rendered.System);
        Assert.Contains("【行动】", rendered.System);
        Assert.Contains("【台词】", rendered.System);
        Assert.Contains("【记忆】", rendered.System);
        Assert.Contains("[内心状态]", rendered.User);
        Assert.Contains("[当前观察]", rendered.User);
        Assert.Contains("[新近变化]", rendered.User);
        Assert.Contains("[决策请求]", rendered.User);
        Assert.Contains("action.travel", rendered.User);
        Assert.Contains("destinationCandidates=[market]", rendered.User);
    }

    [Fact]
    public void Render_KnownFactsDiffUsesRecordValueEqualityAndIsDeterministic()
    {
        var oldFact = new KnownFact(new FactKind("fact.old"), "old", "旧事实");
        var newFact = new KnownFact(new FactKind("fact.new"), "new", "新事实");
        DecisionRequest request = CreateRequest([oldFact, newFact]);
        KnownFact[] previous = [oldFact with { }];

        LlmChatRequest first = PromptRenderer.Render(Character, Memory("记忆"), request, previous);
        LlmChatRequest second = PromptRenderer.Render(Character, Memory("记忆"), request, previous);

        Assert.Equal(first, second);
        string changes = Section(first.User, "[新近变化]", "[决策请求]");
        Assert.Contains("新事实", changes);
        Assert.DoesNotContain("旧事实", changes);
    }

    [Fact]
    public void Render_IncludesRejectedIntent()
    {
        DecisionRequest request = CreateRequest() with
        {
            Reason = DecisionReasons.ActionRejected,
            RejectedIntent = new Intent(ActionKinds.Travel, DestinationId: "cellar"),
        };

        LlmChatRequest rendered = PromptRenderer.Render(Character, Memory("记忆"), request, []);

        string changes = Section(rendered.User, "[新近变化]", "[决策请求]");
        Assert.Contains("上次尝试被拒绝", changes);
        Assert.Contains("action=action.travel", changes);
        Assert.Contains("destination=cellar", changes);
    }

    [Fact]
    public void Render_ReferenceMaterialPreservesSourceWithoutAssertingBelief()
    {
        ReferenceMaterial[] materials =
        [
            new("anonymous-note", "夹在门缝里的匿名纸条", "鲍勃会在正午背叛你。"),
        ];

        LlmChatRequest rendered = PromptRenderer.Render(
            Character,
            Memory("我怀疑纸条是在挑拨。"),
            CreateRequest(),
            [],
            materials);

        Assert.Contains("[可反复查阅的材料]", rendered.User);
        Assert.Contains("[anonymous-note] 来源: 夹在门缝里的匿名纸条", rendered.User);
        Assert.Contains("原文: 鲍勃会在正午背叛你。", rendered.User);
        Assert.Contains("我怀疑纸条是在挑拨。", rendered.User);
        Assert.Contains("不保证内容真实", rendered.System);
        Assert.Contains("你可以怀疑、重新解释或不再采信", rendered.User);
    }

    private static DecisionRequest CreateRequest(IReadOnlyList<KnownFact>? knownFacts = null) =>
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
                VisibleActorIds: ["bob"],
                VisibleObjectIds: ["brass-key"],
                KnownFacts: knownFacts ?? []),
            DecisionReasons.Scheduled,
            [
                new AvailableAction(
                    ActionKinds.Travel,
                    CandidateDestinationIds: ["market"]),
                new AvailableAction(ActionKinds.Wait),
            ]);

    private static MemoryBank Memory(string content) =>
        new(
        [
            new MemoryShard("working", "当前处境", "维护当前处境。", content),
        ]);

    private static string Section(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        return text[startIndex..endIndex];
    }
}

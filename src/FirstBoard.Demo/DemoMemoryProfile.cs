using DramaBoard.Player.Llm;

namespace DramaBoard.FirstBoard.Demo;

internal static class DemoMemoryProfile
{
    public static MemoryBank Alice() => Create(
        working:
            "我在酒馆，准备按案情笔记去集市寻找钥匙和鲍勃；当前尚未核实密信、钥匙或地窖传闻。",
        commitments:
            "我目前打算遵守 alice.meeting-note：若在集市与鲍勃错身，至少等五分钟。拿到密信后先检查，再决定是否支付余款；情况明显变化时可以明确修改计划。",
        beliefs:
            "案情笔记只是零散传闻。钥匙很可能与地窖箱有关；鲍勃可能帮忙，也可能借机要挟。",
        relationships:
            "我对鲍勃保持戒备，但目前没有足够证据认定他会违约；双方尚无已履行的交易债务。");

    public static MemoryBank Bob() => Create(
        working:
            "我在集市，准备先确认摊位附近的钥匙是否还在；当前尚未核实账本边角记录。",
        commitments:
            "我目前愿意遵守 bob.meeting-note：若先去地窖开箱，之后回集市至少等爱丽丝十分钟；我可以根据风险与收益明确改主意。",
        beliefs:
            "钥匙可能让我取得密信筹码，但拖得太久可能一无所获。爱丽丝似乎很在意那封信。",
        relationships:
            "我把爱丽丝视为潜在交易对象而不是同盟；是否履约取决于她实际展示或交付的筹码。");

    public static IReadOnlyList<IMemoryShardMaintainer> Maintainers(
        MemoryBank memory,
        ILlmChatBackend backend) =>
        memory.Shards
            .Select(shard => (IMemoryShardMaintainer)new LlmMemoryShardMaintainer(shard.Key, backend))
            .ToArray();

    private static MemoryBank Create(
        string working,
        string commitments,
        string beliefs,
        string relationships) =>
        new(
        [
            new MemoryShard(
                "working_context",
                "当前处境与未决线索",
                "快速更新当前处境、近期经历和仍需处理的线索；无需重复 Observation 已直接给出的琐碎状态。",
                working),
            new MemoryShard(
                "commitments",
                "承诺与计划",
                "保存约定、承诺、期限和多步计划。未完成事项默认 keep；只有完成、明确放弃、已不可能或被新计划取代时才修改，并写明理由。",
                commitments),
            new MemoryShard(
                "beliefs",
                "判断与假说",
                "维护角色自己的猜想、证据来源、反证和置信变化；区分材料写了什么、他人说了什么与自己相信什么。",
                beliefs),
            new MemoryShard(
                "relationships",
                "关系与社会账本",
                "缓慢维护信任、戒备、情绪、恩怨和已履行或未履行的债务；中性事件通常 keep，变化应保留依据。",
                relationships),
        ]);
}

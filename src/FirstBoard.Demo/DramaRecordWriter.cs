using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Journal;
using DramaBoard.Player.Llm;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo;

internal static class DramaRecordWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Write(
        DemoOptions options,
        BoardRunCapture capture,
        IReadOnlyList<LlmTurnTrace> traces,
        int budgetForcedCount)
    {
        string path = Path.Combine(options.OutputDirectory, "drama-record.md");
        var text = new StringBuilder()
            .AppendLine("# DramaBoard · FirstBoard 首场记录")
            .AppendLine()
            .Append("- 后端：").Append(options.Backend).Append(" / ").AppendLine(options.Model)
            .Append("- 世界种子：").AppendLine(options.WorldSeed.ToString(CultureInfo.InvariantCulture))
            .Append("- 结束：").Append(capture.Result.StopReason)
            .Append(" @ ").Append(capture.Result.Cursor.Now.Ticks.ToString(CultureInfo.InvariantCulture))
            .AppendLine("ms")
            .Append("- 世界事件：").AppendLine(capture.Journal.Events.Count.ToString(CultureInfo.InvariantCulture))
            .Append("- 成功解析的 LLM turn：").AppendLine(traces.Count.ToString(CultureInfo.InvariantCulture))
            .Append("- turn 预算触发的收场等待：").AppendLine(budgetForcedCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .AppendLine("## 世界终局")
            .AppendLine()
            .Append("- 地窖：").AppendLine(capture.Result.World.CellarSealed ? "已封闭" : "仍开放")
            .Append("- 黄铜钥匙：").AppendLine(ObjectLocation(capture.Result.World))
            .Append("- 爱丽丝：").AppendLine(ActorSummary(capture.Result.World, BoardIds.Alice))
            .Append("- 鲍勃：").AppendLine(ActorSummary(capture.Result.World, BoardIds.Bob))
            .AppendLine()
            .AppendLine("## 世界事件叙事 dump")
            .AppendLine();

        foreach (DomainEvent<BoardEventPayload> domainEvent in capture.Journal.Events)
        {
            text.Append("- **").Append(FormatTime(domainEvent.Timestamp.ModelTime.Ticks))
                .Append(" / μ").Append(domainEvent.Timestamp.Microstep.Value.ToString(CultureInfo.InvariantCulture))
                .Append("** ").Append(RenderEvent(domainEvent.Payload))
                .Append("  `").Append(domainEvent.Kind.Id).AppendLine("`");
        }

        text.AppendLine()
            .AppendLine("## 演员内心轨迹")
            .AppendLine();
        foreach (LlmTurnTrace trace in traces)
        {
            text.Append("### ").Append(DisplayActor(trace.Request.ActorId))
                .Append(" · ").AppendLine(trace.Request.DecisionId.Value)
                .AppendLine()
                .Append("> ").AppendLine(trace.Monologue.Replace("\n", "\n> ", StringComparison.Ordinal))
                .AppendLine()
                .Append("- 行动：").AppendLine(FormatIntent(trace.Decision.Intent))
                .Append("- 台词：").AppendLine(trace.Dialogue ?? "（无）")
                .Append("- 更新记忆：").AppendLine(trace.Memory)
                .AppendLine();
        }

        File.WriteAllText(path, text.ToString(), Utf8NoBom);
        return path;
    }

    public static string FormatIntent(Intent intent) =>
        intent.ActionKind.Id switch
        {
            "action.travel" => $"前往 {DisplayPlace(intent.DestinationId)}",
            "action.wait" => $"等待 {intent.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "默认"}ms",
            "action.talk" => $"与 {DisplayActor(intent.TargetActorId)} 交谈",
            "action.observe" => "观察四周",
            "action.take" => $"拿取 {DisplayObject(intent.TargetObjectId)}",
            "action.give" => $"把 {DisplayObject(intent.TargetObjectId)} 交给 {DisplayActor(intent.TargetActorId)}",
            _ => intent.ActionKind.Id,
        };

    private static string RenderEvent(BoardEventPayload payload) =>
        payload switch
        {
            DecisionRequestedEvent value => $"轮到{DisplayActor(value.ActorId)}决定下一步。",
            ActionRequestedEvent value => $"{DisplayActor(value.ActorId)}选择：{FormatIntent(value.Intent)}。",
            ActorDepartedEvent value =>
                $"{DisplayActor(value.ActorId)}离开{DisplayPlace(value.OriginId)}，前往{DisplayPlace(value.DestinationId)}。",
            ActorArrivedEvent value =>
                $"{DisplayActor(value.ActorId)}抵达{DisplayPlace(value.DestinationId)}。",
            ActorWaitStartedEvent value =>
                $"{DisplayActor(value.ActorId)}开始等待，预计到 {FormatTime(value.CompleteAt.Ticks)}。",
            ActorWaitedEvent value => $"{DisplayActor(value.ActorId)}结束等待。",
            ActorSpokeEvent value =>
                $"{DisplayActor(value.ActorId)}对{DisplayActor(value.TargetActorId)}说：“{value.Text}”",
            ActorObservedEvent value => value.LearnedFacts.Count == 0
                ? $"{DisplayActor(value.ActorId)}环顾四周，没有获得新线索。"
                : $"{DisplayActor(value.ActorId)}观察并记住：{string.Join("；", value.LearnedFacts.Select(fact => fact.Text))}",
            ObjectTakenEvent value =>
                $"{DisplayActor(value.ActorId)}拿到了{DisplayObject(value.ObjectId)}。",
            ObjectGivenEvent value =>
                $"{DisplayActor(value.ActorId)}把{DisplayObject(value.ObjectId)}交给{DisplayActor(value.TargetActorId)}。",
            ObjectContentionResolvedEvent value =>
                $"众人争抢{DisplayObject(value.ObjectId)}，最终由演员 #{value.WinnerActorId} 得手。",
            ActionRejectedEvent value =>
                $"{DisplayActor(value.ActorId)}的行动被世界拒绝：{value.Reason}。",
            CellarSealedEvent => "钟声响起，地窖永久封闭。",
            _ => payload.GetType().Name,
        };

    private static string ActorSummary(FirstBoardWorld world, string actorId)
    {
        BoardActor actor = world.Actor(actorId);
        string facts = actor.KnownFacts.Count == 0
            ? "无关键认知"
            : string.Join("；", actor.KnownFacts.Select(fact => fact.Text));
        return $"位于{DisplayPlace(actor.PlaceId)}；{facts}";
    }

    private static string ObjectLocation(FirstBoardWorld world)
    {
        BoardObject item = world.Object(BoardIds.BrassKey);
        if (item.OwnerActorId is long ownerId)
        {
            return $"由{DisplayActor(world.Actor(ownerId).Key)}持有";
        }

        return $"位于{DisplayPlace(item.PlaceId)}";
    }

    private static string DisplayActor(string? actorId) =>
        actorId switch
        {
            BoardIds.Alice => "爱丽丝",
            BoardIds.Bob => "鲍勃",
            null => "（无人）",
            _ => actorId,
        };

    private static string DisplayPlace(string? placeId) =>
        placeId switch
        {
            BoardIds.Tavern => "酒馆",
            BoardIds.Market => "集市",
            BoardIds.Cellar => "地窖",
            null => "（未知地点）",
            _ => placeId,
        };

    private static string DisplayObject(string? objectId) =>
        objectId switch
        {
            BoardIds.BrassKey => "黄铜钥匙",
            BoardIds.LockedChest => "上锁的箱子",
            null => "（未知物品）",
            _ => objectId,
        };

    private static string FormatTime(long ticks)
    {
        long minutes = ticks / 60_000;
        long seconds = ticks % 60_000 / 1_000;
        return $"{minutes:00}:{seconds:00}";
    }
}

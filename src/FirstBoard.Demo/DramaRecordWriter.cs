using System.Globalization;
using System.Text;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Player.Llm;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Demo;

internal static class DramaRecordWriter
{
    private static readonly UTF8Encoding Utf8NoBom =
        new(encoderShouldEmitUTF8Identifier: false);

    public static string Write(
        DemoOptions options,
        ScenarioInstance scenarioInstance,
        BoardRunCapture capture,
        IReadOnlyList<LlmTurnTrace> traces,
        int budgetForcedCount)
    {
        string path = Path.Combine(options.OutputDirectory, "drama-record.md");
        var text = new StringBuilder()
            .AppendLine("# DramaBoard · FirstBoard 首场记录")
            .AppendLine()
            .Append("- 爱丽丝后端：").Append(options.AliceBackend.Backend).Append(" / ")
            .AppendLine(options.AliceBackend.Model)
            .Append("- 鲍勃后端：").Append(options.BobBackend.Backend).Append(" / ")
            .AppendLine(options.BobBackend.Model)
            .Append("- 记忆维护后端：").Append(options.MemoryBackend.Backend).Append(" / ")
            .AppendLine(options.MemoryBackend.Model)
            .Append("- 记忆维护调度：").AppendLine(
                options.MemoryMaintenanceMode.ToString().ToLowerInvariant())
            .Append("- 场景定义：").Append(scenarioInstance.Definition.Id)
            .Append(" @ revision ").AppendLine(
                scenarioInstance.Definition.Revision.ToString(CultureInfo.InvariantCulture))
            .Append("- Definition SHA-256：").AppendLine(scenarioInstance.DefinitionSha256)
            .Append("- Instance SHA-256：").AppendLine(scenarioInstance.InstanceSha256)
            .Append("- 世界种子：").AppendLine(
                options.WorldSeed.ToString(CultureInfo.InvariantCulture))
            .Append("- 结束：").Append(capture.Result.Status)
            .Append(" @ ").Append(
                capture.Result.CurrentModelTime.Ticks.ToString(CultureInfo.InvariantCulture))
            .AppendLine("ms")
            .Append("- 世界 transition：").AppendLine(
                capture.Journal.Batches.Count.ToString(CultureInfo.InvariantCulture))
            .Append("- 成功解析的 LLM turn：").AppendLine(
                traces.Count.ToString(CultureInfo.InvariantCulture))
            .Append("- turn 预算触发的收场等待：").AppendLine(
                budgetForcedCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .AppendLine("## 世界终局")
            .AppendLine()
            .Append("- 地窖入口：").AppendLine(
                capture.Result.World.CellarSealed ? "已禁止进入" : "仍开放")
            .Append("- 锁箱：").AppendLine(
                capture.Result.World.ChestOpened ? "已打开" : "仍上锁")
            .Append("- 黄铜钥匙：").AppendLine(ObjectLocation(capture.Result.World))
            .Append("- 公爵夫人的密信：").AppendLine(
                ObjectLocation(capture.Result.World, BoardIds.DuchessLetter))
            .Append("- 银币一：").AppendLine(
                ObjectLocation(capture.Result.World, BoardIds.SilverCoinOne))
            .Append("- 银币二：").AppendLine(
                ObjectLocation(capture.Result.World, BoardIds.SilverCoinTwo))
            .Append("- 爱丽丝：").AppendLine(
                ActorSummary(capture.Result.World, BoardIds.Alice))
            .Append("- 鲍勃：").AppendLine(
                ActorSummary(capture.Result.World, BoardIds.Bob))
            .AppendLine()
            .AppendLine("## 世界事件叙事 dump")
            .AppendLine();

        foreach (JournalBatch<FirstBoardFact> batch in capture.Journal.Batches)
        {
            foreach (FirstBoardFact fact in batch.Facts)
            {
                text.Append("- **").Append(FormatTime(batch.Instant.ModelTime.Ticks))
                    .Append(" / #").Append(
                        batch.Instant.CausalOrdinal.ToString(CultureInfo.InvariantCulture))
                    .Append("** ").Append(RenderEvent(fact))
                    .Append("  `").Append(FirstBoardScenario.FactName(fact)).AppendLine("`");
            }
        }

        text.AppendLine()
            .AppendLine("## 演员内心轨迹")
            .AppendLine();
        foreach (LlmTurnTrace trace in traces)
        {
            text.Append("### ").Append(DisplayActor(trace.Request.ActorId))
                .Append(" · ").AppendLine(trace.Request.DecisionId.Value)
                .AppendLine()
                .Append("> ").AppendLine(
                    trace.Monologue.Replace("\n", "\n> ", StringComparison.Ordinal))
                .AppendLine()
                .Append("- 行动：").AppendLine(FormatIntent(trace.Decision.Intent))
                .Append("- 台词：").AppendLine(trace.Dialogue ?? "（无）")
                .Append("- 记忆提议：").AppendLine(trace.MemoryProposal)
                .Append("- 分块维护：").AppendLine(string.Join(
                    "; ",
                    trace.MemoryMaintenance.Select(result =>
                        $"{result.ShardKey}={result.Operation}")))
                .AppendLine("- 更新后 MemoryBank：")
                .AppendLine(trace.Memory)
                .AppendLine();
        }

        File.WriteAllText(path, text.ToString(), Utf8NoBom);
        return path;
    }

    public static string FormatIntent(Intent intent) =>
        intent.ActionKind.Id switch
        {
            "action.travel" => $"选择出口 {intent.ExitId}",
            "action.wait" =>
                $"等待 {intent.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "默认"}ms",
            "action.talk" => $"与 {DisplayActor(intent.TargetActorId)} 交谈",
            "action.observe" => intent.TargetObjectId is null
                ? "观察四周"
                : $"检查 {DisplayObject(intent.TargetObjectId)}",
            "action.take" => $"拿取 {DisplayObject(intent.TargetObjectId)}",
            "action.put" => $"把 {DisplayObject(intent.TargetObjectId)} 放到当前公共环境",
            "action.give" =>
                $"把 {DisplayObject(intent.TargetObjectId)} 交给 {DisplayActor(intent.TargetActorId)}",
            "action.show" =>
                $"向 {DisplayActor(intent.TargetActorId)} 展示 {DisplayObject(intent.TargetObjectId)}",
            "action.use" => $"使用 {DisplayObject(intent.TargetObjectId)}",
            _ => intent.ActionKind.Id,
        };

    private static string RenderEvent(FirstBoardFact fact) => fact switch
    {
        GameBoardFact game => RenderGameEvent(game.Value),
        SpatialBoardFact spatial => RenderSpatialEvent(spatial.Value),
        _ => fact.GetType().Name,
    };

    private static string RenderGameEvent(BoardEventPayload payload) => payload switch
    {
        ActorTravelStartedEvent value =>
            $"{DisplayActor(value.ActorId)}选择{value.ExitId}，前往{DisplayPlace(value.DestinationId)}。",
        TicketConsumedEvent value =>
            $"{DisplayActor(value.ActorId)}消耗了{DisplayObject(value.TicketObjectId)}作为通行凭证。",
        ActorWaitStartedEvent value =>
            $"{DisplayActor(value.ActorId)}开始等待，预计到 {FormatTime(value.CompleteAt.Ticks)}。",
        ActorWaitedEvent value => $"{DisplayActor(value.ActorId)}结束等待。",
        ActorSpokeEvent value =>
            $"{DisplayActor(value.ActorId)}对{DisplayActor(value.TargetActorId)}说：“{value.Text}”",
        ActorObservedEvent value when value.TargetObjectId is not null =>
            $"{DisplayActor(value.ActorId)}仔细检查了{DisplayObject(value.TargetObjectId)}并确认：" +
            string.Join("；", value.LearnedFacts.Select(boardFact => boardFact.Text)),
        ActorObservedEvent value => value.LearnedFacts.Count == 0
            ? $"{DisplayActor(value.ActorId)}环顾四周，没有获得新线索。"
            : $"{DisplayActor(value.ActorId)}观察并记住：" +
              string.Join("；", value.LearnedFacts.Select(boardFact => boardFact.Text)),
        ObjectTakenEvent value =>
            $"{DisplayActor(value.ActorId)}拿到了{DisplayObject(value.ObjectId)}。",
        ObjectPlacedEvent value =>
            $"{DisplayActor(value.ActorId)}把{DisplayObject(value.ObjectId)}放在" +
            $"{DisplayPlace(value.PlaceId)}。",
        ObjectGivenEvent value =>
            $"{DisplayActor(value.ActorId)}把{DisplayObject(value.ObjectId)}交给" +
            $"{DisplayActor(value.TargetActorId)}。",
        ObjectShownEvent value =>
            $"{DisplayActor(value.ActorId)}向{DisplayActor(value.TargetActorId)}展示了" +
            $"{DisplayObject(value.ObjectId)}。",
        ChestOpenedEvent value =>
            $"{DisplayActor(value.ActorId)}用{DisplayObject(value.KeyObjectId)}打开了" +
            $"{DisplayObject(value.ObjectId)}。",
        ActionRejectedEvent value =>
            $"{DisplayActor(value.ActorId)}的行动被世界拒绝：{value.Reason}。",
        CellarSealedEvent => "钟声响起，地窖入口禁止继续进入。",
        _ => payload.GetType().Name,
    };

    private static string RenderSpatialEvent(GraphSpatialFact payload) => payload switch
    {
        EntityPlacedFact value =>
            $"{DisplayObject(value.EntityId.Value)}被放置在{DisplayPlace(value.PlaceId.Value)}。",
        EntityRemovedFact value =>
            $"{DisplayObject(value.EntityId.Value)}离开了独立空间位置。",
        TraversalStartedFact value =>
            $"{DisplayActor(value.EntityId.Value)}从{DisplayPlace(value.FromPlaceId.Value)}" +
            $"进入通道 {value.PassageId.Value}。",
        TraversalArrivedFact value =>
            $"{DisplayActor(value.EntityId.Value)}完成第{value.ExpectedMovementGeneration}段旅行并抵达。",
        PassageEntryAccessChangedFact value =>
            $"通道 {value.PassageId.Value} 的入口状态变为 " +
            $"A={value.ResultAccess.EnterableFromA}, B={value.ResultAccess.EnterableFromB}。",
        PassageEntryChangeScheduledFact value =>
            $"通道 {value.PassageId.Value} 安排在 {FormatTime(value.Due.Ticks)} 改变入口。",
        ScheduledPassageEntryChangeAppliedFact value =>
            $"通道 {value.PassageId.Value} 的预定入口变化已经生效。",
        _ => payload.GetType().Name,
    };

    private static string ActorSummary(FirstBoardWorld world, string actorId)
    {
        BoardActor actor = world.Actor(actorId);
        string facts = actor.KnownFacts.Count == 0
            ? "无关键认知"
            : string.Join("；", actor.KnownFacts.Select(fact => fact.Text));
        SpatialEntity entity = world.Spatial.Entities.Single(value =>
            value.Id == new EntityId(actorId));
        return $"{LocationSummary(entity.Location)}；{facts}";
    }

    private static string ObjectLocation(
        FirstBoardWorld world,
        string objectId = BoardIds.BrassKey)
    {
        BoardObject? item = world.Objects.SingleOrDefault(value => value.Key == objectId);
        if (item?.OwnerActorId is long ownerId)
        {
            return $"由{DisplayActor(world.Actor(ownerId).Key)}持有";
        }

        if (objectId == BoardIds.DuchessLetter && !world.ChestOpened)
        {
            return "仍封存在锁箱内";
        }

        if (world.Spatial.TryGetEntity(new EntityId(objectId), out SpatialEntity? entity))
        {
            return LocationSummary(entity!.Location);
        }

        return item is null ? "不在当前物品模型中" : "已消耗或隐藏";
    }

    private static string LocationSummary(SpatialLocation location) => location switch
    {
        AtPlaceLocation atPlace => $"位于{DisplayPlace(atPlace.PlaceId.Value)}",
        TraversingLocation traversing =>
            $"正沿 {traversing.PassageId.Value} 从{DisplayPlace(traversing.FromPlaceId.Value)}" +
            $"前往{DisplayPlace(traversing.ToPlaceId.Value)}，ETA {FormatTime(traversing.ArrivalDue.Ticks)}",
        _ => "空间位置未知",
    };

    private static string DisplayActor(string? actorId) => actorId switch
    {
        BoardIds.Alice => "爱丽丝",
        BoardIds.Bob => "鲍勃",
        null => "（无人）",
        _ => actorId,
    };

    private static string DisplayPlace(string? placeId) => placeId switch
    {
        BoardIds.Tavern => "酒馆",
        BoardIds.Market => "集市",
        BoardIds.CellarGate => "地窖门外",
        BoardIds.Cellar => "地窖",
        null => "（未知地点）",
        _ => placeId,
    };

    private static string DisplayObject(string? objectId) => objectId switch
    {
        BoardIds.BrassKey => "黄铜钥匙",
        BoardIds.LockedChest => "上锁的箱子",
        BoardIds.DuchessLetter => "公爵夫人的密信",
        BoardIds.SilverCoinOne => "第一枚银币",
        BoardIds.SilverCoinTwo => "第二枚银币",
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

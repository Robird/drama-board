using System.Globalization;
using System.Text;
using DramaBoard.FirstBoard;
using DramaBoard.FirstBoard.Demo.Live;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;
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
        int budgetForcedCount) =>
        WriteCore(
            options,
            scenarioInstance,
            capture.Result.Status.ToString(),
            capture.Result.CurrentModelTime,
            capture.Result.World,
            capture.InitialCursor,
            capture.Result.Version,
            capture.CompletedEvents,
            traces,
            budgetForcedCount);

    public static string WriteCanceled(
        DemoOptions options,
        ScenarioInstance scenarioInstance,
        LiveSessionCanceledCapture capture,
        IReadOnlyList<LlmTurnTrace> traces,
        int budgetForcedCount) =>
        WriteCore(
            options,
            scenarioInstance,
            "Canceled",
            capture.CurrentModelTime,
            capture.World,
            capture.InitialCursor,
            capture.Version,
            capture.CompletedEvents,
            traces,
            budgetForcedCount);

    private static string WriteCore(
        DemoOptions options,
        ScenarioInstance scenarioInstance,
        string status,
        ModelTime currentModelTime,
        FirstBoardWorld world,
        KernelCursor initialCursor,
        WorldVersion finalVersion,
        IReadOnlyList<OccurrenceEvent<FirstBoardFact>> events,
        IReadOnlyList<LlmTurnTrace> traces,
        int budgetForcedCount)
    {
        string path = Path.Combine(options.OutputDirectory, "drama-record.md");
        var text = new StringBuilder()
            .AppendLine("# DramaBoard · FirstBoard 首场记录")
            .AppendLine()
            .Append("- 爱丽丝 Driver：").AppendLine(
                DriverDescription(options, BoardIds.Alice, options.AliceBackend))
            .Append("- 鲍勃 Driver：").AppendLine(
                DriverDescription(options, BoardIds.Bob, options.BobBackend))
            .Append("- Presentation：")
            .Append(options.PresentationMode.ToString().ToLowerInvariant())
            .Append("；Human=").Append(options.HumanActorId ?? "none")
            .Append("；interval=").Append(
                checked((long)options.PresentationInterval.TotalMilliseconds)
                    .ToString(CultureInfo.InvariantCulture))
            .AppendLine("ms")
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
                scenarioInstance.WorldSeed.ToString(CultureInfo.InvariantCulture))
            .Append("- 结束：").Append(status)
            .Append(" @ ").Append(
                currentModelTime.Ticks.ToString(CultureInfo.InvariantCulture))
            .AppendLine("ms")
            .Append("- 世界 transition：").AppendLine(
                finalVersion.TransitionCount.ToString(CultureInfo.InvariantCulture))
            .Append("- 本次起点 transition：").AppendLine(
                initialCursor.Version.TransitionCount.ToString(CultureInfo.InvariantCulture))
            .Append("- 本次完成 transition：").AppendLine(
                events.Count.ToString(CultureInfo.InvariantCulture))
            .Append("- 成功解析的 LLM turn：").AppendLine(
                traces.Count.ToString(CultureInfo.InvariantCulture))
            .Append("- turn 预算触发的收场等待：").AppendLine(
                budgetForcedCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .AppendLine("## 世界终局")
            .AppendLine()
            .Append("- 地窖入口：").AppendLine(
                world.CellarSealed ? "已禁止进入" : "仍开放")
            .Append("- 锁箱：").AppendLine(
                world.ChestOpened ? "已打开" : "仍上锁")
            .Append("- 黄铜钥匙：").AppendLine(ObjectLocation(world))
            .Append("- 公爵夫人的密信：").AppendLine(
                ObjectLocation(world, BoardIds.DuchessLetter))
            .Append("- 银币一：").AppendLine(
                ObjectLocation(world, BoardIds.SilverCoinOne))
            .Append("- 银币二：").AppendLine(
                ObjectLocation(world, BoardIds.SilverCoinTwo))
            .Append("- 爱丽丝：").AppendLine(
                ActorSummary(world, BoardIds.Alice))
            .Append("- 鲍勃：").AppendLine(
                ActorSummary(world, BoardIds.Bob))
            .AppendLine()
            .AppendLine("## 世界事件叙事 dump")
            .AppendLine();

        foreach (OccurrenceEvent<FirstBoardFact> batch in events)
        {
            foreach (FirstBoardFact fact in batch.Facts)
            {
                text.Append("- **").Append(FormatTime(batch.TargetInstant.ModelTime.Ticks))
                    .Append(" / #").Append(
                        batch.TargetInstant.CausalOrdinal.ToString(CultureInfo.InvariantCulture))
                    .Append("** ").Append(RenderEvent(fact))
                    .Append("  `").Append(FirstBoardScenario.FactName(fact)).AppendLine("`");
            }
        }

        text.AppendLine()
            .AppendLine("## 演员内心轨迹")
            .AppendLine();
        if (traces.Count == 0)
        {
            text.AppendLine("（本局没有 LLM 内心轨迹。）").AppendLine();
        }

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

    private static string DriverDescription(
        DemoOptions options,
        string actorId,
        DemoBackendOptions backend) =>
        actorId == options.HumanActorId
            ? "Human / Console"
            : $"LLM / {backend.Backend} / {backend.Model}";

    public static string FormatIntent(Intent intent) =>
        intent.ActionKind.Id switch
        {
            "action.travel" => $"选择出口 {intent.ExitId}",
            "action.travel-to" => $"委托旅行至 {DisplayPlace(intent.DestinationId)}",
            "action.continue-travel" => "在当前通道中继续前进",
            "action.reverse-travel" => "在当前通道中转身返回",
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
        ActorTravelGoalSetEvent value =>
            $"{DisplayActor(value.ActorId)}开始委托旅行，目标是" +
            $"{DisplayPlace(value.DestinationPlaceId.Value)}。",
        ActorTravelGoalResolvedEvent value => value.Resolution switch
        {
            TravelGoalResolution.Completed =>
                $"{DisplayActor(value.ActorId)}抵达{DisplayPlace(value.DestinationPlaceId.Value)}，" +
                "完成委托旅行。",
            TravelGoalResolution.Blocked =>
                $"{DisplayActor(value.ActorId)}在当前位置无法继续前往" +
                $"{DisplayPlace(value.DestinationPlaceId.Value)}，结束委托旅行。",
            _ => throw new InvalidOperationException(
                $"Unknown TravelTo resolution '{value.Resolution}'."),
        },
        PassageEncounterOpenedEvent value =>
            $"{DisplayActor(value.ContactKey.EntityA.Value)}与" +
            $"{DisplayActor(value.ContactKey.EntityB.Value)}在通道 " +
            $"{value.ContactKey.PassageId.Value} 中" +
            $"{DisplayContactKind(value.Kind)}。",
        PassageEncounterResolvedEvent value => value.Resolution switch
        {
            PassageEncounterResolution.Continued =>
                $"{DisplayActor(value.RespondingActorId)}决定在相遇后继续前进。",
            PassageEncounterResolution.Reversed =>
                $"{DisplayActor(value.RespondingActorId)}决定在相遇后转身返回。",
            PassageEncounterResolution.WorldChanged =>
                "相遇双方的客观移动已经变化，本次途中相遇随之结束。",
            _ => throw new InvalidOperationException(
                $"Unknown passage encounter resolution '{value.Resolution}'."),
        },
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
        TraversalReversedFact value =>
            $"{DisplayActor(value.EntityId.Value)}结束第" +
            $"{value.ExpectedMovementGeneration}段移动并在通道中反向行进。",
        PassageContactOccurredFact value =>
            $"通道 {value.ContactKey.PassageId.Value} 中，" +
            $"{DisplayActor(value.ContactKey.EntityA.Value)}与" +
            $"{DisplayActor(value.ContactKey.EntityB.Value)}" +
            $"{DisplayContactKind(value.Kind)}。",
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
            $"正沿 {traversing.PassageId.Value} 前往" +
            $"{DisplayPlace(traversing.TargetPlaceId.Value)}，" +
            $"ETA {FormatTime(traversing.ArrivalDue.Ticks)}",
        _ => "空间位置未知",
    };

    private static string DisplayContactKind(PassageContactKind kind) => kind switch
    {
        PassageContactKind.HeadOnMeeting => "迎面相遇",
        PassageContactKind.Overtake => "发生追及相遇",
        _ => throw new InvalidOperationException($"Unknown passage contact kind '{kind}'."),
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

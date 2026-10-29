using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>Groups visible facts around decisions, using only the game's frozen observation.</summary>
internal sealed class DecisionPlayLayout : IPlayLayout {
    public string Id => "decision";
    public string Name => "决策布局";

    public string Render(CycleObservation observation, CharacterInteractionState? interactionState = null) {
        if (!observation.IsExpedition) { return PlayView.Render(observation, interactionState); }
        bool ended = observation.Outcome != CycleExpeditionOutcome.Exploring;
        var text = new StringBuilder();
        text.AppendLine($"# 山林求生 · {observation.Actor.Name}（{observation.Actor.Id}） · {Time(observation.Time)}");
        text.AppendLine();
        text.Append(PlayView.RenderKnowledge(observation));
        if (ended) {
            RenderEnding(text, observation, interactionState);
            return text.ToString();
        }
        text.Append(ContextHistoryPlayView.RenderHistory(observation, interactionState));
        text.AppendLine("## 历史通知与最近天气");
        RenderEvents(text, observation, ended: false);
        text.AppendLine();
        text.AppendLine("## 当前状态与环境");
        text.AppendLine($"{(observation.IsDay ? "白天" : "夜晚")} · 下次{(observation.IsDay ? "天黑" : "天亮")} {Time(observation.NextLightChange)}。");
        text.AppendLine(observation.ArrivalDue is { } arrival
            ? $"**正在途中** · {Time(arrival)} 抵达（剩余 {Minutes(arrival.Ticks - observation.Time.Ticks)} 分钟）；移动不能取消。"
            : $"**{observation.PlaceName}**：{observation.PlaceDescription}");
        if (observation.Survival is { } survival) {
            text.AppendLine();
            text.AppendLine($"**{Exposure(survival)} · 温暖 {Number(survival.Warmth)}/100（{Trend(survival.WarmthPerHour)}/小时）**");
            text.AppendLine($"食物 {Number(survival.Food)}/100（{Trend(survival.FoodPerHour)}/小时） · 饮水 {Number(survival.Water)}/100（{Trend(survival.WaterPerHour)}/小时）。");
            text.AppendLine(survival.FatalAt is { } fatal
                ? $"- 不补给且保持当前庇护状态，预计最早耗尽：{Time(fatal)}（约剩 {Minutes(Math.Max(0, fatal.Ticks - observation.Time.Ticks))} 分钟）。"
                : "- 不补给且保持当前庇护状态，可表示的时间内没有预计耗尽时刻。");
            text.AppendLine("  估计计入昼夜，不预知抵达后的环境。" + (survival.Sheltered
                ? "留在庇护内免受寒风损失；离开后需重新评估。"
                : observation.Weather is not null ? "未计随机寒风，危险可能提前。" : ""));
        }
        PlayView.RenderWarmthAdvice(text, observation);
        if (observation.Weather is { } weather) {
            text.AppendLine(weather.NextWindow is { } next
                ? $"- 下次天气窗口：{Time(next)}；结果未知。"
                : "- 没有后续天气窗口。");
        }
        RenderActivity(text, observation, ended: false);
        text.AppendLine($"采药等级 {observation.Actor.SkillLevel}（基础速度的 {100m + 25m * observation.Actor.SkillLevel:0}%）。");
        text.AppendLine();
        RenderSupplies(text, observation, ended: false);
        text.Append(ContextHistoryPlayView.RenderCurrent(observation, interactionState));
        RenderResources(text, observation);
        text.AppendLine("## 眼前的行动");
        AppendCommand(text, observation, "continue", CycleCommand.Continue(), "确认消息与历史警告，保留活动及原定期限");
        if (observation.Actor.Activity is not null && observation.ArrivalDue is null) {
            AppendCommand(text, observation, "cancel", CycleCommand.Cancel(), "取消采集或等待，保留已获得物品，放弃未完成的一份");
        }
        foreach (CycleCommandCheck supply in observation.Commands.Where(entry => entry.Command.Kind == CycleCommandKind.Use)) {
            AppendCommand(text, observation, $"use {supply.Command.ItemId}", supply.Command,
                $"背包 {observation.Actor.Backpack.Count(supply.Command.ItemId!)} 份；{CycleSurvivalRules.UseDescription(supply.Command.ItemId!)!}");
        }
        if (observation.Commands.Any(entry => entry.Command.Kind == CycleCommandKind.Handoff)) {
            text.AppendLine($"`handoff <文字>` 覆盖草稿 · `handoff --clear` 清空；最多 {CharacterInteractionState.MaximumHandoffScalars} 个 Unicode 字符，不耗时、不确认警告。");
        }
        text.AppendLine("各行动旁显示当前可执行性与已知风险；未来随机天气无法预知，没有提示不等于安全。");
        if (observation.Passages.Count > 0) {
            text.AppendLine();
            text.AppendLine("### 去哪里");
            foreach (var group in observation.Passages.GroupBy(passage => observation.WarningDisplayed(CycleCommand.Go(passage.Id)))) {
                foreach (CyclePassageObservation passage in group) {
                    AppendCommand(text, observation, $"go {passage.Id}", CycleCommand.Go(passage.Id),
                        $"{passage.Description}；耗时 {Minutes(passage.DurationTicks)} 分钟；" +
                        (passage.KnownDestinationName is { } known ? $"曾到访目的地：{known}" : "目的地未探索，线索不保证安全"), includeWarning: false);
                }
                if (group.Key is { } warning) {
                    text.AppendLine($"  {string.Join(" / ", group.Select(passage => passage.Id))} 风险提示（不禁止）：{warning}");
                }
            }
            text.AppendLine("移动途中不能取消；食物和饮水可在请求输入时补充。");
        }
        if (observation.Resources.Count > 0) {
            text.AppendLine();
            text.AppendLine("### 就地采集");
            foreach (CycleResource resource in observation.Resources) {
                AppendCommand(text, observation, $"gather {resource.Id} 1", CycleCommand.Gather(resource.Id, 1), $"取得 1 份{resource.Name}；需 {Minutes(observation.HarvestDurations[resource.Id])} 分钟");
                AppendCommand(text, observation, $"gather {resource.Id} all", CycleCommand.GatherAll(resource.Id), $"固定目标 {resource.Stock} 份，不预留库存");
            }
            text.AppendLine("数量也可输入正整数；逐份结算，库存耗尽会提醒；结算时无货则结束，不会自动等再生。");
        } else {
            text.AppendLine(observation.ArrivalDue is null ? "当前位置没有可采资源。" : "途中没有可采资源或可选通路；需抵达后再安排活动。");
        }
        if (observation.ArrivalDue is null) {
            text.AppendLine();
            AppendCommand(text, observation, "wait 1", CycleCommand.Wait(1), "在原地停留 1 小时；完成或危险警告时请求输入");
            text.AppendLine("等待可选 1–24 整数小时；各时长当前条件与风险：");
        }
        text.Append(CycleUpdateRenderer.RenderCommandStates(CycleViewSnapshot.Create(observation).Values
            .Where(pair => pair.Key.Category == "command" &&
                (pair.Key.ObjectId.StartsWith("Wait|", StringComparison.Ordinal) ||
                 pair.Key.ObjectId.StartsWith("Handoff|", StringComparison.Ordinal) ||
                 pair.Key.ObjectId.StartsWith("Cancel|", StringComparison.Ordinal)))));
        text.AppendLine();
        return text.ToString();
    }

    public string RenderActionHelp(CycleObservation observation) => PlayView.RenderCommands(observation);

    private static void RenderEvents(StringBuilder text, CycleObservation observation, bool ended) {
        foreach (CycleNoticeObservation notice in observation.Notices) { text.AppendLine($"- {PlayView.RenderNotice(notice, ended)}"); }
        if (observation.Survival is { PendingWarnings.Count: > 0 } survival) {
            text.AppendLine(ended ? "- 结束时未确认的历史警告：" : "- **待确认的历史警告**（补给后仍保留，当前数值以当前状态区为准）：");
            foreach (string warning in survival.PendingWarnings) { text.AppendLine($"  - {warning}"); }
        }
        if (observation.Weather is { LastAt: { } at, LastKind: { } kind } weather) {
            string impact = kind == CycleWeatherKind.Calm ? "平静，无温暖损失" : weather.LastWasSheltered
                ? "寒风，当时有庇护，温暖损失 0 点" : $"寒风，当时暴露，温暖实际减少 {Number(weather.LastActualWarmthLoss)} 点";
            text.AppendLine($"- 最近实际天气：{Time(at)}，{impact}；已结算的历史影响，不代表下一次结果。");
        } else {
            text.AppendLine("- 尚无已发生的天气记录。");
        }
    }

    private static void RenderActivity(StringBuilder text, CycleObservation observation, bool ended) {
        if (observation.ArrivalDue is not null) { return; }
        if (observation.Actor.Activity is not { } activity) {
            text.AppendLine(ended ? "结束时没有未完成活动。" : "当前空闲，可选择下方行动。");
        } else if (activity.Kind == CycleActivityKind.Wait) {
            text.AppendLine(ended ? $"未完成的等待已冻结；原截止 {Time(activity.Due)}。" : $"正在等待 · {Time(activity.Due)} 完成。");
        } else {
            string resource = observation.Resources.FirstOrDefault(resource => resource.Id == activity.ResourceId)?.Name ?? activity.ResourceId!;
            text.AppendLine($"{(ended ? "未完成的采集已冻结" : "正在采集")}：{resource} · 已取得 {activity.CompletedCount}/{activity.TargetCount} 份（{(activity.SelectedAll ? "接受 all 时固定的目标" : "固定目标")}）。");
            text.AppendLine(ended ? $"原周期截止 {Time(activity.Due)}，不再结算。"
                : $"当前周期 {Time(activity.StartedAt)} → {Time(activity.Due)}，剩余 {Minutes(activity.Due.Ticks - observation.Time.Ticks)} 分钟；查看、补给与确认消息均不重置期限。");
        }
    }

    private static void RenderSupplies(StringBuilder text, CycleObservation observation, bool ended) {
        var backpack = observation.Actor.Backpack;
        text.AppendLine(ended ? "## 结束时的背包" : "## 当前背包");
        text.AppendLine(backpack.UnitCapacity is { } capacity ? $"容量：{backpack.TotalCount}/{capacity} 件。" : $"共 {backpack.TotalCount} 件。");
        if (backpack.Items.Count == 0) { text.AppendLine("- 背包空。"); }
        foreach (var item in backpack.Items.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            text.AppendLine($"- {ItemName(observation, item.Key)} `{item.Key}` ×{item.Value}");
        }
        text.AppendLine();
    }

    private static void RenderResources(StringBuilder text, CycleObservation observation) {
        text.AppendLine("## 当前资源");
        foreach (CycleResource resource in observation.Resources) {
            string regeneration = resource.Stock == resource.Capacity ? "已满，不积累再生进度" :
                $"{(resource.ActiveSince is null ? "暂停" : "进行中")}，进度 {Minutes(resource.ProgressAt(observation.Time))}/{Minutes(resource.RegenerationTicks)} 分钟";
            text.AppendLine($"- **{resource.Name}** `{resource.Id}`：余量 {resource.Stock}/{resource.Capacity}；每份产出 {ItemName(observation, resource.ItemId)} ×1，需 {Minutes(observation.HarvestDurations[resource.Id])} 分钟；要求等级 {resource.RequiredLevel}。");
            text.AppendLine($"  再生：{(resource.GrowsInDaylight ? "白天" : "夜晚")}每 {Minutes(resource.RegenerationTicks)} 分钟补 1；{regeneration}。");
        }
        if (observation.Resources.Count == 0) { text.AppendLine("当前没有可见的可采资源。"); }
        text.AppendLine();
    }

    private static void RenderEnding(StringBuilder text, CycleObservation observation, CharacterInteractionState? interactionState) {
        text.AppendLine(observation.Outcome == CycleExpeditionOutcome.Escaped ? "**获救。**" : "**旅程结束。只是这一次未能走出山林。**");
        text.AppendLine(observation.EndingReason);
        text.AppendLine("以下为结束时的冻结记录；世界不再推进。未完成的活动不会继续结算。");
        text.Append(ContextHistoryPlayView.RenderHistory(observation, interactionState));
        text.AppendLine("## 结束前的历史通知与最近天气");
        RenderEvents(text, observation, ended: true);
        text.AppendLine();
        text.AppendLine("## 结束时的状态");
        text.AppendLine($"结束时为{(observation.IsDay ? "白天" : "夜晚")}；" + (observation.ArrivalDue is { } arrival
            ? $"仍在途中，原计划 {Time(arrival)} 抵达，行程未完成。" : $"位置：{observation.PlaceName}。{observation.PlaceDescription}"));
        if (observation.Survival is { } survival) {
            text.AppendLine($"{Exposure(survival)} · 温暖 {Number(survival.Warmth)}/100 · 食物 {Number(survival.Food)}/100 · 饮水 {Number(survival.Water)}/100。");
        }
        RenderActivity(text, observation, ended: true);
        text.AppendLine();
        text.Append(ContextHistoryPlayView.RenderCurrent(observation, interactionState));
        text.AppendLine();
        RenderSupplies(text, observation, ended: true);
        text.AppendLine();
        text.AppendLine("## 结束时可见资源");
        if (observation.Resources.Count == 0) { text.AppendLine("结束时没有可见的可采资源。"); }
        foreach (CycleResource resource in observation.Resources) {
            text.AppendLine($"- {resource.Name} `{resource.Id}`：余量 {resource.Stock}/{resource.Capacity}；产出 {ItemName(observation, resource.ItemId)}。");
        }
    }

    private static void AppendCommand(StringBuilder text, CycleObservation observation, string syntax, CycleCommand command, string description,
        bool includeWarning = true) {
        string? reason = observation.CheckDisplayed(command);
        text.AppendLine($"- `{syntax}`：{description.TrimEnd('。', '；')}；{(reason is null ? "可执行。" : $"受阻：{reason}")}");
        if (includeWarning && observation.WarningDisplayed(command) is { } warning) { text.AppendLine($"  风险提示（不禁止）：{warning}"); }
    }

    private static string Exposure(CycleSurvivalObservation survival) => survival.Sheltered ? "有庇护" : "暴露在野外（无庇护）";
    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Trend(int value) => value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);
    private static string Minutes(long ticks) => Number((decimal)ticks / 60_000);
    private static string ItemName(CycleObservation observation, string itemId) => observation.ItemNames.GetValueOrDefault(itemId, itemId);
    private static string Time(ModelTime time) {
        string value = $"第 {time.Ticks / CycleTime.Day} 天 {time.Ticks % CycleTime.Day / CycleTime.Hour:00}:{time.Ticks % CycleTime.Hour / 60_000:00}";
        long remainder = time.Ticks % 60_000;
        return remainder == 0 ? value : $"{value}:{remainder / 1_000:00}.{remainder % 1_000:000}";
    }
}

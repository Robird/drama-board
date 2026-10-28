using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>Both layouts read the same sealed history, never reconstruct it from today's values.</summary>
internal static class ContinuityPlayView {
    public static string Render(CycleObservation observation) {
        if (observation.Continuity is not { } continuity) { return ""; }
        var text = new StringBuilder();
        string resultLabel = observation.Outcome == CycleExpeditionOutcome.Exploring ? "上次推进的结果" : "终局结果";
        text.AppendLine($"## 决策回合 {continuity.ContextSequence} · {resultLabel}");
        if (continuity.Incoming is not { } incoming) {
            text.AppendLine(continuity.Pending is null
                ? "这是首次决策，尚无上次推进的结果。"
                : "本回合已放行推进；尚无已封存的上次行动结果。");
        } else {
            text.AppendLine($"历史区间：{Time(incoming.Action.AcceptedAt)} → {Time(incoming.DeliveredAt)}；行动：{Action(incoming.Action)}。");
            text.AppendLine(incoming.ActivityState switch {
                CycleOutcomeActivityState.Ongoing => "**本段推进已返回，原活动仍在进行**；继续不会新建活动或重置期限。",
                CycleOutcomeActivityState.Completed => "原活动已结束。",
                _ => "旅程已终止；以下是封存结果，未完成的活动不再推进。"
            });
            text.AppendLine($"返回原因：{Reasons(incoming.Summary)}。");
            text.AppendLine($"位置：{Position(incoming.Before)} → {Position(incoming.After)}。");
            if (incoming.Before.ActivityId is not null) {
                string progress = incoming.Before.TargetCount > 0
                    ? $"已取得 {incoming.Before.CompletedCount}/{incoming.Before.TargetCount} 份；" : "";
                text.AppendLine($"- 行动前活动：{progress}" +
                    (incoming.Before.ActivityDue is { } beforeDue ? $"原周期截止 {Time(beforeDue)}。" : "没有周期期限。"));
            }
            Body(text, "食物", incoming.Before.FoodUnits, incoming.After.FoodUnits);
            Body(text, "饮水", incoming.Before.WaterUnits, incoming.After.WaterUnits);
            Body(text, "温暖", incoming.Before.WarmthUnits, incoming.After.WarmthUnits);
            text.AppendLine("上述是本段净变化与交付时历史值；当前身体以当前状态区为准，本轮补给不会改写它们。");
            var itemIds = incoming.Before.Backpack.Keys.Concat(incoming.After.Backpack.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (string itemId in itemIds) {
                int before = incoming.Before.Backpack.GetValueOrDefault(itemId), after = incoming.After.Backpack.GetValueOrDefault(itemId);
                text.AppendLine($"- 背包 {observation.ItemNames.GetValueOrDefault(itemId, itemId)} `{itemId}`：{before} → {after}（净变化 {Signed(after - before)}）。");
            }
            if (incoming.After.ActivityId is not null) {
                string progress = incoming.After.TargetCount > 0
                    ? $"交付时采集进度：{incoming.After.CompletedCount}/{incoming.After.TargetCount}"
                    : "本段活动记录";
                text.AppendLine($"- {progress}" + (incoming.After.ActivityDue is { } due ? $"；记录的周期截止 {Time(due)}。" : "。"));
            }
            CycleOutcomeSummary summary = incoming.Summary;
            string lowerBound = summary.IsSaturated ? "至少 " : "";
            if (summary.GatherItemId is { } item) {
                text.AppendLine($"- 本段采集所得：{observation.ItemNames.GetValueOrDefault(item, item)} ×{lowerBound}{summary.GatheredCount}。");
            }
            if (summary.GatherStopReason is { } stop) {
                text.AppendLine($"- 采集结束原因：{StopReason(stop)}。");
            }
            text.AppendLine($"- 已感知天气：平静 {lowerBound}{summary.CalmWeatherCount} 次、寒风 {lowerBound}{summary.ColdWeatherCount} 次；天气实际温暖损失 {lowerBound}{Number(summary.WeatherWarmthLossUnits)} 点；昼夜变化 {lowerBound}{summary.LightChangeCount} 次。");
            if (summary.IsSaturated) { text.AppendLine("累计值达到表示上限；计数仅表示下界。"); }
            text.AppendLine("上回合的交接文字（角色主观记录，并非游戏确认的事实）：");
            Quote(text, incoming.Handoff ?? "（无）");
        }
        if (continuity.Pending is { } pending) {
            text.AppendLine($"已接受的行动：{Action(pending.Action)}；正在推进，结果将在下次决策时交付。");
        }
        text.AppendLine();
        text.AppendLine("本轮交接草稿（下一次放行模拟时发送，旧交接不会自动续传）：");
        Quote(text, continuity.DraftHandoff ?? "（空）");
        if (observation.Outcome == CycleExpeditionOutcome.Exploring) {
            text.AppendLine("`handoff <文字>` 覆盖草稿 · `handoff --clear` 清空；不耗时、不确认警告。`:help` 查看限制。");
        }
        text.AppendLine();
        return text.ToString();
    }

    private static void Quote(StringBuilder text, string value) {
        foreach (string line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')) {
            text.AppendLine("> " + line);
        }
    }

    private static string Action(CycleAcceptedAction action) => action.Kind switch {
        CycleAcceptedActionKind.Wait => $"wait {action.Hours}（等待 {action.Hours} 小时）",
        CycleAcceptedActionKind.Gather => $"gather {action.ResourceId} {(action.SelectedAll ? "all" : action.TargetCount.ToString(CultureInfo.InvariantCulture))}（固定目标 {action.TargetCount} 份）",
        CycleAcceptedActionKind.Go => $"go {action.PassageId}（沿通路出发）",
        _ => "continue（确认后恢复原活动）"
    };

    private static string Position(CycleContinuitySnapshot snapshot) => snapshot.PlaceName ?? snapshot.PlaceId ??
        (snapshot.PassageId is { } passage ? $"途中 `{passage}`" +
            (snapshot.KnownTargetPlaceName is { } target ? $"，已知目的地 {target}" : "，目的地尚未探索") : "位置未记录");

    private static string Reasons(CycleOutcomeSummary summary) {
        var reasons = new List<string>();
        foreach (var (reason, label) in new[] {
            (CycleWakeReason.ActivityCompleted, "活动完成"), (CycleWakeReason.Arrival, "抵达"),
            (CycleWakeReason.ActivityStopped, "活动停止"), (CycleWakeReason.SurvivalWarning, "身体警告"),
            (CycleWakeReason.CollectorJoined, "有人加入采集"), (CycleWakeReason.ResourceDepleted, "资源耗尽"),
            (CycleWakeReason.Terminal, "旅程终局") }) {
            if (summary.Reasons.HasFlag(reason)) { reasons.Add(label); }
        }
        if (summary.WarningMask != 0) {
            string[] names = ["食物", "饮水", "温暖"];
            reasons.Add("警告指标 " + string.Join("、", Enumerable.Range(0, 3).Where(i => (summary.WarningMask & (1 << i)) != 0).Select(i => names[i])));
        }
        return reasons.Count == 0 ? "已到可决策边界" : string.Join("、", reasons);
    }

    private static string StopReason(CycleGatherStopReason reason) => reason switch {
        CycleGatherStopReason.TargetReached => "已达目标", CycleGatherStopReason.ResourceEmpty => "资源已空",
        CycleGatherStopReason.ItemLimit => "物品数量上限", CycleGatherStopReason.CapacityLimit => "背包容量上限",
        _ => "时间上限，无法安排下一周期"
    };

    private static void Body(StringBuilder text, string name, long? before, long? after) {
        if (before is { } first && after is { } last) {
            text.AppendLine($"- {name}：{Number(first)} → {Number(last)}（净变化 {Signed((decimal)(last - first) / CycleSurvival.Scale)}）。");
        }
    }
    private static string Number(long units) => ((decimal)units / CycleSurvival.Scale).ToString("0.##", CultureInfo.InvariantCulture);
    private static string Signed(decimal value) => value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);
    private static string Time(ModelTime time) {
        string minute = $"第 {time.Ticks / CycleTime.Day} 天 {time.Ticks % CycleTime.Day / CycleTime.Hour:00}:{time.Ticks % CycleTime.Hour / 60_000:00}";
        long remainder = time.Ticks % 60_000;
        return remainder == 0 ? minute : $"{minute}:{remainder / 1000:00}.{remainder % 1000:000}";
    }
}

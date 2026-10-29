using System.Globalization;
using System.Text;
using DramaBoard.ResourceCycle;

/// <summary>Shared causal formatting. Layouts receive the same events and selected semantic values.</summary>
internal static class CycleUpdateRenderer {
    public static string Render(CycleViewUpdate update) {
        var text = new StringBuilder(RenderEvents(update));
        if (update.CurrentValues.Count > 0) {
            text.AppendLine("当前：");
            foreach (var pair in update.CurrentValues.Where(pair => pair.Key.Category != "command")) {
                if (update.Snapshot.RateExplanations.ContainsKey(pair.Key)) { continue; }
                var rateKey = new CycleStateKey(pair.Key.Category, pair.Key.ObjectId, pair.Key.Field + "-rate");
                string value = update.Snapshot.RateExplanations.TryGetValue(rateKey, out var attribute)
                    ? attribute.Description : Format(pair.Value, pair.Key);
                text.AppendLine($"- {update.Label(pair.Key)}：{value}");
            }
            text.Append(RenderCommandStates(update.CurrentValues));
        }
        return text.ToString();
    }

    public static string RenderCommandStates(IEnumerable<KeyValuePair<CycleStateKey, CycleVisibleValue>> values) {
        var text = new StringBuilder();
        foreach (var group in values.Where(pair => pair.Key.Category == "command")
                .GroupBy(pair => (pair.Key.Field, pair.Value))) {
                string commands = string.Join("、", CommandLabels(group.Select(pair => pair.Key)));
                string field = group.Key.Field == "warning" ? "风险" : "条件";
                text.AppendLine($"- {commands} · {field}：{Format(group.Key.Value, group.First().Key)}");
        }
        return text.ToString();
    }

    public static string RenderEvents(CycleViewUpdate update) {
        var text = new StringBuilder();
        foreach (var e in update.Events) {
            text.AppendLine(EventDescription(e));
            foreach (var effect in e.Effects) {
                string label = update.Label(effect.Key);
                string value = effect.Before.Kind == CycleVisibleValueKind.Number && effect.After.Kind == CycleVisibleValueKind.Number &&
                    effect.Before.Unit == effect.After.Unit && effect.Before.Unit != "ticks"
                    ? Signed(effect.After.Number - effect.Before.Number) + (effect.After.Unit ?? "")
                    : $"{Format(effect.Before, effect.Key)} → {Format(effect.After, effect.Key)}";
                text.AppendLine($"  {label} {value}");
            }
        }
        return text.ToString();
    }

    public static string Format(CycleVisibleValue value, CycleStateKey? key = null) => value.Kind switch {
        CycleVisibleValueKind.KnownAbsent => key is { Category: "command", Field: "rejection" } ? "条件满足" : "无",
        CycleVisibleValueKind.Unobserved => "不再可见",
        CycleVisibleValueKind.Boolean => value.Boolean ? "是" : "否",
        CycleVisibleValueKind.Text => TextValue(value.Text ?? "", key),
        CycleVisibleValueKind.Number when value.Unit == "ticks" => Time(value.Units),
        CycleVisibleValueKind.Number when value.Unit == "duration" => (value.Number / 60_000m).ToString("0.##", CultureInfo.InvariantCulture) + "分钟",
        CycleVisibleValueKind.Number => value.Number.ToString("0.##", CultureInfo.InvariantCulture) + value.Unit,
        _ => throw new InvalidOperationException("Unknown visible value kind.")
    };

    private static string TextValue(string text, CycleStateKey? key) => key switch {
        { Category: "continuity", Field: "draft" } => CharacterContextRenderer.DataLiteral(text),
        { Category: "activity", Field: "kind" } or { Category: "continuity", Field: "pending" } => text switch {
            "Wait" => "等待", "Gather" => "采集", "Go" => "移动", "Continue" => "确认后继续", _ => text
        },
        { Category: "world", Field: "outcome" } => text switch {
            "Exploring" => "探索中", "Escaped" => "获救", "Dead" => "旅程结束", _ => text
        },
        { Category: "weather", Field: "last-kind" } => text switch {
            "Calm" => "平静", "ColdWind" => "寒风", _ => text
        },
        { Category: "notice-detail", Field: "kind" } => text switch {
            "ResourceDepleted" => "资源耗尽", "CollectorJoined" => "有人加入采集", _ => text
        },
        _ => text
    };

    internal static string EventDescription(CycleObservedEvent e) {
        string subject = e.SubjectName ?? e.SubjectId ?? "";
        string title = e.Kind.ToString() switch {
            "BodyChanged" => $"{Time(e.From.Ticks)} → {Time(e.At.Ticks)} 的身体变化。", "ItemUsed" => $"使用了{subject}。",
            "WaitStarted" => "开始等待。", "GatherStarted" => $"开始采集{subject}。", "MoveStarted" => $"开始移动{subject}。",
            "GatherYielded" => $"采集获得{subject}。", "GatherFinished" => $"采集停止：{subject}。", "WaitCompleted" => "等待结束。",
            "AttentionAcknowledged" => "已确认消息和历史警告。", "ActivityCancelled" => "已取消当前活动。",
            "LightChanged" => "昼夜发生变化。", "WeatherResolved" => "天气结算。", "MoveArrived" => $"已抵达{subject}。",
            "SurvivalWarning" => "身体触及警戒线。", "SurvivalFailed" => "生存状态耗尽。", "ExpeditionEnded" => "旅程结束。",
            "HandoffEdited" => "已更新下回合留言。", "ResourceRegenerated" => $"{subject}再生。", "ResourceDestroyed" => $"{subject}资源被清空。",
            _ => $"{e.Kind}：{subject}。"
        };
        return e.Detail is { Length: > 0 } detail ? title + " " + (detail switch {
            "ColdWind" => "寒风袭来，当前暴露。", "ColdWindSheltered" => "寒风袭来，庇护内免损。", "Calm" => "天气平静。",
            "TargetReached" => "已达到采集目标。", "ResourceEmpty" => "资源已耗尽。", "ItemLimit" => "物品数量达到上限。",
            "CapacityLimit" => "背包容量已满。", "TimeLimit" => "已达到可表示的时间上限。", _ => detail
        }) : title;
    }
    private static string Signed(decimal value) => (value > 0 ? "+" : "") + value.ToString("0.##", CultureInfo.InvariantCulture);
    // Compact only the display. Every command retains its own semantic key and current value.
    private static IEnumerable<string> CommandLabels(IEnumerable<CycleStateKey> keys) {
        var commands = keys.Select(key => key.ObjectId.Split('|')).ToArray();
        var hours = commands.Where(parts => parts[0] == nameof(CycleCommandKind.Wait))
            .Select(parts => int.Parse(parts[1], CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
        for (int i = 0; i < hours.Length; i++) {
            int first = hours[i], last = first;
            while (i + 1 < hours.Length && hours[i + 1] == last + 1) { last = hours[++i]; }
            yield return first == last ? $"wait {first}" : $"wait {first}–{last}";
        }
        foreach (var parts in commands.Where(parts => parts[0] != nameof(CycleCommandKind.Wait))) {
            yield return parts[0] switch {
                nameof(CycleCommandKind.Gather) => $"gather {parts[2]} {(parts[4] == "all" ? "all" : parts[3])}",
                nameof(CycleCommandKind.Go) => $"go {parts[5]}", nameof(CycleCommandKind.Use) => $"use {parts[6]}",
                _ => parts[0].ToLowerInvariant()
            };
        }
    }
    private static string Time(long ticks) {
        string text = $"第 {ticks / CycleTime.Day} 天 {ticks % CycleTime.Day / CycleTime.Hour:00}:{ticks % CycleTime.Hour / 60_000:00}";
        return ticks % 60_000 == 0 ? text : text + $":{ticks % 60_000 / 1_000:00}.{ticks % 1_000:000}";
    }
}

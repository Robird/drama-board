using System.Globalization;

namespace DramaBoard.ResourceCycle;

/// <summary>The operation audit answer is a projection of the same persisted material in normal and repaired results.</summary>
public static class CycleOperationResults {
    public static string Describe(CycleFact fact) {
        ArgumentNullException.ThrowIfNull(fact);
        CycleObservedEvent[] events = fact.ObservedEvents.Where(e => e.ObserverId == fact.ActorId).ToArray();
        if (events.Length == 0) {
            if (fact.ChangedCharacters.Any(c => c.Survival is not null)) {
                return fact.Kind switch {
                    CycleEventKind.HandoffEdited => "交接草稿未发生变化。",
                    CycleEventKind.AttentionAcknowledged => "没有新的待确认信息，当前活动保持不变。",
                    _ => throw new InvalidOperationException("A survival operation is missing its recorded result material.")
                };
            }
            return fact.Result ?? fact.Kind.ToString();
        }
        return string.Join(Environment.NewLine, events.Select(e => {
            string heading = e.Kind switch {
                CycleObservedEventKind.ItemUsed => $"使用了 1 份{e.SubjectName}。",
                CycleObservedEventKind.HandoffEdited => "已更新交接草稿。",
                CycleObservedEventKind.AttentionAcknowledged => "已确认提醒，当前活动保持不变。",
                CycleObservedEventKind.ActivityCancelled => "已取消当前活动。",
                CycleObservedEventKind.WaitStarted => "开始等待。",
                CycleObservedEventKind.GatherStarted => $"开始采集{e.SubjectName}。",
                CycleObservedEventKind.MoveStarted => "开始移动。",
                _ => e.Kind.ToString()
            };
            return string.Join(Environment.NewLine, new[] { heading }.Concat(e.Effects.Select(effect =>
                $"  {Label(effect.Key)}：{DescribeEffect(effect)}")));
        }));
    }
    private static string DescribeEffect(CycleObservedEffect effect) => effect.Before.Kind == CycleVisibleValueKind.Number && effect.After.Kind == CycleVisibleValueKind.Number
        ? (effect.After.Number - effect.Before.Number).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " " + effect.After.Unit
        : $"{Value(effect.Before)} → {Value(effect.After)}";
    private static string Value(CycleVisibleValue value) => value.Kind switch {
        CycleVisibleValueKind.Number => value.Number.ToString("0.##", CultureInfo.InvariantCulture) + " " + value.Unit,
        CycleVisibleValueKind.Boolean => value.Boolean ? "是" : "否",
        CycleVisibleValueKind.Text => value.Text!,
        CycleVisibleValueKind.KnownAbsent => "无",
        _ => "不可见"
    };
    private static string Label(CycleStateKey key) => (key.Category, key.Field) switch {
        ("body", "food") => "饱食", ("body", "water") => "水分", ("body", "warmth") => "温暖",
        ("inventory", "count") => key.ObjectId,
        _ => $"{key.Category}/{key.Field}"
    };
}

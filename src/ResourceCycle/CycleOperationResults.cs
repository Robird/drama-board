using System.Globalization;

namespace DramaBoard.ResourceCycle;

/// <summary>The operation audit answer is a projection of the same persisted material in normal and repaired results.</summary>
public static class CycleOperationResults {
    public static string Describe(CycleFact fact) {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.GoalOperation is { } goal) { return goal.Message; }
        if (fact.OperationDisposition == CycleOperationDisposition.Rejected) {
            return !string.IsNullOrWhiteSpace(fact.Result) ? fact.Result
                : throw new InvalidOperationException("A rejected operation must retain its visible reason.");
        }
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
                CycleObservedEventKind.FollowStarted => "已开始跟随；由领队决定移动，结束跟随后可独立行动。",
                CycleObservedEventKind.FollowEnded => "已结束跟随。",
                CycleObservedEventKind.FollowersDismissed => "已解散全部跟随者。",
                CycleObservedEventKind.SpeechShouted => "已大喊。",
                CycleObservedEventKind.SpeechSent => $"已向 {e.Communication!.RecipientName}（{e.Communication.RecipientId}）发送署名话语。",
                CycleObservedEventKind.TradeOffered => $"已提交报价 {e.TradeChanges.Single().Id}，资产不预留；接受时重新核对双方支付与容量。",
                CycleObservedEventKind.TradeAccepted => $"报价 {e.TradeChanges.Single().Id} 已成交，双方资产已一次结算。",
                CycleObservedEventKind.TradeRejected => $"已拒绝报价 {e.TradeChanges.Single().Id}，没有转移资产。",
                CycleObservedEventKind.TradeWithdrawn => $"已撤回报价 {e.TradeChanges.Single().Id}，没有转移资产。",
                CycleObservedEventKind.TradesExpired => "报价已到期，没有转移资产。",
                CycleObservedEventKind.TradeParticipantFailed => "报价参与者无法继续，相关报价已关闭，没有转移资产。",
                CycleObservedEventKind.MarketBought => $"购入了{e.SubjectName}。",
                CycleObservedEventKind.MarketSold => $"出售了{e.SubjectName}。",
                CycleObservedEventKind.ProductionStarted => $"开始生产 {e.Production!.RecipeId}，共 {e.Production.Batches} 批。",
                CycleObservedEventKind.ProductionCompleted => $"完成生产 {e.Production!.RecipeId}，共 {e.Production.Batches} 批。",
                CycleObservedEventKind.MoneyGiven => $"已将 {e.Communication!.MoneyAmount} 钱赠与 {e.Communication.RecipientName}（{e.Communication.RecipientId}）。",
                CycleObservedEventKind.ItemGiven => $"已将 {e.Communication!.ItemId} ×{e.Communication.ItemCount} 赠与 {e.Communication.RecipientName}（{e.Communication.RecipientId}）。",
                _ => e.Kind.ToString()
            };
            return string.Join(Environment.NewLine, new[] { heading }.Concat(e.Effects.Select(effect =>
                $"  {Label(effect.Key)}：{DescribeEffect(effect)}")));
        }));
    }
    private static string DescribeEffect(CycleObservedEffect effect) =>
        !(effect.Key.Category == "communication" && effect.Key.Field == "remaining") &&
        effect.Before.Kind == CycleVisibleValueKind.Number && effect.After.Kind == CycleVisibleValueKind.Number
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
        ("money", "balance") => "现金",
        ("inventory", "count") => key.ObjectId,
        ("communication", "remaining") => "本时刻剩余交流次数",
        ("communication", "pending") => "待响应通信",
        ("following", "leader") => "跟随领队",
        ("following", var field) when field.StartsWith("follower:", StringComparison.Ordinal) => $"队员 {field[9..]}",
        _ => $"{key.Category}/{key.Field}"
    };
}

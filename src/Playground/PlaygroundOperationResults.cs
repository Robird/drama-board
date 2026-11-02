using System.Globalization;

namespace DramaBoard.Playground;

/// <summary>The operation audit answer is a projection of the same persisted material in normal and repaired results.</summary>
public static class PlaygroundOperationResults {
    public static string Describe(PlaygroundFact fact) {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.GoalOperation is { } goal) { return goal.Message; }
        if (fact.OperationDisposition == PlaygroundOperationDisposition.Rejected) {
            return !string.IsNullOrWhiteSpace(fact.Result) ? fact.Result
                : throw new InvalidOperationException("A rejected operation must retain its visible reason.");
        }
        PlaygroundObservedEvent[] events = fact.ObservedEvents.Where(e => e.ObserverId == fact.ActorId).ToArray();
        if (events.Length == 0) {
            if (fact.ChangedCharacters.Any(c => c.Survival is not null)) {
                return fact.Kind switch {
                    PlaygroundEventKind.HandoffEdited => "交接草稿未发生变化。",
                    PlaygroundEventKind.AttentionAcknowledged => "没有新的待确认信息，当前活动保持不变。",
                    _ => throw new InvalidOperationException("A survival operation is missing its recorded result material.")
                };
            }
            return fact.Result ?? fact.Kind.ToString();
        }
        return string.Join(Environment.NewLine, events.Select(e => {
            string heading = e.Kind switch {
                PlaygroundObservedEventKind.ItemUsed => $"使用了 1 份{e.SubjectName}。",
                PlaygroundObservedEventKind.HandoffEdited => "已更新交接草稿。",
                PlaygroundObservedEventKind.AttentionAcknowledged => "已确认提醒，当前活动保持不变。",
                PlaygroundObservedEventKind.ActivityCancelled => "已取消当前活动。",
                PlaygroundObservedEventKind.WaitStarted => "开始等待。",
                PlaygroundObservedEventKind.GatherStarted => $"开始采集{e.SubjectName}。",
                PlaygroundObservedEventKind.MoveStarted => "开始移动。",
                PlaygroundObservedEventKind.FollowStarted => "已开始跟随；由领队决定移动，结束跟随后可独立行动。",
                PlaygroundObservedEventKind.FollowEnded => "已结束跟随。",
                PlaygroundObservedEventKind.FollowersDismissed => "已解散全部跟随者。",
                PlaygroundObservedEventKind.SpeechShouted => "已大喊。",
                PlaygroundObservedEventKind.SpeechSent => $"已向 {e.Communication!.RecipientName}（{e.Communication.RecipientId}）发送署名话语。",
                PlaygroundObservedEventKind.TradeOffered => $"已提交报价 {e.TradeChanges.Single().Id}，资产不预留；接受时重新核对双方支付与容量。",
                PlaygroundObservedEventKind.TradeAccepted => $"报价 {e.TradeChanges.Single().Id} 已成交，双方资产已一次结算。",
                PlaygroundObservedEventKind.TradeRejected => $"已拒绝报价 {e.TradeChanges.Single().Id}，没有转移资产。",
                PlaygroundObservedEventKind.TradeWithdrawn => $"已撤回报价 {e.TradeChanges.Single().Id}，没有转移资产。",
                PlaygroundObservedEventKind.TradesExpired => "报价已到期，没有转移资产。",
                PlaygroundObservedEventKind.TradeParticipantFailed => "报价参与者无法继续，相关报价已关闭，没有转移资产。",
                PlaygroundObservedEventKind.MarketBought => $"购入了{e.SubjectName}。",
                PlaygroundObservedEventKind.MarketSold => $"出售了{e.SubjectName}。",
                PlaygroundObservedEventKind.ProductionStarted => $"开始生产 {e.Production!.RecipeId}，共 {e.Production.Batches} 批。",
                PlaygroundObservedEventKind.ProductionCompleted => $"完成生产 {e.Production!.RecipeId}，共 {e.Production.Batches} 批。",
                PlaygroundObservedEventKind.MoneyGiven => $"已将 {e.Communication!.MoneyAmount} 钱赠与 {e.Communication.RecipientName}（{e.Communication.RecipientId}）。",
                PlaygroundObservedEventKind.ItemGiven => $"已将 {e.Communication!.ItemId} ×{e.Communication.ItemCount} 赠与 {e.Communication.RecipientName}（{e.Communication.RecipientId}）。",
                _ => e.Kind.ToString()
            };
            return string.Join(Environment.NewLine, new[] { heading }.Concat(e.Effects.Select(effect =>
                $"  {Label(effect.Key)}：{DescribeEffect(effect)}")));
        }));
    }
    private static string DescribeEffect(PlaygroundObservedEffect effect) =>
        !(effect.Key.Category == "communication" && effect.Key.Field == "remaining") &&
        effect.Before.Kind == PlaygroundVisibleValueKind.Number && effect.After.Kind == PlaygroundVisibleValueKind.Number
        ? (effect.After.Number - effect.Before.Number).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " " + effect.After.Unit
        : $"{Value(effect.Before)} → {Value(effect.After)}";
    private static string Value(PlaygroundVisibleValue value) => value.Kind switch {
        PlaygroundVisibleValueKind.Number => value.Number.ToString("0.##", CultureInfo.InvariantCulture) + " " + value.Unit,
        PlaygroundVisibleValueKind.Boolean => value.Boolean ? "是" : "否",
        PlaygroundVisibleValueKind.Text => value.Text!,
        PlaygroundVisibleValueKind.KnownAbsent => "无",
        _ => "不可见"
    };
    private static string Label(PlaygroundStateKey key) => (key.Category, key.Field) switch {
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

using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>Personal offers rendered only from frozen observation and recorded changes.</summary>
internal static class TradeView {
    public static void RenderStatus(StringBuilder text, CycleObservation observation) {
        if (observation.Trades.Count == 0) { return; }
        text.AppendLine("## 你的居民报价");
        text.AppendLine("条款按发起方的给予 / 索取记录；报价不预留钱或物品，接受时才重新校验双方支付与背包容量并一起结算。可尝试接受不保证成交。");
        foreach (CycleTradeOffer offer in observation.Trades) {
            text.AppendLine($"- `{offer.Id}` · {Participant(observation, offer.ProposerId)} → {Participant(observation, offer.RecipientId)} · {Status(offer.Status)}");
            text.AppendLine($"  发起方给予：{Asset(offer.Give, observation)}；发起方索取：{Asset(offer.Want, observation)}。");
            bool proposer = offer.ProposerId == observation.Actor.Id;
            text.AppendLine($"  对你而言：支付 {Asset(proposer ? offer.Give : offer.Want, observation)}，收到 {Asset(proposer ? offer.Want : offer.Give, observation)}（仅已成交状态表示完成转移）。");
            text.AppendLine($"  发起于 {Time(offer.CreatedAt)}；到期 {Time(offer.ExpiresAt)}" +
                (offer.ClosedAt is { } closed ? $"；结束于 {Time(closed)}。" : "；到期时不可接受。"));
        }
        text.AppendLine();
    }

    public static void RenderCommands(StringBuilder text, CycleObservation observation) {
        if (!observation.IsExpedition || observation.Outcome != CycleExpeditionOutcome.Exploring) { return; }
        text.AppendLine("### 居民报价交易");
        text.AppendLine("`offer <actor-id> <asset> <amount> for <asset> <amount> [hours]`：向同地居民提出用前一资产换后一资产。asset 为 money 或物品 ID；钱为正整数，物品数量为 1–1000；hours 为 1–24 整数小时，省略为 6。报价不预留资产，也不证明对方拥有索取物。");
        text.AppendLine("`accept <trade-id>`：收件人尝试接受同地的有效报价；提交时双方资产、容量与到期条件仍须满足。`reject <trade-id>`：收件人拒绝；`withdraw <trade-id>`：发起方撤回。拒绝与撤回可在不同地点进行，途中仍不能处理报价。");
        text.AppendLine("这些操作不耗模拟时间、不结束采集或生产、不重置原活动期限、不解除跟随，也不确认已有警告；处理后 continue 保留原期限。只有发起 offer 消耗共享通信发送额度。非成交终态不会转移资产。");
        foreach (CycleCommandCheck check in observation.Commands.Where(check => check.Command.Kind is CycleCommandKind.Offer or CycleCommandKind.Accept or CycleCommandKind.Reject or CycleCommandKind.Withdraw)) {
            string condition = check.Rejection is { } reason ? "受阻：" + reason
                : check.Command.Kind == CycleCommandKind.Accept ? "可尝试接受；成交以提交结果为准" : "当前条件允许";
            text.AppendLine($"- `{CycleViewSnapshot.CommandSyntax(check.Command)}`：{condition}。");
        }
        text.AppendLine();
    }

    internal static string Describe(CycleTradeOffer offer) =>
        $"报价 {Literal(offer.Id)}：{Literal(offer.ProposerId)} → {Literal(offer.RecipientId)}；发起方给予 {Asset(offer.Give)}，索取 {Asset(offer.Want)}；{Status(offer.Status)}；到期 {Time(offer.ExpiresAt)}" +
        (offer.ClosedAt is { } closed ? $"；结束于 {Time(closed)}。" : "；尚未转移资产。");

    internal static string Status(CycleTradeStatus status) => status switch {
        CycleTradeStatus.Active => "有效报价，尚未成交", CycleTradeStatus.Accepted => "已成交，资产已转移",
        CycleTradeStatus.Rejected => "已拒绝，未转移资产", CycleTradeStatus.Withdrawn => "已撤回，未转移资产",
        CycleTradeStatus.Expired => "已到期，未转移资产", CycleTradeStatus.ParticipantFailed => "参与方已不可继续交易，报价关闭，未转移资产",
        _ => throw new InvalidOperationException("Unknown trade status.")
    };

    internal static string Asset(CycleTradeAsset asset, CycleObservation? observation = null) => asset.IsMoney ? $"{asset.Amount} 钱"
        : $"{(observation?.ItemNames.GetValueOrDefault(asset.AssetId) is { } name ? Literal(name) + " " : "")}{Literal(asset.AssetId)} ×{asset.Amount}";
    private static string Participant(CycleObservation observation, string id) {
        string? name = id == observation.Actor.Id ? observation.Actor.Name : observation.OtherCharacters.FirstOrDefault(actor => actor.Id == id)?.Name;
        return name is null ? Literal(id) : $"{Literal(name)}（{Literal(id)}）";
    }
    private static string Literal(string value) => CharacterContextRenderer.DataLiteral(value);
    private static string Time(ModelTime time) => CycleUpdateRenderer.Format(CycleVisibleValue.Numeric(time.Ticks, "ticks"));
}

using System.Globalization;
using System.Text;
using DramaBoard.ResourceCycle;

/// <summary>Economic facts and actions from the actor's frozen observation; never queries live content or other inventories.</summary>
internal static class EconomyView {
    public static void RenderStatus(StringBuilder text, CycleObservation observation) {
        if (!observation.IsExpedition) { return; }
        text.AppendLine("## 你的资金");
        text.AppendLine($"余额：{observation.Actor.Money} 钱。资金属于自己，不随往返镇与山重置。");
        text.AppendLine();
        if (observation.MarketQuotes.Count > 0) {
            text.AppendLine("## 当前市场");
            text.AppendLine("买入价是你向市场付款的单价，卖出价是市场支付给你的单价；库存与收购额度可能被其他居民使用，以提交时校验为准。");
            if (observation.MarketRefreshAt is { } refresh) {
                text.AppendLine($"下次市场补充：{CycleUpdateRenderer.Format(CycleVisibleValue.Numeric(refresh.Ticks, "ticks"))}。");
            }
            text.AppendLine("| 物品 | 买入单价 | 可买库存 | 卖出单价 | 剩余收购额度 |");
            text.AppendLine("| --- | ---: | ---: | ---: | ---: |");
            foreach (CycleMarketQuote quote in observation.MarketQuotes) {
                string buy = quote.BuyPrice > 0 ? $"{quote.BuyPrice} 钱" : "不出售";
                string sell = quote.SellPrice > 0 ? $"{quote.SellPrice} 钱" : "不收购";
                text.AppendLine($"| {Item(observation, quote.ItemId)} `{quote.ItemId}` | {buy} | {quote.Stock} 份 | {sell} | {quote.Demand} 份 |");
            }
            text.AppendLine();
        }
        if (observation.Recipes.Count > 0) {
            text.AppendLine("## 当前工坊配方");
            text.AppendLine("以下为每批投入与产出。开始生产时一次预留整批投入，完成时交付产物；主动 cancel 退回尚未完成活动的预留投入，不产出成品。");
            foreach (CycleRecipe recipe in observation.Recipes) {
                text.AppendLine($"- `{recipe.Id}`：{Item(observation, recipe.InputItem)} `{recipe.InputItem}` ×{recipe.InputCount} → "
                    + $"{Item(observation, recipe.OutputItem)} `{recipe.OutputItem}` ×{recipe.OutputCount}；每批 {Minutes(recipe.DurationTicks)} 分钟。");
            }
            text.AppendLine();
        }
    }

    public static void RenderCommands(StringBuilder text, CycleObservation observation) {
        if (observation.Outcome != CycleExpeditionOutcome.Exploring) { return; }
        if (observation.MarketQuotes.Count > 0) {
            text.AppendLine("### 市场买卖");
            text.AppendLine("`buy <item-id> <count>` / `sell <item-id> <count>`：count 为 1–1000 的整数。即时完成，不取消活动或重置期限；钱、物品、市场库存与收购额度一起结算，拒绝不扣款扣物。");
            foreach (CycleMarketQuote quote in observation.MarketQuotes) {
                if (quote.BuyPrice > 0) { Command(text, observation, CycleCommand.Buy(quote.ItemId), $"buy {quote.ItemId} 1", $"花费 {quote.BuyPrice} 钱，买入 1 份{Item(observation, quote.ItemId)}"); }
                if (quote.SellPrice > 0) { Command(text, observation, CycleCommand.Sell(quote.ItemId), $"sell {quote.ItemId} 1", $"卖出 1 份{Item(observation, quote.ItemId)}，获得 {quote.SellPrice} 钱"); }
            }
            text.AppendLine();
        }
        if (observation.Recipes.Count > 0) {
            text.AppendLine("### 工坊生产");
            text.AppendLine("`produce <recipe-id> <batches>`：batches 为 1–1000 的整数，投入、产出与耗时按批数累加。生产占用耗时活动；处理通信后 continue 保留原期限。");
            foreach (CycleRecipe recipe in observation.Recipes) {
                Command(text, observation, CycleCommand.Produce(recipe.Id), $"produce {recipe.Id} 1", $"预留 {recipe.InputCount} 份{Item(observation, recipe.InputItem)}，{Minutes(recipe.DurationTicks)} 分钟后取得 {recipe.OutputCount} 份{Item(observation, recipe.OutputItem)}");
            }
            text.AppendLine();
        }
    }

    public static bool RenderProduction(StringBuilder text, CycleObservation observation, bool ended) {
        if (observation.Actor.Activity is not { Kind: CycleActivityKind.Produce, Production: { } order } activity) { return false; }
        string due = CycleUpdateRenderer.Format(CycleVisibleValue.Numeric(activity.Due.Ticks, "ticks"));
        text.AppendLine($"{(ended ? "未完成的生产记录" : "正在生产")}：`{order.RecipeId}` ×{order.Batches} 批；{(ended ? "原截止" : "完成于")} {due}。");
        text.AppendLine($"本次预留：{Item(observation, order.InputItem)} `{order.InputItem}` ×{order.InputCount}；"
            + $"计划产出：{Item(observation, order.OutputItem)} `{order.OutputItem}` ×{order.OutputCount}。");
        text.AppendLine(ended ? "记录已冻结，不再继续生产。" : "补给、买卖或处理通信不重置生产期限；cancel 取消本次生产并退回预留投入，不生成产物。");
        return true;
    }

    private static void Command(StringBuilder text, CycleObservation observation, CycleCommand command, string syntax, string detail) {
        string? reason = observation.CheckDisplayed(command);
        text.AppendLine($"- `{syntax}`：{detail}；{(reason is null ? "可执行。" : $"受阻：{reason}")}");
    }

    private static string Item(CycleObservation observation, string id) => observation.ItemNames.GetValueOrDefault(id, id);
    private static string Minutes(long ticks) => ((decimal)ticks / 60_000).ToString("0.##", CultureInfo.InvariantCulture);
}

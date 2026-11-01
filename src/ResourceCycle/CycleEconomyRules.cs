using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Economic operations preserve one world's assets and existing activity commitments.</summary>
public static class CycleEconomyRules {
    public static bool CanSettleActivity(CycleCharacter actor, ItemInventory backpack) =>
        actor.Activity?.Production is not { } order ||
        backpack.CheckAdd(order.InputItem, order.InputCount) == InventoryAdmission.Allowed &&
        backpack.CheckAdd(order.OutputItem, order.OutputCount) == InventoryAdmission.Allowed;

    public static ItemInventory ReturnActivityInputs(CycleCharacter actor) => actor.Activity?.Production is { } order
        ? actor.Backpack.Add(order.InputItem, order.InputCount) : actor.Backpack;

    internal static string? Check(CycleWorld world, string actorId, CycleCommand command) {
        if (world.Business.Economy is not { } economy) { return "此世界尚未启用经济。"; }
        CycleCharacter actor = world.GetCharacter(actorId);
        if (command.Hours != 0 || command.ResourceId is not null || command.All || command.PassageId is not null ||
            command.HandoffText is not null || command.SpeechText is not null || command.Goal is not null) {
            return "经济行动只接受声明的参数。";
        }
        try {
            switch (command.Kind) {
                case CycleCommandKind.Buy or CycleCommandKind.Sell: {
                    if (command.RecipeId is not null || command.TargetActorId is not null || command.MoneyAmount != 0 ||
                        command.ItemId is null || command.Count is < 1 or > 1000) { return "买卖需要物品及 1 至 1000 的数量。"; }
                    if (CycleActions.CurrentPlace(world, actorId) != world.Definition.MarketPlaceId) { return "买卖需要身处市场。"; }
                    bool buy = command.Kind == CycleCommandKind.Buy;
                    string item = command.ItemId;
                    int price = buy ? world.Definition.BuyPrice(item) : world.Definition.SellPrice(item);
                    if (price <= 0) { return buy ? "市场不出售此物品。" : "市场不收购此物品。"; }
                    if ((buy ? economy.Market.Stock : economy.Market.Demand).GetValueOrDefault(item) < command.Count) {
                        return buy ? "市场今日供应额度不足。" : "市场今日收购额度不足。";
                    }
                    long payment = checked((long)price * command.Count);
                    if (buy) {
                        if (actor.Money < payment) { return "现金不足。"; }
                        if (actor.Backpack.CheckAdd(item, command.Count) != InventoryAdmission.Allowed) { return "背包无法容纳这些物品。"; }
                        if (!CanSettleActivity(actor, actor.Backpack.Add(item, command.Count))) { return "需要为生产完成及取消退料分别保留容量。"; }
                        _ = economy.Totals.Import(item, command.Count, payment);
                    } else {
                        if (actor.Backpack.Count(item) < command.Count) { return "背包中的物品数量不足。"; }
                        _ = checked(actor.Money + payment);
                        _ = economy.Totals.Export(item, command.Count, payment);
                    }
                    return null;
                }
                case CycleCommandKind.Produce: {
                    if (command.ItemId is not null || command.TargetActorId is not null || command.MoneyAmount != 0 ||
                        command.RecipeId is null || command.Count is < 1 or > 1000) { return "生产需要配方及 1 至 1000 的批次。"; }
                    if (CycleActions.CurrentPlace(world, actorId) != world.Definition.WorkshopPlaceId) { return "生产需要身处工坊。"; }
                    if (actor.Activity is not null || actor.FollowingLeaderId is not null) { return "请先结束当前活动或解除跟随。"; }
                    CycleRecipe? recipe = world.Definition.Recipe(command.RecipeId);
                    if (recipe is null) { return "没有这个配方。"; }
                    int inputs = checked(recipe.InputCount * command.Count);
                    if (actor.Backpack.Count(recipe.InputItem) < inputs) { return "配方原料不足。"; }
                    _ = checked(actor.NextActivityId + 1);
                    CycleActivity activity = ProductionActivity(actor, recipe, command.Count, world.Time);
                    ItemInventory remaining = actor.Backpack.Remove(recipe.InputItem, inputs);
                    if (!CanSettleActivity(actor.Change(activity), remaining)) { return "需要能分别容纳全部产物和取消退料。"; }
                    // Reject a completion already known to exceed the cumulative representation limit.
                    _ = economy.Totals.Produce(recipe, command.Count);
                    return null;
                }
                case CycleCommandKind.GiveMoney: {
                    if (command.ItemId is not null || command.RecipeId is not null || command.Count != 0 || command.MoneyAmount <= 0) {
                        return "赠钱需要目标角色及正整数金额。";
                    }
                    if (!CycleCommunicationRules.CanTarget(world, actorId, command.TargetActorId)) { return "当前不可见或不可交互的角色。"; }
                    if (CycleCommunicationRules.Remaining(actor, world.Time) <= 0) { return "本模型时刻的 4 次交流额度已用完。"; }
                    CycleCharacter target = world.GetCharacter(command.TargetActorId!);
                    if (!CycleCommunicationRules.CanReceiveShout(world.Business, target) ||
                        world.Business.InteractionStates[actorId].NextExperienceSequence == long.MaxValue) { return "当前无法交付这次赠与。"; }
                    if (actor.Money < command.MoneyAmount) { return "现金不足。"; }
                    _ = checked(target.Money + command.MoneyAmount);
                    return null;
                }
                default: return "未知经济行动。";
            }
        } catch (OverflowException) {
            return "金额、数量、时间或累计账已达到支持上限。";
        }
    }

    private static CycleActivity ProductionActivity(CycleCharacter actor, CycleRecipe recipe, int batches, ModelTime at) {
        long duration = checked(recipe.DurationTicks * batches);
        var order = new CycleProductionOrder(recipe.Id, batches, recipe.InputItem, checked(recipe.InputCount * batches),
            recipe.OutputItem, checked(recipe.OutputCount * batches));
        return new(CycleActivityKind.Produce, actor.NextActivityId, at, duration, new(checked(at.Ticks + duration)), production: order);
    }

    internal static CycleFact Plan(CycleWorld world, string actorId, CycleCommand command) {
        string? failure = Check(world, actorId, command);
        if (failure is not null) { throw new InvalidOperationException(failure); }
        CycleCharacter actor = world.GetCharacter(actorId);
        CycleEconomyState economy = world.Business.Economy!;
        if (command.Kind == CycleCommandKind.GiveMoney) {
            CycleCharacter target = world.GetCharacter(command.TargetActorId!);
            string result = $"已将 {command.MoneyAmount} 钱赠与 {target.Name}。";
            CycleCharacter sent = actor.Change(actor.Activity, result: result, money: checked(actor.Money - command.MoneyAmount),
                communication: actor.Communication.Sent(world.Time));
            CycleCharacter received = target.Change(target.Activity, money: checked(target.Money + command.MoneyAmount),
                communication: target.Communication.Receive());
            return new(CycleEventKind.MoneyGiven, world.Time, actorId,
                changedCharacters: world.Characters.Where(c => c.Id == actorId || c.Id == target.Id).Select(c => c.Id == actorId ? sent : received),
                lastServedActorId: actorId, result: result, targetActorId: target.Id, moneyAmount: command.MoneyAmount);
        }
        if (command.Kind == CycleCommandKind.Produce) {
            CycleRecipe recipe = world.Definition.Recipe(command.RecipeId!)!;
            CycleActivity activity = ProductionActivity(actor, recipe, command.Count, world.Time);
            string result = $"开始生产 {world.Definition.Items[recipe.OutputItem]} ×{activity.Production!.OutputCount}，预计 {activity.Due} 完成。";
            return new(CycleEventKind.ProductionStarted, world.Time, actorId,
                changedCharacters: [actor.Change(activity, actor.Backpack.Remove(recipe.InputItem, activity.Production.InputCount), notices: [],
                    result: result, nextActivityId: checked(actor.NextActivityId + 1), communication: actor.Communication.Acknowledge())],
                lastServedActorId: actorId, result: result);
        }
        string item = command.ItemId!;
        bool buy = command.Kind == CycleCommandKind.Buy;
        long payment = checked((long)(buy ? world.Definition.BuyPrice(item) : world.Definition.SellPrice(item)) * command.Count);
        ItemInventory backpack = buy ? actor.Backpack.Add(item, command.Count) : actor.Backpack.Remove(item, command.Count);
        string description = $"已{(buy ? "购买" : "出售")} {world.Definition.Items[item]} ×{command.Count}，{(buy ? "花费" : "获得")} {payment} 钱。";
        CycleEconomyState after = buy
            ? economy.Change(economy.Market.Buy(item, command.Count), economy.Totals.Import(item, command.Count, payment))
            : economy.Change(economy.Market.Sell(item, command.Count), economy.Totals.Export(item, command.Count, payment));
        return new(buy ? CycleEventKind.MarketBought : CycleEventKind.MarketSold, world.Time, actorId,
            changedCharacters: [actor.Change(actor.Activity, backpack, result: description, money: checked(actor.Money + (buy ? -payment : payment)))],
            lastServedActorId: actorId, result: description, itemId: item, itemCount: command.Count, economy: after);
    }

    internal static CycleFact Complete(CycleWorld world, string actorId, ModelTime at) {
        CycleCharacter actor = world.GetCharacter(actorId);
        if (actor.Activity is not { Kind: CycleActivityKind.Produce, Production: { } order } activity || activity.Due != at) {
            throw new InvalidOperationException("No production is due at this boundary.");
        }
        CycleEconomyState economy = world.Business.Economy ?? throw new InvalidOperationException("Production requires economy.");
        CycleRecipe recipe = world.Definition.Recipe(order.RecipeId)!;
        string result = $"生产完成，取得 {world.Definition.Items[order.OutputItem]} ×{order.OutputCount}。";
        return new(CycleEventKind.ProductionCompleted, at, actorId,
            changedCharacters: [actor.Change(null, actor.Backpack.Add(order.OutputItem, order.OutputCount), result: result)],
            result: result, economy: economy.Change(totals: economy.Totals.Produce(recipe, order.Batches)));
    }

    internal static CycleFact Refresh(CycleWorld world, ModelTime at) {
        CycleEconomyState economy = world.Business.Economy ?? throw new InvalidOperationException("Market requires economy.");
        if (at != economy.Market.NextRefresh) { throw new InvalidOperationException("Market is not due for refresh."); }
        var market = CycleMarket.Fresh(world.Definition, new(checked(at.Ticks + world.Definition.MarketRefreshTicks)), checked(economy.Market.Cycle + 1));
        return new(CycleEventKind.MarketRefreshed, at, result: "市场补充了今日供应和收购额度。", economy: economy.Change(market: market));
    }

    internal static CycleFact Enrich(CycleWorld world, CycleFact fact) {
        if (world.Business.Economy is not { } economy) { return fact; }
        if (fact.Kind == CycleEventKind.GatherYielded) {
            return fact.WithEconomy(economy.Change(totals: economy.Totals.Harvest(world.GetResource(fact.ResourceId!).ItemId, 1)));
        }
        if (fact.Kind == CycleEventKind.ItemUsed) {
            CycleCharacter before = world.GetCharacter(fact.ActorId!);
            CycleCharacter after = fact.ChangedCharacters.Single(actor => actor.Id == before.Id);
            string item = before.Backpack.Items.Keys.Single(id => before.Backpack.Count(id) - after.Backpack.Count(id) == 1);
            return fact.WithEconomy(economy.Change(totals: economy.Totals.Consume(item, 1)));
        }
        return fact;
    }
}

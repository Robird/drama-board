using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Checks recorded economic effects without invoking planning, controllers, or replaying a decision.</summary>
internal static class PlaygroundEconomyValidation {
    internal static void Validate(PlaygroundBusinessState before, PlaygroundFact fact) {
        PlaygroundEconomyState? expectedEconomy = before.Economy;
        PlaygroundTradeState? expectedTrades = PlaygroundTradeValidation.Validate(before, fact);
        bool economicOperation = fact.Kind is PlaygroundEventKind.MarketBought or PlaygroundEventKind.MarketSold or PlaygroundEventKind.ProductionStarted or PlaygroundEventKind.MoneyGiven;
        bool economicEvent = economicOperation || fact.Kind is PlaygroundEventKind.ProductionCompleted or PlaygroundEventKind.MarketRefreshed;
        if (economicEvent && before.Economy is null) { Fail("Economic effects require an enabled economy."); }
        if (fact.Kind != PlaygroundEventKind.MoneyGiven && fact.MoneyAmount != 0) { Fail("Only a monetary gift records a monetary payload."); }
        var inventories = before.Characters.ToDictionary(actor => actor.Id, actor => actor.Backpack, StringComparer.Ordinal);
        var money = before.Characters.ToDictionary(actor => actor.Id, actor => actor.Money, StringComparer.Ordinal);
        PlaygroundCommand? command = fact.RawInput is null ? null : PlaygroundCommandParser.Parse(fact.RawInput);
        PlaygroundCharacter? actor = fact.ActorId is { } id ? before.GetCharacter(id) : null;
        if (economicOperation) {
            if (actor is null || command is null || fact.At != before.Time || !PlaygroundActions.NeedsDecision(before, actor.Id) ||
                PlaygroundActions.Location(before, actor.Id) is not AtPlaceLocation || actor.DecisionGeneration >= long.MaxValue - 2 ||
                command.Hours != 0 || command.ResourceId is not null || command.All || command.PassageId is not null ||
                command.HandoffText is not null || command.Goal is not null || command.SpeechText is not null) {
                Fail("Economic operations require an active decision and only their declared parameters.");
            }
        }
        switch (fact.Kind) {
            case PlaygroundEventKind.TradeAccepted: {
                PlaygroundTradeOffer offer = before.Economy!.Trades.Offers.Single(entry => entry.Id == command!.TradeId);
                foreach (PlaygroundCharacter participant in before.Characters.Where(value => offer.Involves(value.Id))) {
                    PlaygroundTradeAsset payment = participant.Id == offer.ProposerId ? offer.Give : offer.Want;
                    PlaygroundTradeAsset receipt = participant.Id == offer.ProposerId ? offer.Want : offer.Give;
                    if (payment.IsMoney) {
                        if (participant.Money < payment.Amount) { Fail("A trade requires the full gross payment."); }
                        money[participant.Id] -= payment.Amount;
                    } else { inventories[participant.Id] = participant.Backpack.Remove(payment.AssetId, checked((int)payment.Amount)); }
                    if (receipt.IsMoney) { money[participant.Id] = checked(money[participant.Id] + receipt.Amount); }
                    else { inventories[participant.Id] = inventories[participant.Id].Add(receipt.AssetId, checked((int)receipt.Amount)); }
                }
                break;
            }
            case PlaygroundEventKind.MarketBought:
            case PlaygroundEventKind.MarketSold: {
                PlaygroundCommand input = command!;
                bool buying = fact.Kind == PlaygroundEventKind.MarketBought;
                if (input.Kind != (buying ? PlaygroundCommandKind.Buy : PlaygroundCommandKind.Sell) || input.Count is < 1 or > 1000 || input.ItemId is null ||
                    input.RecipeId is not null || input.TargetActorId is not null || input.MoneyAmount != 0 ||
                    fact.ItemId != input.ItemId || fact.ItemCount != input.Count || Place(before, actor!.Id) != before.Definition.MarketPlaceId) { Fail("Invalid market operation provenance or location."); }
                string item = input.ItemId!;
                int price = buying ? before.Definition.BuyPrice(item) : before.Definition.SellPrice(item);
                if (price <= 0) { Fail("This item is not traded by the market."); }
                long total = checked((long)price * input.Count);
                PlaygroundMarket market = before.Economy!.Market;
                if (buying) {
                    if (actor!.Money < total || market.Stock.GetValueOrDefault(item) < input.Count || actor.Backpack.CheckAdd(item, input.Count) != InventoryAdmission.Allowed) { Fail("Purchase cannot settle."); }
                    inventories[actor!.Id] = actor.Backpack.Add(item, input.Count);
                    money[actor.Id] = checked(actor.Money - total);
                    expectedEconomy = before.Economy.Change(market.Buy(item, input.Count), before.Economy.Totals.Import(item, input.Count, total));
                } else {
                    if (actor!.Backpack.Count(item) < input.Count || market.Demand.GetValueOrDefault(item) < input.Count || actor.Money > long.MaxValue - total) { Fail("Sale cannot settle."); }
                    inventories[actor.Id] = actor.Backpack.Remove(item, input.Count);
                    money[actor.Id] = checked(actor.Money + total);
                    expectedEconomy = before.Economy.Change(market.Sell(item, input.Count), before.Economy.Totals.Export(item, input.Count, total));
                }
                break;
            }
            case PlaygroundEventKind.ProductionStarted: {
                PlaygroundCommand input = command!;
                PlaygroundRecipe? recipe = input.RecipeId is null ? null : before.Definition.Recipe(input.RecipeId);
                if (input.Kind != PlaygroundCommandKind.Produce || input.Count is < 1 or > 1000 || recipe is null || input.ItemId is not null ||
                    input.TargetActorId is not null || input.MoneyAmount != 0 || actor!.Activity is not null || actor.FollowingLeaderId is not null ||
                    Place(before, actor.Id) != before.Definition.WorkshopPlaceId || actor.NextActivityId == long.MaxValue) { Fail("Invalid production start."); }
                int count = checked(recipe!.InputCount * input.Count);
                if (actor!.Backpack.Count(recipe.InputItem) < count) { Fail("Production has insufficient input."); }
                inventories[actor.Id] = actor.Backpack.Remove(recipe.InputItem, count);
                break;
            }
            case PlaygroundEventKind.ProductionCompleted: {
                if (actor?.Activity is not { Kind: PlaygroundActivityKind.Produce, Production: { } } production || production.Due != fact.At) { Fail("Production can complete only at its recorded deadline."); }
                PlaygroundProductionOrder order = actor!.Activity!.Production!;
                PlaygroundRecipe recipe = before.Definition.Recipe(order.RecipeId)!;
                if (actor.Backpack.CheckAdd(order.OutputItem, order.OutputCount) != InventoryAdmission.Allowed) { Fail("Production output cannot fit."); }
                inventories[actor.Id] = actor.Backpack.Add(order.OutputItem, order.OutputCount);
                expectedEconomy = before.Economy!.Change(totals: before.Economy.Totals.Produce(recipe, order.Batches));
                break;
            }
            case PlaygroundEventKind.MarketRefreshed: {
                PlaygroundMarket market = before.Economy!.Market;
                if (fact.At != market.NextRefresh) { Fail("Market refresh must occur at its committed deadline."); }
                expectedEconomy = before.Economy.Change(market: PlaygroundMarket.Fresh(before.Definition, new(checked(fact.At.Ticks + before.Definition.MarketRefreshTicks)), checked(market.Cycle + 1)));
                break;
            }
            case PlaygroundEventKind.MoneyGiven: {
                PlaygroundCommand input = command!;
                if (input.Kind != PlaygroundCommandKind.GiveMoney || input.Count != 0 || input.ItemId is not null || input.RecipeId is not null ||
                    input.MoneyAmount <= 0 || input.MoneyAmount != fact.MoneyAmount || input.TargetActorId != fact.TargetActorId ||
                    !PlaygroundCommunicationRules.CanTarget(before, actor!.Id, input.TargetActorId) || PlaygroundCommunicationRules.Remaining(actor, fact.At) <= 0) { Fail("Invalid monetary gift."); }
                PlaygroundCharacter recipient = before.GetCharacter(input.TargetActorId!);
                if (actor!.Money < input.MoneyAmount || recipient.Money > long.MaxValue - input.MoneyAmount ||
                    !PlaygroundCommunicationRules.CanReceiveShout(before, recipient) || !PlaygroundCommunicationRules.CanReceiveShout(before, actor)) { Fail("Monetary gift cannot settle or deliver."); }
                money[actor.Id] = actor.Money - input.MoneyAmount;
                money[recipient.Id] = checked(recipient.Money + input.MoneyAmount);
                string[] participants = before.Characters.Where(value => value.Id == actor.Id || value.Id == recipient.Id).Select(value => value.Id).ToArray();
                if (!participants.SequenceEqual(fact.ChangedCharacters.Select(value => value.Id))) { Fail("A monetary gift changes exactly its participants in world order."); }
                break;
            }
            case PlaygroundEventKind.GatherYielded: {
                PlaygroundResource resource = before.GetResource(fact.ResourceId!);
                inventories[actor!.Id] = actor.Backpack.Add(resource.ItemId);
                if (expectedEconomy is not null) { expectedEconomy = expectedEconomy.Change(totals: expectedEconomy.Totals.Harvest(resource.ItemId, 1)); }
                break;
            }
            case PlaygroundEventKind.ItemUsed: {
                if (command?.Kind != PlaygroundCommandKind.Use || command.ItemId is null) { Fail("Consumption requires its original item command."); }
                inventories[actor!.Id] = actor.Backpack.Remove(command!.ItemId!);
                if (expectedEconomy is not null) { expectedEconomy = expectedEconomy.Change(totals: expectedEconomy.Totals.Consume(command.ItemId!, 1)); }
                break;
            }
            case PlaygroundEventKind.ItemGiven:
                inventories[actor!.Id] = actor.Backpack.Remove(fact.ItemId!, fact.ItemCount);
                PlaygroundCharacter receiver = before.GetCharacter(fact.TargetActorId!);
                inventories[receiver.Id] = receiver.Backpack.Add(fact.ItemId!, fact.ItemCount);
                break;
            case PlaygroundEventKind.ActivityCancelled:
                inventories[actor!.Id] = PlaygroundEconomyRules.ReturnActivityInputs(actor);
                break;
        }
        foreach (PlaygroundCharacter prior in before.Characters) {
            PlaygroundCharacter next = fact.ChangedCharacters.SingleOrDefault(value => value.Id == prior.Id) ?? prior;
            if (prior.IsActive && !next.IsActive) {
                if (fact.Kind is not (PlaygroundEventKind.SurvivalFailed or PlaygroundEventKind.WeatherResolved)) { Fail("Only death can retire production holdings."); }
                inventories[prior.Id] = PlaygroundEconomyRules.ReturnActivityInputs(prior);
            }
            if (next.Money != money[prior.Id] || !PlaygroundObservationEquality.SameInventory(next.Backpack, inventories[prior.Id])) {
                Fail("Recorded asset changes do not match the precise economic effect.");
            }
            if (!PlaygroundEconomyRules.CanSettleActivity(next, next.Backpack)) { Fail("An asset transfer blocks production completion or cancellation."); }
            ValidateProduction(before, fact, prior, next, command);
            if (economicOperation || fact.Kind == PlaygroundEventKind.ProductionCompleted) { ValidatePreservation(before, fact, prior, next); }
            if (fact.Kind == PlaygroundEventKind.ItemUsed && prior.Id == fact.ActorId) {
                string item = command!.ItemId!;
                if (fact.At != before.Time || !PlaygroundActions.NeedsDecision(before, prior.Id) || prior.Survival is null ||
                    PlaygroundSurvivalRules.UseDescription(before.Definition, item) is null || prior.Survival.Project(fact.At).IsFatal) { Fail("Consumption requires an available active body and a known supply."); }
                PlaygroundSurvival projected = prior.Survival!.Project(fact.At);
                if ((before.Definition.SupplyFood(item) <= 0 || projected.FoodUnits == PlaygroundSurvival.MaximumUnits) &&
                    (before.Definition.SupplyWater(item) <= 0 || projected.WaterUnits == PlaygroundSurvival.MaximumUnits)) { Fail("A full need cannot consume a supply without benefit."); }
                PlaygroundSurvival expectedBody = prior.Survival.Supply(fact.At, item, before.Definition).WithRates(fact.At, prior.Survival.FoodRate, prior.Survival.WaterRate,
                    PlaygroundSurvivalRules.WarmthRate(before.Definition, PlaygroundSurvivalRules.IsSheltered(before.Definition, before.Expedition!.Spatial, prior.Id), before.Light.IsDay));
                if (!SameBody(expectedBody, next.Survival) || !PlaygroundObservationEquality.SameActivity(prior.Activity, next.Activity) ||
                    prior.NextActivityId != next.NextActivityId || prior.Name != next.Name || prior.SkillLevel != next.SkillLevel ||
                    prior.Ending != next.Ending || prior.FollowingLeaderId != next.FollowingLeaderId || prior.Communication != next.Communication ||
                    next.DecisionGeneration != checked(prior.DecisionGeneration + 1) || next.LastResult != fact.Result ||
                    !prior.Notices.Select(value => value.ToString()).SequenceEqual(next.Notices.Select(value => value.ToString()))) {
                    Fail("Consumption must apply exactly the declared supply and preserve unrelated state.");
                }
            }
        }
        bool changesTrades = before.Economy is not null && !before.Economy.Trades.SameState(expectedTrades);
        if (expectedEconomy is not null && expectedTrades is not null) { expectedEconomy = expectedEconomy.Change(trades: expectedTrades); }
        bool changesEconomy = changesTrades || fact.Kind is PlaygroundEventKind.MarketBought or PlaygroundEventKind.MarketSold or PlaygroundEventKind.ProductionCompleted or PlaygroundEventKind.MarketRefreshed ||
            before.Economy is not null && fact.Kind is PlaygroundEventKind.GatherYielded or PlaygroundEventKind.ItemUsed;
        if (!changesEconomy && fact.Economy is not null || changesEconomy && fact.Economy is null ||
            (expectedEconomy is null ? fact.Economy is not null : !expectedEconomy.SameState(fact.Economy ?? before.Economy))) {
            Fail("Recorded economy replacement does not match the exact external and production flows.");
        }
    }

    private static void ValidateProduction(PlaygroundBusinessState before, PlaygroundFact fact, PlaygroundCharacter prior, PlaygroundCharacter next, PlaygroundCommand? command) {
        if (fact.Kind == PlaygroundEventKind.ProductionStarted && prior.Id == fact.ActorId) {
            PlaygroundRecipe recipe = before.Definition.Recipe(command!.RecipeId!)!;
            var order = new PlaygroundProductionOrder(recipe.Id, command.Count, recipe.InputItem, checked(recipe.InputCount * command.Count), recipe.OutputItem, checked(recipe.OutputCount * command.Count));
            long duration = checked(recipe.DurationTicks * command.Count);
            var expected = new PlaygroundActivity(PlaygroundActivityKind.Produce, prior.NextActivityId, fact.At, duration,
                new(checked(fact.At.Ticks + duration)), production: order);
            if (!PlaygroundObservationEquality.SameActivity(expected, next.Activity) || next.NextActivityId != checked(prior.NextActivityId + 1)) { Fail("Production escrow and deadline must match the accepted recipe exactly."); }
        } else if (prior.Activity?.Production is not null || next.Activity?.Production is not null) {
            bool clears = prior.Id == fact.ActorId && fact.Kind is PlaygroundEventKind.ActivityCancelled or PlaygroundEventKind.ProductionCompleted || prior.IsActive && !next.IsActive;
            if (clears ? next.Activity is not null : !PlaygroundObservationEquality.SameActivity(prior.Activity, next.Activity)) { Fail("Only completion, cancellation or death can consume production escrow."); }
            if (next.NextActivityId != prior.NextActivityId) { Fail("Unrelated effects cannot change production activity identity."); }
        }
    }

    private static void ValidatePreservation(PlaygroundBusinessState before, PlaygroundFact fact, PlaygroundCharacter prior, PlaygroundCharacter next) {
        bool participant = prior.Id == fact.ActorId || fact.Kind == PlaygroundEventKind.MoneyGiven && prior.Id == fact.TargetActorId;
        if (!participant) {
            if (!ReferenceEquals(prior, next)) { Fail("An economic operation changed an unrelated character."); }
            return;
        }
        bool start = fact.Kind == PlaygroundEventKind.ProductionStarted;
        bool completed = fact.Kind == PlaygroundEventKind.ProductionCompleted;
        PlaygroundCommunicationState communication = start ? prior.Communication.Acknowledge() : fact.Kind == PlaygroundEventKind.MoneyGiven
            ? prior.Id == fact.ActorId ? prior.Communication.Sent(fact.At) : prior.Communication.Receive() : prior.Communication;
        PlaygroundSurvival? body = prior.Survival?.Project(fact.At);
        if (start || completed) {
            body = body?.WithRates(fact.At, body.FoodRate, body.WaterRate,
                PlaygroundSurvivalRules.WarmthRate(before.Definition, PlaygroundSurvivalRules.IsSheltered(before.Definition, before.Expedition!.Spatial, prior.Id), before.Light.IsDay));
            if (start) { body = body?.Acknowledge(fact.At); }
        }
        if (prior.Name != next.Name || prior.SkillLevel != next.SkillLevel || prior.Ending != next.Ending || prior.FollowingLeaderId != next.FollowingLeaderId ||
            next.DecisionGeneration != checked(prior.DecisionGeneration + 1) || next.Communication != communication ||
            !SameBody(body, next.Survival) || !(start ? Array.Empty<string>() : prior.Notices.Select(value => value.ToString())).SequenceEqual(next.Notices.Select(value => value.ToString())) ||
            next.LastResult != (prior.Id == fact.ActorId ? fact.Result : prior.LastResult) ||
            !start && next.NextActivityId != prior.NextActivityId || !start && !completed && !PlaygroundObservationEquality.SameActivity(prior.Activity, next.Activity)) {
            Fail("An economic operation changed unrelated activity, physiology, reminders, or identity.");
        }
        if (completed && next.Activity is not null) { Fail("Production completion must clear its activity."); }
    }
    private static bool SameBody(PlaygroundSurvival? left, PlaygroundSurvival? right) => left is null ? right is null : right is not null &&
        left.BaselineAt == right.BaselineAt && left.FoodUnits == right.FoodUnits && left.WaterUnits == right.WaterUnits && left.WarmthUnits == right.WarmthUnits &&
        left.FoodRate.SameAs(right.FoodRate) && left.WaterRate.SameAs(right.WaterRate) &&
        left.WarmthRate.SameAs(right.WarmthRate) && left.WarnedMask == right.WarnedMask && left.PendingMask == right.PendingMask && left.PendingSince == right.PendingSince;
    private static string? Place(PlaygroundBusinessState business, string actorId) => (PlaygroundActions.Location(business, actorId) as AtPlaceLocation)?.PlaceId.Value;
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new InvalidOperationException(message);
}

using DramaBoard.Goals;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

/// <summary>Pure application rules. Inventory and Goal rules stay reusable; cross-component settlement stays here.</summary>
public static class TownPlanning {
    public static string? Check(TownBusinessState state, string actorId, TownCommand command) {
        ArgumentNullException.ThrowIfNull(command);
        TownCharacter actor = state.Character(actorId);
        if (state.Ended || actor.Failed) { return "This resident can no longer act."; }
        if (command.Kind == TownCommandKind.Invalid || !Enum.IsDefined(command.Kind)) { return command.ParseError ?? "Unknown command."; }
        if (command.Quantity <= 0 || command.Quantity > 1000) { return "Quantity must be between 1 and 1000."; }
        if (command.Kind == TownCommandKind.Goal) { return command.Goal is null ? "Missing goal operation." : null; }
        if (command.Kind == TownCommandKind.Eat) { return actor.Inventory.Count("meal") > 0 ? null : "You have no meal."; }
        if (actor.Activity is not null || state.Place(actorId) is null) { return "Finish the current activity first."; }
        if (command.Kind == TownCommandKind.Wait) { return null; }
        if (command.Kind == TownCommandKind.Go) {
            if (string.IsNullOrWhiteSpace(command.Target)) { return "Missing passage."; }
            return TownContent.Planner.TryStartTraversal(state.Spatial, new(actorId), new(command.Target), 1, state.Time) is SpatialPlanRejected rejected
                ? rejected.Reason : null;
        }
        if (command.Kind == TownCommandKind.Produce) {
            TownRecipe? recipe = TownContent.Recipe(command.Target ?? "");
            if (state.Place(actorId) != "workshop") { return "Production requires the workshop."; }
            if (recipe is null) { return "Unknown recipe."; }
            if (actor.Inventory.Count(recipe.Input) < (long)recipe.InputCount * command.Quantity) { return "Insufficient ingredients."; }
            return actor.Inventory.Count(recipe.Output) > int.MaxValue - (long)recipe.OutputCount * command.Quantity ? "Output quantity would overflow." : null;
        }
        if (state.Place(actorId) != "market") { return "External trading requires the market."; }
        string item = command.Target ?? "";
        if (command.Kind == TownCommandKind.Buy) {
            int price = TownContent.BuyPrice(item);
            if (price <= 0) { return "The external market does not sell this item."; }
            if (state.Market.Stock.GetValueOrDefault(item) < command.Quantity) { return "External stock is insufficient."; }
            if (actor.Money < (long)price * command.Quantity) { return "Insufficient money."; }
            return actor.Inventory.CheckAdd(item, command.Quantity) != Inventory.InventoryAdmission.Allowed ? "Inventory cannot accept the purchase." : null;
        }
        if (command.Kind == TownCommandKind.Sell) {
            int price = TownContent.SellPrice(item);
            if (price <= 0) { return "The external market does not buy this item."; }
            if (state.Market.Demand.GetValueOrDefault(item) < command.Quantity) { return "External purchase quota is insufficient."; }
            if (actor.Inventory.Count(item) < command.Quantity) { return "Insufficient items."; }
            return actor.Money > long.MaxValue - (long)price * command.Quantity ? "Money balance would overflow." : null;
        }
        return "Unknown command.";
    }

    /// <summary>Developer/script seam. No controller provenance is invented for an already parsed command.</summary>
    public static TownFact Plan(TownWorld world, string actorId, TownCommand command) => PlanCore(world, actorId, command, null);

    /// <summary>Validates captured provenance before parsing; both accepted and rejected results are recorded in the fact and resident state.</summary>
    public static TownFact Plan(TownWorld world, string actorId, TownControllerOperation operation) {
        ArgumentNullException.ThrowIfNull(operation);
        operation.Validate();
        return PlanCore(world, actorId, TownCommand.Parse(operation.RawInput), operation);
    }

    private static TownFact PlanCore(TownWorld world, string actorId, TownCommand command, TownControllerOperation? source) {
        TownBusinessState before = world.Business;
        if (before.OpenDecisionActorId != actorId) { throw new InvalidOperationException("The operation requires this actor's published decision boundary."); }
        TownCharacter actor = before.Character(actorId);
        string? error = Check(before, actorId, command);
        long operation = checked(actor.OperationSequence + 1);
        GoalOperationResult? goal = error is null && command.Kind == TownCommandKind.Goal
            ? GoalRules.Apply(actor.Goals, command.Goal!, actor.DecisionSequence, operation) : null;
        error ??= goal?.Error;
        if (error is not null) {
            TownOperationReceipt? receipt = source is null ? null : new(actorId, source, false, error, before.Time, actor.DecisionSequence, operation, goal?.Receipt);
            TownCharacter rejected = actor with { OperationSequence = operation, LastOperationReceipt = receipt ?? actor.LastOperationReceipt };
            return new(TownEventKind.Operation, before, Replace(before, rejected, openDecision: actorId), actorId, error, false,
                goalReceipt: goal?.Receipt, operationReceipt: receipt);
        }
        TownCharacter changed = actor with { OperationSequence = operation };
        TownMarket market = before.Market;
        TownEconomicTotals economy = before.Economy;
        IReadOnlyList<GraphSpatialFact> spatialFacts = [];
        switch (command.Kind) {
            case TownCommandKind.Buy: {
                string item = command.Target!; long price = checked((long)TownContent.BuyPrice(item) * command.Quantity);
                changed = changed with { Inventory = actor.Inventory.Add(item, command.Quantity), Money = checked(actor.Money - price) };
                market = market.Buy(item, command.Quantity); economy = economy.Import(item, command.Quantity, price); break;
            }
            case TownCommandKind.Sell: {
                string item = command.Target!; long price = checked((long)TownContent.SellPrice(item) * command.Quantity);
                changed = changed with { Inventory = actor.Inventory.Remove(item, command.Quantity), Money = checked(actor.Money + price) };
                market = market.Sell(item, command.Quantity); economy = economy.Export(item, command.Quantity, price); break;
            }
            case TownCommandKind.Produce: {
                TownRecipe recipe = TownContent.Recipe(command.Target!)!;
                int inputs = checked(recipe.InputCount * command.Quantity);
                changed = changed with {
                    Inventory = actor.Inventory.Remove(recipe.Input, inputs), NextActivityId = checked(actor.NextActivityId + 1),
                    Activity = new(actor.NextActivityId, TownActivityKind.Produce, new(checked(before.Time.Ticks + recipe.DurationTicks * command.Quantity)),
                        recipe.Id, command.Quantity, recipe.Input, inputs)
                };
                break;
            }
            case TownCommandKind.Eat:
                changed = changed with { Inventory = actor.Inventory.Remove("meal"), FoodReserve = Math.Min(100 * TownContent.Hour, actor.FoodReserve + TownContent.MealFood * TownContent.Hour) };
                economy = economy.Eat(); break;
            case TownCommandKind.Wait:
                changed = changed with { Activity = new(actor.NextActivityId, TownActivityKind.Wait, new(checked(before.Time.Ticks + command.Quantity * TownContent.Hour))), NextActivityId = checked(actor.NextActivityId + 1) }; break;
            case TownCommandKind.Go:
                spatialFacts = ((SpatialPlanAccepted)TownContent.Planner.TryStartTraversal(before.Spatial, new(actorId), new(command.Target!), 1, before.Time)).Facts; break;
            case TownCommandKind.Goal:
                changed = changed with { Goals = goal!.After }; break;
        }
        string result = command.Kind switch {
            TownCommandKind.Buy => $"Bought {command.Quantity} {command.Target} for {(long)TownContent.BuyPrice(command.Target!) * command.Quantity} money.",
            TownCommandKind.Sell => $"Sold {command.Quantity} {command.Target} for {(long)TownContent.SellPrice(command.Target!) * command.Quantity} money.",
            TownCommandKind.Produce => $"Started {command.Quantity} batch(es) of {command.Target}; reserved {changed.Activity!.InputCount} {changed.Activity.InputItem}; due {changed.Activity.Due}.",
            TownCommandKind.Eat => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Consumed one meal; food is now {changed.Food:0.###}/100."),
            TownCommandKind.Wait => $"Waiting {command.Quantity} hour(s), due {changed.Activity!.Due}.",
            TownCommandKind.Go => $"Started movement along {command.Target}.",
            TownCommandKind.Goal => goal!.Receipt.Message,
            _ => throw new InvalidOperationException("An accepted operation must have a known command kind.")
        };
        TownOperationReceipt? acceptedReceipt = source is null ? null : new(actorId, source, true, result, before.Time, actor.DecisionSequence, operation, goal?.Receipt);
        changed = changed with { LastOperationReceipt = acceptedReceipt ?? actor.LastOperationReceipt };
        GraphSpatialState spatial = ApplySpatial(before.Spatial, before.Time, spatialFacts);
        TownBusinessState next = before.Change(before.Characters.Select(character => character.Id == actorId ? changed : character), market, economy, spatial,
            lastServed: actorId);
        return new(TownEventKind.Operation, before, next, actorId, result, spatialFacts: spatialFacts, goalReceipt: goal?.Receipt,
            operationReceipt: acceptedReceipt);
    }

    internal static TownBusinessState Replace(TownBusinessState before, TownCharacter actor, string? openDecision = null) =>
        before.Change(before.Characters.Select(current => current.Id == actor.Id ? actor : current), openDecision: openDecision);

    internal static GraphSpatialState ApplySpatial(GraphSpatialState before, ModelTime time, IReadOnlyList<GraphSpatialFact> facts) {
        foreach (GraphSpatialFact fact in facts) { before = TownContent.Reducer.Apply(before, new(time, 0), fact); }
        return before;
    }

    internal static TownBusinessState At(TownBusinessState before, ModelTime time) {
        long elapsed = (time - before.Time).Ticks;
        if (elapsed < 0) { throw new InvalidOperationException("Town time cannot move backwards."); }
        if (elapsed == 0) { return before; }
        return before.Change(before.Characters.Select(actor => {
            long food = actor.Failed ? 0 : Math.Max(0, actor.FoodReserve - checked(elapsed * TownContent.FoodDecayPerHour));
            return actor with { FoodReserve = food, MinimumFoodReserve = Math.Min(actor.MinimumFoodReserve, food) };
        }), time: time);
    }
}

using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public sealed record TownVisibleResident(string Id, string Name, string Activity);
public sealed record TownVisibleRoute(string PassageId, string Destination, long DurationTicks);
public sealed record TownVisibleMarketItem(string ItemId, int BuyPrice, int SellPrice, int? AvailableStock, int? RemainingDemand);

/// <summary>One command family at this observation boundary. Quantified families accept every integer from 1 through MaximumQuantity.</summary>
/// <remarks>Example is a complete command. A zero maximum denotes a blocked family, with a concrete reason.
/// Eligibility does not reserve stock or guarantee that a later observation will have the same limits.</remarks>
public sealed record TownVisibleAction(string Syntax, string Example, string Description, int MaximumQuantity,
    string? BlockedReason, bool HasQuantity = false) {
    public bool Available => BlockedReason is null;
}

/// <summary>A frozen actor-visible value projection; it retains no world, callbacks or other residents' private state.</summary>
/// <remarks>Inventory and Goals are immutable snapshots. Market prices and refresh schedules are public;
/// live stock and demand are visible only at the market. Other residents are visible only at the same place.
/// This first slice is an observation, not a persistent interaction-history model.</remarks>
public sealed class TownObservation {
    public string ActorId { get; }
    public string Name { get; }
    public ModelTime Time { get; }
    public ModelTime EndTime { get; }
    public long DecisionSequence { get; }
    public long OperationSequence { get; }
    public bool IsDecisionOpen { get; }
    public bool WorldEnded { get; }
    public bool Failed { get; }
    public string? PlaceId { get; }
    public string? PassageId { get; }
    public string? Destination { get; }
    public ModelTime? ArrivalDue { get; }
    public long Money { get; }
    public long FoodReserve { get; }
    public long MinimumFoodReserve { get; }
    public ItemInventory Inventory { get; }
    public GoalSet Goals { get; }
    public TownActivity? Activity { get; }
    public TownOperationReceipt? LastOperationReceipt { get; }
    public TownExperience? LastExperience { get; }
    public IReadOnlyList<TownVisibleResident> Residents { get; }
    public IReadOnlyList<TownVisibleRoute> Routes { get; }
    public IReadOnlyList<TownRecipe> Recipes { get; }
    public IReadOnlyList<TownVisibleMarketItem> Market { get; }
    public ModelTime NextMarketRefresh { get; }
    public IReadOnlyList<TownVisibleAction> Actions { get; }

    private TownObservation(TownWorld world, string actorId) {
        TownBusinessState state = world.Business;
        TownCharacter actor = state.Character(actorId);
        ActorId = actor.Id; Name = actor.Name; Time = state.Time; EndTime = state.EndTime;
        DecisionSequence = actor.DecisionSequence; OperationSequence = actor.OperationSequence;
        IsDecisionOpen = state.OpenDecisionActorId == actorId; WorldEnded = state.Ended; Failed = actor.Failed;
        PlaceId = state.Place(actorId); Money = actor.Money; FoodReserve = actor.FoodReserve;
        MinimumFoodReserve = actor.MinimumFoodReserve; Inventory = actor.Inventory; Goals = actor.Goals; Activity = actor.Activity;
        LastOperationReceipt = actor.LastOperationReceipt; LastExperience = actor.LastExperience;
        if (state.Spatial.TryGetEntity(new(actorId), out SpatialEntity? entity) && entity!.Location is TraversingLocation travel) {
            PassageId = travel.PassageId.Value; Destination = travel.TargetPlaceId.Value; ArrivalDue = travel.ArrivalDue;
        }
        Residents = Array.AsReadOnly(PlaceId is null ? [] : state.Characters
            .Where(other => other.Id != actorId && !other.Failed && state.Place(other.Id) == PlaceId)
            .OrderBy(other => other.Id, StringComparer.Ordinal)
            .Select(other => new TownVisibleResident(other.Id, other.Name, other.Activity?.Kind switch {
                TownActivityKind.Produce => "producing", TownActivityKind.Wait => "waiting", _ => "available"
            })).ToArray());
        Routes = Array.AsReadOnly(TownContent.Graph.Passages
            .Where(route => PlaceId is not null && (route.EndpointA.Value == PlaceId || route.EndpointB.Value == PlaceId))
            .Select(route => new TownVisibleRoute(route.Id.Value,
                route.EndpointA.Value == PlaceId ? route.EndpointB.Value : route.EndpointA.Value, route.Length)).ToArray());
        Recipes = Array.AsReadOnly(TownContent.Recipes.ToArray());
        Market = Array.AsReadOnly(TownContent.Items.Select(item => new TownVisibleMarketItem(item,
            TownContent.BuyPrice(item), TownContent.SellPrice(item),
            PlaceId == "market" && TownContent.BuyPrice(item) > 0 ? state.Market.Stock.GetValueOrDefault(item) : null,
            PlaceId == "market" && TownContent.SellPrice(item) > 0 ? state.Market.Demand.GetValueOrDefault(item) : null)).ToArray());
        NextMarketRefresh = state.Market.NextRefresh;
        var actions = new List<TownVisibleAction>();
        Add("eat", "eat", $"Consume one meal; instant, +{TownContent.MealFood} food capped at 100.");
        Add("wait <hours>", "wait 1", "Wait for the specified whole hours; hunger continues.", true);
        foreach (TownVisibleRoute route in Routes) {
            Add($"go {route.PassageId}", $"go {route.PassageId}", $"Travel to {route.Destination}; duration shown in adjacent routes.");
        }
        foreach (TownRecipe recipe in Recipes) {
            Add($"produce {recipe.Id} <batches>", $"produce {recipe.Id} 1", "Start the entire batch at the workshop; ingredients leave inventory immediately.", true);
        }
        foreach (TownVisibleMarketItem item in Market) {
            if (item.BuyPrice > 0) { Add($"buy {item.ItemId} <count>", $"buy {item.ItemId} 1", $"Import at {item.BuyPrice} money per unit; instant at market.", true); }
            if (item.SellPrice > 0) { Add($"sell {item.ItemId} <count>", $"sell {item.ItemId} 1", $"Export at {item.SellPrice} money per unit; instant at market.", true); }
        }
        Actions = actions.AsReadOnly();

        void Add(string syntax, string example, string description, bool quantity = false) {
            TownCommand command = TownCommand.Parse(example);
            string? reason = TownPlanning.Check(state, actorId, command);
            int maximum = reason is null ? 1 : 0;
            if (reason is null && quantity) {
                // Current counted commands have monotone capacity checks: once a quantity is blocked,
                // every larger quantity is blocked too. Keep the authoritative rule as the validator.
                int low = 1, high = 1000;
                while (low < high) {
                    int middle = low + (high - low + 1) / 2;
                    if (TownPlanning.Check(state, actorId, command with { Quantity = middle }) is null) { low = middle; }
                    else { high = middle - 1; }
                }
                maximum = low;
            }
            actions.Add(new(syntax, example, description, maximum, reason, quantity));
        }
    }

    public static TownObservation Capture(TownWorld world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return new(world, actorId);
    }
}

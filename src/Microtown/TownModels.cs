using System.Collections.ObjectModel;
using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public enum TownActivityKind { Wait, Produce }
public sealed record TownActivity(long Id, TownActivityKind Kind, ModelTime Due, string? RecipeId = null,
    int Batches = 0, string? InputItem = null, int InputCount = 0);

/// <summary>FoodReserve uses unit-hours in millisecond ticks, so elapsed need loss is exact and independent of step frequency.</summary>
public sealed record TownCharacter(string Id, string Name, ItemInventory Inventory, long Money, long FoodReserve,
    long MinimumFoodReserve, GoalSet Goals, TownActivity? Activity = null, bool Failed = false,
    long DecisionSequence = 0, long OperationSequence = 0, long NextActivityId = 1) {
    public double Food => (double)FoodReserve / TownContent.Hour;
    public double MinimumFood => (double)MinimumFoodReserve / TownContent.Hour;
}

public sealed class TownMarket {
    public IReadOnlyDictionary<string, int> Stock { get; }
    public IReadOnlyDictionary<string, int> Demand { get; }
    public ModelTime NextRefresh { get; }
    public long Cycle { get; }
    public TownMarket(IReadOnlyDictionary<string, int> stock, IReadOnlyDictionary<string, int> demand, ModelTime nextRefresh, long cycle) {
        Stock = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(stock, StringComparer.Ordinal));
        Demand = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(demand, StringComparer.Ordinal));
        NextRefresh = nextRefresh; Cycle = cycle;
    }
    public static TownMarket Fresh(ModelTime nextRefresh, long cycle = 0) => new(
        new Dictionary<string, int> { ["grain"] = 18, ["wood"] = 18 },
        new Dictionary<string, int> { ["meal"] = 6, ["woodware"] = 9 }, nextRefresh, cycle);
    internal TownMarket Buy(string item, int quantity) {
        var stock = new Dictionary<string, int>(Stock, StringComparer.Ordinal) { [item] = checked(Stock[item] - quantity) };
        return new(stock, Demand, NextRefresh, Cycle);
    }
    internal TownMarket Sell(string item, int quantity) {
        var demand = new Dictionary<string, int>(Demand, StringComparer.Ordinal) { [item] = checked(Demand[item] - quantity) };
        return new(Stock, demand, NextRefresh, Cycle);
    }
}

/// <summary>Same-root cumulative economy authority. Moving ingredients into work-in-progress is not consumption.</summary>
public sealed class TownEconomicTotals {
    public long InitialMoney { get; }
    public long ImportExpense { get; }
    public long ExportIncome { get; }
    public IReadOnlyDictionary<string, long> InitialItems { get; }
    public IReadOnlyDictionary<string, long> Imported { get; }
    public IReadOnlyDictionary<string, long> Exported { get; }
    public IReadOnlyDictionary<string, long> Produced { get; }
    public IReadOnlyDictionary<string, long> Consumed { get; }
    public IReadOnlyDictionary<string, long> RecipeInputs { get; }
    public TownEconomicTotals(long initialMoney, IReadOnlyDictionary<string, long> initialItems,
        long importExpense = 0, long exportIncome = 0, IReadOnlyDictionary<string, long>? imported = null,
        IReadOnlyDictionary<string, long>? exported = null, IReadOnlyDictionary<string, long>? produced = null,
        IReadOnlyDictionary<string, long>? consumed = null, IReadOnlyDictionary<string, long>? recipeInputs = null) {
        InitialMoney = initialMoney; ImportExpense = importExpense; ExportIncome = exportIncome;
        InitialItems = Copy(initialItems); Imported = Copy(imported); Exported = Copy(exported);
        Produced = Copy(produced); Consumed = Copy(consumed); RecipeInputs = Copy(recipeInputs);
    }
    private static IReadOnlyDictionary<string, long> Copy(IReadOnlyDictionary<string, long>? source) =>
        new ReadOnlyDictionary<string, long>(source is null ? new Dictionary<string, long>(StringComparer.Ordinal) : new Dictionary<string, long>(source, StringComparer.Ordinal));
    private static IReadOnlyDictionary<string, long> Add(IReadOnlyDictionary<string, long> source, string item, int count) {
        var copy = new Dictionary<string, long>(source, StringComparer.Ordinal) { [item] = checked(source.GetValueOrDefault(item) + count) };
        return copy;
    }
    internal TownEconomicTotals Import(string item, int count, long price) => new(InitialMoney, InitialItems,
        checked(ImportExpense + price), ExportIncome, Add(Imported, item, count), Exported, Produced, Consumed, RecipeInputs);
    internal TownEconomicTotals Export(string item, int count, long price) => new(InitialMoney, InitialItems,
        ImportExpense, checked(ExportIncome + price), Imported, Add(Exported, item, count), Produced, Consumed, RecipeInputs);
    internal TownEconomicTotals Eat() => new(InitialMoney, InitialItems, ImportExpense, ExportIncome,
        Imported, Exported, Produced, Add(Consumed, "meal", 1), RecipeInputs);
    internal TownEconomicTotals Produce(TownRecipe recipe, int batches) => new(InitialMoney, InitialItems, ImportExpense, ExportIncome,
        Imported, Exported, Add(Produced, recipe.Output, checked(recipe.OutputCount * batches)), Consumed,
        Add(RecipeInputs, recipe.Input, checked(recipe.InputCount * batches)));
    public void Verify(TownBusinessState business) {
        if (InitialMoney < 0 || ImportExpense < 0 || ExportIncome < 0 ||
            new[] { InitialItems, Imported, Exported, Produced, Consumed, RecipeInputs }
                .Any(table => table.Any(pair => pair.Value < 0 || !TownContent.Items.Contains(pair.Key)))) {
            throw new InvalidOperationException("Invalid economy counters.");
        }
        if (business.Characters.Sum(actor => actor.Money) != checked(InitialMoney + ExportIncome - ImportExpense)) {
            throw new InvalidOperationException("Town money does not reconcile with external flows.");
        }
        foreach (string item in TownContent.Items) {
            long held = business.Characters.Sum(actor => (long)actor.Inventory.Count(item) +
                (actor.Activity is { InputItem: var input, InputCount: var count } && input == item ? count : 0));
            long expected = checked(InitialItems.GetValueOrDefault(item) + Imported.GetValueOrDefault(item) + Produced.GetValueOrDefault(item)
                - Exported.GetValueOrDefault(item) - Consumed.GetValueOrDefault(item) - RecipeInputs.GetValueOrDefault(item));
            if (held != expected) { throw new InvalidOperationException($"Town item '{item}' does not reconcile: {held} != {expected}."); }
        }
    }
}

/// <summary>An immutable application root. Kernel time is copied here only at authoritative fact application.</summary>
public sealed class TownBusinessState {
    public ModelTime Time { get; }
    public ModelTime EndTime { get; }
    public IReadOnlyList<TownCharacter> Characters { get; }
    public TownMarket Market { get; }
    public TownEconomicTotals Economy { get; }
    public GraphSpatialState Spatial { get; }
    public string? LastServedActorId { get; }
    public string? OpenDecisionActorId { get; }
    public long Generation { get; }
    public bool Ended { get; }
    public TownBusinessState(ModelTime time, ModelTime endTime, IEnumerable<TownCharacter> characters,
        TownMarket market, TownEconomicTotals economy, GraphSpatialState spatial, string? lastServedActorId = null,
        string? openDecisionActorId = null, long generation = 0, bool ended = false) {
        Time = time; EndTime = endTime; Characters = Array.AsReadOnly(characters.ToArray()); Market = market;
        Economy = economy; Spatial = spatial; LastServedActorId = lastServedActorId;
        OpenDecisionActorId = openDecisionActorId; Generation = generation; Ended = ended;
    }
    public TownCharacter Character(string id) => Characters.Single(actor => actor.Id == id);
    public string? Place(string id) => Spatial.TryGetEntity(new(id), out SpatialEntity? entity) && entity!.Location is AtPlaceLocation at ? at.PlaceId.Value : null;
    public TownCharacter WorstOff => Characters.OrderBy(actor => actor.FoodReserve).ThenBy(actor => actor.Id, StringComparer.Ordinal).First();
    internal TownBusinessState Change(IEnumerable<TownCharacter>? characters = null, TownMarket? market = null,
        TownEconomicTotals? economy = null, GraphSpatialState? spatial = null, ModelTime? time = null,
        string? lastServed = null, string? openDecision = null, bool ended = false) => new(time ?? Time, EndTime,
        characters ?? Characters, market ?? Market, economy ?? Economy, spatial ?? Spatial,
        lastServed ?? LastServedActorId, openDecision, checked(Generation + 1), ended || Ended);
    public void Validate() {
        if (Characters.Count != 3 || Characters.Select(actor => actor.Id).Distinct(StringComparer.Ordinal).Count() != Characters.Count ||
            Time < ModelTime.Zero || EndTime < Time || Generation < 0 || Market.NextRefresh < Time || Market.Cycle < 0 ||
            !Market.Stock.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "grain", "wood" }) ||
            !Market.Demand.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "meal", "woodware" }) ||
            Market.Stock.Any(pair => TownContent.BuyPrice(pair.Key) <= 0 || pair.Value < 0) ||
            Market.Demand.Any(pair => TownContent.SellPrice(pair.Key) <= 0 || pair.Value < 0) ||
            LastServedActorId is { } served && !Characters.Any(actor => actor.Id == served) ||
            OpenDecisionActorId is { } deciding && !Characters.Any(actor => actor.Id == deciding)) {
            throw new InvalidOperationException("Invalid town boundary.");
        }
        foreach (TownCharacter actor in Characters) {
            actor.Inventory.Validate(); actor.Goals.Validate();
            if (string.IsNullOrWhiteSpace(actor.Id) || string.IsNullOrWhiteSpace(actor.Name) || actor.Money < 0 || actor.FoodReserve < 0 || actor.FoodReserve > 100 * TownContent.Hour ||
                actor.MinimumFoodReserve < 0 || actor.MinimumFoodReserve > actor.FoodReserve ||
                actor.DecisionSequence < 0 || actor.OperationSequence < 0 || actor.NextActivityId <= 0 ||
                actor.Inventory.Items.Keys.Any(item => !TownContent.Items.Contains(item)) ||
                actor.Failed && (actor.FoodReserve != 0 || actor.Activity is not null) || actor.Activity?.Due < Time) {
                throw new InvalidOperationException("Invalid town character.");
            }
            bool placed = Spatial.TryGetEntity(new(actor.Id), out _);
            if (placed == actor.Failed) { throw new InvalidOperationException("Active residents require a spatial entity; failed residents must have none."); }
            if (actor.Activity is { } activity) {
                if (activity.Id <= 0 || activity.Id >= actor.NextActivityId || !Enum.IsDefined(activity.Kind) || Place(actor.Id) is null ||
                    activity.Kind == TownActivityKind.Wait && (activity.RecipeId is not null || activity.Batches != 0 || activity.InputItem is not null || activity.InputCount != 0)) {
                    throw new InvalidOperationException("Invalid resident activity.");
                }
                if (activity.Kind == TownActivityKind.Produce) {
                    TownRecipe? recipe = TownContent.Recipe(activity.RecipeId ?? "");
                    if (recipe is null || activity.Batches is < 1 or > 1000 || activity.InputItem != recipe.Input ||
                        activity.InputCount != (long)recipe.InputCount * activity.Batches || Place(actor.Id) != "workshop") {
                        throw new InvalidOperationException("Production must retain the recipe's exact work-in-progress ingredients.");
                    }
                }
            }
        }
        if (Spatial.Entities.Count != Characters.Count(actor => !actor.Failed)) { throw new InvalidOperationException("Unexpected town spatial entity."); }
        if (OpenDecisionActorId is { } id && (Character(id).Failed || Character(id).Activity is not null || Place(id) is null)) {
            throw new InvalidOperationException("A decision requires an available resident.");
        }
        Economy.Verify(this);
    }
}

public sealed record TownWorld(TownBusinessState Business, SpatialSimulationState Spatial) {
    public ModelTime Time => Business.Time;
}

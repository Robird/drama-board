using System.Collections.ObjectModel;
using Atelia.DurableGraph;
using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

[DurableType("DramaBoard.Microtown.ActivityKind", 1)]
public enum TownActivityKind { Wait, Produce }
[DurableType("DramaBoard.Microtown.Activity", 1)]
public sealed partial record TownActivity(
    [field: DurableField(1)] long Id,
    [field: DurableField(2)] TownActivityKind Kind,
    [field: DurableField(3)] ModelTime Due,
    [field: DurableField(4)] string? RecipeId = null,
    [field: DurableField(5)] int Batches = 0,
    [field: DurableField(6)] string? InputItem = null,
    [field: DurableField(7)] int InputCount = 0) : IDurableObject;

/// <summary>FoodReserve uses unit-hours in millisecond ticks, so elapsed need loss is exact and independent of step frequency.</summary>
[DurableType("DramaBoard.Microtown.Character", 1)]
public sealed partial record TownCharacter(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Name,
    [field: DurableField(3)] ItemInventory Inventory,
    [field: DurableField(4)] long Money,
    [field: DurableField(5)] long FoodReserve,
    [field: DurableField(6)] long MinimumFoodReserve,
    [field: DurableField(7)] GoalSet Goals,
    [field: DurableField(8)] TownActivity? Activity = null,
    [field: DurableField(9)] bool Failed = false,
    [field: DurableField(10)] long DecisionSequence = 0,
    [field: DurableField(11)] long OperationSequence = 0,
    [field: DurableField(12)] long NextActivityId = 1,
    [field: DurableField(13)] TownOperationReceipt? LastOperationReceipt = null,
    [field: DurableField(14)] TownExperience? LastExperience = null) : IDurableObject {
    public double Food => (double)FoodReserve / TownContent.Hour;
    public double MinimumFood => (double)MinimumFoodReserve / TownContent.Hour;
}

[DurableType("DramaBoard.Microtown.Market", 1)]
public sealed partial class TownMarket : IDurableObject {
    [DurableField(1)] private Dictionary<string, int> _stock;
    public IReadOnlyDictionary<string, int> Stock => new ReadOnlyDictionary<string, int>(_stock);
    [DurableField(2)] private Dictionary<string, int> _demand;
    public IReadOnlyDictionary<string, int> Demand => new ReadOnlyDictionary<string, int>(_demand);
    [DurableField(3)] private readonly ModelTime _nextRefresh;
    public ModelTime NextRefresh => _nextRefresh;
    [DurableField(4)] private readonly long _cycle;
    public long Cycle => _cycle;
    public TownMarket(IReadOnlyDictionary<string, int> stock, IReadOnlyDictionary<string, int> demand, ModelTime nextRefresh, long cycle) {
        _stock = new Dictionary<string, int>(stock, StringComparer.Ordinal);
        _demand = new Dictionary<string, int>(demand, StringComparer.Ordinal);
        _nextRefresh = nextRefresh; _cycle = cycle;
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
[DurableType("DramaBoard.Microtown.EconomicTotals", 1)]
public sealed partial class TownEconomicTotals : IDurableObject {
    [DurableField(1)] private readonly long _initialMoney;
    public long InitialMoney => _initialMoney;
    [DurableField(2)] private readonly long _importExpense;
    public long ImportExpense => _importExpense;
    [DurableField(3)] private readonly long _exportIncome;
    public long ExportIncome => _exportIncome;
    [DurableField(4)] private Dictionary<string, long> _initialItems;
    public IReadOnlyDictionary<string, long> InitialItems => new ReadOnlyDictionary<string, long>(_initialItems);
    [DurableField(5)] private Dictionary<string, long> _imported;
    public IReadOnlyDictionary<string, long> Imported => new ReadOnlyDictionary<string, long>(_imported);
    [DurableField(6)] private Dictionary<string, long> _exported;
    public IReadOnlyDictionary<string, long> Exported => new ReadOnlyDictionary<string, long>(_exported);
    [DurableField(7)] private Dictionary<string, long> _produced;
    public IReadOnlyDictionary<string, long> Produced => new ReadOnlyDictionary<string, long>(_produced);
    [DurableField(8)] private Dictionary<string, long> _consumed;
    public IReadOnlyDictionary<string, long> Consumed => new ReadOnlyDictionary<string, long>(_consumed);
    [DurableField(9)] private Dictionary<string, long> _recipeInputs;
    public IReadOnlyDictionary<string, long> RecipeInputs => new ReadOnlyDictionary<string, long>(_recipeInputs);
    public TownEconomicTotals(long initialMoney, IReadOnlyDictionary<string, long> initialItems,
        long importExpense = 0, long exportIncome = 0, IReadOnlyDictionary<string, long>? imported = null,
        IReadOnlyDictionary<string, long>? exported = null, IReadOnlyDictionary<string, long>? produced = null,
        IReadOnlyDictionary<string, long>? consumed = null, IReadOnlyDictionary<string, long>? recipeInputs = null) {
        _initialMoney = initialMoney; _importExpense = importExpense; _exportIncome = exportIncome;
        _initialItems = Copy(initialItems); _imported = Copy(imported); _exported = Copy(exported);
        _produced = Copy(produced); _consumed = Copy(consumed); _recipeInputs = Copy(recipeInputs);
    }
    private static Dictionary<string, long> Copy(IReadOnlyDictionary<string, long>? source) =>
        source is null ? new(StringComparer.Ordinal) : new(source, StringComparer.Ordinal);
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
[DurableType("DramaBoard.Microtown.BusinessState", 1)]
public sealed partial class TownBusinessState : IDurableObject {
    [DurableField(1)] private readonly ModelTime _time;
    public ModelTime Time => _time;
    [DurableField(2)] private readonly ModelTime _endTime;
    public ModelTime EndTime => _endTime;
    [DurableField(3)] private List<TownCharacter> _characters;
    public IReadOnlyList<TownCharacter> Characters => _characters.AsReadOnly();
    [DurableField(4)] private readonly TownMarket _market;
    public TownMarket Market => _market;
    [DurableField(5)] private readonly TownEconomicTotals _economy;
    public TownEconomicTotals Economy => _economy;
    [DurableField(6)] private readonly GraphSpatialState _spatial;
    public GraphSpatialState Spatial => _spatial;
    [DurableField(7)] private readonly string? _lastServedActorId;
    public string? LastServedActorId => _lastServedActorId;
    [DurableField(8)] private readonly string? _openDecisionActorId;
    public string? OpenDecisionActorId => _openDecisionActorId;
    [DurableField(9)] private readonly long _generation;
    public long Generation => _generation;
    [DurableField(10)] private readonly bool _ended;
    public bool Ended => _ended;
    public TownBusinessState(ModelTime time, ModelTime endTime, IEnumerable<TownCharacter> characters,
        TownMarket market, TownEconomicTotals economy, GraphSpatialState spatial, string? lastServedActorId = null,
        string? openDecisionActorId = null, long generation = 0, bool ended = false) {
        _time = time; _endTime = endTime; _characters = characters.ToList(); _market = market;
        _economy = economy; _spatial = spatial; _lastServedActorId = lastServedActorId;
        _openDecisionActorId = openDecisionActorId; _generation = generation; _ended = ended;
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
            actor.Inventory.Validate(); actor.Goals.ValidateThrough(actor.DecisionSequence, actor.OperationSequence);
            actor.LastOperationReceipt?.Validate();
            actor.LastExperience?.Validate();
            if (actor.LastOperationReceipt is { } receipt && (receipt.ActorId != actor.Id || receipt.At > Time ||
                receipt.DecisionSequence > actor.DecisionSequence || receipt.OperationSequence > actor.OperationSequence) ||
                actor.LastExperience is { } experience && experience.At > Time) {
                throw new InvalidOperationException("Character records exceed their publication boundary.");
            }
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

using System.Collections.ObjectModel;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Market quotas and conservation counters share the world's commit boundary.</summary>
[DurableType("DramaBoard.ResourceCycle.EconomyState", 1)]
public sealed partial class CycleEconomyState : IDurableObject {
    [DurableField(1)] private readonly CycleMarket _market;
    [DurableField(2)] private readonly CycleEconomicTotals _totals;
    public CycleMarket Market => _market;
    public CycleEconomicTotals Totals => _totals;
    public CycleEconomyState(CycleMarket market, CycleEconomicTotals totals) {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _totals = totals ?? throw new ArgumentNullException(nameof(totals));
    }
    public CycleEconomyState Change(CycleMarket? market = null, CycleEconomicTotals? totals = null) =>
        new(market ?? Market, totals ?? Totals);
    public void Validate(CycleBusinessState business) {
        if (Market is null || Totals is null || Market.NextRefresh < business.Time) {
            throw new InvalidOperationException("Invalid economy boundary.");
        }
        Market.Validate();
        Totals.Verify(business);
    }
    public bool SameState(CycleEconomyState? other) => other is not null && Market.SameState(other.Market) && Totals.SameState(other.Totals);
    public override string ToString() => System.Text.Json.JsonSerializer.Serialize(this);
}

[DurableType("DramaBoard.ResourceCycle.Market", 1)]
public sealed partial class CycleMarket : IDurableObject {
    [DurableField(1)] private Dictionary<string, int> _stock;
    [DurableField(2)] private Dictionary<string, int> _demand;
    [DurableField(3)] private readonly ModelTime _nextRefresh;
    [DurableField(4)] private readonly long _cycle;
    public IReadOnlyDictionary<string, int> Stock => new ReadOnlyDictionary<string, int>(_stock);
    public IReadOnlyDictionary<string, int> Demand => new ReadOnlyDictionary<string, int>(_demand);
    public ModelTime NextRefresh => _nextRefresh;
    public long Cycle => _cycle;
    public CycleMarket(IReadOnlyDictionary<string, int> stock, IReadOnlyDictionary<string, int> demand, ModelTime nextRefresh, long cycle) {
        _stock = new(stock, StringComparer.Ordinal);
        _demand = new(demand, StringComparer.Ordinal);
        _nextRefresh = nextRefresh; _cycle = cycle;
        Validate();
    }
    public static CycleMarket Fresh(ModelTime nextRefresh, long cycle = 0) =>
        new(LivingWorldEconomyContent.MarketStock, LivingWorldEconomyContent.MarketDemand, nextRefresh, cycle);
    public CycleMarket Buy(string item, int quantity) {
        if (quantity <= 0 || !Stock.TryGetValue(item, out int available) || quantity > available) {
            throw new InvalidOperationException("Purchase exceeds the market supply.");
        }
        var stock = new Dictionary<string, int>(Stock, StringComparer.Ordinal) { [item] = available - quantity };
        return new(stock, Demand, NextRefresh, Cycle);
    }
    public CycleMarket Sell(string item, int quantity) {
        if (quantity <= 0 || !Demand.TryGetValue(item, out int available) || quantity > available) {
            throw new InvalidOperationException("Sale exceeds the market demand.");
        }
        var demand = new Dictionary<string, int>(Demand, StringComparer.Ordinal) { [item] = available - quantity };
        return new(Stock, demand, NextRefresh, Cycle);
    }
    public void Validate() {
        if (NextRefresh < ModelTime.Zero || Cycle < 0 ||
            !ValidQuotas(Stock, LivingWorldEconomyContent.MarketStock) || !ValidQuotas(Demand, LivingWorldEconomyContent.MarketDemand)) {
            throw new InvalidOperationException("Invalid market quotas or refresh boundary.");
        }
    }
    private static bool ValidQuotas(IReadOnlyDictionary<string, int> actual, IReadOnlyDictionary<string, int> limits) =>
        actual.Count == limits.Count && actual.All(pair => limits.TryGetValue(pair.Key, out int limit) && pair.Value >= 0 && pair.Value <= limit);
    public bool SameState(CycleMarket? other) => other is not null && NextRefresh == other.NextRefresh && Cycle == other.Cycle &&
        Stock.Count == other.Stock.Count && Stock.All(pair => other.Stock.TryGetValue(pair.Key, out int value) && value == pair.Value) &&
        Demand.Count == other.Demand.Count && Demand.All(pair => other.Demand.TryGetValue(pair.Key, out int value) && value == pair.Value);
}

/// <summary>External flows reconcile all holdings, including dead residents and production escrow.</summary>
[DurableType("DramaBoard.ResourceCycle.EconomicTotals", 1)]
public sealed partial class CycleEconomicTotals : IDurableObject {
    [DurableField(1)] private readonly long _initialMoney;
    [DurableField(2)] private readonly long _importExpense;
    [DurableField(3)] private readonly long _exportIncome;
    [DurableField(4)] private Dictionary<string, long> _initialItems;
    [DurableField(5)] private Dictionary<string, long> _imported;
    [DurableField(6)] private Dictionary<string, long> _harvested;
    [DurableField(7)] private Dictionary<string, long> _exported;
    [DurableField(8)] private Dictionary<string, long> _produced;
    [DurableField(9)] private Dictionary<string, long> _consumed;
    [DurableField(10)] private Dictionary<string, long> _recipeInputs;
    public long InitialMoney => _initialMoney;
    public long ImportExpense => _importExpense;
    public long ExportIncome => _exportIncome;
    public IReadOnlyDictionary<string, long> InitialItems => new ReadOnlyDictionary<string, long>(_initialItems);
    public IReadOnlyDictionary<string, long> Imported => new ReadOnlyDictionary<string, long>(_imported);
    public IReadOnlyDictionary<string, long> Harvested => new ReadOnlyDictionary<string, long>(_harvested);
    public IReadOnlyDictionary<string, long> Exported => new ReadOnlyDictionary<string, long>(_exported);
    public IReadOnlyDictionary<string, long> Produced => new ReadOnlyDictionary<string, long>(_produced);
    public IReadOnlyDictionary<string, long> Consumed => new ReadOnlyDictionary<string, long>(_consumed);
    public IReadOnlyDictionary<string, long> RecipeInputs => new ReadOnlyDictionary<string, long>(_recipeInputs);
    public CycleEconomicTotals(long initialMoney, IReadOnlyDictionary<string, long> initialItems,
        long importExpense = 0, long exportIncome = 0, IReadOnlyDictionary<string, long>? imported = null,
        IReadOnlyDictionary<string, long>? exported = null, IReadOnlyDictionary<string, long>? produced = null,
        IReadOnlyDictionary<string, long>? consumed = null, IReadOnlyDictionary<string, long>? recipeInputs = null,
        IReadOnlyDictionary<string, long>? harvested = null) {
        ArgumentNullException.ThrowIfNull(initialItems);
        _initialMoney = initialMoney; _importExpense = importExpense; _exportIncome = exportIncome;
        _initialItems = Copy(initialItems); _imported = Copy(imported); _exported = Copy(exported);
        _produced = Copy(produced); _consumed = Copy(consumed); _recipeInputs = Copy(recipeInputs); _harvested = Copy(harvested);
        Validate();
    }
    private static Dictionary<string, long> Copy(IReadOnlyDictionary<string, long>? source) =>
        source is null ? new(StringComparer.Ordinal) : new(source, StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, long> Add(IReadOnlyDictionary<string, long> source, string item, int count) {
        if (count <= 0 || !LivingWorldEconomyContent.Items.ContainsKey(item)) {
            throw new ArgumentOutOfRangeException(nameof(count), "An economic flow requires a known item and positive count.");
        }
        return new Dictionary<string, long>(source, StringComparer.Ordinal) { [item] = checked(source.GetValueOrDefault(item) + count) };
    }
    /// <param name="price">Total payment for the entire purchase, not the unit price.</param>
    public CycleEconomicTotals Import(string item, int count, long price) {
        ArgumentOutOfRangeException.ThrowIfNegative(price);
        return new(InitialMoney, InitialItems, checked(ImportExpense + price), ExportIncome,
            Add(Imported, item, count), Exported, Produced, Consumed, RecipeInputs, Harvested);
    }
    /// <param name="price">Total payment for the entire sale, not the unit price.</param>
    public CycleEconomicTotals Export(string item, int count, long price) {
        ArgumentOutOfRangeException.ThrowIfNegative(price);
        return new(InitialMoney, InitialItems, ImportExpense, checked(ExportIncome + price),
            Imported, Add(Exported, item, count), Produced, Consumed, RecipeInputs, Harvested);
    }
    public CycleEconomicTotals Harvest(string item, int count) => new(InitialMoney, InitialItems, ImportExpense, ExportIncome,
        Imported, Exported, Produced, Consumed, RecipeInputs, Add(Harvested, item, count));
    public CycleEconomicTotals Consume(string item, int count) => new(InitialMoney, InitialItems, ImportExpense, ExportIncome,
        Imported, Exported, Produced, Add(Consumed, item, count), RecipeInputs, Harvested);
    public CycleEconomicTotals Produce(CycleRecipe recipe, int batches) {
        ArgumentNullException.ThrowIfNull(recipe);
        if (batches <= 0 || recipe != LivingWorldEconomyContent.Recipe(recipe.Id)) {
            throw new ArgumentOutOfRangeException(nameof(batches), "Production requires a known recipe and positive batches.");
        }
        return new(InitialMoney, InitialItems, ImportExpense, ExportIncome, Imported, Exported,
            Add(Produced, recipe.OutputItem, checked(recipe.OutputCount * batches)), Consumed,
            Add(RecipeInputs, recipe.InputItem, checked(recipe.InputCount * batches)), Harvested);
    }
    public void Validate() {
        if (InitialMoney < 0 || ImportExpense < 0 || ExportIncome < 0 ||
            Tables().Any(table => table.Any(pair => pair.Value < 0 || !LivingWorldEconomyContent.Items.ContainsKey(pair.Key)))) {
            throw new InvalidOperationException("Invalid economy counters.");
        }
    }
    public void Verify(CycleBusinessState business) {
        Validate();
        if (business.Characters.Any(actor => actor.Money < 0 || actor.Backpack.Items.Keys.Any(item => !LivingWorldEconomyContent.Items.ContainsKey(item))) ||
            business.Characters.Sum(actor => actor.Money) != checked(InitialMoney + ExportIncome - ImportExpense)) {
            throw new InvalidOperationException("World money or item identities do not reconcile with economic flows.");
        }
        foreach (string item in LivingWorldEconomyContent.Items.Keys) {
            long held = business.Characters.Sum(actor => (long)actor.Backpack.Count(item) +
                (actor.Activity?.Production is { } order && order.InputItem == item ? order.InputCount : 0));
            long expected = checked(InitialItems.GetValueOrDefault(item) + Imported.GetValueOrDefault(item) + Harvested.GetValueOrDefault(item) + Produced.GetValueOrDefault(item)
                - Exported.GetValueOrDefault(item) - Consumed.GetValueOrDefault(item) - RecipeInputs.GetValueOrDefault(item));
            if (held != expected) { throw new InvalidOperationException($"World item '{item}' does not reconcile: {held} != {expected}."); }
        }
    }
    private IReadOnlyDictionary<string, long>[] Tables() => [InitialItems, Imported, Harvested, Exported, Produced, Consumed, RecipeInputs];
    public bool SameState(CycleEconomicTotals? other) => other is not null && InitialMoney == other.InitialMoney &&
        ImportExpense == other.ImportExpense && ExportIncome == other.ExportIncome && Tables().Zip(other.Tables()).All(pair =>
            pair.First.Count == pair.Second.Count && pair.First.All(entry => pair.Second.TryGetValue(entry.Key, out long value) && value == entry.Value));
}

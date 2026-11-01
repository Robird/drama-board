using Atelia.DurableGraph;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.MarketQuote", 1)]
public sealed partial record CycleMarketQuote(
    [field: DurableField(1)] string ItemId,
    [field: DurableField(2)] int BuyPrice,
    [field: DurableField(3)] int SellPrice,
    [field: DurableField(4)] int Stock,
    [field: DurableField(5)] int Demand) : IDurableObject;

/// <summary>Local public market data; no remote state or other character's purse is exposed.</summary>
internal static class CycleEconomyObservation {
    internal static IReadOnlyList<CycleMarketQuote> Quotes(CycleBusinessState state, string? place) {
        if (state.Economy is not { } economy || place != "market") { return []; }
        return Array.AsReadOnly(economy.Market.Stock.Keys.Concat(economy.Market.Demand.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(item => new CycleMarketQuote(item, LivingWorldEconomyContent.BuyPrice(item),
                LivingWorldEconomyContent.SellPrice(item), economy.Market.Stock.GetValueOrDefault(item),
                economy.Market.Demand.GetValueOrDefault(item))).ToArray());
    }
    internal static IReadOnlyList<CycleRecipe> Recipes(CycleBusinessState state, string? place) =>
        state.Economy is not null && place == "workshop" ? LivingWorldEconomyContent.Recipes : [];
    internal static string ItemName(string item) => LivingWorldEconomyContent.Items.GetValueOrDefault(item, item);
}

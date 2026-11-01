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
    // Delivery cursors belong to the host ledger, not either character's knowledge.
    internal static CycleTradeOffer VisibleTrade(CycleTradeOffer offer) => offer with {
        ProposerDeliveredVersion = 0, RecipientDeliveredVersion = 0,
        ProposerDeliveryContextSequence = 0, RecipientDeliveryContextSequence = 0
    };
    internal static IReadOnlyList<CycleTradeOffer> Trades(CycleBusinessState state, string actorId) =>
        Array.AsReadOnly(state.Economy?.Trades.Offers.Where(offer => offer.Involves(actorId)).Select(VisibleTrade).ToArray() ?? []);
    internal static void ValidateVisibleTrade(CycleTradeOffer offer, string actorId, DramaBoard.Kernel.Time.ModelTime at) {
        offer.Validate(at);
        if (!offer.Involves(actorId) || offer.ProposerDeliveredVersion != 0 || offer.RecipientDeliveredVersion != 0 ||
            offer.ProposerDeliveryContextSequence != 0 || offer.RecipientDeliveryContextSequence != 0) {
            throw new InvalidOperationException("A personal trade view must exclude unrelated offers and internal delivery cursors.");
        }
    }
    internal static string ItemName(string item) => LivingWorldEconomyContent.Items.GetValueOrDefault(item, item);
}

using Atelia.DurableGraph;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.MarketQuote", 1)]
public sealed partial record PlaygroundMarketQuote(
    [field: DurableField(1)] string ItemId,
    [field: DurableField(2)] int BuyPrice,
    [field: DurableField(3)] int SellPrice,
    [field: DurableField(4)] int Stock,
    [field: DurableField(5)] int Demand) : IDurableObject;

/// <summary>Local public market data; no remote state or other character's purse is exposed.</summary>
internal static class PlaygroundEconomyObservation {
    internal static IReadOnlyList<PlaygroundMarketQuote> Quotes(PlaygroundBusinessState state, string? place) {
        if (state.Economy is not { } economy || place != state.Definition.MarketPlaceId) { return []; }
        return Array.AsReadOnly(economy.Market.Stock.Keys.Concat(economy.Market.Demand.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(item => new PlaygroundMarketQuote(item, state.Definition.BuyPrice(item),
                state.Definition.SellPrice(item), economy.Market.Stock.GetValueOrDefault(item),
                economy.Market.Demand.GetValueOrDefault(item))).ToArray());
    }
    internal static IReadOnlyList<PlaygroundRecipe> Recipes(PlaygroundBusinessState state, string? place) =>
        state.Economy is not null && place == state.Definition.WorkshopPlaceId ? state.Definition.Recipes : [];
    // Delivery cursors belong to the host ledger, not either character's knowledge.
    internal static PlaygroundTradeOffer VisibleTrade(PlaygroundTradeOffer offer) => offer with {
        ProposerDeliveredVersion = 0, RecipientDeliveredVersion = 0,
        ProposerDeliveryContextSequence = 0, RecipientDeliveryContextSequence = 0
    };
    internal static IReadOnlyList<PlaygroundTradeOffer> Trades(PlaygroundBusinessState state, string actorId) =>
        Array.AsReadOnly(state.Economy?.Trades.Offers.Where(offer => offer.Involves(actorId)).Select(VisibleTrade).ToArray() ?? []);
    internal static void ValidateVisibleTrade(PlaygroundTradeOffer offer, string actorId, DramaBoard.Kernel.Time.ModelTime at) {
        offer.Validate(at);
        if (!offer.Involves(actorId) || offer.ProposerDeliveredVersion != 0 || offer.RecipientDeliveredVersion != 0 ||
            offer.ProposerDeliveryContextSequence != 0 || offer.RecipientDeliveryContextSequence != 0) {
            throw new InvalidOperationException("A personal trade view must exclude unrelated offers and internal delivery cursors.");
        }
    }
    internal static string ItemName(PlaygroundWorldDefinition definition, string item) => definition.Items.GetValueOrDefault(item, item);
}

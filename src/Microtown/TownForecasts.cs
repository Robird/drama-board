using System.Collections.ObjectModel;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown;

/// <summary>Immutable, runtime-only town owner outputs. Spatial owns its separate arrival/contact projections.</summary>
public sealed class TownForecasts {
    internal TownBusinessState SourceBusiness { get; }
    public IReadOnlyDictionary<string, TownPrediction> Entries { get; }
    /// <summary>Owners evaluated by this preparation only, for diagnostics; this is not a cumulative event log.</summary>
    public IReadOnlyList<string> LastRecomputedOwners { get; }

    internal TownForecasts(TownBusinessState source, IDictionary<string, TownPrediction> entries, IEnumerable<string> recomputed) {
        SourceBusiness = source;
        Entries = new ReadOnlyDictionary<string, TownPrediction>(new Dictionary<string, TownPrediction>(entries, StringComparer.Ordinal));
        LastRecomputedOwners = Array.AsReadOnly(recomputed.ToArray());
    }

    internal void Validate(TownBusinessState business) {
        // The stamp rejects a Fold-only or manually substituted business root. Only Rebuild/Complete
        // can create a prepared cache; validation never hides a full prediction recalculation.
        if (!ReferenceEquals(SourceBusiness, business) || business.Ended && Entries.Count != 0 ||
            Entries.Any(entry => string.IsNullOrEmpty(entry.Key) || entry.Value.Due < business.Time ||
                entry.Value.Kind == TownPredictionKind.Spatial || !Enum.IsDefined(entry.Value.Kind))) {
            throw new InvalidOperationException("Town forecasts do not belong to this completed business boundary.");
        }
    }
}

/// <summary>Checks semantic owner dependencies, then predicts only affected town owners against the final state.</summary>
internal static class TownPredictionMaintenance {
    internal static TownForecasts Rebuild(TownBusinessState business) {
        var entries = new Dictionary<string, TownPrediction>(StringComparer.Ordinal);
        if (!business.Ended) {
            entries.Add("end", new(TownPredictionKind.End, business.EndTime));
            entries.Add("market", new(TownPredictionKind.MarketRefresh, business.Market.NextRefresh));
            foreach (TownCharacter actor in business.Characters.Where(actor => !actor.Failed)) {
                entries.Add(FailureOwner(actor.Id), Failure(business, actor));
                if (actor.Activity is { } activity) { entries.Add(ActivityOwner(actor.Id), new(TownPredictionKind.Activity, activity.Due, actor.Id)); }
            }
            foreach (TownTradeOffer offer in business.Trades.Offers.Where(offer => offer.Status == TownTradeStatus.Active)) {
                entries.Add(TradeOwner(offer.Id), new(TownPredictionKind.TradeExpiry, offer.ExpiresAt, TradeId: offer.Id));
            }
            if (TownEngine.NextDecisionActor(business) is { } actorId) { entries.Add("decision", new(TownPredictionKind.Decision, business.Time, actorId)); }
        }
        return new(business, entries, entries.Keys);
    }

    internal static TownForecasts Complete(TownBusinessState before, TownBusinessState after, TownForecasts previous) {
        previous.Validate(before);
        var entries = previous.Entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var recomputed = new List<string>();
        void Set(string owner, TownPrediction? next) {
            recomputed.Add(owner);
            if (next is null) { entries.Remove(owner); }
            else if (!entries.TryGetValue(owner, out TownPrediction? old) || old != next) { entries[owner] = next; }
        }
        if (after.Ended) { return new(after, new Dictionary<string, TownPrediction>(), entries.Keys); }
        if (before.Ended) { throw new InvalidOperationException("A completed town cannot resume without rebuilding its business state."); }
        long elapsed = checked(after.Time.Ticks - before.Time.Ticks);
        if (elapsed < 0) { throw new InvalidOperationException("Town forecast preparation cannot move backwards."); }
        long decay = checked(elapsed * TownContent.FoodDecayPerHour);
        var previousActors = before.Characters.ToDictionary(actor => actor.Id, StringComparer.Ordinal);
        foreach (TownCharacter actor in after.Characters) {
            bool existed = previousActors.Remove(actor.Id, out TownCharacter? old);
            string failure = FailureOwner(actor.Id);
            // Exact uniform decay translates the reserve but not its absolute zero crossing.
            // Do not invalidate all residents just because At cloned them or changed their food.
            bool sameFailure = existed && old!.Failed == actor.Failed &&
                (actor.Failed || actor.FoodReserve == Math.Max(0, old.FoodReserve - decay) &&
                    previous.Entries.TryGetValue(failure, out TownPrediction? prediction) && prediction.Due >= after.Time);
            if (!sameFailure) { Set(failure, actor.Failed ? null : Failure(after, actor)); }
            if (!existed || old!.Failed != actor.Failed || old.Activity != actor.Activity) {
                Set(ActivityOwner(actor.Id), !actor.Failed && actor.Activity is { } activity
                    ? new(TownPredictionKind.Activity, activity.Due, actor.Id) : null);
            }
        }
        foreach (TownCharacter removed in previousActors.Values) { Set(FailureOwner(removed.Id), null); Set(ActivityOwner(removed.Id), null); }
        if (before.EndTime != after.EndTime) { Set("end", new(TownPredictionKind.End, after.EndTime)); }
        if (before.Market.NextRefresh != after.Market.NextRefresh) { Set("market", new(TownPredictionKind.MarketRefresh, after.Market.NextRefresh)); }
        if (!ReferenceEquals(before.Trades, after.Trades)) {
            var oldOffers = before.Trades.Offers.ToDictionary(offer => offer.Id, StringComparer.Ordinal);
            foreach (TownTradeOffer offer in after.Trades.Offers) {
                bool existed = oldOffers.Remove(offer.Id, out TownTradeOffer? old);
                bool active = offer.Status == TownTradeStatus.Active;
                if (!existed || active != (old!.Status == TownTradeStatus.Active) || active && offer.ExpiresAt != old!.ExpiresAt) {
                    Set(TradeOwner(offer.Id), active ? new(TownPredictionKind.TradeExpiry, offer.ExpiresAt, TradeId: offer.Id) : null);
                }
            }
            foreach (TownTradeOffer removed in oldOffers.Values.Where(offer => offer.Status == TownTradeStatus.Active)) { Set(TradeOwner(removed.Id), null); }
        }
        // Fair service is one global owner: notifications can change somebody other than the acting resident.
        string? deciding = TownEngine.NextDecisionActor(after);
        Set("decision", deciding is null ? null : new(TownPredictionKind.Decision, after.Time, deciding));
        return new(after, entries, recomputed);
    }

    private static TownPrediction Failure(TownBusinessState business, TownCharacter actor) {
        long remaining = checked((actor.FoodReserve + TownContent.FoodDecayPerHour - 1) / TownContent.FoodDecayPerHour);
        return new(TownPredictionKind.Failure, new(checked(business.Time.Ticks + remaining)), actor.Id);
    }
    internal static string FailureOwner(string id) => $"actor:{id}:failure";
    internal static string ActivityOwner(string id) => $"actor:{id}:activity";
    internal static string TradeOwner(string id) => $"trade:{id}:expiry";
}

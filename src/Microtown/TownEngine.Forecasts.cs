using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public static partial class TownEngine {
    /// <summary>Projects prepared town owners plus Spatial's maintained contacts and stored arrivals.
    /// Candidate keys always bind to the current business generation, even for reused owner outputs.</summary>
    public static IReadOnlyList<OccurrenceCandidate<TownPrediction>> Forecast(TownWorld world, SimulationRules rules) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        world.Forecasts.Validate(world.Business);
        TownBusinessState business = world.Business;
        if (business.Ended) { return []; }
        var predictions = new List<TownPrediction>();
        void Add(string owner) {
            if (world.Forecasts.Entries.TryGetValue(owner, out TownPrediction? prediction)) { predictions.Add(prediction); }
        }
        // Preserve the old full-forecast iteration order independently of dictionary insertion history.
        Add("end");
        Add("market");
        foreach (TownCharacter actor in business.Characters) {
            Add(TownPredictionMaintenance.FailureOwner(actor.Id));
            Add(TownPredictionMaintenance.ActivityOwner(actor.Id));
        }
        foreach (TownTradeOffer offer in business.Trades.Offers) { Add(TownPredictionMaintenance.TradeOwner(offer.Id)); }
        predictions.AddRange(new SpatialOccurrenceRule(TownContent.Graph).Forecast(world.Spatial, rules)
            .Concat(new SpatialContactOccurrenceRule(TownContent.Graph).Forecast(world.Spatial, rules))
            .Select(candidate => new TownPrediction(TownPredictionKind.Spatial, candidate.Due.ModelTime, SpatialCandidate: candidate)));
        Add("decision");
        ModelTime due = predictions.Min(prediction => prediction.Due);
        TownPredictionKind kind = predictions.Where(prediction => prediction.Due == due).Min(prediction => prediction.Kind);
        return predictions.Where(prediction => prediction.Due == due && prediction.Kind == kind)
            .Select(prediction => new OccurrenceCandidate<TownPrediction>(
                new CandidateKey($"town:{business.Generation}:{prediction.Kind}:{prediction.ActorId}:{prediction.SpatialCandidate?.Key}" +
                    (prediction.TradeId is { } tradeId ? $":{tradeId}" : "")),
                new(prediction.Due), prediction)).ToArray();
    }

    /// <summary>Independent full-derivation reference for tests and diagnostics. Normal execution never calls this oracle.</summary>
    public static IReadOnlyList<OccurrenceCandidate<TownPrediction>> ForecastOracle(TownWorld world, SimulationRules rules) {
        TownBusinessState business = world.Business;
        if (business.Ended) { return []; }
        var predictions = new List<TownPrediction> {
            new(TownPredictionKind.End, business.EndTime),
            new(TownPredictionKind.MarketRefresh, business.Market.NextRefresh)
        };
        foreach (TownCharacter actor in business.Characters.Where(actor => !actor.Failed)) {
            long remaining = checked((actor.FoodReserve + TownContent.FoodDecayPerHour - 1) / TownContent.FoodDecayPerHour);
            predictions.Add(new(TownPredictionKind.Failure, new(checked(business.Time.Ticks + remaining)), actor.Id));
            if (actor.Activity is { } activity) { predictions.Add(new(TownPredictionKind.Activity, activity.Due, actor.Id)); }
        }
        predictions.AddRange(business.Trades.Offers.Where(offer => offer.Status == TownTradeStatus.Active)
            .Select(offer => new TownPrediction(TownPredictionKind.TradeExpiry, offer.ExpiresAt, TradeId: offer.Id)));
        predictions.AddRange(new SpatialOccurrenceRule(TownContent.Graph).Forecast(world.Spatial, rules).Concat(new SpatialContactOccurrenceRule(TownContent.Graph).Forecast(world.Spatial, rules)).Select(candidate =>
            new TownPrediction(TownPredictionKind.Spatial, candidate.Due.ModelTime, SpatialCandidate: candidate)));
        if (NextDecisionActor(world) is { } deciding) { predictions.Add(new(TownPredictionKind.Decision, business.Time, deciding)); }
        ModelTime due = predictions.Min(prediction => prediction.Due);
        TownPredictionKind kind = predictions.Where(prediction => prediction.Due == due).Min(prediction => prediction.Kind);
        return predictions.Where(prediction => prediction.Due == due && prediction.Kind == kind)
            .Select(prediction => new OccurrenceCandidate<TownPrediction>(
                new CandidateKey($"town:{business.Generation}:{prediction.Kind}:{prediction.ActorId}:{prediction.SpatialCandidate?.Key}" +
                    (prediction.TradeId is { } tradeId ? $":{tradeId}" : "")),
                new(prediction.Due), prediction)).ToArray();
    }
}

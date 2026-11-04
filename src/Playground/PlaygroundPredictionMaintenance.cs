using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground;

/// <summary>Derives runtime forecasts from business snapshots. It neither applies facts nor invokes controllers.</summary>
internal static class PlaygroundPredictionMaintenance {
    internal static PlaygroundForecasts Rebuild(PlaygroundBusinessState business) =>
        new(Owners(business).Select(owner => Predict(business, owner)).OfType<PlaygroundPrediction>(), CreateBindings(business));

    /// <summary>Accumulates affected owners for one recorded fact, including the owner whose prediction was consumed.</summary>
    /// <remarks>The caller supplies consecutive business snapshots and retains this working set across the batch.</remarks>
    internal static void CollectDirtyOwners(PlaygroundBusinessState before, PlaygroundBusinessState after, PlaygroundForecasts previous,
        PlaygroundFact fact, HashSet<string> dirty, PlaygroundMetrics? metrics = null) {
        foreach (PlaygroundBinding binding in previous.FindBindings(fact)) {
            if (metrics is not null) { metrics.RoutedNotifications++; }
            if (Affected(binding.OwnerId, before, after)) {
                if (metrics is not null) { metrics.Invalidations++; }
                dirty.Add(binding.OwnerId);
            }
        }
        dirty.Add(ConsumedOwner(fact));
        // Ending changes remove every forecast owned by that actor, regardless of the source event.
        foreach (PlaygroundCharacter actor in after.Characters) {
            if (before.GetCharacter(actor.Id).FollowingLeaderId != actor.FollowingLeaderId) {
                dirty.Add($"actor:{actor.Id}:attention");
            }
            if (before.GetCharacter(actor.Id).IsActive != actor.IsActive) {
                dirty.Add($"actor:{actor.Id}:activity");
                dirty.Add($"actor:{actor.Id}:attention");
                if (actor.Survival is not null) { dirty.Add($"actor:{actor.Id}:survival"); }
            }
        }
    }

    /// <summary>Recomputes each selected owner against the final business state, preserving unchanged prediction instances.</summary>
    internal static PlaygroundForecasts Update(PlaygroundBusinessState business, PlaygroundForecasts previous, IReadOnlySet<string> owners,
        ModelTime completionTime, PlaygroundMetrics? metrics = null) {
        var predictions = previous.Predictions.ToDictionary(p => p.OwnerId, StringComparer.Ordinal);
        foreach (string owner in owners.Order(StringComparer.Ordinal)) {
            metrics?.Count(owner, $"complete@{completionTime}");
            PlaygroundPrediction? next = Predict(business, owner);
            if (next is null) { predictions.Remove(owner); }
            else if (!predictions.TryGetValue(owner, out PlaygroundPrediction? old) || !old.HasSameIdentity(next)) { predictions[owner] = next; }
        }
        bool changed = predictions.Count != previous.Predictions.Count ||
            previous.Predictions.Any(p => !predictions.TryGetValue(p.OwnerId, out PlaygroundPrediction? current) || !ReferenceEquals(p, current));
        return changed ? previous.WithPredictions(predictions.Values) : previous;
    }

    // Fixed coarse subscriptions retain owners whose current output is absent. Child identity filtering
    // prevents attention-only changes from recalculating an activity or unrelated resource.
    private static bool Affected(string owner, PlaygroundBusinessState before, PlaygroundBusinessState after) {
        if (owner == "trades") { return !ReferenceEquals(before.Economy?.Trades, after.Economy?.Trades); }
        if (owner == "market") { return !ReferenceEquals(before.Economy?.Market, after.Economy?.Market); }
        if (owner == "light") { return !ReferenceEquals(before.Light, after.Light); }
        if (owner == "script") { return !ReferenceEquals(before.Script, after.Script); }
        if (owner == "environment") { return !ReferenceEquals(before.Expedition?.Environment, after.Expedition?.Environment); }
        if (owner.StartsWith("resource:", StringComparison.Ordinal)) {
            string id = owner["resource:".Length..^":regen".Length];
            return !ReferenceEquals(before.GetResource(id), after.GetResource(id));
        }
        if (owner.EndsWith(":survival", StringComparison.Ordinal)) {
            string id = owner["actor:".Length..^":survival".Length];
            return !ReferenceEquals(before.GetCharacter(id).Survival, after.GetCharacter(id).Survival) ||
                before.GetCharacter(id).DecisionGeneration != after.GetCharacter(id).DecisionGeneration;
        }
        bool activity = owner.EndsWith(":activity", StringComparison.Ordinal);
        string actorId = owner["actor:".Length..^(activity ? ":activity".Length : ":attention".Length)];
        PlaygroundCharacter previous = before.GetCharacter(actorId), next = after.GetCharacter(actorId);
        return activity ? !ReferenceEquals(previous.Activity, next.Activity) : !ReferenceEquals(previous, next);
    }
    private static IEnumerable<PlaygroundBinding> CreateBindings(PlaygroundBusinessState business) {
        if (business.Economy is not null) {
            foreach (PlaygroundEventKind kind in new[] { PlaygroundEventKind.TradeOffered, PlaygroundEventKind.TradeAccepted, PlaygroundEventKind.TradeRejected,
                PlaygroundEventKind.TradeWithdrawn, PlaygroundEventKind.TradesExpired, PlaygroundEventKind.SurvivalFailed, PlaygroundEventKind.WeatherResolved,
                PlaygroundEventKind.DecisionContextOpened }) { yield return new("trades", kind, "*"); }
            foreach (PlaygroundEventKind kind in new[] { PlaygroundEventKind.MarketBought, PlaygroundEventKind.MarketSold, PlaygroundEventKind.MarketRefreshed }) { yield return new("market", kind, "*"); }
        }
        yield return new("light", PlaygroundEventKind.LightChanged, "*");
        yield return new("script", PlaygroundEventKind.ResourceDestroyed, "*");
        if (business.Expedition?.Environment is not null) { yield return new("environment", PlaygroundEventKind.WeatherResolved, "*"); }
        foreach (PlaygroundResource resource in business.Resources) {
            string owner = $"resource:{resource.Id}:regen";
            yield return new(owner, PlaygroundEventKind.LightChanged, "*");
            foreach (PlaygroundEventKind kind in new[] { PlaygroundEventKind.ResourceRegenerated, PlaygroundEventKind.ResourceDestroyed, PlaygroundEventKind.GatherYielded }) {
                yield return new(owner, kind, resource.Id);
            }
        }
        foreach (PlaygroundCharacter actor in business.Characters) {
            if (actor.Survival is not null) {
                foreach (PlaygroundEventKind kind in Enum.GetValues<PlaygroundEventKind>()) {
                    yield return new($"actor:{actor.Id}:survival", kind, "*");
                }
                foreach (PlaygroundEventKind kind in new[] { PlaygroundEventKind.LightChanged, PlaygroundEventKind.SurvivalWarning,
                    PlaygroundEventKind.SurvivalFailed, PlaygroundEventKind.ItemUsed, PlaygroundEventKind.WeatherResolved }) {
                    yield return new($"actor:{actor.Id}:attention", kind, "*");
                }
            }
            foreach (PlaygroundEventKind kind in new[] { PlaygroundEventKind.WaitStarted, PlaygroundEventKind.WaitCompleted,
                PlaygroundEventKind.GatherStarted, PlaygroundEventKind.GatherYielded, PlaygroundEventKind.GatherFinished, PlaygroundEventKind.ActivityCancelled,
                PlaygroundEventKind.ProductionStarted, PlaygroundEventKind.ProductionCompleted }) {
                yield return new($"actor:{actor.Id}:activity", kind, actor.Id);
            }
            foreach (PlaygroundEventKind kind in new[] { PlaygroundEventKind.WaitStarted, PlaygroundEventKind.WaitCompleted,
                PlaygroundEventKind.GatherFinished, PlaygroundEventKind.ActivityCancelled, PlaygroundEventKind.AttentionAcknowledged,
                PlaygroundEventKind.HandoffEdited, PlaygroundEventKind.GoalOperation, PlaygroundEventKind.DecisionContextOpened, PlaygroundEventKind.OperationRejected,
                PlaygroundEventKind.ProductionStarted, PlaygroundEventKind.ProductionCompleted, PlaygroundEventKind.MarketBought, PlaygroundEventKind.MarketSold }) {
                yield return new($"actor:{actor.Id}:attention", kind, actor.Id);
            }
            foreach (PlaygroundEventKind kind in new[] { PlaygroundEventKind.GatherStarted, PlaygroundEventKind.GatherYielded, PlaygroundEventKind.ResourceDestroyed, PlaygroundEventKind.SpeechSent, PlaygroundEventKind.ItemGiven, PlaygroundEventKind.SpeechShouted,
                PlaygroundEventKind.MoveStarted, PlaygroundEventKind.MoveArrived, PlaygroundEventKind.FollowStarted, PlaygroundEventKind.FollowEnded, PlaygroundEventKind.FollowersDismissed, PlaygroundEventKind.MoneyGiven, PlaygroundEventKind.TradeOffered, PlaygroundEventKind.TradeAccepted,
                PlaygroundEventKind.TradeRejected, PlaygroundEventKind.TradeWithdrawn, PlaygroundEventKind.TradesExpired,
                PlaygroundEventKind.SurvivalFailed, PlaygroundEventKind.WeatherResolved }) {
                yield return new($"actor:{actor.Id}:attention", kind, "*");
            }
        }
    }
    internal static void Validate(PlaygroundBusinessState business, PlaygroundForecasts forecasts, ModelTime boundaryTime) {
        var owners = Owners(business).ToHashSet(StringComparer.Ordinal);
        string? previous = null;
        foreach (PlaygroundPrediction prediction in forecasts.Predictions) {
            if (prediction.Due < boundaryTime || !owners.Contains(prediction.OwnerId) || !Enum.IsDefined(prediction.Kind) ||
                (previous is not null && StringComparer.Ordinal.Compare(previous, prediction.OwnerId) >= 0) || !ValidIdentity(business, prediction)) {
                throw new InvalidOperationException("Invalid prediction set.");
            }
            previous = prediction.OwnerId;
        }
    }
    private static bool ValidIdentity(PlaygroundBusinessState business, PlaygroundPrediction prediction) {
        switch (prediction.Kind) {
            case PlaygroundPredictionKind.TradesExpire:
                return prediction.OwnerId == "trades" && prediction.HasSameIdentity(Predict(business, "trades"));
            case PlaygroundPredictionKind.MarketRefresh:
                return business.Economy is { } economy && prediction.OwnerId == "market" && prediction.TargetId == "world" &&
                    prediction.Due == economy.Market.NextRefresh && prediction.Generation == economy.Market.Cycle && prediction.Sequence == 0;
            case PlaygroundPredictionKind.ChangeLight:
                return prediction.OwnerId == "light" && prediction.TargetId == "world" && prediction.Generation == 0 && prediction.Sequence == 0 && prediction.Due == business.Light.NextChange;
            case PlaygroundPredictionKind.DestroyResource:
                return prediction.OwnerId == "script" && prediction.Generation == business.Script.NextIndex && prediction.Sequence == 0 &&
                    business.Script.NextIndex < business.Script.Entries.Count && business.Script.Entries[business.Script.NextIndex].ResourceId == prediction.TargetId &&
                    business.Script.Entries[business.Script.NextIndex].Due == prediction.Due;
            case PlaygroundPredictionKind.RegenerateResource:
                PlaygroundResource? resource = business.Resources.SingleOrDefault(r => r.Id == prediction.TargetId);
                return resource is not null && prediction.OwnerId == $"resource:{resource.Id}:regen" && prediction.Generation == resource.Generation &&
                    prediction.Sequence == 0 && resource.Stock < resource.Capacity && resource.ActiveSince is not null;
            case PlaygroundPredictionKind.CompleteActivity:
                PlaygroundCharacter? actor = business.Characters.SingleOrDefault(c => c.Id == prediction.TargetId);
                return actor?.Activity is { } activity && prediction.OwnerId == $"actor:{actor.Id}:activity" &&
                    prediction.Generation == activity.Id && prediction.Sequence == activity.CycleIndex && prediction.Due == activity.Due;
            case PlaygroundPredictionKind.RequestDecision:
                PlaygroundCharacter? deciding = business.Characters.SingleOrDefault(c => c.Id == prediction.TargetId);
                return deciding is not null && PlaygroundActions.NeedsDecision(business, deciding.Id) && prediction.OwnerId == $"actor:{deciding.Id}:attention" &&
                    prediction.Generation == deciding.DecisionGeneration && prediction.Sequence == 0 && prediction.Due == business.Time;
            case PlaygroundPredictionKind.SurvivalFatal or PlaygroundPredictionKind.SurvivalWarning:
                return prediction.HasSameIdentity(Predict(business, prediction.OwnerId));
            case PlaygroundPredictionKind.Weather:
                return prediction.OwnerId == "environment" && prediction.TargetId == "world" && prediction.Sequence == 0 &&
                    business.Expedition?.Environment is { } environment && prediction.Generation == environment.Sequence && prediction.Due == environment.NextDue;
            default: return false;
        }
    }
    private static string ConsumedOwner(PlaygroundFact fact) => fact.Kind switch {
        PlaygroundEventKind.TradesExpired => "trades",
        PlaygroundEventKind.MarketRefreshed => "market",
        PlaygroundEventKind.LightChanged => "light",
        PlaygroundEventKind.ResourceDestroyed => "script",
        PlaygroundEventKind.ResourceRegenerated => $"resource:{fact.ResourceId}:regen",
        PlaygroundEventKind.WeatherResolved => "environment",
        PlaygroundEventKind.SurvivalWarning or PlaygroundEventKind.SurvivalFailed => $"actor:{fact.ActorId}:survival",
        PlaygroundEventKind.GatherYielded or PlaygroundEventKind.GatherFinished or PlaygroundEventKind.WaitCompleted or PlaygroundEventKind.ProductionCompleted => $"actor:{fact.ActorId}:activity",
        _ => $"actor:{fact.ActorId}:attention"
    };
    internal static IEnumerable<string> Owners(PlaygroundBusinessState business) => new[] { "light", "script" }
        .Concat(business.Economy is not null ? new[] { "market", "trades" } : [])
        .Concat(business.Resources.Select(r => $"resource:{r.Id}:regen"))
        .Concat(business.Characters.SelectMany(c => new[] { $"actor:{c.Id}:activity", $"actor:{c.Id}:attention" }))
        .Concat(business.Characters.Where(c => c.Survival is not null).Select(c => $"actor:{c.Id}:survival"))
        .Concat(business.Expedition?.Environment is not null ? new[] { "environment" } : []);
    internal static PlaygroundPrediction? Predict(PlaygroundBusinessState business, string owner) {
        if (owner == "trades") {
            PlaygroundTradeOffer? first = business.Economy?.Trades.Offers.Where(offer => offer.Status == PlaygroundTradeStatus.Active)
                .OrderBy(offer => offer.ExpiresAt).ThenBy(offer => offer.Id, StringComparer.Ordinal).FirstOrDefault();
            return first is null ? null : new(owner, PlaygroundPredictionKind.TradesExpire, first.Id, first.ExpiresAt, 0);
        }
        if (owner == "market") { return business.Economy is { } economy ? new(owner, PlaygroundPredictionKind.MarketRefresh, "world", economy.Market.NextRefresh, economy.Market.Cycle) : null; }
        if (owner == "environment") {
            PlaygroundEnvironment environment = business.Expedition!.Environment!;
            return environment.NextDue is { } due ? new(owner, PlaygroundPredictionKind.Weather, "world", due, environment.Sequence) : null;
        }
        if (owner.EndsWith(":survival", StringComparison.Ordinal)) {
            PlaygroundCharacter actor = business.GetCharacter(owner["actor:".Length..^":survival".Length]);
            if (!actor.IsActive) { return null; }
            return PlaygroundSurvivalRules.NextThreshold(actor.Survival!, business.Time) is { } threshold
                ? new(owner, threshold.Fatal ? PlaygroundPredictionKind.SurvivalFatal : PlaygroundPredictionKind.SurvivalWarning,
                    actor.Id, threshold.Due, actor.DecisionGeneration) : null;
        }
        if (owner == "light") { return new(owner, PlaygroundPredictionKind.ChangeLight, "world", business.Light.NextChange, 0); }
        if (owner == "script") {
            if (business.Script.NextIndex == business.Script.Entries.Count) { return null; }
            PlaygroundDestruction entry = business.Script.Entries[business.Script.NextIndex];
            return new(owner, PlaygroundPredictionKind.DestroyResource, entry.ResourceId, entry.Due, business.Script.NextIndex);
        }
        if (owner.StartsWith("resource:", StringComparison.Ordinal) && owner.EndsWith(":regen", StringComparison.Ordinal)) {
            PlaygroundResource resource = business.GetResource(owner["resource:".Length..^":regen".Length]);
            if (resource.ActiveSince is not { } since || resource.Stock == resource.Capacity) { return null; }
            long remaining = resource.RegenerationTicks - resource.AccumulatedTicks;
            if (since.Ticks > long.MaxValue - remaining) { return null; }
            return new(owner, PlaygroundPredictionKind.RegenerateResource, resource.Id, new(since.Ticks + remaining), resource.Generation);
        }
        if (owner.StartsWith("actor:", StringComparison.Ordinal) && owner.EndsWith(":activity", StringComparison.Ordinal)) {
            PlaygroundCharacter actor = business.GetCharacter(owner["actor:".Length..^":activity".Length]);
            return actor.IsActive && actor.Activity is { } activity ? new(owner, PlaygroundPredictionKind.CompleteActivity, actor.Id, activity.Due, activity.Id, activity.CycleIndex) : null;
        }
        if (owner.StartsWith("actor:", StringComparison.Ordinal) && owner.EndsWith(":attention", StringComparison.Ordinal)) {
            PlaygroundCharacter actor = business.GetCharacter(owner["actor:".Length..^":attention".Length]);
            return PlaygroundActions.NeedsDecision(business, actor.Id) ? new(owner, PlaygroundPredictionKind.RequestDecision, actor.Id, business.Time, actor.DecisionGeneration) : null;
        }
        throw new InvalidOperationException("Unknown prediction owner.");
    }

}

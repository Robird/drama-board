using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Derives runtime forecasts from business snapshots. It neither applies facts nor invokes controllers.</summary>
internal static class CyclePredictionMaintenance {
    internal static CycleForecasts Rebuild(CycleBusinessState business) =>
        new(Owners(business).Select(owner => Predict(business, owner)).OfType<CyclePrediction>(), CreateBindings(business));

    /// <summary>Accumulates affected owners for one recorded fact, including the owner whose prediction was consumed.</summary>
    /// <remarks>The caller supplies consecutive business snapshots and retains this working set across the batch.</remarks>
    internal static void CollectDirtyOwners(CycleBusinessState before, CycleBusinessState after, CycleForecasts previous,
        CycleFact fact, HashSet<string> dirty, CycleMetrics? metrics = null) {
        foreach (CycleBinding binding in previous.FindBindings(fact)) {
            if (metrics is not null) { metrics.RoutedNotifications++; }
            if (Affected(binding.OwnerId, before, after)) {
                if (metrics is not null) { metrics.Invalidations++; }
                dirty.Add(binding.OwnerId);
            }
        }
        dirty.Add(ConsumedOwner(fact));
        // Ending changes remove every forecast owned by that actor, regardless of the source event.
        foreach (CycleCharacter actor in after.Characters) {
            if (before.GetCharacter(actor.Id).IsActive != actor.IsActive) {
                dirty.Add($"actor:{actor.Id}:activity");
                dirty.Add($"actor:{actor.Id}:attention");
                if (actor.Survival is not null) { dirty.Add($"actor:{actor.Id}:survival"); }
            }
        }
    }

    /// <summary>Recomputes each selected owner against the final business state, preserving unchanged prediction instances.</summary>
    internal static CycleForecasts Update(CycleBusinessState business, CycleForecasts previous, IReadOnlySet<string> owners,
        ModelTime completionTime, CycleMetrics? metrics = null) {
        var predictions = previous.Predictions.ToDictionary(p => p.OwnerId, StringComparer.Ordinal);
        foreach (string owner in owners.Order(StringComparer.Ordinal)) {
            metrics?.Count(owner, $"complete@{completionTime}");
            CyclePrediction? next = Predict(business, owner);
            if (next is null) { predictions.Remove(owner); }
            else if (!predictions.TryGetValue(owner, out CyclePrediction? old) || old.ToString() != next.ToString()) { predictions[owner] = next; }
        }
        bool changed = predictions.Count != previous.Predictions.Count ||
            previous.Predictions.Any(p => !predictions.TryGetValue(p.OwnerId, out CyclePrediction? current) || !ReferenceEquals(p, current));
        return changed ? previous.WithPredictions(predictions.Values) : previous;
    }

    // Fixed coarse subscriptions retain owners whose current output is absent. Child identity filtering
    // prevents attention-only changes from recalculating an activity or unrelated resource.
    private static bool Affected(string owner, CycleBusinessState before, CycleBusinessState after) {
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
        CycleCharacter previous = before.GetCharacter(actorId), next = after.GetCharacter(actorId);
        return activity ? !ReferenceEquals(previous.Activity, next.Activity) : !ReferenceEquals(previous, next);
    }
    private static IEnumerable<CycleBinding> CreateBindings(CycleBusinessState business) {
        yield return new("light", CycleEventKind.LightChanged, "*");
        yield return new("script", CycleEventKind.ResourceDestroyed, "*");
        if (business.Expedition?.Environment is not null) { yield return new("environment", CycleEventKind.WeatherResolved, "*"); }
        foreach (CycleResource resource in business.Resources) {
            string owner = $"resource:{resource.Id}:regen";
            yield return new(owner, CycleEventKind.LightChanged, "*");
            foreach (CycleEventKind kind in new[] { CycleEventKind.ResourceRegenerated, CycleEventKind.ResourceDestroyed, CycleEventKind.GatherYielded }) {
                yield return new(owner, kind, resource.Id);
            }
        }
        foreach (CycleCharacter actor in business.Characters) {
            if (actor.Survival is not null) {
                foreach (CycleEventKind kind in Enum.GetValues<CycleEventKind>()) {
                    yield return new($"actor:{actor.Id}:survival", kind, "*");
                }
                foreach (CycleEventKind kind in new[] { CycleEventKind.LightChanged, CycleEventKind.SurvivalWarning,
                    CycleEventKind.SurvivalFailed, CycleEventKind.ItemUsed, CycleEventKind.WeatherResolved }) {
                    yield return new($"actor:{actor.Id}:attention", kind, "*");
                }
            }
            foreach (CycleEventKind kind in new[] { CycleEventKind.WaitStarted, CycleEventKind.WaitCompleted,
                CycleEventKind.GatherStarted, CycleEventKind.GatherYielded, CycleEventKind.GatherFinished, CycleEventKind.ActivityCancelled }) {
                yield return new($"actor:{actor.Id}:activity", kind, actor.Id);
            }
            foreach (CycleEventKind kind in new[] { CycleEventKind.WaitStarted, CycleEventKind.WaitCompleted,
                CycleEventKind.GatherFinished, CycleEventKind.ActivityCancelled, CycleEventKind.AttentionAcknowledged,
                CycleEventKind.MoveStarted, CycleEventKind.MoveArrived, CycleEventKind.HandoffEdited, CycleEventKind.DecisionContextOpened, CycleEventKind.OperationRejected }) {
                yield return new($"actor:{actor.Id}:attention", kind, actor.Id);
            }
            foreach (CycleEventKind kind in new[] { CycleEventKind.GatherStarted, CycleEventKind.GatherYielded, CycleEventKind.ResourceDestroyed }) {
                yield return new($"actor:{actor.Id}:attention", kind, "*");
            }
        }
    }
    internal static void Validate(CycleBusinessState business, CycleForecasts forecasts, ModelTime boundaryTime) {
        var owners = Owners(business).ToHashSet(StringComparer.Ordinal);
        string? previous = null;
        foreach (CyclePrediction prediction in forecasts.Predictions) {
            if (prediction.Due < boundaryTime || !owners.Contains(prediction.OwnerId) || !Enum.IsDefined(prediction.Kind) ||
                (previous is not null && StringComparer.Ordinal.Compare(previous, prediction.OwnerId) >= 0) || !ValidIdentity(business, prediction)) {
                throw new InvalidOperationException("Invalid prediction set.");
            }
            previous = prediction.OwnerId;
        }
    }
    private static bool ValidIdentity(CycleBusinessState business, CyclePrediction prediction) {
        switch (prediction.Kind) {
            case CyclePredictionKind.ChangeLight:
                return prediction.OwnerId == "light" && prediction.TargetId == "world" && prediction.Generation == 0 && prediction.Sequence == 0 && prediction.Due == business.Light.NextChange;
            case CyclePredictionKind.DestroyResource:
                return prediction.OwnerId == "script" && prediction.Generation == business.Script.NextIndex && prediction.Sequence == 0 &&
                    business.Script.NextIndex < business.Script.Entries.Count && business.Script.Entries[business.Script.NextIndex].ResourceId == prediction.TargetId &&
                    business.Script.Entries[business.Script.NextIndex].Due == prediction.Due;
            case CyclePredictionKind.RegenerateResource:
                CycleResource? resource = business.Resources.SingleOrDefault(r => r.Id == prediction.TargetId);
                return resource is not null && prediction.OwnerId == $"resource:{resource.Id}:regen" && prediction.Generation == resource.Generation &&
                    prediction.Sequence == 0 && resource.Stock < resource.Capacity && resource.ActiveSince is not null;
            case CyclePredictionKind.CompleteActivity:
                CycleCharacter? actor = business.Characters.SingleOrDefault(c => c.Id == prediction.TargetId);
                return actor?.Activity is { } activity && prediction.OwnerId == $"actor:{actor.Id}:activity" &&
                    prediction.Generation == activity.Id && prediction.Sequence == activity.CycleIndex && prediction.Due == activity.Due;
            case CyclePredictionKind.RequestDecision:
                CycleCharacter? deciding = business.Characters.SingleOrDefault(c => c.Id == prediction.TargetId);
                return deciding is not null && CycleActions.NeedsDecision(business, deciding.Id) && prediction.OwnerId == $"actor:{deciding.Id}:attention" &&
                    prediction.Generation == deciding.DecisionGeneration && prediction.Sequence == 0 && prediction.Due == business.Time;
            case CyclePredictionKind.SurvivalFatal or CyclePredictionKind.SurvivalWarning:
                return Predict(business, prediction.OwnerId)?.ToString() == prediction.ToString();
            case CyclePredictionKind.Weather:
                return prediction.OwnerId == "environment" && prediction.TargetId == "world" && prediction.Sequence == 0 &&
                    business.Expedition?.Environment is { } environment && prediction.Generation == environment.Sequence && prediction.Due == environment.NextDue;
            default: return false;
        }
    }
    private static string ConsumedOwner(CycleFact fact) => fact.Kind switch {
        CycleEventKind.LightChanged => "light",
        CycleEventKind.ResourceDestroyed => "script",
        CycleEventKind.ResourceRegenerated => $"resource:{fact.ResourceId}:regen",
        CycleEventKind.WeatherResolved => "environment",
        CycleEventKind.SurvivalWarning or CycleEventKind.SurvivalFailed => $"actor:{fact.ActorId}:survival",
        CycleEventKind.GatherYielded or CycleEventKind.GatherFinished or CycleEventKind.WaitCompleted => $"actor:{fact.ActorId}:activity",
        _ => $"actor:{fact.ActorId}:attention"
    };
    internal static IEnumerable<string> Owners(CycleBusinessState business) => new[] { "light", "script" }
        .Concat(business.Resources.Select(r => $"resource:{r.Id}:regen"))
        .Concat(business.Characters.SelectMany(c => new[] { $"actor:{c.Id}:activity", $"actor:{c.Id}:attention" }))
        .Concat(business.Characters.Where(c => c.Survival is not null).Select(c => $"actor:{c.Id}:survival"))
        .Concat(business.Expedition?.Environment is not null ? new[] { "environment" } : []);
    internal static CyclePrediction? Predict(CycleBusinessState business, string owner) {
        if (owner == "environment") {
            CycleEnvironment environment = business.Expedition!.Environment!;
            return environment.NextDue is { } due ? new(owner, CyclePredictionKind.Weather, "world", due, environment.Sequence) : null;
        }
        if (owner.EndsWith(":survival", StringComparison.Ordinal)) {
            CycleCharacter actor = business.GetCharacter(owner["actor:".Length..^":survival".Length]);
            if (!actor.IsActive) { return null; }
            return CycleSurvivalRules.NextThreshold(actor.Survival!, business.Time) is { } threshold
                ? new(owner, threshold.Fatal ? CyclePredictionKind.SurvivalFatal : CyclePredictionKind.SurvivalWarning,
                    actor.Id, threshold.Due, actor.DecisionGeneration) : null;
        }
        if (owner == "light") { return new(owner, CyclePredictionKind.ChangeLight, "world", business.Light.NextChange, 0); }
        if (owner == "script") {
            if (business.Script.NextIndex == business.Script.Entries.Count) { return null; }
            CycleDestruction entry = business.Script.Entries[business.Script.NextIndex];
            return new(owner, CyclePredictionKind.DestroyResource, entry.ResourceId, entry.Due, business.Script.NextIndex);
        }
        if (owner.StartsWith("resource:", StringComparison.Ordinal) && owner.EndsWith(":regen", StringComparison.Ordinal)) {
            CycleResource resource = business.GetResource(owner["resource:".Length..^":regen".Length]);
            if (resource.ActiveSince is not { } since || resource.Stock == resource.Capacity) { return null; }
            long remaining = resource.RegenerationTicks - resource.AccumulatedTicks;
            if (since.Ticks > long.MaxValue - remaining) { return null; }
            return new(owner, CyclePredictionKind.RegenerateResource, resource.Id, new(since.Ticks + remaining), resource.Generation);
        }
        if (owner.StartsWith("actor:", StringComparison.Ordinal) && owner.EndsWith(":activity", StringComparison.Ordinal)) {
            CycleCharacter actor = business.GetCharacter(owner["actor:".Length..^":activity".Length]);
            return actor.IsActive && actor.Activity is { } activity ? new(owner, CyclePredictionKind.CompleteActivity, actor.Id, activity.Due, activity.Id, activity.CycleIndex) : null;
        }
        if (owner.StartsWith("actor:", StringComparison.Ordinal) && owner.EndsWith(":attention", StringComparison.Ordinal)) {
            CycleCharacter actor = business.GetCharacter(owner["actor:".Length..^":attention".Length]);
            return CycleActions.NeedsDecision(business, actor.Id) ? new(owner, CyclePredictionKind.RequestDecision, actor.Id, business.Time, actor.DecisionGeneration) : null;
        }
        throw new InvalidOperationException("Unknown prediction owner.");
    }

}

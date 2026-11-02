using System.Text;

namespace DramaBoard.Playground;

/// <summary>Privileged diagnostics; unlike the player view this deliberately exposes all process state.</summary>
public static class PlaygroundDiagnostics {
    public static string Render(PlaygroundWorld world, PlaygroundMetrics? metrics = null) {
        var text = new StringBuilder();
        text.AppendLine($"# Playground — {world.Time}");
        text.AppendLine($"Light: {(world.Light.IsDay ? "day" : "night")}; next change: {world.Light.NextChange}");
        text.AppendLine();
        text.AppendLine("## Resources");
        foreach (PlaygroundResource resource in world.Resources) {
            string state = resource.Stock == resource.Capacity ? "full" : resource.ActiveSince is null ? "paused" : "regenerating";
            text.AppendLine($"- {resource.Id}: {resource.Stock}/{resource.Capacity}; {state}; regeneration {resource.ProgressAt(world.Time) / (double)PlaygroundTime.Hour:0.###}/{resource.RegenerationTicks / (double)PlaygroundTime.Hour:0.###} h; generation {resource.Generation}");
        }
        text.AppendLine();
        text.AppendLine("## Characters");
        foreach (PlaygroundCharacter actor in world.Characters) {
            text.AppendLine($"- {actor.Id}: {actor.Activity?.ToString() ?? "idle"}; request generation {actor.DecisionGeneration}; needs decision={PlaygroundActions.NeedsDecision(world, actor.Id)}; following={actor.FollowingLeaderId ?? "none"}");
            if (actor.Ending is { } ending) {
                text.AppendLine($"  - ended: {ending.Outcome} at {ending.At}; {ending.Reason}; historical location: {ending.Location}");
            }
            text.AppendLine($"  - backpack: {actor.Backpack}; notices: [{string.Join(';', actor.Notices)}]");
            text.AppendLine($"  - last result: {actor.LastResult}");
        }
        text.AppendLine($"Last served: {world.Business.LastServedActorId ?? "none"}");
        if (world.Expedition is not null) { text.AppendLine($"Expedition summary: {world.ExpeditionSummary}"); }
        text.AppendLine();
        text.AppendLine("## Predictors and dependencies");
        text.AppendLine("Fixed coarse subscriptions are filtered by affected child identity; absent outputs remain subscribed.");
        foreach (string owner in PlaygroundPredictionMaintenance.Owners(world.Business)) {
            PlaygroundPrediction? prediction = world.Forecasts.Predictions.SingleOrDefault(p => p.OwnerId == owner);
            text.AppendLine($"- {owner}: {(prediction is null ? "no current prediction" : $"{prediction.Kind}({prediction.TargetId}) at {prediction.Due}")}");
            text.AppendLine($"  - watches: {string.Join(", ", world.Forecasts.Bindings.Where(b => b.OwnerId == owner).Select(b => b.EventKind))}");
            if (metrics is not null) {
                text.AppendLine($"  - recalculations: {metrics.RecalculationsByOwner.GetValueOrDefault(owner)}; last reason: {metrics.LastReasons.GetValueOrDefault(owner, "genesis/restored")}");
            }
        }
        text.AppendLine($"Script: {world.Script.NextIndex}/{world.Script.Entries.Count} consumed");
        if (metrics is not null) {
            text.AppendLine($"Process counters: completion recalculations={metrics.Recalculations}; planning prediction checks={metrics.PredictionChecks}; routed={metrics.RoutedNotifications}; invalidations={metrics.Invalidations}");
            text.AppendLine("Counters cover this Kernel instance. Completion includes pending recovery; genesis and cold-open rebuild are excluded. Recovery never repeats Player input or planning.");
        }
        return text.ToString();
    }
}

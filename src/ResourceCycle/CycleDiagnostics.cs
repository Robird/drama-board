using System.Text;

namespace DramaBoard.ResourceCycle;

public static class CycleDiagnostics {
    public static string Render(CycleWorld world, CycleMetrics? metrics = null) {
        var text = new StringBuilder();
        text.AppendLine($"# Resource cycle — {world.Time}");
        text.AppendLine($"Light: {(world.Light.IsDay ? "day" : "night")}; next change: {world.Light.NextChange}");
        text.AppendLine();
        text.AppendLine("## Plants");
        foreach (CyclePlant plant in world.Plants) {
            string state = plant.IsMature ? "mature" : plant.ActiveSince is null ? "paused" : "growing";
            text.AppendLine($"- {plant.Id}: {state}; grows during {(plant.GrowsInDaylight ? "day" : "night")}; effective growth {plant.ProgressAt(world.Time) / (double)CycleTime.Hour:0.###}/{plant.RequiredTicks / (double)CycleTime.Hour:0.###} h; cycle {plant.Generation}");
        }
        text.AppendLine();
        if (world.Character is { } actor) {
            text.AppendLine("## Character");
            text.AppendLine($"- {actor.Id}: {(actor.WaitUntil is { } until ? $"waiting until {until}" : "ready for decision")}; request generation {actor.DecisionGeneration}");
            text.AppendLine($"- backpack: {actor.Backpack}");
            if (actor.LastResult is { } result) { text.AppendLine($"- last action: {result}"); }
            text.AppendLine();
        }
        text.AppendLine("## Predictors and dependencies");
        text.AppendLine("Predictions assume current conditions; watched events can invalidate them. Dependencies remain active even with no current prediction.");
        foreach (string owner in CycleEngine.Owners(world)) {
            CyclePrediction? prediction = world.Forecasts.Predictions.SingleOrDefault(p => p.OwnerId == owner);
            text.AppendLine($"- {owner}: {(prediction is null ? "no current prediction" : $"{prediction.Kind}({prediction.TargetId}) at {prediction.Due}")}");
            foreach (CycleBinding binding in world.Forecasts.Bindings.Where(b => b.OwnerId == owner)) {
                text.AppendLine($"  - watches {binding.EventKind} target={binding.TargetId}");
            }
            if (metrics is not null) {
                text.AppendLine($"  - recalculations: {metrics.RecalculationsByOwner.GetValueOrDefault(owner)}; last reason: {metrics.LastReasons.GetValueOrDefault(owner, "genesis/restored")}");
            }
        }
        text.AppendLine();
        text.AppendLine($"Script: {world.Script.NextIndex}/{world.Script.Entries.Count} consumed");
        if (metrics is not null) {
            text.AppendLine($"Process planning counters: dirty recalculations={metrics.Recalculations}; execution prediction checks={metrics.PredictionChecks}; routed={metrics.RoutedNotifications}; invalidations={metrics.Invalidations}");
            text.AppendLine("Counters and last reasons cover this process only; genesis calculations and recovered facts are not counted as planning.");
        }
        return text.ToString();
    }
}

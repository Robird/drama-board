using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Tests;

internal static class CycleTestSupport {
    public static ModelTime Hour(long hours) => new(hours * 3_600_000);

    public static KernelCursor InitialCursor(CycleWorld world) =>
        new(new WorldVersion(17, 0), world.Time, null, null);

    public static InMemoryOccurrenceHistory<CycleWorld, CycleFact> Memory(CycleWorld world) =>
        new(world, InitialCursor(world));

    public static async Task RunUntil(
        SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, ModelTime boundary) {
        for (int steps = 0; steps < 1_000; steps++) {
            if (await kernel.StepAsync(boundary) != StepStatus.Committed) { return; }
            AssertPredictions(kernel.World);
        }
        Assert.Fail("The finite test horizon exceeded 1,000 occurrences; possible zero-time loop.");
    }

    public static string Describe(OccurrenceEvent<CycleFact> occurrence) =>
        $"{occurrence.CauseKey}|{occurrence.TargetInstant}|{string.Join("\n", occurrence.Facts.Select(fact => fact.ToString()))}";

    public static long Recalculations(CycleMetrics metrics, string owner) =>
        metrics.RecalculationsByOwner.TryGetValue(owner, out long count) ? count : 0;

    // Deliberately enumerate business processes directly, without Owners/Predict or the binding index.
    // Rebuild agreement alone would miss a discovery bug shared by incremental and full maintenance.
    public static void AssertPredictions(CycleWorld world) {
        List<(string Owner, CyclePredictionKind Kind, string Target, long Due, long Generation)> expected = [
            ("light", CyclePredictionKind.ChangeLight, "world", world.Light.NextChange.Ticks, 0)
        ];
        if (world.Script.NextIndex < world.Script.Entries.Count) {
            CycleDestruction entry = world.Script.Entries[world.Script.NextIndex];
            expected.Add(("script", CyclePredictionKind.DestroyPlant, entry.PlantId, entry.Due.Ticks, world.Script.NextIndex));
        }
        foreach (CyclePlant plant in world.Plants) {
            if (!plant.IsMature && plant.ActiveSince is { } started) {
                long remaining = plant.RequiredTicks - plant.AccumulatedTicks;
                expected.Add(($"plant:{plant.Id}", CyclePredictionKind.MaturePlant, plant.Id,
                    checked(started.Ticks + remaining), plant.Generation));
            }
        }
        if (world.Character is { } actor) {
            expected.Add(($"actor:{actor.Id}", actor.WaitUntil is null
                ? CyclePredictionKind.RequestDecision : CyclePredictionKind.CompleteWait,
                actor.Id, (actor.WaitUntil ?? world.Time).Ticks, actor.DecisionGeneration));
        }
        Assert.Equal(expected.OrderBy(p => p.Owner, StringComparer.Ordinal),
            world.Forecasts.Predictions.Select(p => (p.OwnerId, p.Kind, p.TargetId, p.Due.Ticks, p.Generation))
                .OrderBy(p => p.OwnerId, StringComparer.Ordinal));
        CycleWorld rebuilt = CycleEngine.Rebuild(world.Business, world.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), world.Forecasts.ToString());
    }
}

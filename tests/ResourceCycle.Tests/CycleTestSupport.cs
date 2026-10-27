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
        }
        Assert.Fail("The finite test horizon exceeded 1,000 occurrences; possible zero-time loop.");
    }

    public static string Describe(OccurrenceEvent<CycleFact> occurrence) =>
        $"{occurrence.CauseKey}|{occurrence.TargetInstant}|{string.Join("\n", occurrence.Facts.Select(fact => fact.ToString()))}";

    public static long Recalculations(CycleMetrics metrics, string owner) =>
        metrics.RecalculationsByOwner.TryGetValue(owner, out long count) ? count : 0;
}

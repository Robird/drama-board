using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Tests;

internal static class CycleTestSupport {
    public static ModelTime Hour(long hours) => new(hours * CycleTime.Hour);
    public static ModelTime Minute(long minutes) => new(minutes * 60_000);
    public static KernelCursor InitialCursor(CycleWorld world) => new(new WorldVersion(17, 0), world.Time, null, null);
    public static InMemoryOccurrenceHistory<CycleWorld, CycleFact> Memory(CycleWorld world) => new(world, InitialCursor(world));
    public static CycleWorld Solo(ModelTime? start = null, IEnumerable<CycleResource>? resources = null, int skill = 0) =>
        CycleWorld.CreatePlayable(start, [new("a", "Alice", skill)], resources);
    public static CycleResource Sun(ModelTime start, int stock = 3, int capacity = 5,
        long regenerationTicks = 3 * CycleTime.Hour, long harvestTicks = CycleTime.Hour, int requiredLevel = 0) =>
        new("sunflower", "Sunflower", "item.sunflower", true, regenerationTicks, capacity, stock,
            stock < capacity && CycleTime.IsDay(start) ? start : null, harvestTicks: harvestTicks, requiredLevel: requiredLevel);
    public static CycleResource Moon(ModelTime start, int stock = 3, int capacity = 5) =>
        new("moongrass", "Moongrass", "item.moongrass", false, 4 * CycleTime.Hour, capacity, stock,
            stock < capacity && !CycleTime.IsDay(start) ? start : null);
    public static async Task RunUntil(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, ModelTime boundary) {
        for (int steps = 0; steps < 1_000; steps++) {
            if (await kernel.StepAsync(boundary) != StepStatus.Committed) { return; }
            AssertPredictions(kernel.World);
        }
        Assert.Fail("The finite horizon exceeded 1,000 occurrences.");
    }
    public static async Task Step(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        AssertPredictions(kernel.World);
    }
    public static async Task Until(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> stop) {
        for (int i = 0; i < 300; i++) {
            if (stop(kernel.World)) { return; }
            await Step(kernel);
        }
        Assert.Fail("Expected business boundary was not reached.");
    }
    public static string Describe(OccurrenceEvent<CycleFact> occurrence) =>
        $"{occurrence.CauseKey}|{occurrence.TargetInstant}|{string.Join("\n", occurrence.Facts.Select(fact => fact.ToString()))}";
    public static long Recalculations(CycleMetrics metrics, string owner) =>
        metrics.RecalculationsByOwner.TryGetValue(owner, out long count) ? count : 0;

    // Independent discovery/formula oracle: do not call Owners/Predict or consult routing.
    public static void AssertPredictions(CycleWorld world) {
        List<(string Owner, CyclePredictionKind Kind, string Target, long Due, long Generation, int Sequence)> expected = [
            ("light", CyclePredictionKind.ChangeLight, "world", world.Light.NextChange.Ticks, 0, 0)
        ];
        if (world.Script.NextIndex < world.Script.Entries.Count) {
            CycleDestruction entry = world.Script.Entries[world.Script.NextIndex];
            expected.Add(("script", CyclePredictionKind.DestroyResource, entry.ResourceId, entry.Due.Ticks, world.Script.NextIndex, 0));
        }
        foreach (CycleResource resource in world.Resources) {
            if (resource.Stock < resource.Capacity && resource.ActiveSince is { } started) {
                Int128 due = (Int128)started.Ticks + resource.RegenerationTicks - resource.AccumulatedTicks;
                if (due <= long.MaxValue) {
                    expected.Add(($"resource:{resource.Id}:regen", CyclePredictionKind.RegenerateResource, resource.Id,
                        (long)due, resource.Generation, 0));
                }
            }
        }
        foreach (CycleCharacter actor in world.Characters) {
            if (actor.Activity is { } activity) {
                expected.Add(($"actor:{actor.Id}:activity", CyclePredictionKind.CompleteActivity, actor.Id,
                    activity.Due.Ticks, activity.Id, activity.CycleIndex));
            }
            if (actor.Activity is null || actor.Notices.Count != 0) {
                expected.Add(($"actor:{actor.Id}:attention", CyclePredictionKind.RequestDecision, actor.Id,
                    world.Time.Ticks, actor.DecisionGeneration, 0));
            }
        }
        Assert.Equal(expected.OrderBy(p => p.Owner, StringComparer.Ordinal), world.Forecasts.Predictions
            .Select(p => (p.OwnerId, p.Kind, p.TargetId, p.Due.Ticks, p.Generation, p.Sequence))
            .OrderBy(p => p.OwnerId, StringComparer.Ordinal));
        Assert.Equal(CycleEngine.Rebuild(world.Business, world.Time).Forecasts.ToString(), world.Forecasts.ToString());
    }

    internal sealed class StubCharacterController(Func<CycleObservation, string, CycleCommand> choose) : ICycleCharacterController {
        public int Calls { get; private set; }
        public List<(string Actor, ModelTime Time)> Requests { get; } = [];
        public ValueTask<CycleCommand> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            Requests.Add((context.Observation.Actor.Id, context.Observation.Time));
            return ValueTask.FromResult(choose(context.Observation, context.Observation.Actor.Id));
        }
    }
    public static StubCharacterController Commands(params CycleCommand[] commands) {
        Queue<CycleCommand> remaining = new(commands);
        return new((_, _) => remaining.Count > 0 ? remaining.Dequeue() : throw new InvalidOperationException("Unexpected Player request."));
    }
    internal sealed class TemporaryStore : IDisposable {
        private readonly string _parent = Path.Combine(Path.GetTempPath(), "DramaBoard.Gathering.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string StorePath => Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}

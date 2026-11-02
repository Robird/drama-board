using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Tests;

internal static class PlaygroundTestSupport {
    public static RateBreakdown Rate(int amount, string source = "fixture") =>
        new([new(source, "测试来源", amount)]);
    public static ModelTime Hour(long hours) => new(hours * PlaygroundTime.Hour);
    public static ModelTime Minute(long minutes) => new(minutes * 60_000);
    public static KernelCursor InitialCursor(PlaygroundWorld world) => new(new WorldVersion(17, 0), world.Time, null, null);
    public static InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> Memory(PlaygroundWorld world) => new(world, InitialCursor(world));
    public static PlaygroundWorld Solo(ModelTime? start = null, IEnumerable<PlaygroundResource>? resources = null, int skill = 0) =>
        PlaygroundWorld.CreatePlayable(start, [new("a", "Alice", skill)], resources);
    public static PlaygroundResource Sun(ModelTime start, int stock = 3, int capacity = 5,
        long regenerationTicks = 3 * PlaygroundTime.Hour, long harvestTicks = PlaygroundTime.Hour, int requiredLevel = 0) =>
        new("sunflower", "Sunflower", "item.sunflower", true, regenerationTicks, capacity, stock,
            stock < capacity && PlaygroundTime.IsDay(start) ? start : null, harvestTicks: harvestTicks, requiredLevel: requiredLevel);
    public static PlaygroundResource Moon(ModelTime start, int stock = 3, int capacity = 5) =>
        new("moongrass", "Moongrass", "item.moongrass", false, 4 * PlaygroundTime.Hour, capacity, stock,
            stock < capacity && !PlaygroundTime.IsDay(start) ? start : null);
    public static async Task RunUntil(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, ModelTime boundary) {
        for (int steps = 0; steps < 1_000; steps++) {
            if (await kernel.StepAsync(boundary) != StepStatus.Committed) { return; }
            AssertPredictions(kernel.World);
        }
        Assert.Fail("The finite horizon exceeded 1,000 occurrences.");
    }
    public static async Task Step(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        AssertPredictions(kernel.World);
    }
    // Physics-focused tests may pass the separately committed opening, but no world event.
    public static async Task StepWorldEvent(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        do {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
            if (kernel.World.Expedition is null) { AssertPredictions(kernel.World); }
            else { Assert.Equal(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString()); }
        } while (kernel.LastCompletion!.Event.Facts.All(fact => fact.Kind == PlaygroundEventKind.DecisionContextOpened));
    }
    public static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> stop) {
        for (int i = 0; i < 300; i++) {
            if (stop(kernel.World)) { return; }
            await Step(kernel);
        }
        Assert.Fail("Expected business boundary was not reached.");
    }
    public static string Describe(OccurrenceEvent<PlaygroundFact> occurrence) =>
        $"{occurrence.CauseKey}|{occurrence.TargetInstant}|{string.Join("\n", occurrence.Facts.Select(fact => fact.ToString()))}";
    // Test-only semantic fingerprint. It follows public material rather than renderer bytes or
    // production equality, and handles the conditional Number accessor without evaluating it.
    public static string MaterialSnapshot(object? value) => System.Text.Json.JsonSerializer.Serialize(Material(value));
    private static object? Material(object? value) {
        if (value is null) { return null; }
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal) { return value; }
        if (value is PlaygroundVisibleValue visible) {
            return new { visible.Kind, visible.Units, visible.Scale, visible.Unit, visible.Text, visible.Boolean };
        }
        if (value is System.Collections.IDictionary dictionary) {
            return dictionary.Keys.Cast<object>().OrderBy(key => key.ToString(), StringComparer.Ordinal)
                .ToDictionary(key => key.ToString()!, key => Material(dictionary[key]), StringComparer.Ordinal);
        }
        if (value is System.Collections.IEnumerable entries) { return entries.Cast<object?>().Select(Material).ToArray(); }
        return type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(property => property.Name, property => Material(property.GetValue(value)), StringComparer.Ordinal);
    }
    public static long Recalculations(PlaygroundMetrics metrics, string owner) =>
        metrics.RecalculationsByOwner.TryGetValue(owner, out long count) ? count : 0;

    // Independent discovery/formula oracle: do not call Owners/Predict or consult routing.
    public static void AssertPredictions(PlaygroundWorld world) {
        List<(string Owner, PlaygroundPredictionKind Kind, string Target, long Due, long Generation, int Sequence)> expected = [
            ("light", PlaygroundPredictionKind.ChangeLight, "world", world.Light.NextChange.Ticks, 0, 0)
        ];
        if (world.Script.NextIndex < world.Script.Entries.Count) {
            PlaygroundDestruction entry = world.Script.Entries[world.Script.NextIndex];
            expected.Add(("script", PlaygroundPredictionKind.DestroyResource, entry.ResourceId, entry.Due.Ticks, world.Script.NextIndex, 0));
        }
        foreach (PlaygroundResource resource in world.Resources) {
            if (resource.Stock < resource.Capacity && resource.ActiveSince is { } started) {
                Int128 due = (Int128)started.Ticks + resource.RegenerationTicks - resource.AccumulatedTicks;
                if (due <= long.MaxValue) {
                    expected.Add(($"resource:{resource.Id}:regen", PlaygroundPredictionKind.RegenerateResource, resource.Id,
                        (long)due, resource.Generation, 0));
                }
            }
        }
        foreach (PlaygroundCharacter actor in world.Characters) {
            if (actor.Activity is { } activity) {
                expected.Add(($"actor:{actor.Id}:activity", PlaygroundPredictionKind.CompleteActivity, actor.Id,
                    activity.Due.Ticks, activity.Id, activity.CycleIndex));
            }
            if (actor.Activity is null || actor.Notices.Count != 0) {
                expected.Add(($"actor:{actor.Id}:attention", PlaygroundPredictionKind.RequestDecision, actor.Id,
                    world.Time.Ticks, actor.DecisionGeneration, 0));
            }
        }
        Assert.Equal(expected.OrderBy(p => p.Owner, StringComparer.Ordinal), world.Forecasts.Predictions
            .Select(p => (p.OwnerId, p.Kind, p.TargetId, p.Due.Ticks, p.Generation, p.Sequence))
            .OrderBy(p => p.OwnerId, StringComparer.Ordinal));
        Assert.Equal(PlaygroundEngine.Rebuild(world.Business, world.Time).Forecasts.ToString(), world.Forecasts.ToString());
    }

    internal sealed class StubCharacterController(Func<PlaygroundObservation, string, PlaygroundCommand> choose) : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public List<(string Actor, ModelTime Time)> Requests { get; } = [];
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            Requests.Add((context.Observation.Actor.Id, context.Observation.Time));
            return ValueTask.FromResult(Operation(choose(context.Observation, context.Observation.Actor.Id), Calls));
        }
    }
    public static PlaygroundControllerOperation Operation(PlaygroundCommand command, int ordinal = 1) =>
        new($"call_fixture_{ordinal}", PlaygroundCommandParser.Format(command), $"fixture/{ordinal}");
    public static StubCharacterController Commands(params PlaygroundCommand[] commands) {
        Queue<PlaygroundCommand> remaining = new(commands);
        return new((_, _) => remaining.Count > 0 ? remaining.Dequeue() : throw new InvalidOperationException("Unexpected Player request."));
    }
    internal sealed class TemporaryStore : IDisposable {
        private readonly string _parent = Path.Combine(Path.GetTempPath(), "DramaBoard.Gathering.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string StorePath => Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}

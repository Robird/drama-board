using Atelia.DurableGraph;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Concrete planned changes. Replay applies these values and never calls business handlers.</summary>
[DurableType("DramaBoard.ResourceCycle.Fact", 2)]
public sealed partial class CycleFact : IDurableObject {
    [DurableField(1)] private CycleEventKind _kind;
    public CycleEventKind Kind => _kind;
    [DurableField(2)] private ModelTime _at;
    public ModelTime At => _at;
    [DurableField(3)] private string _targetId;
    public string TargetId => _targetId;
    [DurableField(4)] private CycleLight? _light;
    public CycleLight? Light => _light;
    [DurableField(5)] private List<CyclePlant> _changedPlants = [];
    [DurableField(6)] private CycleScript? _script;
    public CycleScript? Script => _script;
    [DurableField(7)] private CycleForecasts? _forecasts;
    public CycleForecasts? Forecasts => _forecasts;
    [DurableField(8)] private CycleCharacter? _character;
    public CycleCharacter? Character => _character;
    public IReadOnlyList<CyclePlant> ChangedPlants => _changedPlants.AsReadOnly();
    internal CycleFact(CycleEventKind kind, ModelTime at, string targetId, CycleLight? light,
        IEnumerable<CyclePlant> changedPlants, CycleScript? script, CycleForecasts? forecasts, CycleCharacter? character = null) {
        _kind = kind; _at = at; _targetId = targetId; _light = light; _changedPlants = [.. changedPlants]; _script = script; _forecasts = forecasts; _character = character;
    }
    public override string ToString() => $"{Kind}@{At.Ticks}:{TargetId};light={Light?.ToString() ?? "-"};plants=[{string.Join(';', _changedPlants)}];script={Script?.ToString() ?? "-"};forecasts={Forecasts?.ToString() ?? "-"}" +
        (Character is null ? "" : $";character={Character}");
}

/// <summary>Measurements of this process's planning work, not durable world state.</summary>
public sealed class CycleMetrics {
    private readonly Dictionary<string, long> _recalculationsByOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastReasons = new(StringComparer.Ordinal);
    public long Recalculations { get; private set; }
    public long PredictionChecks { get; internal set; }
    public long RoutedNotifications { get; internal set; }
    public long Invalidations { get; internal set; }
    public IReadOnlyDictionary<string, long> RecalculationsByOwner => _recalculationsByOwner;
    public IReadOnlyDictionary<string, string> LastReasons => _lastReasons;
    internal void Count(string owner, string reason) {
        Recalculations++;
        _recalculationsByOwner[owner] = _recalculationsByOwner.GetValueOrDefault(owner) + 1;
        _lastReasons[owner] = reason;
    }
}

public static class CycleEngine {
    public static SimulationKernel<CycleWorld, CyclePrediction, CycleFact> CreateKernel(
        IOccurrenceHistory<CycleWorld, CycleFact> history, PredictionMode mode = PredictionMode.Incremental, CycleMetrics? metrics = null,
        ICycleDecisionSource? decisionSource = null) {
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        return new(history, new SimulationRules(0, 1024), [new CycleRule(mode, metrics ?? new(), decisionSource)],
            Fold, (_, _, _, folded) => folded, (world, _) => world.Validate());
    }
    public static CycleWorld Fold(CycleWorld world, LogicalInstant instant, CycleFact fact) {
        if (fact.At != instant.ModelTime || fact.At < world.Time) {
            throw new InvalidOperationException("A fact must apply at its recorded time and cannot move time backwards.");
        }
        return world.Replace(fact.At, fact.Light, fact.ChangedPlants, fact.Script, fact.Forecasts, fact.Character);
    }
    internal static IEnumerable<string> Owners(CycleWorld world) => new[] { "light", "script" }.Concat(world.Plants.Select(p => $"plant:{p.Id}"))
        .Concat(world.Character is { } actor ? [$"actor:{actor.Id}"] : Array.Empty<string>());
    internal static CyclePrediction? Predict(CycleWorld world, string owner) {
        if (owner == "light") { return new(owner, CyclePredictionKind.ChangeLight, "world", world.Light.NextChange, 0); }
        if (owner == "script") {
            if (world.Script.NextIndex == world.Script.Entries.Count) { return null; }
            CycleDestruction entry = world.Script.Entries[world.Script.NextIndex];
            return new(owner, CyclePredictionKind.DestroyPlant, entry.PlantId, entry.Due, world.Script.NextIndex);
        }
        if (owner.StartsWith("actor:", StringComparison.Ordinal)) {
            CycleCharacter actor = world.Character ?? throw new InvalidOperationException("The actor predictor requires a character.");
            if (owner != $"actor:{actor.Id}") { throw new InvalidOperationException("Unknown actor predictor."); }
            return actor.WaitUntil is { } until
                ? new(owner, CyclePredictionKind.CompleteWait, actor.Id, until, actor.DecisionGeneration)
                : new(owner, CyclePredictionKind.RequestDecision, actor.Id, world.Time, actor.DecisionGeneration);
        }
        CyclePlant plant = world.GetPlant(owner["plant:".Length..]);
        if (plant.IsMature || plant.ActiveSince is not { } start) { return null; }
        return new(owner, CyclePredictionKind.MaturePlant, plant.Id,
            new ModelTime(checked(start.Ticks + plant.RequiredTicks - plant.AccumulatedTicks)), plant.Generation);
    }

    private sealed class CycleRule(PredictionMode mode, CycleMetrics metrics, ICycleDecisionSource? decisionSource)
        : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        private object? _bindingIdentity;
        private Dictionary<(CycleEventKind, string), CycleBinding[]> _index = [];

        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) {
            if (world.Forecasts.Predictions.Count == 0) { return []; }
            // Application priority precedes Kernel's seeded tie arbitration; owners of equal priority are stable-keyed.
            ModelTime due = world.Forecasts.Predictions.Min(p => p.Due);
            CyclePredictionKind kind = world.Forecasts.Predictions.Where(p => p.Due == due).Min(p => p.Kind);
            return world.Forecasts.Predictions.Where(p => p.Due == due && p.Kind == kind)
                .Select(p => new OccurrenceCandidate<CyclePrediction>(Key(p), new CandidateDue(p.Due), p)).ToArray();
        }

        public async ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            CyclePrediction prediction = winner.Data;
            metrics.PredictionChecks++;
            CyclePrediction? expected = Predict(world, prediction.OwnerId);
            if (prediction.Due < world.Time || winner.Due.ModelTime != prediction.Due || winner.Key != Key(prediction) ||
                !world.Forecasts.Predictions.Any(p => p.ToString() == prediction.ToString()) ||
                expected?.ToString() != prediction.ToString()) {
                throw new InvalidOperationException("A stale or invalid cycle prediction cannot execute.");
            }
            ModelTime time = prediction.Due;
            CycleLight? light = null;
            CycleScript? script = null;
            CycleCharacter? character = null;
            List<CyclePlant> changedPlants = [];
            string targetId = prediction.TargetId;
            CycleEventKind kind;
            switch (prediction.Kind) {
                case CyclePredictionKind.ChangeLight:
                    kind = CycleEventKind.LightChanged;
                    light = new(!world.Light.IsDay, CycleTime.NextLightChange(time));
                    foreach (CyclePlant plant in world.Plants.Where(p => !p.IsMature)) {
                        changedPlants.Add(new(plant.Id, plant.GrowsInDaylight, plant.RequiredTicks, plant.ProgressAt(time),
                            plant.GrowsInDaylight == light.IsDay ? time : null, false, plant.Generation));
                    }
                    break;
                case CyclePredictionKind.MaturePlant:
                    kind = CycleEventKind.PlantMatured;
                    CyclePlant mature = world.GetPlant(prediction.TargetId);
                    changedPlants.Add(new(mature.Id, mature.GrowsInDaylight, mature.RequiredTicks, mature.RequiredTicks, null, true, mature.Generation));
                    break;
                case CyclePredictionKind.DestroyPlant:
                    kind = CycleEventKind.PlantDestroyed;
                    CyclePlant destroyed = world.GetPlant(prediction.TargetId);
                    changedPlants.Add(new(destroyed.Id, destroyed.GrowsInDaylight, destroyed.RequiredTicks, 0,
                        destroyed.GrowsInDaylight == world.Light.IsDay ? time : null, false, checked(destroyed.Generation + 1)));
                    script = new(world.Script.Entries, checked(world.Script.NextIndex + 1));
                    break;
                case CyclePredictionKind.CompleteWait:
                    kind = CycleEventKind.WaitCompleted;
                    CycleCharacter waiting = world.Character ?? throw new InvalidOperationException("Missing waiting character.");
                    character = new(waiting.Id, waiting.Backpack, decisionGeneration: waiting.DecisionGeneration,
                        lastResult: $"等待完成，现在是 {time}。");
                    break;
                case CyclePredictionKind.RequestDecision:
                    if (decisionSource is null) {
                        throw new InvalidOperationException("A playable world requires an ICycleDecisionSource to continue at a decision boundary.");
                    }
                    CycleCharacter actor = world.Character ?? throw new InvalidOperationException("Missing deciding character.");
                    // No event or state is published while the runtime input source is waiting.
                    CycleCommand command = await decisionSource.ChooseAsync(world, actor.Id, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    string? rejection = CycleActions.Check(world, actor.Id, command);
                    if (rejection is not null) { throw new InvalidOperationException(rejection); }
                    long generation = checked(actor.DecisionGeneration + 1);
                    if (command.Kind == CycleCommandKind.Wait) {
                        kind = CycleEventKind.WaitStarted;
                        ModelTime until = new(checked(time.Ticks + command.Hours * CycleTime.Hour));
                        character = new(actor.Id, actor.Backpack, until, generation, $"开始等待 {command.Hours} 小时，预计 {until} 完成。");
                    } else {
                        kind = CycleEventKind.PlantGathered;
                        CyclePlant gathered = world.GetPlant(command.PlantId!);
                        targetId = gathered.Id;
                        changedPlants.Add(new(gathered.Id, gathered.GrowsInDaylight, gathered.RequiredTicks, 0,
                            gathered.GrowsInDaylight == world.Light.IsDay ? time : null, false, checked(gathered.Generation + 1)));
                        character = new(actor.Id, actor.Backpack.Add(gathered.Id), decisionGeneration: generation,
                            lastResult: $"采集 {gathered.Id}，背包 +1。资源开始新一轮生长。");
                    }
                    break;
                default: throw new InvalidOperationException("Unknown prediction action.");
            }
            CycleWorld changed = world.Replace(time, light, changedPlants, script, character: character);
            HashSet<string> dirty = new(StringComparer.Ordinal);
            if (mode == PredictionMode.Full) {
                dirty.UnionWith(Owners(changed));
            } else {
                EnsureIndex(world.Forecasts);
                foreach (CycleBinding binding in FindBindings(kind, targetId)) {
                    metrics.RoutedNotifications++;
                    // Fine filter: a mature plant does not depend on light anymore.
                    bool affected = binding.OwnerId is "light" or "script" ||
                        (binding.OwnerId.StartsWith("actor:", StringComparison.Ordinal)
                            ? !ReferenceEquals(world.Character, changed.Character)
                            : !ReferenceEquals(world.GetPlant(binding.OwnerId["plant:".Length..]), changed.GetPlant(binding.OwnerId["plant:".Length..])));
                    if (affected) { metrics.Invalidations++; dirty.Add(binding.OwnerId); }
                }
                // Consumption invalidates its producer even if a future new action has no event binding.
                dirty.Add(prediction.OwnerId);
            }
            Dictionary<string, CyclePrediction> predictions = world.Forecasts.Predictions.ToDictionary(p => p.OwnerId, StringComparer.Ordinal);
            foreach (string owner in dirty.Order(StringComparer.Ordinal)) {
                metrics.Count(owner, $"{kind}:{targetId}@{time}");
                CyclePrediction? next = Predict(changed, owner);
                if (next is null) { predictions.Remove(owner); }
                else if (!predictions.TryGetValue(owner, out CyclePrediction? previous) || previous.ToString() != next.ToString()) { predictions[owner] = next; }
            }
            bool forecastsChanged = predictions.Count != world.Forecasts.Predictions.Count ||
                world.Forecasts.Predictions.Any(p => !predictions.TryGetValue(p.OwnerId, out CyclePrediction? current) || !ReferenceEquals(p, current));
            CycleForecasts? forecasts = forecastsChanged ? world.Forecasts.WithPredictions(predictions.Values) : null;
            CycleFact fact = new(kind, time, targetId, light, changedPlants, script, forecasts, character);
            return new TransitionDraft<CycleFact>([fact]);
        }
        private static CandidateKey Key(CyclePrediction prediction) => new(prediction.ToString());
        private void EnsureIndex(CycleForecasts forecasts) {
            if (ReferenceEquals(_bindingIdentity, forecasts.BindingIdentity)) { return; }
            _index = forecasts.Bindings.GroupBy(b => (b.EventKind, b.TargetId))
                .ToDictionary(g => g.Key, g => g.OrderBy(b => b.OwnerId, StringComparer.Ordinal).ToArray());
            _bindingIdentity = forecasts.BindingIdentity;
        }
        private IEnumerable<CycleBinding> FindBindings(CycleEventKind kind, string target) =>
            _index.GetValueOrDefault((kind, target), []).Concat(_index.GetValueOrDefault((kind, "*"), []));
    }
}

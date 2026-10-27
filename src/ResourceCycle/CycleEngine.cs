using Atelia.DurableGraph;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Concrete planned changes. Replay applies these values and never calls business handlers.</summary>
[DurableType("DramaBoard.ResourceCycle.Fact", 1)]
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
    [DurableField(7)] private CycleCharacter? _character;
    public CycleCharacter? Character => _character;
    public IReadOnlyList<CyclePlant> ChangedPlants => _changedPlants.AsReadOnly();
    internal CycleFact(CycleEventKind kind, ModelTime at, string targetId, CycleLight? light,
        IEnumerable<CyclePlant> changedPlants, CycleScript? script, CycleCharacter? character = null) {
        _kind = kind; _at = at; _targetId = targetId; _light = light; _changedPlants = [.. changedPlants]; _script = script; _character = character;
    }
    public override string ToString() => $"{Kind}@{At.Ticks}:{TargetId};light={Light?.ToString() ?? "-"};plants=[{string.Join(';', _changedPlants)}];script={Script?.ToString() ?? "-"}" +
        (Character is null ? "" : $";character={Character}");
}

/// <summary>Runtime planning and cache-maintenance diagnostics; never durable business state.</summary>
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
        CycleMetrics measurements = metrics ?? new();
        return new(history, new SimulationRules(0, 1024), [new CycleRule(measurements, decisionSource)],
            Fold, (before, instant, facts, folded) => Complete(before, instant, facts, folded, mode, measurements), Validate);
    }
    /// <summary>Reconstructs all fixed routing and predictions from persistent business truth.</summary>
    public static CycleWorld Rebuild(CycleBusinessState business, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(business);
        business.Validate();
        List<CycleBinding> bindings = [new("light", CycleEventKind.LightChanged, "world"), new("script", CycleEventKind.PlantDestroyed, "*")];
        foreach (CyclePlant plant in business.Plants) {
            string owner = $"plant:{plant.Id}";
            bindings.Add(new(owner, CycleEventKind.LightChanged, "world"));
            bindings.Add(new(owner, CycleEventKind.PlantDestroyed, plant.Id));
            bindings.Add(new(owner, CycleEventKind.PlantMatured, plant.Id));
            if (business.Character is not null) { bindings.Add(new(owner, CycleEventKind.PlantGathered, plant.Id)); }
        }
        if (business.Character is { } actor) {
            string owner = $"actor:{actor.Id}";
            bindings.Add(new(owner, CycleEventKind.WaitStarted, actor.Id));
            bindings.Add(new(owner, CycleEventKind.WaitCompleted, actor.Id));
            bindings.Add(new(owner, CycleEventKind.PlantGathered, "*"));
        }
        var world = new CycleWorld(business, new([], bindings));
        world = new(business, world.Forecasts.WithPredictions(Owners(world).Select(owner => Predict(world, owner)).OfType<CyclePrediction>()));
        Validate(world, boundaryTime);
        return world;
    }
    /// <summary>Applies recorded business effects, leaving the preceding cache as scratch until Complete.</summary>
    public static CycleWorld Fold(CycleWorld world, LogicalInstant instant, CycleFact fact) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.At != instant.ModelTime || fact.At < world.Time) {
            throw new InvalidOperationException("A fact must apply at its recorded time and cannot move time backwards.");
        }
        ValidateFact(world, fact);
        return new(world.Business.Replace(fact.At, fact.Light, fact.ChangedPlants, fact.Script, fact.Character), world.Forecasts);
    }
    /// <summary>Collects impacts across ordered facts and recomputes each affected owner once from final Business.</summary>
    public static CycleWorld Complete(CycleWorld before, LogicalInstant instant, IReadOnlyList<CycleFact> facts,
        CycleWorld folded) => Complete(before, instant, facts, folded, PredictionMode.Incremental);

    public static CycleWorld Complete(CycleWorld before, LogicalInstant instant, IReadOnlyList<CycleFact> facts,
        CycleWorld folded, PredictionMode mode, CycleMetrics? metrics = null) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(folded);
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        CycleWorld intermediate = before;
        foreach (CycleFact fact in facts) {
            CycleWorld next = Fold(intermediate, instant, fact);
            if (mode == PredictionMode.Incremental) {
                foreach (CycleBinding binding in before.Forecasts.FindBindings(fact.Kind, fact.TargetId)) {
                    if (metrics is not null) { metrics.RoutedNotifications++; }
                    bool affected = binding.OwnerId is "light" or "script" ||
                        (binding.OwnerId.StartsWith("actor:", StringComparison.Ordinal)
                            ? !ReferenceEquals(intermediate.Character, next.Character)
                            : !ReferenceEquals(intermediate.GetPlant(binding.OwnerId["plant:".Length..]), next.GetPlant(binding.OwnerId["plant:".Length..])));
                    if (affected) {
                        if (metrics is not null) { metrics.Invalidations++; }
                        dirty.Add(binding.OwnerId);
                    }
                }
                // A consumed producer must be reconsidered even when its old output was its only dependency.
                dirty.Add(ConsumedOwner(intermediate, fact));
            }
            intermediate = next;
        }
        if (!HasSameFoldedBusiness(intermediate.Business, folded.Business)) {
            throw new InvalidOperationException("The folded cycle business does not match the recorded fact sequence.");
        }
        if (mode == PredictionMode.Full) { dirty.UnionWith(Owners(folded)); }
        Dictionary<string, CyclePrediction> predictions = before.Forecasts.Predictions.ToDictionary(p => p.OwnerId, StringComparer.Ordinal);
        foreach (string owner in dirty.Order(StringComparer.Ordinal)) {
            metrics?.Count(owner, $"complete@{instant.ModelTime}");
            CyclePrediction? next = Predict(folded, owner);
            if (next is null) { predictions.Remove(owner); }
            else if (!predictions.TryGetValue(owner, out CyclePrediction? previous) || previous.ToString() != next.ToString()) { predictions[owner] = next; }
        }
        bool changed = predictions.Count != before.Forecasts.Predictions.Count ||
            before.Forecasts.Predictions.Any(p => !predictions.TryGetValue(p.OwnerId, out CyclePrediction? current) || !ReferenceEquals(p, current));
        CycleForecasts forecasts = changed ? before.Forecasts.WithPredictions(predictions.Values) : before.Forecasts;
        return ReferenceEquals(forecasts, folded.Forecasts) ? folded : new(folded.Business, forecasts);
    }
    // Re-folding the same facts creates another container but retains the exact recorded child objects.
    private static bool HasSameFoldedBusiness(CycleBusinessState expected, CycleBusinessState actual) =>
        expected.Time == actual.Time && ReferenceEquals(expected.Light, actual.Light) &&
        ReferenceEquals(expected.Script, actual.Script) && ReferenceEquals(expected.Character, actual.Character) &&
        expected.Plants.Count == actual.Plants.Count &&
        expected.Plants.Zip(actual.Plants).All(pair => ReferenceEquals(pair.First, pair.Second));

    /// <summary>Checks business and existing cache outputs; full-rebuild comparisons independently check completeness.</summary>
    public static void Validate(CycleWorld world, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(world);
        world.Business.Validate();
        if (world.Time != boundaryTime) { throw new InvalidOperationException("The business clock does not match the publication boundary."); }
        string? previous = null;
        foreach (CyclePrediction prediction in world.Forecasts.Predictions) {
            if (prediction.Due < boundaryTime || !Enum.IsDefined(prediction.Kind) ||
                (previous is not null && StringComparer.Ordinal.Compare(previous, prediction.OwnerId) >= 0) ||
                !HasValidIdentity(world, prediction)) {
                throw new InvalidOperationException("Invalid prediction set.");
            }
            previous = prediction.OwnerId;
        }
    }
    private static bool HasValidIdentity(CycleWorld world, CyclePrediction prediction) => prediction.Kind switch {
        CyclePredictionKind.ChangeLight => prediction.OwnerId == "light" && prediction.TargetId == "world" && prediction.Generation == 0,
        CyclePredictionKind.DestroyPlant => prediction.OwnerId == "script" && prediction.Generation == world.Script.NextIndex &&
            world.Script.NextIndex < world.Script.Entries.Count && world.Script.Entries[world.Script.NextIndex].PlantId == prediction.TargetId,
        CyclePredictionKind.MaturePlant => prediction.OwnerId == $"plant:{prediction.TargetId}" &&
            world.Plants.Any(plant => plant.Id == prediction.TargetId && plant.Generation == prediction.Generation && !plant.IsMature && plant.ActiveSince is not null),
        CyclePredictionKind.CompleteWait or CyclePredictionKind.RequestDecision => world.Character is { } actor &&
            prediction.OwnerId == $"actor:{actor.Id}" && prediction.TargetId == actor.Id && prediction.Generation == actor.DecisionGeneration &&
            (prediction.Kind == CyclePredictionKind.CompleteWait) == (actor.WaitUntil is not null),
        _ => false
    };
    private static string ConsumedOwner(CycleWorld world, CycleFact fact) => fact.Kind switch {
        CycleEventKind.LightChanged => "light",
        CycleEventKind.PlantDestroyed => "script",
        CycleEventKind.PlantMatured => $"plant:{fact.TargetId}",
        CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted or CycleEventKind.PlantGathered => $"actor:{world.Character!.Id}",
        _ => throw new InvalidOperationException("Unknown cycle event kind.")
    };

    // Validate recorded effect identities and shape, without asking the Player or planning the action again.
    private static void ValidateFact(CycleWorld world, CycleFact fact) {
        if (!Enum.IsDefined(fact.Kind) || string.IsNullOrWhiteSpace(fact.TargetId) ||
            fact.ChangedPlants.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != fact.ChangedPlants.Count ||
            fact.ChangedPlants.Any(p => !world.Plants.Any(old => old.Id == p.Id)) ||
            (fact.Character is not null && fact.Character.Id != world.Character?.Id)) {
            throw new InvalidOperationException("A cycle fact contains an unknown kind, identity or duplicate plant effect.");
        }
        bool onePlant = fact.ChangedPlants.Count == 1 && fact.ChangedPlants[0].Id == fact.TargetId;
        bool actorTarget = world.Character is { } actor && fact.TargetId == actor.Id;
        bool valid = fact.Kind switch {
            CycleEventKind.LightChanged => fact.TargetId == "world" && fact.Light is not null &&
                fact.Script is null && fact.Character is null,
            CycleEventKind.PlantMatured => onePlant && fact.Light is null && fact.Script is null && fact.Character is null,
            CycleEventKind.PlantDestroyed => onePlant && fact.Light is null && fact.Script is not null && fact.Character is null,
            CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted => actorTarget && fact.Character is not null &&
                fact.Light is null && fact.Script is null && fact.ChangedPlants.Count == 0,
            CycleEventKind.PlantGathered => onePlant && fact.Character is not null && fact.Light is null && fact.Script is null,
            _ => false
        };
        if (!valid) { throw new InvalidOperationException("The recorded effects do not match the cycle event identity and shape."); }
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
        if (!owner.StartsWith("plant:", StringComparison.Ordinal)) { throw new InvalidOperationException("Unknown prediction owner."); }
        CyclePlant plant = world.GetPlant(owner["plant:".Length..]);
        if (plant.IsMature || plant.ActiveSince is not { } start) { return null; }
        return new(owner, CyclePredictionKind.MaturePlant, plant.Id,
            new ModelTime(checked(start.Ticks + plant.RequiredTicks - plant.AccumulatedTicks)), plant.Generation);
    }

    private sealed class CycleRule(CycleMetrics metrics, ICycleDecisionSource? decisionSource)
        : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
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
            CycleFact fact = new(kind, time, targetId, light, changedPlants, script, character);
            return new TransitionDraft<CycleFact>([fact]);
        }
        private static CandidateKey Key(CyclePrediction prediction) => new(prediction.ToString());
    }
}

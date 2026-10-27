using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.EventKind", 2)]
public enum CycleEventKind { PlantMatured, LightChanged, PlantDestroyed, WaitStarted, WaitCompleted, PlantGathered }
public enum CyclePredictionKind { MaturePlant, ChangeLight, DestroyPlant, CompleteWait, RequestDecision }
public enum PredictionMode { Incremental, Full }

public static class CycleTime {
    public const long Hour = 3_600_000;
    public const long Day = 24 * Hour;
    public static bool IsDay(ModelTime time) => time.Ticks % Day >= 6 * Hour && time.Ticks % Day < 18 * Hour;
    public static ModelTime NextLightChange(ModelTime time) {
        long day = time.Ticks / Day * Day;
        long offset = time.Ticks % Day;
        return new ModelTime(checked(day + (offset < 6 * Hour ? 6 * Hour : offset < 18 * Hour ? 18 * Hour : Day + 6 * Hour)));
    }
}

[DurableType("DramaBoard.ResourceCycle.Light", 1)]
public sealed partial class CycleLight : IDurableObject {
    [DurableField(1)] private bool _isDay;
    public bool IsDay => _isDay;
    [DurableField(2)] private ModelTime _nextChange;
    public ModelTime NextChange => _nextChange;
    public CycleLight(bool isDay, ModelTime nextChange) { _isDay = isDay; _nextChange = nextChange; }
    public override string ToString() => $"{IsDay}:{NextChange.Ticks}";
}

[DurableType("DramaBoard.ResourceCycle.Plant", 1)]
public sealed partial class CyclePlant : IDurableObject {
    [DurableField(1)] private string _id;
    public string Id => _id;
    [DurableField(2)] private bool _growsInDaylight;
    public bool GrowsInDaylight => _growsInDaylight;
    [DurableField(3)] private long _requiredTicks;
    public long RequiredTicks => _requiredTicks;
    [DurableField(4)] private long _accumulatedTicks;
    public long AccumulatedTicks => _accumulatedTicks;
    [DurableField(5)] private ModelTime? _activeSince;
    public ModelTime? ActiveSince => _activeSince;
    [DurableField(6)] private bool _isMature;
    public bool IsMature => _isMature;
    [DurableField(7)] private long _generation;
    public long Generation => _generation;
    internal CyclePlant(string id, bool growsInDaylight, long requiredTicks, long accumulatedTicks,
        ModelTime? activeSince, bool isMature, long generation) {
        _id = id; _growsInDaylight = growsInDaylight; _requiredTicks = requiredTicks;
        _accumulatedTicks = accumulatedTicks; _activeSince = activeSince; _isMature = isMature; _generation = generation;
    }
    public long ProgressAt(ModelTime time) => IsMature ? RequiredTicks : Math.Min(RequiredTicks,
        checked(AccumulatedTicks + (ActiveSince is { } since ? time.Ticks - since.Ticks : 0)));
    public override string ToString() => $"{Id}:{GrowsInDaylight}:{RequiredTicks}:{AccumulatedTicks}:{ActiveSince?.Ticks.ToString() ?? "-"}:{IsMature}:{Generation}";
}

[DurableType("DramaBoard.ResourceCycle.Destruction", 1)]
public sealed partial class CycleDestruction : IDurableObject {
    [DurableField(1)] private string _plantId;
    public string PlantId => _plantId;
    [DurableField(2)] private ModelTime _due;
    public ModelTime Due => _due;
    public CycleDestruction(string plantId, ModelTime due) { _plantId = plantId; _due = due; }
    public override string ToString() => $"{PlantId}:{Due.Ticks}";
}

[DurableType("DramaBoard.ResourceCycle.Script", 1)]
public sealed partial class CycleScript : IDurableObject {
    [DurableField(1)] private List<CycleDestruction> _entries = [];
    [DurableField(2)] private int _nextIndex;
    public int NextIndex => _nextIndex;
    public IReadOnlyList<CycleDestruction> Entries => _entries.AsReadOnly();
    internal CycleScript(IEnumerable<CycleDestruction> entries, int nextIndex = 0) { _entries = [.. entries]; _nextIndex = nextIndex; }
    public override string ToString() => $"{NextIndex}[{string.Join(';', _entries)}]";
}

/// <summary>A fixed runtime routing entry; owners remain subscribed while they have no prediction.</summary>
public sealed class CycleBinding(string ownerId, CycleEventKind eventKind, string targetId) {
    public string OwnerId { get; } = ownerId;
    public CycleEventKind EventKind { get; } = eventKind;
    public string TargetId { get; } = targetId;
    public override string ToString() => $"{OwnerId}:{EventKind}:{TargetId}";
}

public sealed class CyclePrediction {
    public string OwnerId { get; }
    public CyclePredictionKind Kind { get; }
    public string TargetId { get; }
    public ModelTime Due { get; }
    public long Generation { get; }
    internal CyclePrediction(string ownerId, CyclePredictionKind kind, string targetId, ModelTime due, long generation) {
        OwnerId = ownerId; Kind = kind; TargetId = targetId; Due = due; Generation = generation;
    }
    public override string ToString() => $"{OwnerId}:{Kind}:{TargetId}:{Due.Ticks}:{Generation}";
}

/// <summary>Immutable derived predictions and fixed routing, never part of a durable graph.</summary>
public sealed class CycleForecasts {
    private readonly IReadOnlyList<CyclePrediction> _predictions;
    private readonly IReadOnlyList<CycleBinding> _bindings;
    private readonly Dictionary<(CycleEventKind, string), CycleBinding[]> _index;
    public IReadOnlyList<CyclePrediction> Predictions => _predictions;
    public IReadOnlyList<CycleBinding> Bindings => _bindings;
    internal CycleForecasts(IEnumerable<CyclePrediction> predictions, IEnumerable<CycleBinding> bindings) {
        _predictions = Array.AsReadOnly(predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal).ToArray());
        _bindings = Array.AsReadOnly(bindings.ToArray());
        _index = _bindings.GroupBy(b => (b.EventKind, b.TargetId))
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.OwnerId, StringComparer.Ordinal).ToArray());
    }
    private CycleForecasts(IEnumerable<CyclePrediction> predictions, CycleForecasts previous) {
        _predictions = Array.AsReadOnly(predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal).ToArray());
        _bindings = previous._bindings;
        _index = previous._index;
    }
    internal CycleForecasts WithPredictions(IEnumerable<CyclePrediction> predictions) => new(predictions, this);
    internal IEnumerable<CycleBinding> FindBindings(CycleEventKind kind, string target) =>
        _index.GetValueOrDefault((kind, target), []).Concat(_index.GetValueOrDefault((kind, "*"), []));
    public override string ToString() => $"predictions[{string.Join(';', _predictions)}]bindings[{string.Join(';', _bindings)}]";
}

/// <summary>Persistent world truth; predictions and dispatch indexes are derived after loading.</summary>
[DurableType("DramaBoard.ResourceCycle.BusinessState", 1)]
public sealed partial class CycleBusinessState : IDurableObject {
    [DurableField(1)] private ModelTime _time;
    public ModelTime Time => _time;
    [DurableField(2)] private CycleLight _light;
    public CycleLight Light => _light;
    [DurableField(3)] private List<CyclePlant> _plants = [];
    [DurableField(4)] private CycleScript _script;
    public CycleScript Script => _script;
    [DurableField(5)] private CycleCharacter? _character;
    public CycleCharacter? Character => _character;
    public IReadOnlyList<CyclePlant> Plants => _plants.AsReadOnly();
    internal CycleBusinessState(ModelTime time, CycleLight light, IEnumerable<CyclePlant> plants,
        CycleScript script, CycleCharacter? character = null) {
        _time = time; _light = light; _plants = [.. plants]; _script = script; _character = character;
    }
    public CyclePlant GetPlant(string id) => _plants.Single(p => p.Id == id);
    internal CycleBusinessState Replace(ModelTime time, CycleLight? light = null, IEnumerable<CyclePlant>? changedPlants = null,
        CycleScript? script = null, CycleCharacter? character = null) {
        List<CyclePlant> plants = _plants;
        if (changedPlants is not null) {
            Dictionary<string, CyclePlant> changes = changedPlants.ToDictionary(p => p.Id, StringComparer.Ordinal);
            if (changes.Keys.Any(id => !_plants.Any(p => p.Id == id))) {
                throw new InvalidOperationException("A fact cannot replace an unknown plant.");
            }
            if (changes.Count > 0) { plants = _plants.Select(p => changes.GetValueOrDefault(p.Id, p)).ToList(); }
        }
        return new(time, light ?? Light, plants, script ?? Script, character ?? Character);
    }
    public void Validate() {
        if (Time.Ticks < 0 || Light is null || Script is null || Light.NextChange < Time) {
            throw new InvalidOperationException("Invalid cycle world clock or subgraphs.");
        }
        if (_plants.Count != 2 || _plants.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != _plants.Count) {
            throw new InvalidOperationException("The prototype requires two distinct plants.");
        }
        foreach (CyclePlant plant in _plants) {
            if (plant.RequiredTicks <= 0 || plant.AccumulatedTicks < 0 || plant.AccumulatedTicks > plant.RequiredTicks || plant.Generation < 0 ||
                plant.ActiveSince > Time || (plant.IsMature && (plant.ActiveSince is not null || plant.AccumulatedTicks != plant.RequiredTicks)) ||
                (!plant.IsMature && (plant.ActiveSince is not null) != (plant.GrowsInDaylight == Light.IsDay))) {
                throw new InvalidOperationException($"Invalid plant state: {plant.Id}.");
            }
        }
        if (Script.NextIndex < 0 || Script.NextIndex > Script.Entries.Count ||
            Script.Entries.Skip(Script.NextIndex).Any(e => e.Due < Time || !_plants.Any(p => p.Id == e.PlantId))) {
            throw new InvalidOperationException("Invalid script position.");
        }
        Character?.Validate(Time);
    }
}

/// <summary>A ready runtime snapshot; Fold may temporarily retain the preceding cache until Complete.</summary>
public sealed class CycleWorld {
    public CycleBusinessState Business { get; }
    public CycleForecasts Forecasts { get; }
    public ModelTime Time => Business.Time;
    public CycleLight Light => Business.Light;
    public IReadOnlyList<CyclePlant> Plants => Business.Plants;
    public CycleScript Script => Business.Script;
    public CycleCharacter? Character => Business.Character;
    public static ModelTime DefaultStart => new(16 * CycleTime.Hour);
    internal CycleWorld(CycleBusinessState business, CycleForecasts forecasts) {
        Business = business ?? throw new ArgumentNullException(nameof(business));
        Forecasts = forecasts ?? throw new ArgumentNullException(nameof(forecasts));
    }
    public CyclePlant GetPlant(string id) => Business.GetPlant(id);
    public static CycleWorld Create(ModelTime? start = null, IEnumerable<CycleDestruction>? script = null) {
        ModelTime time = start ?? DefaultStart;
        if (time.Ticks < 0) { throw new ArgumentOutOfRangeException(nameof(start)); }
        bool isDay = CycleTime.IsDay(time);
        List<CyclePlant> plants = [
            new("moongrass", false, 4 * CycleTime.Hour, 0, isDay ? null : time, false, 0),
            new("sunflower", true, 3 * CycleTime.Hour, 0, isDay ? time : null, false, 0)
        ];
        var entries = (script ?? DefaultScript(time)).OrderBy(e => e.Due).ThenBy(e => e.PlantId, StringComparer.Ordinal).ToArray();
        if (entries.Any(e => e.Due < time || !plants.Any(p => p.Id == e.PlantId))) {
            throw new ArgumentException("Script entries require an existing plant and a due time at or after genesis.", nameof(script));
        }
        var business = new CycleBusinessState(time, new(isDay, CycleTime.NextLightChange(time)), plants, new(entries));
        return CycleEngine.Rebuild(business, time);
    }
    public static CycleWorld CreatePlayable(ModelTime? start = null, string actorId = "gardener") {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        CycleWorld world = Create(start, []);
        return CycleEngine.Rebuild(world.Business.Replace(world.Time, character: new(actorId, new())), world.Time);
    }
    private static IEnumerable<CycleDestruction> DefaultScript(ModelTime start) {
        yield return new("sunflower", new(checked(start.Ticks + 7 * CycleTime.Hour)));
        yield return new("moongrass", new(checked(start.Ticks + 14 * CycleTime.Hour)));
        yield return new("sunflower", new(checked(start.Ticks + 17 * CycleTime.Hour)));
        yield return new("moongrass", new(checked(start.Ticks + 30 * CycleTime.Hour)));
        yield return new("sunflower", new(checked(start.Ticks + 47 * CycleTime.Hour)));
    }
    public void Validate() => CycleEngine.Validate(this, Time);
    public string DescribeState() => $"time={Time.Ticks};light={Light};plants=[{string.Join(';', Plants)}];script={Script};{Forecasts}" +
        (Character is null ? "" : $";character={Character}");
}

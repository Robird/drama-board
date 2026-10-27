using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.EventKind", 2)]
public enum CycleEventKind { PlantMatured, LightChanged, PlantDestroyed, WaitStarted, WaitCompleted, PlantGathered }
[DurableType("DramaBoard.ResourceCycle.PredictionKind", 2)]
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

/// <summary>A durable data binding, interpreted by the application's fixed dispatch table.</summary>
[DurableType("DramaBoard.ResourceCycle.Binding", 2)]
public sealed partial class CycleBinding : IDurableObject {
    [DurableField(1)] private string _ownerId;
    public string OwnerId => _ownerId;
    [DurableField(2)] private CycleEventKind _eventKind;
    public CycleEventKind EventKind => _eventKind;
    [DurableField(3)] private string _targetId;
    public string TargetId => _targetId;
    public CycleBinding(string ownerId, CycleEventKind eventKind, string targetId) {
        _ownerId = ownerId; _eventKind = eventKind; _targetId = targetId;
    }
    public override string ToString() => $"{OwnerId}:{EventKind}:{TargetId}";
}

[DurableType("DramaBoard.ResourceCycle.Prediction", 2)]
public sealed partial class CyclePrediction : IDurableObject {
    [DurableField(1)] private string _ownerId;
    public string OwnerId => _ownerId;
    [DurableField(2)] private CyclePredictionKind _kind;
    public CyclePredictionKind Kind => _kind;
    [DurableField(3)] private string _targetId;
    public string TargetId => _targetId;
    [DurableField(4)] private ModelTime _due;
    public ModelTime Due => _due;
    [DurableField(5)] private long _generation;
    public long Generation => _generation;
    internal CyclePrediction(string ownerId, CyclePredictionKind kind, string targetId, ModelTime due, long generation) {
        _ownerId = ownerId; _kind = kind; _targetId = targetId; _due = due; _generation = generation;
    }
    public override string ToString() => $"{OwnerId}:{Kind}:{TargetId}:{Due.Ticks}:{Generation}";
}

/// <summary>Bindings survive periods in which their owner has no future prediction.</summary>
[DurableType("DramaBoard.ResourceCycle.Forecasts", 1)]
public sealed partial class CycleForecasts : IDurableObject {
    [DurableField(1)] private List<CyclePrediction> _predictions = [];
    [DurableField(2)] private List<CycleBinding> _bindings = [];
    public IReadOnlyList<CyclePrediction> Predictions => _predictions.AsReadOnly();
    public IReadOnlyList<CycleBinding> Bindings => _bindings.AsReadOnly();
    internal object BindingIdentity => _bindings;
    internal CycleForecasts(IEnumerable<CyclePrediction> predictions, IEnumerable<CycleBinding> bindings) {
        _predictions = [.. predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal)]; _bindings = [.. bindings];
    }
    private CycleForecasts(IEnumerable<CyclePrediction> predictions, List<CycleBinding> bindings) {
        _predictions = [.. predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal)]; _bindings = bindings;
    }
    internal CycleForecasts WithPredictions(IEnumerable<CyclePrediction> predictions) => new(predictions, _bindings);
    public override string ToString() => $"predictions[{string.Join(';', _predictions)}]bindings[{string.Join(';', _bindings)}]";
}

[DurableType("DramaBoard.ResourceCycle.World", 2)]
public sealed partial class CycleWorld : IDurableObject {
    [DurableField(1)] private ModelTime _time;
    public ModelTime Time => _time;
    [DurableField(2)] private CycleLight _light;
    public CycleLight Light => _light;
    [DurableField(3)] private List<CyclePlant> _plants = [];
    [DurableField(4)] private CycleScript _script;
    public CycleScript Script => _script;
    [DurableField(5)] private CycleForecasts _forecasts;
    public CycleForecasts Forecasts => _forecasts;
    [DurableField(6)] private CycleCharacter? _character;
    public CycleCharacter? Character => _character;
    public IReadOnlyList<CyclePlant> Plants => _plants.AsReadOnly();
    public static ModelTime DefaultStart => new(16 * CycleTime.Hour);
    internal CycleWorld(ModelTime time, CycleLight light, List<CyclePlant> plants, CycleScript script, CycleForecasts forecasts, CycleCharacter? character = null) {
        _time = time; _light = light; _plants = plants; _script = script; _forecasts = forecasts; _character = character;
    }
    public CyclePlant GetPlant(string id) => _plants.Single(p => p.Id == id);
    internal CycleWorld Replace(ModelTime time, CycleLight? light = null, IEnumerable<CyclePlant>? changedPlants = null,
        CycleScript? script = null, CycleForecasts? forecasts = null, CycleCharacter? character = null) {
        List<CyclePlant> plants = _plants;
        if (changedPlants is not null) {
            Dictionary<string, CyclePlant> changes = changedPlants.ToDictionary(p => p.Id, StringComparer.Ordinal);
            if (changes.Count > 0) { plants = _plants.Select(p => changes.GetValueOrDefault(p.Id, p)).ToList(); }
        }
        return new(time, light ?? Light, plants, script ?? Script, forecasts ?? Forecasts, character ?? Character);
    }
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
        List<CycleBinding> bindings = [new("light", CycleEventKind.LightChanged, "world"), new("script", CycleEventKind.PlantDestroyed, "*")];
        foreach (CyclePlant plant in plants) {
            bindings.Add(new($"plant:{plant.Id}", CycleEventKind.LightChanged, "world"));
            bindings.Add(new($"plant:{plant.Id}", CycleEventKind.PlantDestroyed, plant.Id));
            bindings.Add(new($"plant:{plant.Id}", CycleEventKind.PlantMatured, plant.Id));
        }
        var world = new CycleWorld(time, new(isDay, CycleTime.NextLightChange(time)), plants, new(entries), new([], bindings));
        world = world.Replace(time, forecasts: world.Forecasts.WithPredictions(CycleEngine.Owners(world).Select(owner => CycleEngine.Predict(world, owner)).OfType<CyclePrediction>()));
        world.Validate();
        return world;
    }
    public static CycleWorld CreatePlayable(ModelTime? start = null, string actorId = "gardener") {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        CycleWorld world = Create(start, []);
        var bindings = world.Forecasts.Bindings.ToList();
        foreach (CyclePlant plant in world.Plants) {
            bindings.Add(new($"plant:{plant.Id}", CycleEventKind.PlantGathered, plant.Id));
        }
        string owner = $"actor:{actorId}";
        bindings.Add(new(owner, CycleEventKind.WaitStarted, actorId));
        bindings.Add(new(owner, CycleEventKind.WaitCompleted, actorId));
        bindings.Add(new(owner, CycleEventKind.PlantGathered, "*"));
        world = world.Replace(world.Time, character: new(actorId, new()), forecasts: new([], bindings));
        world = world.Replace(world.Time, forecasts: world.Forecasts.WithPredictions(CycleEngine.Owners(world).Select(id => CycleEngine.Predict(world, id)).OfType<CyclePrediction>()));
        world.Validate();
        return world;
    }
    private static IEnumerable<CycleDestruction> DefaultScript(ModelTime start) {
        yield return new("sunflower", new(checked(start.Ticks + 7 * CycleTime.Hour)));
        yield return new("moongrass", new(checked(start.Ticks + 14 * CycleTime.Hour)));
        yield return new("sunflower", new(checked(start.Ticks + 17 * CycleTime.Hour)));
        yield return new("moongrass", new(checked(start.Ticks + 30 * CycleTime.Hour)));
        yield return new("sunflower", new(checked(start.Ticks + 47 * CycleTime.Hour)));
    }
    public void Validate() {
        if (Time.Ticks < 0 || Light is null || Script is null || Forecasts is null || Light.NextChange < Time) {
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
        if (Forecasts.Predictions.Any(p => p.Due < Time) || Forecasts.Predictions.Select(p => p.OwnerId).Distinct(StringComparer.Ordinal).Count() != Forecasts.Predictions.Count) {
            throw new InvalidOperationException("Invalid prediction set.");
        }
        Character?.Validate(Time);
    }
    public string DescribeState() => $"time={Time.Ticks};light={Light};plants=[{string.Join(';', _plants)}];script={Script};{Forecasts}" +
        (Character is null ? "" : $";character={Character}");
}

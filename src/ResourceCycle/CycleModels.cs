using System.Collections.ObjectModel;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.EventKind", 13)]
public enum CycleEventKind {
    ResourceRegenerated, LightChanged, ResourceDestroyed, WaitStarted, WaitCompleted,
    GatherStarted, GatherYielded, GatherFinished, ActivityCancelled, AttentionAcknowledged, MoveStarted, MoveArrived,
    SurvivalWarning, SurvivalFailed, ItemUsed, WeatherResolved, HandoffEdited, DecisionContextOpened, OperationRejected, SpeechSent, ItemGiven, GoalOperation,
    FollowStarted, FollowEnded, FollowersDismissed, SpeechShouted,
    MarketBought, MarketSold, ProductionStarted, ProductionCompleted, MarketRefreshed, MoneyGiven
}
public enum CyclePredictionKind { SurvivalFatal, RegenerateResource, ChangeLight, DestroyResource, SpatialOccurrence, Weather, CompleteActivity, MarketRefresh, SurvivalWarning, RequestDecision }
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
    [DurableField(2)] private ModelTime _nextChange;
    public bool IsDay => _isDay;
    public ModelTime NextChange => _nextChange;
    public CycleLight(bool isDay, ModelTime nextChange) { _isDay = isDay; _nextChange = nextChange; }
    public override string ToString() => $"{IsDay}:{NextChange.Ticks}";
}

[DurableType("DramaBoard.ResourceCycle.Destruction", 2)]
public sealed partial class CycleDestruction : IDurableObject {
    [DurableField(1)] private string _resourceId;
    [DurableField(2)] private ModelTime _due;
    public string ResourceId => _resourceId;
    public ModelTime Due => _due;
    public CycleDestruction(string resourceId, ModelTime due) { _resourceId = resourceId; _due = due; }
    public override string ToString() => $"{ResourceId}:{Due.Ticks}";
}

[DurableType("DramaBoard.ResourceCycle.Script", 1)]
public sealed partial class CycleScript : IDurableObject {
    [DurableField(1)] private List<CycleDestruction> _entries;
    [DurableField(2)] private int _nextIndex;
    public int NextIndex => _nextIndex;
    public IReadOnlyList<CycleDestruction> Entries => _entries.AsReadOnly();
    public CycleScript(IEnumerable<CycleDestruction> entries, int nextIndex = 0) { _entries = [.. entries]; _nextIndex = nextIndex; }
    public override string ToString() => $"{NextIndex}[{string.Join(';', _entries)}]";
}

/// <summary>Persistent world truth; caches and member lists are rebuilt after loading.</summary>
[DurableType("DramaBoard.ResourceCycle.BusinessState", 5)]
public sealed partial class CycleBusinessState : IDurableObject {
    [DurableField(1)] private ModelTime _time;
    [DurableField(2)] private CycleLight _light;
    [DurableField(3)] private List<CycleResource> _resources;
    [DurableField(4)] private CycleScript _script;
    [DurableField(5)] private List<CycleCharacter> _characters;
    [DurableField(6)] private string? _lastServedActorId;
    [DurableField(7)] private CycleExpedition? _expedition;
    [DurableField(8)] private Dictionary<string, CharacterInteractionState> _interactionStates;
    [DurableField(9)] private CycleEconomyState? _economy;
    public CycleEconomyState? Economy => _economy;
    public IReadOnlyDictionary<string, CharacterInteractionState> InteractionStates => new ReadOnlyDictionary<string, CharacterInteractionState>(_interactionStates);
    public ModelTime Time => _time;
    public CycleLight Light => _light;
    public CycleScript Script => _script;
    public IReadOnlyList<CycleResource> Resources => _resources.AsReadOnly();
    public IReadOnlyList<CycleCharacter> Characters => _characters.AsReadOnly();
    public string? LastServedActorId => _lastServedActorId;
    public CycleExpedition? Expedition => _expedition;
    public CycleBusinessState(ModelTime time, CycleLight light, IEnumerable<CycleResource> resources,
        CycleScript script, IEnumerable<CycleCharacter>? characters = null, string? lastServedActorId = null,
        CycleExpedition? expedition = null, IEnumerable<CharacterInteractionState>? interactionStates = null, CycleEconomyState? economy = null) {
        _time = time; _light = light; _resources = [.. resources]; _script = script;
        _characters = characters?.ToList() ?? []; _lastServedActorId = lastServedActorId;
        _expedition = expedition;
        _economy = economy;
        CharacterInteractionState[] states = (interactionStates ?? _characters.Select(c => new CharacterInteractionState(c.Id))).ToArray();
        if (states.Select(state => state.ActorId).Distinct(StringComparer.Ordinal).Count() != states.Length) {
            throw new InvalidOperationException("Interaction states must have unique actor identities.");
        }
        _interactionStates = states.ToDictionary(state => state.ActorId, StringComparer.Ordinal);
    }
    public CycleResource GetResource(string id) => _resources.Single(p => p.Id == id);
    public CycleCharacter GetCharacter(string id) => _characters.Single(c => c.Id == id);
    public CycleBusinessState Replace(ModelTime time, CycleLight? light = null, IEnumerable<CycleResource>? changedResources = null,
        CycleScript? script = null, IEnumerable<CycleCharacter>? changedCharacters = null, string? lastServedActorId = null,
        CycleExpedition? expedition = null, IEnumerable<CharacterInteractionState>? interactionChanges = null, CycleEconomyState? economy = null) {
        return new(time, light ?? Light, ReplaceChildren(_resources, changedResources, r => r.Id), script ?? Script,
            ReplaceChildren(_characters, changedCharacters, c => c.Id), lastServedActorId ?? LastServedActorId, expedition ?? Expedition,
            ReplaceChildren(_interactionStates.Values.ToArray(), interactionChanges, state => state.ActorId), economy ?? Economy);
    }
    private static IEnumerable<T> ReplaceChildren<T>(IReadOnlyList<T> previous, IEnumerable<T>? changes, Func<T, string> id) {
        if (changes is null) { return previous; }
        var map = changes.ToDictionary(id, StringComparer.Ordinal);
        if (map.Keys.Any(key => !previous.Any(value => id(value) == key))) {
            throw new InvalidOperationException("A fact cannot replace an unknown child.");
        }
        return previous.Select(value => map.GetValueOrDefault(id(value), value)).ToArray();
    }
    public void Validate() {
        if (Time.Ticks < 0 || Light is null || Script is null || Light.NextChange < Time ||
            _resources.Count == 0 || _resources.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != _resources.Count ||
            _characters.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != _characters.Count ||
            (LastServedActorId is not null && !_characters.Any(c => c.Id == LastServedActorId))) {
            throw new InvalidOperationException("Invalid world clock, identities or service cursor.");
        }
        if (_interactionStates.Count != _characters.Count || _characters.Any(c => !_interactionStates.ContainsKey(c.Id)) ||
            _interactionStates.Any(pair => pair.Key != pair.Value.ActorId)) {
            throw new InvalidOperationException("Interaction states must belong one-to-one to the world's characters.");
        }
        foreach (CharacterInteractionState state in _interactionStates.Values) {
            state.Validate(state.ActorId, Time);
            if (state.CurrentContext is { } context && (Expedition is null
                ? context.LatestObservation.Goals is not null
                : !state.Goals.SameAs(context.LatestObservation.Goals))) {
                throw new InvalidOperationException("Current goal state and the character's latest frozen material disagree.");
            }
        }
        foreach (CycleResource resource in _resources) { resource.Validate(Time, Light.IsDay); }
        foreach (CycleCharacter actor in _characters) {
            actor.Validate(Time);
            if ((!actor.IsActive) != (_interactionStates[actor.Id].CurrentContext?.Status == CharacterContextStatus.Terminal) ||
                !actor.IsActive && Expedition is null) {
                throw new InvalidOperationException("Personal ending and terminal context must be published together in an expedition.");
            }
            if ((actor.Activity?.ResourceId is { } target && !_resources.Any(r => r.Id == target)) ||
                actor.Notices.Any(n => !_resources.Any(r => r.Id == n.ResourceId) ||
                    (n.ActorId is not null && !_characters.Any(c => c.Id == n.ActorId)))) {
                throw new InvalidOperationException("Activity or perception refers to an unknown entity.");
            }
        }
        if (Script.NextIndex < 0 || Script.NextIndex > Script.Entries.Count ||
            Script.Entries.Skip(Script.NextIndex).Any(e => e.Due < Time || !_resources.Any(p => p.Id == e.ResourceId)) ||
            Script.Entries.Zip(Script.Entries.Skip(1)).Any(pair => pair.First.Due > pair.Second.Due)) {
            throw new InvalidOperationException("Invalid script position or ordering.");
        }
        Expedition?.Validate(this);
        CycleFollowingRules.Validate(this);
        CycleSurvivalRules.Validate(this);
        if (Economy is null) {
            if (Characters.Any(actor => actor.Money != 0 || actor.Activity?.Production is not null)) { throw new InvalidOperationException("Economic assets require an economic state."); }
        } else {
            if (Expedition is null) { throw new InvalidOperationException("Economy requires the continuous world."); }
            Economy.Validate(this);
            foreach (CycleCharacter actor in Characters.Where(actor => actor.Activity?.Production is not null)) {
                if (CycleActions.Location(this, actor.Id) is not AtPlaceLocation place || place.PlaceId.Value != "workshop") {
                    throw new InvalidOperationException("Production requires the workshop.");
                }
            }
        }
    }
}

/// <summary>A ready immutable runtime snapshot. Fold temporarily carries the old derived cache.</summary>
public sealed class CycleWorld {
    public CycleBusinessState Business { get; }
    public CycleForecasts Forecasts { get; }
    public SpatialSimulationState? Spatial { get; }
    public CycleExpedition? Expedition => Business.Expedition;
    public CycleExpeditionSummary ExpeditionSummary => Expedition is null || Characters.Any(c => c.IsActive)
        ? CycleExpeditionSummary.Exploring : Characters.All(c => c.Ending?.Outcome == CycleExpeditionOutcome.Escaped)
        ? CycleExpeditionSummary.AllEscaped : Characters.Any(c => c.Ending?.Outcome == CycleExpeditionOutcome.Escaped)
        ? CycleExpeditionSummary.PartiallyEscaped : CycleExpeditionSummary.NoneEscaped;
    public ModelTime Time => Business.Time;
    public CycleLight Light => Business.Light;
    public IReadOnlyList<CycleResource> Resources => Business.Resources;
    public IReadOnlyList<CycleCharacter> Characters => Business.Characters;
    public CycleScript Script => Business.Script;
    public static ModelTime DefaultStart => new(16 * CycleTime.Hour);
    internal CycleWorld(CycleBusinessState business, CycleForecasts forecasts, SpatialSimulationState? spatial = null) {
        Business = business ?? throw new ArgumentNullException(nameof(business));
        Forecasts = forecasts ?? throw new ArgumentNullException(nameof(forecasts));
        Spatial = spatial;
    }
    public CycleResource GetResource(string id) => Business.GetResource(id);
    public CycleCharacter GetCharacter(string id) => Business.GetCharacter(id);
    /// <summary>Constructs a new batch world and rebuilds its runtime predictions; does not publish a save.</summary>
    /// <remarks>Supplied destruction instances are retained, not deep-cloned.</remarks>
    public static CycleWorld Create(ModelTime? start = null, IEnumerable<CycleDestruction>? script = null) =>
        CycleGameFactory.CreateBatch(start, script).World;
    /// <summary>Constructs a new playable world and rebuilds its runtime predictions; does not publish a save.</summary>
    /// <remarks>This is a new-game factory, not a saved-world repair or revision operation.
    /// Supplied character and resource instances are retained, not deep-cloned.</remarks>
    public static CycleWorld CreatePlayable(ModelTime? start = null, IEnumerable<CycleCharacter>? characters = null,
        IEnumerable<CycleResource>? resources = null) => CycleGameFactory.CreatePlayable(start, characters, resources).World;
    public void Validate() => CycleEngine.Validate(this, Time);
    public string DescribeState() => $"time={Time.Ticks};light={Light};resources=[{string.Join(';', Resources)}];script={Script};characters=[{string.Join(';', Characters)}];lastServed={Business.LastServedActorId};economy={Business.Economy};{(Expedition is null ? "" : $"expedition={Expedition};")}{Forecasts}";
}

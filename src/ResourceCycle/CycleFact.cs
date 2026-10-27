using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Concrete recorded replacements. Restore applies these values without replaying Player or planning.</summary>
[DurableType("DramaBoard.ResourceCycle.Fact", 3)]
public sealed partial class CycleFact : IDurableObject {
    [DurableField(1)] private CycleEventKind _kind;
    [DurableField(2)] private ModelTime _at;
    [DurableField(3)] private string? _actorId;
    [DurableField(4)] private string? _resourceId;
    [DurableField(5)] private CycleLight? _light;
    [DurableField(6)] private List<CycleResource> _changedResources;
    [DurableField(7)] private CycleScript? _script;
    [DurableField(8)] private List<CycleCharacter> _changedCharacters;
    [DurableField(9)] private string? _lastServedActorId;
    [DurableField(10)] private string? _result;
    [DurableField(11)] private List<GraphSpatialFact> _spatialFacts;
    [DurableField(12)] private List<CycleVisit> _visits;
    [DurableField(13)] private CycleExpeditionOutcome? _outcome;
    [DurableField(14)] private string? _endingReason;
    public CycleEventKind Kind => _kind;
    public ModelTime At => _at;
    public string? ActorId => _actorId;
    public string? ResourceId => _resourceId;
    public CycleLight? Light => _light;
    public IReadOnlyList<CycleResource> ChangedResources => _changedResources.AsReadOnly();
    public CycleScript? Script => _script;
    public IReadOnlyList<CycleCharacter> ChangedCharacters => _changedCharacters.AsReadOnly();
    public string? LastServedActorId => _lastServedActorId;
    public string? Result => _result;
    public IReadOnlyList<GraphSpatialFact> SpatialFacts => _spatialFacts.AsReadOnly();
    public IReadOnlyList<CycleVisit> Visits => _visits.AsReadOnly();
    public CycleExpeditionOutcome? Outcome => _outcome;
    public string? EndingReason => _endingReason;
    public CycleFact(CycleEventKind kind, ModelTime at, string? actorId = null, string? resourceId = null,
        CycleLight? light = null, IEnumerable<CycleResource>? changedResources = null, CycleScript? script = null,
        IEnumerable<CycleCharacter>? changedCharacters = null, string? lastServedActorId = null, string? result = null,
        IEnumerable<GraphSpatialFact>? spatialFacts = null, IEnumerable<CycleVisit>? visits = null,
        CycleExpeditionOutcome? outcome = null, string? endingReason = null) {
        _kind = kind; _at = at; _actorId = actorId; _resourceId = resourceId; _light = light;
        _changedResources = changedResources?.ToList() ?? []; _script = script;
        _changedCharacters = changedCharacters?.ToList() ?? []; _lastServedActorId = lastServedActorId; _result = result;
        _spatialFacts = spatialFacts?.ToList() ?? []; _visits = visits?.ToList() ?? []; _outcome = outcome; _endingReason = endingReason;
    }
    internal CycleFact WithVisits(IEnumerable<CycleVisit> visits) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, visits, Outcome, EndingReason);
    public override string ToString() => $"{Kind}@{At.Ticks};actor={ActorId};resource={ResourceId};light={Light};resources=[{string.Join(';', _changedResources)}];script={Script};characters=[{string.Join(';', _changedCharacters)}];served={LastServedActorId};result={Result};spatial=[{string.Join(';', _spatialFacts.Select(f => f.GetType().Name))}];visits=[{string.Join(';', Visits)}];outcome={Outcome}:{EndingReason}";
}

/// <summary>Runtime diagnostics, deliberately outside business snapshots.</summary>
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

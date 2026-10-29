using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.OperationDisposition", 1)]
public enum CycleOperationDisposition { Succeeded, Rejected }

/// <summary>Concrete recorded replacements. Restore applies these values without replaying Player or planning.</summary>
[DurableType("DramaBoard.ResourceCycle.Fact", 11)]
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
    [DurableField(15)] private CycleEnvironment? _environment;
    [DurableField(16)] private string? _toolCallId;
    [DurableField(17)] private string? _operationRecordId;
    [DurableField(18)] private CycleGatherStopReason? _gatherStopReason;
    [DurableField(19)] private List<CycleObservedEvent> _observedEvents;
    [DurableField(20)] private List<CharacterInteractionState> _interactionChanges;
    [DurableField(21)] private string? _rawInput;
    [DurableField(22)] private CycleOperationDisposition? _operationDisposition;
    [DurableField(23)] private string? _targetActorId;
    [DurableField(24)] private string? _speechText;
    [DurableField(25)] private string? _itemId;
    [DurableField(26)] private int _itemCount;
    public string? TargetActorId => _targetActorId;
    public string? SpeechText => _speechText;
    public string? ItemId => _itemId;
    public int ItemCount => _itemCount;
    public string? RawInput => _rawInput;
    public CycleOperationDisposition? OperationDisposition => _operationDisposition;
    public IReadOnlyList<CharacterInteractionState> InteractionChanges => _interactionChanges.AsReadOnly();
    public IReadOnlyList<CycleObservedEvent> ObservedEvents => _observedEvents.AsReadOnly();
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
    public CycleEnvironment? Environment => _environment;
    public string? ToolCallId => _toolCallId;
    public string? OperationRecordId => _operationRecordId;
    public CycleGatherStopReason? GatherStopReason => _gatherStopReason;
    public CycleFact(CycleEventKind kind, ModelTime at, string? actorId = null, string? resourceId = null,
        CycleLight? light = null, IEnumerable<CycleResource>? changedResources = null, CycleScript? script = null,
        IEnumerable<CycleCharacter>? changedCharacters = null, string? lastServedActorId = null, string? result = null,
        IEnumerable<GraphSpatialFact>? spatialFacts = null, IEnumerable<CycleVisit>? visits = null,
        CycleEnvironment? environment = null,
        string? toolCallId = null, string? operationRecordId = null, CycleGatherStopReason? gatherStopReason = null, IEnumerable<CycleObservedEvent>? observedEvents = null,
        IEnumerable<CharacterInteractionState>? interactionChanges = null, string? rawInput = null, CycleOperationDisposition? operationDisposition = null,
        string? targetActorId = null, string? speechText = null, string? itemId = null, int itemCount = 0) {
        _targetActorId = targetActorId; _speechText = speechText; _itemId = itemId; _itemCount = itemCount;
        _interactionChanges = interactionChanges?.ToList() ?? [];
        _rawInput = rawInput; _operationDisposition = operationDisposition;
        _kind = kind; _at = at; _actorId = actorId; _resourceId = resourceId; _light = light;
        _changedResources = changedResources?.ToList() ?? []; _script = script;
        _changedCharacters = changedCharacters?.ToList() ?? []; _lastServedActorId = lastServedActorId; _result = result;
        _spatialFacts = spatialFacts?.ToList() ?? []; _visits = visits?.ToList() ?? [];
        _environment = environment;
        _toolCallId = toolCallId; _operationRecordId = operationRecordId; _gatherStopReason = gatherStopReason; _observedEvents = observedEvents?.ToList() ?? [];
    }
    internal CycleFact WithVisits(IEnumerable<CycleVisit> visits) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, visits, Environment, ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount);
    internal CycleFact WithCharacters(IEnumerable<CycleCharacter> characters) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, characters, LastServedActorId, Result, SpatialFacts, Visits, Environment, ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount);
    internal CycleFact WithOperation(CycleControllerOperation operation, CycleOperationDisposition disposition = CycleOperationDisposition.Succeeded) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment,
        operation.ToolCallId, operation.OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, operation.RawInput, disposition, TargetActorId, SpeechText, ItemId, ItemCount);
    internal CycleFact WithObservedEvents(IEnumerable<CycleObservedEvent> events) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment, ToolCallId, OperationRecordId, GatherStopReason, events, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount);
    internal CycleFact WithInteractions(IEnumerable<CharacterInteractionState> changes) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment,
        ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, changes, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount);
    public override string ToString() => $"{Kind}@{At.Ticks};actor={ActorId};resource={ResourceId};light={Light};resources=[{string.Join(';', _changedResources)}];script={Script};characters=[{string.Join(';', _changedCharacters)}];served={LastServedActorId};result={Result};spatial=[{string.Join(';', _spatialFacts.Select(f => f.GetType().Name))}];visits=[{string.Join(';', Visits)}]" +
        (Environment is null ? "" : $";environment={Environment}") + $";call={ToolCallId};operation={OperationRecordId};stop={GatherStopReason};disposition={OperationDisposition};target={TargetActorId};speech={SpeechText};item={ItemId}:{ItemCount}";
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

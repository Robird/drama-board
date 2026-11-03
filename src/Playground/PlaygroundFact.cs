using Atelia.DurableGraph;
using DramaBoard.Goals;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.OperationDisposition", 1)]
public enum PlaygroundOperationDisposition { Succeeded, Rejected }

/// <summary>Concrete recorded replacements. Restore applies these values without replaying Player or planning.</summary>
[DurableType("DramaBoard.Playground.Fact", 4)]
public sealed partial class PlaygroundFact : IDurableObject {
    [DurableField(1)] private PlaygroundEventKind _kind;
    [DurableField(2)] private ModelTime _at;
    [DurableField(3)] private string? _actorId;
    [DurableField(4)] private string? _resourceId;
    [DurableField(5)] private PlaygroundLight? _light;
    [DurableField(6)] private List<PlaygroundResource> _changedResources;
    [DurableField(7)] private PlaygroundScript? _script;
    [DurableField(8)] private List<PlaygroundCharacter> _changedCharacters;
    [DurableField(9)] private string? _lastServedActorId;
    [DurableField(10)] private string? _result;
    [DurableField(11)] private List<GraphSpatialFact> _spatialFacts;
    [DurableField(12)] private List<PlaygroundVisit> _visits;
    [DurableField(15)] private PlaygroundEnvironment? _environment;
    [DurableField(16)] private string? _toolCallId;
    [DurableField(17)] private string? _operationRecordId;
    [DurableField(18)] private PlaygroundGatherStopReason? _gatherStopReason;
    [DurableField(19)] private List<PlaygroundObservedEvent> _observedEvents;
    [DurableField(20)] private List<CharacterInteractionState> _interactionChanges;
    [DurableField(21)] private string? _rawInput;
    [DurableField(22)] private PlaygroundOperationDisposition? _operationDisposition;
    [DurableField(23)] private string? _targetActorId;
    [DurableField(24)] private string? _speechText;
    [DurableField(25)] private string? _itemId;
    [DurableField(26)] private int _itemCount;
    [DurableField(27)] private GoalReceipt? _goalOperation;
    [DurableField(28)] private PlaygroundEconomyState? _economy;
    [DurableField(29)] private long _moneyAmount;
    [DurableField(33)] private PlaygroundNavigationChange? _navigationChange;
    public PlaygroundNavigationChange? NavigationChange => _navigationChange;
    [DurableField(32)] private PlaygroundPath? _candidatePath;
    public PlaygroundPath? CandidatePath => _candidatePath;
    [DurableField(31)] private List<PlaygroundConfirmedPassage> _confirmedPassages;
    public IReadOnlyList<PlaygroundConfirmedPassage> ConfirmedPassages => _confirmedPassages.AsReadOnly();
    [DurableField(30)] private List<PlaygroundTradeOffer> _tradeChanges;
    public IReadOnlyList<PlaygroundTradeOffer> TradeChanges => _tradeChanges.AsReadOnly();
    public PlaygroundEconomyState? Economy => _economy;
    public long MoneyAmount => _moneyAmount;
    public GoalReceipt? GoalOperation => _goalOperation;
    public string? TargetActorId => _targetActorId;
    public string? SpeechText => _speechText;
    public string? ItemId => _itemId;
    public int ItemCount => _itemCount;
    public string? RawInput => _rawInput;
    public PlaygroundOperationDisposition? OperationDisposition => _operationDisposition;
    public IReadOnlyList<CharacterInteractionState> InteractionChanges => _interactionChanges.AsReadOnly();
    public IReadOnlyList<PlaygroundObservedEvent> ObservedEvents => _observedEvents.AsReadOnly();
    public PlaygroundEventKind Kind => _kind;
    public ModelTime At => _at;
    public string? ActorId => _actorId;
    public string? ResourceId => _resourceId;
    public PlaygroundLight? Light => _light;
    public IReadOnlyList<PlaygroundResource> ChangedResources => _changedResources.AsReadOnly();
    public PlaygroundScript? Script => _script;
    public IReadOnlyList<PlaygroundCharacter> ChangedCharacters => _changedCharacters.AsReadOnly();
    public string? LastServedActorId => _lastServedActorId;
    public string? Result => _result;
    public IReadOnlyList<GraphSpatialFact> SpatialFacts => _spatialFacts.AsReadOnly();
    public IReadOnlyList<PlaygroundVisit> Visits => _visits.AsReadOnly();
    public PlaygroundEnvironment? Environment => _environment;
    public string? ToolCallId => _toolCallId;
    public string? OperationRecordId => _operationRecordId;
    public PlaygroundGatherStopReason? GatherStopReason => _gatherStopReason;
    public PlaygroundFact(PlaygroundEventKind kind, ModelTime at, string? actorId = null, string? resourceId = null,
        PlaygroundLight? light = null, IEnumerable<PlaygroundResource>? changedResources = null, PlaygroundScript? script = null,
        IEnumerable<PlaygroundCharacter>? changedCharacters = null, string? lastServedActorId = null, string? result = null,
        IEnumerable<GraphSpatialFact>? spatialFacts = null, IEnumerable<PlaygroundVisit>? visits = null,
        PlaygroundEnvironment? environment = null,
        string? toolCallId = null, string? operationRecordId = null, PlaygroundGatherStopReason? gatherStopReason = null, IEnumerable<PlaygroundObservedEvent>? observedEvents = null,
        IEnumerable<CharacterInteractionState>? interactionChanges = null, string? rawInput = null, PlaygroundOperationDisposition? operationDisposition = null,
        string? targetActorId = null, string? speechText = null, string? itemId = null, int itemCount = 0, GoalReceipt? goalOperation = null,
        PlaygroundEconomyState? economy = null, long moneyAmount = 0, IEnumerable<PlaygroundTradeOffer>? tradeChanges = null, IEnumerable<PlaygroundConfirmedPassage>? confirmedPassages = null, PlaygroundPath? candidatePath = null, PlaygroundNavigationChange? navigationChange = null) {
        _navigationChange = navigationChange;
        _candidatePath = candidatePath;
        _confirmedPassages = confirmedPassages?.ToList() ?? [];
        _tradeChanges = tradeChanges?.ToList() ?? [];
        _economy = economy; _moneyAmount = moneyAmount;
        _goalOperation = goalOperation;
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
    internal PlaygroundFact WithVisits(IEnumerable<PlaygroundVisit> visits) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, visits, Environment, ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount, GoalOperation, Economy, MoneyAmount, TradeChanges, ConfirmedPassages, CandidatePath, NavigationChange);
    internal PlaygroundFact WithCharacters(IEnumerable<PlaygroundCharacter> characters) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, characters, LastServedActorId, Result, SpatialFacts, Visits, Environment, ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount, GoalOperation, Economy, MoneyAmount, TradeChanges, ConfirmedPassages, CandidatePath, NavigationChange);
    internal PlaygroundFact WithOperation(PlaygroundControllerOperation operation, PlaygroundOperationDisposition disposition = PlaygroundOperationDisposition.Succeeded) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment,
        operation.ToolCallId, operation.OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, operation.RawInput, disposition, TargetActorId, SpeechText, ItemId, ItemCount, GoalOperation, Economy, MoneyAmount, TradeChanges, ConfirmedPassages, CandidatePath, NavigationChange);
    internal PlaygroundFact WithObservedEvents(IEnumerable<PlaygroundObservedEvent> events) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment, ToolCallId, OperationRecordId, GatherStopReason, events, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount, GoalOperation, Economy, MoneyAmount, TradeChanges, ConfirmedPassages, CandidatePath, NavigationChange);
    internal PlaygroundFact WithInteractions(IEnumerable<CharacterInteractionState> changes) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment,
        ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, changes, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount, GoalOperation, Economy, MoneyAmount, TradeChanges, ConfirmedPassages, CandidatePath, NavigationChange);
    internal PlaygroundFact WithEconomy(PlaygroundEconomyState economy) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment,
        ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount, GoalOperation, economy, MoneyAmount, TradeChanges, ConfirmedPassages, CandidatePath, NavigationChange);
    internal PlaygroundFact WithTradeChanges(IEnumerable<PlaygroundTradeOffer> changes) => new(Kind, At, ActorId, ResourceId, Light,
        ChangedResources, Script, ChangedCharacters, LastServedActorId, Result, SpatialFacts, Visits, Environment,
        ToolCallId, OperationRecordId, GatherStopReason, ObservedEvents, InteractionChanges, RawInput, OperationDisposition, TargetActorId, SpeechText, ItemId, ItemCount, GoalOperation, Economy, MoneyAmount, changes, ConfirmedPassages, CandidatePath, NavigationChange);
    public override string ToString() => $"{Kind}@{At.Ticks};actor={ActorId};resource={ResourceId};light={Light};resources=[{string.Join(';', _changedResources)}];script={Script};characters=[{string.Join(';', _changedCharacters)}];served={LastServedActorId};result={Result};spatial=[{string.Join(';', _spatialFacts.Select(f => f.GetType().Name))}];visits=[{string.Join(';', Visits)}]" +
        (Environment is null ? "" : $";environment={Environment}") + $";call={ToolCallId};operation={OperationRecordId};stop={GatherStopReason};disposition={OperationDisposition};target={TargetActorId};speech={SpeechText};item={ItemId}:{ItemCount}";
}

/// <summary>Runtime diagnostics, deliberately outside business snapshots.</summary>
public sealed class PlaygroundMetrics {
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

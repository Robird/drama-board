using System.Collections.ObjectModel;
using System.Text;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.AcceptedActionKind", 1)]
public enum CycleAcceptedActionKind { Wait, Gather, Go, Continue }
[DurableType("DramaBoard.ResourceCycle.GatherStopReason", 1)]
public enum CycleGatherStopReason { TargetReached, ResourceEmpty, ItemLimit, CapacityLimit, TimeLimit }
[Flags, DurableType("DramaBoard.ResourceCycle.WakeReason", 1)]
public enum CycleWakeReason {
    None = 0, ActivityCompleted = 1, Arrival = 2, ActivityStopped = 4, SurvivalWarning = 8,
    CollectorJoined = 16, ResourceDepleted = 32, Terminal = 64
}
[DurableType("DramaBoard.ResourceCycle.OutcomeActivityState", 1)]
public enum CycleOutcomeActivityState { Ongoing, Completed, Terminal }

/// <summary>A fixed, actor-owned handoff slot. Replacements never mutate a published context.</summary>
[DurableType("DramaBoard.ResourceCycle.DecisionContinuity", 1)]
public sealed partial class CycleDecisionContinuity : IDurableObject {
    public const int MaximumHandoffScalars = 2000;
    [DurableField(1)] private long _contextSequence;
    [DurableField(2)] private CycleActionOutcome? _incoming;
    [DurableField(3)] private string? _draftHandoff;
    [DurableField(4)] private CyclePendingAction? _pending;
    public long ContextSequence => _contextSequence;
    public CycleActionOutcome? Incoming => _incoming;
    public string? DraftHandoff => _draftHandoff;
    public CyclePendingAction? Pending => _pending;
    public CycleDecisionContinuity(long contextSequence = 1, CycleActionOutcome? incoming = null,
        string? draftHandoff = null, CyclePendingAction? pending = null) {
        _contextSequence = contextSequence; _incoming = incoming; _draftHandoff = draftHandoff; _pending = pending;
    }
    internal static bool ValidHandoff(string? text) => text is null || text.EnumerateRunes().Count() <= MaximumHandoffScalars;
    internal void Validate(string actorId, ModelTime time) {
        if (ContextSequence <= 0 || !ValidHandoff(DraftHandoff) || Pending is not null && (Incoming is not null || DraftHandoff is not null)) {
            throw new InvalidOperationException("Invalid decision continuity slot.");
        }
        Pending?.Validate(actorId, time);
        Incoming?.Validate(actorId, time);
        if (Pending is { } pending && pending.Action.SourceContextSequence != ContextSequence ||
            Incoming is { } incoming && (incoming.ActivityState == CycleOutcomeActivityState.Terminal
                ? incoming.Action.SourceContextSequence != ContextSequence
                : incoming.Action.SourceContextSequence != ContextSequence - 1)) {
            throw new InvalidOperationException("The handoff does not belong to this context.");
        }
    }
    public override string ToString() => $"{ContextSequence};incoming={Incoming};draft={DraftHandoff};pending={Pending}";
}

/// <summary>The accepted operation and activity association, with no endpoint-specific message objects.</summary>
[DurableType("DramaBoard.ResourceCycle.AcceptedAction", 1)]
public sealed partial class CycleAcceptedAction : IDurableObject {
    [DurableField(1)] private string _actorId;
    [DurableField(2)] private long _sourceContextSequence;
    [DurableField(3)] private string _toolCallId;
    [DurableField(4)] private string _operationRecordId;
    [DurableField(5)] private CycleAcceptedActionKind _kind;
    [DurableField(6)] private ModelTime _acceptedAt;
    [DurableField(7)] private int _hours;
    [DurableField(8)] private string? _resourceId;
    [DurableField(9)] private int _targetCount;
    [DurableField(10)] private bool _selectedAll;
    [DurableField(11)] private string? _passageId;
    [DurableField(12)] private long? _activityId;
    [DurableField(13)] private long? _movementGeneration;
    public string ActorId => _actorId;
    public long SourceContextSequence => _sourceContextSequence;
    public string ToolCallId => _toolCallId;
    public string OperationRecordId => _operationRecordId;
    public CycleAcceptedActionKind Kind => _kind;
    public ModelTime AcceptedAt => _acceptedAt;
    public int Hours => _hours;
    public string? ResourceId => _resourceId;
    public int TargetCount => _targetCount;
    public bool SelectedAll => _selectedAll;
    public string? PassageId => _passageId;
    public long? ActivityId => _activityId;
    public long? MovementGeneration => _movementGeneration;
    public CycleAcceptedAction(string actorId, long sourceContextSequence, string toolCallId, string operationRecordId,
        CycleAcceptedActionKind kind, ModelTime acceptedAt, int hours = 0, string? resourceId = null,
        int targetCount = 0, bool selectedAll = false, string? passageId = null, long? activityId = null, long? movementGeneration = null) {
        _actorId = actorId; _sourceContextSequence = sourceContextSequence; _toolCallId = toolCallId; _operationRecordId = operationRecordId;
        _kind = kind; _acceptedAt = acceptedAt; _hours = hours; _resourceId = resourceId; _targetCount = targetCount;
        _selectedAll = selectedAll; _passageId = passageId; _activityId = activityId; _movementGeneration = movementGeneration;
    }
    internal void Validate(string actorId, ModelTime time) {
        if (ActorId != actorId || SourceContextSequence <= 0 || string.IsNullOrWhiteSpace(ToolCallId) ||
            string.IsNullOrWhiteSpace(OperationRecordId) || !Enum.IsDefined(Kind) || AcceptedAt.Ticks < 0 || AcceptedAt > time ||
            Hours < 0 || TargetCount < 0 || ActivityId <= 0 || MovementGeneration < 0 ||
            (ActivityId is null) == (MovementGeneration is null)) {
            throw new InvalidOperationException("Invalid accepted action identity or activity association.");
        }
    }
    public override string ToString() => $"{ActorId}:{SourceContextSequence}:{ToolCallId}:{OperationRecordId}:{Kind}@{AcceptedAt.Ticks}:{Hours}:{ResourceId}:{TargetCount}:{SelectedAll}:{PassageId}:{ActivityId}:{MovementGeneration}";
}

/// <summary>Finite personal endpoints; unknown destinations remain absent even when the world knows them.</summary>
[DurableType("DramaBoard.ResourceCycle.ContinuitySnapshot", 1)]
public sealed partial class CycleContinuitySnapshot : IDurableObject {
    [DurableField(1)] private long? _foodUnits;
    [DurableField(2)] private long? _waterUnits;
    [DurableField(3)] private long? _warmthUnits;
    [DurableField(4)] private Dictionary<string, int> _backpack;
    [DurableField(5)] private string? _placeId;
    [DurableField(6)] private string? _passageId;
    [DurableField(7)] private string? _knownTargetPlaceId;
    [DurableField(8)] private long? _activityId;
    [DurableField(9)] private int _completedCount;
    [DurableField(10)] private int _targetCount;
    [DurableField(11)] private ModelTime? _activityDue;
    [DurableField(12)] private string? _placeName;
    [DurableField(13)] private string? _knownTargetPlaceName;
    public long? FoodUnits => _foodUnits;
    public long? WaterUnits => _waterUnits;
    public long? WarmthUnits => _warmthUnits;
    public IReadOnlyDictionary<string, int> Backpack => new ReadOnlyDictionary<string, int>(_backpack);
    public string? PlaceId => _placeId;
    public string? PassageId => _passageId;
    public string? KnownTargetPlaceId => _knownTargetPlaceId;
    public long? ActivityId => _activityId;
    public int CompletedCount => _completedCount;
    public int TargetCount => _targetCount;
    public ModelTime? ActivityDue => _activityDue;
    public string? PlaceName => _placeName;
    public string? KnownTargetPlaceName => _knownTargetPlaceName;
    public CycleContinuitySnapshot(long? foodUnits = null, long? waterUnits = null, long? warmthUnits = null,
        IReadOnlyDictionary<string, int>? backpack = null, string? placeId = null, string? passageId = null,
        string? knownTargetPlaceId = null, long? activityId = null, int completedCount = 0, int targetCount = 0, ModelTime? activityDue = null,
        string? placeName = null, string? knownTargetPlaceName = null) {
        _foodUnits = foodUnits; _waterUnits = waterUnits; _warmthUnits = warmthUnits;
        _backpack = backpack is null ? new(StringComparer.Ordinal) : new(backpack, StringComparer.Ordinal);
        _placeId = placeId; _passageId = passageId; _knownTargetPlaceId = knownTargetPlaceId;
        _activityId = activityId; _completedCount = completedCount; _targetCount = targetCount; _activityDue = activityDue;
        _placeName = placeName; _knownTargetPlaceName = knownTargetPlaceName;
    }
    internal void Validate() {
        if ((FoodUnits is null) != (WaterUnits is null) || (FoodUnits is null) != (WarmthUnits is null) ||
            FoodUnits < 0 || FoodUnits > CycleSurvival.MaximumUnits || WaterUnits < 0 || WaterUnits > CycleSurvival.MaximumUnits ||
            WarmthUnits < 0 || WarmthUnits > CycleSurvival.MaximumUnits ||
            _backpack.Any(i => string.IsNullOrWhiteSpace(i.Key) || i.Value <= 0) ||
            PlaceId is not null && PassageId is not null || KnownTargetPlaceId is not null && PassageId is null ||
            ActivityId <= 0 || CompletedCount < 0 || TargetCount < CompletedCount || ActivityDue?.Ticks < 0) {
            throw new InvalidOperationException("Invalid personal action endpoint.");
        }
    }
    public override string ToString() => $"{FoodUnits}:{WaterUnits}:{WarmthUnits};bag=[{string.Join(';', _backpack.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => $"{i.Key}:{i.Value}"))}];{PlaceId}:{PassageId}:{KnownTargetPlaceId}:{ActivityId}:{CompletedCount}:{TargetCount}:{ActivityDue?.Ticks}";
}

/// <summary>Fixed observable categories with saturating totals, never a growing event list.</summary>
[DurableType("DramaBoard.ResourceCycle.OutcomeSummary", 1)]
public sealed partial class CycleOutcomeSummary : IDurableObject {
    [DurableField(1)] private string? _gatherItemId;
    [DurableField(2)] private long _gatheredCount;
    [DurableField(3)] private long _calmWeatherCount;
    [DurableField(4)] private long _coldWeatherCount;
    [DurableField(5)] private long _weatherWarmthLossUnits;
    [DurableField(6)] private long _lightChangeCount;
    [DurableField(7)] private CycleWakeReason _reasons;
    [DurableField(8)] private int _warningMask;
    [DurableField(9)] private CycleGatherStopReason? _gatherStopReason;
    [DurableField(10)] private bool _isSaturated;
    public string? GatherItemId => _gatherItemId;
    public long GatheredCount => _gatheredCount;
    public long CalmWeatherCount => _calmWeatherCount;
    public long ColdWeatherCount => _coldWeatherCount;
    public long WeatherWarmthLossUnits => _weatherWarmthLossUnits;
    public long LightChangeCount => _lightChangeCount;
    public CycleWakeReason Reasons => _reasons;
    public int WarningMask => _warningMask;
    public CycleGatherStopReason? GatherStopReason => _gatherStopReason;
    public bool IsSaturated => _isSaturated;
    public CycleOutcomeSummary(string? gatherItemId = null, long gatheredCount = 0, long calmWeatherCount = 0,
        long coldWeatherCount = 0, long weatherWarmthLossUnits = 0, long lightChangeCount = 0,
        CycleWakeReason reasons = CycleWakeReason.None, int warningMask = 0,
        CycleGatherStopReason? gatherStopReason = null, bool isSaturated = false) {
        _gatherItemId = gatherItemId; _gatheredCount = gatheredCount; _calmWeatherCount = calmWeatherCount;
        _coldWeatherCount = coldWeatherCount; _weatherWarmthLossUnits = weatherWarmthLossUnits; _lightChangeCount = lightChangeCount;
        _reasons = reasons; _warningMask = warningMask; _gatherStopReason = gatherStopReason; _isSaturated = isSaturated;
    }
    internal void Validate() {
        if (GatheredCount < 0 || CalmWeatherCount < 0 || ColdWeatherCount < 0 || WeatherWarmthLossUnits < 0 || LightChangeCount < 0 ||
            GatheredCount > 0 && string.IsNullOrWhiteSpace(GatherItemId) || ((int)Reasons & ~127) != 0 ||
            (WarningMask & ~7) != 0 || GatherStopReason is { } stop && !Enum.IsDefined(stop)) {
            throw new InvalidOperationException("Invalid bounded action summary.");
        }
    }
    public override string ToString() => $"{GatherItemId}:{GatheredCount}:{CalmWeatherCount}:{ColdWeatherCount}:{WeatherWarmthLossUnits}:{LightChangeCount}:{Reasons}:{WarningMask}:{GatherStopReason}:{IsSaturated}";
}

[DurableType("DramaBoard.ResourceCycle.PendingAction", 1)]
public sealed partial class CyclePendingAction : IDurableObject {
    [DurableField(1)] private CycleAcceptedAction _action;
    [DurableField(2)] private CycleContinuitySnapshot _before;
    [DurableField(3)] private CycleOutcomeSummary _summary;
    [DurableField(4)] private string? _handoff;
    public CycleAcceptedAction Action => _action;
    public CycleContinuitySnapshot Before => _before;
    public CycleOutcomeSummary Summary => _summary;
    public string? Handoff => _handoff;
    public CyclePendingAction(CycleAcceptedAction action, CycleContinuitySnapshot before, CycleOutcomeSummary? summary = null, string? handoff = null) {
        _action = action; _before = before; _summary = summary ?? new(); _handoff = handoff;
    }
    internal void Validate(string actorId, ModelTime time) {
        Action.Validate(actorId, time); Before.Validate(); Summary.Validate();
        if (!CycleDecisionContinuity.ValidHandoff(Handoff)) { throw new InvalidOperationException("Invalid handoff text."); }
    }
    public override string ToString() => $"{Action};before={Before};summary={Summary};handoff={Handoff}";
}

[DurableType("DramaBoard.ResourceCycle.ActionOutcome", 1)]
public sealed partial class CycleActionOutcome : IDurableObject {
    [DurableField(1)] private CyclePendingAction _record;
    [DurableField(2)] private ModelTime _deliveredAt;
    [DurableField(3)] private CycleContinuitySnapshot _after;
    [DurableField(4)] private CycleOutcomeActivityState _activityState;
    public CycleAcceptedAction Action => _record.Action;
    public CycleContinuitySnapshot Before => _record.Before;
    public CycleOutcomeSummary Summary => _record.Summary;
    public string? Handoff => _record.Handoff;
    public ModelTime DeliveredAt => _deliveredAt;
    public CycleContinuitySnapshot After => _after;
    public CycleOutcomeActivityState ActivityState => _activityState;
    public CycleActionOutcome(CyclePendingAction record, ModelTime deliveredAt, CycleContinuitySnapshot after, CycleOutcomeActivityState activityState) {
        _record = record; _deliveredAt = deliveredAt; _after = after; _activityState = activityState;
    }
    internal void Validate(string actorId, ModelTime time) {
        _record.Validate(actorId, time); After.Validate();
        if (!Enum.IsDefined(ActivityState) || DeliveredAt < Action.AcceptedAt || DeliveredAt > time) {
            throw new InvalidOperationException("Invalid sealed action result.");
        }
    }
    public override string ToString() => $"{_record};delivered={DeliveredAt.Ticks};after={After};state={ActivityState}";
}

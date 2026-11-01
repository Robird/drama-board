using Atelia.DurableGraph;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.ActivityKind", 2)]
public enum CycleActivityKind { Wait, Gather, Produce }

[DurableType("DramaBoard.ResourceCycle.ProductionOrder", 1)]
public sealed partial record CycleProductionOrder(
    [field: DurableField(1)] string RecipeId,
    [field: DurableField(2)] int Batches,
    [field: DurableField(3)] string InputItem,
    [field: DurableField(4)] int InputCount,
    [field: DurableField(5)] string OutputItem,
    [field: DurableField(6)] int OutputCount) : IDurableObject;

/// <summary>A committed arrangement, including the current cycle's duration snapshot.</summary>
[DurableType("DramaBoard.ResourceCycle.Activity", 2)]
public sealed partial class CycleActivity : IDurableObject {
    [DurableField(1)] private CycleActivityKind _kind;
    [DurableField(2)] private long _id;
    [DurableField(3)] private ModelTime _startedAt;
    [DurableField(4)] private long _durationTicks;
    [DurableField(5)] private ModelTime _due;
    [DurableField(6)] private string? _resourceId;
    [DurableField(7)] private int _targetCount;
    [DurableField(8)] private int _completedCount;
    [DurableField(9)] private bool _selectedAll;
    [DurableField(10)] private int _cycleIndex;
    [DurableField(11)] private CycleProductionOrder? _production;
    public CycleProductionOrder? Production => _production;
    public CycleActivityKind Kind => _kind;
    public long Id => _id;
    public ModelTime StartedAt => _startedAt;
    public long DurationTicks => _durationTicks;
    public ModelTime Due => _due;
    public string? ResourceId => _resourceId;
    public int TargetCount => _targetCount;
    public int CompletedCount => _completedCount;
    public bool SelectedAll => _selectedAll;
    public int CycleIndex => _cycleIndex;
    public CycleActivity(CycleActivityKind kind, long id, ModelTime startedAt, long durationTicks, ModelTime due,
        string? resourceId = null, int targetCount = 0, int completedCount = 0, bool selectedAll = false, int cycleIndex = 0, CycleProductionOrder? production = null) {
        _kind = kind; _id = id; _startedAt = startedAt; _durationTicks = durationTicks; _due = due;
        _resourceId = resourceId; _targetCount = targetCount; _completedCount = completedCount;
        _selectedAll = selectedAll; _cycleIndex = cycleIndex;
        _production = production;
    }
    internal void Validate(ModelTime time) {
        if (!Enum.IsDefined(Kind) || Id <= 0 || StartedAt.Ticks < 0 || StartedAt > time || Due < time ||
            DurationTicks <= 0 || Due.Ticks - StartedAt.Ticks != DurationTicks || CycleIndex < 0 ||
            (Kind == CycleActivityKind.Gather
                ? string.IsNullOrWhiteSpace(ResourceId) || TargetCount <= 0 || CompletedCount < 0 || CompletedCount >= TargetCount || CycleIndex != CompletedCount
                : ResourceId is not null || TargetCount != 0 || CompletedCount != 0 || SelectedAll || CycleIndex != 0)) {
            throw new InvalidOperationException("Invalid committed activity.");
        }
        if ((Kind == CycleActivityKind.Produce) != (Production is not null)) { throw new InvalidOperationException("Production requires its escrow record."); }
        if (Production is { } order) {
            if (string.IsNullOrWhiteSpace(order.RecipeId) || string.IsNullOrWhiteSpace(order.InputItem) || string.IsNullOrWhiteSpace(order.OutputItem) ||
                order.Batches is < 1 or > 1000 || order.InputCount <= 0 || order.OutputCount <= 0) {
                throw new InvalidOperationException("Invalid production escrow structure.");
            }
        }
    }
    internal void Validate(ModelTime time, CycleWorldDefinition definition) {
        Validate(time);
        if (Production is { } order) {
            CycleRecipe? recipe = definition.Recipe(order.RecipeId);
            if (recipe is null || order.InputItem != recipe.InputItem || order.OutputItem != recipe.OutputItem ||
                order.InputCount != (long)recipe.InputCount * order.Batches || order.OutputCount != (long)recipe.OutputCount * order.Batches ||
                DurationTicks != checked(recipe.DurationTicks * order.Batches)) { throw new InvalidOperationException("Production escrow does not match the world's recipe."); }
        }
    }
    public override string ToString() => $"{Kind}:{Id}:{StartedAt.Ticks}:{DurationTicks}:{Due.Ticks}:{ResourceId}:{TargetCount}:{CompletedCount}:{SelectedAll}:{CycleIndex}:{Production}";
}

[DurableType("DramaBoard.ResourceCycle.NoticeKind", 1)]
public enum CycleNoticeKind { CollectorJoined, ResourceDepleted }

/// <summary>Bounded causal perception, recorded in business facts rather than cache callbacks.</summary>
[DurableType("DramaBoard.ResourceCycle.Notice", 1)]
public sealed partial class CycleNotice : IDurableObject {
    [DurableField(1)] private CycleNoticeKind _kind;
    [DurableField(2)] private string _resourceId;
    [DurableField(3)] private string? _actorId;
    [DurableField(4)] private string _sourceId;
    [DurableField(5)] private ModelTime _at;
    [DurableField(6)] private string _reason;
    [DurableField(7)] private int _count;
    public CycleNoticeKind Kind => _kind;
    public string ResourceId => _resourceId;
    public string? ActorId => _actorId;
    public string SourceId => _sourceId;
    public ModelTime At => _at;
    public string Reason => _reason;
    public int Count => _count;
    public CycleNotice(CycleNoticeKind kind, string resourceId, string? actorId, string sourceId, ModelTime at, string reason, int count = 1) {
        _kind = kind; _resourceId = resourceId; _actorId = actorId; _sourceId = sourceId; _at = at; _reason = reason; _count = count;
    }
    internal string AggregationKey => $"{Kind}:{ResourceId}:{ActorId}";
    public override string ToString() => $"{Kind}:{ResourceId}:{ActorId}:{SourceId}:{At.Ticks}:{Count}:{Reason}";
}

[DurableType("DramaBoard.ResourceCycle.Character", 9)]
public sealed partial class CycleCharacter : IDurableObject {
    [DurableField(1)] private string _id;
    [DurableField(2)] private string _name;
    [DurableField(3)] private int _skillLevel;
    [DurableField(4)] private ItemInventory _backpack;
    [DurableField(5)] private CycleActivity? _activity;
    [DurableField(6)] private List<CycleNotice> _notices;
    [DurableField(7)] private long _decisionGeneration;
    [DurableField(8)] private string? _lastResult;
    [DurableField(9)] private long _nextActivityId;
    [DurableField(10)] private CycleSurvival? _survival;
    [DurableField(11)] private CycleCharacterEnding? _ending;
    [DurableField(12)] private CycleCommunicationState _communication;
    [DurableField(13)] private string? _followingLeaderId;
    [DurableField(14)] private long _money;
    public long Money => _money;
    public string? FollowingLeaderId => _followingLeaderId;
    public CycleCommunicationState Communication => _communication;
    public CycleCharacterEnding? Ending => _ending;
    public bool IsActive => Ending is null;
    public string Id => _id;
    public string Name => _name;
    public int SkillLevel => _skillLevel;
    public ItemInventory Backpack => _backpack;
    public CycleActivity? Activity => _activity;
    public IReadOnlyList<CycleNotice> Notices => _notices.AsReadOnly();
    public long DecisionGeneration => _decisionGeneration;
    public string? LastResult => _lastResult;
    public long NextActivityId => _nextActivityId;
    public CycleSurvival? Survival => _survival;
    public CycleCharacter(string id, string name, int skillLevel = 0, ItemInventory? backpack = null,
        CycleActivity? activity = null, IEnumerable<CycleNotice>? notices = null, long decisionGeneration = 0,
        string? lastResult = null, long nextActivityId = 1, CycleSurvival? survival = null, CycleCharacterEnding? ending = null, CycleCommunicationState? communication = null, string? followingLeaderId = null, long money = 0) {
        _id = id; _name = name; _skillLevel = skillLevel; _backpack = backpack ?? new(); _activity = activity;
        _notices = notices?.ToList() ?? []; _decisionGeneration = decisionGeneration; _lastResult = lastResult; _nextActivityId = nextActivityId;
        _survival = survival; _ending = ending; _communication = communication ?? new(new(0), 0, false);
        _followingLeaderId = followingLeaderId;
        _money = money;
    }
    internal CycleCharacter Change(CycleActivity? activity, ItemInventory? backpack = null,
        IEnumerable<CycleNotice>? notices = null, string? result = null, long? nextActivityId = null, CycleSurvival? survival = null, CycleCommunicationState? communication = null, long? money = null) =>
        new(Id, Name, SkillLevel, backpack ?? Backpack, activity, notices ?? _notices,
            checked(DecisionGeneration + 1), result ?? LastResult, nextActivityId ?? NextActivityId, survival ?? Survival, Ending, communication ?? Communication, FollowingLeaderId, money ?? Money);
    internal CycleCharacter WithSurvival(CycleSurvival survival) => new(Id, Name, SkillLevel, Backpack, Activity,
        Notices, DecisionGeneration, LastResult, NextActivityId, survival, Ending, Communication, FollowingLeaderId, Money);
    internal CycleCharacter AdvanceDecisionGeneration() =>
        new(Id, Name, SkillLevel, Backpack, Activity, Notices, checked(DecisionGeneration + 1),
            LastResult, NextActivityId, Survival, Ending, Communication, FollowingLeaderId, Money);
    internal CycleCharacter WithFollowing(string? leaderId, bool acknowledge = false, string? result = null) =>
        new(Id, Name, SkillLevel, Backpack, Activity, acknowledge ? [] : Notices, checked(DecisionGeneration + 1),
            result ?? LastResult, NextActivityId, Survival, Ending, acknowledge ? Communication.Acknowledge() : Communication, leaderId, Money);
    internal CycleCharacter End(CycleExpeditionOutcome outcome, ModelTime at, string reason, SpatialLocation location) {
        if (!IsActive || outcome == CycleExpeditionOutcome.Exploring) { throw new InvalidOperationException("A personal ending is final."); }
        return new(Id, Name, SkillLevel, CycleEconomyRules.ReturnActivityInputs(this), null, [], checked(DecisionGeneration + 1), reason,
            NextActivityId, Survival?.Freeze(at), new(outcome, at, reason, location), Communication.Acknowledge(), money: Money);
    }
    internal CycleCharacter Notify(CycleNotice notice) {
        CycleNotice? previous = _notices.SingleOrDefault(n => n.AggregationKey == notice.AggregationKey);
        if (previous?.SourceId == notice.SourceId) { return this; }
        CycleNotice merged = previous is null ? notice : new(notice.Kind, notice.ResourceId, notice.ActorId,
            notice.SourceId, notice.At, notice.Reason, previous.Count == int.MaxValue ? int.MaxValue : previous.Count + 1);
        return Change(Activity, notices: _notices.Where(n => n.AggregationKey != notice.AggregationKey).Append(merged)
            .OrderBy(n => n.AggregationKey, StringComparer.Ordinal));
    }
    internal void Validate(ModelTime time) {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || SkillLevel < 0 || Backpack is null ||
            Money < 0 || DecisionGeneration < 0 || NextActivityId <= 0 || (Activity is { } activity && activity.Id >= NextActivityId) ||
            _notices.Select(n => n.AggregationKey).Distinct(StringComparer.Ordinal).Count() != _notices.Count ||
            _notices.Any(n => !Enum.IsDefined(n.Kind) || string.IsNullOrWhiteSpace(n.ResourceId) || string.IsNullOrWhiteSpace(n.SourceId) ||
                string.IsNullOrWhiteSpace(n.Reason) || n.At.Ticks < 0 || n.At > time || n.Count <= 0)) {
            throw new InvalidOperationException("Invalid character or pending perception state.");
        }
        if (Ending is { } ending && (!Enum.IsDefined(ending.Outcome) || ending.Outcome == CycleExpeditionOutcome.Exploring ||
            ending.At.Ticks < 0 || ending.At > time || string.IsNullOrWhiteSpace(ending.Reason) || ending.Location is null ||
            ending.Outcome == CycleExpeditionOutcome.Dead && Survival is null ||
            Activity is not null || Notices.Count != 0 || Survival is { } body && (body.BaselineAt != ending.At ||
                body.FoodPerHour != 0 || body.WaterPerHour != 0 || body.WarmthPerHour != 0 || body.PendingMask != 0))) {
            throw new InvalidOperationException("Invalid personal ending or unfrozen ended character.");
        }
        if (Communication is null || !IsActive && Communication.AttentionPending) { throw new InvalidOperationException("Invalid character communication state."); }
        if (FollowingLeaderId is not null && (string.IsNullOrWhiteSpace(FollowingLeaderId) || FollowingLeaderId == Id || !IsActive || Activity is not null)) {
            throw new InvalidOperationException("Following requires an active character without an independent activity.");
        }
        Communication.Validate(time);
        Backpack.Validate();
        Activity?.Validate(time);
        if (!CycleEconomyRules.CanSettleActivity(this, Backpack)) { throw new InvalidOperationException("Production completion and cancellation must both remain possible."); }
        Survival?.Validate();
        if (Survival?.BaselineAt > time) { throw new InvalidOperationException("Survival anchor cannot be in the future."); }
    }
    internal void Validate(ModelTime time, CycleWorldDefinition definition) {
        Validate(time);
        Activity?.Validate(time, definition);
    }
    public override string ToString() => $"{Id}:{Name}:{SkillLevel};backpack={Backpack};activity={Activity};decision={DecisionGeneration};next={NextActivityId};notices=[{string.Join(';', _notices)}];result={LastResult}" +
        (Survival is null ? "" : $";survival={Survival}") + $";ending={Ending};communication={Communication};following={FollowingLeaderId};money={Money}";
}

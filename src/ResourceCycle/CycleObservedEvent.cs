using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.StateKey", 1)]
public readonly partial record struct CycleStateKey {
    [field: DurableField(1)] public string Category { get; }
    [field: DurableField(2)] public string ObjectId { get; }
    [field: DurableField(3)] public string Field { get; }
    public CycleStateKey(string category, string objectId, string field) { Category = category; ObjectId = objectId; Field = field; }
}

[DurableType("DramaBoard.ResourceCycle.VisibleValueKind", 1)]
public enum CycleVisibleValueKind { Number, Boolean, Text, KnownAbsent, Unobserved }

/// <summary>Exact finite semantic values. No display markup or arbitrary object payloads.</summary>
[DurableType("DramaBoard.ResourceCycle.VisibleValue", 1)]
public readonly partial record struct CycleVisibleValue {
    [field: DurableField(1)] public CycleVisibleValueKind Kind { get; }
    [field: DurableField(2)] public long Units { get; }
    [field: DurableField(3)] public long Scale { get; }
    [field: DurableField(4)] public string? Unit { get; }
    [field: DurableField(5)] public string? Text { get; }
    [field: DurableField(6)] public bool Boolean { get; }
    public decimal Number => Kind == CycleVisibleValueKind.Number ? (decimal)Units / Scale : throw new InvalidOperationException("Not a numeric value.");
    private CycleVisibleValue(CycleVisibleValueKind kind, long units = 0, long scale = 1, string? unit = null, string? text = null, bool boolean = false) {
        Kind = kind; Units = units; Scale = scale; Unit = unit; Text = text; Boolean = boolean;
    }
    public static CycleVisibleValue Numeric(long units, string unit = "", long scale = 1) => scale > 0 ? new(CycleVisibleValueKind.Number, units, scale, unit) : throw new ArgumentOutOfRangeException(nameof(scale));
    public static CycleVisibleValue Flag(bool value) => new(CycleVisibleValueKind.Boolean, boolean: value);
    public static CycleVisibleValue Literal(string value) => new(CycleVisibleValueKind.Text, text: value ?? throw new ArgumentNullException(nameof(value)));
    public static CycleVisibleValue KnownAbsent => new(CycleVisibleValueKind.KnownAbsent);
    public static CycleVisibleValue Unobserved => new(CycleVisibleValueKind.Unobserved);
    public override string ToString() => Kind switch {
        CycleVisibleValueKind.Number => $"{Number} {Unit}",
        CycleVisibleValueKind.Boolean => Boolean.ToString(),
        CycleVisibleValueKind.Text => Text ?? "",
        _ => Kind.ToString()
    };
    internal void Validate() {
        if (!Enum.IsDefined(Kind) || Scale <= 0 || Kind == CycleVisibleValueKind.Text && Text is null ||
            Kind != CycleVisibleValueKind.Number && (Units != 0 || Scale != 1 || Unit is not null) ||
            Kind != CycleVisibleValueKind.Text && Text is not null || Kind != CycleVisibleValueKind.Boolean && Boolean) {
            throw new InvalidOperationException("Malformed visible value.");
        }
    }
}

[DurableType("DramaBoard.ResourceCycle.ObservedEventKind", 5)]
public enum CycleObservedEventKind {
    BodyChanged, ResourceRegenerated, LightChanged, ResourceDestroyed, WaitStarted, WaitCompleted,
    GatherStarted, GatherYielded, GatherFinished, ActivityCancelled, AttentionAcknowledged, MoveStarted, MoveArrived,
    SurvivalWarning, SurvivalFailed, ItemUsed, WeatherResolved, HandoffEdited, ExpeditionEnded, SpeechSent, ItemGiven,
    CommunicationBudgetRenewed, FollowStarted, FollowEnded, FollowersDismissed, SpeechShouted, MarketBought, MarketSold, ProductionStarted, ProductionCompleted, MarketRefreshed, MoneyGiven
}

/// <summary>Signed communication, not independently verified geography or another character's private state.</summary>
[DurableType("DramaBoard.ResourceCycle.ObservedCommunication", 2)]
public sealed partial record CycleObservedCommunication(
    [field: DurableField(1)] string SenderId,
    [field: DurableField(2)] string SenderName,
    [field: DurableField(3)] string RecipientId,
    [field: DurableField(4)] string RecipientName,
    [field: DurableField(5)] string? SpeechText,
    [field: DurableField(6)] string? ItemId,
    [field: DurableField(7)] int ItemCount,
    [field: DurableField(8)] long MoneyAmount = 0) : IDurableObject {
    internal void Validate(string observerId, CycleObservedEventKind kind) {
        if (string.IsNullOrWhiteSpace(SenderId) || string.IsNullOrWhiteSpace(SenderName) ||
            string.IsNullOrWhiteSpace(RecipientId) || string.IsNullOrWhiteSpace(RecipientName) ||
            SenderId == RecipientId || observerId != SenderId && observerId != RecipientId ||
            !(kind switch {
                CycleObservedEventKind.SpeechSent => CycleCommunicationRules.ValidSpeech(SpeechText) && ItemId is null && ItemCount == 0 && MoneyAmount == 0,
                CycleObservedEventKind.ItemGiven => SpeechText is null && !string.IsNullOrWhiteSpace(ItemId) && ItemCount > 0 && MoneyAmount == 0,
                CycleObservedEventKind.MoneyGiven => SpeechText is null && ItemId is null && ItemCount == 0 && MoneyAmount > 0,
                _ => false
            })) {
            throw new InvalidOperationException("Invalid signed communication material.");
        }
    }
}

[DurableType("DramaBoard.ResourceCycle.ObservedEffect", 1)]
public sealed partial class CycleObservedEffect : IDurableObject {
    [DurableField(1)] private CycleStateKey _key;
    [DurableField(2)] private CycleVisibleValue _before;
    [DurableField(3)] private CycleVisibleValue _after;
    public CycleStateKey Key => _key;
    public CycleVisibleValue Before => _before;
    public CycleVisibleValue After => _after;
    public CycleObservedEffect(CycleStateKey key, CycleVisibleValue before, CycleVisibleValue after) { _key = key; _before = before; _after = after; }
}

/// <summary>Experienced facts frozen in E; effects explain, but never execute, recorded business replacements.</summary>
[DurableType("DramaBoard.ResourceCycle.ObservedEvent", 5)]
public sealed partial class CycleObservedEvent : IDurableObject {
    [DurableField(1)] private int _eventOrdinal;
    [DurableField(2)] private string _observerId;
    [DurableField(3)] private CycleObservedEventKind _kind;
    [DurableField(4)] private ModelTime _from;
    [DurableField(5)] private ModelTime _at;
    [DurableField(6)] private string? _subjectId;
    [DurableField(7)] private string? _subjectName;
    [DurableField(8)] private string? _detail;
    [DurableField(9)] private List<CycleObservedEffect> _effects;
    [DurableField(10)] private CycleObservedCommunication? _communication;
    [DurableField(11)] private CycleProductionOrder? _production;
    public CycleProductionOrder? Production => _production;
    public int EventOrdinal => _eventOrdinal;
    public string ObserverId => _observerId;
    public CycleObservedEventKind Kind => _kind;
    public ModelTime From => _from;
    public ModelTime At => _at;
    public string? SubjectId => _subjectId;
    public string? SubjectName => _subjectName;
    public string? Detail => _detail;
    public IReadOnlyList<CycleObservedEffect> Effects => _effects.AsReadOnly();
    public CycleObservedCommunication? Communication => _communication;
    public CycleObservedEvent(int eventOrdinal, string observerId, CycleObservedEventKind kind, ModelTime from, ModelTime at,
        string? subjectId = null, string? subjectName = null, string? detail = null, IEnumerable<CycleObservedEffect>? effects = null,
        CycleObservedCommunication? communication = null, CycleProductionOrder? production = null) {
        _eventOrdinal = eventOrdinal; _observerId = observerId; _kind = kind; _from = from; _at = at;
        _subjectId = subjectId; _subjectName = subjectName; _detail = detail; _effects = effects?.ToList() ?? [];
        _communication = communication;
        _production = production;
    }
}

using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.StateKey", 1)]
public readonly partial record struct PlaygroundStateKey {
    [field: DurableField(1)] public string Category { get; }
    [field: DurableField(2)] public string ObjectId { get; }
    [field: DurableField(3)] public string Field { get; }
    public PlaygroundStateKey(string category, string objectId, string field) { Category = category; ObjectId = objectId; Field = field; }
}

[DurableType("DramaBoard.Playground.VisibleValueKind", 1)]
public enum PlaygroundVisibleValueKind { Number, Boolean, Text, KnownAbsent, Unobserved }

/// <summary>Exact finite semantic values. No display markup or arbitrary object payloads.</summary>
[DurableType("DramaBoard.Playground.VisibleValue", 1)]
public readonly partial record struct PlaygroundVisibleValue {
    [field: DurableField(1)] public PlaygroundVisibleValueKind Kind { get; }
    [field: DurableField(2)] public long Units { get; }
    [field: DurableField(3)] public long Scale { get; }
    [field: DurableField(4)] public string? Unit { get; }
    [field: DurableField(5)] public string? Text { get; }
    [field: DurableField(6)] public bool Boolean { get; }
    public decimal Number => Kind == PlaygroundVisibleValueKind.Number ? (decimal)Units / Scale : throw new InvalidOperationException("Not a numeric value.");
    private PlaygroundVisibleValue(PlaygroundVisibleValueKind kind, long units = 0, long scale = 1, string? unit = null, string? text = null, bool boolean = false) {
        Kind = kind; Units = units; Scale = scale; Unit = unit; Text = text; Boolean = boolean;
    }
    public static PlaygroundVisibleValue Numeric(long units, string unit = "", long scale = 1) => scale > 0 ? new(PlaygroundVisibleValueKind.Number, units, scale, unit) : throw new ArgumentOutOfRangeException(nameof(scale));
    public static PlaygroundVisibleValue Flag(bool value) => new(PlaygroundVisibleValueKind.Boolean, boolean: value);
    public static PlaygroundVisibleValue Literal(string value) => new(PlaygroundVisibleValueKind.Text, text: value ?? throw new ArgumentNullException(nameof(value)));
    public static PlaygroundVisibleValue KnownAbsent => new(PlaygroundVisibleValueKind.KnownAbsent);
    public static PlaygroundVisibleValue Unobserved => new(PlaygroundVisibleValueKind.Unobserved);
    public override string ToString() => Kind switch {
        PlaygroundVisibleValueKind.Number => $"{Number} {Unit}",
        PlaygroundVisibleValueKind.Boolean => Boolean.ToString(),
        PlaygroundVisibleValueKind.Text => Text ?? "",
        _ => Kind.ToString()
    };
    internal void Validate() {
        if (!Enum.IsDefined(Kind) || Scale <= 0 || Kind == PlaygroundVisibleValueKind.Text && Text is null ||
            Kind != PlaygroundVisibleValueKind.Number && (Units != 0 || Scale != 1 || Unit is not null) ||
            Kind != PlaygroundVisibleValueKind.Text && Text is not null || Kind != PlaygroundVisibleValueKind.Boolean && Boolean) {
            throw new InvalidOperationException("Malformed visible value.");
        }
    }
}

[DurableType("DramaBoard.Playground.ObservedEventKind", 1)]
public enum PlaygroundObservedEventKind {
    BodyChanged, ResourceRegenerated, LightChanged, ResourceDestroyed, WaitStarted, WaitCompleted,
    GatherStarted, GatherYielded, GatherFinished, ActivityCancelled, AttentionAcknowledged, MoveStarted, MoveArrived,
    SurvivalWarning, SurvivalFailed, ItemUsed, WeatherResolved, HandoffEdited, ExpeditionEnded, SpeechSent, ItemGiven,
    CommunicationBudgetRenewed, FollowStarted, FollowEnded, FollowersDismissed, SpeechShouted, MarketBought, MarketSold, ProductionStarted, ProductionCompleted, MarketRefreshed, MoneyGiven,
    TradeOffered, TradeAccepted, TradeRejected, TradeWithdrawn, TradesExpired, TradeParticipantFailed
}

/// <summary>Signed communication, not independently verified geography or another character's private state.</summary>
[DurableType("DramaBoard.Playground.ObservedCommunication", 1)]
public sealed partial record PlaygroundObservedCommunication(
    [field: DurableField(1)] string SenderId,
    [field: DurableField(2)] string SenderName,
    [field: DurableField(3)] string RecipientId,
    [field: DurableField(4)] string RecipientName,
    [field: DurableField(5)] string? SpeechText,
    [field: DurableField(6)] string? ItemId,
    [field: DurableField(7)] int ItemCount,
    [field: DurableField(8)] long MoneyAmount = 0) : IDurableObject {
    internal void Validate(string observerId, PlaygroundObservedEventKind kind) {
        if (string.IsNullOrWhiteSpace(SenderId) || string.IsNullOrWhiteSpace(SenderName) ||
            string.IsNullOrWhiteSpace(RecipientId) || string.IsNullOrWhiteSpace(RecipientName) ||
            SenderId == RecipientId || observerId != SenderId && observerId != RecipientId ||
            !(kind switch {
                PlaygroundObservedEventKind.SpeechSent => PlaygroundCommunicationRules.ValidSpeech(SpeechText) && ItemId is null && ItemCount == 0 && MoneyAmount == 0,
                PlaygroundObservedEventKind.ItemGiven => SpeechText is null && !string.IsNullOrWhiteSpace(ItemId) && ItemCount > 0 && MoneyAmount == 0,
                PlaygroundObservedEventKind.MoneyGiven => SpeechText is null && ItemId is null && ItemCount == 0 && MoneyAmount > 0,
                _ => false
            })) {
            throw new InvalidOperationException("Invalid signed communication material.");
        }
    }
}

[DurableType("DramaBoard.Playground.ObservedEffect", 1)]
public sealed partial class PlaygroundObservedEffect : IDurableObject {
    [DurableField(1)] private PlaygroundStateKey _key;
    [DurableField(2)] private PlaygroundVisibleValue _before;
    [DurableField(3)] private PlaygroundVisibleValue _after;
    public PlaygroundStateKey Key => _key;
    public PlaygroundVisibleValue Before => _before;
    public PlaygroundVisibleValue After => _after;
    public PlaygroundObservedEffect(PlaygroundStateKey key, PlaygroundVisibleValue before, PlaygroundVisibleValue after) { _key = key; _before = before; _after = after; }
}

/// <summary>Experienced facts frozen in E; effects explain, but never execute, recorded business replacements.</summary>
[DurableType("DramaBoard.Playground.ObservedEvent", 1)]
public sealed partial class PlaygroundObservedEvent : IDurableObject {
    [DurableField(1)] private int _eventOrdinal;
    [DurableField(2)] private string _observerId;
    [DurableField(3)] private PlaygroundObservedEventKind _kind;
    [DurableField(4)] private ModelTime _from;
    [DurableField(5)] private ModelTime _at;
    [DurableField(6)] private string? _subjectId;
    [DurableField(7)] private string? _subjectName;
    [DurableField(8)] private string? _detail;
    [DurableField(9)] private List<PlaygroundObservedEffect> _effects;
    [DurableField(10)] private PlaygroundObservedCommunication? _communication;
    [DurableField(11)] private PlaygroundProductionOrder? _production;
    public PlaygroundProductionOrder? Production => _production;
    [DurableField(12)] private List<PlaygroundTradeOffer> _tradeChanges;
    public IReadOnlyList<PlaygroundTradeOffer> TradeChanges => _tradeChanges.AsReadOnly();
    internal void ValidateTradeChanges() {
        PlaygroundTradeStatus? status = Kind switch {
            PlaygroundObservedEventKind.TradeOffered => PlaygroundTradeStatus.Active,
            PlaygroundObservedEventKind.TradeAccepted => PlaygroundTradeStatus.Accepted,
            PlaygroundObservedEventKind.TradeRejected => PlaygroundTradeStatus.Rejected,
            PlaygroundObservedEventKind.TradeWithdrawn => PlaygroundTradeStatus.Withdrawn,
            PlaygroundObservedEventKind.TradesExpired => PlaygroundTradeStatus.Expired,
            PlaygroundObservedEventKind.TradeParticipantFailed => PlaygroundTradeStatus.ParticipantFailed,
            _ => null
        };
        if ((status is null) != (TradeChanges.Count == 0) || TradeChanges.Select(offer => offer.Id).Distinct(StringComparer.Ordinal).Count() != TradeChanges.Count ||
            TradeChanges.Any(offer => offer.Status != status)) {
            throw new InvalidOperationException("Trade experiences require exact participant terms and the matching transition.");
        }
        foreach (PlaygroundTradeOffer offer in TradeChanges) { PlaygroundEconomyObservation.ValidateVisibleTrade(offer, ObserverId, At); }
    }
    public int EventOrdinal => _eventOrdinal;
    public string ObserverId => _observerId;
    public PlaygroundObservedEventKind Kind => _kind;
    public ModelTime From => _from;
    public ModelTime At => _at;
    public string? SubjectId => _subjectId;
    public string? SubjectName => _subjectName;
    public string? Detail => _detail;
    public IReadOnlyList<PlaygroundObservedEffect> Effects => _effects.AsReadOnly();
    public PlaygroundObservedCommunication? Communication => _communication;
    public PlaygroundObservedEvent(int eventOrdinal, string observerId, PlaygroundObservedEventKind kind, ModelTime from, ModelTime at,
        string? subjectId = null, string? subjectName = null, string? detail = null, IEnumerable<PlaygroundObservedEffect>? effects = null,
        PlaygroundObservedCommunication? communication = null, PlaygroundProductionOrder? production = null, IEnumerable<PlaygroundTradeOffer>? tradeChanges = null) {
        _eventOrdinal = eventOrdinal; _observerId = observerId; _kind = kind; _from = from; _at = at;
        _subjectId = subjectId; _subjectName = subjectName; _detail = detail; _effects = effects?.ToList() ?? [];
        _communication = communication;
        _production = production;
        _tradeChanges = tradeChanges?.ToList() ?? [];
    }
}

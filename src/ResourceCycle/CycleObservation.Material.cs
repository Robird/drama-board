using Atelia.DurableGraph;
using System.Collections.ObjectModel;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Only the requested character's own state. No other character or world graph is reachable.</summary>
[DurableType("DramaBoard.ResourceCycle.ActorObservation", 1)]
public sealed partial record CycleActorObservation(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Name,
    [field: DurableField(3)] int SkillLevel,
    [field: DurableField(4)] ItemInventory Backpack,
    [field: DurableField(5)] CycleActivity? Activity,
    IReadOnlyList<CycleNotice> Notices,
    [field: DurableField(7)] string? LastResult,
    [field: DurableField(8)] long NextActivityId) : IDurableObject {
    [DurableField(6)] private List<CycleNotice> _notices = new(Notices);
    public IReadOnlyList<CycleNotice> Notices { get => _notices.AsReadOnly(); init => _notices = new(value); }
}

[DurableType("DramaBoard.ResourceCycle.OtherCharacterObservation", 1)]
public sealed partial record CycleOtherCharacterObservation(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Name,
    [field: DurableField(3)] string? GatheringResourceId,
    [field: DurableField(4)] bool Waiting) : IDurableObject;

[DurableType("DramaBoard.ResourceCycle.PassageObservation", 1)]
public sealed partial record CyclePassageObservation(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Description,
    [field: DurableField(3)] long DurationTicks,
    [field: DurableField(4)] string? KnownDestinationName) : IDurableObject;

[DurableType("DramaBoard.ResourceCycle.PlaceMemory", 1)]
public sealed partial record CyclePlaceMemory(
    [field: DurableField(1)] string Name,
    [field: DurableField(2)] ModelTime At,
    IReadOnlyList<CycleResource> Resources,
    [field: DurableField(4)] string? PlaceId = null) : IDurableObject {
    [DurableField(3)] private List<CycleResource> _resources = new(Resources);
    public IReadOnlyList<CycleResource> Resources { get => _resources.AsReadOnly(); init => _resources = new(value); }
}

[DurableType("DramaBoard.ResourceCycle.NoticeObservation", 1)]
public sealed partial record CycleNoticeObservation(
    [field: DurableField(1)] CycleNotice Notice,
    [field: DurableField(2)] string ResourceName,
    [field: DurableField(3)] string ActorLabel,
    [field: DurableField(4)] bool? StillGathering,
    [field: DurableField(5)] int? CurrentStock,
    [field: DurableField(6)] int? Capacity) : IDurableObject;

[DurableType("DramaBoard.ResourceCycle.CommandCheck", 1)]
public sealed partial record CycleCommandCheck(
    [field: DurableField(1)] CycleCommand Command,
    [field: DurableField(2)] string? Rejection,
    [field: DurableField(3)] string? Warning = null) : IDurableObject;

/// <summary>Pure visible data: current senses, private state and explicit historical observations.</summary>
/// <remarks>Resource and inventory values are immutable snapshots. Hidden geography, distant current
/// stock, continuity and validation delegates are deliberately absent. Saved materials never capture
/// a controller or world; this projection is not a security sandbox.</remarks>
[DurableType("DramaBoard.ResourceCycle.Observation", 4)]
public sealed partial class CycleObservation : IDurableObject {
    [DurableField(1)] private ModelTime _time;
    public required ModelTime Time { get => _time; init => _time = value; }
    [DurableField(2)] private bool _isDay;
    public required bool IsDay { get => _isDay; init => _isDay = value; }
    [DurableField(3)] private ModelTime _nextLightChange;
    public required ModelTime NextLightChange { get => _nextLightChange; init => _nextLightChange = value; }
    [DurableField(4)] private CycleActorObservation _actor = null!;
    public required CycleActorObservation Actor { get => _actor; init => _actor = value; }
    [DurableField(5)] private List<CycleResource> _resources = [];
    public required IReadOnlyList<CycleResource> Resources { get => _resources.AsReadOnly(); init => _resources = new(value); }
    [DurableField(6)] private Dictionary<string, long> _harvestDurations = new(StringComparer.Ordinal);
    public required IReadOnlyDictionary<string, long> HarvestDurations { get => new ReadOnlyDictionary<string, long>(_harvestDurations); init => _harvestDurations = new(value, StringComparer.Ordinal); }
    [DurableField(7)] private Dictionary<string, string> _itemNames = new(StringComparer.Ordinal);
    public required IReadOnlyDictionary<string, string> ItemNames { get => new ReadOnlyDictionary<string, string>(_itemNames); init => _itemNames = new(value, StringComparer.Ordinal); }
    [DurableField(8)] private List<CycleOtherCharacterObservation> _otherCharacters = [];
    public required IReadOnlyList<CycleOtherCharacterObservation> OtherCharacters { get => _otherCharacters.AsReadOnly(); init => _otherCharacters = new(value); }
    [DurableField(9)] private List<CycleNoticeObservation> _notices = [];
    public required IReadOnlyList<CycleNoticeObservation> Notices { get => _notices.AsReadOnly(); init => _notices = new(value); }
    [DurableField(10)] private List<CycleCommandCheck> _commands = [];
    public required IReadOnlyList<CycleCommandCheck> Commands { get => _commands.AsReadOnly(); init => _commands = new(value); }
    [DurableField(11)] private bool _isExpedition;
    public bool IsExpedition { get => _isExpedition; init => _isExpedition = value; }
    [DurableField(12)] private string? _placeId;
    public string? PlaceId { get => _placeId; init => _placeId = value; }
    [DurableField(13)] private string? _placeName;
    public string? PlaceName { get => _placeName; init => _placeName = value; }
    [DurableField(14)] private string? _placeDescription;
    public string? PlaceDescription { get => _placeDescription; init => _placeDescription = value; }
    [DurableField(15)] private ModelTime? _arrivalDue;
    public ModelTime? ArrivalDue { get => _arrivalDue; init => _arrivalDue = value; }
    [DurableField(16)] private List<CyclePassageObservation> _passages = [];
    public IReadOnlyList<CyclePassageObservation> Passages { get => _passages.AsReadOnly(); init => _passages = new(value); }
    [DurableField(17)] private List<CyclePlaceMemory> _memories = [];
    public IReadOnlyList<CyclePlaceMemory> Memories { get => _memories.AsReadOnly(); init => _memories = new(value); }
    [DurableField(18)] private CycleExpeditionOutcome _outcome = CycleExpeditionOutcome.Exploring;
    public CycleExpeditionOutcome Outcome { get => _outcome; init => _outcome = value; }
    [DurableField(19)] private string? _endingReason;
    public string? EndingReason { get => _endingReason; init => _endingReason = value; }
    [DurableField(20)] private CycleSurvivalObservation? _survival;
    public CycleSurvivalObservation? Survival { get => _survival; init => _survival = value; }
    [DurableField(21)] private CycleWeatherObservation? _weather;
    public CycleWeatherObservation? Weather { get => _weather; init => _weather = value; }
    [DurableField(22)] private CycleJourneyObservation? _journey;
    public CycleJourneyObservation? Journey { get => _journey; init => _journey = value; }
    [DurableField(23)] private CycleCommunicationObservation? _communication;
    public CycleCommunicationObservation? Communication { get => _communication; init => _communication = value; }
    [DurableField(24)] private CycleGoalSet? _goals;
    /// <summary>Frozen private goal material; null means this scenario has no goal capability.</summary>
    public CycleGoalSet? Goals { get => _goals; init => _goals = value; }
    internal void ValidateJourney() {
        if ((ArrivalDue is null) != (Journey is null) || Journey is not null && (!IsExpedition || PlaceId is not null)) {
            throw new InvalidOperationException("Journey and visible location disagree.");
        }
        Journey?.Validate();
    }
    public string? CheckDisplayed(CycleCommand command) => Commands.Single(entry => entry.Command == command).Rejection;
    public string? WarningDisplayed(CycleCommand command) => Commands.Single(entry => entry.Command == command).Warning;
}

/// <summary>Private communication budget; targets and transferable items use existing visible character and inventory domains.</summary>
[DurableType("DramaBoard.ResourceCycle.CommunicationObservation", 1)]
public sealed partial record CycleCommunicationObservation(
    [field: DurableField(1)] int RemainingSends,
    [field: DurableField(2)] int SendLimit,
    [field: DurableField(3)] bool AttentionPending,
    [field: DurableField(4)] int MaximumSpeechScalars) : IDurableObject {
    internal void Validate() {
        if (SendLimit <= 0 || RemainingSends < 0 || RemainingSends > SendLimit || MaximumSpeechScalars <= 0) {
            throw new InvalidOperationException("Invalid frozen communication limits or remaining budget.");
        }
    }
}

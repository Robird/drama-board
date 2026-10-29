using System.Text;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.CharacterContextStatus", 1)]
public enum CharacterContextStatus { Open, Closed, Terminal }

/// <summary>Actor-owned interaction material, separate from the actor's physical state.</summary>
[DurableType("DramaBoard.ResourceCycle.CharacterInteractionState", 1)]
public sealed partial class CharacterInteractionState : IDurableObject {
    public const int MaximumHandoffScalars = 2000;
    [DurableField(1)] private string _actorId;
    [DurableField(2)] private CharacterContext? _currentContext;
    [DurableField(3)] private PendingAdvance? _pendingAdvance;
    [DurableField(4)] private List<CharacterExperience> _undeliveredExperiences;
    [DurableField(5)] private string? _draftHandoff;
    [DurableField(6)] private long _nextExperienceSequence;
    public string ActorId => _actorId;
    public CharacterContext? CurrentContext => _currentContext;
    public PendingAdvance? PendingAdvance => _pendingAdvance;
    public IReadOnlyList<CharacterExperience> UndeliveredExperiences => _undeliveredExperiences.AsReadOnly();
    public string? DraftHandoff => _draftHandoff;
    public long NextExperienceSequence => _nextExperienceSequence;

    public CharacterInteractionState(string actorId, CharacterContext? currentContext = null,
        PendingAdvance? pendingAdvance = null, IEnumerable<CharacterExperience>? undeliveredExperiences = null,
        string? draftHandoff = null, long nextExperienceSequence = 1) {
        _actorId = actorId;
        _currentContext = currentContext;
        _pendingAdvance = pendingAdvance;
        _undeliveredExperiences = undeliveredExperiences?.ToList() ?? [];
        _draftHandoff = draftHandoff;
        _nextExperienceSequence = nextExperienceSequence;
    }

    public static bool ValidHandoff(string? text) => text is null || text.EnumerateRunes().Count() <= MaximumHandoffScalars;

    internal void Validate(string actorId, ModelTime worldTime) {
        if (string.IsNullOrWhiteSpace(ActorId) || ActorId != actorId || worldTime.Ticks < 0 ||
            NextExperienceSequence <= 0 || !ValidHandoff(DraftHandoff)) {
            throw new InvalidOperationException("Invalid character interaction identity or handoff.");
        }
        long lastExperience = 0;
        if (CurrentContext is { } context) {
            context.Validate(actorId, worldTime, ref lastExperience);
            if (context.Status == CharacterContextStatus.Closed) {
                if (PendingAdvance is null || PendingAdvance.SourceContextSequence != context.ContextSequence ||
                    PendingAdvance.OperationSequence != context.LastOperationSequence || DraftHandoff is not null ||
                    context.Items.Any(item => item.OperationSequence >= PendingAdvance.OperationSequence)) {
                    throw new InvalidOperationException("A closed context must own its undelivered advance.");
                }
            } else if (PendingAdvance is not null || UndeliveredExperiences.Count != 0 ||
                context.Status == CharacterContextStatus.Terminal && DraftHandoff is not null) {
                throw new InvalidOperationException("An open or terminal context cannot retain undelivered material.");
            }
        } else if (PendingAdvance is not null) {
            throw new InvalidOperationException("An unstarted context cannot have a pending advance.");
        }
        PendingAdvance?.Validate(actorId, worldTime);
        CharacterExperience.ValidateSequence(UndeliveredExperiences, actorId, worldTime, ref lastExperience);
        if (NextExperienceSequence <= lastExperience) {
            throw new InvalidOperationException("The next experience identity must exceed every retained experience.");
        }
    }
}

/// <summary>A complete frozen opening followed by retained, ordered semantic feedback groups.</summary>
[DurableType("DramaBoard.ResourceCycle.CharacterContext", 1)]
public sealed partial class CharacterContext : IDurableObject {
    [DurableField(1)] private long _contextSequence;
    [DurableField(2)] private CharacterContextStatus _status;
    [DurableField(3)] private CharacterContextOpening _opening;
    [DurableField(4)] private List<CharacterContextItem> _items;
    [DurableField(5)] private long _nextItemSequence;
    [DurableField(6)] private long _lastOperationSequence;
    public long ContextSequence => _contextSequence;
    public CharacterContextStatus Status => _status;
    public CharacterContextOpening Opening => _opening;
    public IReadOnlyList<CharacterContextItem> Items => _items.AsReadOnly();
    public long NextItemSequence => _nextItemSequence;
    public long LastOperationSequence => _lastOperationSequence;
    public CycleObservation LatestObservation => _items.LastOrDefault(item => item.Observation is not null)?.Observation ?? Opening.Observation;

    public CharacterContext(long contextSequence, CharacterContextStatus status, CharacterContextOpening opening,
        IEnumerable<CharacterContextItem>? items = null, long nextItemSequence = 1, long lastOperationSequence = 0) {
        _contextSequence = contextSequence;
        _status = status;
        _opening = opening;
        _items = items?.ToList() ?? [];
        _nextItemSequence = nextItemSequence;
        _lastOperationSequence = lastOperationSequence;
    }

    internal void Validate(string actorId, ModelTime worldTime, ref long lastExperience) {
        if (ContextSequence <= 0 || !Enum.IsDefined(Status) || Opening is null || NextItemSequence <= 0 || LastOperationSequence < 0) {
            throw new InvalidOperationException("Invalid character context identity or sequence.");
        }
        Opening.Validate(actorId, worldTime, ref lastExperience);
        if (Opening.PreviousAdvance is { } previous && previous.SourceContextSequence != ContextSequence - 1 ||
            (Status == CharacterContextStatus.Terminal) != (Opening.ActivityState == CycleOutcomeActivityState.Terminal) ||
            Status == CharacterContextStatus.Terminal && (Items.Count != 0 || LastOperationSequence != 0)) {
            throw new InvalidOperationException("The context opening does not match its lifecycle.");
        }
        long lastItem = 0;
        long lastOperation = 0;
        ModelTime lastObservationAt = Opening.Observation.Time;
        foreach (CharacterContextItem item in Items) {
            if (item is null || item.ItemSequence <= lastItem) {
                throw new InvalidOperationException("Context item identities must be strictly increasing.");
            }
            item.Validate(actorId, worldTime, ref lastExperience);
            lastItem = item.ItemSequence;
            if (item.OperationSequence is { } operation) {
                if (operation <= lastOperation || operation > LastOperationSequence) {
                    throw new InvalidOperationException("Context operation identities must be strictly increasing.");
                }
                lastOperation = operation;
            }
            if (item.Observation is { } observation) {
                if (observation.Time < lastObservationAt) {
                    throw new InvalidOperationException("Context observations cannot move backwards in time.");
                }
                lastObservationAt = observation.Time;
            }
        }
        if (NextItemSequence <= lastItem) {
            throw new InvalidOperationException("The next item identity must exceed every retained item.");
        }
    }
}

/// <summary>Materials first delivered at a context boundary; background and observations never reproject on read.</summary>
[DurableType("DramaBoard.ResourceCycle.CharacterContextOpening", 1)]
public sealed partial class CharacterContextOpening : IDurableObject {
    [DurableField(1)] private CycleBackground _background;
    [DurableField(2)] private CycleObservation _observation;
    [DurableField(3)] private PendingAdvance? _previousAdvance;
    [DurableField(4)] private ModelTime _deliveredAt;
    [DurableField(5)] private CycleOutcomeActivityState _activityState;
    [DurableField(6)] private List<CharacterExperience> _experiences;
    public CycleBackground Background => _background;
    public CycleObservation Observation => _observation;
    public PendingAdvance? PreviousAdvance => _previousAdvance;
    public ModelTime DeliveredAt => _deliveredAt;
    public CycleOutcomeActivityState ActivityState => _activityState;
    public IReadOnlyList<CharacterExperience> Experiences => _experiences.AsReadOnly();

    public CharacterContextOpening(CycleBackground background, CycleObservation observation, PendingAdvance? previousAdvance,
        ModelTime deliveredAt, CycleOutcomeActivityState activityState, IEnumerable<CharacterExperience>? experiences = null) {
        _background = background;
        _observation = observation;
        _previousAdvance = previousAdvance;
        _deliveredAt = deliveredAt;
        _activityState = activityState;
        _experiences = experiences?.ToList() ?? [];
    }

    internal void Validate(string actorId, ModelTime worldTime, ref long lastExperience) {
        if (Background is null || Observation is null || Observation.Actor is null || Observation.Actor.Id != actorId ||
            DeliveredAt.Ticks < 0 || DeliveredAt > worldTime || Observation.Time != DeliveredAt || !Enum.IsDefined(ActivityState)) {
            throw new InvalidOperationException("Invalid context opening actor, time or activity state.");
        }
        Observation.Survival?.Validate(Observation.Time);
        Observation.ValidateJourney();
        Observation.Communication?.Validate();
        PreviousAdvance?.Validate(actorId, DeliveredAt);
        CharacterExperience.ValidateSequence(Experiences, actorId, DeliveredAt, ref lastExperience);
    }
}

/// <summary>An actor-local identity for one recorded perception, independent of transport or diagnostic cursors.</summary>
[DurableType("DramaBoard.ResourceCycle.CharacterExperience", 1)]
public sealed partial class CharacterExperience : IDurableObject {
    [DurableField(1)] private long _experienceSequence;
    [DurableField(2)] private CycleObservedEvent _event;
    public long ExperienceSequence => _experienceSequence;
    public CycleObservedEvent Event => _event;

    public CharacterExperience(long experienceSequence, CycleObservedEvent @event) {
        _experienceSequence = experienceSequence;
        _event = @event;
    }

    internal static void ValidateSequence(IReadOnlyList<CharacterExperience> experiences, string actorId,
        ModelTime worldTime, ref long lastExperience) {
        foreach (CharacterExperience experience in experiences) {
            if (experience is null || experience.ExperienceSequence <= lastExperience || experience.Event is not { } entry ||
                entry.ObserverId != actorId || entry.EventOrdinal < 0 || !Enum.IsDefined(entry.Kind) ||
                entry.From.Ticks < 0 || entry.At < entry.From || entry.At > worldTime) {
                throw new InvalidOperationException("Invalid actor experience identity, order or interval.");
            }
            if ((entry.Kind is CycleObservedEventKind.SpeechSent or CycleObservedEventKind.ItemGiven) != (entry.Communication is not null)) {
                throw new InvalidOperationException("Communication experiences must retain their signed content.");
            }
            entry.Communication?.Validate(actorId, entry.Kind);
            foreach (CycleObservedEffect effect in entry.Effects) {
                if (effect is null || string.IsNullOrWhiteSpace(effect.Key.Category) ||
                    string.IsNullOrWhiteSpace(effect.Key.ObjectId) || string.IsNullOrWhiteSpace(effect.Key.Field)) {
                    throw new InvalidOperationException("Invalid recorded experience effect.");
                }
                effect.Before.Validate();
                effect.After.Validate();
            }
            lastExperience = experience.ExperienceSequence;
        }
    }
}

/// <summary>One atomic request/result group or independent perception with its full updated visible observation.</summary>
[DurableType("DramaBoard.ResourceCycle.CharacterContextItem", 1)]
public sealed partial class CharacterContextItem : IDurableObject {
    [DurableField(1)] private long _itemSequence;
    [DurableField(2)] private long? _operationSequence;
    [DurableField(3)] private string? _rawInput;
    [DurableField(4)] private string? _toolCallId;
    [DurableField(5)] private string? _operationRecordId;
    [DurableField(6)] private string? _result;
    [DurableField(7)] private List<CharacterExperience> _experiences;
    [DurableField(8)] private CycleObservation? _observation;
    [DurableField(9)] private bool _isRejected;
    public long ItemSequence => _itemSequence;
    public long? OperationSequence => _operationSequence;
    public string? RawInput => _rawInput;
    public string? ToolCallId => _toolCallId;
    public string? OperationRecordId => _operationRecordId;
    public string? Result => _result;
    public IReadOnlyList<CharacterExperience> Experiences => _experiences.AsReadOnly();
    public CycleObservation? Observation => _observation;
    public bool IsRejected => _isRejected;

    public CharacterContextItem(long itemSequence, long? operationSequence = null, string? rawInput = null,
        string? toolCallId = null, string? operationRecordId = null, string? result = null,
        IEnumerable<CharacterExperience>? experiences = null, CycleObservation? observation = null, bool isRejected = false) {
        _itemSequence = itemSequence;
        _operationSequence = operationSequence;
        _rawInput = rawInput;
        _toolCallId = toolCallId;
        _operationRecordId = operationRecordId;
        _result = result;
        _experiences = experiences?.ToList() ?? [];
        _observation = observation;
        _isRejected = isRejected;
    }

    internal void Validate(string actorId, ModelTime worldTime, ref long lastExperience) {
        if (ItemSequence <= 0 || OperationSequence <= 0) {
            throw new InvalidOperationException("Invalid context item identity.");
        }
        if (OperationSequence is not null) {
            if (RawInput is null || string.IsNullOrWhiteSpace(ToolCallId) || string.IsNullOrWhiteSpace(OperationRecordId) ||
                string.IsNullOrWhiteSpace(Result)) {
                throw new InvalidOperationException("A context operation must preserve its input, identity and result together.");
            }
        } else if (RawInput is not null || ToolCallId is not null || OperationRecordId is not null || Result is not null || IsRejected) {
            throw new InvalidOperationException("An independent perception must not invent a character operation.");
        }
        if (IsRejected ? Observation is not null || Experiences.Count != 0 : Observation is null) {
            throw new InvalidOperationException("Only pure rejections omit the resulting observation and carry no experiences.");
        }
        if (Observation is { } observation && (observation.Actor is null || observation.Actor.Id != actorId ||
            observation.Time.Ticks < 0 || observation.Time > worldTime)) {
            throw new InvalidOperationException("Invalid context item observation actor or time.");
        }
        Observation?.Survival?.Validate(Observation.Time);
        Observation?.ValidateJourney();
        Observation?.Communication?.Validate();
        CharacterExperience.ValidateSequence(Experiences, actorId, Observation?.Time ?? worldTime, ref lastExperience);
    }
}

/// <summary>Original advancing operation plus only information known when accepted; no duplicate outcome totals.</summary>
[DurableType("DramaBoard.ResourceCycle.PendingAdvance", 2)]
public sealed partial class PendingAdvance : IDurableObject {
    [DurableField(1)] private string _actorId;
    [DurableField(2)] private long _sourceContextSequence;
    [DurableField(3)] private long _operationSequence;
    [DurableField(4)] private CycleCommand _command;
    [DurableField(5)] private string _toolCallId;
    [DurableField(6)] private string _operationRecordId;
    [DurableField(7)] private ModelTime _acceptedAt;
    [DurableField(8)] private string _description;
    [DurableField(9)] private string? _originPlaceName;
    [DurableField(10)] private long? _activityId;
    [DurableField(11)] private long? _movementGeneration;
    [DurableField(12)] private string? _handoff;
    [DurableField(13)] private string _rawInput;
    public string RawInput => _rawInput;
    public string ActorId => _actorId;
    public long SourceContextSequence => _sourceContextSequence;
    public long OperationSequence => _operationSequence;
    public CycleCommand Command => _command;
    public string ToolCallId => _toolCallId;
    public string OperationRecordId => _operationRecordId;
    public ModelTime AcceptedAt => _acceptedAt;
    public string Description => _description;
    public string? OriginPlaceName => _originPlaceName;
    public long? ActivityId => _activityId;
    public long? MovementGeneration => _movementGeneration;
    public string? Handoff => _handoff;

    public PendingAdvance(string actorId, long sourceContextSequence, long operationSequence, CycleCommand command, string rawInput,
        string toolCallId, string operationRecordId, ModelTime acceptedAt, string description, string? originPlaceName = null,
        long? activityId = null, long? movementGeneration = null, string? handoff = null) {
        _actorId = actorId;
        _sourceContextSequence = sourceContextSequence;
        _operationSequence = operationSequence;
        _command = command;
        _rawInput = rawInput;
        _toolCallId = toolCallId;
        _operationRecordId = operationRecordId;
        _acceptedAt = acceptedAt;
        _description = description;
        _originPlaceName = originPlaceName;
        _activityId = activityId;
        _movementGeneration = movementGeneration;
        _handoff = handoff;
    }

    internal void Validate(string actorId, ModelTime worldTime) {
        if (ActorId != actorId || string.IsNullOrWhiteSpace(ActorId) || SourceContextSequence <= 0 || OperationSequence <= 0 ||
            RawInput is null || Command is null || CycleCommandParser.Parse(RawInput) != Command || Command.Kind is not (CycleCommandKind.Wait or CycleCommandKind.Gather or CycleCommandKind.Go or CycleCommandKind.Continue) ||
            string.IsNullOrWhiteSpace(ToolCallId) || string.IsNullOrWhiteSpace(OperationRecordId) ||
            AcceptedAt.Ticks < 0 || AcceptedAt > worldTime || string.IsNullOrWhiteSpace(Description) ||
            ActivityId <= 0 || MovementGeneration < 0 || (ActivityId is null) == (MovementGeneration is null) ||
            !CharacterInteractionState.ValidHandoff(Handoff)) {
            throw new InvalidOperationException("Invalid advancing operation identity or activity association.");
        }
    }
}

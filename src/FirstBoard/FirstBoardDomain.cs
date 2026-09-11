using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

public static class BoardIds
{
    public const string Tavern = "tavern";
    public const string Market = "market";
    public const string CellarGate = "cellar-gate-front";
    public const string Cellar = "cellar";

    public const string TavernMarketRoad = "tavern-market-road";
    public const string TavernMarketFerry = "tavern-market-ferry";
    public const string MarketTavernCart = "market-tavern-cart";
    public const string MarketCellarApproach = "market-cellar-approach";
    public const string CellarGatePassage = "cellar-gate-passage";

    public const string Alice = "alice";
    public const string Bob = "bob";
    public const string BrassKey = "brass-key";
    public const string LockedChest = "locked-chest";
    public const string DuchessLetter = "duchess-letter";
    public const string SilverCoinOne = "silver-coin-1";
    public const string SilverCoinTwo = "silver-coin-2";
    public const string KeyLocationKnown = "key.location-known";
    public const string ChestContainsLetter = "chest.contains-letter";
    public const string ChestOpenedKnown = "chest.opened-known";
    public const string CellarSealedKnown = "cellar.sealed-known";
    public const string ObjectHeld = "object.held";
    public const string ObjectReceived = "object.received";
    public const string ObjectShown = "object.shown";
    public const string ObjectPlaced = "object.placed";
    public const string ObjectInspected = "object.inspected";
    public const string LetterAuthenticityKnown = "duchess-letter.authenticity-known";
    public const string LetterContentsKnown = "duchess-letter.contents-known";
    public const string DialogueHeard = "dialogue.heard";
    public const string LastActionOutcome = "action.last-outcome";
    public const string ActionRejected = "action.rejected";
    public const string CurrentTravelTarget = "travel.current-target";
    public const string CurrentTravelReverseDestination = "travel.current-reverse-destination";
    public const string CurrentTravelEta = "travel.arrival-eta";
    public const string PassageContactKindKnown = "passage.contact-kind";
    public const string PassageContactCounterpart = "passage.contact-counterpart";
    public const string ActiveTravelGoal = "travel.active-goal";
}

public static class BoardTiming
{
    public const long TravelSpeed = 1;
    public const long DeadlineTicks = 3_600_000;
    public const long DefaultWaitTicks = 60_000;
    public const long RandomRunBoundaryTicks = 4_200_000;
}

[DurableType("DramaBoard.FirstBoard.BoardFact", 1)]
public sealed partial class BoardFact : IDurableObject, IEquatable<BoardFact>
{
    [DurableField(1)] public readonly string Kind;
    [DurableField(2)] public readonly string? RelatedId;
    [DurableField(3)] public readonly string Text;
    public BoardFact(string Kind, string? RelatedId, string Text) => (this.Kind, this.RelatedId, this.Text) = (Kind, RelatedId, Text);
    public bool Equals(BoardFact? other) => other is not null && Kind == other.Kind && RelatedId == other.RelatedId && Text == other.Text;
    public override bool Equals(object? obj) => Equals(obj as BoardFact);
    public override int GetHashCode() => HashCode.Combine(Kind, RelatedId, Text);
}

[DurableType("DramaBoard.FirstBoard.BoardWaitActivity", 1)]
public sealed partial class BoardWaitActivity : IDurableObject, IEquatable<BoardWaitActivity>
{
    [DurableField(1)] public readonly ModelTime Due;
    public BoardWaitActivity(ModelTime Due) => this.Due = Due;
    public bool Equals(BoardWaitActivity? other) => other is not null && Due == other.Due;
    public override bool Equals(object? obj) => Equals(obj as BoardWaitActivity);
    public override int GetHashCode() => Due.GetHashCode();
}

[DurableType("DramaBoard.FirstBoard.BoardActor", 1)]
public sealed partial class BoardActor : IDurableObject, IEquatable<BoardActor>
{
    [DurableField(1)] public readonly long Id;
    [DurableField(2)] public readonly string Key;
    [DurableField(3)] public readonly long Generation;
    [DurableField(4)] public readonly long DecisionSequence;
    [DurableField(5)] public readonly BoardWaitActivity? Activity;
    [DurableField(6)] public readonly PlaceId? TravelGoalPlaceId;
    [DurableField(7)] private readonly List<BoardFact> _knownFacts;
    public IReadOnlyList<BoardFact> KnownFacts => _knownFacts.AsReadOnly();
    public BoardActor(long Id, string Key, long Generation, long DecisionSequence, BoardWaitActivity? Activity, PlaceId? TravelGoalPlaceId, IEnumerable<BoardFact> KnownFacts) => (this.Id, this.Key, this.Generation, this.DecisionSequence, this.Activity, this.TravelGoalPlaceId, _knownFacts) = (Id, Key, Generation, DecisionSequence, Activity, TravelGoalPlaceId, [.. KnownFacts]);
    public BoardActor With(long? generation = null, long? decisionSequence = null, IEnumerable<BoardFact>? knownFacts = null) => new(Id, Key, generation ?? Generation, decisionSequence ?? DecisionSequence, Activity, TravelGoalPlaceId, knownFacts ?? KnownFacts);
    public BoardActor WithActivity(BoardWaitActivity? activity) => new(Id, Key, Generation, DecisionSequence, activity, TravelGoalPlaceId, KnownFacts);
    public BoardActor WithTravelGoal(PlaceId? travelGoalPlaceId) => new(Id, Key, Generation, DecisionSequence, Activity, travelGoalPlaceId, KnownFacts);
    public bool Equals(BoardActor? other) => other is not null && Id == other.Id && Key == other.Key && Generation == other.Generation && DecisionSequence == other.DecisionSequence && Equals(Activity, other.Activity) && TravelGoalPlaceId == other.TravelGoalPlaceId && KnownFacts.SequenceEqual(other.KnownFacts);
    public override bool Equals(object? obj) => Equals(obj as BoardActor);
    public override int GetHashCode() => HashCode.Combine(Id, Key, Generation, DecisionSequence, Activity, TravelGoalPlaceId);
}

[DurableType("DramaBoard.FirstBoard.BoardObject", 1)]
public sealed partial class BoardObject : IDurableObject, IEquatable<BoardObject>
{
    [DurableField(1)] public readonly long Id;
    [DurableField(2)] public readonly string Key;
    [DurableField(3)] public readonly long? OwnerActorId;
    public BoardObject(long Id, string Key, long? OwnerActorId) => (this.Id, this.Key, this.OwnerActorId) = (Id, Key, OwnerActorId);
    public BoardObject WithOwnerActorId(long? ownerActorId) => new(Id, Key, ownerActorId);
    public bool Equals(BoardObject? other) => other is not null && Id == other.Id && Key == other.Key && OwnerActorId == other.OwnerActorId;
    public override bool Equals(object? obj) => Equals(obj as BoardObject);
    public override int GetHashCode() => HashCode.Combine(Id, Key, OwnerActorId);
}

[DurableType("DramaBoard.FirstBoard.PendingPassageEncounter", 1)]
public sealed partial class PendingPassageEncounter : IDurableObject, IEquatable<PendingPassageEncounter>
{
    [DurableField(1)] public readonly PassageContactKey ContactKey;
    [DurableField(2)] public readonly PassageContactKind Kind;
    public PendingPassageEncounter(PassageContactKey contactKey, PassageContactKind kind) => (ContactKey, Kind) = (contactKey, kind);
    public bool Equals(PendingPassageEncounter? other) => other is not null && Equals(ContactKey, other.ContactKey) && Kind == other.Kind;
    public override bool Equals(object? obj) => Equals(obj as PendingPassageEncounter);
    public override int GetHashCode() => HashCode.Combine(ContactKey, Kind);
}

[DurableType("DramaBoard.FirstBoard.FirstBoardGameState", 1)]
public sealed partial class FirstBoardGameState : IDurableObject, IEquatable<FirstBoardGameState>
{
    [DurableField(1)] public readonly ulong WorldSeed;
    [DurableField(2)] public readonly long NextPersistentId;
    [DurableField(3)] public readonly ModelTime Now;
    [DurableField(4)] private readonly List<BoardActor> _actors;
    [DurableField(5)] private readonly List<BoardObject> _objects;
    public IReadOnlyList<BoardActor> Actors => _actors.AsReadOnly();
    public IReadOnlyList<BoardObject> Objects => _objects.AsReadOnly();
    [DurableField(6)] public readonly bool CellarSealed;
    [DurableField(7)] public readonly bool ChestOpened;
    [DurableField(8)] public readonly PendingPassageEncounter? PendingEncounter;
    public FirstBoardGameState(ulong WorldSeed, long NextPersistentId, ModelTime Now, IEnumerable<BoardActor> Actors, IEnumerable<BoardObject> Objects, bool CellarSealed, bool ChestOpened, PendingPassageEncounter? PendingEncounter = null) => (this.WorldSeed, this.NextPersistentId, this.Now, _actors, _objects, this.CellarSealed, this.ChestOpened, this.PendingEncounter) = (WorldSeed, NextPersistentId, Now, [.. Actors], [.. Objects], CellarSealed, ChestOpened, PendingEncounter);
    public FirstBoardGameState With(ulong? worldSeed = null, long? nextPersistentId = null, ModelTime? now = null, IEnumerable<BoardActor>? actors = null, IEnumerable<BoardObject>? objects = null, bool? cellarSealed = null, bool? chestOpened = null) => new(worldSeed ?? WorldSeed, nextPersistentId ?? NextPersistentId, now ?? Now, actors ?? Actors, objects ?? Objects, cellarSealed ?? CellarSealed, chestOpened ?? ChestOpened, PendingEncounter);
    public FirstBoardGameState WithPendingEncounter(PendingPassageEncounter? pendingEncounter) => new(WorldSeed, NextPersistentId, Now, Actors, Objects, CellarSealed, ChestOpened, pendingEncounter);
    public bool Equals(FirstBoardGameState? other) => other is not null && WorldSeed == other.WorldSeed && NextPersistentId == other.NextPersistentId && Now == other.Now && Actors.SequenceEqual(other.Actors) && Objects.SequenceEqual(other.Objects) && CellarSealed == other.CellarSealed && ChestOpened == other.ChestOpened && Equals(PendingEncounter, other.PendingEncounter);
    public override bool Equals(object? obj) => Equals(obj as FirstBoardGameState);
    public override int GetHashCode() => HashCode.Combine(WorldSeed, NextPersistentId, Now, CellarSealed, ChestOpened, PendingEncounter);
    public BoardActor Actor(string actorId) =>
        Actors.Single(actor => actor.Key == actorId);

    public BoardActor Actor(long actorId) =>
        Actors.Single(actor => actor.Id == actorId);

    public BoardObject Object(string objectId) =>
        Objects.Single(item => item.Key == objectId);

    public bool IsIdle(BoardActor actor) => actor.Activity is null;
}

/// <summary>Owns the complete Game + objective Graph Spatial committed world.</summary>
[DurableType("DramaBoard.FirstBoard.FirstBoardWorld", 1)]
public sealed partial class FirstBoardWorld : IDurableObject, IEquatable<FirstBoardWorld>
{
    [DurableField(1)] public readonly FirstBoardGameState Game;
    [DurableField(2)] public readonly GraphSpatialState Spatial;
    public FirstBoardWorld(FirstBoardGameState game, GraphSpatialState spatial) => (Game, Spatial) = (game, spatial);
    public FirstBoardWorld With(FirstBoardGameState? game = null, GraphSpatialState? spatial = null) => new(game ?? Game, spatial ?? Spatial);
    public bool Equals(FirstBoardWorld? other) => other is not null && Equals(Game, other.Game) && Equals(Spatial, other.Spatial);
    public override bool Equals(object? obj) => Equals(obj as FirstBoardWorld);
    public override int GetHashCode() => HashCode.Combine(Game, Spatial);
    public ulong WorldSeed => Game.WorldSeed;
    public ModelTime Now => Game.Now;
    public IReadOnlyList<BoardActor> Actors => Game.Actors;
    public IReadOnlyList<BoardObject> Objects => Game.Objects;
    public bool CellarSealed => Game.CellarSealed;
    public bool ChestOpened => Game.ChestOpened;

    public static FirstBoardWorld CreateInitial(ulong worldSeed) =>
        ScenarioInstance.CreateDefault(worldSeed).CreateInitialWorld();

    public BoardActor Actor(string actorId) => Game.Actor(actorId);
    public BoardActor Actor(long actorId) => Game.Actor(actorId);
    public BoardObject Object(string objectId) => Game.Object(objectId);

    public bool IsAtPlace(string entityId, PlaceId placeId) =>
        Spatial.TryGetEntity(new EntityId(entityId), out SpatialEntity? entity) &&
        entity!.Location is AtPlaceLocation atPlace &&
        atPlace.PlaceId == placeId;

    public bool TryGetPlace(string entityId, out PlaceId placeId)
    {
        if (Spatial.TryGetEntity(new EntityId(entityId), out SpatialEntity? entity) &&
            entity!.Location is AtPlaceLocation atPlace)
        {
            placeId = atPlace.PlaceId;
            return true;
        }

        placeId = default;
        return false;
    }

    public bool IsReadyForDecision(BoardActor actor) =>
        Game.IsIdle(actor) &&
        actor.TravelGoalPlaceId is null &&
        !IsPendingEncounterParticipant(actor) &&
        TryGetPlace(actor.Key, out _);

    public bool IsPendingEncounterParticipant(BoardActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        PendingPassageEncounter? pending = Game.PendingEncounter;
        if (pending is null)
        {
            return false;
        }

        var entityId = new EntityId(actor.Key);
        return pending.ContactKey.EntityA == entityId ||
            pending.ContactKey.EntityB == entityId;
    }

    public bool AreCoLocated(string firstEntityId, string secondEntityId) =>
        TryGetPlace(firstEntityId, out PlaceId first) &&
        TryGetPlace(secondEntityId, out PlaceId second) &&
        first == second;
}

[DurableType("DramaBoard.FirstBoard.BoardEventPayload", 1)]
public abstract partial class BoardEventPayload : IDurableObject
{
    protected virtual IEnumerable<object?> GetEqualityComponents() => this switch
    {
        ActorTravelGoalResolvedEvent x => [x.ActorId, x.DestinationPlaceId, x.Resolution],
        PassageEncounterOpenedEvent x => [x.ContactKey, x.Kind],
        PassageEncounterResolvedEvent x => [x.ContactKey, x.RespondingActorId, x.Resolution],
        TicketConsumedEvent x => [x.ActorId, x.TicketObjectId],
        ActorWaitStartedEvent x => [x.ActorId, x.CompleteAt],
        ActorSpokeEvent x => [x.ActorId, x.TargetActorId, x.Text, x.SharedFactKind],
        ObjectTakenEvent x => [x.ActorId, x.ObjectId],
        ObjectPlacedEvent x => [x.ActorId, x.ObjectId, x.PlaceId],
        ObjectGivenEvent x => [x.ActorId, x.TargetActorId, x.ObjectId],
        ObjectShownEvent x => [x.ActorId, x.TargetActorId, x.ObjectId],
        ChestOpenedEvent x => [x.ActorId, x.ObjectId, x.KeyObjectId],
        ActionRejectedEvent x => [x.ActorId, x.RejectedIntent, x.Reason],
        ActorObservedEvent x => [x.ActorId, x.TargetObjectId],
        _ => [],
    };
    public sealed override bool Equals(object? obj)
    {
        if (obj is not BoardEventPayload other || other.GetType() != GetType()) return false;
        if (this is ActorObservedEvent observed && other is ActorObservedEvent compared)
            return observed.ActorId == compared.ActorId && observed.TargetObjectId == compared.TargetObjectId && observed.LearnedFacts.SequenceEqual(compared.LearnedFacts);
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }
    public sealed override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(GetType());
        if (this is ActorObservedEvent observed) { hash.Add(observed.ActorId); hash.Add(observed.TargetObjectId); foreach (BoardFact fact in observed.LearnedFacts) hash.Add(fact); return hash.ToHashCode(); }
        foreach (object? value in GetEqualityComponents()) hash.Add(value); return hash.ToHashCode();
    }
    public static bool operator ==(BoardEventPayload? left, BoardEventPayload? right) => ReferenceEquals(left, right) || left is not null && left.Equals(right);
    public static bool operator !=(BoardEventPayload? left, BoardEventPayload? right) => !(left == right);
}

/// <summary>Records Game decision progress without owning location or arrival time.</summary>
[DurableType("DramaBoard.FirstBoard.ActorTravelStartedEvent", 1)]
public sealed partial class ActorTravelStartedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string ExitId;
    [DurableField(3)] public readonly string DestinationId;

    public ActorTravelStartedEvent(string actorId, string exitId, string destinationId) =>
        (ActorId, ExitId, DestinationId) = (actorId, exitId, destinationId);

    protected override IEnumerable<object?> GetEqualityComponents() => [ActorId, ExitId, DestinationId];
}

[DurableType("DramaBoard.FirstBoard.ActorTravelGoalSetEvent", 1)]
public sealed partial class ActorTravelGoalSetEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly PlaceId DestinationPlaceId;

    public ActorTravelGoalSetEvent(string actorId, PlaceId destinationPlaceId) =>
        (ActorId, DestinationPlaceId) = (actorId, destinationPlaceId);

    protected override IEnumerable<object?> GetEqualityComponents() => [ActorId, DestinationPlaceId];
}

[DurableType("DramaBoard.FirstBoard.TravelGoalResolution", 1)]
public enum TravelGoalResolution
{
    Completed = 0,
    Blocked = 1,
}

[DurableType("DramaBoard.FirstBoard.ActorTravelGoalResolvedEvent", 1)]
public sealed partial class ActorTravelGoalResolvedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly PlaceId DestinationPlaceId;
    [DurableField(3)] public readonly TravelGoalResolution Resolution;

    public ActorTravelGoalResolvedEvent(string actorId, PlaceId destinationPlaceId, TravelGoalResolution resolution) => (ActorId, DestinationPlaceId, Resolution) = (actorId, destinationPlaceId, resolution);
}

[DurableType("DramaBoard.FirstBoard.PassageEncounterOpenedEvent", 1)]
public sealed partial class PassageEncounterOpenedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly PassageContactKey ContactKey;
    [DurableField(2)] public readonly PassageContactKind Kind;

    public PassageEncounterOpenedEvent(PassageContactKey contactKey, PassageContactKind kind) => (ContactKey, Kind) = (contactKey, kind);
}

[DurableType("DramaBoard.FirstBoard.PassageEncounterResolution", 1)]
public enum PassageEncounterResolution
{
    Continued = 0,
    Reversed = 1,
    WorldChanged = 2,
}

[DurableType("DramaBoard.FirstBoard.PassageEncounterResolvedEvent", 1)]
public sealed partial class PassageEncounterResolvedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly PassageContactKey ContactKey;
    [DurableField(2)] public readonly string? RespondingActorId;
    [DurableField(3)] public readonly PassageEncounterResolution Resolution;

    public PassageEncounterResolvedEvent(PassageContactKey ContactKey, string? RespondingActorId, PassageEncounterResolution Resolution) => (this.ContactKey, this.RespondingActorId, this.Resolution) = (ContactKey, RespondingActorId, Resolution);
}

[DurableType("DramaBoard.FirstBoard.TicketConsumedEvent", 1)]
public sealed partial class TicketConsumedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string TicketObjectId;

    public TicketConsumedEvent(string actorId, string ticketObjectId) => (ActorId, TicketObjectId) = (actorId, ticketObjectId);
}

[DurableType("DramaBoard.FirstBoard.ActorWaitStartedEvent", 1)]
public sealed partial class ActorWaitStartedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly ModelTime CompleteAt;

    public ActorWaitStartedEvent(string actorId, ModelTime completeAt) => (ActorId, CompleteAt) = (actorId, completeAt);
}

[DurableType("DramaBoard.FirstBoard.ActorWaitedEvent", 1)]
public sealed partial class ActorWaitedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;

    public ActorWaitedEvent(string actorId) => ActorId = actorId;

    protected override IEnumerable<object?> GetEqualityComponents() => [ActorId];
}

[DurableType("DramaBoard.FirstBoard.ActorSpokeEvent", 1)]
public sealed partial class ActorSpokeEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string TargetActorId;
    [DurableField(3)] public readonly string Text;
    [DurableField(4)] public readonly string? SharedFactKind;

    public ActorSpokeEvent(string actorId, string targetActorId, string text, string? sharedFactKind) => (ActorId, TargetActorId, Text, SharedFactKind) = (actorId, targetActorId, text, sharedFactKind);
}

[DurableType("DramaBoard.FirstBoard.ActorObservedEvent", 1)]
public sealed partial class ActorObservedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] private readonly List<BoardFact> _learnedFacts;
    [DurableField(3)] public readonly string? TargetObjectId;
    public IReadOnlyList<BoardFact> LearnedFacts => _learnedFacts.AsReadOnly();
    public ActorObservedEvent(string actorId, IEnumerable<BoardFact> learnedFacts, string? targetObjectId = null) => (ActorId, _learnedFacts, TargetObjectId) = (actorId, [.. learnedFacts], targetObjectId);
}

[DurableType("DramaBoard.FirstBoard.ObjectTakenEvent", 1)]
public sealed partial class ObjectTakenEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string ObjectId;

    public ObjectTakenEvent(string actorId, string objectId) => (ActorId, ObjectId) = (actorId, objectId);
}

[DurableType("DramaBoard.FirstBoard.ObjectPlacedEvent", 1)]
public sealed partial class ObjectPlacedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string ObjectId;
    [DurableField(3)] public readonly string PlaceId;

    public ObjectPlacedEvent(string actorId, string objectId, string placeId) => (ActorId, ObjectId, PlaceId) = (actorId, objectId, placeId);
}

[DurableType("DramaBoard.FirstBoard.ObjectGivenEvent", 1)]
public sealed partial class ObjectGivenEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string TargetActorId;
    [DurableField(3)] public readonly string ObjectId;

    public ObjectGivenEvent(string actorId, string targetActorId, string objectId) => (ActorId, TargetActorId, ObjectId) = (actorId, targetActorId, objectId);
}

[DurableType("DramaBoard.FirstBoard.ObjectShownEvent", 1)]
public sealed partial class ObjectShownEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string TargetActorId;
    [DurableField(3)] public readonly string ObjectId;

    public ObjectShownEvent(string actorId, string targetActorId, string objectId) => (ActorId, TargetActorId, ObjectId) = (actorId, targetActorId, objectId);
}

[DurableType("DramaBoard.FirstBoard.ChestOpenedEvent", 1)]
public sealed partial class ChestOpenedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly string ObjectId;
    [DurableField(3)] public readonly string KeyObjectId;

    public ChestOpenedEvent(string actorId, string objectId, string keyObjectId) => (ActorId, ObjectId, KeyObjectId) = (actorId, objectId, keyObjectId);
}

[DurableType("DramaBoard.FirstBoard.RejectedIntentSnapshot", 1)]
public sealed partial class RejectedIntentSnapshot : IDurableObject, IEquatable<RejectedIntentSnapshot>
{
    [DurableField(1)] public readonly string ActionKindId;
    [DurableField(2)] public readonly string? TargetActorId;
    [DurableField(3)] public readonly string? TargetObjectId;
    [DurableField(4)] public readonly string? ExitId;
    [DurableField(5)] public readonly string? DestinationId;
    [DurableField(6)] public readonly string? FreeText;
    [DurableField(7)] public readonly long? DurationMs;
    [DurableField(8)] public readonly long? UntilModelTimeMs;
    public RejectedIntentSnapshot(string actionKindId, string? targetActorId, string? targetObjectId, string? exitId, string? destinationId, string? freeText, long? durationMs, long? untilModelTimeMs) => (ActionKindId, TargetActorId, TargetObjectId, ExitId, DestinationId, FreeText, DurationMs, UntilModelTimeMs) = (actionKindId, targetActorId, targetObjectId, exitId, destinationId, freeText, durationMs, untilModelTimeMs);
    public static RejectedIntentSnapshot FromIntent(Intent intent) => new(intent.ActionKind.Id, intent.TargetActorId, intent.TargetObjectId, intent.ExitId, intent.DestinationId, intent.FreeText, intent.DurationMs, intent.UntilModelTimeMs);
    public Intent ToIntent() => new(new ActionKind(ActionKindId), TargetActorId, TargetObjectId, ExitId, DestinationId, FreeText, DurationMs, UntilModelTimeMs);
    public bool Equals(RejectedIntentSnapshot? other) => other is not null && ActionKindId == other.ActionKindId && TargetActorId == other.TargetActorId && TargetObjectId == other.TargetObjectId && ExitId == other.ExitId && DestinationId == other.DestinationId && FreeText == other.FreeText && DurationMs == other.DurationMs && UntilModelTimeMs == other.UntilModelTimeMs;
    public override bool Equals(object? obj) => Equals(obj as RejectedIntentSnapshot);
    public override int GetHashCode() => HashCode.Combine(ActionKindId, TargetActorId, TargetObjectId, ExitId, DestinationId, FreeText, DurationMs, UntilModelTimeMs);
}

[DurableType("DramaBoard.FirstBoard.ActionRejectedEvent", 1)]
public sealed partial class ActionRejectedEvent : BoardEventPayload
{
    [DurableField(1)] public readonly string ActorId;
    [DurableField(2)] public readonly RejectedIntentSnapshot RejectedIntent;
    [DurableField(3)] public readonly string Reason;

    public ActionRejectedEvent(string actorId, RejectedIntentSnapshot rejectedIntent, string reason) => (ActorId, RejectedIntent, Reason) = (actorId, rejectedIntent, reason);

    public ActionRejectedEvent(string actorId, Intent rejectedIntent, string reason) : this(actorId, RejectedIntentSnapshot.FromIntent(rejectedIntent), reason)
{

}
}

[DurableType("DramaBoard.FirstBoard.CellarSealedEvent", 1)]
public sealed partial class CellarSealedEvent : BoardEventPayload
{
    protected override IEnumerable<object?> GetEqualityComponents() => [];
}

/// <summary>Exact Host fact union; every batch may combine Game and Spatial facts.</summary>
[DurableType("DramaBoard.FirstBoard.FirstBoardFact", 1)]
public abstract partial class FirstBoardFact : IDurableObject
{
    protected abstract object? EqualityValue { get; }
    public sealed override bool Equals(object? obj) => obj is FirstBoardFact other && other.GetType() == GetType() && Equals(EqualityValue, other.EqualityValue);
    public sealed override int GetHashCode() => HashCode.Combine(GetType(), EqualityValue);
    public static bool operator ==(FirstBoardFact? left, FirstBoardFact? right) => ReferenceEquals(left, right) || left is not null && left.Equals(right);
    public static bool operator !=(FirstBoardFact? left, FirstBoardFact? right) => !(left == right);
}

[DurableType("DramaBoard.FirstBoard.GameBoardFact", 1)]
public sealed partial class GameBoardFact : FirstBoardFact
{
    [DurableField(1)] public readonly BoardEventPayload Value;

    protected override object? EqualityValue => Value;

    public GameBoardFact(BoardEventPayload value) => Value = value;
}

[DurableType("DramaBoard.FirstBoard.SpatialBoardFact", 1)]
public sealed partial class SpatialBoardFact : FirstBoardFact
{
    [DurableField(1)] public readonly GraphSpatialFact Value;

    protected override object? EqualityValue => Value;

    public SpatialBoardFact(GraphSpatialFact value) => Value = value;
}

/// <summary>Folds the Host union while leaving cross-domain validation to the batch boundary.</summary>
public sealed class FirstBoardReducer
{
    private readonly GraphDefinition _definition;
    private readonly GraphSpatialReducer _spatialReducer;

    public FirstBoardReducer(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        _spatialReducer = new GraphSpatialReducer(definition);
    }

    public FirstBoardWorld Apply(
        FirstBoardWorld world,
        LogicalInstant instant,
        FirstBoardFact fact)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fact);

        FirstBoardWorld updated = fact switch
        {
            GameBoardFact game => world.With(game: ApplyGame(world, instant, game.Value)),
            SpatialBoardFact spatial => world.With(spatial: _spatialReducer.Apply(world.Spatial, instant, spatial.Value)),
            _ => throw new InvalidOperationException(
                $"Unknown FirstBoard fact '{fact.GetType().Name}'."),
        };

        return updated.With(game: updated.Game.With(now: instant.ModelTime));
    }

    public void Validate(FirstBoardWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        GraphSpatialStateValidator.ValidateComplete(_definition, world.Spatial);
        EnsureUnique(world.Actors.Select(actor => actor.Id), "actor id");
        EnsureUnique(world.Actors.Select(actor => actor.Key), "actor key");
        EnsureUnique(world.Objects.Select(item => item.Id), "object id");
        EnsureUnique(world.Objects.Select(item => item.Key), "object key");

        var actorIds = world.Actors.Select(actor => actor.Id).ToHashSet();
        if (world.Objects.Any(item =>
                item.OwnerActorId is long ownerId && !actorIds.Contains(ownerId)))
        {
            throw new InvalidOperationException("A FirstBoard object owner must exist.");
        }

        foreach (BoardActor actor in world.Actors)
        {
            if (!world.Spatial.TryGetEntity(new EntityId(actor.Key), out SpatialEntity? entity))
            {
                throw new InvalidOperationException(
                    $"Actor '{actor.Key}' must have one objective Spatial entity.");
            }

            if (actor.Activity is not null && entity!.Location is not AtPlaceLocation)
            {
                throw new InvalidOperationException(
                    $"Traversing actor '{actor.Key}' cannot also own a Wait activity.");
            }

            if (actor.Activity is not null && actor.TravelGoalPlaceId is not null)
            {
                throw new InvalidOperationException(
                    $"Actor '{actor.Key}' cannot wait while owning a TravelTo goal.");
            }

            if (actor.TravelGoalPlaceId is PlaceId destination &&
                !_definition.Contains(destination))
            {
                throw new InvalidOperationException(
                    $"Actor '{actor.Key}' has an undefined TravelTo destination '{destination}'.");
            }
        }

        if (world.Game.PendingEncounter is PendingPassageEncounter pending)
        {
            ArgumentNullException.ThrowIfNull(pending.ContactKey);
            if (!Enum.IsDefined(pending.Kind))
            {
                throw new InvalidOperationException(
                    $"Unknown pending passage contact kind '{pending.Kind}'.");
            }

            var actorKeys = world.Actors
                .Select(actor => actor.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (!actorKeys.Contains(pending.ContactKey.EntityA.Value) ||
                !actorKeys.Contains(pending.ContactKey.EntityB.Value))
            {
                throw new InvalidOperationException(
                    "A pending passage encounter must reference two FirstBoard actors.");
            }
        }

        foreach (BoardObject item in world.Objects)
        {
            bool hasSpatialEntity = world.Spatial.TryGetEntity(new EntityId(item.Key), out _);
            if (item.OwnerActorId is not null && hasSpatialEntity)
            {
                throw new InvalidOperationException(
                    $"Owned object '{item.Key}' cannot also have a standalone Spatial location.");
            }
        }
    }

    private FirstBoardGameState ApplyGame(
        FirstBoardWorld world,
        LogicalInstant instant,
        BoardEventPayload fact)
    {
        FirstBoardGameState game = world.Game;
        return fact switch
        {
            ActorTravelStartedEvent started =>
                UpdateActor(game, started.ActorId, actor =>
                    AddFacts(CompleteDecision(actor), [LastOutcome(
                        $"Your travel via {started.ExitId} to {started.DestinationId} was accepted.")])),
            ActorTravelGoalSetEvent set =>
                ApplyTravelGoalSet(world, game, set),
            ActorTravelGoalResolvedEvent resolved =>
                ApplyTravelGoalResolved(world, game, resolved),
            PassageEncounterOpenedEvent opened =>
                ApplyPassageEncounterOpened(world, game, opened),
            PassageEncounterResolvedEvent resolved =>
                ApplyPassageEncounterResolved(game, resolved),
            TicketConsumedEvent consumed =>
                ConsumeTicket(game, consumed),
            ActorWaitStartedEvent waited =>
                UpdateActor(game, waited.ActorId, actor =>
                    AddFacts(CompleteDecision(actor).WithActivity(new BoardWaitActivity(waited.CompleteAt)), [LastOutcome(
                        $"Your wait was accepted until model time {waited.CompleteAt.Ticks}ms.")])),
            ActorWaitedEvent waited =>
                UpdateActor(game, waited.ActorId, actor =>
                    AddFacts(CompleteActivity(actor), [LastOutcome(
                        $"You successfully finished waiting at model time {instant.ModelTime.Ticks}ms.")])),
            ActorSpokeEvent spoke =>
                ApplySpoke(game, spoke),
            ActorObservedEvent observed =>
                UpdateActor(game, observed.ActorId, actor =>
                    AddFacts(
                        CompleteDecision(actor),
                        observed.LearnedFacts.Append(LastOutcome(ObservationOutcome(observed))))),
            ObjectTakenEvent taken =>
                ApplyTaken(game, taken),
            ObjectPlacedEvent placed =>
                ApplyPlaced(world, game, placed),
            ObjectGivenEvent given =>
                ApplyGiven(game, given),
            ObjectShownEvent shown =>
                ApplyShown(game, shown),
            ChestOpenedEvent opened =>
                ApplyChestOpened(game, opened),
            ActionRejectedEvent rejected =>
                ApplyRejected(game, rejected),
            CellarSealedEvent =>
                ApplyCellarSealed(world, game),
            _ => throw new InvalidOperationException(
                $"Unknown FirstBoard Game fact '{fact.GetType().Name}'."),
        };
    }

    private static FirstBoardGameState ApplyPassageEncounterOpened(
        FirstBoardWorld world,
        FirstBoardGameState game,
        PassageEncounterOpenedEvent opened)
    {
        ArgumentNullException.ThrowIfNull(opened.ContactKey);
        if (game.PendingEncounter is not null)
        {
            throw new InvalidOperationException(
                "FirstBoard supports only one pending passage encounter.");
        }

        if (!Enum.IsDefined(opened.Kind))
        {
            throw new InvalidOperationException(
                $"Unknown passage contact kind '{opened.Kind}'.");
        }

        if (!world.Spatial.ConsumedContacts.Contains(opened.ContactKey))
        {
            throw new InvalidOperationException(
                "A passage encounter can open only after its Spatial contact was consumed.");
        }

        var actorKeys = game.Actors
            .Select(actor => actor.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!actorKeys.Contains(opened.ContactKey.EntityA.Value) ||
            !actorKeys.Contains(opened.ContactKey.EntityB.Value))
        {
            throw new InvalidOperationException(
                "A passage encounter requires two FirstBoard actors.");
        }

        return game.WithPendingEncounter(new PendingPassageEncounter(opened.ContactKey, opened.Kind));
    }

    private static FirstBoardGameState ApplyPassageEncounterResolved(
        FirstBoardGameState game,
        PassageEncounterResolvedEvent resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved.ContactKey);
        PendingPassageEncounter pending = game.PendingEncounter ??
            throw new InvalidOperationException("There is no pending passage encounter to resolve.");
        if (pending.ContactKey != resolved.ContactKey)
        {
            throw new InvalidOperationException(
                "The passage encounter resolution does not match the pending contact.");
        }

        if (resolved.Resolution == PassageEncounterResolution.WorldChanged)
        {
            if (resolved.RespondingActorId is not null)
            {
                throw new InvalidOperationException(
                    "A WorldChanged encounter resolution cannot name a responding actor.");
            }

            return game.WithPendingEncounter(null);
        }

        if (resolved.Resolution is not (
                PassageEncounterResolution.Continued or
                PassageEncounterResolution.Reversed))
        {
            throw new InvalidOperationException(
                $"Unknown passage encounter resolution '{resolved.Resolution}'.");
        }

        if (resolved.RespondingActorId is not string responderId)
        {
            throw new InvalidOperationException(
                "A Player passage encounter resolution requires a responding actor.");
        }

        var responderEntityId = new EntityId(responderId);
        if (pending.ContactKey.EntityA != responderEntityId &&
            pending.ContactKey.EntityB != responderEntityId)
        {
            throw new InvalidOperationException(
                $"Actor '{responderId}' is not a participant in the pending passage encounter.");
        }

        BoardActor responder = game.Actor(responderId);
        FirstBoardGameState cleared = game.WithPendingEncounter(null);
        return UpdateActor(cleared, responder.Id, actor =>
        {
            BoardActor completed = CompleteDecision(actor);
            if (resolved.Resolution == PassageEncounterResolution.Continued)
            {
                return completed;
            }

            return AddFacts(
                completed.WithTravelGoal(null),
                [LastOutcome(
                    "Your delegated travel was interrupted when you reversed after a passage encounter.")]);
        });
    }

    private FirstBoardGameState ApplyTravelGoalSet(
        FirstBoardWorld world,
        FirstBoardGameState game,
        ActorTravelGoalSetEvent set)
    {
        BoardActor actor = game.Actor(set.ActorId);
        if (actor.Activity is not null || actor.TravelGoalPlaceId is not null)
        {
            throw new InvalidOperationException(
                $"Actor '{actor.Key}' must be idle without a TravelTo goal before setting one.");
        }

        if (!world.TryGetPlace(actor.Key, out PlaceId currentPlaceId))
        {
            throw new InvalidOperationException(
                $"Actor '{actor.Key}' must be at a Place before setting a TravelTo goal.");
        }

        if (!_definition.Contains(set.DestinationPlaceId))
        {
            throw new InvalidOperationException(
                $"TravelTo destination '{set.DestinationPlaceId}' does not exist.");
        }

        if (set.DestinationPlaceId == currentPlaceId)
        {
            throw new InvalidOperationException(
                "A TravelTo goal must differ from the actor's current Place.");
        }

        return UpdateActor(game, actor.Id, current =>
            AddFacts(
                CompleteDecision(current).WithTravelGoal(set.DestinationPlaceId),
                [LastOutcome(
                    $"Your delegated travel toward {set.DestinationPlaceId} was accepted.")]));
    }

    private static FirstBoardGameState ApplyTravelGoalResolved(
        FirstBoardWorld world,
        FirstBoardGameState game,
        ActorTravelGoalResolvedEvent resolved)
    {
        BoardActor actor = game.Actor(resolved.ActorId);
        if (actor.Activity is not null ||
            actor.TravelGoalPlaceId != resolved.DestinationPlaceId)
        {
            throw new InvalidOperationException(
                $"Actor '{actor.Key}' does not own the TravelTo goal being resolved.");
        }

        if (!world.TryGetPlace(actor.Key, out PlaceId currentPlaceId))
        {
            throw new InvalidOperationException(
                $"Actor '{actor.Key}' must be at a Place when resolving a TravelTo goal.");
        }

        string outcome = resolved.Resolution switch
        {
            TravelGoalResolution.Completed when currentPlaceId == resolved.DestinationPlaceId =>
                $"You completed delegated travel to {resolved.DestinationPlaceId}.",
            TravelGoalResolution.Blocked when currentPlaceId != resolved.DestinationPlaceId =>
                $"Your delegated travel toward {resolved.DestinationPlaceId} is blocked from here.",
            TravelGoalResolution.Completed => throw new InvalidOperationException(
                "A completed TravelTo goal requires the actor to be at its destination."),
            TravelGoalResolution.Blocked => throw new InvalidOperationException(
                "A blocked TravelTo goal cannot be resolved at its destination."),
            _ => throw new InvalidOperationException(
                $"Unknown TravelTo resolution '{resolved.Resolution}'."),
        };
        return UpdateActor(game, actor.Id, current =>
            AddFacts(CompleteTravelGoal(current), [LastOutcome(outcome)]));
    }

    private static FirstBoardGameState ApplyRejected(
        FirstBoardGameState game,
        ActionRejectedEvent rejected)
    {
        var learnedFacts = new List<BoardFact>
        {
            RejectedActionFact(rejected.RejectedIntent, rejected.Reason),
            LastOutcome($"Your {rejected.RejectedIntent.ActionKindId} action was rejected: " +
                $"{rejected.Reason}."),
        };
        if (rejected.RejectedIntent.ActionKindId == ActionKinds.Travel.Id &&
            rejected.Reason == "cellar is sealed")
        {
            learnedFacts.Add(CellarSealedFact());
        }

        return UpdateActor(game, rejected.ActorId, actor =>
            AddFacts(CompleteDecision(actor), learnedFacts));
    }

    private static FirstBoardGameState ApplyCellarSealed(
        FirstBoardWorld world,
        FirstBoardGameState game)
    {
        FirstBoardGameState updated = game.With(cellarSealed: true);
        foreach (BoardActor witness in game.Actors.Where(actor =>
                     world.IsAtPlace(actor.Key, new PlaceId(BoardIds.Cellar))))
        {
            updated = UpdateActor(updated, witness.Id, actor =>
                AddFacts(actor, [CellarSealedFact()]));
        }

        return updated;
    }

    private static FirstBoardGameState ApplySpoke(
        FirstBoardGameState game,
        ActorSpokeEvent spoke)
    {
        BoardActor speaker = game.Actor(spoke.ActorId);
        BoardFact? sharedFact = spoke.SharedFactKind is null
            ? null
            : speaker.KnownFacts.Single(fact => fact.Kind == spoke.SharedFactKind);
        FirstBoardGameState afterSpeaker = UpdateActor(game, spoke.ActorId, actor =>
            AddFacts(CompleteDecision(actor), [LastOutcome(
                $"You successfully spoke to {spoke.TargetActorId}: {spoke.Text}")]));
        return UpdateActor(afterSpeaker, spoke.TargetActorId, actor =>
        {
            var facts = new List<BoardFact>
            {
                new(
                    BoardIds.DialogueHeard,
                    spoke.ActorId,
                    $"{spoke.ActorId} said to you: {spoke.Text}"),
            };
            if (sharedFact is not null)
            {
                facts.Add(sharedFact);
            }

            if (actor.Activity is not null)
            {
                facts.Add(LastOutcome(
                    $"Your wait was interrupted because {spoke.ActorId} spoke to you."));
                actor = CompleteActivity(actor);
            }

            return AddFacts(actor, facts);
        });
    }

    private static FirstBoardGameState ApplyTaken(
        FirstBoardGameState game,
        ObjectTakenEvent taken)
    {
        BoardActor actor = game.Actor(taken.ActorId);
        FirstBoardGameState updated = UpdateObject(game, taken.ObjectId, item => item.WithOwnerActorId(actor.Id));
        return UpdateActor(updated, taken.ActorId, current =>
            AddFacts(CompleteDecision(current),
            [
                KeyLocationFact(),
                LastOutcome($"You successfully took {taken.ObjectId}."),
            ]));
    }

    private static FirstBoardGameState ApplyPlaced(
        FirstBoardWorld world,
        FirstBoardGameState game,
        ObjectPlacedEvent placed)
    {
        FirstBoardGameState updated = UpdateObject(game, placed.ObjectId, item => item.WithOwnerActorId(null));
        updated = UpdateActor(updated, placed.ActorId, actor =>
        {
            var facts = new List<BoardFact>
            {
                LastOutcome(
                    $"You placed {placed.ObjectId} at {placed.PlaceId}; it is now public and anyone there may inspect or take it."),
            };
            if (placed.ObjectId == BoardIds.BrassKey)
            {
                facts.Add(KeyLocationFact(placed.PlaceId));
            }

            return AddFacts(CompleteDecision(actor), facts);
        });

        foreach (BoardActor witness in game.Actors.Where(actor =>
                     actor.Key != placed.ActorId &&
                     world.IsAtPlace(actor.Key, new PlaceId(placed.PlaceId))))
        {
            updated = UpdateActor(updated, witness.Id, actor =>
            {
                var facts = new List<BoardFact>
                {
                    new(
                        BoardIds.ObjectPlaced,
                        placed.ObjectId,
                        $"{placed.ActorId} placed {placed.ObjectId} at {placed.PlaceId}; it was publicly available at that moment."),
                };
                if (placed.ObjectId == BoardIds.BrassKey)
                {
                    facts.Add(KeyLocationFact(placed.PlaceId));
                }

                if (actor.Activity is not null)
                {
                    facts.Add(LastOutcome(
                        $"Your wait was interrupted because {placed.ActorId} placed {placed.ObjectId} here."));
                    actor = CompleteActivity(actor);
                }

                return AddFacts(actor, facts);
            });
        }

        return updated;
    }

    private static FirstBoardGameState ApplyGiven(
        FirstBoardGameState game,
        ObjectGivenEvent given)
    {
        BoardActor target = game.Actor(given.TargetActorId);
        FirstBoardGameState updated = UpdateObject(game, given.ObjectId, item => item.WithOwnerActorId(target.Id));
        updated = UpdateActor(updated, given.ActorId, actor =>
            AddFacts(CompleteDecision(actor), [LastOutcome(
                $"You successfully gave {given.ObjectId} to {given.TargetActorId}.")]));
        var targetFacts = new List<BoardFact>
        {
            new(
                BoardIds.ObjectReceived,
                given.ObjectId,
                $"{given.ActorId} gave you {given.ObjectId}."),
        };
        if (given.ObjectId == BoardIds.BrassKey)
        {
            targetFacts.Add(KeyLocationFact());
        }

        return UpdateActor(updated, given.TargetActorId, actor =>
            AddFacts(actor, targetFacts));
    }

    private static FirstBoardGameState ApplyShown(
        FirstBoardGameState game,
        ObjectShownEvent shown)
    {
        FirstBoardGameState updated = UpdateActor(game, shown.ActorId, actor =>
            AddFacts(CompleteDecision(actor), [LastOutcome(
                $"You successfully showed {shown.ObjectId} to {shown.TargetActorId} without giving it away.")]));
        return UpdateActor(updated, shown.TargetActorId, actor =>
        {
            var facts = new List<BoardFact>
            {
                new(
                    BoardIds.ObjectShown,
                    shown.ObjectId,
                    $"{shown.ActorId} showed you {shown.ObjectId}; you verified that they held it at that moment."),
            };
            if (actor.Activity is not null)
            {
                facts.Add(LastOutcome(
                    $"Your wait was interrupted because {shown.ActorId} showed you {shown.ObjectId}."));
                actor = CompleteActivity(actor);
            }

            return AddFacts(actor, facts);
        });
    }

    private static FirstBoardGameState ApplyChestOpened(
        FirstBoardGameState game,
        ChestOpenedEvent opened)
    {
        FirstBoardGameState updated = UpdateObject(
            game.With(chestOpened: true),
            BoardIds.DuchessLetter,
            letter => letter.WithOwnerActorId(game.Actor(opened.ActorId).Id));
        return UpdateActor(updated, opened.ActorId, actor =>
            AddFacts(CompleteDecision(actor),
            [
                new BoardFact(
                    BoardIds.ChestContainsLetter,
                    BoardIds.DuchessLetter,
                    "You recovered the duchess's letter from the opened chest and now carry it."),
                LastOutcome(
                    $"You successfully used {opened.KeyObjectId} to open {opened.ObjectId} " +
                    "and took the duchess's letter."),
            ]));
    }

    private static FirstBoardGameState ConsumeTicket(
        FirstBoardGameState game,
        TicketConsumedEvent consumed)
    {
        BoardActor actor = game.Actor(consumed.ActorId);
        BoardObject ticket = game.Object(consumed.TicketObjectId);
        if (ticket.OwnerActorId != actor.Id)
        {
            throw new InvalidOperationException(
                $"Ticket '{ticket.Key}' is not owned by actor '{actor.Key}'.");
        }

        return game.With(objects: game.Objects.Where(item => item.Id != ticket.Id));
    }

    private static BoardActor CompleteDecision(BoardActor actor) =>
        actor.With(generation: checked(actor.Generation + 1), decisionSequence: checked(actor.DecisionSequence + 1));

    private static BoardActor CompleteActivity(BoardActor actor) =>
        actor.With(generation: checked(actor.Generation + 1)).WithActivity(null);

    private static BoardActor CompleteTravelGoal(BoardActor actor) =>
        actor.With(generation: checked(actor.Generation + 1)).WithTravelGoal(null);

    private static BoardActor AddFacts(BoardActor actor, IEnumerable<BoardFact> facts)
    {
        BoardFact[] merged =
        [
            .. actor.KnownFacts
                .Concat(facts)
                .GroupBy(fact => (fact.Kind, fact.RelatedId))
                .Select(group => group.Last())
                .OrderBy(fact => fact.Kind, StringComparer.Ordinal)
                .ThenBy(fact => fact.RelatedId, StringComparer.Ordinal),
        ];
        return actor.With(knownFacts: merged);
    }

    private static BoardFact RejectedActionFact(RejectedIntentSnapshot intent, string reason) =>
        new(
            BoardIds.ActionRejected,
            intent.ActionKindId,
            $"Action {intent.ActionKindId} was rejected: {reason}; " +
            $"targetActor={intent.TargetActorId}; targetObject={intent.TargetObjectId}; " +
            $"exit={intent.ExitId}; destination={intent.DestinationId}; durationMs={intent.DurationMs}; " +
            $"untilModelTimeMs={intent.UntilModelTimeMs}.");

    private static BoardFact CellarSealedFact() =>
        new(BoardIds.CellarSealedKnown, BoardIds.Cellar, "The cellar is sealed against entry.");

    private static BoardFact KeyLocationFact(string? publicPlaceId = null) =>
        new(
            BoardIds.KeyLocationKnown,
            BoardIds.BrassKey,
            publicPlaceId is null
                ? "The brass key is in an actor's possession."
                : $"The brass key was placed in the public environment at {publicPlaceId}.");

    private static BoardFact LastOutcome(string text) =>
        new(BoardIds.LastActionOutcome, RelatedId: null, Text: text);

    private static string ObservationOutcome(ActorObservedEvent observed) =>
        observed.TargetObjectId is null
            ? $"You successfully observed the current place; " +
              $"the event reported {observed.LearnedFacts.Count} visible facts."
            : $"You successfully inspected {observed.TargetObjectId}; " +
              $"the event reported {observed.LearnedFacts.Count} inspection facts.";

    private static FirstBoardGameState UpdateActor(
        FirstBoardGameState game,
        string actorId,
        Func<BoardActor, BoardActor> update) =>
        game.With(actors: game.Actors
            .Select(actor => actor.Key == actorId ? update(actor) : actor)
            .OrderBy(actor => actor.Id));

    private static FirstBoardGameState UpdateActor(
        FirstBoardGameState game,
        long actorId,
        Func<BoardActor, BoardActor> update) =>
        game.With(actors: game.Actors
            .Select(actor => actor.Id == actorId ? update(actor) : actor)
            .OrderBy(actor => actor.Id));

    private static FirstBoardGameState UpdateObject(
        FirstBoardGameState game,
        string objectId,
        Func<BoardObject, BoardObject> update) =>
        game.With(objects: game.Objects
            .Select(item => item.Key == objectId ? update(item) : item)
            .OrderBy(item => item.Id));

    private static void EnsureUnique<T>(IEnumerable<T> values, string description)
    {
        var known = new HashSet<T>();
        if (values.Any(value => !known.Add(value)))
        {
            throw new InvalidOperationException(
                $"FirstBoard {description} values must be unique.");
        }
    }
}

using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard;

public static class BoardIds
{
    public const string Tavern = "tavern";
    public const string Market = "market";
    public const string Cellar = "cellar";
    public const string Alice = "alice";
    public const string Bob = "bob";
    public const string BrassKey = "brass-key";
    public const string LockedChest = "locked-chest";
    public const string KeyLocationKnown = "key.location-known";
    public const string ChestContainsLetter = "chest.contains-letter";
    public const string CellarSealedKnown = "cellar.sealed-known";
    public const string ObjectHeld = "object.held";
    public const string ActionRejected = "action.rejected";
}

public static class BoardTiming
{
    public const long TravelTicks = 300_000;
    public const long DeadlineTicks = 3_600_000;
    public const long DefaultWaitTicks = 60_000;
    public const long RandomRunBoundaryTicks = 4_200_000;
}

public sealed record BoardPlace(long Id, string Key, IReadOnlyList<string> AdjacentPlaceIds);

public sealed record BoardFact(string Kind, string? RelatedId, string Text);

public enum BoardActivityKind
{
    Travel,
    Wait,
}

public sealed record BoardActivity(
    BoardActivityKind Kind,
    ModelTime Due,
    string? DestinationId = null);

public sealed record SubmittedAction(string DecisionId, Intent Intent);

public sealed record BoardActor(
    long Id,
    string Key,
    string PlaceId,
    long Generation,
    long DecisionSequence,
    bool AwaitingDecision,
    string? OpenDecisionId,
    SubmittedAction? PendingAction,
    BoardActivity? Activity,
    IReadOnlyList<BoardFact> KnownFacts,
    Intent? LastRejectedIntent);

public sealed record BoardObject(
    long Id,
    string Key,
    string? PlaceId,
    long? OwnerActorId,
    long ContentionRound);

public sealed record FirstBoardWorld(
    ulong WorldSeed,
    long WorldRuleSourceId,
    long NextPersistentId,
    IReadOnlyList<BoardPlace> Places,
    IReadOnlyList<BoardActor> Actors,
    IReadOnlyList<BoardObject> Objects,
    bool CellarSealed)
{
    public static FirstBoardWorld CreateInitial(ulong worldSeed)
    {
        long nextId = 1;
        long worldRuleSourceId = nextId++;
        var places = new[]
        {
            new BoardPlace(nextId++, BoardIds.Tavern, [BoardIds.Market]),
            new BoardPlace(nextId++, BoardIds.Market, [BoardIds.Tavern, BoardIds.Cellar]),
            new BoardPlace(nextId++, BoardIds.Cellar, [BoardIds.Market]),
        };
        var actors = new[]
        {
            NewActor(nextId++, BoardIds.Alice, BoardIds.Tavern),
            NewActor(nextId++, BoardIds.Bob, BoardIds.Market),
        };
        var objects = new[]
        {
            new BoardObject(nextId++, BoardIds.BrassKey, BoardIds.Market, null, 0),
        };

        return new FirstBoardWorld(
            worldSeed,
            worldRuleSourceId,
            nextId,
            Array.AsReadOnly(places),
            Array.AsReadOnly(actors),
            Array.AsReadOnly(objects),
            CellarSealed: false);
    }

    public BoardActor Actor(string actorId) =>
        Actors.Single(actor => actor.Key == actorId);

    public BoardActor Actor(long actorId) =>
        Actors.Single(actor => actor.Id == actorId);

    public BoardObject Object(string objectId) =>
        Objects.Single(item => item.Key == objectId);

    public BoardPlace Place(string placeId) =>
        Places.Single(place => place.Key == placeId);

    public bool AreAdjacent(string firstPlaceId, string secondPlaceId) =>
        Place(firstPlaceId).AdjacentPlaceIds.Contains(secondPlaceId, StringComparer.Ordinal);

    public bool IsIdle(BoardActor actor) =>
        !actor.AwaitingDecision && actor.PendingAction is null && actor.Activity is null;

    public bool IsPresent(BoardActor actor) =>
        actor.Activity?.Kind != BoardActivityKind.Travel;

    private static BoardActor NewActor(long id, string key, string placeId) =>
        new(
            id,
            key,
            placeId,
            Generation: 0,
            DecisionSequence: 0,
            AwaitingDecision: false,
            OpenDecisionId: null,
            PendingAction: null,
            Activity: null,
            KnownFacts: [],
            LastRejectedIntent: null);
}

public abstract record BoardEventPayload;

public sealed record DecisionRequestedEvent(
    string ActorId,
    long DecisionNumber,
    string DecisionId) : BoardEventPayload;

public sealed record ActionRequestedEvent(
    string ActorId,
    string DecisionId,
    Intent Intent) : BoardEventPayload;

public sealed record ActorDepartedEvent(
    string ActorId,
    string OriginId,
    string DestinationId,
    ModelTime ArriveAt) : BoardEventPayload;

public sealed record ActorArrivedEvent(
    string ActorId,
    string DestinationId) : BoardEventPayload;

public sealed record ActorWaitStartedEvent(
    string ActorId,
    ModelTime CompleteAt) : BoardEventPayload;

public sealed record ActorWaitedEvent(string ActorId) : BoardEventPayload;

public sealed record ActorSpokeEvent(
    string ActorId,
    string TargetActorId,
    string Text,
    string? SharedFactKind) : BoardEventPayload;

public sealed record ActorObservedEvent(
    string ActorId,
    IReadOnlyList<BoardFact> LearnedFacts) : BoardEventPayload;

public sealed record ObjectTakenEvent(
    string ActorId,
    string ObjectId) : BoardEventPayload;

public sealed record ObjectGivenEvent(
    string ActorId,
    string TargetActorId,
    string ObjectId) : BoardEventPayload;

public sealed record BoardRandomSample(
    ulong StreamId,
    ulong Generation,
    ulong SampleIndex);

public sealed record ObjectContentionResolvedEvent(
    string ObjectId,
    IReadOnlyList<long> CompetitorActorIds,
    long WinnerActorId,
    BoardRandomSample Sample) : BoardEventPayload;

public sealed record ActionRejectedEvent(
    string ActorId,
    Intent RejectedIntent,
    string Reason) : BoardEventPayload;

public sealed record CellarSealedEvent : BoardEventPayload;

public static class BoardEventKinds
{
    public static EventKind DecisionRequested { get; } = new("decision.requested", 1);
    public static EventKind TravelRequested { get; } = new("action.travel-requested", 1);
    public static EventKind WaitRequested { get; } = new("action.wait-requested", 1);
    public static EventKind TalkRequested { get; } = new("action.talk-requested", 1);
    public static EventKind ObserveRequested { get; } = new("action.observe-requested", 1);
    public static EventKind TakeRequested { get; } = new("action.take-requested", 1);
    public static EventKind GiveRequested { get; } = new("action.give-requested", 1);
    public static EventKind UnknownActionRequested { get; } = new("action.unknown-requested", 1);
    public static EventKind ActorDeparted { get; } = new("actor.departed", 1);
    public static EventKind ActorArrived { get; } = new("actor.arrived", 1);
    public static EventKind ActorWaitStarted { get; } = new("actor.wait-started", 1);
    public static EventKind ActorWaited { get; } = new("actor.waited", 1);
    public static EventKind ActorSpoke { get; } = new("actor.spoke", 1);
    public static EventKind ActorObserved { get; } = new("actor.observed", 1);
    public static EventKind ObjectTaken { get; } = new("object.taken", 1);
    public static EventKind ObjectGiven { get; } = new("object.given", 1);
    public static EventKind ObjectContentionResolved { get; } = new("object.contention-resolved", 1);
    public static EventKind ActionRejected { get; } = new("action.rejected", 1);
    public static EventKind CellarSealed { get; } = new("cellar.sealed", 1);

    public static EventKind ForAction(ActionKind actionKind) =>
        actionKind.Id switch
        {
            "action.travel" => TravelRequested,
            "action.wait" => WaitRequested,
            "action.talk" => TalkRequested,
            "action.observe" => ObserveRequested,
            "action.take" => TakeRequested,
            "action.give" => GiveRequested,
            _ => UnknownActionRequested,
        };

    public static bool IsActionRequest(EventKind kind) =>
        kind == TravelRequested ||
        kind == WaitRequested ||
        kind == TalkRequested ||
        kind == ObserveRequested ||
        kind == TakeRequested ||
        kind == GiveRequested ||
        kind == UnknownActionRequested;
}

public sealed class FirstBoardReducer : IEventReducer<FirstBoardWorld, BoardEventPayload>
{
    public FirstBoardWorld Apply(
        FirstBoardWorld world,
        DomainEvent<BoardEventPayload> domainEvent) =>
        (domainEvent.Kind, domainEvent.Payload) switch
        {
            ({ } kind, ActionRequestedEvent requested) when BoardEventKinds.IsActionRequest(kind) =>
                UpdateActor(world, requested.ActorId, actor => actor with
                {
                    AwaitingDecision = false,
                    OpenDecisionId = null,
                    PendingAction = new SubmittedAction(requested.DecisionId, requested.Intent),
                    LastRejectedIntent = null,
                    Generation = checked(actor.Generation + 1),
                }),
            ({ } kind, DecisionRequestedEvent requested) when kind == BoardEventKinds.DecisionRequested =>
                UpdateActor(world, requested.ActorId, actor => actor with
                {
                    AwaitingDecision = true,
                    OpenDecisionId = requested.DecisionId,
                    DecisionSequence = requested.DecisionNumber,
                }),
            ({ } kind, ActorDepartedEvent departed) when kind == BoardEventKinds.ActorDeparted =>
                UpdateActor(world, departed.ActorId, actor => CompleteAction(actor) with
                {
                    Activity = new BoardActivity(
                        BoardActivityKind.Travel,
                        departed.ArriveAt,
                        departed.DestinationId),
                    PlaceId = departed.OriginId,
                }),
            ({ } kind, ActorArrivedEvent arrived) when kind == BoardEventKinds.ActorArrived =>
                UpdateActor(world, arrived.ActorId, actor => CompleteActivity(actor) with
                {
                    PlaceId = arrived.DestinationId,
                }),
            ({ } kind, ActorWaitStartedEvent waited) when kind == BoardEventKinds.ActorWaitStarted =>
                UpdateActor(world, waited.ActorId, actor => CompleteAction(actor) with
                {
                    Activity = new BoardActivity(BoardActivityKind.Wait, waited.CompleteAt),
                }),
            ({ } kind, ActorWaitedEvent waited) when kind == BoardEventKinds.ActorWaited =>
                UpdateActor(world, waited.ActorId, CompleteActivity),
            ({ } kind, ActorSpokeEvent spoke) when kind == BoardEventKinds.ActorSpoke =>
                ApplySpoke(world, spoke),
            ({ } kind, ActorObservedEvent observed) when kind == BoardEventKinds.ActorObserved =>
                UpdateActor(world, observed.ActorId, actor =>
                    AddFacts(CompleteAction(actor), observed.LearnedFacts)),
            ({ } kind, ObjectTakenEvent taken) when kind == BoardEventKinds.ObjectTaken =>
                ApplyTaken(world, taken),
            ({ } kind, ObjectGivenEvent given) when kind == BoardEventKinds.ObjectGiven =>
                ApplyGiven(world, given),
            ({ } kind, ObjectContentionResolvedEvent resolved)
                when kind == BoardEventKinds.ObjectContentionResolved =>
                ApplyContention(world, resolved),
            ({ } kind, ActionRejectedEvent rejected) when kind == BoardEventKinds.ActionRejected =>
                ApplyRejected(world, rejected),
            ({ } kind, CellarSealedEvent) when kind == BoardEventKinds.CellarSealed =>
                ApplyCellarSealed(world),
            _ => throw new InvalidOperationException(
                $"Unknown or out-of-sequence event kind '{domainEvent.Kind.Id}'."),
        };

    private static FirstBoardWorld ApplyRejected(
        FirstBoardWorld world,
        ActionRejectedEvent rejected)
    {
        var learnedFacts = new List<BoardFact>
        {
            RejectedActionFact(rejected.RejectedIntent, rejected.Reason),
        };
        if (rejected.RejectedIntent.ActionKind == ActionKinds.Travel &&
            rejected.RejectedIntent.DestinationId == BoardIds.Cellar &&
            rejected.Reason == "cellar is sealed")
        {
            learnedFacts.Add(CellarSealedFact());
        }

        return UpdateActor(world, rejected.ActorId, actor =>
            AddFacts(CompleteAction(actor) with
            {
                LastRejectedIntent = rejected.RejectedIntent,
            }, learnedFacts));
    }

    private static FirstBoardWorld ApplyCellarSealed(FirstBoardWorld world)
    {
        FirstBoardWorld updated = world with { CellarSealed = true };
        foreach (BoardActor witness in world.Actors.Where(actor =>
                     world.IsPresent(actor) && actor.PlaceId == BoardIds.Cellar))
        {
            updated = UpdateActor(updated, witness.Id, actor => AddFacts(actor, [CellarSealedFact()]));
        }

        return updated;
    }

    private static FirstBoardWorld ApplySpoke(FirstBoardWorld world, ActorSpokeEvent spoke)
    {
        BoardActor speaker = world.Actor(spoke.ActorId);
        BoardFact? sharedFact = spoke.SharedFactKind is null
            ? null
            : speaker.KnownFacts.Single(fact => fact.Kind == spoke.SharedFactKind);
        FirstBoardWorld afterSpeaker = UpdateActor(world, spoke.ActorId, CompleteAction);
        return sharedFact is null
            ? afterSpeaker
            : UpdateActor(afterSpeaker, spoke.TargetActorId, actor => AddFacts(actor, [sharedFact]));
    }

    private static FirstBoardWorld ApplyTaken(FirstBoardWorld world, ObjectTakenEvent taken)
    {
        BoardActor actor = world.Actor(taken.ActorId);
        FirstBoardWorld updated = UpdateObject(world, taken.ObjectId, item => item with
        {
            PlaceId = null,
            OwnerActorId = actor.Id,
        });
        return UpdateActor(updated, taken.ActorId, current =>
            AddFacts(CompleteAction(current), [KeyLocationFact()]));
    }

    private static FirstBoardWorld ApplyGiven(FirstBoardWorld world, ObjectGivenEvent given)
    {
        BoardActor target = world.Actor(given.TargetActorId);
        FirstBoardWorld updated = UpdateObject(world, given.ObjectId, item => item with
        {
            PlaceId = null,
            OwnerActorId = target.Id,
        });
        updated = UpdateActor(updated, given.ActorId, CompleteAction);
        return UpdateActor(updated, given.TargetActorId, actor => AddFacts(actor, [KeyLocationFact()]));
    }

    private static FirstBoardWorld ApplyContention(
        FirstBoardWorld world,
        ObjectContentionResolvedEvent resolved)
    {
        FirstBoardWorld updated = UpdateObject(world, resolved.ObjectId, item => item with
        {
            PlaceId = null,
            OwnerActorId = resolved.WinnerActorId,
            ContentionRound = checked(item.ContentionRound + 1),
        });
        foreach (long actorId in resolved.CompetitorActorIds)
        {
            updated = UpdateActor(updated, actorId, actor => actorId == resolved.WinnerActorId
                ? AddFacts(CompleteAction(actor), [KeyLocationFact()])
                : CompleteAction(actor));
        }

        return updated;
    }

    private static BoardActor CompleteAction(BoardActor actor) =>
        actor with
        {
            PendingAction = null,
            Generation = checked(actor.Generation + 1),
        };

    private static BoardActor CompleteActivity(BoardActor actor) =>
        actor with
        {
            Activity = null,
            Generation = checked(actor.Generation + 1),
        };

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
        return actor with { KnownFacts = Array.AsReadOnly(merged) };
    }

    private static BoardFact RejectedActionFact(Intent intent, string reason) =>
        new(
            BoardIds.ActionRejected,
            intent.ActionKind.Id,
            $"Action {intent.ActionKind.Id} was rejected: {reason}; " +
            $"targetActor={intent.TargetActorId}; targetObject={intent.TargetObjectId}; " +
            $"destination={intent.DestinationId}; durationMs={intent.DurationMs}; " +
            $"untilModelTimeMs={intent.UntilModelTimeMs}.");

    private static BoardFact CellarSealedFact() =>
        new(BoardIds.CellarSealedKnown, BoardIds.Cellar, "The cellar is sealed.");

    private static BoardFact KeyLocationFact() =>
        new(BoardIds.KeyLocationKnown, BoardIds.BrassKey, "The brass key is in an actor's possession.");

    private static FirstBoardWorld UpdateActor(
        FirstBoardWorld world,
        string actorId,
        Func<BoardActor, BoardActor> update) =>
        world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor.Key == actorId ? update(actor) : actor)
                .OrderBy(actor => actor.Id)
                .ToArray()),
        };

    private static FirstBoardWorld UpdateActor(
        FirstBoardWorld world,
        long actorId,
        Func<BoardActor, BoardActor> update) =>
        world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor.Id == actorId ? update(actor) : actor)
                .OrderBy(actor => actor.Id)
                .ToArray()),
        };

    private static FirstBoardWorld UpdateObject(
        FirstBoardWorld world,
        string objectId,
        Func<BoardObject, BoardObject> update) =>
        world with
        {
            Objects = Array.AsReadOnly(world.Objects
                .Select(item => item.Key == objectId ? update(item) : item)
                .OrderBy(item => item.Id)
                .ToArray()),
        };
}

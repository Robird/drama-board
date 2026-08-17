using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard;

public abstract record BoardCandidate;

public sealed record DecisionCandidate : BoardCandidate;

public sealed record ActionCandidate(long ActorId, long Generation) : BoardCandidate;

public sealed record ActivityCandidate(long ActorId, long Generation) : BoardCandidate;

public sealed record DeadlineCandidate : BoardCandidate;

public sealed class DecisionSchedulingSystem :
    ISimSystem<FirstBoardWorld, BoardCandidate, BoardEventPayload>
{
    public IReadOnlyList<EventCandidate<BoardCandidate>> ForecastNext(
        FirstBoardWorld world,
        ModelTime now)
    {
        bool hasUnresolvedWorkAtNow = world.Actors.Any(actor =>
            actor.PendingAction is not null || actor.Activity?.Due <= now);
        return hasUnresolvedWorkAtNow || !world.Actors.Any(world.IsIdle)
            ? []
            :
            [
                new EventCandidate<BoardCandidate>(
                    new EventCandidateId(1),
                    now,
                    world.WorldRuleSourceId,
                    new DecisionCandidate()),
            ];
    }

    public IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> Resolve(
        FirstBoardWorld world,
        EventCandidate<BoardCandidate> candidate)
    {
        if (candidate.Payload is not DecisionCandidate)
        {
            throw new InvalidOperationException("The decision system received another system's candidate.");
        }

        BoardActor[] idleActors = [.. world.Actors.Where(world.IsIdle).OrderBy(actor => actor.Id)];
        if (idleActors.Length == 0 || world.Actors.Any(actor =>
            actor.PendingAction is not null || actor.Activity?.Due <= candidate.Due))
        {
            throw new InvalidOperationException("The world decision candidate is stale.");
        }

        return
        [
            .. idleActors.Select(actor =>
            {
                long decisionNumber = checked(actor.DecisionSequence + 1);
                string decisionId = $"decision.{actor.Key}.{decisionNumber}";
                return new UncommittedDomainEvent<BoardEventPayload>(
                    BoardEventKinds.DecisionRequested,
                    new DecisionRequestedEvent(actor.Key, decisionNumber, decisionId));
            }),
        ];
    }
}

public sealed class ActionResolutionSystem :
    ISimSystem<FirstBoardWorld, BoardCandidate, BoardEventPayload>
{
    public IReadOnlyList<EventCandidate<BoardCandidate>> ForecastNext(
        FirstBoardWorld world,
        ModelTime now) =>
        [
            .. world.Actors
                .Where(actor => actor.PendingAction is not null)
                .OrderBy(actor => actor.Id)
                .Select(actor => new EventCandidate<BoardCandidate>(
                    new EventCandidateId(actor.Generation),
                    now,
                    actor.Id,
                    new ActionCandidate(actor.Id, actor.Generation))),
        ];

    public IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> Resolve(
        FirstBoardWorld world,
        EventCandidate<BoardCandidate> candidate)
    {
        if (candidate.Payload is not ActionCandidate action)
        {
            throw new InvalidOperationException("The action system received another system's candidate.");
        }

        BoardActor actor = world.Actor(action.ActorId);
        if (actor.PendingAction is not SubmittedAction submitted || actor.Generation != action.Generation)
        {
            throw new InvalidOperationException("The action candidate is stale for its actor.");
        }

        return submitted.Intent.ActionKind.Id switch
        {
            "action.travel" => ResolveTravel(world, actor, submitted.Intent, candidate.Due),
            "action.wait" => ResolveWait(actor, submitted.Intent, candidate.Due),
            "action.talk" => ResolveTalk(world, actor, submitted.Intent),
            "action.observe" => ResolveObserve(world, actor, submitted.Intent),
            "action.take" => ResolveTake(world, actor, submitted.Intent),
            "action.put" => ResolvePut(world, actor, submitted.Intent),
            "action.give" => ResolveGive(world, actor, submitted.Intent),
            "action.show" => ResolveShow(world, actor, submitted.Intent),
            "action.use" => ResolveUse(world, actor, submitted.Intent),
            _ => Reject(actor, submitted.Intent, "unknown action kind"),
        };
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveTravel(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        ModelTime now)
    {
        if (actor.Activity is not null)
        {
            return Reject(actor, intent, "actor is already busy");
        }

        string? destinationId = intent.DestinationId;
        if (destinationId is null || !world.Places.Any(place => place.Key == destinationId))
        {
            return Reject(actor, intent, "destination does not exist");
        }

        if (!world.AreAdjacent(actor.PlaceId, destinationId))
        {
            return Reject(actor, intent, "destination is not adjacent");
        }

        if (destinationId == BoardIds.Cellar && world.CellarSealed)
        {
            return Reject(actor, intent, "cellar is sealed");
        }

        return Result(
            BoardEventKinds.ActorDeparted,
            new ActorDepartedEvent(
                actor.Key,
                actor.PlaceId,
                destinationId,
                now + new ModelDuration(BoardTiming.TravelTicks)));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveWait(
        BoardActor actor,
        Intent intent,
        ModelTime now)
    {
        ModelTime completeAt;
        if (intent.UntilModelTimeMs is long until)
        {
            if (until <= now.Ticks)
            {
                return Reject(actor, intent, "wait duration must be positive");
            }

            completeAt = new ModelTime(until);
        }
        else
        {
            long duration = intent.DurationMs ?? BoardTiming.DefaultWaitTicks;
            if (duration <= 0)
            {
                return Reject(actor, intent, "wait duration must be positive");
            }

            if (now.Ticks > long.MaxValue - duration)
            {
                return Reject(actor, intent, "wait completion time exceeds the model-time range");
            }

            completeAt = now + new ModelDuration(duration);
        }

        return Result(
            BoardEventKinds.ActorWaitStarted,
            new ActorWaitStartedEvent(actor.Key, completeAt));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveTalk(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardActor? target = world.Actors.SingleOrDefault(current => current.Key == intent.TargetActorId);
        if (target is null || target.Id == actor.Id)
        {
            return Reject(actor, intent, "target actor does not exist");
        }

        if (!world.IsPresent(actor) || !world.IsPresent(target) || target.PlaceId != actor.PlaceId)
        {
            return Reject(actor, intent, "target actor is not at the same place");
        }

        string text = intent.FreeText ?? string.Empty;
        string? sharedFactKind = ParseFactReference(text);
        if (sharedFactKind is not null &&
            !actor.KnownFacts.Any(fact => fact.Kind == sharedFactKind))
        {
            return Reject(actor, intent, "speaker does not know the referenced fact");
        }

        return Result(
            BoardEventKinds.ActorSpoke,
            new ActorSpokeEvent(actor.Key, target.Key, text, sharedFactKind));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveObserve(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        if (intent.TargetObjectId is string targetObjectId)
        {
            return ResolveInspectObject(world, actor, intent, targetObjectId);
        }

        var facts = new List<BoardFact>();
        foreach (BoardActor visibleActor in world.Actors.Where(other =>
                     other.Id != actor.Id &&
                     world.IsPresent(other) &&
                     other.PlaceId == actor.PlaceId))
        {
            facts.Add(new BoardFact(
                "actor.present",
                visibleActor.Key,
                $"{visibleActor.Key} is present at {actor.PlaceId}."));
        }

        foreach (BoardObject visibleObject in VisibleObjects(world, actor))
        {
            facts.Add(new BoardFact(
                "object.visible",
                visibleObject.Key,
                $"{visibleObject.Key} is visible at {actor.PlaceId}."));
            if (visibleObject.Key == BoardIds.BrassKey)
            {
                facts.Add(new BoardFact(
                    BoardIds.KeyLocationKnown,
                    BoardIds.BrassKey,
                    "The brass key's location is known."));
            }
        }

        if (actor.PlaceId == BoardIds.Cellar)
        {
            facts.Add(new BoardFact(
                "cellar.locked-chest-visible",
                BoardIds.LockedChest,
                world.ChestOpened
                    ? "An opened chest is visible in the cellar."
                    : "A locked chest is visible in the cellar."));
            if (world.ChestOpened)
            {
                BoardObject letter = world.Object(BoardIds.DuchessLetter);
                facts.Add(new BoardFact(
                    BoardIds.ChestOpenedKnown,
                    BoardIds.LockedChest,
                    letter.OwnerActorId == actor.Id
                        ? "The chest is open; you recovered the duchess's letter from it."
                        : "The chest is open and empty; someone removed the duchess's letter."));
            }
        }

        BoardFact[] learned =
        [
            .. facts
                .OrderBy(fact => fact.Kind, StringComparer.Ordinal)
                .ThenBy(fact => fact.RelatedId, StringComparer.Ordinal),
        ];
        return Result(
            BoardEventKinds.ActorObserved,
            new ActorObservedEvent(actor.Key, Array.AsReadOnly(learned)));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveInspectObject(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        string targetObjectId)
    {
        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == targetObjectId);
        if (item is null)
        {
            return Reject(actor, intent, "target object does not exist");
        }

        bool heldByActor = item.OwnerActorId == actor.Id;
        bool publicHere = item.OwnerActorId is null &&
            item.PlaceId == actor.PlaceId &&
            world.IsPresent(actor);
        if (!heldByActor && !publicHere)
        {
            return Reject(actor, intent, "actor may only inspect a held or public object here");
        }

        var facts = new List<BoardFact>
        {
            new(
                BoardIds.ObjectInspected,
                item.Key,
                heldByActor
                    ? $"You carefully inspected {item.Key} while holding it."
                    : $"You carefully inspected public {item.Key} at {actor.PlaceId}."),
        };
        if (item.Key == BoardIds.DuchessLetter)
        {
            facts.Add(new BoardFact(
                BoardIds.LetterAuthenticityKnown,
                item.Key,
                "The duchess's signet, handwriting, and private cipher confirm that the letter is genuine."));
            facts.Add(new BoardFact(
                BoardIds.LetterContentsKnown,
                item.Key,
                "The letter orders its bearer to deliver evidence of the cellar conspiracy to the city archivist; " +
                "the brass key is no longer required once the letter is recovered."));
        }

        BoardFact[] learned =
        [
            .. facts
                .OrderBy(fact => fact.Kind, StringComparer.Ordinal)
                .ThenBy(fact => fact.RelatedId, StringComparer.Ordinal),
        ];
        return Result(
            BoardEventKinds.ActorObserved,
            new ActorObservedEvent(actor.Key, Array.AsReadOnly(learned), item.Key));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveTake(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == intent.TargetObjectId);
        if (item is null)
        {
            return Reject(actor, intent, "target object does not exist");
        }

        if (item.OwnerActorId is not null || item.PlaceId != actor.PlaceId || !world.IsPresent(actor))
        {
            return Reject(actor, intent, "target object is not available here");
        }

        long[] competitors =
        [
            .. world.Actors
                .Where(current =>
                    world.IsPresent(current) &&
                    current.PlaceId == actor.PlaceId &&
                    current.PendingAction?.Intent.ActionKind == ActionKinds.Take &&
                    current.PendingAction.Intent.TargetObjectId == item.Key)
                .Select(current => current.Id)
                .OrderBy(actorId => actorId),
        ];
        if (!competitors.Contains(actor.Id))
        {
            throw new InvalidOperationException("The resolving actor is not a take competitor.");
        }

        if (competitors.Length == 1)
        {
            return Result(
                BoardEventKinds.ObjectTaken,
                new ObjectTakenEvent(actor.Key, item.Key));
        }

        ulong generation = checked((ulong)item.ContentionRound);
        ulong streamId = DeterministicRandom.DeriveStreamId(item.Id);
        int winnerIndex = DeterministicRandom.SampleInt32(
            world.WorldSeed,
            streamId,
            generation,
            minInclusive: 0,
            maxExclusive: competitors.Length,
            sampleIndex: 0);
        return Result(
            BoardEventKinds.ObjectContentionResolved,
            new ObjectContentionResolvedEvent(
                item.Key,
                Array.AsReadOnly(competitors),
                competitors[winnerIndex],
                new BoardRandomSample(streamId, generation, 0)));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolvePut(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == intent.TargetObjectId);
        if (item is null || item.OwnerActorId != actor.Id)
        {
            return Reject(actor, intent, "actor does not hold the target object");
        }

        if (!world.IsPresent(actor))
        {
            return Reject(actor, intent, "actor is not present at a place");
        }

        return Result(
            BoardEventKinds.ObjectPlaced,
            new ObjectPlacedEvent(actor.Key, item.Key, actor.PlaceId));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveGive(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == intent.TargetObjectId);
        BoardActor? target = world.Actors.SingleOrDefault(current => current.Key == intent.TargetActorId);
        if (item is null || item.OwnerActorId != actor.Id)
        {
            return Reject(actor, intent, "actor does not hold the target object");
        }

        if (target is null || target.Id == actor.Id)
        {
            return Reject(actor, intent, "target actor does not exist");
        }

        if (!world.IsPresent(actor) || !world.IsPresent(target) || target.PlaceId != actor.PlaceId)
        {
            return Reject(actor, intent, "target actor is not at the same place");
        }

        return Result(
            BoardEventKinds.ObjectGiven,
            new ObjectGivenEvent(actor.Key, target.Key, item.Key));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveShow(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == intent.TargetObjectId);
        BoardActor? target = world.Actors.SingleOrDefault(current => current.Key == intent.TargetActorId);
        if (item is null || item.OwnerActorId != actor.Id)
        {
            return Reject(actor, intent, "actor does not hold the target object");
        }

        if (target is null || target.Id == actor.Id)
        {
            return Reject(actor, intent, "target actor does not exist");
        }

        if (!world.IsPresent(actor) || !world.IsPresent(target) || target.PlaceId != actor.PlaceId)
        {
            return Reject(actor, intent, "target actor is not at the same place");
        }

        return Result(
            BoardEventKinds.ObjectShown,
            new ObjectShownEvent(actor.Key, target.Key, item.Key));
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> ResolveUse(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        if (intent.TargetObjectId != BoardIds.LockedChest)
        {
            return Reject(actor, intent, "target object cannot be used here");
        }

        if (!world.IsPresent(actor) || actor.PlaceId != BoardIds.Cellar)
        {
            return Reject(actor, intent, "locked chest is not at the actor's current place");
        }

        if (world.CellarSealed)
        {
            return Reject(actor, intent, "cellar is sealed");
        }

        if (world.ChestOpened)
        {
            return Reject(actor, intent, "locked chest is already open");
        }

        if (!ActorOwns(world, actor, BoardIds.BrassKey))
        {
            return Reject(actor, intent, "actor does not hold the brass key");
        }

        return Result(
            BoardEventKinds.ChestOpened,
            new ChestOpenedEvent(actor.Key, BoardIds.LockedChest, BoardIds.BrassKey));
    }

    private static IEnumerable<BoardObject> VisibleObjects(FirstBoardWorld world, BoardActor observer) =>
        world.Objects.Where(item =>
            item.PlaceId == observer.PlaceId ||
            item.OwnerActorId == observer.Id);

    private static bool ActorOwns(FirstBoardWorld world, BoardActor actor, string objectId) =>
        world.Object(objectId).OwnerActorId == actor.Id;

    private static string? ParseFactReference(string text)
    {
        const string prefix = "fact:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string factKind = text[prefix.Length..];
        return factKind.Length == 0 ? null : factKind;
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> Reject(
        BoardActor actor,
        Intent intent,
        string reason) =>
        Result(
            BoardEventKinds.ActionRejected,
            new ActionRejectedEvent(actor.Key, intent, reason));

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> Result(
        DramaBoard.Kernel.Journal.EventKind kind,
        BoardEventPayload payload) =>
        [new UncommittedDomainEvent<BoardEventPayload>(kind, payload)];
}

public sealed class ActivityCompletionSystem :
    ISimSystem<FirstBoardWorld, BoardCandidate, BoardEventPayload>
{
    public IReadOnlyList<EventCandidate<BoardCandidate>> ForecastNext(
        FirstBoardWorld world,
        ModelTime now) =>
        [
            .. world.Actors
                .Where(actor => actor.Activity is not null)
                .OrderBy(actor => actor.Id)
                .Select(actor => new EventCandidate<BoardCandidate>(
                    new EventCandidateId(actor.Generation),
                    actor.Activity!.Due,
                    actor.Id,
                    new ActivityCandidate(actor.Id, actor.Generation))),
        ];

    public IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> Resolve(
        FirstBoardWorld world,
        EventCandidate<BoardCandidate> candidate)
    {
        if (candidate.Payload is not ActivityCandidate activity)
        {
            throw new InvalidOperationException("The activity system received another system's candidate.");
        }

        BoardActor actor = world.Actor(activity.ActorId);
        if (actor.Activity is not BoardActivity current ||
            actor.Generation != activity.Generation ||
            current.Due != candidate.Due)
        {
            throw new InvalidOperationException("The activity candidate is stale for its actor.");
        }

        return current.Kind switch
        {
            BoardActivityKind.Travel when current.DestinationId is not null =>
                Result(BoardEventKinds.ActorArrived, new ActorArrivedEvent(actor.Key, current.DestinationId)),
            BoardActivityKind.Wait =>
                Result(BoardEventKinds.ActorWaited, new ActorWaitedEvent(actor.Key)),
            _ => throw new InvalidOperationException("The actor activity is invalid."),
        };
    }

    private static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> Result(
        DramaBoard.Kernel.Journal.EventKind kind,
        BoardEventPayload payload) =>
        [new UncommittedDomainEvent<BoardEventPayload>(kind, payload)];
}

public sealed class CellarDeadlineSystem :
    ISimSystem<FirstBoardWorld, BoardCandidate, BoardEventPayload>
{
    public IReadOnlyList<EventCandidate<BoardCandidate>> ForecastNext(
        FirstBoardWorld world,
        ModelTime now) =>
        world.CellarSealed
            ? []
            :
            [
                new EventCandidate<BoardCandidate>(
                    new EventCandidateId(0),
                    new ModelTime(BoardTiming.DeadlineTicks),
                    world.WorldRuleSourceId,
                    new DeadlineCandidate()),
            ];

    public IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> Resolve(
        FirstBoardWorld world,
        EventCandidate<BoardCandidate> candidate)
    {
        if (candidate.Payload is not DeadlineCandidate || world.CellarSealed)
        {
            throw new InvalidOperationException("The cellar deadline candidate is stale.");
        }

        return
        [
            new UncommittedDomainEvent<BoardEventPayload>(
                BoardEventKinds.CellarSealed,
                new CellarSealedEvent()),
        ];
    }
}

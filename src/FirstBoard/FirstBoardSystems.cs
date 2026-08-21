using DramaBoard.Decision.Validation;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard;

public abstract record BoardCandidate;

public sealed record DecisionPointCandidate(long ActorId, long Generation) : BoardCandidate;

public sealed record ActivityCandidate(long ActorId, long Generation) : BoardCandidate;

public sealed record DeadlineCandidate : BoardCandidate;

public sealed class DecisionPointRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, BoardEventPayload>
{
    private readonly IReadOnlyDictionary<string, IPlayerDriver> _drivers;

    public DecisionPointRule(IReadOnlyDictionary<string, IPlayerDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        _drivers = new Dictionary<string, IPlayerDriver>(drivers, StringComparer.Ordinal);
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules) =>
        [
            .. world.Actors
                .Where(world.IsIdle)
                .OrderBy(actor => actor.Id)
                .Select(actor => new OccurrenceCandidate<BoardCandidate>(
                    CandidateKey.FromUtf8(
                        $"firstboard/decision/{actor.Key}/{actor.DecisionSequence + 1}"),
                    new CandidateDue(world.Now),
                    new DecisionPointCandidate(actor.Id, actor.Generation))),
        ];

    public async ValueTask<TransitionDraft<BoardEventPayload>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        if (winner.Data is not DecisionPointCandidate decisionPoint)
        {
            throw new InvalidOperationException("The decision rule received another rule's candidate.");
        }

        BoardActor actor = world.Actor(decisionPoint.ActorId);
        if (!world.IsIdle(actor) || actor.Generation != decisionPoint.Generation)
        {
            throw new InvalidOperationException("The selected decision point is stale.");
        }

        if (!_drivers.TryGetValue(actor.Key, out IPlayerDriver? driver))
        {
            throw new InvalidOperationException($"No Player driver is registered for actor '{actor.Key}'.");
        }

        DecisionRequest request = FirstBoardScenario.BuildRequest(
            world,
            actor,
            winner.Due.ModelTime);
        PlayerDecision decision = await driver.DecideAsync(request, cancellationToken)
            ?? throw new InvalidOperationException("A Player driver returned null.");
        PlayerDecisionValidationResult validation = PlayerDecisionValidator.Validate(decision, request);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        return new TransitionDraft<BoardEventPayload>(
            FirstBoardActionPlanner.Plan(world, actor, decision.Intent, winner.Due.ModelTime));
    }
}

public static class FirstBoardActionPlanner
{
    public static IReadOnlyList<BoardEventPayload> Plan(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        ModelTime now)
    {
        return intent.ActionKind.Id switch
        {
            "action.travel" => ResolveTravel(world, actor, intent, now),
            "action.wait" => ResolveWait(actor, intent, now),
            "action.talk" => ResolveTalk(world, actor, intent),
            "action.observe" => ResolveObserve(world, actor, intent),
            "action.take" => ResolveTake(world, actor, intent),
            "action.put" => ResolvePut(world, actor, intent),
            "action.give" => ResolveGive(world, actor, intent),
            "action.show" => ResolveShow(world, actor, intent),
            "action.use" => ResolveUse(world, actor, intent),
            _ => Reject(actor, intent, "unknown action kind"),
        };
    }

    private static IReadOnlyList<BoardEventPayload> ResolveTravel(
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
            new ActorDepartedEvent(
                actor.Key,
                actor.PlaceId,
                destinationId,
                now + new ModelDuration(BoardTiming.TravelTicks)));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveWait(
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
            new ActorWaitStartedEvent(actor.Key, completeAt));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveTalk(
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
            new ActorSpokeEvent(actor.Key, target.Key, text, sharedFactKind));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveObserve(
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
            new ActorObservedEvent(actor.Key, Array.AsReadOnly(learned)));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveInspectObject(
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
            new ActorObservedEvent(actor.Key, Array.AsReadOnly(learned), item.Key));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveTake(
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

        return Result(
            new ObjectTakenEvent(actor.Key, item.Key));
    }

    private static IReadOnlyList<BoardEventPayload> ResolvePut(
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
            new ObjectPlacedEvent(actor.Key, item.Key, actor.PlaceId));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveGive(
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
            new ObjectGivenEvent(actor.Key, target.Key, item.Key));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveShow(
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
            new ObjectShownEvent(actor.Key, target.Key, item.Key));
    }

    private static IReadOnlyList<BoardEventPayload> ResolveUse(
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

    private static IReadOnlyList<BoardEventPayload> Reject(
        BoardActor actor,
        Intent intent,
        string reason) =>
        Result(
            new ActionRejectedEvent(actor.Key, intent, reason));

    private static IReadOnlyList<BoardEventPayload> Result(
        BoardEventPayload payload) =>
        [payload];
}

public sealed class ActivityCompletionRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, BoardEventPayload>
{
    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules) =>
        [
            .. world.Actors
                .Where(actor => actor.Activity is not null)
                .OrderBy(actor => actor.Id)
                .Select(actor => new OccurrenceCandidate<BoardCandidate>(
                    CandidateKey.FromUtf8(
                        $"firstboard/activity/{actor.Key}/{actor.Generation}"),
                    new CandidateDue(actor.Activity!.Due),
                    new ActivityCandidate(actor.Id, actor.Generation))),
        ];

    public ValueTask<TransitionDraft<BoardEventPayload>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (winner.Data is not ActivityCandidate activity)
        {
            throw new InvalidOperationException("The activity rule received another rule's candidate.");
        }

        BoardActor actor = world.Actor(activity.ActorId);
        if (actor.Activity is not BoardActivity current ||
            actor.Generation != activity.Generation ||
            current.Due != winner.Due.ModelTime)
        {
            throw new InvalidOperationException("The activity candidate is stale for its actor.");
        }

        BoardEventPayload fact = current.Kind switch
        {
            BoardActivityKind.Travel when current.DestinationId is not null =>
                new ActorArrivedEvent(actor.Key, current.DestinationId),
            BoardActivityKind.Wait =>
                new ActorWaitedEvent(actor.Key),
            _ => throw new InvalidOperationException("The actor activity is invalid."),
        };
        return ValueTask.FromResult(new TransitionDraft<BoardEventPayload>([fact]));
    }
}

public sealed class CellarDeadlineRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, BoardEventPayload>
{
    private readonly long _deadlineMs;

    /// <summary>Creates the deadline rule with a scenario-provided model time.</summary>
    public CellarDeadlineRule(long deadlineMs = BoardTiming.DeadlineTicks)
    {
        if (deadlineMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deadlineMs),
                "The deadline cannot be negative.");
        }

        _deadlineMs = deadlineMs;
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules) =>
        world.CellarSealed
            ? []
            :
            [
                new OccurrenceCandidate<BoardCandidate>(
                    CandidateKey.FromUtf8("firstboard/deadline/cellar-seal"),
                    new CandidateDue(new ModelTime(_deadlineMs)),
                    new DeadlineCandidate()),
            ];

    public ValueTask<TransitionDraft<BoardEventPayload>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (winner.Data is not DeadlineCandidate || world.CellarSealed)
        {
            throw new InvalidOperationException("The cellar deadline candidate is stale.");
        }

        return ValueTask.FromResult(
            new TransitionDraft<BoardEventPayload>([new CellarSealedEvent()]));
    }
}

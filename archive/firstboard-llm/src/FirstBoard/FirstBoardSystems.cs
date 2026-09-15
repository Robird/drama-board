using System.Text.Json;
using DramaBoard.Decision.Validation;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

public abstract record BoardCandidate;

public sealed record DecisionPointCandidate(long ActorId, long Generation) : BoardCandidate;

public sealed record ActivityCandidate(long ActorId, long Generation) : BoardCandidate;

public sealed record TravelGoalCandidate(
    long ActorId,
    long ActorGeneration,
    long SpatialMovementGeneration,
    PlaceId CurrentPlaceId,
    PlaceId DestinationPlaceId) : BoardCandidate;

public sealed record DeadlineCandidate : BoardCandidate;

public sealed record SpatialBoardCandidate(SpatialOccurrenceData Value) : BoardCandidate;

public sealed record PassageEncounterOpeningCandidate(
    PassageContactOccurrenceData Value) : BoardCandidate;

public sealed record PassageEncounterResponseCandidate(
    PassageContactKey ContactKey,
    string RespondingActorId,
    long ActorGeneration,
    long NextDecisionSequence) : BoardCandidate;

public sealed record PassageEncounterCleanupCandidate(
    PassageContactKey ContactKey) : BoardCandidate;

public sealed class DecisionPointRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
{
    private readonly IReadOnlyDictionary<string, IPlayerDriver> _drivers;
    private readonly ScenarioInstance _instance;
    private readonly IPlayerSpatialKnowledgeGetter<FirstBoardWorld> _spatialKnowledgeGetter;

    public DecisionPointRule(
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        ScenarioInstance instance,
        IPlayerSpatialKnowledgeGetter<FirstBoardWorld>? spatialKnowledgeGetter = null)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(instance);
        _drivers = new Dictionary<string, IPlayerDriver>(drivers, StringComparer.Ordinal);
        _instance = instance;
        _spatialKnowledgeGetter = spatialKnowledgeGetter ??
            FullMapPlayerSpatialKnowledgeGetter<FirstBoardWorld>.Instance;
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules) =>
        [
            .. world.Actors
                .Where(actor => _drivers.ContainsKey(actor.Key))
                .Where(world.IsReadyForDecision)
                .OrderBy(actor => actor.Id)
                .Select(actor => new OccurrenceCandidate<BoardCandidate>(
                    CandidateKey.FromUtf8(
                        $"firstboard/decision/{actor.Key}/{actor.DecisionSequence + 1}"),
                    new CandidateDue(world.Now),
                    new DecisionPointCandidate(actor.Id, actor.Generation))),
        ];

    public async ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        if (winner.Data is not DecisionPointCandidate decisionPoint)
        {
            throw new InvalidOperationException("The decision rule received another rule's candidate.");
        }

        BoardActor actor = world.Actor(decisionPoint.ActorId);
        if (!world.IsReadyForDecision(actor) || actor.Generation != decisionPoint.Generation)
        {
            throw new InvalidOperationException("The selected decision point is stale.");
        }

        if (!_drivers.TryGetValue(actor.Key, out IPlayerDriver? driver))
        {
            throw new InvalidOperationException($"No Player driver is registered for actor '{actor.Key}'.");
        }

        PlayerSpatialKnowledgeSnapshot knowledge = _spatialKnowledgeGetter.GetKnownGraph(
            world,
            actor.Key,
            _instance.Graph) ?? throw new InvalidOperationException(
                "A Player spatial knowledge Getter returned null.");
        DecisionRequest request = FirstBoardScenario.BuildRequest(
            _instance,
            world,
            actor,
            winner.Due.ModelTime,
            knowledge);
        PlayerDecision decision = await driver.DecideAsync(request, cancellationToken)
            ?? throw new InvalidOperationException("A Player driver returned null.");
        PlayerDecisionValidationResult validation = PlayerDecisionValidator.Validate(decision, request);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        return new TransitionDraft<FirstBoardFact>(
            FirstBoardActionPlanner.Plan(
                _instance,
                world,
                actor,
                decision.Intent,
                winner.Due.ModelTime,
                knowledge));
    }
}

public static class FirstBoardActionPlanner
{
    public static IReadOnlyList<FirstBoardFact> Plan(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        ModelTime now,
        PlayerSpatialKnowledgeSnapshot knowledge) =>
        intent.ActionKind.Id switch
        {
            "action.travel" => ResolveTravel(instance, world, actor, intent, now),
            "action.travel-to" => ResolveTravelTo(
                instance,
                world,
                actor,
                intent,
                now,
                knowledge),
            "action.wait" => ResolveWait(world, actor, intent, now),
            "action.talk" => ResolveTalk(world, actor, intent),
            "action.observe" => ResolveObserve(world, actor, intent),
            "action.take" => ResolveTake(instance, world, actor, intent),
            "action.put" => ResolvePut(instance, world, actor, intent),
            "action.give" => ResolveGive(world, actor, intent),
            "action.show" => ResolveShow(world, actor, intent),
            "action.use" => ResolveUse(world, actor, intent),
            _ => Reject(actor, intent, "unknown action kind"),
        };

    private static IReadOnlyList<FirstBoardFact> ResolveTravelTo(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        ModelTime now,
        PlayerSpatialKnowledgeSnapshot knowledge)
    {
        if (!world.IsReadyForDecision(actor))
        {
            return Reject(actor, intent, "actor is not idle at a place");
        }

        if (intent.DestinationId is not string destinationId)
        {
            throw new InvalidOperationException(
                "A validated TravelTo intent must contain a destination.");
        }

        var destination = new PlaceId(destinationId);
        FirstBoardTravelGoalPlan plan = FirstBoardTravelGoalPlanner.PlanNextLeg(
            instance,
            world,
            actor,
            destination,
            knowledge);
        if (plan.Route is not RouteFound || plan.FirstExit is null)
        {
            throw new InvalidOperationException(
                $"An advertised TravelTo destination '{destination}' no longer has a legal first leg.");
        }

        return StartTravelGoalLeg(
            instance,
            world,
            actor,
            plan.FirstExit,
            now,
            destination);
    }

    internal static IReadOnlyList<FirstBoardFact> StartTravelGoalLeg(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        FirstBoardExit selected,
        ModelTime now,
        PlaceId? setGoalDestination = null)
    {
        var planner = new SpatialPlanner(instance.Graph);
        SpatialPlanResult movement = planner.TryStartTraversal(
            world.Spatial,
            new EntityId(actor.Key),
            selected.Objective.PassageId,
            BoardTiming.TravelSpeed,
            now);
        if (movement is not SpatialPlanAccepted accepted)
        {
            throw new InvalidOperationException(
                "A TravelTo first leg was rejected by objective Spatial: " +
                ((SpatialPlanRejected)movement).Reason);
        }

        var facts = new List<FirstBoardFact>();
        if (setGoalDestination is PlaceId destination)
        {
            facts.Add(new GameBoardFact(new ActorTravelGoalSetEvent(actor.Key, destination)));
        }

        if (selected.RequiredTicketObjectId is string ticket)
        {
            facts.Add(new GameBoardFact(new TicketConsumedEvent(actor.Key, ticket)));
        }

        facts.AddRange(accepted.Facts.Select(fact => new SpatialBoardFact(fact)));
        return facts.AsReadOnly();
    }

    private static IReadOnlyList<FirstBoardFact> ResolveTravel(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        ModelTime now)
    {
        if (!world.IsReadyForDecision(actor))
        {
            return Reject(actor, intent, "actor is not idle at a place");
        }

        FirstBoardExit? selected = FirstBoardSpatialProjection
            .GetExits(instance, world, actor)
            .SingleOrDefault(exit => exit.ExitId == intent.ExitId);
        if (selected is null)
        {
            return Reject(actor, intent, "exit does not exist here");
        }

        if (!selected.CanTakeNow)
        {
            string reason = selected.Objective.PassageId ==
                    new PassageId(BoardIds.CellarGatePassage) &&
                world.CellarSealed
                ? "cellar is sealed"
                : "exit is not currently available";
            return Reject(actor, intent, reason);
        }

        var planner = new SpatialPlanner(instance.Graph);
        SpatialPlanResult movement = planner.TryStartTraversal(
            world.Spatial,
            new EntityId(actor.Key),
            selected.Objective.PassageId,
            BoardTiming.TravelSpeed,
            now);
        if (movement is not SpatialPlanAccepted accepted)
        {
            string reason = ((SpatialPlanRejected)movement).Reason;
            throw new InvalidOperationException(
                $"An advertised FirstBoard exit was rejected by Spatial: {reason}");
        }

        var facts = new List<FirstBoardFact>();
        if (selected.RequiredTicketObjectId is string ticket)
        {
            facts.Add(new GameBoardFact(new TicketConsumedEvent(actor.Key, ticket)));
        }

        facts.Add(new GameBoardFact(new ActorTravelStartedEvent(
            actor.Key,
            selected.ExitId,
            selected.Objective.DestinationPlaceId.Value)));
        facts.AddRange(accepted.Facts.Select(fact => new SpatialBoardFact(fact)));
        return facts.AsReadOnly();
    }

    private static IReadOnlyList<FirstBoardFact> ResolveWait(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        ModelTime now)
    {
        if (!world.IsReadyForDecision(actor))
        {
            return Reject(actor, intent, "actor is not idle at a place");
        }

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

        return GameResult(new ActorWaitStartedEvent(actor.Key, completeAt));
    }

    private static IReadOnlyList<FirstBoardFact> ResolveTalk(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardActor? target = world.Actors.SingleOrDefault(current => current.Key == intent.TargetActorId);
        if (target is null || target.Id == actor.Id)
        {
            return Reject(actor, intent, "target actor does not exist");
        }

        if (!world.AreCoLocated(actor.Key, target.Key))
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

        return GameResult(new ActorSpokeEvent(
            actor.Key,
            target.Key,
            text,
            sharedFactKind));
    }

    private static IReadOnlyList<FirstBoardFact> ResolveObserve(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        if (!world.TryGetPlace(actor.Key, out PlaceId actorPlace))
        {
            return Reject(actor, intent, "actor is not present at a place");
        }

        if (intent.TargetObjectId is string targetObjectId)
        {
            return ResolveInspectObject(world, actor, intent, targetObjectId, actorPlace);
        }

        var facts = new List<BoardFact>();
        foreach (BoardActor visibleActor in world.Actors.Where(other =>
                     other.Id != actor.Id &&
                     world.AreCoLocated(actor.Key, other.Key)))
        {
            facts.Add(new BoardFact(
                "actor.present",
                visibleActor.Key,
                $"{visibleActor.Key} is present at {actorPlace.Value}."));
        }

        foreach (BoardObject visibleObject in VisiblePortableObjects(world, actor, actorPlace))
        {
            facts.Add(new BoardFact(
                "object.visible",
                visibleObject.Key,
                $"{visibleObject.Key} is visible at {actorPlace.Value}."));
            if (visibleObject.Key == BoardIds.BrassKey)
            {
                facts.Add(new BoardFact(
                    BoardIds.KeyLocationKnown,
                    BoardIds.BrassKey,
                    "The brass key's location is known."));
            }
        }

        if (world.IsAtPlace(BoardIds.LockedChest, actorPlace))
        {
            facts.Add(new BoardFact(
                "cellar.locked-chest-visible",
                BoardIds.LockedChest,
                world.ChestOpened
                    ? "An opened chest is visible here."
                    : "A locked chest is visible here."));
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
        return GameResult(new ActorObservedEvent(actor.Key, Array.AsReadOnly(learned)));
    }

    private static IReadOnlyList<FirstBoardFact> ResolveInspectObject(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent,
        string targetObjectId,
        PlaceId actorPlace)
    {
        if (targetObjectId == BoardIds.LockedChest)
        {
            if (!world.IsAtPlace(BoardIds.LockedChest, actorPlace))
            {
                return Reject(actor, intent, "target object is not available here");
            }

            return GameResult(new ActorObservedEvent(
                actor.Key,
                [new BoardFact(
                    BoardIds.ObjectInspected,
                    BoardIds.LockedChest,
                    world.ChestOpened
                        ? "The opened chest is empty."
                        : "The chest is locked and bears the brass-key mark.")],
                BoardIds.LockedChest));
        }

        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == targetObjectId);
        if (item is null)
        {
            return Reject(actor, intent, "target object does not exist");
        }

        bool heldByActor = item.OwnerActorId == actor.Id;
        bool publicHere = item.OwnerActorId is null &&
            world.IsAtPlace(item.Key, actorPlace);
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
                    : $"You carefully inspected public {item.Key} at {actorPlace.Value}."),
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
                "The letter orders its bearer to deliver evidence of the cellar conspiracy to the city archivist."));
        }

        BoardFact[] learned =
        [
            .. facts
                .OrderBy(fact => fact.Kind, StringComparer.Ordinal)
                .ThenBy(fact => fact.RelatedId, StringComparer.Ordinal),
        ];
        return GameResult(new ActorObservedEvent(
            actor.Key,
            Array.AsReadOnly(learned),
            item.Key));
    }

    private static IReadOnlyList<FirstBoardFact> ResolveTake(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == intent.TargetObjectId);
        if (item is null)
        {
            return Reject(actor, intent, "target object does not exist");
        }

        if (!world.TryGetPlace(actor.Key, out PlaceId actorPlace) ||
            item.OwnerActorId is not null ||
            !world.IsAtPlace(item.Key, actorPlace))
        {
            return Reject(actor, intent, "target object is not available here");
        }

        var planner = new SpatialPlanner(instance.Graph);
        SpatialPlanResult removal = planner.TryRemoveEntity(
            world.Spatial,
            new EntityId(item.Key));
        if (removal is not SpatialPlanAccepted accepted)
        {
            throw new InvalidOperationException(
                $"A visible loose object could not be removed: {((SpatialPlanRejected)removal).Reason}");
        }

        return
        [
            new GameBoardFact(new ObjectTakenEvent(actor.Key, item.Key)),
            .. accepted.Facts.Select(fact => new SpatialBoardFact(fact)),
        ];
    }

    private static IReadOnlyList<FirstBoardFact> ResolvePut(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        BoardObject? item = world.Objects.SingleOrDefault(current => current.Key == intent.TargetObjectId);
        if (item is null || item.OwnerActorId != actor.Id)
        {
            return Reject(actor, intent, "actor does not hold the target object");
        }

        if (!world.TryGetPlace(actor.Key, out PlaceId actorPlace))
        {
            return Reject(actor, intent, "actor is not present at a place");
        }

        var planner = new SpatialPlanner(instance.Graph);
        SpatialPlanResult placement = planner.TryPlaceEntity(
            world.Spatial,
            new EntityId(item.Key),
            actorPlace);
        if (placement is not SpatialPlanAccepted accepted)
        {
            throw new InvalidOperationException(
                $"A held object could not be placed: {((SpatialPlanRejected)placement).Reason}");
        }

        return
        [
            new GameBoardFact(new ObjectPlacedEvent(actor.Key, item.Key, actorPlace.Value)),
            .. accepted.Facts.Select(fact => new SpatialBoardFact(fact)),
        ];
    }

    private static IReadOnlyList<FirstBoardFact> ResolveGive(
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

        if (!world.AreCoLocated(actor.Key, target.Key))
        {
            return Reject(actor, intent, "target actor is not at the same place");
        }

        return GameResult(new ObjectGivenEvent(actor.Key, target.Key, item.Key));
    }

    private static IReadOnlyList<FirstBoardFact> ResolveShow(
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

        if (!world.AreCoLocated(actor.Key, target.Key))
        {
            return Reject(actor, intent, "target actor is not at the same place");
        }

        return GameResult(new ObjectShownEvent(actor.Key, target.Key, item.Key));
    }

    private static IReadOnlyList<FirstBoardFact> ResolveUse(
        FirstBoardWorld world,
        BoardActor actor,
        Intent intent)
    {
        if (intent.TargetObjectId != BoardIds.LockedChest)
        {
            return Reject(actor, intent, "target object cannot be used here");
        }

        if (!world.AreCoLocated(actor.Key, BoardIds.LockedChest))
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

        return GameResult(new ChestOpenedEvent(
            actor.Key,
            BoardIds.LockedChest,
            BoardIds.BrassKey));
    }

    private static IEnumerable<BoardObject> VisiblePortableObjects(
        FirstBoardWorld world,
        BoardActor observer,
        PlaceId observerPlace) =>
        world.Objects.Where(item =>
            item.OwnerActorId == observer.Id ||
            (item.OwnerActorId is null && world.IsAtPlace(item.Key, observerPlace)));

    private static bool ActorOwns(FirstBoardWorld world, BoardActor actor, string objectId) =>
        world.Objects.SingleOrDefault(item => item.Key == objectId)?.OwnerActorId == actor.Id;

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

    private static IReadOnlyList<FirstBoardFact> Reject(
        BoardActor actor,
        Intent intent,
        string reason) =>
        GameResult(new ActionRejectedEvent(actor.Key, intent, reason));

    private static IReadOnlyList<FirstBoardFact> GameResult(BoardEventPayload payload) =>
        [new GameBoardFact(payload)];
}

public sealed class TravelGoalRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
{
    private readonly ScenarioInstance _instance;
    private readonly IPlayerSpatialKnowledgeGetter<FirstBoardWorld> _spatialKnowledgeGetter;

    public TravelGoalRule(
        ScenarioInstance instance,
        IPlayerSpatialKnowledgeGetter<FirstBoardWorld> spatialKnowledgeGetter)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(spatialKnowledgeGetter);
        _instance = instance;
        _spatialKnowledgeGetter = spatialKnowledgeGetter;
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules)
    {
        ArgumentNullException.ThrowIfNull(world);
        var candidates = new List<OccurrenceCandidate<BoardCandidate>>();
        foreach (BoardActor actor in world.Actors.OrderBy(value => value.Id))
        {
            if (world.IsPendingEncounterParticipant(actor) ||
                actor.Activity is not null ||
                actor.TravelGoalPlaceId is not PlaceId destination ||
                !world.Spatial.TryGetEntity(new EntityId(actor.Key), out SpatialEntity? entity) ||
                entity!.Location is not AtPlaceLocation atPlace)
            {
                continue;
            }

            var data = new TravelGoalCandidate(
                actor.Id,
                actor.Generation,
                entity.MovementGeneration,
                atPlace.PlaceId,
                destination);
            candidates.Add(new OccurrenceCandidate<BoardCandidate>(
                CreateCandidateKey(actor.Key, data),
                new CandidateDue(world.Now),
                data));
        }

        return candidates.AsReadOnly();
    }

    public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (winner.Data is not TravelGoalCandidate goal)
        {
            throw new InvalidOperationException(
                "The TravelTo goal rule received another rule's candidate.");
        }

        BoardActor actor = world.Actor(goal.ActorId);
        if (world.IsPendingEncounterParticipant(actor) ||
            actor.Activity is not null ||
            actor.TravelGoalPlaceId != goal.DestinationPlaceId ||
            actor.Generation != goal.ActorGeneration ||
            !world.Spatial.TryGetEntity(new EntityId(actor.Key), out SpatialEntity? entity) ||
            entity!.MovementGeneration != goal.SpatialMovementGeneration ||
            entity.Location is not AtPlaceLocation atPlace ||
            atPlace.PlaceId != goal.CurrentPlaceId ||
            winner.Due.ModelTime != world.Now ||
            winner.Key != CreateCandidateKey(actor.Key, goal))
        {
            throw new InvalidOperationException(
                "The selected TravelTo goal candidate is stale for its actor.");
        }

        PlayerSpatialKnowledgeSnapshot knowledge = _spatialKnowledgeGetter.GetKnownGraph(
            world,
            actor.Key,
            _instance.Graph) ?? throw new InvalidOperationException(
                "A Player spatial knowledge Getter returned null.");
        if (goal.CurrentPlaceId == goal.DestinationPlaceId)
        {
            return ValueTask.FromResult(Resolve(
                actor,
                goal.DestinationPlaceId,
                TravelGoalResolution.Completed));
        }

        FirstBoardTravelGoalPlan plan = FirstBoardTravelGoalPlanner.PlanNextLeg(
            _instance,
            world,
            actor,
            goal.DestinationPlaceId,
            knowledge);
        return plan.Route switch
        {
            RouteFound when plan.FirstExit is not null =>
                ValueTask.FromResult(new TransitionDraft<FirstBoardFact>(
                    FirstBoardActionPlanner.StartTravelGoalLeg(
                        _instance,
                        world,
                        actor,
                        plan.FirstExit,
                        world.Now))),
            NoRoute or CostOverflow =>
                ValueTask.FromResult(Resolve(
                    actor,
                    goal.DestinationPlaceId,
                    TravelGoalResolution.Blocked)),
            AlreadyAtGoal => throw new InvalidOperationException(
                "TravelTo completion must be handled before route planning."),
            UnknownStart or UnknownGoal or InvalidSpeed => throw new InvalidOperationException(
                $"TravelTo planning violated a scenario invariant: {plan.Route.GetType().Name}."),
            _ => throw new InvalidOperationException(
                $"Unknown TravelTo route result '{plan.Route.GetType().Name}'."),
        };
    }

    private static TransitionDraft<FirstBoardFact> Resolve(
        BoardActor actor,
        PlaceId destination,
        TravelGoalResolution resolution) =>
        new(
        [
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                actor.Key,
                destination,
                resolution)),
        ]);

    private static CandidateKey CreateCandidateKey(
        string actorKey,
        TravelGoalCandidate goal)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("firstboard/travel-goal");
            writer.WriteStringValue(actorKey);
            writer.WriteNumberValue(goal.ActorGeneration);
            writer.WriteNumberValue(goal.SpatialMovementGeneration);
            writer.WriteStringValue(goal.CurrentPlaceId.Value);
            writer.WriteStringValue(goal.DestinationPlaceId.Value);
            writer.WriteEndArray();
        }

        return CandidateKey.FromBytes(stream.ToArray());
    }
}

public sealed class ActivityCompletionRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
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
                        $"firstboard/wait/{actor.Key}/{actor.Generation}"),
                    new CandidateDue(actor.Activity!.Due),
                    new ActivityCandidate(actor.Id, actor.Generation))),
        ];

    public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
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
        if (actor.Activity is not BoardWaitActivity current ||
            actor.Generation != activity.Generation ||
            current.Due != winner.Due.ModelTime)
        {
            throw new InvalidOperationException("The wait candidate is stale for its actor.");
        }

        return ValueTask.FromResult(new TransitionDraft<FirstBoardFact>(
            [new GameBoardFact(new ActorWaitedEvent(actor.Key))]));
    }
}

public sealed class CellarDeadlineRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
{
    private readonly long _deadlineMs;
    private readonly SpatialPlanner _spatialPlanner;

    public CellarDeadlineRule(
        GraphDefinition definition,
        long deadlineMs = BoardTiming.DeadlineTicks)
    {
        if (deadlineMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deadlineMs));
        }

        _deadlineMs = deadlineMs;
        _spatialPlanner = new SpatialPlanner(definition);
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules) =>
        world.CellarSealed
            ? []
            :
            [
                new(
                    CandidateKey.FromUtf8("firstboard/deadline/cellar-seal"),
                    new CandidateDue(new ModelTime(_deadlineMs)),
                    new DeadlineCandidate()),
            ];

    public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (winner.Data is not DeadlineCandidate ||
            world.CellarSealed ||
            winner.Due.ModelTime != new ModelTime(_deadlineMs))
        {
            throw new InvalidOperationException("The cellar deadline candidate is stale.");
        }

        SpatialPlanResult access = _spatialPlanner.TrySetPassageEntryAccess(
            world.Spatial,
            new PassageId(BoardIds.CellarGatePassage),
            new PassageEntryPatch(enterableFromA: false, enterableFromB: null));
        if (access is not SpatialPlanAccepted accepted)
        {
            throw new InvalidOperationException(
                $"The cellar gate could not be closed: {((SpatialPlanRejected)access).Reason}");
        }

        FirstBoardFact[] facts =
        [
            new GameBoardFact(new CellarSealedEvent()),
            .. accepted.Facts.Select(fact => new SpatialBoardFact(fact)),
        ];
        return ValueTask.FromResult(new TransitionDraft<FirstBoardFact>(facts));
    }
}

/// <summary>Wraps pure Spatial mutation and arrival occurrences in the product Host union.</summary>
public sealed class SpatialHostOccurrenceRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
{
    private readonly SpatialOccurrenceRule _inner;

    public SpatialHostOccurrenceRule(GraphDefinition definition)
    {
        _inner = new SpatialOccurrenceRule(definition);
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules) =>
        [
            .. _inner.Forecast(world.Spatial, rules)
                .Select(candidate => new OccurrenceCandidate<BoardCandidate>(
                    candidate.Key,
                    candidate.Due,
                    new SpatialBoardCandidate(candidate.Data))),
        ];

    public async ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        if (winner.Data is not SpatialBoardCandidate spatial)
        {
            throw new InvalidOperationException("The Spatial Host rule received another rule's candidate.");
        }

        var innerWinner = new OccurrenceCandidate<SpatialOccurrenceData>(
            winner.Key,
            winner.Due,
            spatial.Value);
        TransitionDraft<GraphSpatialFact> innerDraft = await _inner.PlanSelectedAsync(
            world.Spatial,
            innerWinner,
            cancellationToken);
        return new TransitionDraft<FirstBoardFact>(
            innerDraft.Facts.Select(fact => new SpatialBoardFact(fact)));
    }
}

/// <summary>
/// Lifts one objective Spatial contact into an atomic FirstBoard encounter opening.
/// </summary>
public sealed class FirstBoardPassageEncounterRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
{
    private readonly SpatialContactOccurrenceRule _inner;
    private readonly IReadOnlySet<string> _driverActorIds;

    public FirstBoardPassageEncounterRule(
        GraphDefinition definition,
        IReadOnlyDictionary<string, IPlayerDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(drivers);
        _inner = new SpatialContactOccurrenceRule(definition);
        _driverActorIds = drivers.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        if (world.Game.PendingEncounter is not null)
        {
            return [];
        }

        return Array.AsReadOnly(
        [
            .. _inner.Forecast(world.Spatial, rules)
                .Where(candidate => IsEligible(world, candidate.Data.ContactKey))
                .Select(candidate => new OccurrenceCandidate<BoardCandidate>(
                    candidate.Key,
                    candidate.Due,
                    new PassageEncounterOpeningCandidate(candidate.Data))),
        ]);
    }

    public async ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(winner);
        if (winner.Data is not PassageEncounterOpeningCandidate opening ||
            opening.Value is null)
        {
            throw new InvalidOperationException(
                "The passage encounter opening rule received another rule's candidate.");
        }

        if (world.Game.PendingEncounter is not null ||
            !IsEligible(world, opening.Value.ContactKey))
        {
            throw new InvalidOperationException(
                "The selected passage encounter opening is no longer eligible.");
        }

        var innerWinner = new OccurrenceCandidate<PassageContactOccurrenceData>(
            winner.Key,
            winner.Due,
            opening.Value);
        TransitionDraft<GraphSpatialFact> innerDraft = await _inner.PlanSelectedAsync(
            world.Spatial,
            innerWinner,
            cancellationToken);
        FirstBoardFact[] facts =
        [
            .. innerDraft.Facts.Select(fact => new SpatialBoardFact(fact)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                opening.Value.ContactKey,
                opening.Value.Kind)),
        ];
        return new TransitionDraft<FirstBoardFact>(facts);
    }

    private bool IsEligible(FirstBoardWorld world, PassageContactKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        bool entityAIsActor = world.Actors.Any(actor =>
            StringComparer.Ordinal.Equals(actor.Key, key.EntityA.Value));
        bool entityBIsActor = world.Actors.Any(actor =>
            StringComparer.Ordinal.Equals(actor.Key, key.EntityB.Value));
        return entityAIsActor &&
            entityBIsActor &&
            (_driverActorIds.Contains(key.EntityA.Value) ||
                _driverActorIds.Contains(key.EntityB.Value));
    }
}

/// <summary>Requests one Player response or clears a passage encounter invalidated by world change.</summary>
public sealed class FirstBoardPassageEncounterResponseRule :
    IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
{
    private readonly IReadOnlyDictionary<string, IPlayerDriver> _drivers;
    private readonly GraphDefinition _definition;
    private readonly SpatialPlanner _spatialPlanner;

    public FirstBoardPassageEncounterResponseRule(
        GraphDefinition definition,
        IReadOnlyDictionary<string, IPlayerDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(drivers);
        _drivers = new Dictionary<string, IPlayerDriver>(drivers, StringComparer.Ordinal);
        _definition = definition;
        _spatialPlanner = new SpatialPlanner(definition);
    }

    public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
        FirstBoardWorld world,
        SimulationRules rules)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        PendingPassageEncounter? pending = world.Game.PendingEncounter;
        if (pending is null)
        {
            return [];
        }

        if (!IsCurrent(world, pending.ContactKey))
        {
            return
            [
                new OccurrenceCandidate<BoardCandidate>(
                    CreateCleanupKey(pending.ContactKey),
                    new CandidateDue(world.Now),
                    new PassageEncounterCleanupCandidate(pending.ContactKey)),
            ];
        }

        BoardActor[] responders =
        [
            .. world.Actors
                .Where(actor => IsParticipant(pending.ContactKey, actor.Key))
                .Where(actor => _drivers.ContainsKey(actor.Key))
                .OrderBy(actor => actor.Id),
        ];
        if (responders.Length == 0)
        {
            throw new InvalidOperationException(
                "A current passage encounter has no registered Player responder.");
        }

        return Array.AsReadOnly(
        [
            .. responders.Select(actor =>
            {
                long nextDecisionSequence = checked(actor.DecisionSequence + 1);
                var data = new PassageEncounterResponseCandidate(
                    pending.ContactKey,
                    actor.Key,
                    actor.Generation,
                    nextDecisionSequence);
                return new OccurrenceCandidate<BoardCandidate>(
                    CreateResponseKey(data),
                    new CandidateDue(world.Now),
                    data);
            }),
        ]);
    }

    public async ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(winner);
        cancellationToken.ThrowIfCancellationRequested();
        return winner.Data switch
        {
            PassageEncounterCleanupCandidate cleanup =>
                PlanCleanup(world, winner, cleanup),
            PassageEncounterResponseCandidate response =>
                await PlanResponseAsync(world, winner, response, cancellationToken),
            _ => throw new InvalidOperationException(
                "The passage encounter response rule received another rule's candidate."),
        };
    }

    private static TransitionDraft<FirstBoardFact> PlanCleanup(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        PassageEncounterCleanupCandidate cleanup)
    {
        PendingPassageEncounter? pending = world.Game.PendingEncounter;
        if (cleanup.ContactKey is null ||
            pending is null ||
            pending.ContactKey != cleanup.ContactKey ||
            IsCurrent(world, cleanup.ContactKey) ||
            winner.Due.ModelTime != world.Now ||
            winner.Key != CreateCleanupKey(cleanup.ContactKey))
        {
            throw new InvalidOperationException(
                "The selected passage encounter cleanup is stale.");
        }

        return new TransitionDraft<FirstBoardFact>(
        [
            new GameBoardFact(new PassageEncounterResolvedEvent(
                cleanup.ContactKey,
                RespondingActorId: null,
                PassageEncounterResolution.WorldChanged)),
        ]);
    }

    private async ValueTask<TransitionDraft<FirstBoardFact>> PlanResponseAsync(
        FirstBoardWorld world,
        OccurrenceCandidate<BoardCandidate> winner,
        PassageEncounterResponseCandidate response,
        CancellationToken cancellationToken)
    {
        PendingPassageEncounter? pending = world.Game.PendingEncounter;
        if (response.ContactKey is null ||
            pending is null ||
            pending.ContactKey != response.ContactKey ||
            !IsCurrent(world, response.ContactKey) ||
            !IsParticipant(response.ContactKey, response.RespondingActorId) ||
            winner.Due.ModelTime != world.Now)
        {
            throw new InvalidOperationException(
                "The selected passage encounter response is stale.");
        }

        BoardActor actor = world.Actor(response.RespondingActorId);
        long nextDecisionSequence = checked(actor.DecisionSequence + 1);
        if (actor.Generation != response.ActorGeneration ||
            nextDecisionSequence != response.NextDecisionSequence ||
            winner.Key != CreateResponseKey(response) ||
            !_drivers.TryGetValue(actor.Key, out IPlayerDriver? driver))
        {
            throw new InvalidOperationException(
                "The selected passage encounter response does not match its responder.");
        }

        SpatialPlanResult reversePlan = _spatialPlanner.TryReverseTraversal(
            world.Spatial,
            new EntityId(actor.Key),
            world.Now);
        SpatialPlanAccepted? acceptedReverse = reversePlan as SpatialPlanAccepted;
        DecisionRequest request = FirstBoardScenario.BuildPassageEncounterRequest(
            _definition,
            world,
            actor,
            pending,
            world.Now,
            canReverse: acceptedReverse is not null);
        PlayerDecision decision = await driver.DecideAsync(request, cancellationToken)
            ?? throw new InvalidOperationException("A Player driver returned null.");
        PlayerDecisionValidationResult validation = PlayerDecisionValidator.Validate(decision, request);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        if (decision.Intent.ActionKind == ActionKinds.ContinueTravel)
        {
            return new TransitionDraft<FirstBoardFact>(
            [
                new GameBoardFact(new PassageEncounterResolvedEvent(
                    response.ContactKey,
                    actor.Key,
                    PassageEncounterResolution.Continued)),
            ]);
        }

        if (decision.Intent.ActionKind != ActionKinds.ReverseTravel || acceptedReverse is null)
        {
            throw new InvalidOperationException(
                "The validated passage encounter response is not supported by the Host.");
        }

        FirstBoardFact[] facts =
        [
            new GameBoardFact(new PassageEncounterResolvedEvent(
                response.ContactKey,
                actor.Key,
                PassageEncounterResolution.Reversed)),
            .. acceptedReverse.Facts.Select(fact => new SpatialBoardFact(fact)),
        ];
        return new TransitionDraft<FirstBoardFact>(facts);
    }

    private static bool IsCurrent(FirstBoardWorld world, PassageContactKey key) =>
        world.Spatial.ConsumedContacts.Contains(key);

    private static bool IsParticipant(PassageContactKey key, string actorId) =>
        key.EntityA == new EntityId(actorId) || key.EntityB == new EntityId(actorId);

    private static CandidateKey CreateResponseKey(PassageEncounterResponseCandidate response)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("firstboard/passage-encounter-response");
            WriteContactKey(writer, response.ContactKey);
            writer.WriteStringValue(response.RespondingActorId);
            writer.WriteNumberValue(response.ActorGeneration);
            writer.WriteNumberValue(response.NextDecisionSequence);
            writer.WriteEndArray();
        }

        return CandidateKey.FromBytes(stream.ToArray());
    }

    private static CandidateKey CreateCleanupKey(PassageContactKey contactKey)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("firstboard/passage-encounter-response");
            WriteContactKey(writer, contactKey);
            writer.WriteStringValue("world-changed");
            writer.WriteEndArray();
        }

        return CandidateKey.FromBytes(stream.ToArray());
    }

    private static void WriteContactKey(Utf8JsonWriter writer, PassageContactKey contactKey)
    {
        ArgumentNullException.ThrowIfNull(contactKey);
        writer.WriteStringValue(contactKey.PassageId.Value);
        writer.WriteStringValue(contactKey.EntityA.Value);
        writer.WriteNumberValue(contactKey.MovementGenerationA);
        writer.WriteStringValue(contactKey.EntityB.Value);
        writer.WriteNumberValue(contactKey.MovementGenerationB);
    }
}

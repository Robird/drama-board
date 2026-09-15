using DramaBoard.Kernel.Journal;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Demo.Live;

/// <summary>
/// Projects one already-committed FirstBoard batch without reading Authority state or changing replay state.
/// </summary>
internal sealed class FirstBoardPresentationProjector
{
    internal static IReadOnlyList<Type> KnownGamePayloadTypes { get; } =
        Array.AsReadOnly<Type>(
        [
            typeof(ActorTravelStartedEvent),
            typeof(ActorTravelGoalSetEvent),
            typeof(ActorTravelGoalResolvedEvent),
            typeof(PassageEncounterOpenedEvent),
            typeof(PassageEncounterResolvedEvent),
            typeof(TicketConsumedEvent),
            typeof(ActorWaitStartedEvent),
            typeof(ActorWaitedEvent),
            typeof(ActorSpokeEvent),
            typeof(ActorObservedEvent),
            typeof(ObjectTakenEvent),
            typeof(ObjectPlacedEvent),
            typeof(ObjectGivenEvent),
            typeof(ObjectShownEvent),
            typeof(ChestOpenedEvent),
            typeof(ActionRejectedEvent),
            typeof(CellarSealedEvent),
        ]);

    internal static IReadOnlyList<Type> KnownSpatialPayloadTypes { get; } =
        Array.AsReadOnly<Type>(
        [
            typeof(EntityPlacedFact),
            typeof(EntityRemovedFact),
            typeof(TraversalStartedFact),
            typeof(TraversalReversedFact),
            typeof(PassageContactOccurredFact),
            typeof(TraversalArrivedFact),
            typeof(PassageEntryAccessChangedFact),
            typeof(PassageEntryChangeScheduledFact),
            typeof(ScheduledPassageEntryChangeAppliedFact),
        ]);

    private readonly ScenarioInstance _scenario;
    private readonly string? _humanActorId;
    private readonly SpatialQueries _spatialQueries;

    public FirstBoardPresentationProjector(
        ScenarioInstance scenario,
        string? humanActorId)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (humanActorId is not null &&
            !scenario.Definition.Actors.Any(actor =>
                StringComparer.Ordinal.Equals(actor.Id, humanActorId)))
        {
            throw new ArgumentException(
                $"Human actor '{humanActorId}' is not defined by the scenario.",
                nameof(humanActorId));
        }

        _scenario = scenario;
        _humanActorId = humanActorId;
        _spatialQueries = new SpatialQueries(scenario.Graph);
    }

    public FirstBoardProjection Project(
        FirstBoardWorld preWorld,
        CommittedTransition transition,
        FirstBoardWorld postWorld)
    {
        ArgumentNullException.ThrowIfNull(preWorld);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(postWorld);
        if (preWorld.WorldSeed != _scenario.WorldSeed ||
            postWorld.WorldSeed != _scenario.WorldSeed)
        {
            throw new ArgumentException(
                "Presentation worlds must belong to the projector's scenario instance.");
        }

        JournalBatch<FirstBoardFact> batch = transition.Batch;
        var overlays = new List<DeveloperOverlay>
        {
            new(
                "developer.batch",
                $"version={transition.Version.LineageId}/{transition.Version.TransitionCount} " +
                $"instant={batch.Instant.ModelTime.Ticks}/{batch.Instant.CausalOrdinal} " +
                $"cause={batch.CauseKey} facts={batch.Facts.Count}"),
        };
        for (int index = 0; index < batch.Facts.Count; index++)
        {
            FirstBoardFact fact = batch.Facts[index];
            overlays.Add(new DeveloperOverlay(
                "developer.fact",
                $"index={index} name={FactCode(fact)} {DeveloperSummary(fact)}"));
        }

        if (_humanActorId is null)
        {
            return new FirstBoardProjection([], overlays);
        }

        var cues = new List<PresentationCue>();
        var handledTraversalStarts = batch.Facts
            .OfType<GameBoardFact>()
            .Select(fact => fact.Value)
            .Select(payload => payload switch
            {
                ActorTravelStartedEvent value => value.ActorId,
                ActorTravelGoalSetEvent value => value.ActorId,
                _ => null,
            })
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var handledRemovedEntities = batch.Facts
            .OfType<GameBoardFact>()
            .Select(fact => fact.Value)
            .OfType<ObjectTakenEvent>()
            .Select(value => value.ObjectId)
            .ToHashSet(StringComparer.Ordinal);
        var handledPlacedEntities = batch.Facts
            .OfType<GameBoardFact>()
            .Select(fact => fact.Value)
            .OfType<ObjectPlacedEvent>()
            .Select(value => value.ObjectId)
            .ToHashSet(StringComparer.Ordinal);
        var handledContacts = batch.Facts
            .OfType<GameBoardFact>()
            .Select(fact => fact.Value)
            .OfType<PassageEncounterOpenedEvent>()
            .Select(value => value.ContactKey)
            .ToHashSet();
        var handledReversals = batch.Facts
            .OfType<GameBoardFact>()
            .Select(fact => fact.Value)
            .OfType<PassageEncounterResolvedEvent>()
            .Where(value => value.Resolution == PassageEncounterResolution.Reversed)
            .Select(value => value.RespondingActorId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        bool cellarSealHandled = batch.Facts
            .OfType<GameBoardFact>()
            .Any(fact => fact.Value is CellarSealedEvent);

        foreach (FirstBoardFact fact in batch.Facts)
        {
            switch (fact)
            {
                case GameBoardFact game:
                    ProjectGameFact(preWorld, postWorld, game.Value, cues);
                    break;
                case SpatialBoardFact spatial:
                    ProjectSpatialFact(
                        preWorld,
                        postWorld,
                        spatial.Value,
                        handledTraversalStarts,
                        handledRemovedEntities,
                        handledPlacedEntities,
                        handledContacts,
                        handledReversals,
                        cellarSealHandled,
                        cues);
                    break;
                default:
                    // Player-safe fallback: a new unclassified fact is invisible until explicitly reviewed.
                    break;
            }
        }

        AddHumanKnowledgeCues(preWorld, postWorld, cues);
        return new FirstBoardProjection(cues, overlays);
    }

    private void ProjectGameFact(
        FirstBoardWorld preWorld,
        FirstBoardWorld postWorld,
        BoardEventPayload payload,
        ICollection<PresentationCue> cues)
    {
        switch (payload)
        {
            case ActorTravelStartedEvent value when
                IsHuman(value.ActorId) || IsCoLocated(preWorld, value.ActorId):
                cues.Add(new PresentationCue(
                    "actor.travel-started",
                    $"{value.ActorId} entered {value.ExitId} toward {value.DestinationId}."));
                break;
            case ActorTravelGoalSetEvent value when IsHuman(value.ActorId):
                cues.Add(new PresentationCue(
                    "actor.travel-goal-set",
                    DescribeGoalStart(postWorld, value)));
                break;
            case ActorTravelGoalResolvedEvent value when IsHuman(value.ActorId):
                cues.Add(new PresentationCue(
                    "actor.travel-goal-resolved",
                    $"Your travel goal {value.DestinationPlaceId.Value} ended as {value.Resolution}."));
                break;
            case PassageEncounterOpenedEvent value when IsParticipant(value.ContactKey):
                cues.Add(new PresentationCue(
                    "passage-encounter.opened",
                    $"{value.ContactKey.EntityA.Value} and {value.ContactKey.EntityB.Value} " +
                    $"encountered each other in {value.ContactKey.PassageId.Value}: {value.Kind}."));
                break;
            case PassageEncounterResolvedEvent value when IsParticipant(value.ContactKey):
                cues.Add(new PresentationCue(
                    "passage-encounter.resolved",
                    DescribeEncounterResolution(postWorld, value)));
                break;
            case TicketConsumedEvent value when IsHuman(value.ActorId):
                cues.Add(new PresentationCue(
                    "ticket.consumed",
                    $"You consumed {value.TicketObjectId} as passage fare."));
                break;
            case ActorWaitStartedEvent value when
                IsHuman(value.ActorId) || IsCoLocated(preWorld, value.ActorId):
                cues.Add(new PresentationCue(
                    "actor.wait-started",
                    $"{value.ActorId} started waiting until model time {value.CompleteAt.Ticks}."));
                break;
            case ActorWaitedEvent value when
                IsHuman(value.ActorId) || IsCoLocated(postWorld, value.ActorId):
                cues.Add(new PresentationCue(
                    "actor.waited",
                    $"{value.ActorId} finished waiting."));
                break;
            case ActorSpokeEvent value when
                IsHuman(value.ActorId) || IsHuman(value.TargetActorId):
                cues.Add(new PresentationCue(
                    "actor.spoke",
                    $"{value.ActorId} said to {value.TargetActorId}: {value.Text}"));
                break;
            case ActorObservedEvent value when IsHuman(value.ActorId):
                cues.Add(new PresentationCue(
                    "actor.observed",
                    value.LearnedFacts.Count == 0
                        ? "You observed the current context and learned nothing new."
                        : "You observed: " + string.Join(
                            "; ",
                            value.LearnedFacts.Select(fact => fact.Text))));
                break;
            case ObjectTakenEvent value when
                IsHuman(value.ActorId) || IsLooseEntityVisible(preWorld, value.ObjectId):
                cues.Add(new PresentationCue(
                    "object.taken",
                    $"{value.ActorId} took {value.ObjectId}."));
                break;
            case ObjectPlacedEvent value when
                IsHuman(value.ActorId) || IsHumanAt(postWorld, new PlaceId(value.PlaceId)):
                cues.Add(new PresentationCue(
                    "object.placed",
                    $"{value.ActorId} placed {value.ObjectId} at {value.PlaceId}."));
                break;
            case ObjectGivenEvent value when
                IsHuman(value.ActorId) || IsHuman(value.TargetActorId):
                cues.Add(new PresentationCue(
                    "object.given",
                    $"{value.ActorId} gave {value.ObjectId} to {value.TargetActorId}."));
                break;
            case ObjectShownEvent value when
                IsHuman(value.ActorId) || IsHuman(value.TargetActorId):
                cues.Add(new PresentationCue(
                    "object.shown",
                    $"{value.ActorId} showed {value.ObjectId} to {value.TargetActorId}."));
                break;
            case ChestOpenedEvent value when
                IsHuman(value.ActorId) ||
                IsCoLocated(preWorld, value.ActorId) ||
                IsCoLocated(preWorld, value.ObjectId):
                cues.Add(new PresentationCue(
                    "chest.opened",
                    $"{value.ActorId} used {value.KeyObjectId} to open {value.ObjectId}."));
                break;
            case ActionRejectedEvent value when IsHuman(value.ActorId):
                cues.Add(new PresentationCue(
                    "action.rejected",
                    $"Your {value.RejectedIntent.ActionKindId} action was rejected: {value.Reason}."));
                break;
            case CellarSealedEvent when
                IsAtPassageEndpoint(preWorld, new PassageId(BoardIds.CellarGatePassage)) ||
                IsAtPassageEndpoint(postWorld, new PassageId(BoardIds.CellarGatePassage)):
                cues.Add(new PresentationCue(
                    "cellar.sealed",
                    "The cellar entrance has been sealed."));
                break;
            default:
                // Deliberately hidden. This includes every known fact whose visibility guard failed.
                break;
        }
    }

    private void ProjectSpatialFact(
        FirstBoardWorld preWorld,
        FirstBoardWorld postWorld,
        GraphSpatialFact payload,
        IReadOnlySet<string> handledTraversalStarts,
        IReadOnlySet<string> handledRemovedEntities,
        IReadOnlySet<string> handledPlacedEntities,
        IReadOnlySet<PassageContactKey> handledContacts,
        IReadOnlySet<string> handledReversals,
        bool cellarSealHandled,
        ICollection<PresentationCue> cues)
    {
        if ((payload is EntityPlacedFact placed &&
                handledPlacedEntities.Contains(placed.EntityId.Value)) ||
            (payload is EntityRemovedFact removed &&
                handledRemovedEntities.Contains(removed.EntityId.Value)) ||
            (payload is TraversalStartedFact started &&
                handledTraversalStarts.Contains(started.EntityId.Value)) ||
            (payload is PassageContactOccurredFact contact &&
                handledContacts.Contains(contact.ContactKey)) ||
            (payload is TraversalReversedFact reversed &&
                handledReversals.Contains(reversed.EntityId.Value)) ||
            (payload is PassageEntryAccessChangedFact accessChanged &&
                cellarSealHandled &&
                accessChanged.PassageId == new PassageId(BoardIds.CellarGatePassage)))
        {
            return;
        }

        switch (payload)
        {
            case EntityPlacedFact value when
                IsHuman(value.EntityId.Value) || IsHumanAt(postWorld, value.PlaceId):
                cues.Add(new PresentationCue(
                    "spatial.entity-placed",
                    $"{value.EntityId.Value} appeared at {value.PlaceId.Value}."));
                break;
            case EntityRemovedFact value when
                IsHuman(value.EntityId.Value) || IsLooseEntityVisible(preWorld, value.EntityId.Value):
                cues.Add(new PresentationCue(
                    "spatial.entity-removed",
                    $"{value.EntityId.Value} left its previous place."));
                break;
            case TraversalStartedFact value when
                IsHuman(value.EntityId.Value) || IsCoLocated(preWorld, value.EntityId.Value):
                cues.Add(new PresentationCue(
                    "spatial.traversal-started",
                    DescribeTraversal(postWorld, value.EntityId.Value, value.PassageId)));
                break;
            case TraversalReversedFact value when IsHuman(value.EntityId.Value):
                cues.Add(new PresentationCue(
                    "spatial.traversal-reversed",
                    DescribeReversal(postWorld, value.EntityId.Value)));
                break;
            case PassageContactOccurredFact value when IsParticipant(value.ContactKey):
                cues.Add(new PresentationCue(
                    "spatial.passage-contact-occurred",
                    $"{value.ContactKey.EntityA.Value} and {value.ContactKey.EntityB.Value} " +
                    $"made {value.Kind} contact in {value.ContactKey.PassageId.Value}."));
                break;
            case TraversalArrivedFact value when
                IsHuman(value.EntityId.Value) || IsCoLocated(postWorld, value.EntityId.Value):
                cues.Add(new PresentationCue(
                    "spatial.traversal-arrived",
                    DescribeArrival(postWorld, value.EntityId.Value)));
                break;
            case PassageEntryAccessChangedFact value when
                IsAtPassageEndpoint(preWorld, value.PassageId) ||
                IsAtPassageEndpoint(postWorld, value.PassageId):
                cues.Add(new PresentationCue(
                    "spatial.passage-entry-access-changed",
                    $"Entry access for {value.PassageId.Value} changed to " +
                    $"A={value.ResultAccess.EnterableFromA}, B={value.ResultAccess.EnterableFromB}."));
                break;
            case PassageEntryChangeScheduledFact:
                // A future schedule is objective debug data, not a Player-visible occurrence.
                break;
            case ScheduledPassageEntryChangeAppliedFact value when
                IsAtPassageEndpoint(preWorld, value.PassageId) ||
                IsAtPassageEndpoint(postWorld, value.PassageId):
                PassageEntryAccess access = _spatialQueries.GetPassageEntryAccess(
                    postWorld.Spatial,
                    value.PassageId);
                cues.Add(new PresentationCue(
                    "spatial.scheduled-passage-entry-change-applied",
                    $"Scheduled entry access for {value.PassageId.Value} is now " +
                    $"A={access.EnterableFromA}, B={access.EnterableFromB}."));
                break;
            default:
                // Player-safe fallback for unknown and remote Spatial facts.
                break;
        }
    }

    private void AddHumanKnowledgeCues(
        FirstBoardWorld preWorld,
        FirstBoardWorld postWorld,
        ICollection<PresentationCue> cues)
    {
        BoardActor preHuman = preWorld.Actor(_humanActorId!);
        BoardActor postHuman = postWorld.Actor(_humanActorId!);
        var previousFacts = preHuman.KnownFacts.ToHashSet();
        foreach (BoardFact fact in postHuman.KnownFacts.Where(fact =>
                     fact.Kind != BoardIds.LastActionOutcome && !previousFacts.Contains(fact)))
        {
            cues.Add(new PresentationCue("knowledge.learned", fact.Text));
        }
    }

    private string DescribeGoalStart(FirstBoardWorld postWorld, ActorTravelGoalSetEvent value) =>
        TryGetTraversal(postWorld, value.ActorId, out TraversingLocation? traversal)
            ? $"You set a travel goal for {value.DestinationPlaceId.Value} and entered " +
              $"{traversal!.PassageId.Value} toward {traversal.TargetPlaceId.Value}."
            : $"You set a travel goal for {value.DestinationPlaceId.Value}.";

    private static string DescribeEncounterResolution(
        FirstBoardWorld postWorld,
        PassageEncounterResolvedEvent value)
    {
        if (value.Resolution == PassageEncounterResolution.WorldChanged)
        {
            return "The passage encounter ended after objective movement changed.";
        }

        string responder = value.RespondingActorId ?? "unknown";
        if (value.Resolution == PassageEncounterResolution.Reversed &&
            TryGetTraversal(postWorld, responder, out TraversingLocation? traversal))
        {
            return $"{responder} reversed toward {traversal!.TargetPlaceId.Value}.";
        }

        return $"{responder} chose to continue after the passage encounter.";
    }

    private static string DescribeTraversal(
        FirstBoardWorld postWorld,
        string entityId,
        PassageId passageId) =>
        TryGetTraversal(postWorld, entityId, out TraversingLocation? traversal)
            ? $"{entityId} entered {passageId.Value} toward {traversal!.TargetPlaceId.Value}."
            : $"{entityId} entered {passageId.Value}.";

    private static string DescribeReversal(FirstBoardWorld postWorld, string entityId) =>
        TryGetTraversal(postWorld, entityId, out TraversingLocation? traversal)
            ? $"{entityId} reversed toward {traversal!.TargetPlaceId.Value}."
            : $"{entityId} reversed in its passage.";

    private static string DescribeArrival(FirstBoardWorld postWorld, string entityId) =>
        postWorld.TryGetPlace(entityId, out PlaceId placeId)
            ? $"{entityId} arrived at {placeId.Value}."
            : $"{entityId} completed a traversal.";

    private bool IsHuman(string actorId) =>
        StringComparer.Ordinal.Equals(_humanActorId, actorId);

    private bool IsParticipant(PassageContactKey key) =>
        IsHuman(key.EntityA.Value) || IsHuman(key.EntityB.Value);

    private bool IsCoLocated(FirstBoardWorld world, string entityId) =>
        IsHuman(entityId) || world.AreCoLocated(_humanActorId!, entityId);

    private bool IsLooseEntityVisible(FirstBoardWorld world, string entityId) =>
        world.TryGetPlace(entityId, out PlaceId placeId) && IsHumanAt(world, placeId);

    private bool IsHumanAt(FirstBoardWorld world, PlaceId placeId) =>
        world.IsAtPlace(_humanActorId!, placeId);

    private bool IsAtPassageEndpoint(FirstBoardWorld world, PassageId passageId)
    {
        if (!world.TryGetPlace(_humanActorId!, out PlaceId placeId))
        {
            return false;
        }

        PassageDefinition passage = _scenario.Graph.GetPassage(passageId);
        return placeId == passage.EndpointA || placeId == passage.EndpointB;
    }

    private static bool TryGetTraversal(
        FirstBoardWorld world,
        string entityId,
        out TraversingLocation? traversal)
    {
        if (world.Spatial.TryGetEntity(new EntityId(entityId), out SpatialEntity? entity) &&
            entity!.Location is TraversingLocation current)
        {
            traversal = current;
            return true;
        }

        traversal = null;
        return false;
    }

    private static string FactCode(FirstBoardFact fact) => fact switch
    {
        GameBoardFact { Value: ActorTravelStartedEvent } => "actor.travel-started",
        GameBoardFact { Value: ActorTravelGoalSetEvent } => "actor.travel-goal-set",
        GameBoardFact { Value: ActorTravelGoalResolvedEvent } => "actor.travel-goal-resolved",
        GameBoardFact { Value: PassageEncounterOpenedEvent } => "passage-encounter.opened",
        GameBoardFact { Value: PassageEncounterResolvedEvent } => "passage-encounter.resolved",
        GameBoardFact { Value: TicketConsumedEvent } => "ticket.consumed",
        GameBoardFact { Value: ActorWaitStartedEvent } => "actor.wait-started",
        GameBoardFact { Value: ActorWaitedEvent } => "actor.waited",
        GameBoardFact { Value: ActorSpokeEvent } => "actor.spoke",
        GameBoardFact { Value: ActorObservedEvent } => "actor.observed",
        GameBoardFact { Value: ObjectTakenEvent } => "object.taken",
        GameBoardFact { Value: ObjectPlacedEvent } => "object.placed",
        GameBoardFact { Value: ObjectGivenEvent } => "object.given",
        GameBoardFact { Value: ObjectShownEvent } => "object.shown",
        GameBoardFact { Value: ChestOpenedEvent } => "chest.opened",
        GameBoardFact { Value: ActionRejectedEvent } => "action.rejected",
        GameBoardFact { Value: CellarSealedEvent } => "cellar.sealed",
        SpatialBoardFact { Value: EntityPlacedFact } => "spatial.entity-placed",
        SpatialBoardFact { Value: EntityRemovedFact } => "spatial.entity-removed",
        SpatialBoardFact { Value: TraversalStartedFact } => "spatial.traversal-started",
        SpatialBoardFact { Value: TraversalReversedFact } => "spatial.traversal-reversed",
        SpatialBoardFact { Value: PassageContactOccurredFact } =>
            "spatial.passage-contact-occurred",
        SpatialBoardFact { Value: TraversalArrivedFact } => "spatial.traversal-arrived",
        SpatialBoardFact { Value: PassageEntryAccessChangedFact } =>
            "spatial.passage-entry-access-changed",
        SpatialBoardFact { Value: PassageEntryChangeScheduledFact } =>
            "spatial.passage-entry-change-scheduled",
        SpatialBoardFact { Value: ScheduledPassageEntryChangeAppliedFact } =>
            "spatial.scheduled-passage-entry-change-applied",
        _ => "unknown." + fact.GetType().Name,
    };

    private static string DeveloperSummary(FirstBoardFact fact) => fact switch
    {
        GameBoardFact game => DeveloperGameSummary(game.Value),
        SpatialBoardFact spatial => DeveloperSpatialSummary(spatial.Value),
        _ => $"type={fact.GetType().FullName}",
    };

    private static string DeveloperGameSummary(BoardEventPayload payload) => payload switch
    {
        ActorTravelStartedEvent value =>
            $"actor={value.ActorId} exit={value.ExitId} destination={value.DestinationId}",
        ActorTravelGoalSetEvent value =>
            $"actor={value.ActorId} destination={value.DestinationPlaceId.Value}",
        ActorTravelGoalResolvedEvent value =>
            $"actor={value.ActorId} destination={value.DestinationPlaceId.Value} " +
            $"resolution={value.Resolution}",
        PassageEncounterOpenedEvent value =>
            $"contact={ContactSummary(value.ContactKey)} kind={value.Kind}",
        PassageEncounterResolvedEvent value =>
            $"contact={ContactSummary(value.ContactKey)} " +
            $"responder={value.RespondingActorId ?? "-"} resolution={value.Resolution}",
        TicketConsumedEvent value =>
            $"actor={value.ActorId} ticket={value.TicketObjectId}",
        ActorWaitStartedEvent value =>
            $"actor={value.ActorId} completeAt={value.CompleteAt.Ticks}",
        ActorWaitedEvent value => $"actor={value.ActorId}",
        ActorSpokeEvent value =>
            $"actor={value.ActorId} target={value.TargetActorId} text={value.Text} " +
            $"sharedFact={value.SharedFactKind ?? "-"}",
        ActorObservedEvent value =>
            $"actor={value.ActorId} target={value.TargetObjectId ?? "-"} learned=" +
            string.Join(" | ", value.LearnedFacts.Select(fact =>
                $"{fact.Kind}@{fact.RelatedId}:{fact.Text}")),
        ObjectTakenEvent value => $"actor={value.ActorId} object={value.ObjectId}",
        ObjectPlacedEvent value =>
            $"actor={value.ActorId} object={value.ObjectId} place={value.PlaceId}",
        ObjectGivenEvent value =>
            $"actor={value.ActorId} target={value.TargetActorId} object={value.ObjectId}",
        ObjectShownEvent value =>
            $"actor={value.ActorId} target={value.TargetActorId} object={value.ObjectId}",
        ChestOpenedEvent value =>
            $"actor={value.ActorId} object={value.ObjectId} key={value.KeyObjectId}",
        ActionRejectedEvent value =>
            $"actor={value.ActorId} action={value.RejectedIntent.ActionKindId} " +
            $"reason={value.Reason}",
        CellarSealedEvent => "place=cellar",
        _ => $"type={payload.GetType().FullName}",
    };

    private static string DeveloperSpatialSummary(GraphSpatialFact payload) => payload switch
    {
        EntityPlacedFact value =>
            $"entity={value.EntityId.Value} place={value.PlaceId.Value}",
        EntityRemovedFact value => $"entity={value.EntityId.Value}",
        TraversalStartedFact value =>
            $"entity={value.EntityId.Value} passage={value.PassageId.Value} " +
            $"from={value.FromPlaceId.Value} speed={value.SpeedSnapshot}",
        TraversalReversedFact value =>
            $"entity={value.EntityId.Value} generation={value.ExpectedMovementGeneration}",
        PassageContactOccurredFact value =>
            $"contact={ContactSummary(value.ContactKey)} kind={value.Kind}",
        TraversalArrivedFact value =>
            $"entity={value.EntityId.Value} generation={value.ExpectedMovementGeneration}",
        PassageEntryAccessChangedFact value =>
            $"passage={value.PassageId.Value} a={value.ResultAccess.EnterableFromA} " +
            $"b={value.ResultAccess.EnterableFromB}",
        PassageEntryChangeScheduledFact value =>
            $"passage={value.PassageId.Value} due={value.Due.Ticks} " +
            $"a={value.Patch.EnterableFromA} b={value.Patch.EnterableFromB}",
        ScheduledPassageEntryChangeAppliedFact value =>
            $"passage={value.PassageId.Value} due={value.Due.Ticks}",
        _ => $"type={payload.GetType().FullName}",
    };

    private static string ContactSummary(PassageContactKey key) =>
        $"{key.PassageId.Value}:{key.EntityA.Value}/{key.MovementGenerationA}:" +
        $"{key.EntityB.Value}/{key.MovementGenerationB}";
}

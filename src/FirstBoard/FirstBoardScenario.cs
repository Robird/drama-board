using System.Globalization;
using System.Text;
using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

public static class FirstBoardScenario
{
    public const long LineageId = 10_001;
    private const int MaxTransitionsPerModelTime = 10_000;

    public static SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> CreateKernel(
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        ScenarioInstance instance,
        IJournalSink<FirstBoardFact> journal,
        FirstBoardWorld world,
        WorldVersion? version = null,
        LogicalInstant? lastCommittedInstant = null)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(world);
        instance.Definition.Validate();
        if (world.WorldSeed != instance.WorldSeed)
        {
            throw new ArgumentException(
                "The supplied committed world and scenario instance use different seeds.",
                nameof(world));
        }

        var reducer = new FirstBoardReducer(instance.Graph);
        reducer.Validate(world);
        return new SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact>(
            world,
            version ?? new WorldVersion(journal.LineageId, journal.Batches.Count),
            world.Now,
            lastCommittedInstant,
            new SimulationRules(instance.WorldSeed, MaxTransitionsPerModelTime),
            [
                new CellarDeadlineRule(
                    instance.Graph,
                    instance.Definition.CellarDeadlineMs),
                new ActivityCompletionRule(),
                new SpatialHostOccurrenceRule(instance.Graph),
                new DecisionPointRule(drivers, instance),
            ],
            journal,
            reducer.Apply,
            reducer.Validate);
    }

    public static Task<BoardRunCapture> RunAsync(
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        ulong worldSeed,
        ModelTime until,
        FirstBoardWorld? initialWorld = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            drivers,
            ScenarioInstance.CreateDefault(worldSeed),
            until,
            initialWorld,
            cancellationToken);

    public static async Task<BoardRunCapture> RunAsync(
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        ScenarioInstance instance,
        ModelTime until,
        FirstBoardWorld? initialWorld = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        FirstBoardWorld world = initialWorld ?? instance.CreateInitialWorld();
        if (world.WorldSeed != instance.WorldSeed)
        {
            throw new ArgumentException(
                "The supplied initial world and scenario instance use different seeds.",
                nameof(initialWorld));
        }

        var journal = new InMemoryJournal<FirstBoardFact>(LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            CreateKernel(drivers, instance, journal, world);
        HostRunResult<FirstBoardWorld> result = await SimulationHost.RunUntilAsync(
            kernel,
            until,
            cancellationToken);
        return new BoardRunCapture(world, result, journal);
    }

    public static DecisionRequest BuildRequest(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        ModelTime modelTime)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!world.IsReadyForDecision(actor) ||
            !world.TryGetPlace(actor.Key, out PlaceId placeId))
        {
            throw new InvalidOperationException(
                "Only an idle actor at a Place can receive a decision request.");
        }

        IReadOnlyList<ObservedExit> exits =
            FirstBoardSpatialProjection.ObserveExits(instance, world, actor);
        var observation = new Observation(
            actor.Key,
            placeId.Value,
            ModelTimeMs: modelTime.Ticks,
            Exits: exits,
            VisibleActorIds: VisibleActors(world, actor),
            VisibleObjectIds: VisibleObjectIds(world, actor, placeId),
            KnownFacts: ObservationFacts(world, actor));
        return new DecisionRequest(
            new DecisionId($"decision.{actor.Key}.{actor.DecisionSequence + 1}"),
            actor.Key,
            ModelTimeMs: modelTime.Ticks,
            observation,
            AvailableActions(instance, world, actor, placeId, exits));
    }

    public static string WorldSnapshot(FirstBoardWorld world)
    {
        string actors = string.Join(
            ";",
            world.Actors.OrderBy(actor => actor.Id).Select(actor => string.Join(
                "|",
                actor.Id.ToString(CultureInfo.InvariantCulture),
                actor.Key,
                actor.Generation.ToString(CultureInfo.InvariantCulture),
                actor.DecisionSequence.ToString(CultureInfo.InvariantCulture),
                actor.Activity?.Due.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-",
                string.Join(",", actor.KnownFacts.Select(fact =>
                    $"{fact.Kind}@{fact.RelatedId}")))));
        string objects = string.Join(
            ";",
            world.Objects.OrderBy(item => item.Id).Select(item => string.Join(
                "|",
                item.Id.ToString(CultureInfo.InvariantCulture),
                item.Key,
                item.OwnerActorId?.ToString(CultureInfo.InvariantCulture) ?? "-")));
        string entities = string.Join(
            ";",
            world.Spatial.Entities.Select(entity =>
                $"{entity.Id.Value}|{entity.MovementGeneration}|" +
                LocationSnapshot(entity.Location)));
        string overrides = string.Join(
            ";",
            world.Spatial.PassageEntryAccessOverrides.Select(value =>
                $"{value.PassageId.Value}:{value.Access.EnterableFromA}:" +
                value.Access.EnterableFromB));
        string schedules = string.Join(
            ";",
            world.Spatial.ScheduledPassageEntryChanges.Select(value =>
                $"{value.PassageId.Value}:{value.Due.Ticks}:" +
                $"{value.Patch.EnterableFromA}:{value.Patch.EnterableFromB}"));
        return $"seed={world.WorldSeed};now={world.Now.Ticks};sealed={world.CellarSealed};" +
            $"chestOpened={world.ChestOpened};actors={actors};objects={objects};" +
            $"entities={entities};overrides={overrides};schedules={schedules}";
    }

    public static string[] EventSnapshots(InMemoryJournal<FirstBoardFact> journal) =>
        [
            .. journal.Batches.SelectMany(batch => batch.Facts.Select((fact, index) =>
                $"{batch.Instant.ModelTime.Ticks}:{batch.Instant.CausalOrdinal} " +
                $"#{index} {FactName(fact)} {PayloadSummary(fact)}")),
        ];

    public static string FormatJournal(InMemoryJournal<FirstBoardFact> journal)
    {
        var text = new StringBuilder();
        foreach (string snapshot in EventSnapshots(journal))
        {
            text.AppendLine(snapshot);
        }

        return text.ToString();
    }

    public static string FactName(FirstBoardFact fact) => fact switch
    {
        GameBoardFact game => GameFactName(game.Value),
        SpatialBoardFact spatial => SpatialFactName(spatial.Value),
        _ => throw new InvalidOperationException("Unknown FirstBoard Host fact."),
    };

    private static IReadOnlyList<string> VisibleActors(
        FirstBoardWorld world,
        BoardActor observer) =>
        [
            .. world.Actors
                .Where(actor =>
                    actor.Id != observer.Id &&
                    world.AreCoLocated(observer.Key, actor.Key))
                .OrderBy(actor => actor.Id)
                .Select(actor => actor.Key),
        ];

    private static IReadOnlyList<KnownFact> ObservationFacts(
        FirstBoardWorld world,
        BoardActor actor) =>
        [
            .. actor.KnownFacts.Select(fact => new KnownFact(
                new FactKind(fact.Kind),
                fact.RelatedId,
                fact.Text)),
            .. world.Objects
                .Where(item => item.OwnerActorId == actor.Id)
                .OrderBy(item => item.Id)
                .Select(item => new KnownFact(
                    new FactKind(BoardIds.ObjectHeld),
                    item.Key,
                    $"You are carrying {item.Key}.")),
        ];

    private static IReadOnlyList<string> VisibleObjectIds(
        FirstBoardWorld world,
        BoardActor observer,
        PlaceId observerPlace)
    {
        IEnumerable<string> portableObjects = world.Objects
            .Where(item =>
                item.OwnerActorId == observer.Id ||
                (item.OwnerActorId is null && world.IsAtPlace(item.Key, observerPlace)))
            .OrderBy(item => item.Id)
            .Select(item => item.Key);
        return world.IsAtPlace(BoardIds.LockedChest, observerPlace)
            ? [.. portableObjects, BoardIds.LockedChest]
            : [.. portableObjects];
    }

    private static IReadOnlyList<AvailableAction> AvailableActions(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        PlaceId actorPlace,
        IReadOnlyList<ObservedExit> exits)
    {
        var actions = new List<AvailableAction>();
        string[] exitIds =
        [
            .. exits
                .Where(exit => exit.IsAvailable)
                .OrderBy(exit => exit.ExitId, StringComparer.Ordinal)
                .Select(exit => exit.ExitId),
        ];
        if (exitIds.Length > 0)
        {
            actions.Add(new AvailableAction(
                ActionKinds.Travel,
                CandidateExitIds: Array.AsReadOnly(exitIds)));
        }

        actions.Add(new AvailableAction(ActionKinds.Wait));

        string[] targetActors =
        [
            .. world.Actors
                .Where(target =>
                    target.Id != actor.Id &&
                    world.AreCoLocated(actor.Key, target.Key))
                .OrderBy(target => target.Id)
                .Select(target => target.Key),
        ];
        if (targetActors.Length > 0)
        {
            actions.Add(new AvailableAction(
                ActionKinds.Talk,
                CandidateActorIds: Array.AsReadOnly(targetActors)));
        }

        string[] heldObjects =
        [
            .. world.Objects
                .Where(item => item.OwnerActorId == actor.Id)
                .OrderBy(item => item.Id)
                .Select(item => item.Key),
        ];
        string[] publicObjects =
        [
            .. world.Objects
                .Where(item =>
                    item.OwnerActorId is null &&
                    world.IsAtPlace(item.Key, actorPlace))
                .OrderBy(item => item.Id)
                .Select(item => item.Key),
        ];
        string[] inspectableObjects =
        [
            .. heldObjects,
            .. publicObjects,
            .. world.IsAtPlace(BoardIds.LockedChest, actorPlace)
                ? [BoardIds.LockedChest]
                : Array.Empty<string>(),
        ];
        actions.Add(new AvailableAction(
            ActionKinds.Observe,
            CandidateObjectIds: inspectableObjects.Length == 0
                ? null
                : Array.AsReadOnly(inspectableObjects)));

        if (publicObjects.Length > 0)
        {
            actions.Add(new AvailableAction(
                ActionKinds.Take,
                CandidateObjectIds: Array.AsReadOnly(publicObjects)));
        }

        if (world.AreCoLocated(actor.Key, BoardIds.LockedChest) &&
            !world.CellarSealed &&
            !world.ChestOpened &&
            heldObjects.Contains(BoardIds.BrassKey, StringComparer.Ordinal))
        {
            actions.Add(new AvailableAction(
                ActionKinds.Use,
                CandidateObjectIds: [BoardIds.LockedChest]));
        }

        if (heldObjects.Length > 0)
        {
            actions.Add(new AvailableAction(
                ActionKinds.Put,
                CandidateObjectIds: Array.AsReadOnly(heldObjects)));
        }

        if (heldObjects.Length > 0 && targetActors.Length > 0)
        {
            actions.Add(new AvailableAction(
                ActionKinds.Show,
                CandidateActorIds: Array.AsReadOnly(targetActors),
                CandidateObjectIds: Array.AsReadOnly(heldObjects)));
            actions.Add(new AvailableAction(
                ActionKinds.Give,
                CandidateActorIds: Array.AsReadOnly(targetActors),
                CandidateObjectIds: Array.AsReadOnly(heldObjects)));
        }

        _ = instance;
        return actions.AsReadOnly();
    }

    private static string LocationSnapshot(SpatialLocation location) =>
        location switch
        {
            AtPlaceLocation atPlace => $"place:{atPlace.PlaceId.Value}",
            TraversingLocation traversing =>
                $"passage:{traversing.PassageId.Value}:{traversing.FromPlaceId.Value}:" +
                $"{traversing.ToPlaceId.Value}:{traversing.StartedAt.Ticks}:" +
                $"{traversing.SpeedSnapshot}:{traversing.ArrivalDue.Ticks}",
            _ => throw new InvalidOperationException("Unknown Spatial location."),
        };

    private static string PayloadSummary(FirstBoardFact fact) => fact switch
    {
        GameBoardFact game => GamePayloadSummary(game.Value),
        SpatialBoardFact spatial => SpatialPayloadSummary(spatial.Value),
        _ => throw new InvalidOperationException("Unknown FirstBoard Host fact."),
    };

    private static string GamePayloadSummary(BoardEventPayload payload) => payload switch
    {
        ActorTravelStartedEvent started =>
            $"actor={started.ActorId} exit={started.ExitId} destination={started.DestinationId}",
        TicketConsumedEvent consumed =>
            $"actor={consumed.ActorId} ticket={consumed.TicketObjectId}",
        ActorWaitStartedEvent waited =>
            $"actor={waited.ActorId} completeAt={waited.CompleteAt.Ticks}",
        ActorWaitedEvent waited => $"actor={waited.ActorId}",
        ActorSpokeEvent spoke =>
            $"actor={spoke.ActorId} target={spoke.TargetActorId} text={spoke.Text}",
        ActorObservedEvent observed =>
            $"actor={observed.ActorId} targetObject={observed.TargetObjectId} facts=" +
            string.Join(",", observed.LearnedFacts.Select(value => value.Kind)),
        ObjectTakenEvent taken => $"actor={taken.ActorId} object={taken.ObjectId}",
        ObjectPlacedEvent placed =>
            $"actor={placed.ActorId} object={placed.ObjectId} place={placed.PlaceId}",
        ObjectGivenEvent given =>
            $"actor={given.ActorId} target={given.TargetActorId} object={given.ObjectId}",
        ObjectShownEvent shown =>
            $"actor={shown.ActorId} target={shown.TargetActorId} object={shown.ObjectId}",
        ChestOpenedEvent opened =>
            $"actor={opened.ActorId} object={opened.ObjectId} key={opened.KeyObjectId}",
        ActionRejectedEvent rejected =>
            $"actor={rejected.ActorId} action={rejected.RejectedIntent.ActionKind.Id} " +
            $"reason={rejected.Reason}",
        CellarSealedEvent => "place=cellar",
        _ => throw new InvalidOperationException("Unknown FirstBoard Game payload."),
    };

    private static string SpatialPayloadSummary(GraphSpatialFact payload) => payload switch
    {
        EntityPlacedFact placed =>
            $"entity={placed.EntityId.Value} place={placed.PlaceId.Value}",
        EntityRemovedFact removed =>
            $"entity={removed.EntityId.Value}",
        TraversalStartedFact started =>
            $"entity={started.EntityId.Value} passage={started.PassageId.Value} " +
            $"from={started.FromPlaceId.Value} speed={started.SpeedSnapshot}",
        TraversalArrivedFact arrived =>
            $"entity={arrived.EntityId.Value} generation={arrived.ExpectedMovementGeneration}",
        PassageEntryAccessChangedFact changed =>
            $"passage={changed.PassageId.Value} a={changed.ResultAccess.EnterableFromA} " +
            $"b={changed.ResultAccess.EnterableFromB}",
        PassageEntryChangeScheduledFact scheduled =>
            $"passage={scheduled.PassageId.Value} due={scheduled.Due.Ticks}",
        ScheduledPassageEntryChangeAppliedFact applied =>
            $"passage={applied.PassageId.Value} due={applied.Due.Ticks}",
        _ => throw new InvalidOperationException("Unknown Spatial payload."),
    };

    private static string GameFactName(BoardEventPayload fact) => fact switch
    {
        ActorTravelStartedEvent => "actor.travel-started",
        TicketConsumedEvent => "ticket.consumed",
        ActorWaitStartedEvent => "actor.wait-started",
        ActorWaitedEvent => "actor.waited",
        ActorSpokeEvent => "actor.spoke",
        ActorObservedEvent => "actor.observed",
        ObjectTakenEvent => "object.taken",
        ObjectPlacedEvent => "object.placed",
        ObjectGivenEvent => "object.given",
        ObjectShownEvent => "object.shown",
        ChestOpenedEvent => "chest.opened",
        ActionRejectedEvent => "action.rejected",
        CellarSealedEvent => "cellar.sealed",
        _ => throw new InvalidOperationException("Unknown FirstBoard Game fact."),
    };

    private static string SpatialFactName(GraphSpatialFact fact) => fact switch
    {
        EntityPlacedFact => "spatial.entity-placed",
        EntityRemovedFact => "spatial.entity-removed",
        TraversalStartedFact => "spatial.traversal-started",
        TraversalArrivedFact => "spatial.traversal-arrived",
        PassageEntryAccessChangedFact => "spatial.passage-entry-access-changed",
        PassageEntryChangeScheduledFact => "spatial.passage-entry-change-scheduled",
        ScheduledPassageEntryChangeAppliedFact => "spatial.scheduled-passage-entry-change-applied",
        _ => throw new InvalidOperationException("Unknown Spatial fact."),
    };
}

public sealed record BoardRunCapture(
    FirstBoardWorld InitialWorld,
    HostRunResult<FirstBoardWorld> Result,
    InMemoryJournal<FirstBoardFact> Journal);

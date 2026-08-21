using System.Globalization;
using System.Text;
using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard;

public static class FirstBoardScenario
{
    public const long LineageId = 10_001;
    private const int MaxTransitionsPerModelTime = 10_000;

    public static SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> CreateKernel(
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        ScenarioInstance instance,
        IJournalSink<BoardEventPayload> journal,
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

        var reducer = new FirstBoardReducer();
        return new SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload>(
            world,
            version ?? new WorldVersion(journal.LineageId, journal.Batches.Count),
            world.Now,
            lastCommittedInstant,
            new SimulationRules(instance.WorldSeed, MaxTransitionsPerModelTime),
            [
                new CellarDeadlineRule(instance.Definition.CellarDeadlineMs),
                new ActivityCompletionRule(),
                new DecisionPointRule(drivers),
            ],
            journal,
            reducer.Apply,
            ValidateWorld);
    }

    /// <summary>Runs the default definition with a backward-compatible explicit seed.</summary>
    public static async Task<BoardRunCapture> RunAsync(
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        ulong worldSeed,
        ModelTime until,
        FirstBoardWorld? initialWorld = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            drivers,
            ScenarioInstance.CreateDefault(worldSeed),
            until,
            initialWorld,
            cancellationToken);

    /// <summary>Runs one frozen seeded scenario instance.</summary>
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

        var journal = new InMemoryJournal<BoardEventPayload>(LineageId);
        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            CreateKernel(drivers, instance, journal, world);
        HostRunResult<FirstBoardWorld> result = await SimulationHost.RunUntilAsync(
            kernel,
            until,
            cancellationToken);
        return new BoardRunCapture(world, result, journal);
    }

    public static DecisionRequest BuildRequest(
        FirstBoardWorld world,
        BoardActor actor,
        ModelTime modelTime)
    {
        if (!world.IsIdle(actor))
        {
            throw new InvalidOperationException("Only an idle actor can receive a decision request.");
        }

        var observation = new Observation(
            actor.Key,
            actor.PlaceId,
            ModelTimeMs: modelTime.Ticks,
            VisibleActorIds: VisibleActors(world, actor),
            VisibleObjectIds: VisibleObjectIds(world, actor),
            KnownFacts: ObservationFacts(world, actor));
        return new DecisionRequest(
            new DecisionId($"decision.{actor.Key}.{actor.DecisionSequence + 1}"),
            actor.Key,
            ModelTimeMs: modelTime.Ticks,
            observation,
            AvailableActions(world, actor));
    }

    public static string WorldSnapshot(FirstBoardWorld world)
    {
        string actors = string.Join(
            ";",
            world.Actors.OrderBy(actor => actor.Id).Select(actor => string.Join(
                "|",
                actor.Id.ToString(CultureInfo.InvariantCulture),
                actor.Key,
                actor.PlaceId,
                actor.Generation.ToString(CultureInfo.InvariantCulture),
                actor.DecisionSequence.ToString(CultureInfo.InvariantCulture),
                ActivitySummary(actor.Activity),
                string.Join(",", actor.KnownFacts.Select(fact => $"{fact.Kind}@{fact.RelatedId}")))));
        string objects = string.Join(
            ";",
            world.Objects.OrderBy(item => item.Id).Select(item => string.Join(
                "|",
                item.Id.ToString(CultureInfo.InvariantCulture),
                item.Key,
                item.PlaceId,
                item.OwnerActorId?.ToString(CultureInfo.InvariantCulture))));
        return FormattableString.Invariant(
            $"seed={world.WorldSeed};now={world.Now.Ticks};sealed={world.CellarSealed};chestOpened={world.ChestOpened};actors={actors};objects={objects}");
    }

    public static string[] EventSnapshots(InMemoryJournal<BoardEventPayload> journal) =>
        [
            .. journal.Batches.SelectMany(batch => batch.Facts.Select((fact, index) =>
                FormattableString.Invariant(
                    $"{batch.Instant.ModelTime.Ticks}:{batch.Instant.CausalOrdinal} #{index} {FactName(fact)} {PayloadSummary(fact)}"))),
        ];

    public static string FormatJournal(InMemoryJournal<BoardEventPayload> journal)
    {
        var text = new StringBuilder();
        foreach (string snapshot in EventSnapshots(journal))
        {
            text.AppendLine(snapshot);
        }

        return text.ToString();
    }

    private static IReadOnlyList<string> VisibleActors(FirstBoardWorld world, BoardActor observer) =>
        [
            .. world.Actors
                .Where(actor =>
                    actor.Id != observer.Id &&
                    world.IsPresent(actor) &&
                    actor.PlaceId == observer.PlaceId)
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

    private static IReadOnlyList<string> VisibleObjectIds(FirstBoardWorld world, BoardActor observer)
    {
        IEnumerable<string> portableObjects = world.Objects
            .Where(item => IsVisible(world, observer, item))
            .OrderBy(item => item.Id)
            .Select(item => item.Key);
        return observer.PlaceId == BoardIds.Cellar
            ? [.. portableObjects, BoardIds.LockedChest]
            : [.. portableObjects];
    }

    private static IReadOnlyList<AvailableAction> AvailableActions(
        FirstBoardWorld world,
        BoardActor actor)
    {
        var actions = new List<AvailableAction>();
        string[] destinations =
        [
            .. world.Place(actor.PlaceId).AdjacentPlaceIds
                .Where(destination => destination != BoardIds.Cellar ||
                    !actor.KnownFacts.Any(fact => fact.Kind == BoardIds.CellarSealedKnown))
                .Order(StringComparer.Ordinal),
        ];
        if (destinations.Length > 0)
        {
            actions.Add(new AvailableAction(
                ActionKinds.Travel,
                CandidateDestinationIds: Array.AsReadOnly(destinations)));
        }

        actions.Add(new AvailableAction(ActionKinds.Wait));

        string[] targetActors =
        [
            .. world.Actors
                .Where(target =>
                    target.Id != actor.Id &&
                    world.IsPresent(target) &&
                    target.PlaceId == actor.PlaceId)
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
        string[] inspectableObjects =
        [
            .. world.Objects
                .Where(item =>
                    item.OwnerActorId == actor.Id ||
                    (item.OwnerActorId is null && item.PlaceId == actor.PlaceId))
                .OrderBy(item => item.Id)
                .Select(item => item.Key),
        ];
        actions.Add(new AvailableAction(
            ActionKinds.Observe,
            CandidateObjectIds: inspectableObjects.Length == 0
                ? null
                : Array.AsReadOnly(inspectableObjects)));

        string[] takeableObjects =
        [
            .. world.Objects
                .Where(item => item.OwnerActorId is null && item.PlaceId == actor.PlaceId)
                .OrderBy(item => item.Id)
                .Select(item => item.Key),
        ];
        if (takeableObjects.Length > 0)
        {
            actions.Add(new AvailableAction(
                ActionKinds.Take,
                CandidateObjectIds: Array.AsReadOnly(takeableObjects)));
        }

        if (actor.PlaceId == BoardIds.Cellar &&
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

        return actions.AsReadOnly();
    }

    private static bool IsVisible(
        FirstBoardWorld world,
        BoardActor observer,
        BoardObject item)
    {
        if (item.PlaceId == observer.PlaceId)
        {
            return true;
        }

        if (item.OwnerActorId is not long ownerId)
        {
            return false;
        }

        return ownerId == observer.Id;
    }

    private static string ActivitySummary(BoardActivity? activity) =>
        activity is null
            ? "-"
            : FormattableString.Invariant($"{activity.Kind}:{activity.Due.Ticks}:{activity.DestinationId}");

    private static string PayloadSummary(BoardEventPayload payload) =>
        payload switch
        {
            ActorDepartedEvent departed =>
                $"actor={departed.ActorId} origin={departed.OriginId} " +
                $"destination={departed.DestinationId} arriveAt={departed.ArriveAt.Ticks}",
            ActorArrivedEvent arrived =>
                $"actor={arrived.ActorId} destination={arrived.DestinationId}",
            ActorWaitStartedEvent waited =>
                $"actor={waited.ActorId} completeAt={waited.CompleteAt.Ticks}",
            ActorWaitedEvent waited => $"actor={waited.ActorId}",
            ActorSpokeEvent spoke =>
                $"actor={spoke.ActorId} target={spoke.TargetActorId} text={spoke.Text}",
            ActorObservedEvent observed =>
                $"actor={observed.ActorId} targetObject={observed.TargetObjectId} facts=" +
                string.Join(",", observed.LearnedFacts.Select(fact => fact.Kind)),
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
            _ => throw new InvalidOperationException("Unknown FirstBoard event payload."),
        };

    public static string FactName(BoardEventPayload fact) => fact switch
    {
        ActorDepartedEvent => "actor.departed",
        ActorArrivedEvent => "actor.arrived",
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
        _ => throw new InvalidOperationException("Unknown FirstBoard fact."),
    };

    private static void ValidateWorld(FirstBoardWorld world)
    {
        if (world.Actors.Select(actor => actor.Id).Distinct().Count() != world.Actors.Count ||
            world.Actors.Select(actor => actor.Key).Distinct(StringComparer.Ordinal).Count() != world.Actors.Count)
        {
            throw new InvalidOperationException("FirstBoard actor identities must be unique.");
        }

        if (world.Objects.Select(item => item.Id).Distinct().Count() != world.Objects.Count ||
            world.Objects.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != world.Objects.Count)
        {
            throw new InvalidOperationException("FirstBoard object identities must be unique.");
        }
    }
}

public sealed record BoardRunCapture(
    FirstBoardWorld InitialWorld,
    HostRunResult<FirstBoardWorld> Result,
    InMemoryJournal<BoardEventPayload> Journal);

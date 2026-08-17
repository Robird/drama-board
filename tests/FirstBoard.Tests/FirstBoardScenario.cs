using System.Globalization;
using System.Text;
using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Tests;

internal static class FirstBoardScenario
{
    public const long LineageId = 10_001;

    public static SimulationLoop<FirstBoardWorld, BoardCandidate, BoardEventPayload> CreateLoop(
        FirstBoardReducer reducer) =>
        new(
            [
                new CellarDeadlineSystem(),
                new ActionResolutionSystem(),
                new ActivityCompletionSystem(),
                new DecisionSchedulingSystem(),
            ],
            reducer,
            decisionRequestPredicate: domainEvent =>
                domainEvent.Kind == BoardEventKinds.DecisionRequested);

    public static async Task<BoardRunCapture> RunAsync(
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        ulong worldSeed,
        ModelTime until,
        FirstBoardWorld? initialWorld = null)
    {
        FirstBoardWorld world = initialWorld ?? FirstBoardWorld.CreateInitial(worldSeed);
        var reducer = new FirstBoardReducer();
        var journal = new InMemoryJournal<BoardEventPayload>();
        var session = new PlayerDecisionSession<FirstBoardWorld, BoardCandidate, BoardEventPayload>(
            CreateLoop(reducer),
            journal,
            world,
            SimulationCursor.CreateInitial(LineageId, ModelTime.Zero),
            SelectActor,
            drivers,
            BuildRequest,
            TranslateDecision);

        PlayerDecisionSessionResult<FirstBoardWorld> result = await session.RunUntilAsync(
            until,
            CancellationToken.None);
        return new BoardRunCapture(world, result, journal);
    }

    public static string SelectActor(DomainEvent<BoardEventPayload> decisionEvent) =>
        decisionEvent.Payload is DecisionRequestedEvent requested
            ? requested.ActorId
            : throw new InvalidOperationException("The routed event is not a decision request.");

    public static DecisionRequest? BuildRequest(
        FirstBoardWorld world,
        DomainEvent<BoardEventPayload> decisionEvent,
        WorldVersion version)
    {
        if (decisionEvent.Payload is not DecisionRequestedEvent requested)
        {
            throw new InvalidOperationException("The request event has the wrong payload.");
        }

        BoardActor actor = world.Actor(requested.ActorId);
        if (!actor.AwaitingDecision || actor.OpenDecisionId != requested.DecisionId)
        {
            return null;
        }

        var observation = new Observation(
            actor.Key,
            actor.PlaceId,
            ModelTimeMs: -1,
            Microstep: -1,
            VisibleActorIds: VisibleActors(world, actor),
            VisibleObjectIds: VisibleObjectIds(world, actor),
            KnownFacts:
            [
                .. actor.KnownFacts.Select(fact => new KnownFact(
                    new FactKind(fact.Kind),
                    fact.RelatedId,
                    fact.Text)),
            ]);
        return new DecisionRequest(
            new DecisionId(requested.DecisionId),
            BasedOnWorldVersion: -1,
            LineageId: -1,
            ModelTimeMs: -1,
            Microstep: -1,
            actor.Key,
            observation,
            DecisionReasons.Scheduled,
            AvailableActions(world, actor));
    }

    public static IReadOnlyList<UncommittedDomainEvent<BoardEventPayload>> TranslateDecision(
        PlayerDecision decision,
        FirstBoardWorld world)
    {
        BoardActor actor = world.Actors.Single(current =>
            current.OpenDecisionId == decision.DecisionId.Value);
        return [ActionInput(actor.Key, decision.DecisionId.Value, decision.Intent)];
    }

    public static UncommittedDomainEvent<BoardEventPayload> ActionInput(
        string actorId,
        string decisionId,
        Intent intent) =>
        new(
            BoardEventKinds.ForAction(intent.ActionKind),
            new ActionRequestedEvent(actorId, decisionId, intent));

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
                actor.AwaitingDecision,
                actor.OpenDecisionId,
                ActionSummary(actor.PendingAction),
                ActivitySummary(actor.Activity),
                string.Join(",", actor.KnownFacts.Select(fact => $"{fact.Kind}@{fact.RelatedId}")))));
        string objects = string.Join(
            ";",
            world.Objects.OrderBy(item => item.Id).Select(item => string.Join(
                "|",
                item.Id.ToString(CultureInfo.InvariantCulture),
                item.Key,
                item.PlaceId,
                item.OwnerActorId?.ToString(CultureInfo.InvariantCulture),
                item.ContentionRound.ToString(CultureInfo.InvariantCulture))));
        return FormattableString.Invariant(
            $"seed={world.WorldSeed};sealed={world.CellarSealed};actors={actors};objects={objects}");
    }

    public static string[] EventSnapshots(InMemoryJournal<BoardEventPayload> journal) =>
        [
            .. journal.Events.Select(domainEvent => FormattableString.Invariant(
                $"{domainEvent.Timestamp.ModelTime.Ticks}:{domainEvent.Timestamp.Microstep.Value} {domainEvent.Kind.Id} {PayloadSummary(domainEvent.Payload)}")),
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
                .Where(destination => destination != BoardIds.Cellar || !world.CellarSealed)
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

        actions.Add(new AvailableAction(ActionKinds.Observe));

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

        string[] heldObjects =
        [
            .. world.Objects
                .Where(item => item.OwnerActorId == actor.Id)
                .OrderBy(item => item.Id)
                .Select(item => item.Key),
        ];
        if (heldObjects.Length > 0 && targetActors.Length > 0)
        {
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

        BoardActor owner = world.Actor(ownerId);
        return world.IsPresent(owner) && owner.PlaceId == observer.PlaceId;
    }

    private static string ActionSummary(SubmittedAction? action) =>
        action is null
            ? "-"
            : $"{action.Intent.ActionKind.Id}:{action.Intent.TargetActorId}:" +
              $"{action.Intent.TargetObjectId}:{action.Intent.DestinationId}:" +
              $"{action.Intent.FreeText}:{action.Intent.DurationMs}:{action.Intent.UntilModelTimeMs}";

    private static string ActivitySummary(BoardActivity? activity) =>
        activity is null
            ? "-"
            : FormattableString.Invariant($"{activity.Kind}:{activity.Due.Ticks}:{activity.DestinationId}");

    private static string PayloadSummary(BoardEventPayload payload) =>
        payload switch
        {
            DecisionRequestedEvent requested =>
                $"actor={requested.ActorId} decision={requested.DecisionId}",
            ActionRequestedEvent requested =>
                $"actor={requested.ActorId} action={requested.Intent.ActionKind.Id} " +
                $"targetActor={requested.Intent.TargetActorId} " +
                $"targetObject={requested.Intent.TargetObjectId} " +
                $"destination={requested.Intent.DestinationId}",
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
                $"actor={observed.ActorId} facts=" +
                string.Join(",", observed.LearnedFacts.Select(fact => fact.Kind)),
            ObjectTakenEvent taken => $"actor={taken.ActorId} object={taken.ObjectId}",
            ObjectGivenEvent given =>
                $"actor={given.ActorId} target={given.TargetActorId} object={given.ObjectId}",
            ObjectContentionResolvedEvent contention =>
                $"object={contention.ObjectId} competitors=" +
                $"{string.Join(",", contention.CompetitorActorIds)} winner={contention.WinnerActorId} " +
                $"sample={contention.Sample.StreamId}/{contention.Sample.Generation}/" +
                $"{contention.Sample.SampleIndex}",
            ActionRejectedEvent rejected =>
                $"actor={rejected.ActorId} action={rejected.ActionKind.Id} reason={rejected.Reason}",
            CellarSealedEvent => "place=cellar",
            _ => throw new InvalidOperationException("Unknown FirstBoard event payload."),
        };
}

internal sealed record BoardRunCapture(
    FirstBoardWorld InitialWorld,
    PlayerDecisionSessionResult<FirstBoardWorld> Result,
    InMemoryJournal<BoardEventPayload> Journal);

internal sealed class RecordingPlayerDriver : IPlayerDriver
{
    private readonly IPlayerDriver _inner;
    private readonly List<DecisionRequest> _requests = [];

    public RecordingPlayerDriver(IPlayerDriver inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<DecisionRequest> Requests => _requests.AsReadOnly();

    public async ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);
        return await _inner.DecideAsync(request, cancellationToken);
    }
}
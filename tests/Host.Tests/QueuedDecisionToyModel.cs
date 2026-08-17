using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.Host.Tests;

internal sealed record QueuedDecisionWorld(
    bool InitialRequestsRaised,
    bool FollowUpPending,
    bool FollowUpRaised,
    int Value,
    string AppliedOrder)
{
    public static QueuedDecisionWorld Initial { get; } = new(false, false, false, 0, string.Empty);
}

internal sealed record QueuedDecisionEvent(
    string ActorId,
    string DecisionId,
    int Delta = 0,
    bool OpensFollowUp = false);

internal sealed record QueuedDecisionCandidate(bool IsFollowUp);

internal static class QueuedDecisionEventKinds
{
    public static EventKind DecisionRequested { get; } = new("queue.decision-requested", 1);

    public static EventKind DecisionApplied { get; } = new("queue.decision-applied", 1);
}

internal sealed class QueuedDecisionSystem
    : ISimSystem<QueuedDecisionWorld, QueuedDecisionCandidate, QueuedDecisionEvent>
{
    private const long SourceId = 2_001;
    private readonly bool _forecastFutureWork;
    private readonly bool _duplicateActorInInitialBatch;

    public QueuedDecisionSystem(
        bool forecastFutureWork = false,
        bool duplicateActorInInitialBatch = false)
    {
        _forecastFutureWork = forecastFutureWork;
        _duplicateActorInInitialBatch = duplicateActorInInitialBatch;
    }

    public IReadOnlyList<EventCandidate<QueuedDecisionCandidate>> ForecastNext(
        QueuedDecisionWorld world,
        ModelTime now)
    {
        if (!world.InitialRequestsRaised)
        {
            return
            [
                new EventCandidate<QueuedDecisionCandidate>(
                    new EventCandidateId(1),
                    now + new ModelDuration(10),
                    SourceId,
                    new QueuedDecisionCandidate(IsFollowUp: false)),
            ];
        }

        if (world.FollowUpPending && !world.FollowUpRaised)
        {
            return
            [
                new EventCandidate<QueuedDecisionCandidate>(
                    new EventCandidateId(2),
                    now,
                    SourceId,
                    new QueuedDecisionCandidate(IsFollowUp: true)),
            ];
        }

        if (_forecastFutureWork)
        {
            return
            [
                new EventCandidate<QueuedDecisionCandidate>(
                    new EventCandidateId(3),
                    now + new ModelDuration(10),
                    SourceId,
                    new QueuedDecisionCandidate(IsFollowUp: false)),
            ];
        }

        return [];
    }

    public IReadOnlyList<UncommittedDomainEvent<QueuedDecisionEvent>> Resolve(
        QueuedDecisionWorld world,
        EventCandidate<QueuedDecisionCandidate> candidate)
    {
        if (candidate.Payload.IsFollowUp)
        {
            return [QueuedDecisionScenario.Request("actor.c", "decision.c")];
        }

        return
        [
            QueuedDecisionScenario.Request("actor.a", "decision.a"),
            QueuedDecisionScenario.Request(
                _duplicateActorInInitialBatch ? "actor.a" : "actor.b",
                "decision.b"),
        ];
    }
}

internal sealed class QueuedDecisionReducer : IEventReducer<QueuedDecisionWorld, QueuedDecisionEvent>
{
    public QueuedDecisionWorld Apply(
        QueuedDecisionWorld world,
        DomainEvent<QueuedDecisionEvent> domainEvent) =>
        domainEvent.Kind.Id switch
        {
            "queue.decision-requested" => ApplyRequest(world, domainEvent.Payload),
            "queue.decision-applied" => ApplyDecision(world, domainEvent.Payload),
            _ => throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'."),
        };

    private static QueuedDecisionWorld ApplyRequest(
        QueuedDecisionWorld world,
        QueuedDecisionEvent payload) =>
        payload.DecisionId == "decision.c"
            ? world with { FollowUpPending = false, FollowUpRaised = true }
            : world with { InitialRequestsRaised = true };

    private static QueuedDecisionWorld ApplyDecision(
        QueuedDecisionWorld world,
        QueuedDecisionEvent payload) =>
        world with
        {
            FollowUpPending = world.FollowUpPending || payload.OpensFollowUp,
            Value = checked(world.Value + payload.Delta),
            AppliedOrder = string.IsNullOrEmpty(world.AppliedOrder)
                ? payload.DecisionId
                : $"{world.AppliedOrder},{payload.DecisionId}",
        };
}

internal static class QueuedDecisionScenario
{
    public static string SelectActor(DomainEvent<QueuedDecisionEvent> decisionEvent) =>
        decisionEvent.Payload.ActorId;

    public static DecisionRequest BuildRequest(
        QueuedDecisionWorld world,
        DomainEvent<QueuedDecisionEvent> decisionEvent,
        WorldVersion version)
    {
        QueuedDecisionEvent payload = decisionEvent.Payload;
        var observation = new Observation(
            payload.ActorId,
            $"value.{world.Value}",
            ModelTimeMs: -1,
            Microstep: -1,
            VisibleActorIds: [],
            VisibleObjectIds: [],
            KnownFacts: []);
        return new DecisionRequest(
            new DecisionId(payload.DecisionId),
            BasedOnWorldVersion: -1,
            LineageId: -1,
            ModelTimeMs: -1,
            Microstep: -1,
            payload.ActorId,
            observation,
            DecisionReasons.Scheduled,
            [new AvailableAction(ActionKinds.Wait)]);
    }

    public static UncommittedDomainEvent<QueuedDecisionEvent> Request(
        string actorId,
        string decisionId) =>
        new(
            QueuedDecisionEventKinds.DecisionRequested,
            new QueuedDecisionEvent(actorId, decisionId));

    public static IReadOnlyList<UncommittedDomainEvent<QueuedDecisionEvent>> Apply(
        PlayerDecision decision,
        int delta,
        bool opensFollowUp = false) =>
        [
            new UncommittedDomainEvent<QueuedDecisionEvent>(
                QueuedDecisionEventKinds.DecisionApplied,
                new QueuedDecisionEvent(
                    ActorFor(decision.DecisionId.Value),
                    decision.DecisionId.Value,
                    delta,
                    opensFollowUp)),
        ];

    private static string ActorFor(string decisionId) =>
        decisionId switch
        {
            "decision.a" => "actor.a",
            "decision.b" => "actor.b",
            "decision.c" => "actor.c",
            _ => throw new InvalidOperationException($"Unknown decision identifier '{decisionId}'."),
        };
}

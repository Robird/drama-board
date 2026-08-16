using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.Host.Tests;

internal sealed record TravelerWorld(
    string Location,
    int CompletedForks,
    bool AwaitingDecision,
    string? FirstChoice,
    string? SecondChoice,
    bool HasArrived)
{
    public static TravelerWorld Initial { get; } = new("origin", 0, false, null, null, false);
}

internal sealed record TravelerEvent(string ActorId, int ForkIndex, string? DestinationId = null);

internal sealed record TravelerCandidate(int ForkIndex, bool IsArrival);

internal static class TravelerEventKinds
{
    public static EventKind ReachedFork { get; } = new("traveler.reached-fork", 1);

    public static EventKind DirectionChosen { get; } = new("traveler.direction-chosen", 1);

    public static EventKind Waited { get; } = new("traveler.waited", 1);

    public static EventKind Arrived { get; } = new("traveler.arrived", 1);
}

internal sealed class TravelerSystem : ISimSystem<TravelerWorld, TravelerCandidate, TravelerEvent>
{
    private const long TravelerSourceId = 1_001;

    public IReadOnlyList<EventCandidate<TravelerCandidate>> ForecastNext(TravelerWorld world, ModelTime now)
    {
        if (world.AwaitingDecision || world.HasArrived)
        {
            return [];
        }

        bool isArrival = world.CompletedForks == 2;
        int sequence = world.CompletedForks + 1;
        return
        [
            new EventCandidate<TravelerCandidate>(
                new EventCandidateId(sequence),
                now + new ModelDuration(10),
                TravelerSourceId,
                new TravelerCandidate(sequence, isArrival)),
        ];
    }

    public IReadOnlyList<UncommittedDomainEvent<TravelerEvent>> Resolve(
        TravelerWorld world,
        EventCandidate<TravelerCandidate> candidate)
    {
        if (candidate.Payload.IsArrival)
        {
            string endpoint = world.FirstChoice == "wait" && world.SecondChoice == "wait"
                ? "endpoint.default"
                : $"endpoint.{world.FirstChoice}.{world.SecondChoice}";
            return
            [
                new UncommittedDomainEvent<TravelerEvent>(
                    TravelerEventKinds.Arrived,
                    new TravelerEvent(TravelerScenario.ActorId, candidate.Payload.ForkIndex, endpoint)),
            ];
        }

        return
        [
            new UncommittedDomainEvent<TravelerEvent>(
                TravelerEventKinds.ReachedFork,
                new TravelerEvent(TravelerScenario.ActorId, candidate.Payload.ForkIndex)),
        ];
    }
}

internal sealed class TravelerReducer : IEventReducer<TravelerWorld, TravelerEvent>
{
    public TravelerWorld Apply(TravelerWorld world, DomainEvent<TravelerEvent> domainEvent)
    {
        TravelerEvent payload = domainEvent.Payload;
        return domainEvent.Kind.Id switch
        {
            "traveler.reached-fork" => world with
            {
                Location = $"fork.{payload.ForkIndex}",
                AwaitingDecision = true,
            },
            "traveler.direction-chosen" => ApplyChoice(world, payload.DestinationId!),
            "traveler.waited" => ApplyChoice(world, "wait"),
            "traveler.arrived" => world with
            {
                Location = payload.DestinationId!,
                HasArrived = true,
            },
            _ => throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'."),
        };
    }

    private static TravelerWorld ApplyChoice(TravelerWorld world, string choice) =>
        world.CompletedForks switch
        {
            0 => world with
            {
                Location = choice == "wait" ? world.Location : choice,
                CompletedForks = 1,
                AwaitingDecision = false,
                FirstChoice = choice,
            },
            1 => world with
            {
                Location = choice == "wait" ? world.Location : choice,
                CompletedForks = 2,
                AwaitingDecision = false,
                SecondChoice = choice,
            },
            _ => throw new InvalidOperationException("The traveler has no unresolved fork."),
        };
}

internal static class TravelerScenario
{
    public const string ActorId = "actor.traveler";

    public static string SelectActor(DomainEvent<TravelerEvent> decisionEvent) =>
        decisionEvent.Payload.ActorId;

    public static DecisionRequest BuildRequest(
        TravelerWorld world,
        DomainEvent<TravelerEvent> decisionEvent,
        WorldVersion version)
    {
        TravelerEvent payload = decisionEvent.Payload;
        var observation = new Observation(
            ActorId,
            world.Location,
            ModelTimeMs: -1,
            Microstep: -1,
            VisibleActorIds: [],
            VisibleObjectIds: [],
            KnownFacts: []);
        return new DecisionRequest(
            new DecisionId($"decision.fork-{payload.ForkIndex}"),
            BasedOnWorldVersion: -1,
            LineageId: -1,
            ModelTimeMs: -1,
            Microstep: -1,
            ActorId,
            observation,
            DecisionReasons.Scheduled,
            [
                new AvailableAction(ActionKinds.Travel, CandidateDestinationIds: ["left", "right"]),
                new AvailableAction(ActionKinds.Wait),
            ]);
    }

    public static IReadOnlyList<UncommittedDomainEvent<TravelerEvent>> TranslateDecision(
        PlayerDecision decision,
        TravelerWorld world)
    {
        int forkIndex = world.CompletedForks + 1;
        if (decision.Intent.ActionKind == ActionKinds.Wait)
        {
            return
            [
                new UncommittedDomainEvent<TravelerEvent>(
                    TravelerEventKinds.Waited,
                    new TravelerEvent(ActorId, forkIndex)),
            ];
        }

        string? destination = decision.Intent.DestinationId;
        if (decision.Intent.ActionKind != ActionKinds.Travel ||
            destination is null ||
            destination != "left" && destination != "right")
        {
            throw new InvalidOperationException("The traveler decision is not an available fork action.");
        }

        return
        [
            new UncommittedDomainEvent<TravelerEvent>(
                TravelerEventKinds.DirectionChosen,
                new TravelerEvent(ActorId, forkIndex, destination)),
        ];
    }
}
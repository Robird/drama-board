using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record ScheduledReroute(ModelTime Due, string Destination);

internal sealed record RerouteWorld(
    long TravelerId,
    string Destination,
    ScheduledReroute? PendingReroute,
    bool HasRedirected,
    bool HasArrived)
{
    public static RerouteWorld Start(string destination, long travelerId = 1) =>
        new(travelerId, destination, null, false, false);
}

internal abstract record RerouteCandidatePayload;

internal sealed record ArrivalCandidatePayload(string Destination) : RerouteCandidatePayload;

internal sealed record ScheduledRerouteCandidatePayload(string Destination) : RerouteCandidatePayload;

internal abstract record RerouteEventPayload;

internal sealed record ArrivedEventPayload(string Destination) : RerouteEventPayload;

internal sealed record ReroutedEventPayload(string Destination) : RerouteEventPayload;

internal sealed record RerouteScheduledEventPayload(ModelTime Due, string Destination) : RerouteEventPayload;

internal static class RerouteEventKinds
{
    public static readonly EventKind Arrived = new("travel.arrived", 1);
    public static readonly EventKind Rerouted = new("travel.rerouted", 1);
    public static readonly EventKind RerouteScheduled = new("travel.reroute-scheduled", 1);
}

internal sealed class TravelSystem : ISimSystem<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>
{
    private readonly ModelTime _arrivalAtB;
    private readonly ModelTime _arrivalAtC;

    public TravelSystem(ModelTime arrivalAtB, ModelTime arrivalAtC)
    {
        _arrivalAtB = arrivalAtB;
        _arrivalAtC = arrivalAtC;
    }

    public IReadOnlyList<EventCandidate<RerouteCandidatePayload>> ForecastNext(RerouteWorld world, ModelTime now)
    {
        if (world.HasArrived)
        {
            return [];
        }

        ModelTime due = world.Destination switch
        {
            "B" => _arrivalAtB,
            "C" => _arrivalAtC,
            _ => throw new InvalidOperationException($"Unknown destination {world.Destination}."),
        };
        long generation = world.HasRedirected ? 1 : 0;

        return
        [
            new EventCandidate<RerouteCandidatePayload>(
                new EventCandidateId(100 + generation),
                due,
                world.TravelerId,
                new ArrivalCandidatePayload(world.Destination)),
        ];
    }

    public IReadOnlyList<UncommittedDomainEvent<RerouteEventPayload>> Resolve(
        RerouteWorld world,
        EventCandidate<RerouteCandidatePayload> candidate)
    {
        if (candidate.Payload is not ArrivalCandidatePayload arrival || arrival.Destination != world.Destination)
        {
            throw new InvalidOperationException("TravelSystem received an incompatible candidate payload.");
        }

        return
        [
            new UncommittedDomainEvent<RerouteEventPayload>(
                RerouteEventKinds.Arrived,
                new ArrivedEventPayload(arrival.Destination)),
        ];
    }
}

internal sealed class ScheduledRerouteSystem : ISimSystem<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>
{
    public IReadOnlyList<EventCandidate<RerouteCandidatePayload>> ForecastNext(RerouteWorld world, ModelTime now) =>
        world.HasRedirected || world.PendingReroute is not ScheduledReroute reroute
            ? []
            :
            [
                new EventCandidate<RerouteCandidatePayload>(
                    new EventCandidateId(200),
                    reroute.Due,
                    world.TravelerId,
                    new ScheduledRerouteCandidatePayload(reroute.Destination)),
            ];

    public IReadOnlyList<UncommittedDomainEvent<RerouteEventPayload>> Resolve(
        RerouteWorld world,
        EventCandidate<RerouteCandidatePayload> candidate)
    {
        if (candidate.Payload is not ScheduledRerouteCandidatePayload reroute)
        {
            throw new InvalidOperationException("ScheduledRerouteSystem received an incompatible candidate payload.");
        }

        return
        [
            new UncommittedDomainEvent<RerouteEventPayload>(
                RerouteEventKinds.Rerouted,
                new ReroutedEventPayload(reroute.Destination)),
        ];
    }
}

internal sealed class RerouteReducer : IEventReducer<RerouteWorld, RerouteEventPayload>
{
    public RerouteWorld Apply(RerouteWorld world, DomainEvent<RerouteEventPayload> domainEvent) =>
        (domainEvent.Kind.Id, domainEvent.Payload) switch
        {
            ({ } kindId, ArrivedEventPayload arrived) when kindId == RerouteEventKinds.Arrived.Id =>
                world with { Destination = arrived.Destination, HasArrived = true },
            ({ } kindId, ReroutedEventPayload rerouted) when kindId == RerouteEventKinds.Rerouted.Id =>
                world with
                {
                    Destination = rerouted.Destination,
                    PendingReroute = null,
                    HasRedirected = true,
                },
            ({ } kindId, RerouteScheduledEventPayload scheduled)
                when kindId == RerouteEventKinds.RerouteScheduled.Id =>
                world with { PendingReroute = new ScheduledReroute(scheduled.Due, scheduled.Destination) },
            _ => throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'."),
        };
}

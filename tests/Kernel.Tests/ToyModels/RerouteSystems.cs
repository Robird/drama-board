using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record RerouteWorld(string Destination, bool HasRedirected, bool HasArrived);

internal abstract record RerouteCandidatePayload;

internal sealed record ArrivalCandidatePayload(string Destination) : RerouteCandidatePayload;

internal sealed record ScheduledRerouteCandidatePayload(string Destination) : RerouteCandidatePayload;

internal abstract record RerouteEventPayload;

internal sealed record ArrivedEventPayload(string Destination) : RerouteEventPayload;

internal sealed record ReroutedEventPayload(string Destination) : RerouteEventPayload;

internal static class RerouteEventKinds
{
    public static readonly EventKind Arrived = new("travel.arrived", 1);
    public static readonly EventKind Rerouted = new("travel.rerouted", 1);
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
                sourceId: 1,
                generation,
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

internal sealed class ScheduledInputSystem : ISimSystem<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>
{
    private readonly ModelTime _due;
    private readonly string _destination;

    public ScheduledInputSystem(ModelTime due, string destination)
    {
        _due = due;
        _destination = destination;
    }

    public IReadOnlyList<EventCandidate<RerouteCandidatePayload>> ForecastNext(RerouteWorld world, ModelTime now) =>
        world.HasRedirected
            ? []
            :
            [
                new EventCandidate<RerouteCandidatePayload>(
                    new EventCandidateId(200),
                    _due,
                    sourceId: 2,
                    generation: 0,
                    new ScheduledRerouteCandidatePayload(_destination)),
            ];

    public IReadOnlyList<UncommittedDomainEvent<RerouteEventPayload>> Resolve(
        RerouteWorld world,
        EventCandidate<RerouteCandidatePayload> candidate)
    {
        if (candidate.Payload is not ScheduledRerouteCandidatePayload reroute)
        {
            throw new InvalidOperationException("ScheduledInputSystem received an incompatible candidate payload.");
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
        (domainEvent.Kind, domainEvent.Payload) switch
        {
            ({ } kind, ArrivedEventPayload arrived) when kind == RerouteEventKinds.Arrived =>
                world with { Destination = arrived.Destination, HasArrived = true },
            ({ } kind, ReroutedEventPayload rerouted) when kind == RerouteEventKinds.Rerouted =>
                world with { Destination = rerouted.Destination, HasRedirected = true },
            _ => throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind.Id}'."),
        };
}

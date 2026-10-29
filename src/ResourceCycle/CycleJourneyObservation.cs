using Atelia.DurableGraph;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>The traveller's known route, without the identity of an unexplored destination.</summary>
[DurableType("DramaBoard.ResourceCycle.JourneyObservation", 1)]
public sealed partial record CycleJourneyObservation(
    [field: DurableField(1)] string OriginName,
    [field: DurableField(2)] string PassageId,
    [field: DurableField(3)] string PassageDescription,
    [field: DurableField(4)] string? KnownDestinationName) : IDurableObject {
    internal static CycleJourneyObservation? Observe(CycleExpedition? expedition, string actorId, SpatialLocation? location = null) {
        location ??= expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location;
        if (expedition is null || location is not TraversingLocation moving) {
            return null;
        }
        var passage = CycleExpeditionMap.Graph.GetPassage(moving.PassageId);
        string origin = (passage.EndpointA == moving.TargetPlaceId ? passage.EndpointB : passage.EndpointA).Value;
        var directed = CycleExpeditionMap.GetPassages(origin).Single(p => p.Id == moving.PassageId.Value);
        return new(CycleExpeditionMap.GetPlace(origin).Name, directed.Id, directed.Description,
            expedition.Visits.Any(v => v.ActorId == actorId && v.PlaceId == moving.TargetPlaceId.Value)
                ? CycleExpeditionMap.GetPlace(moving.TargetPlaceId.Value).Name : null);
    }

    internal void Validate() {
        if (string.IsNullOrWhiteSpace(OriginName) || string.IsNullOrWhiteSpace(PassageId) ||
            string.IsNullOrWhiteSpace(PassageDescription) || KnownDestinationName is not null && string.IsNullOrWhiteSpace(KnownDestinationName)) {
            throw new InvalidOperationException("Malformed visible journey.");
        }
    }
}

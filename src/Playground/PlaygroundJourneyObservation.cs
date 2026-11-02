using Atelia.DurableGraph;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>The traveller's known route, without the identity of an unexplored destination.</summary>
[DurableType("DramaBoard.Playground.JourneyObservation", 1)]
public sealed partial record PlaygroundJourneyObservation(
    [field: DurableField(1)] string OriginName,
    [field: DurableField(2)] string PassageId,
    [field: DurableField(3)] string PassageDescription,
    [field: DurableField(4)] string? KnownDestinationName) : IDurableObject {
    internal static PlaygroundJourneyObservation? Observe(PlaygroundWorldDefinition definition, PlaygroundExpedition? expedition, string actorId, SpatialLocation? location = null) {
        location ??= expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location;
        if (expedition is null || location is not TraversingLocation moving) {
            return null;
        }
        var passage = definition.Runtime.Graph.GetPassage(moving.PassageId);
        string origin = (passage.EndpointA == moving.TargetPlaceId ? passage.EndpointB : passage.EndpointA).Value;
        var directed = definition.GetPassages(origin).Single(p => p.Id == moving.PassageId.Value);
        return new(definition.GetPlace(origin).Name, directed.Id, directed.Description,
            expedition.Visits.Any(v => v.ActorId == actorId && v.PlaceId == moving.TargetPlaceId.Value)
                ? definition.GetPlace(moving.TargetPlaceId.Value).Name : null);
    }

    internal void Validate() {
        if (string.IsNullOrWhiteSpace(OriginName) || string.IsNullOrWhiteSpace(PassageId) ||
            string.IsNullOrWhiteSpace(PassageDescription) || KnownDestinationName is not null && string.IsNullOrWhiteSpace(KnownDestinationName)) {
            throw new InvalidOperationException("Malformed visible journey.");
        }
    }
}

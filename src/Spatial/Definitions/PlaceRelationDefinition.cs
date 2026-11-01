namespace DramaBoard.Spatial;

/// <summary>Names capabilities of place connections, independently of event kinds.</summary>
public static class PlaceRelationCapabilities {
    public const ulong Sound = 1UL << 0;
}

/// <summary>Defines an undirected semantic connection independently of movement passages.</summary>
public sealed record PlaceRelationDefinition {
    public PlaceRelationDefinition(PlaceId endpointA, PlaceId endpointB, ulong flags) {
        SpatialIdentifier.Require(endpointA, nameof(endpointA));
        SpatialIdentifier.Require(endpointB, nameof(endpointB));
        if (endpointA == endpointB) {
            throw new ArgumentException("A place relation must connect two different places.", nameof(endpointB));
        }

        (EndpointA, EndpointB) = endpointA.CompareTo(endpointB) < 0
            ? (endpointA, endpointB)
            : (endpointB, endpointA);
        Flags = flags;
    }

    public PlaceId EndpointA { get; }

    public PlaceId EndpointB { get; }

    public ulong Flags { get; }
}

using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Base value for one entity's exclusive objective location.</summary>
public abstract record SpatialLocation
{
    private protected SpatialLocation()
    {
    }
}

/// <summary>Places an entity at one stable semantic locality.</summary>
public sealed record AtPlaceLocation : SpatialLocation
{
    public AtPlaceLocation(PlaceId placeId)
    {
        SpatialIdentifier.Require(placeId, nameof(placeId));
        PlaceId = placeId;
    }

    public PlaceId PlaceId { get; }
}

/// <summary>Stores one immutable endpoint-to-endpoint movement segment.</summary>
public sealed record TraversingLocation : SpatialLocation
{
    public TraversingLocation(
        PassageId passageId,
        PlaceId fromPlaceId,
        PlaceId toPlaceId,
        ModelTime startedAt,
        long speedSnapshot,
        ModelTime arrivalDue)
    {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        SpatialIdentifier.Require(fromPlaceId, nameof(fromPlaceId));
        SpatialIdentifier.Require(toPlaceId, nameof(toPlaceId));
        if (fromPlaceId == toPlaceId)
        {
            throw new ArgumentException("A traversal must have different endpoints.", nameof(toPlaceId));
        }

        if (speedSnapshot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedSnapshot), "Traversal speed must be positive.");
        }

        if (arrivalDue <= startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(arrivalDue), "Arrival must be later than traversal start.");
        }

        PassageId = passageId;
        FromPlaceId = fromPlaceId;
        ToPlaceId = toPlaceId;
        StartedAt = startedAt;
        SpeedSnapshot = speedSnapshot;
        ArrivalDue = arrivalDue;
    }

    public PassageId PassageId { get; }

    public PlaceId FromPlaceId { get; }

    public PlaceId ToPlaceId { get; }

    public ModelTime StartedAt { get; }

    public long SpeedSnapshot { get; }

    public ModelTime ArrivalDue { get; }
}

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

/// <summary>Stores one immutable anchored movement segment toward a passage endpoint.</summary>
public sealed record TraversingLocation : SpatialLocation
{
    public TraversingLocation(
        PassageId passageId,
        long anchorOffset,
        ModelTime anchorTime,
        PlaceId targetPlaceId,
        long speedSnapshot,
        ModelTime arrivalDue)
    {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        if (anchorOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(anchorOffset),
                "Traversal anchor offset cannot be negative.");
        }

        SpatialIdentifier.Require(targetPlaceId, nameof(targetPlaceId));
        if (speedSnapshot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedSnapshot), "Traversal speed must be positive.");
        }

        if (arrivalDue <= anchorTime)
        {
            throw new ArgumentOutOfRangeException(nameof(arrivalDue), "Arrival must be later than traversal start.");
        }

        PassageId = passageId;
        AnchorOffset = anchorOffset;
        AnchorTime = anchorTime;
        TargetPlaceId = targetPlaceId;
        SpeedSnapshot = speedSnapshot;
        ArrivalDue = arrivalDue;
    }

    public PassageId PassageId { get; }

    public long AnchorOffset { get; }

    public ModelTime AnchorTime { get; }

    public PlaceId TargetPlaceId { get; }

    public long SpeedSnapshot { get; }

    public ModelTime ArrivalDue { get; }
}

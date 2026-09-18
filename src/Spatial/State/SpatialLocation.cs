using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Base value for one entity's exclusive objective location.</summary>
[DurableType("DramaBoard.Spatial.SpatialLocation", 1)]
public abstract partial class SpatialLocation : IDurableObject {
    private protected SpatialLocation() {
    }
}

/// <summary>Places an entity at one stable semantic locality.</summary>
[DurableType("DramaBoard.Spatial.AtPlaceLocation", 1)]
public sealed partial class AtPlaceLocation : SpatialLocation, IEquatable<AtPlaceLocation> {
    [DurableField(1)] private PlaceId _placeId;
    public AtPlaceLocation(PlaceId placeId) {
        SpatialIdentifier.Require(placeId, nameof(placeId));
        _placeId = placeId;
    }

    public PlaceId PlaceId => _placeId;
    public bool Equals(AtPlaceLocation? other) => other is not null && PlaceId == other.PlaceId;
    public override bool Equals(object? obj) => Equals(obj as AtPlaceLocation);
    public override int GetHashCode() => PlaceId.GetHashCode();
    public static bool operator ==(AtPlaceLocation? left, AtPlaceLocation? right) => Equals(left, right);
    public static bool operator !=(AtPlaceLocation? left, AtPlaceLocation? right) => !Equals(left, right);
}

/// <summary>Stores one immutable anchored movement segment toward a passage endpoint.</summary>
[DurableType("DramaBoard.Spatial.TraversingLocation", 1)]
public sealed partial class TraversingLocation : SpatialLocation, IEquatable<TraversingLocation> {
    [DurableField(1)] private PassageId _passageId;
    [DurableField(2)] private long _anchorOffset;
    [DurableField(3)] private ModelTime _anchorTime;
    [DurableField(4)] private PlaceId _targetPlaceId;
    [DurableField(5)] private long _speedSnapshot;
    [DurableField(6)] private ModelTime _arrivalDue;
    public TraversingLocation(
        PassageId passageId,
        long anchorOffset,
        ModelTime anchorTime,
        PlaceId targetPlaceId,
        long speedSnapshot,
        ModelTime arrivalDue) {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        if (anchorOffset < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(anchorOffset),
                "Traversal anchor offset cannot be negative.");
        }

        SpatialIdentifier.Require(targetPlaceId, nameof(targetPlaceId));
        if (speedSnapshot <= 0) {
            throw new ArgumentOutOfRangeException(nameof(speedSnapshot), "Traversal speed must be positive.");
        }

        if (arrivalDue <= anchorTime) {
            throw new ArgumentOutOfRangeException(nameof(arrivalDue), "Arrival must be later than traversal start.");
        }

        _passageId = passageId; _anchorOffset = anchorOffset; _anchorTime = anchorTime;
        _targetPlaceId = targetPlaceId; _speedSnapshot = speedSnapshot; _arrivalDue = arrivalDue;
    }

    public PassageId PassageId => _passageId;

    public long AnchorOffset => _anchorOffset;

    public ModelTime AnchorTime => _anchorTime;

    public PlaceId TargetPlaceId => _targetPlaceId;

    public long SpeedSnapshot => _speedSnapshot;

    public ModelTime ArrivalDue => _arrivalDue;
    public bool Equals(TraversingLocation? other) => other is not null && PassageId == other.PassageId && AnchorOffset == other.AnchorOffset && AnchorTime == other.AnchorTime && TargetPlaceId == other.TargetPlaceId && SpeedSnapshot == other.SpeedSnapshot && ArrivalDue == other.ArrivalDue;
    public override bool Equals(object? obj) => Equals(obj as TraversingLocation);
    public override int GetHashCode() => HashCode.Combine(PassageId, AnchorOffset, AnchorTime, TargetPlaceId, SpeedSnapshot, ArrivalDue);
    public static bool operator ==(TraversingLocation? left, TraversingLocation? right) => Equals(left, right);
    public static bool operator !=(TraversingLocation? left, TraversingLocation? right) => !Equals(left, right);
}

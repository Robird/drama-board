using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Base objective location projected at one requested model time.</summary>
public abstract record SpatialLocationView
{
    private protected SpatialLocationView()
    {
    }
}

public sealed record AtPlaceView(PlaceId PlaceId) : SpatialLocationView;

public sealed record TraversingView(
    PassageId PassageId,
    PlaceId FromPlaceId,
    PlaceId ToPlaceId,
    long Offset,
    long SpeedSnapshot,
    ModelTime ArrivalDue) : SpatialLocationView;

/// <summary>Describes one distinguishable incident Passage from a requested Place.</summary>
public sealed record PassageExit(
    PassageId PassageId,
    PlaceId DestinationPlaceId,
    bool EffectiveEntryAllowed,
    ModelDuration ExpectedDuration);

/// <summary>Describes one other entity currently projected on the same Passage.</summary>
public sealed record SamePassageRelation(
    EntityId OtherEntityId,
    long OtherOffset,
    bool IsCoTraveling);

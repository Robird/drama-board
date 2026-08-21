using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

internal static class SpatialMath
{
    internal static ModelDuration TravelDuration(long distance, long speed)
    {
        if (distance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distance), "Travel distance must be positive.");
        }

        if (speed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "Travel speed must be positive.");
        }

        long quotient = distance / speed;
        long remainder = distance % speed;
        return new ModelDuration(checked(quotient + (remainder == 0 ? 0 : 1)));
    }

    internal static ModelTime ArrivalDue(ModelTime startedAt, long distance, long speed) =>
        startedAt + TravelDuration(distance, speed);

    internal static long OffsetAt(
        PassageDefinition passage,
        TraversingLocation traversal,
        ModelTime at)
    {
        if (at < traversal.StartedAt || at > traversal.ArrivalDue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(at),
                "Location time must lie within the active traversal interval.");
        }

        bool fromA = traversal.FromPlaceId == passage.EndpointA;
        if (at == traversal.ArrivalDue)
        {
            return fromA ? passage.Length : 0;
        }

        long elapsed = (at - traversal.StartedAt).Ticks;
        Int128 advanced = (Int128)elapsed * traversal.SpeedSnapshot;
        if (advanced < 0 || advanced >= passage.Length)
        {
            throw new InvalidOperationException("Traversal offset is inconsistent with its arrival due time.");
        }

        long distance = checked((long)advanced);
        return fromA ? distance : checked(passage.Length - distance);
    }
}

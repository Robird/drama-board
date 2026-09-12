using System.Numerics;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

internal readonly record struct PassageContactCalculation(
    PassageContactKind Kind,
    ModelTime Due);

internal static class PassageContactCalculator
{
    internal static bool TryCalculate(
        GraphDefinition definition,
        GraphSpatialState state,
        PassageContactKey key,
        out PassageContactCalculation calculation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);
        calculation = default;

        if (!definition.Contains(key.PassageId) ||
            !TryResolveCurrentTraversal(
                state,
                key.PassageId,
                key.EntityA,
                key.MovementGenerationA,
                out TraversingLocation? traversalA) ||
            !TryResolveCurrentTraversal(
                state,
                key.PassageId,
                key.EntityB,
                key.MovementGenerationB,
                out TraversingLocation? traversalB))
        {
            return false;
        }

        PassageDefinition passage = definition.GetPassage(key.PassageId);
        BigInteger t0 = BigInteger.Max(
            traversalA!.AnchorTime.Ticks,
            traversalB!.AnchorTime.Ticks);
        if (!TryProjectAtWindowStart(passage, traversalA, t0, out MotionAtWindowStart motionA) ||
            !TryProjectAtWindowStart(passage, traversalB, t0, out MotionAtWindowStart motionB))
        {
            return false;
        }

        BigInteger denominator = motionA.Velocity - motionB.Velocity;
        if (denominator.IsZero)
        {
            return false;
        }

        BigInteger numerator = motionB.Position - motionA.Position;
        if (denominator.Sign < 0)
        {
            denominator = BigInteger.Negate(denominator);
            numerator = BigInteger.Negate(numerator);
        }

        if (numerator.Sign <= 0 ||
            numerator * motionA.Speed >= motionA.RemainingDistance * denominator ||
            numerator * motionB.Speed >= motionB.RemainingDistance * denominator)
        {
            return false;
        }

        BigInteger contactOffsetNumerator =
            motionA.Position * denominator + motionA.Velocity * numerator;
        if (contactOffsetNumerator.Sign <= 0 ||
            contactOffsetNumerator >= new BigInteger(passage.Length) * denominator)
        {
            return false;
        }

        BigInteger absoluteContactNumerator = t0 * denominator + numerator;
        // Offer interaction at the start of the tick containing the exact intersection.
        // Physical exits remain rounded up, so an interior contact precedes either arrival.
        if (!TryFloorModelTime(absoluteContactNumerator, denominator, out ModelTime due))
        {
            return false;
        }

        PassageContactKind kind = motionA.Velocity.Sign == motionB.Velocity.Sign
            ? PassageContactKind.Overtake
            : PassageContactKind.HeadOnMeeting;
        calculation = new PassageContactCalculation(kind, due);
        return true;
    }

    private static bool TryResolveCurrentTraversal(
        GraphSpatialState state,
        PassageId passageId,
        EntityId entityId,
        long movementGeneration,
        out TraversingLocation? traversal)
    {
        if (state.TryGetEntity(entityId, out SpatialEntity? entity) &&
            entity!.MovementGeneration == movementGeneration &&
            entity.Location is TraversingLocation current &&
            current.PassageId == passageId)
        {
            traversal = current;
            return true;
        }

        traversal = null;
        return false;
    }

    private static bool TryProjectAtWindowStart(
        PassageDefinition passage,
        TraversingLocation traversal,
        BigInteger t0,
        out MotionAtWindowStart motion)
    {
        BigInteger elapsed = t0 - traversal.AnchorTime.Ticks;
        bool targetsB = traversal.TargetPlaceId == passage.EndpointB;
        BigInteger distanceToTarget = targetsB
            ? new BigInteger(passage.Length) - traversal.AnchorOffset
            : traversal.AnchorOffset;
        BigInteger speed = traversal.SpeedSnapshot;
        BigInteger advanced = elapsed * speed;
        if (elapsed.Sign < 0 || advanced >= distanceToTarget)
        {
            motion = default;
            return false;
        }

        BigInteger velocity = targetsB ? speed : BigInteger.Negate(speed);
        motion = new MotionAtWindowStart(
            new BigInteger(traversal.AnchorOffset) + velocity * elapsed,
            velocity,
            speed,
            distanceToTarget - advanced);
        return true;
    }

    private static bool TryFloorModelTime(
        BigInteger numerator,
        BigInteger denominator,
        out ModelTime due)
    {
        BigInteger quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        if (remainder.Sign < 0)
        {
            quotient -= BigInteger.One;
        }

        if (quotient < long.MinValue || quotient > long.MaxValue)
        {
            due = default;
            return false;
        }

        due = new ModelTime((long)quotient);
        return true;
    }

    private readonly record struct MotionAtWindowStart(
        BigInteger Position,
        BigInteger Velocity,
        BigInteger Speed,
        BigInteger RemainingDistance);
}

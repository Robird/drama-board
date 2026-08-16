using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal readonly record struct PhysicsVector(double X, double Y)
{
    public double LengthSquared => Dot(this);

    public double Dot(PhysicsVector other) => (X * other.X) + (Y * other.Y);

    public PhysicsVector Normalized()
    {
        double length = Math.Sqrt(LengthSquared);
        return this / length;
    }

    public static PhysicsVector operator +(PhysicsVector left, PhysicsVector right) =>
        new(left.X + right.X, left.Y + right.Y);

    public static PhysicsVector operator -(PhysicsVector left, PhysicsVector right) =>
        new(left.X - right.X, left.Y - right.Y);

    public static PhysicsVector operator *(PhysicsVector vector, double scalar) =>
        new(vector.X * scalar, vector.Y * scalar);

    public static PhysicsVector operator /(PhysicsVector vector, double scalar) =>
        new(vector.X / scalar, vector.Y / scalar);
}

internal sealed record BouncingBall(
    int Id,
    double Radius,
    double Mass,
    PhysicsVector PositionAtReference,
    PhysicsVector Velocity);

internal sealed record BouncingWorld
{
    public BouncingWorld(
        double width,
        double height,
        ModelTime referenceTime,
        IEnumerable<BouncingBall> balls,
        long generation = 0)
    {
        Width = width;
        Height = height;
        ReferenceTime = referenceTime;
        Balls = Array.AsReadOnly(balls.OrderBy(ball => ball.Id).ToArray());
        Generation = generation;
    }

    public double Width { get; }

    public double Height { get; }

    public ModelTime ReferenceTime { get; }

    public IReadOnlyList<BouncingBall> Balls { get; }

    public long Generation { get; }

    public PhysicsVector PositionAt(BouncingBall ball, ModelTime time)
    {
        double elapsedSeconds = (time - ReferenceTime).Ticks / 1_000.0;
        return ball.PositionAtReference + (ball.Velocity * elapsedSeconds);
    }
}

internal enum CollisionKind
{
    BallBall,
    LeftWall,
    RightWall,
    BottomWall,
    TopWall,
}

internal sealed record CollisionCandidatePayload(
    CollisionKind Kind,
    int FirstBallId,
    int? SecondBallId,
    ModelTime ForecastAt,
    double SecondsToImpact);

internal sealed record CollisionEventPayload(CollisionKind Kind, int FirstBallId, int? SecondBallId);

internal sealed class BouncingSystem : ISimSystem<BouncingWorld, CollisionCandidatePayload, CollisionEventPayload>
{
    private const double TimeEpsilon = 1e-12;
    private const long SourceId = 1;

    public IReadOnlyList<EventCandidate<CollisionCandidatePayload>> ForecastNext(BouncingWorld world, ModelTime now)
    {
        CollisionForecast? earliest = ForecastCollisions(world, now)
            .OrderBy(forecast => forecast.SecondsToImpact)
            .ThenBy(forecast => forecast.Kind)
            .ThenBy(forecast => forecast.FirstBallId)
            .ThenBy(forecast => forecast.SecondBallId)
            .FirstOrDefault();

        if (earliest is null)
        {
            return [];
        }

        long ticksToImpact = checked((long)Math.Ceiling(earliest.SecondsToImpact * 1_000.0));
        ModelTime due = now + ModelDuration.FromMilliseconds(ticksToImpact);
        var payload = new CollisionCandidatePayload(
            earliest.Kind,
            earliest.FirstBallId,
            earliest.SecondBallId,
            now,
            earliest.SecondsToImpact);

        return
        [
            new EventCandidate<CollisionCandidatePayload>(
                new EventCandidateId(checked(world.Generation + 1)),
                due,
                SourceId,
                world.Generation,
                payload),
        ];
    }

    public ResolveResult<BouncingWorld, CollisionEventPayload> Resolve(
        BouncingWorld world,
        EventCandidate<CollisionCandidatePayload> candidate)
    {
        CollisionCandidatePayload collision = candidate.Payload;
        double quantizedSeconds = (candidate.Due - collision.ForecastAt).Ticks / 1_000.0;
        double remainderSeconds = Math.Max(0.0, quantizedSeconds - collision.SecondsToImpact);
        Dictionary<int, PhysicsVector> impactPositions = world.Balls.ToDictionary(
            ball => ball.Id,
            ball => world.PositionAt(ball, collision.ForecastAt) + (ball.Velocity * collision.SecondsToImpact));
        Dictionary<int, PhysicsVector> nextVelocities = world.Balls.ToDictionary(ball => ball.Id, ball => ball.Velocity);

        if (collision.Kind == CollisionKind.BallBall)
        {
            ResolveBallCollision(world, collision, impactPositions, nextVelocities);
        }
        else
        {
            ResolveWallCollision(collision, nextVelocities);
        }

        BouncingBall[] nextBalls = world.Balls
            .Select(ball =>
            {
                PhysicsVector velocity = nextVelocities[ball.Id];
                PhysicsVector positionAtDue = impactPositions[ball.Id] + (velocity * remainderSeconds);
                return ball with { PositionAtReference = positionAtDue, Velocity = velocity };
            })
            .ToArray();
        var nextWorld = new BouncingWorld(
            world.Width,
            world.Height,
            candidate.Due,
            nextBalls,
            checked(world.Generation + 1));
        var eventPayload = new CollisionEventPayload(
            collision.Kind,
            collision.FirstBallId,
            collision.SecondBallId);

        return new ResolveResult<BouncingWorld, CollisionEventPayload>(
            nextWorld,
            [new UncommittedDomainEvent<CollisionEventPayload>("Physics.Collision", eventPayload)]);
    }

    private static IEnumerable<CollisionForecast> ForecastCollisions(BouncingWorld world, ModelTime now)
    {
        for (int firstIndex = 0; firstIndex < world.Balls.Count; firstIndex++)
        {
            BouncingBall first = world.Balls[firstIndex];
            PhysicsVector firstPosition = world.PositionAt(first, now);

            foreach (CollisionForecast wallCollision in ForecastWallCollisions(world, first, firstPosition))
            {
                yield return wallCollision;
            }

            for (int secondIndex = firstIndex + 1; secondIndex < world.Balls.Count; secondIndex++)
            {
                BouncingBall second = world.Balls[secondIndex];
                PhysicsVector secondPosition = world.PositionAt(second, now);
                CollisionForecast? collision = ForecastBallCollision(first, firstPosition, second, secondPosition);
                if (collision is not null)
                {
                    yield return collision;
                }
            }
        }
    }

    private static IEnumerable<CollisionForecast> ForecastWallCollisions(
        BouncingWorld world,
        BouncingBall ball,
        PhysicsVector position)
    {
        if (ball.Velocity.X < -TimeEpsilon)
        {
            double seconds = (ball.Radius - position.X) / ball.Velocity.X;
            if (seconds > TimeEpsilon)
            {
                yield return new CollisionForecast(CollisionKind.LeftWall, ball.Id, null, seconds);
            }
        }
        else if (ball.Velocity.X > TimeEpsilon)
        {
            double seconds = (world.Width - ball.Radius - position.X) / ball.Velocity.X;
            if (seconds > TimeEpsilon)
            {
                yield return new CollisionForecast(CollisionKind.RightWall, ball.Id, null, seconds);
            }
        }

        if (ball.Velocity.Y < -TimeEpsilon)
        {
            double seconds = (ball.Radius - position.Y) / ball.Velocity.Y;
            if (seconds > TimeEpsilon)
            {
                yield return new CollisionForecast(CollisionKind.BottomWall, ball.Id, null, seconds);
            }
        }
        else if (ball.Velocity.Y > TimeEpsilon)
        {
            double seconds = (world.Height - ball.Radius - position.Y) / ball.Velocity.Y;
            if (seconds > TimeEpsilon)
            {
                yield return new CollisionForecast(CollisionKind.TopWall, ball.Id, null, seconds);
            }
        }
    }

    private static CollisionForecast? ForecastBallCollision(
        BouncingBall first,
        PhysicsVector firstPosition,
        BouncingBall second,
        PhysicsVector secondPosition)
    {
        PhysicsVector offset = secondPosition - firstPosition;
        PhysicsVector relativeVelocity = second.Velocity - first.Velocity;
        double a = relativeVelocity.LengthSquared;
        double b = 2.0 * offset.Dot(relativeVelocity);
        double radiusSum = first.Radius + second.Radius;
        double c = offset.LengthSquared - (radiusSum * radiusSum);

        if (a <= TimeEpsilon || b >= 0.0)
        {
            return null;
        }

        double discriminant = (b * b) - (4.0 * a * c);
        if (discriminant < 0.0)
        {
            return null;
        }

        double seconds = (-b - Math.Sqrt(discriminant)) / (2.0 * a);
        return seconds > TimeEpsilon
            ? new CollisionForecast(CollisionKind.BallBall, first.Id, second.Id, seconds)
            : null;
    }

    private static void ResolveBallCollision(
        BouncingWorld world,
        CollisionCandidatePayload collision,
        IReadOnlyDictionary<int, PhysicsVector> impactPositions,
        IDictionary<int, PhysicsVector> nextVelocities)
    {
        BouncingBall first = world.Balls.Single(ball => ball.Id == collision.FirstBallId);
        BouncingBall second = world.Balls.Single(ball => ball.Id == collision.SecondBallId);
        PhysicsVector normal = (impactPositions[second.Id] - impactPositions[first.Id]).Normalized();
        double relativeNormalSpeed = (first.Velocity - second.Velocity).Dot(normal);
        double impulse = (2.0 * relativeNormalSpeed) / ((1.0 / first.Mass) + (1.0 / second.Mass));

        nextVelocities[first.Id] = first.Velocity - (normal * (impulse / first.Mass));
        nextVelocities[second.Id] = second.Velocity + (normal * (impulse / second.Mass));
    }

    private static void ResolveWallCollision(
        CollisionCandidatePayload collision,
        IDictionary<int, PhysicsVector> nextVelocities)
    {
        PhysicsVector velocity = nextVelocities[collision.FirstBallId];
        nextVelocities[collision.FirstBallId] = collision.Kind switch
        {
            CollisionKind.LeftWall or CollisionKind.RightWall => velocity with { X = -velocity.X },
            CollisionKind.BottomWall or CollisionKind.TopWall => velocity with { Y = -velocity.Y },
            _ => throw new InvalidOperationException("Expected a wall collision."),
        };
    }

    private sealed record CollisionForecast(
        CollisionKind Kind,
        int FirstBallId,
        int? SecondBallId,
        double SecondsToImpact);
}

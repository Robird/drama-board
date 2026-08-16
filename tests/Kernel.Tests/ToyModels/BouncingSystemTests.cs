using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class BouncingSystemTests
{
    private const double ConservationTolerance = 1e-9;

    [Fact]
    public void Run_TwoBallsCollideHeadOn_QuantizesUpAndExchangesVelocities()
    {
        BouncingWorld initialWorld = CreateWorld(
            width: 100,
            height: 20,
            new BouncingBall(1, 1, 1, new PhysicsVector(10, 10), new PhysicsVector(3, 0)),
            new BouncingBall(2, 1, 1, new PhysicsVector(20, 10), new PhysicsVector(0, 0)));

        (SimulationRunResult<BouncingWorld, CollisionEventPayload> result, InMemoryJournal<CollisionEventPayload> journal) =
            Run(initialWorld, AtSecond(3));

        Assert.Single(journal.Events);
        Assert.Equal(new ModelTime(2_667), journal.Events[0].Timestamp.ModelTime);
        Assert.Equal(CollisionKind.BallBall, journal.Events[0].Payload.Kind);
        Assert.Equal(1, journal.Events[0].Payload.FirstBallId);
        Assert.Equal(2, journal.Events[0].Payload.SecondBallId);
        AssertVector(new PhysicsVector(0, 0), Ball(result.World, 1).Velocity);
        AssertVector(new PhysicsVector(3, 0), Ball(result.World, 2).Velocity);
        AssertVector(new PhysicsVector(18, 10), Ball(result.World, 1).PositionAtReference);
        AssertVector(new PhysicsVector(20.001, 10), Ball(result.World, 2).PositionAtReference);
    }

    [Fact]
    public void Run_FiveBallsAcrossFourCollisions_ConservesEnergyAndMomentum()
    {
        BouncingWorld initialWorld = CreateWorld(
            width: 100,
            height: 20,
            new BouncingBall(1, 1, 1, new PhysicsVector(10, 10), new PhysicsVector(5, 0)),
            new BouncingBall(2, 1, 1, new PhysicsVector(14, 10), new PhysicsVector(0, 0)),
            new BouncingBall(3, 1, 1, new PhysicsVector(20, 10), new PhysicsVector(0, 0)),
            new BouncingBall(4, 1, 1, new PhysicsVector(28, 10), new PhysicsVector(0, 0)),
            new BouncingBall(5, 1, 1, new PhysicsVector(38, 10), new PhysicsVector(0, 0)));
        double initialEnergy = KineticEnergy(initialWorld);
        PhysicsVector initialMomentum = Momentum(initialWorld);

        (SimulationRunResult<BouncingWorld, CollisionEventPayload> result, InMemoryJournal<CollisionEventPayload> journal) =
            Run(initialWorld, AtSecond(5));

        Assert.Equal(4, journal.Events.Count);
        AssertClose(initialEnergy, KineticEnergy(result.World));
        AssertVector(initialMomentum, Momentum(result.World));
    }

    [Fact]
    public void Run_SameInitialWorldTwice_ProducesIdenticalJournal()
    {
        BouncingWorld initialWorld = CreateWorld(
            width: 20,
            height: 12,
            new BouncingBall(1, 0.4, 1, new PhysicsVector(1, 1.5), new PhysicsVector(1.7, 0)),
            new BouncingBall(2, 0.4, 1, new PhysicsVector(4, 3.5), new PhysicsVector(1.9, 0)),
            new BouncingBall(3, 0.4, 1, new PhysicsVector(7, 5.5), new PhysicsVector(2.1, 0)),
            new BouncingBall(4, 0.4, 1, new PhysicsVector(10, 7.5), new PhysicsVector(2.3, 0)),
            new BouncingBall(5, 0.4, 1, new PhysicsVector(13, 9.5), new PhysicsVector(2.5, 0)));

        (_, InMemoryJournal<CollisionEventPayload> first) = Run(initialWorld, AtSecond(100));
        (_, InMemoryJournal<CollisionEventPayload> second) = Run(initialWorld, AtSecond(100));

        Assert.True(first.Events.Count >= 30, $"Expected dozens of collisions, but observed {first.Events.Count}.");
        Assert.Equal(
            Snapshot(first),
            Snapshot(second));
    }

    [Fact]
    public void Run_StationaryBallWithinBoundary_CompletesWithNoEvents()
    {
        BouncingWorld initialWorld = CreateWorld(
            width: 20,
            height: 12,
            new BouncingBall(1, 1, 1, new PhysicsVector(5, 5), new PhysicsVector(0, 0)));

        (SimulationRunResult<BouncingWorld, CollisionEventPayload> result, InMemoryJournal<CollisionEventPayload> journal) =
            Run(initialWorld, AtSecond(100));

        Assert.Empty(journal.Events);
        Assert.Equal(ModelTime.Zero, result.CurrentTime);
        Assert.Equal(0, result.ResolvedCandidateCount);
    }

    [Fact]
    public void Run_FiveBallWorld_FirstCollisionPayloadContainsOnlyAffectedBallsAndThirteenScalars()
    {
        BouncingWorld initialWorld = CreateWorld(
            width: 100,
            height: 20,
            new BouncingBall(1, 1, 1, new PhysicsVector(10, 10), new PhysicsVector(5, 0)),
            new BouncingBall(2, 1, 1, new PhysicsVector(14, 10), new PhysicsVector(0, 0)),
            new BouncingBall(3, 1, 1, new PhysicsVector(20, 10), new PhysicsVector(0, 0)),
            new BouncingBall(4, 1, 1, new PhysicsVector(28, 10), new PhysicsVector(0, 0)),
            new BouncingBall(5, 1, 1, new PhysicsVector(38, 10), new PhysicsVector(0, 0)));

        (SimulationRunResult<BouncingWorld, CollisionEventPayload> result, InMemoryJournal<CollisionEventPayload> journal) =
            Run(initialWorld, new ModelTime(500));
        CollisionEventPayload payload = Assert.Single(journal.Events).Payload;
        BouncingWorld replayed = journal.Events.Aggregate(initialWorld, new BouncingReducer().Apply);

        Assert.Equal([1, 2], payload.BallResolutions.Select(resolution => resolution.BallId));
        Assert.Equal(13, 3 + (payload.BallResolutions.Count * 5));
        AssertWorldEqual(result.World, replayed);
    }

    [Fact]
    public void Run_MultipleCollisions_ReplaysJournalToFinalWorld()
    {
        BouncingWorld initialWorld = CreateWorld(
            width: 100,
            height: 20,
            new BouncingBall(1, 1, 1, new PhysicsVector(10, 10), new PhysicsVector(5, 0)),
            new BouncingBall(2, 1, 1, new PhysicsVector(14, 10), new PhysicsVector(0, 0)),
            new BouncingBall(3, 1, 1, new PhysicsVector(20, 10), new PhysicsVector(0, 0)),
            new BouncingBall(4, 1, 1, new PhysicsVector(28, 10), new PhysicsVector(0, 0)),
            new BouncingBall(5, 1, 1, new PhysicsVector(38, 10), new PhysicsVector(0, 0)));

        (SimulationRunResult<BouncingWorld, CollisionEventPayload> result, InMemoryJournal<CollisionEventPayload> journal) =
            Run(initialWorld, AtSecond(5));
        BouncingWorld replayed = journal.Events.Aggregate(initialWorld, new BouncingReducer().Apply);

        Assert.Equal(4, journal.Events.Count);
        AssertWorldEqual(result.World, replayed);
    }

    private static BouncingWorld CreateWorld(double width, double height, params BouncingBall[] balls) =>
        new(width, height, balls);

    private static (SimulationRunResult<BouncingWorld, CollisionEventPayload>, InMemoryJournal<CollisionEventPayload>) Run(
        BouncingWorld initialWorld,
        ModelTime until)
    {
        var loop = new SimulationLoop<BouncingWorld, CollisionCandidatePayload, CollisionEventPayload>(
            [new BouncingSystem()],
            new BouncingReducer());
        var journal = new InMemoryJournal<CollisionEventPayload>();
        SimulationRunResult<BouncingWorld, CollisionEventPayload> result = loop.Run(
            initialWorld,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            until,
            journal);
        return (result, journal);
    }

    private static BouncingBall Ball(BouncingWorld world, int id) => world.Balls.Single(ball => ball.Id == id);

    private static double KineticEnergy(BouncingWorld world) =>
        world.Balls.Sum(ball => 0.5 * ball.Mass * ball.Velocity.LengthSquared);

    private static PhysicsVector Momentum(BouncingWorld world) =>
        world.Balls.Aggregate(
            new PhysicsVector(0, 0),
            (total, ball) => total + (ball.Velocity * ball.Mass));

    private static void AssertVector(PhysicsVector expected, PhysicsVector actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0.0, ConservationTolerance);

    private static (int EventIndex, LogicalTimestamp Timestamp, EventKind Kind, CollisionKind CollisionKind, int FirstBallId, int? SecondBallId, BouncingBallResolution Resolution)[] Snapshot(
        InMemoryJournal<CollisionEventPayload> journal) =>
    [
        .. journal.Events.SelectMany((domainEvent, eventIndex) =>
            domainEvent.Payload.BallResolutions.Select(resolution =>
                (eventIndex,
                 domainEvent.Timestamp,
                 domainEvent.Kind,
                 domainEvent.Payload.Kind,
                 domainEvent.Payload.FirstBallId,
                 domainEvent.Payload.SecondBallId,
                 resolution))),
    ];

    private static void AssertWorldEqual(BouncingWorld expected, BouncingWorld actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.Balls.ToArray(), actual.Balls.ToArray());
    }

    private static ModelTime AtSecond(long seconds) => ModelTime.Zero + ModelDuration.FromSeconds(seconds);
}

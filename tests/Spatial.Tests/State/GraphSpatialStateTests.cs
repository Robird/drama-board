using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.State;

public sealed class GraphSpatialStateTests
{
    [Fact]
    public void Create_CanonicalizesEntitiesAtKnownPlacesWithZeroMovementGeneration()
    {
        GraphDefinition definition = GraphTestWorld.Definition(
            places: [GraphTestWorld.A, GraphTestWorld.B],
            passages: []);

        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("z", GraphTestWorld.B),
            ("a", GraphTestWorld.A));

        Assert.Equal([new EntityId("a"), new EntityId("z")], state.Entities.Select(value => value.Id));
        Assert.All(state.Entities, value => Assert.Equal(0, value.MovementGeneration));
        Assert.Equal(GraphTestWorld.A, Assert.IsType<AtPlaceLocation>(state.Entities[0].Location).PlaceId);
        Assert.Empty(state.PassageEntryAccessOverrides);
        Assert.Empty(state.ScheduledPassageEntryChanges);
    }

    [Fact]
    public void Create_RejectsDuplicateEntityAndUnknownPlace()
    {
        GraphDefinition definition = GraphTestWorld.Definition(
            places: [GraphTestWorld.A, GraphTestWorld.B],
            passages: []);

        Assert.Throws<InvalidOperationException>(() => GraphTestWorld.State(
            definition,
            ("actor", GraphTestWorld.A),
            ("actor", GraphTestWorld.B)));
        Assert.Throws<ArgumentException>(() => GraphTestWorld.State(
            definition,
            ("actor", GraphTestWorld.C)));
    }

    [Fact]
    public void LocationValues_RejectImpossibleSnapshots()
    {
        Assert.Throws<ArgumentException>(() => new AtPlaceLocation(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialEntity(
            new EntityId("actor"),
            movementGeneration: -1,
            new AtPlaceLocation(GraphTestWorld.A)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraversingLocation(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.B,
            GraphTestWorld.Time(10),
            speedSnapshot: 0,
            GraphTestWorld.Time(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraversingLocation(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.B,
            GraphTestWorld.Time(10),
            speedSnapshot: 1,
            GraphTestWorld.Time(10)));
    }
}

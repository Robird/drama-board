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
        Assert.Empty(state.ConsumedContacts);
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
            anchorOffset: 0,
            GraphTestWorld.Time(10),
            GraphTestWorld.B,
            speedSnapshot: 0,
            GraphTestWorld.Time(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 0,
            GraphTestWorld.Time(10),
            GraphTestWorld.B,
            speedSnapshot: 1,
            GraphTestWorld.Time(10)));
    }

    [Fact]
    public void Validator_RejectsMalformedAnchoredTraversalSnapshots()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(definition, ("actor", GraphTestWorld.A));
        var actor = new EntityId("actor");

        AssertInvalidTraversal(new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 11,
            GraphTestWorld.Time(0),
            GraphTestWorld.A,
            speedSnapshot: 1,
            GraphTestWorld.Time(11)));
        AssertInvalidTraversal(new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 0,
            GraphTestWorld.Time(0),
            GraphTestWorld.C,
            speedSnapshot: 1,
            GraphTestWorld.Time(10)));
        AssertInvalidTraversal(new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 0,
            GraphTestWorld.Time(0),
            GraphTestWorld.B,
            speedSnapshot: 3,
            GraphTestWorld.Time(3)));

        void AssertInvalidTraversal(TraversingLocation traversal)
        {
            GraphSpatialState malformed = state.Rebuild(
                entities: [new SpatialEntity(actor, movementGeneration: 1, traversal)]);
            Assert.Throws<InvalidOperationException>(() =>
                GraphSpatialStateValidator.ValidateComplete(definition, malformed));
        }
    }
}

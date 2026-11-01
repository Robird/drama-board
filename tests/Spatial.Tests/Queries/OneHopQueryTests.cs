using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Queries;

public sealed class OneHopQueryTests {
    private const ulong Sound = PlaceRelationCapabilities.Sound;
    private const ulong Other = 1UL << 63;

    [Fact]
    public void Query_UsesUndirectedSingleHopRelationsWithoutMovementConnectivity() {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C, GraphTestWorld.D],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.D)],
            [new(GraphTestWorld.A, GraphTestWorld.B, Sound), new(GraphTestWorld.B, GraphTestWorld.C, Sound)]);
        GraphSpatialState state = GraphTestWorld.State(definition,
            ("z-self", GraphTestWorld.A), ("a-object", GraphTestWorld.B),
            ("c-second-hop", GraphTestWorld.C), ("d-movement-neighbor", GraphTestWorld.D),
            ("b-local", GraphTestWorld.A));
        var queries = new SpatialQueries(definition);

        Assert.Equal([new EntityId("a-object"), new EntityId("b-local"), new EntityId("z-self")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.A, Sound));
        Assert.Equal([new EntityId("a-object"), new EntityId("b-local"), new EntityId("c-second-hop"), new EntityId("z-self")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.B, Sound));
        Assert.Equal([new EntityId("a-object"), new EntityId("c-second-hop")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.C, Sound));
        Assert.Equal([new EntityId("d-movement-neighbor")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.D, Sound));
    }

    [Fact]
    public void Query_RequiresEveryBitAndPreservesHighestBitMeaning() {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C, GraphTestWorld.D], [],
            [new(GraphTestWorld.A, GraphTestWorld.B, Sound | Other),
             new(GraphTestWorld.A, GraphTestWorld.C, Sound), new(GraphTestWorld.A, GraphTestWorld.D, 0)]);
        GraphSpatialState state = GraphTestWorld.State(definition,
            ("a", GraphTestWorld.A), ("b", GraphTestWorld.B), ("c", GraphTestWorld.C), ("d", GraphTestWorld.D));
        var queries = new SpatialQueries(definition);

        Assert.Equal([new EntityId("a"), new EntityId("b"), new EntityId("c")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.A, Sound));
        Assert.Equal([new EntityId("a"), new EntityId("b")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.A, Sound | Other));
        Assert.Equal([new EntityId("a"), new EntityId("b")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.A, Other));
        Assert.Equal([new EntityId("a")],
            queries.GetEntitiesInOneHop(state, GraphTestWorld.A, 1UL << 37));
    }

    [Fact]
    public void Query_DistinguishesEmptyAndLocalResultsFromInvalidArguments() {
        GraphDefinition definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B], []);
        GraphSpatialState state = GraphTestWorld.State(definition, ("local", GraphTestWorld.A));
        var queries = new SpatialQueries(definition);

        Assert.Equal([new EntityId("local")], queries.GetEntitiesInOneHop(state, GraphTestWorld.A, Sound));
        Assert.Empty(queries.GetEntitiesInOneHop(state, GraphTestWorld.B, Sound));
        Assert.Throws<ArgumentOutOfRangeException>(() => queries.GetEntitiesInOneHop(state, GraphTestWorld.A, 0));
        Assert.Throws<KeyNotFoundException>(() => queries.GetEntitiesInOneHop(state, GraphTestWorld.C, Sound));
        Assert.Throws<ArgumentNullException>(() => queries.GetEntitiesInOneHop(null!, GraphTestWorld.A, Sound));
    }

    [Fact]
    public void Query_ExcludesTraversalUntilArrivalCommitsAndReturnsAnImmutableSnapshot() {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B)],
            [new(GraphTestWorld.A, GraphTestWorld.B, Sound)]);
        GraphSpatialState before = GraphTestWorld.State(definition,
            ("traveler", GraphTestWorld.A), ("waiting", GraphTestWorld.B));
        var queries = new SpatialQueries(definition);
        IReadOnlyList<EntityId> original = queries.GetEntitiesInOneHop(before, GraphTestWorld.A, Sound);
        var traveler = new EntityId("traveler");
        var reducer = new GraphSpatialReducer(definition);
        GraphSpatialState traveling = GraphTestWorld.Fold(reducer, before, GraphTestWorld.Instant(0),
            new SpatialPlanner(definition).TryStartTraversal(before, traveler, GraphTestWorld.Bridge, 1, GraphTestWorld.Time(0)));
        SpatialEntity[] entitiesBeforeQuery = [.. traveling.Entities];
        int hashBeforeQuery = traveling.GetHashCode();

        IReadOnlyList<EntityId> duringTravel = queries.GetEntitiesInOneHop(traveling, GraphTestWorld.A, Sound);

        Assert.Equal([new EntityId("waiting")], duringTravel);
        Assert.Equal(entitiesBeforeQuery, traveling.Entities);
        Assert.Equal(hashBeforeQuery, traveling.GetHashCode());
        Assert.Throws<NotSupportedException>(() => ((IList<EntityId>)duringTravel)[0] = traveler);
        Assert.Equal([traveler, new EntityId("waiting")], original);

        GraphSpatialState arrived = reducer.Apply(traveling, GraphTestWorld.Instant(10), new TraversalArrivedFact(traveler, 1));
        Assert.Equal([traveler, new EntityId("waiting")], queries.GetEntitiesInOneHop(arrived, GraphTestWorld.A, Sound));
        Assert.Equal([new EntityId("waiting")], duringTravel);
    }

    [Fact]
    public void Query_IsIndependentOfRelationAndEntityDeclarationOrder() {
        PlaceRelationDefinition[] relations =
            [new(GraphTestWorld.A, GraphTestWorld.C, Sound), new(GraphTestWorld.A, GraphTestWorld.B, Sound)];
        GraphDefinition first = GraphDefinition.Create([GraphTestWorld.C, GraphTestWorld.B, GraphTestWorld.A], [], relations);
        GraphDefinition second = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C], [], relations.Reverse());
        GraphSpatialState firstState = GraphTestWorld.State(first,
            ("z", GraphTestWorld.A), ("b", GraphTestWorld.B), ("a", GraphTestWorld.C));
        GraphSpatialState secondState = GraphTestWorld.State(second,
            ("a", GraphTestWorld.C), ("b", GraphTestWorld.B), ("z", GraphTestWorld.A));

        Assert.Equal(new SpatialQueries(first).GetEntitiesInOneHop(firstState, GraphTestWorld.A, Sound),
            new SpatialQueries(second).GetEntitiesInOneHop(secondState, GraphTestWorld.A, Sound));
    }
}

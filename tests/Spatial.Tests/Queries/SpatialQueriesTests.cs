using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Queries;

public sealed class SpatialQueriesTests
{
    [Fact]
    public void GetLocation_ProjectsCeilingTraversalAndKeepsBoundaryTraversingUntilArrivalCommits()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("traveler", GraphTestWorld.A),
            ("waiting", GraphTestWorld.B));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        var queries = new SpatialQueries(definition);
        var traveler = new EntityId("traveler");
        state = GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(0),
            planner.TryStartTraversal(state, traveler, GraphTestWorld.Bridge, speedSnapshot: 3, GraphTestWorld.Time(0)));

        Assert.Equal(0, Assert.IsType<TraversingView>(queries.GetLocation(state, traveler, GraphTestWorld.Time(0))).Offset);
        Assert.Equal(3, Assert.IsType<TraversingView>(queries.GetLocation(state, traveler, GraphTestWorld.Time(1))).Offset);
        Assert.Equal(6, Assert.IsType<TraversingView>(queries.GetLocation(state, traveler, GraphTestWorld.Time(2))).Offset);
        Assert.Equal(9, Assert.IsType<TraversingView>(queries.GetLocation(state, traveler, GraphTestWorld.Time(3))).Offset);
        TraversingView boundary = Assert.IsType<TraversingView>(
            queries.GetLocation(state, traveler, GraphTestWorld.Time(4)));
        Assert.Equal(10, boundary.Offset);
        Assert.Empty(queries.GetCoLocatedEntities(state, traveler));
        Assert.DoesNotContain(traveler, queries.GetCoLocatedEntities(state, new EntityId("waiting")));

        state = reducer.Apply(state, GraphTestWorld.Instant(4), new TraversalArrivedFact(traveler, 1));

        Assert.Equal(
            GraphTestWorld.B,
            Assert.IsType<AtPlaceView>(queries.GetLocation(state, traveler, GraphTestWorld.Time(4))).PlaceId);
        Assert.Equal([new EntityId("waiting")], queries.GetCoLocatedEntities(state, traveler));
    }

    [Fact]
    public void GetExits_ReturnsParallelOpenAndClosedDirectionsInStablePassageOrder()
    {
        PassageDefinition bridge = GraphTestWorld.Passage(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.B,
            length: 10,
            enterableFromA: true,
            enterableFromB: false);
        PassageDefinition ferry = GraphTestWorld.Passage(
            GraphTestWorld.Ferry,
            GraphTestWorld.A,
            GraphTestWorld.B,
            length: 5,
            enterableFromA: false,
            enterableFromB: true);
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.B, GraphTestWorld.A],
            [ferry, bridge]);
        GraphSpatialState state = GraphTestWorld.State(definition);
        var queries = new SpatialQueries(definition);

        IReadOnlyList<PassageExit> fromA = queries.GetExits(state, GraphTestWorld.A, speedSnapshot: 3);
        IReadOnlyList<PassageExit> fromB = queries.GetExits(state, GraphTestWorld.B, speedSnapshot: 3);

        Assert.Equal([GraphTestWorld.Bridge, GraphTestWorld.Ferry], fromA.Select(value => value.PassageId));
        Assert.Equal([true, false], fromA.Select(value => value.EffectiveEntryAllowed));
        Assert.Equal([4L, 2L], fromA.Select(value => value.ExpectedDuration.Ticks));
        Assert.All(fromA, value => Assert.Equal(GraphTestWorld.B, value.DestinationPlaceId));
        Assert.Equal([false, true], fromB.Select(value => value.EffectiveEntryAllowed));
        Assert.All(fromB, value => Assert.Equal(GraphTestWorld.A, value.DestinationPlaceId));
    }

    [Fact]
    public void CurrentRelations_AreDerivedFromExclusiveLocationAndFutureWorldline()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("target", GraphTestWorld.A),
            ("companion-z", GraphTestWorld.A),
            ("companion-a", GraphTestWorld.A),
            ("opposite", GraphTestWorld.B),
            ("waiting", GraphTestWorld.A));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        foreach (string id in new[] { "target", "companion-z", "companion-a", "opposite" })
        {
            state = GraphTestWorld.Fold(
                reducer,
                state,
                GraphTestWorld.Instant(0),
                planner.TryStartTraversal(
                    state,
                    new EntityId(id),
                    GraphTestWorld.Bridge,
                    speedSnapshot: 1,
                    GraphTestWorld.Time(0)));
        }

        var queries = new SpatialQueries(definition);
        var target = new EntityId("target");
        IReadOnlyList<SamePassageRelation> relations =
            queries.GetSamePassageRelations(state, target, GraphTestWorld.Time(5));

        Assert.Equal(
            [new EntityId("companion-a"), new EntityId("companion-z"), new EntityId("opposite")],
            relations.Select(value => value.OtherEntityId));
        Assert.Equal([true, true, false], relations.Select(value => value.IsCoTraveling));
        Assert.Equal([new EntityId("companion-a"), new EntityId("companion-z")],
            queries.GetCoTravelingEntities(state, target, GraphTestWorld.Time(5)));
        Assert.Empty(queries.GetCoLocatedEntities(state, target));
        Assert.DoesNotContain(target, queries.GetCoLocatedEntities(state, new EntityId("waiting")));
    }

    [Fact]
    public void Query_DistinguishesUnknownReferencesFromValidEmptyResults()
    {
        GraphDefinition definition = GraphDefinition.Create([GraphTestWorld.A], []);
        GraphSpatialState state = GraphTestWorld.State(definition, ("alone", GraphTestWorld.A));
        var queries = new SpatialQueries(definition);

        Assert.Empty(queries.GetExits(state, GraphTestWorld.A, speedSnapshot: 1));
        Assert.Empty(queries.GetCoLocatedEntities(state, new EntityId("alone")));
        Assert.Throws<KeyNotFoundException>(() =>
            queries.GetLocation(state, new EntityId("missing"), GraphTestWorld.Time(0)));
        Assert.Throws<KeyNotFoundException>(() =>
            queries.GetExits(state, GraphTestWorld.B, speedSnapshot: 1));
        Assert.Throws<KeyNotFoundException>(() =>
            queries.GetPassageEntryAccess(state, GraphTestWorld.Bridge));
    }
}

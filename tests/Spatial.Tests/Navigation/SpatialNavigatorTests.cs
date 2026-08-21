using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Navigation;

public sealed class SpatialNavigatorTests
{
    [Fact]
    public void FindRoute_UsesDirectionalAccessAndReturnsDistinctOutcomeCases()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(
                GraphTestWorld.Bridge,
                GraphTestWorld.A,
                GraphTestWorld.B,
                enterableFromA: true,
                enterableFromB: false)]);
        GraphSpatialState state = GraphTestWorld.State(definition);
        var navigator = new SpatialNavigator(definition);

        RouteFound found = Assert.IsType<RouteFound>(navigator.FindRoute(
            state,
            GraphTestWorld.A,
            GraphTestWorld.B,
            speedSnapshot: 3));
        Assert.Equal(4, found.TotalDuration.Ticks);
        Assert.Equal(
            new RouteLeg(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B),
            Assert.Single(found.Legs));
        Assert.IsType<NoRoute>(navigator.FindRoute(state, GraphTestWorld.B, GraphTestWorld.A, 3));
        Assert.IsType<AlreadyAtGoal>(navigator.FindRoute(state, GraphTestWorld.A, GraphTestWorld.A, 3));
        Assert.IsType<UnknownStart>(navigator.FindRoute(state, new PlaceId("unknown"), GraphTestWorld.A, 3));
        Assert.IsType<UnknownGoal>(navigator.FindRoute(state, GraphTestWorld.A, new PlaceId("unknown"), 3));
        Assert.IsType<InvalidSpeed>(navigator.FindRoute(state, GraphTestWorld.A, GraphTestWorld.B, 0));
    }

    [Fact]
    public void FindRoute_TieBreaksByFullOrdinalLegSequenceIndependentOfDefinitionPermutation()
    {
        var start = new PlaceId("start");
        var x = new PlaceId("x");
        var y = new PlaceId("y");
        var goal = new PlaceId("goal");
        PassageDefinition viaXFirst = GraphTestWorld.Passage(new PassageId("z-first"), start, x, length: 1);
        PassageDefinition viaXSecond = GraphTestWorld.Passage(new PassageId("a-second"), x, goal, length: 1);
        PassageDefinition viaYFirst = GraphTestWorld.Passage(new PassageId("a-first"), start, y, length: 1);
        PassageDefinition viaYSecond = GraphTestWorld.Passage(new PassageId("z-second"), y, goal, length: 1);

        GraphDefinition first = GraphDefinition.Create(
            [goal, y, x, start],
            [viaXFirst, viaXSecond, viaYFirst, viaYSecond]);
        GraphDefinition second = GraphDefinition.Create(
            [start, x, y, goal],
            [viaYSecond, viaYFirst, viaXSecond, viaXFirst]);

        RouteFound firstRoute = Route(first);
        RouteFound secondRoute = Route(second);
        PassageId[] expected = [new PassageId("a-first"), new PassageId("z-second")];
        Assert.Equal(expected, firstRoute.Legs.Select(value => value.PassageId));
        Assert.Equal(firstRoute, secondRoute);

        RouteFound Route(GraphDefinition definition)
        {
            GraphSpatialState state = GraphTestWorld.State(definition);
            return Assert.IsType<RouteFound>(
                new SpatialNavigator(definition).FindRoute(state, start, goal, speedSnapshot: 1));
        }
    }

    [Fact]
    public void FindRoute_UnrelatedOverflowingBranchDoesNotHideRepresentableGoal()
    {
        var start = new PlaceId("start");
        var goal = new PlaceId("goal");
        var x = new PlaceId("x");
        var deadEnd = new PlaceId("dead-end");
        GraphDefinition definition = GraphDefinition.Create(
            [start, goal, x, deadEnd],
            [
                GraphTestWorld.Passage(new PassageId("goal"), start, goal, length: 1),
                GraphTestWorld.Passage(new PassageId("huge"), start, x, length: long.MaxValue),
                GraphTestWorld.Passage(new PassageId("overflow"), x, deadEnd, length: 1),
            ]);

        RouteFound found = Assert.IsType<RouteFound>(new SpatialNavigator(definition).FindRoute(
            GraphTestWorld.State(definition), start, goal, speedSnapshot: 1));

        Assert.Equal(1, found.TotalDuration.Ticks);
        Assert.Equal(new PassageId("goal"), Assert.Single(found.Legs).PassageId);
    }

    [Fact]
    public void FindRoute_ReturnsCostOverflowWhenEveryTopologicalRouteOverflows()
    {
        var start = new PlaceId("start");
        var x = new PlaceId("x");
        var goal = new PlaceId("goal");
        GraphDefinition definition = GraphDefinition.Create(
            [start, x, goal],
            [
                GraphTestWorld.Passage(new PassageId("huge"), start, x, length: long.MaxValue),
                GraphTestWorld.Passage(new PassageId("last"), x, goal, length: 1),
            ]);

        Assert.IsType<CostOverflow>(new SpatialNavigator(definition).FindRoute(
            GraphTestWorld.State(definition), start, goal, speedSnapshot: 1));
    }

    [Fact]
    public void FindRoute_ReturnsNoRouteWhenOverflowingDeadEndIsUnrelatedToDisconnectedGoal()
    {
        var start = new PlaceId("start");
        var x = new PlaceId("x");
        var deadEnd = new PlaceId("dead-end");
        var goal = new PlaceId("goal");
        GraphDefinition definition = GraphDefinition.Create(
            [start, x, deadEnd, goal],
            [
                GraphTestWorld.Passage(new PassageId("huge"), start, x, length: long.MaxValue),
                GraphTestWorld.Passage(new PassageId("overflow"), x, deadEnd, length: 1),
            ]);

        Assert.IsType<NoRoute>(new SpatialNavigator(definition).FindRoute(
            GraphTestWorld.State(definition), start, goal, speedSnapshot: 1));
    }
}

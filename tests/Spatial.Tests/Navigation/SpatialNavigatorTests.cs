using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialNavigatorTests
{
    [Fact]
    public void FindNextStep_CellGoalAtStart_IsAlreadySatisfied()
    {
        SpatialDefinition definition = Definition([Map("a", width: 2, height: 1)]);
        CellRef start = Cell("a", 0, 0);

        PathSearchResult result = Find(definition, SpatialState.Create(definition), start, new CellGoal(start));

        var satisfied = Assert.IsType<PathSearchResult.AlreadySatisfied>(result);
        Assert.Equal(start, satisfied.SatisfiedGoal);
    }

    [Fact]
    public void FindNextStep_AnchorGoal_UsesEffectiveTargetMoveCost()
    {
        GridMapDefinition map = Map("a", width: 2, height: 1, stepTicks: 10);
        CellRef start = Cell("a", 0, 0);
        CellRef target = Cell("a", 1, 0);
        SpatialDefinition definition = Definition(
            [map],
            anchors: [new AnchorDefinition(new AnchorId("inn"), target)]);
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new CellStateChangedEvent(
                target,
                expectedOverride: null,
                resultingOverride: new CellOverride(moveCost: 3)));

        PathSearchResult.NextStep step = Step(Find(
            definition,
            state,
            start,
            new AnchorGoal(new AnchorId("inn"))));

        AssertOrthogonal(step, start, target, durationTicks: 30, totalCostTicks: 30);
    }

    [Fact]
    public void FindNextStep_DiagonalBehindBlockedOrthogonalNeighbors_IsUnreachable()
    {
        CellDefinition floor = Floor();
        CellDefinition wall = Floor(blocksMovement: true);
        GridMapDefinition map = Map(
            "a",
            width: 2,
            height: 2,
            cells: [floor, wall, wall, floor]);
        SpatialDefinition definition = Definition([map]);

        PathSearchResult result = Find(
            definition,
            SpatialState.Create(definition),
            Cell("a", 0, 0),
            new CellGoal(Cell("a", 1, 1)));

        Assert.IsType<PathSearchResult.Unreachable>(result);
    }

    [Fact]
    public void FindNextStep_DirectedPortal_HasIndependentCostAndNoImplicitReverse()
    {
        GridMapDefinition mapA = Map("a", width: 1, height: 1);
        GridMapDefinition mapB = Map(
            "b",
            width: 1,
            height: 1,
            cells: [Floor(moveCost: 999)]);
        PortalDefinition portal = Portal("p.ab", Cell("a", 0, 0), Cell("b", 0, 0), 7);
        SpatialDefinition definition = Definition([mapA, mapB], [portal]);
        SpatialState state = SpatialState.Create(definition);

        PathSearchResult.NextStep forward = Step(Find(
            definition,
            state,
            portal.From,
            new CellGoal(portal.To)));

        AssertPortal(forward, portal, durationTicks: 7, totalCostTicks: 7);
        Assert.IsType<PathSearchResult.Unreachable>(Find(
            definition,
            state,
            portal.To,
            new CellGoal(portal.From)));
    }

    [Fact]
    public void FindNextStep_DisabledPortal_IsExcluded()
    {
        PortalDefinition portal = Portal("p.ab", Cell("a", 0, 0), Cell("b", 0, 0), 7);
        SpatialDefinition definition = Definition(
            [Map("a", 1, 1), Map("b", 1, 1)],
            [portal]);
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new PortalStateChangedEvent(portal.Id, expectedOverride: null, resultingOverride: false));

        PathSearchResult result = Find(definition, state, portal.From, new CellGoal(portal.To));

        Assert.IsType<PathSearchResult.Unreachable>(result);
    }

    [Fact]
    public void FindNextStep_PortalShortcut_BeatsLocalManhattanRoute()
    {
        CellRef start = Cell("a", 0, 0);
        CellRef goal = Cell("a", 4, 0);
        PortalDefinition first = Portal("p.1", start, Cell("b", 0, 0), 3);
        PortalDefinition second = Portal("p.2", Cell("b", 0, 0), goal, 3);
        SpatialDefinition definition = Definition(
            [Map("a", 5, 1, stepTicks: 10), Map("b", 1, 1)],
            [first, second]);

        PathSearchResult.NextStep step = Step(Find(
            definition,
            SpatialState.Create(definition),
            start,
            new CellGoal(goal)));

        AssertPortal(step, first, durationTicks: 3, totalCostTicks: 6);
        Assert.Equal(goal, step.SelectedGoal);
    }

    [Fact]
    public void FindNextStep_EqualCostDiamond_KeepsFirstCanonicalPredecessor()
    {
        SpatialDefinition definition = Definition([Map("a", 3, 2, stepTicks: 1)]);
        CellRef start = Cell("a", 1, 1);
        CellRef north = Cell("a", 1, 0);
        CellRef goal = Cell("a", 2, 0);

        PathSearchResult.NextStep step = Step(Find(
            definition,
            SpatialState.Create(definition),
            start,
            new CellGoal(goal)));

        AssertOrthogonal(step, start, north, durationTicks: 1, totalCostTicks: 2);
        Assert.Equal(goal, step.SelectedGoal);
    }

    [Fact]
    public void FindNextStep_EqualCostOrdinaryAndPortal_KeepsOrdinaryFirstDiscovery()
    {
        CellRef start = Cell("a", 0, 0);
        CellRef goal = Cell("a", 1, 0);
        PortalDefinition portal = Portal("p.equal", start, goal, 10);
        SpatialDefinition definition = Definition([Map("a", 2, 1, stepTicks: 10)], [portal]);

        PathSearchResult.NextStep step = Step(Find(
            definition,
            SpatialState.Create(definition),
            start,
            new CellGoal(goal)));

        AssertOrthogonal(step, start, goal, durationTicks: 10, totalCostTicks: 10);
    }

    [Fact]
    public void FindNextStep_EqualCostZoneEndpoints_UseCellFrontierOrder()
    {
        CellRef west = Cell("a", 0, 0);
        CellRef start = Cell("a", 1, 0);
        CellRef east = Cell("a", 2, 0);
        SpatialDefinition definition = Definition(
            [Map("a", 3, 1, stepTicks: 10)],
            zones: [new ZoneDefinition(new ZoneId("exits"), [east, west])]);

        PathSearchResult.NextStep step = Step(Find(
            definition,
            SpatialState.Create(definition),
            start,
            new ZoneGoal(new ZoneId("exits"))));

        AssertOrthogonal(step, start, west, durationTicks: 10, totalCostTicks: 10);
        Assert.Equal(west, step.SelectedGoal);
    }

    [Fact]
    public void FindNextStep_EqualCostPortalGoals_UseMapIdRatherThanPortalEnqueueOrder()
    {
        CellRef start = Cell("source", 0, 0);
        CellRef goalA = Cell("a", 0, 0);
        CellRef goalB = Cell("b", 0, 0);
        PortalDefinition firstById = Portal("p.a", start, goalB, 5);
        PortalDefinition chosenByFrontier = Portal("p.z", start, goalA, 5);
        SpatialDefinition definition = Definition(
            [Map("source", 1, 1), Map("a", 1, 1), Map("b", 1, 1)],
            [firstById, chosenByFrontier],
            zones: [new ZoneDefinition(new ZoneId("destinations"), [goalB, goalA])]);

        PathSearchResult.NextStep step = Step(Find(
            definition,
            SpatialState.Create(definition),
            start,
            new ZoneGoal(new ZoneId("destinations"))));

        AssertPortal(step, chosenByFrontier, durationTicks: 5, totalCostTicks: 5);
        Assert.Equal(goalA, step.SelectedGoal);
    }

    [Fact]
    public void FindNextStep_OverflowingBranch_DoesNotHideRepresentableGoal()
    {
        CellRef start = Cell("source", 0, 0);
        CellRef overflowBranch = Cell("x", 0, 0);
        CellRef goal = Cell("g", 0, 0);
        PortalDefinition toOverflow = Portal("p.overflow", start, overflowBranch, long.MaxValue - 10);
        PortalDefinition toGoal = Portal("p.goal", start, goal, long.MaxValue - 5);
        SpatialDefinition definition = Definition(
            [Map("source", 1, 1), Map("x", 2, 1, stepTicks: 20), Map("g", 1, 1)],
            [toOverflow, toGoal]);

        PathSearchResult.NextStep step = Step(Find(
            definition,
            SpatialState.Create(definition),
            start,
            new CellGoal(goal)));

        AssertPortal(step, toGoal, long.MaxValue - 5, long.MaxValue - 5);
    }

    [Fact]
    public void FindNextStep_OnlyRouteOverflows_ReturnsCostOverflow()
    {
        CellRef start = Cell("source", 0, 0);
        CellRef branch = Cell("x", 0, 0);
        CellRef goal = Cell("x", 1, 0);
        PortalDefinition portal = Portal("p.overflow", start, branch, long.MaxValue - 10);
        SpatialDefinition definition = Definition(
            [Map("source", 1, 1), Map("x", 2, 1, stepTicks: 20)],
            [portal]);

        PathSearchResult result = Find(
            definition,
            SpatialState.Create(definition),
            start,
            new CellGoal(goal));

        Assert.IsType<PathSearchResult.CostOverflow>(result);
    }

    [Fact]
    public void FindNextStep_UnrelatedOverlongDeadEnd_DoesNotTurnDisconnectedGoalIntoOverflow()
    {
        CellRef start = Cell("source", 0, 0);
        CellRef branch = Cell("x", 0, 0);
        CellRef disconnectedGoal = Cell("goal", 0, 0);
        PortalDefinition portal = Portal("p.dead-end", start, branch, long.MaxValue - 10);
        SpatialDefinition definition = Definition(
            [
                Map("source", 1, 1),
                Map("x", 2, 1, stepTicks: 20),
                Map("goal", 1, 1),
            ],
            [portal]);

        PathSearchResult result = Find(
            definition,
            SpatialState.Create(definition),
            start,
            new CellGoal(disconnectedGoal));

        Assert.IsType<PathSearchResult.Unreachable>(result);
    }

    [Fact]
    public void FindNextStep_RepresentableZoneGoal_IsChosenBeforeOverlongZoneGoal()
    {
        CellRef start = Cell("source", 0, 0);
        CellRef overflowBranch = Cell("x", 0, 0);
        CellRef overlongGoal = Cell("x", 1, 0);
        CellRef representableGoal = Cell("g", 0, 0);
        PortalDefinition toOverflow = Portal("p.overflow", start, overflowBranch, long.MaxValue - 10);
        PortalDefinition toRepresentable = Portal("p.goal", start, representableGoal, long.MaxValue - 5);
        SpatialDefinition definition = Definition(
            [Map("source", 1, 1), Map("x", 2, 1, stepTicks: 20), Map("g", 1, 1)],
            [toOverflow, toRepresentable],
            zones:
            [
                new ZoneDefinition(
                    new ZoneId("mixed-goals"),
                    [overlongGoal, representableGoal]),
            ]);

        PathSearchResult.NextStep step = Step(Find(
            definition,
            SpatialState.Create(definition),
            start,
            new ZoneGoal(new ZoneId("mixed-goals"))));

        AssertPortal(step, toRepresentable, long.MaxValue - 5, long.MaxValue - 5);
        Assert.Equal(representableGoal, step.SelectedGoal);
    }

    [Fact]
    public void FindNextStep_DynamicMoveCost_ChangesTheShortestFirstStep()
    {
        SpatialDefinition definition = Definition([Map("a", 3, 2, stepTicks: 10)]);
        CellRef start = Cell("a", 0, 0);
        CellRef expensiveMiddle = Cell("a", 1, 0);
        CellRef south = Cell("a", 0, 1);
        CellRef goal = Cell("a", 2, 0);
        SpatialState initial = SpatialState.Create(definition);

        PathSearchResult.NextStep direct = Step(Find(definition, initial, start, new CellGoal(goal)));
        AssertOrthogonal(direct, start, expensiveMiddle, durationTicks: 10, totalCostTicks: 20);

        SpatialState changed = SpatialEventTestHarness.Apply(
            definition,
            initial,
            new CellStateChangedEvent(
                expensiveMiddle,
                expectedOverride: null,
                resultingOverride: new CellOverride(moveCost: 5)));
        PathSearchResult.NextStep detour = Step(Find(definition, changed, start, new CellGoal(goal)));

        AssertOrthogonal(detour, start, south, durationTicks: 10, totalCostTicks: 40);
    }

    [Fact]
    public void FindNextStep_InitiallyDisabledPortal_BecomesShortestWhenEnabledByOverride()
    {
        CellRef start = Cell("a", 0, 0);
        CellRef goal = Cell("a", 2, 0);
        var portal = new PortalDefinition(
            new PortalId("p.shortcut"),
            start,
            goal,
            new ModelDuration(1),
            initiallyEnabled: false);
        SpatialDefinition definition = Definition([Map("a", 3, 1, stepTicks: 10)], [portal]);
        SpatialState initial = SpatialState.Create(definition);

        PathSearchResult.NextStep disabled = Step(Find(definition, initial, start, new CellGoal(goal)));
        AssertOrthogonal(disabled, start, Cell("a", 1, 0), durationTicks: 10, totalCostTicks: 20);

        SpatialState enabledState = SpatialEventTestHarness.Apply(
            definition,
            initial,
            new PortalStateChangedEvent(portal.Id, expectedOverride: null, resultingOverride: true));
        PathSearchResult.NextStep enabled = Step(Find(
            definition,
            enabledState,
            start,
            new CellGoal(goal)));

        AssertPortal(enabled, portal, durationTicks: 1, totalCostTicks: 1);
    }

    [Fact]
    public void FindNextStep_InvalidStampStartAndGoalReferences_Throw()
    {
        SpatialDefinition definition = Definition([Map("a", 2, 1)]);
        SpatialDefinition other = Definition([Map("other", 1, 1)]);

        Assert.Throws<InvalidOperationException>(() => Find(
            definition,
            SpatialState.Create(other),
            Cell("a", 0, 0),
            new CellGoal(Cell("a", 1, 0))));
        Assert.Throws<InvalidOperationException>(() => Find(
            definition,
            SpatialState.Create(definition),
            Cell("a", 2, 0),
            new CellGoal(Cell("a", 1, 0))));
        Assert.Throws<InvalidOperationException>(() => Find(
            definition,
            SpatialState.Create(definition),
            Cell("a", 0, 0),
            new AnchorGoal(new AnchorId("missing"))));
    }

    private static PathSearchResult Find(
        SpatialDefinition definition,
        SpatialState state,
        CellRef start,
        MoveGoal goal) => SpatialNavigator.FindNextStep(definition, state, start, goal);

    private static PathSearchResult.NextStep Step(PathSearchResult result) =>
        Assert.IsType<PathSearchResult.NextStep>(result);

    private static void AssertOrthogonal(
        PathSearchResult.NextStep step,
        CellRef from,
        CellRef to,
        long durationTicks,
        long totalCostTicks)
    {
        Assert.Equal(SpatialEdgeKind.Orthogonal, step.Edge.EdgeKind);
        Assert.Equal(from, step.Edge.From);
        Assert.Equal(to, step.Edge.To);
        Assert.Null(step.Edge.PortalId);
        Assert.Equal(new ModelDuration(durationTicks), step.Edge.Duration);
        Assert.Equal(totalCostTicks, step.TotalCostTicks);
    }

    private static void AssertPortal(
        PathSearchResult.NextStep step,
        PortalDefinition portal,
        long durationTicks,
        long totalCostTicks)
    {
        Assert.Equal(SpatialEdgeKind.Portal, step.Edge.EdgeKind);
        Assert.Equal(portal.From, step.Edge.From);
        Assert.Equal(portal.To, step.Edge.To);
        Assert.Equal(portal.Id, step.Edge.PortalId);
        Assert.Equal(new ModelDuration(durationTicks), step.Edge.Duration);
        Assert.Equal(totalCostTicks, step.TotalCostTicks);
    }

    private static SpatialDefinition Definition(
        IEnumerable<GridMapDefinition> maps,
        IEnumerable<PortalDefinition>? portals = null,
        IEnumerable<AnchorDefinition>? anchors = null,
        IEnumerable<ZoneDefinition>? zones = null) =>
        SpatialDefinition.Create(
            new SpatialDefinitionId("navigation-space"),
            revision: 0,
            rulesVersion: 1,
            maps,
            portals,
            anchors,
            zones);

    private static GridMapDefinition Map(
        string id,
        int width,
        int height,
        long stepTicks = 1,
        IReadOnlyList<CellDefinition>? cells = null) =>
        new(
            new MapId(id),
            width,
            height,
            new ModelDuration(stepTicks),
            visionRange: 4,
            cells ?? Enumerable.Range(0, checked(width * height)).Select(_ => Floor()).ToArray());

    private static CellDefinition Floor(int moveCost = 1, bool blocksMovement = false) =>
        new(new TerrainId(blocksMovement ? "wall" : "floor"), moveCost, blocksMovement, blocksSight: false);

    private static PortalDefinition Portal(
        string id,
        CellRef from,
        CellRef to,
        long durationTicks) =>
        new(new PortalId(id), from, to, new ModelDuration(durationTicks), initiallyEnabled: true);

    private static CellRef Cell(string mapId, int x, int y) => new(new MapId(mapId), x, y);
}

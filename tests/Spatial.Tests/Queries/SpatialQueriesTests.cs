using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialQueriesTests
{
    [Fact]
    public void VisibleCells_UseManhattanRangeStableOrderAndReadOnlyResult()
    {
        SpatialDefinition definition = Definition(
            Map("map", width: 3, height: 3, visionRange: 1));
        SpatialState state = Place(
            definition,
            SpatialState.Create(definition),
            entityId: 1,
            Cell("map", 1, 1));
        var queries = new SpatialQueries(definition);

        IReadOnlyList<CellRef> visible = queries.GetVisibleCells(state, new EntityId(1));

        Assert.Equal(
            [
                Cell("map", 1, 0),
                Cell("map", 0, 1),
                Cell("map", 1, 1),
                Cell("map", 2, 1),
                Cell("map", 1, 2),
            ],
            visible);
        Assert.True(queries.IsCellVisible(state, new EntityId(1), Cell("map", 2, 1)));
        Assert.False(queries.IsCellVisible(state, new EntityId(1), Cell("map", 2, 2)));

        var collection = Assert.IsAssignableFrom<ICollection<CellRef>>(visible);
        Assert.Throws<NotSupportedException>(() => collection.Add(Cell("map", 0, 0)));
    }

    [Fact]
    public void LineOfSight_SeesOpaqueEndpointButNotBeyondItAndUsesStrictCorners()
    {
        SpatialDefinition definition = Definition(
            Map("row", width: 3, height: 1, visionRange: 3, opaqueCells: [(1, 0)]),
            Map("corner", width: 2, height: 2, visionRange: 3, opaqueCells: [(1, 0)]));
        SpatialState state = SpatialState.Create(definition);
        var queries = new SpatialQueries(definition);

        Assert.True(queries.HasLineOfSight(state, Cell("row", 0, 0), Cell("row", 1, 0)));
        Assert.False(queries.HasLineOfSight(state, Cell("row", 0, 0), Cell("row", 2, 0)));
        Assert.False(queries.HasLineOfSight(state, Cell("corner", 0, 0), Cell("corner", 1, 1)));
    }

    [Fact]
    public void VisibleEntities_ExcludeSelfCrossMapAndPortalButIncludeSameCellWhenObservationDisabled()
    {
        GridMapDefinition firstMap = Map("first", width: 3, height: 1, visionRange: 3);
        GridMapDefinition secondMap = Map("second", width: 1, height: 1, visionRange: 3);
        SpatialDefinition definition = Definition(
            [firstMap, secondMap],
            [
                new PortalDefinition(
                    new PortalId("open-portal"),
                    Cell("first", 2, 0),
                    Cell("second", 0, 0),
                    ModelDuration.FromSeconds(1),
                    initiallyEnabled: true),
            ]);
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, entityId: 10, Cell("first", 0, 0), observationEnabled: false);
        state = Place(definition, state, entityId: 3, Cell("first", 0, 0));
        state = Place(definition, state, entityId: 2, Cell("first", 1, 0));
        state = Place(definition, state, entityId: 4, Cell("second", 0, 0));
        var queries = new SpatialQueries(definition);

        IReadOnlyList<EntityId> visible = queries.GetVisibleEntities(state, new EntityId(10));

        Assert.Equal([new EntityId(2), new EntityId(3)], visible);
        Assert.False(queries.HasLineOfSight(
            state,
            Cell("first", 2, 0),
            Cell("second", 0, 0)));
        var collection = Assert.IsAssignableFrom<ICollection<EntityId>>(visible);
        Assert.Throws<NotSupportedException>(() => collection.Add(new EntityId(20)));
    }

    [Fact]
    public void DynamicSightOverride_ImmediatelyChangesObjectiveQueriesAndCanBeCleared()
    {
        SpatialDefinition definition = Definition(Map("map", width: 3, height: 1, visionRange: 3));
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, entityId: 1, Cell("map", 0, 0));
        state = Place(definition, state, entityId: 2, Cell("map", 2, 0));
        var queries = new SpatialQueries(definition);
        CellRef middle = Cell("map", 1, 0);
        var opaque = new CellOverride(blocksSight: true);

        Assert.Equal([new EntityId(2)], queries.GetVisibleEntities(state, new EntityId(1)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(middle, expectedOverride: null, resultingOverride: opaque));

        Assert.True(queries.IsCellVisible(state, new EntityId(1), middle));
        Assert.Empty(queries.GetVisibleEntities(state, new EntityId(1)));

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(middle, expectedOverride: opaque, resultingOverride: null));
        Assert.Equal([new EntityId(2)], queries.GetVisibleEntities(state, new EntityId(1)));
    }

    [Fact]
    public void DynamicSightOverride_CanOpenStaticOpaqueCellAndClearingRestoresIt()
    {
        SpatialDefinition definition = Definition(
            Map("map", width: 3, height: 1, visionRange: 3, opaqueCells: [(1, 0)]));
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, entityId: 1, Cell("map", 0, 0));
        state = Place(definition, state, entityId: 2, Cell("map", 2, 0));
        var queries = new SpatialQueries(definition);
        CellRef middle = Cell("map", 1, 0);
        var transparent = new CellOverride(blocksSight: false);

        Assert.Empty(queries.GetVisibleEntities(state, new EntityId(1)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(middle, expectedOverride: null, resultingOverride: transparent));
        Assert.Equal([new EntityId(2)], queries.GetVisibleEntities(state, new EntityId(1)));

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(middle, expectedOverride: transparent, resultingOverride: null));
        Assert.Empty(queries.GetVisibleEntities(state, new EntityId(1)));
    }

    [Fact]
    public void VisionRangeZero_OnlySeesSourceCellAndSourceOpacityDoesNotBlockSight()
    {
        SpatialDefinition zeroRange = Definition(
            Map("zero", width: 2, height: 1, visionRange: 0, opaqueCells: [(0, 0)]));
        SpatialState zeroState = SpatialState.Create(zeroRange);
        zeroState = Place(zeroRange, zeroState, entityId: 1, Cell("zero", 0, 0));
        zeroState = Place(zeroRange, zeroState, entityId: 2, Cell("zero", 0, 0));
        zeroState = Place(zeroRange, zeroState, entityId: 3, Cell("zero", 1, 0));
        var zeroQueries = new SpatialQueries(zeroRange);

        Assert.Equal([Cell("zero", 0, 0)], zeroQueries.GetVisibleCells(zeroState, new EntityId(1)));
        Assert.Equal([new EntityId(2)], zeroQueries.GetVisibleEntities(zeroState, new EntityId(1)));
        Assert.False(zeroQueries.IsCellVisible(zeroState, new EntityId(1), Cell("zero", 1, 0)));

        SpatialDefinition opaqueSource = Definition(
            Map("opaque", width: 2, height: 1, visionRange: 1, opaqueCells: [(0, 0)]));
        SpatialState opaqueState = Place(
            opaqueSource,
            SpatialState.Create(opaqueSource),
            entityId: 1,
            Cell("opaque", 0, 0));
        var opaqueQueries = new SpatialQueries(opaqueSource);

        Assert.True(opaqueQueries.IsCellVisible(
            opaqueState,
            new EntityId(1),
            Cell("opaque", 1, 0)));
    }

    [Fact]
    public void ActiveJourney_RemainsQueryableAtAuthoritativeFromCellUntilStepCommits()
    {
        SpatialDefinition definition = Definition(Map("map", width: 3, height: 1, visionRange: 1));
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, entityId: 1, Cell("map", 0, 0));
        state = Place(definition, state, entityId: 2, Cell("map", 1, 0));
        CurrentLeg leg = SpatialEventTestHarness.Leg(
            Cell("map", 1, 0),
            Cell("map", 2, 0),
            generation: 1);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1),
                new EntityId(2),
                new CellGoal(Cell("map", 2, 0)),
                generation: 1,
                leg)));
        var queries = new SpatialQueries(definition);

        Assert.Equal(Cell("map", 1, 0), state.Entities.Single(entity => entity.Id == new EntityId(2)).Cell);
        Assert.Equal([new EntityId(2)], queries.GetVisibleEntities(state, new EntityId(1)));
    }

    [Fact]
    public void EveryPublicEntryRejectsStampMismatchAndObserverQueriesRequirePlacedEntity()
    {
        SpatialDefinition definition = Definition(Map("expected", width: 1, height: 1, visionRange: 1));
        SpatialDefinition other = Definition(Map("other", width: 1, height: 1, visionRange: 1));
        SpatialState mismatched = SpatialState.Create(other);
        SpatialState state = SpatialState.Create(definition);
        var queries = new SpatialQueries(definition);

        Assert.Throws<InvalidOperationException>(() =>
            queries.GetVisibleCells(mismatched, new EntityId(1)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.GetVisibleEntities(mismatched, new EntityId(1)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.IsCellVisible(mismatched, new EntityId(1), Cell("expected", 0, 0)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.HasLineOfSight(mismatched, Cell("expected", 0, 0), Cell("expected", 0, 0)));

        Assert.Throws<InvalidOperationException>(() =>
            queries.GetVisibleCells(state, new EntityId(1)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.GetVisibleEntities(state, new EntityId(1)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.IsCellVisible(state, new EntityId(1), Cell("expected", 0, 0)));
    }

    [Fact]
    public void EveryPublicEntryRejectsAnotherActorsUnfinishedStepPrefix()
    {
        SpatialDefinition definition = Definition(Map("map", width: 3, height: 1, visionRange: 3));
        SpatialState state = SpatialState.Create(definition);
        state = Place(definition, state, entityId: 1, Cell("map", 0, 0));
        state = Place(definition, state, entityId: 2, Cell("map", 1, 0));
        CurrentLeg leg = SpatialEventTestHarness.Leg(
            Cell("map", 1, 0),
            Cell("map", 2, 0),
            generation: 1);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1),
                new EntityId(2),
                new CellGoal(Cell("map", 0, 0)),
                generation: 1,
                leg)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntitySteppedEvent(
                new EntityId(2),
                new JourneyId(1),
                Cell("map", 1, 0),
                Cell("map", 2, 0),
                journeyGeneration: 1),
            SpatialEventTestHarness.AtSecond(1));
        var queries = new SpatialQueries(definition);

        Assert.Throws<InvalidOperationException>(() =>
            queries.GetVisibleCells(state, new EntityId(1)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.GetVisibleEntities(state, new EntityId(1)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.IsCellVisible(state, new EntityId(1), Cell("map", 0, 0)));
        Assert.Throws<InvalidOperationException>(() =>
            queries.HasLineOfSight(state, Cell("map", 0, 0), Cell("map", 1, 0)));
    }

    [Fact]
    public void HasLineOfSight_RequiresDefinedCells()
    {
        SpatialDefinition definition = Definition(Map("map", width: 1, height: 1, visionRange: 1));
        SpatialState state = SpatialState.Create(definition);
        var queries = new SpatialQueries(definition);

        Assert.Throws<InvalidOperationException>(() => queries.HasLineOfSight(
            state,
            Cell("map", 0, 0),
            Cell("map", 1, 0)));
    }

    private static SpatialState Place(
        SpatialDefinition definition,
        SpatialState state,
        long entityId,
        CellRef cell,
        bool observationEnabled = true) =>
        SpatialEventTestHarness.Place(definition, state, entityId, cell, observationEnabled);

    private static SpatialDefinition Definition(params GridMapDefinition[] maps) => Definition(maps, []);

    private static SpatialDefinition Definition(
        IReadOnlyList<GridMapDefinition> maps,
        IReadOnlyList<PortalDefinition> portals) =>
        TestSpatialDefinitionBuilder.Create(maps, portals);

    private static GridMapDefinition Map(
        string id,
        int width,
        int height,
        int visionRange,
        IReadOnlyList<(int X, int Y)>? opaqueCells = null)
    {
        var opaque = (opaqueCells ?? []).ToHashSet();
        CellDefinition[] cells =
        [
            .. Enumerable.Range(0, height).SelectMany(y =>
                Enumerable.Range(0, width).Select(x =>
                    TestSpatialDefinitionBuilder.Floor(blocksSight: opaque.Contains((x, y))))),
        ];
        return TestSpatialDefinitionBuilder.Map(id, width, height, visionRange: visionRange, cells: cells);
    }

    private static CellRef Cell(string mapId, int x, int y) =>
        TestSpatialDefinitionBuilder.Cell(mapId, x, y);
}

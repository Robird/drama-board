using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialNavigatorDeterminismTests
{
    [Fact]
    public void FindNextStep_DefinitionCollectionPermutations_ProduceIdenticalResult()
    {
        GridMapDefinition source = Map("source");
        GridMapDefinition mapA = Map("a");
        GridMapDefinition mapB = Map("b");
        CellRef start = Cell("source");
        CellRef goalA = Cell("a");
        CellRef goalB = Cell("b");
        PortalDefinition portalA = Portal("p.a", start, goalB);
        PortalDefinition portalZ = Portal("p.z", start, goalA);
        ZoneDefinition zoneFirst = new(new ZoneId("destinations"), [goalB, goalA]);
        ZoneDefinition zoneSecond = new(new ZoneId("destinations"), [goalA, goalB]);
        SpatialDefinition first = Definition(
            [source, mapB, mapA],
            [portalZ, portalA],
            [zoneFirst]);
        SpatialDefinition second = Definition(
            [mapA, source, mapB],
            [portalA, portalZ],
            [zoneSecond]);

        PathSearchResult firstResult = SpatialNavigator.FindNextStep(
            first,
            SpatialState.Create(first),
            start,
            new ZoneGoal(new ZoneId("destinations")));
        PathSearchResult secondResult = SpatialNavigator.FindNextStep(
            second,
            SpatialState.Create(second),
            start,
            new ZoneGoal(new ZoneId("destinations")));

        Assert.Equal(firstResult, secondResult);
    }

    [Fact]
    public void FindNextStep_EntityCountPlacementAndRegistrationOrder_DoNotAffectRoute()
    {
        SpatialDefinition definition = Definition([new GridMapDefinition(
            new MapId("map"),
            width: 3,
            height: 1,
            new ModelDuration(10),
            visionRange: 4,
            [Floor(), Floor(), Floor()])]);
        SpatialState empty = SpatialState.Create(definition);
        SpatialState forward = PlaceEntities(
            definition,
            empty,
            [(1, 0), (2, 1), (3, 2)]);
        SpatialState reverse = PlaceEntities(
            definition,
            empty,
            [(3, 2), (2, 1), (1, 0)]);
        CellRef start = new(new MapId("map"), 0, 0);
        var goal = new CellGoal(new CellRef(new MapId("map"), 2, 0));

        PathSearchResult withoutEntities = SpatialNavigator.FindNextStep(definition, empty, start, goal);
        PathSearchResult forwardResult = SpatialNavigator.FindNextStep(definition, forward, start, goal);
        PathSearchResult reverseResult = SpatialNavigator.FindNextStep(definition, reverse, start, goal);

        Assert.Equal(withoutEntities, forwardResult);
        Assert.Equal(withoutEntities, reverseResult);
    }

    private static SpatialState PlaceEntities(
        SpatialDefinition definition,
        SpatialState initial,
        IEnumerable<(long Id, int X)> entities)
    {
        SpatialState state = initial;
        foreach ((long id, int x) in entities)
        {
            state = SpatialEventTestHarness.Apply(
                definition,
                state,
                new EntityPlacedEvent(new SpatialEntityState(
                    new EntityId(id),
                    new CellRef(new MapId("map"), x, 0),
                    observationEnabled: true,
                    movementGeneration: 0)));
        }

        return state;
    }

    private static SpatialDefinition Definition(
        IEnumerable<GridMapDefinition> maps,
        IEnumerable<PortalDefinition>? portals = null,
        IEnumerable<ZoneDefinition>? zones = null) =>
        SpatialDefinition.Create(
            new SpatialDefinitionId("navigation-space"),
            revision: 0,
            rulesVersion: 1,
            maps,
            portals,
            anchors: null,
            zones);

    private static GridMapDefinition Map(string id) =>
        new(
            new MapId(id),
            width: 1,
            height: 1,
            new ModelDuration(1),
            visionRange: 1,
            [Floor()]);

    private static CellDefinition Floor() =>
        new(new TerrainId("floor"), moveCost: 1, blocksMovement: false, blocksSight: false);

    private static CellRef Cell(string mapId) => new(new MapId(mapId), 0, 0);

    private static PortalDefinition Portal(string id, CellRef from, CellRef to) =>
        new(new PortalId(id), from, to, new ModelDuration(5), initiallyEnabled: true);
}

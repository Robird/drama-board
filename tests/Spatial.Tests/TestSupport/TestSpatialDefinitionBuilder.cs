using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

internal static class TestSpatialDefinitionBuilder
{
    public static SpatialDefinition CreateDefault() => Create(
        maps:
        [
            Map("world", width: 2, height: 2),
            Map("town", width: 2, height: 1),
        ],
        portals:
        [
            new PortalDefinition(
                new PortalId("world-to-town"),
                Cell("world", 1, 0),
                Cell("town", 0, 0),
                ModelDuration.FromSeconds(3),
                initiallyEnabled: true),
        ],
        anchors:
        [
            new AnchorDefinition(new AnchorId("town-gate"), Cell("town", 0, 0)),
        ],
        zones:
        [
            new ZoneDefinition(new ZoneId("town-square"), [Cell("town", 1, 0)]),
        ]);

    public static SpatialDefinition Create(
        IEnumerable<GridMapDefinition> maps,
        IEnumerable<PortalDefinition>? portals = null,
        IEnumerable<AnchorDefinition>? anchors = null,
        IEnumerable<ZoneDefinition>? zones = null) =>
        SpatialDefinition.Create(
            new SpatialDefinitionId("test-space"),
            revision: 0,
            rulesVersion: 1,
            maps,
            portals,
            anchors,
            zones);

    public static GridMapDefinition Map(
        string id,
        int width = 2,
        int height = 2,
        ModelDuration? stepDuration = null,
        int visionRange = 4,
        IReadOnlyList<CellDefinition>? cells = null) =>
        new(
            new MapId(id),
            width,
            height,
            stepDuration ?? ModelDuration.FromSeconds(1),
            visionRange,
            cells ?? Enumerable.Range(0, checked(width * height)).Select(_ => Floor()).ToArray());

    public static CellDefinition Floor(
        string terrainId = "floor",
        int moveCost = 1,
        bool blocksMovement = false,
        bool blocksSight = false) =>
        new(new TerrainId(terrainId), moveCost, blocksMovement, blocksSight);

    public static CellRef Cell(string mapId, int x, int y) => new(new MapId(mapId), x, y);
}

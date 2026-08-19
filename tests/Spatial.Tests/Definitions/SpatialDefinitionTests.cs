using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialDefinitionTests
{
    [Fact]
    public void Create_ValidContent_CanonicalizesAndClonesCollections()
    {
        var inputMaps = new List<GridMapDefinition>
        {
            TestSpatialDefinitionBuilder.Map("world"),
            TestSpatialDefinitionBuilder.Map("town", width: 1, height: 1),
        };
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(inputMaps);

        inputMaps.Clear();

        Assert.Equal(["town", "world"], definition.Maps.Select(map => map.Id.Value));
        Assert.Equal(2, definition.Maps.Count);
        Assert.Equal(64, definition.ContentHash.Value.Length);
        Assert.True(definition.Contains(TestSpatialDefinitionBuilder.Cell("world", 1, 1)));
        Assert.False(definition.Contains(TestSpatialDefinitionBuilder.Cell("world", 2, 1)));
    }

    [Fact]
    public void GridMap_ClonesCells()
    {
        CellDefinition[] cells = [TestSpatialDefinitionBuilder.Floor()];
        var map = new GridMapDefinition(
            new MapId("map"),
            1,
            1,
            ModelDuration.FromSeconds(1),
            1,
            cells);

        cells[0] = TestSpatialDefinitionBuilder.Floor("wall", blocksMovement: true);

        Assert.Equal("floor", map.Cells[0].TerrainId.Value);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void GridMap_NonPositiveDimension_Throws(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridMapDefinition(
            new MapId("map"),
            width,
            height,
            ModelDuration.FromSeconds(1),
            1,
            []));
    }

    [Fact]
    public void GridMap_CellCountMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GridMapDefinition(
            new MapId("map"),
            2,
            2,
            ModelDuration.FromSeconds(1),
            1,
            [TestSpatialDefinitionBuilder.Floor()]));
    }

    [Fact]
    public void GridMap_DimensionProductOverflow_Throws()
    {
        Assert.Throws<OverflowException>(() => new GridMapDefinition(
            new MapId("map"),
            int.MaxValue,
            2,
            ModelDuration.FromSeconds(1),
            1,
            []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GridMap_NonPositiveStepDuration_Throws(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridMapDefinition(
            new MapId("map"),
            1,
            1,
            new ModelDuration(ticks),
            1,
            [TestSpatialDefinitionBuilder.Floor()]));
    }

    [Fact]
    public void GridMap_StaticMovementCostOverflow_Throws()
    {
        Assert.Throws<OverflowException>(() => new GridMapDefinition(
            new MapId("map"),
            1,
            1,
            new ModelDuration(long.MaxValue),
            1,
            [TestSpatialDefinitionBuilder.Floor(moveCost: 2)]));
    }

    [Fact]
    public void GridMap_MaxRepresentableMovementCost_Succeeds()
    {
        var map = new GridMapDefinition(
            new MapId("map"),
            1,
            1,
            new ModelDuration(long.MaxValue),
            1,
            [TestSpatialDefinitionBuilder.Floor()]);

        Assert.Equal(long.MaxValue, map.OrthogonalStepDuration.Ticks);
    }

    [Fact]
    public void GridMap_NegativeVisionRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestSpatialDefinitionBuilder.Map(
            "map",
            visionRange: -1));
    }

    [Fact]
    public void Cell_NonPositiveMoveCost_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestSpatialDefinitionBuilder.Floor(moveCost: 0));
    }

    [Fact]
    public void Portal_NonPositiveDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortalDefinition(
            new PortalId("portal"),
            TestSpatialDefinitionBuilder.Cell("map", 0, 0),
            TestSpatialDefinitionBuilder.Cell("map", 0, 0),
            new ModelDuration(0),
            true));
    }

    [Fact]
    public void Create_DuplicateTypedIdentifier_Throws()
    {
        Assert.Throws<ArgumentException>(() => TestSpatialDefinitionBuilder.Create(
            maps:
            [
                TestSpatialDefinitionBuilder.Map("map"),
                TestSpatialDefinitionBuilder.Map("map"),
            ]));
    }

    [Fact]
    public void Create_DuplicateIdentifierAcrossCategories_Throws()
    {
        Assert.Throws<ArgumentException>(() => TestSpatialDefinitionBuilder.Create(
            maps: [TestSpatialDefinitionBuilder.Map("shared")],
            anchors:
            [
                new AnchorDefinition(
                    new AnchorId("shared"),
                    TestSpatialDefinitionBuilder.Cell("shared", 0, 0)),
            ]));
    }

    [Theory]
    [InlineData("portal")]
    [InlineData("anchor")]
    [InlineData("zone")]
    public void Create_UndefinedCellReference_Throws(string referenceKind)
    {
        GridMapDefinition map = TestSpatialDefinitionBuilder.Map("map", width: 1, height: 1);
        CellRef invalidCell = TestSpatialDefinitionBuilder.Cell("map", 1, 0);

        Action create = referenceKind switch
        {
            "portal" => () => TestSpatialDefinitionBuilder.Create(
                [map],
                portals:
                [
                    new PortalDefinition(
                        new PortalId("portal"),
                        TestSpatialDefinitionBuilder.Cell("map", 0, 0),
                        invalidCell,
                        ModelDuration.FromSeconds(1),
                        true),
                ]),
            "anchor" => () => TestSpatialDefinitionBuilder.Create(
                [map],
                anchors: [new AnchorDefinition(new AnchorId("anchor"), invalidCell)]),
            "zone" => () => TestSpatialDefinitionBuilder.Create(
                [map],
                zones: [new ZoneDefinition(new ZoneId("zone"), [invalidCell])]),
            _ => throw new InvalidOperationException(),
        };

        Assert.Throws<ArgumentException>(create);
    }

    [Fact]
    public void Zone_DuplicateCell_Throws()
    {
        CellRef cell = TestSpatialDefinitionBuilder.Cell("map", 0, 0);
        Assert.Throws<ArgumentException>(() => new ZoneDefinition(new ZoneId("zone"), [cell, cell]));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    public void Create_InvalidMetadata_Throws(long revision, ushort rulesVersion)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => SpatialDefinition.Create(
            new SpatialDefinitionId("space"),
            revision,
            rulesVersion,
            [TestSpatialDefinitionBuilder.Map("map")]));
    }
}

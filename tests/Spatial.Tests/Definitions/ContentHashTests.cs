using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class ContentHashTests
{
    [Fact]
    public void ContentHash_CanonicalEncodingContract_IsStable()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();

        Assert.Equal(
            "aeb23e3ae4c0743b4c84e92dffa9b77af775995ca8d84074a55535492f72d2fa",
            definition.ContentHash.Value);
    }

    [Fact]
    public void ContentHash_InputPermutation_DoesNotChangeDigest()
    {
        GridMapDefinition mapA = TestSpatialDefinitionBuilder.Map("a", width: 2, height: 1);
        GridMapDefinition mapB = TestSpatialDefinitionBuilder.Map("b", width: 1, height: 1);
        PortalDefinition portalA = new(
            new PortalId("portal-a"),
            TestSpatialDefinitionBuilder.Cell("a", 0, 0),
            TestSpatialDefinitionBuilder.Cell("b", 0, 0),
            ModelDuration.FromSeconds(1),
            true);
        PortalDefinition portalB = new(
            new PortalId("portal-b"),
            TestSpatialDefinitionBuilder.Cell("b", 0, 0),
            TestSpatialDefinitionBuilder.Cell("a", 1, 0),
            ModelDuration.FromSeconds(2),
            false);
        AnchorDefinition anchorA = new(new AnchorId("anchor-a"), TestSpatialDefinitionBuilder.Cell("a", 0, 0));
        AnchorDefinition anchorB = new(new AnchorId("anchor-b"), TestSpatialDefinitionBuilder.Cell("b", 0, 0));
        ZoneDefinition zoneA = new(
            new ZoneId("zone-a"),
            [TestSpatialDefinitionBuilder.Cell("a", 1, 0), TestSpatialDefinitionBuilder.Cell("a", 0, 0)]);
        ZoneDefinition zoneB = new(new ZoneId("zone-b"), [TestSpatialDefinitionBuilder.Cell("b", 0, 0)]);

        SpatialDefinition first = TestSpatialDefinitionBuilder.Create(
            [mapB, mapA],
            [portalB, portalA],
            [anchorB, anchorA],
            [zoneB, zoneA]);
        SpatialDefinition second = TestSpatialDefinitionBuilder.Create(
            [mapA, mapB],
            [portalA, portalB],
            [anchorA, anchorB],
            [zoneA, zoneB]);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.Maps.Select(map => map.Id), second.Maps.Select(map => map.Id));
        Assert.Equal(first.Portals.Select(portal => portal.Id), second.Portals.Select(portal => portal.Id));
    }

    [Fact]
    public void ContentHash_ZoneCellInputOrder_DoesNotChangeDigest()
    {
        GridMapDefinition map = TestSpatialDefinitionBuilder.Map("map", width: 2, height: 1);
        CellRef left = TestSpatialDefinitionBuilder.Cell("map", 0, 0);
        CellRef right = TestSpatialDefinitionBuilder.Cell("map", 1, 0);

        SpatialDefinition first = TestSpatialDefinitionBuilder.Create(
            [map],
            zones: [new ZoneDefinition(new ZoneId("zone"), [right, left])]);
        SpatialDefinition second = TestSpatialDefinitionBuilder.Create(
            [map],
            zones: [new ZoneDefinition(new ZoneId("zone"), [left, right])]);

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void ContentHash_DefinitionIdentityAndRulesMetadata_AreExcluded()
    {
        GridMapDefinition[] maps = [TestSpatialDefinitionBuilder.Map("map")];
        SpatialDefinition first = SpatialDefinition.Create(
            new SpatialDefinitionId("first"),
            revision: 1,
            rulesVersion: 1,
            maps);
        SpatialDefinition second = SpatialDefinition.Create(
            new SpatialDefinitionId("second"),
            revision: 99,
            rulesVersion: 7,
            maps);

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void ContentHash_ContentChange_ChangesDigest()
    {
        SpatialDefinition first = TestSpatialDefinitionBuilder.Create(
            [TestSpatialDefinitionBuilder.Map("map", width: 1, height: 1)]);
        SpatialDefinition second = TestSpatialDefinitionBuilder.Create(
            [TestSpatialDefinitionBuilder.Map(
                "map",
                width: 1,
                height: 1,
                cells: [TestSpatialDefinitionBuilder.Floor(moveCost: 2)])]);

        Assert.NotEqual(first.ContentHash, second.ContentHash);
    }
}

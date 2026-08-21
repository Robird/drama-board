using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialRulesVersionGateTests
{
    [Fact]
    public void FutureDefinitionVersion_CanBeArchivedButCannotBindToVersionOneExecutors()
    {
        SpatialDefinition future = Definition(rulesVersion: 7);

        Assert.Equal((ushort)7, future.RulesVersion);
        SpatialState archivedState = SpatialState.Create(future);
        Assert.Equal((ushort)7, archivedState.Definition.RulesVersion);

        NotSupportedException[] failures =
        [
            Assert.Throws<NotSupportedException>(() => new SpatialReducer(future)),
            Assert.Throws<NotSupportedException>(() => new SpatialQueries(future)),
            Assert.Throws<NotSupportedException>(() => new SpatialCommandHandler(future)),
            Assert.Throws<NotSupportedException>(() => new SpatialOccurrenceRule(future)),
        ];
        Assert.All(failures, failure => Assert.Equal(
            "Spatial rules version 7 is not supported by this runtime; supported version is 1.",
            failure.Message));
    }

    [Fact]
    public void CurrentDefinitionVersion_BindsToEveryVersionOneExecutor()
    {
        SpatialDefinition current = Definition(SpatialRules.CurrentVersion);

        Assert.Equal((ushort)1, SpatialRules.CurrentVersion);
        Assert.NotNull(new SpatialReducer(current));
        Assert.NotNull(new SpatialQueries(current));
        Assert.NotNull(new SpatialCommandHandler(current));
        Assert.NotNull(new SpatialOccurrenceRule(current));
    }

    private static SpatialDefinition Definition(ushort rulesVersion)
    {
        var cell = new CellDefinition(
            new TerrainId("floor"),
            moveCost: 1,
            blocksMovement: false,
            blocksSight: false);
        var map = new GridMapDefinition(
            new MapId("map"),
            width: 1,
            height: 1,
            new ModelDuration(1),
            visionRange: 0,
            rowMajorCells: [cell]);
        return SpatialDefinition.Create(
            new SpatialDefinitionId("version-gate"),
            revision: 0,
            rulesVersion,
            maps: [map]);
    }
}

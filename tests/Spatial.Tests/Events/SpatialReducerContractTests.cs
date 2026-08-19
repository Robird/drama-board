using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialReducerContractTests
{
    [Fact]
    public void Apply_DefinitionStampMismatch_Throws()
    {
        SpatialDefinition first = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialDefinition second = TestSpatialDefinitionBuilder.Create(
            [TestSpatialDefinitionBuilder.Map("different", width: 1, height: 1)]);
        SpatialState state = SpatialState.Create(first);

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            second,
            state,
            new ZoneEnteredEvent(new EntityId(1), new ZoneId("zone"))));
    }

    [Fact]
    public void Apply_KindIdPayloadAndVersionMustMatchExactly()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        var payload = new EntityPlacedEvent(new SpatialEntityState(
            new EntityId(1),
            TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            true,
            0));

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            payload,
            kind: SpatialEventKinds.EntityRemoved));
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            payload,
            kind: new EventKind(SpatialEventKinds.EntityPlaced.Id, 2)));
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            payload,
            kind: new EventKind("spatial.unknown", 1)));
    }

    [Fact]
    public void Apply_PrimaryIncrementsRevisionWhileDerivedIsExactNoOp()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState initial = SpatialState.Create(definition);
        SpatialState placed = SpatialEventTestHarness.Place(definition, initial);
        SpatialState derived = SpatialEventTestHarness.Apply(
            definition,
            placed,
            new ZoneEnteredEvent(new EntityId(1), new ZoneId("town-square")));

        Assert.Equal(1, placed.Revision);
        Assert.Same(placed, derived);
        Assert.Equal(1, derived.Revision);
    }

    [Fact]
    public void Apply_ScratchProjectorAndFormalReducerProduceEqualState()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState initial = SpatialState.Create(definition);
        SpatialEvent payload = new EntityPlacedEvent(new SpatialEntityState(
            new EntityId(1),
            TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            true,
            0));

        SpatialState scratch = SpatialProjector.Apply(
            definition,
            initial,
            SpatialEventKinds.EntityPlaced,
            payload,
            ModelTime.Zero);
        SpatialState formal = SpatialEventTestHarness.Apply(definition, initial, payload);

        Assert.Equal(scratch, formal);
        Assert.Equal(scratch.GetHashCode(), formal.GetHashCode());
    }

    [Fact]
    public void Apply_UnknownCellAndNonCanonicalOverride_Throw()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntityPlacedEvent(new SpatialEntityState(
                new EntityId(1),
                TestSpatialDefinitionBuilder.Cell("world", 99, 0),
                true,
                0))));
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(
                TestSpatialDefinitionBuilder.Cell("world", 0, 0),
                expectedOverride: null,
                resultingOverride: new CellOverride(blocksMovement: false))));
    }
}

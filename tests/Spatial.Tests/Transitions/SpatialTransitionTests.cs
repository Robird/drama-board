using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialTransitionTests
{
    [Fact]
    public void Complete_AppendsCanonicalDerivedFactsAfterRawPrimaryFacts()
    {
        CellRef west = TestSpatialDefinitionBuilder.Cell("map", 0, 0);
        CellRef east = TestSpatialDefinitionBuilder.Cell("map", 1, 0);
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            maps: [TestSpatialDefinitionBuilder.Map("map", width: 2, height: 1, visionRange: 2)],
            zones:
            [
                new ZoneDefinition(new ZoneId("west"), [west]),
                new ZoneDefinition(new ZoneId("east"), [east]),
            ]);
        SpatialState preState = SpatialState.Create(definition);
        SpatialEvent[] body =
        [
            Place(2, west, observationEnabled: false),
            Place(1, west, observationEnabled: true),
            Place(3, east, observationEnabled: true),
        ];

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition, preState, ModelTime.Zero, body);

        Assert.Equal(body, result.Facts.Take(body.Length));
        Assert.Contains(result.Facts, fact =>
            fact == new ZoneEnteredEvent(new EntityId(1), new ZoneId("west")));
        Assert.Contains(result.Facts, fact =>
            fact == new CoPresenceStartedEvent(new EntityId(1), new EntityId(2)));
        Assert.Contains(result.Facts, fact =>
            fact is GeometricVisibilityChangedEvent visibility &&
            visibility.ObserverId == new EntityId(1));
        Assert.All(result.Facts, fact => Assert.IsAssignableFrom<SpatialEvent>(fact));
        Assert.Equal(
            result.ResultingState,
            Fold(definition, preState, ModelTime.Zero, result.Facts));
    }

    [Fact]
    public void Complete_ObservationChangeDerivesVisibilityDelta()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            maps: [TestSpatialDefinitionBuilder.Map("map", width: 2, height: 1, visionRange: 2)]);
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Apply(definition, state, Place(
            1, TestSpatialDefinitionBuilder.Cell("map", 0, 0), observationEnabled: false));
        state = SpatialEventTestHarness.Apply(definition, state, Place(
            2, TestSpatialDefinitionBuilder.Cell("map", 1, 0), observationEnabled: false));

        SpatialTransitionResult result = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new ObservationStateChangedEvent(new EntityId(1), false, true)]);

        GeometricVisibilityChangedEvent visibility = Assert.Single(
            result.Facts.OfType<GeometricVisibilityChangedEvent>());
        Assert.Equal(new EntityId(1), visibility.ObserverId);
        Assert.Equal([new EntityId(2)], visibility.AddedEntityIds);
        Assert.Empty(visibility.RemovedEntityIds);
    }

    [Fact]
    public void Complete_RejectsDerivedFactsAsPrimaryBody()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);

        Assert.Throws<ArgumentException>(() => SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new ZoneEnteredEvent(new EntityId(1), new ZoneId("town-square"))]));
    }

    private static EntityPlacedEvent Place(long id, CellRef cell, bool observationEnabled) =>
        new(new SpatialEntityState(new EntityId(id), cell, observationEnabled, movementGeneration: 0));

    private static SpatialState Fold(
        SpatialDefinition definition,
        SpatialState state,
        ModelTime modelTime,
        IEnumerable<SpatialEvent> facts)
    {
        var reducer = new SpatialReducer(definition);
        var instant = new LogicalInstant(modelTime, 0);
        foreach (SpatialEvent fact in facts)
        {
            state = reducer.Apply(state, instant, fact);
        }

        return state;
    }
}

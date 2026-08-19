using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class CurrentLegDueProjectionTests
{
    [Fact]
    public void JourneyStarted_OrthogonalDueUsesBaseTargetCostExactly()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        CurrentLeg leg = OrthogonalLeg(ModelTime.Zero, SpatialEventTestHarness.AtSecond(1));

        state = Start(definition, state, leg, ModelTime.Zero);

        Assert.Equal(SpatialEventTestHarness.AtSecond(1), Assert.Single(state.Journeys).CurrentLeg.Due);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    public void JourneyStarted_OrthogonalWrongDueIsRejected(long dueMilliseconds)
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        CurrentLeg leg = OrthogonalLeg(
            ModelTime.Zero,
            ModelTime.Zero + ModelDuration.FromMilliseconds(dueMilliseconds));

        Assert.Throws<InvalidOperationException>(() => Start(definition, state, leg, ModelTime.Zero));
    }

    [Fact]
    public void JourneyStarted_OrthogonalDueUsesCurrentCellOverrideCost()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(
                TestSpatialDefinitionBuilder.Cell("world", 1, 0),
                expectedOverride: null,
                resultingOverride: new CellOverride(moveCost: 2)));
        CurrentLeg leg = OrthogonalLeg(ModelTime.Zero, SpatialEventTestHarness.AtSecond(2));

        state = Start(definition, state, leg, ModelTime.Zero);

        Assert.Equal(SpatialEventTestHarness.AtSecond(2), Assert.Single(state.Journeys).CurrentLeg.Due);
    }

    [Fact]
    public void JourneyStarted_PortalDueUsesPortalDurationWithoutTargetMoveCost()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        CellRef from = TestSpatialDefinitionBuilder.Cell("world", 1, 0);
        CellRef to = TestSpatialDefinitionBuilder.Cell("town", 0, 0);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: from);
        var leg = new CurrentLeg(
            from,
            to,
            SpatialEdgeKind.Portal,
            new PortalId("world-to-town"),
            ModelTime.Zero,
            SpatialEventTestHarness.AtSecond(3),
            journeyGeneration: 1);
        var journey = new JourneyState(
            new JourneyId(1),
            new EntityId(1),
            new CellGoal(TestSpatialDefinitionBuilder.Cell("town", 1, 0)),
            generation: 1,
            leg);

        state = SpatialEventTestHarness.Apply(definition, state, new JourneyStartedEvent(journey));

        Assert.Equal(SpatialEventTestHarness.AtSecond(3), Assert.Single(state.Journeys).CurrentLeg.Due);
    }

    [Fact]
    public void JourneyStarted_ExactDueCalculationOverflowIsRejected()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        var now = new ModelTime(long.MaxValue - 500);
        CurrentLeg leg = OrthogonalLeg(now, new ModelTime(long.MaxValue));

        Assert.Throws<OverflowException>(() => Start(definition, state, leg, now));
        Assert.Empty(state.Journeys);
    }

    private static CurrentLeg OrthogonalLeg(ModelTime startedAt, ModelTime due) =>
        new(
            TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            TestSpatialDefinitionBuilder.Cell("world", 1, 0),
            SpatialEdgeKind.Orthogonal,
            portalId: null,
            startedAt,
            due,
            journeyGeneration: 1);

    private static SpatialState Start(
        SpatialDefinition definition,
        SpatialState state,
        CurrentLeg leg,
        ModelTime now) =>
        SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1),
                new EntityId(1),
                new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 1, 1)),
                generation: 1,
                leg)),
            now);
}

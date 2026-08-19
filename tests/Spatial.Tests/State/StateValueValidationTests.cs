using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class StateValueValidationTests
{
    [Fact]
    public void CurrentLeg_RequiresPositiveDurationAndMatchingPortalShape()
    {
        CellRef from = TestSpatialDefinitionBuilder.Cell("world", 0, 0);
        CellRef to = TestSpatialDefinitionBuilder.Cell("world", 1, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrentLeg(
            from,
            to,
            SpatialEdgeKind.Orthogonal,
            portalId: null,
            ModelTime.Zero,
            ModelTime.Zero,
            journeyGeneration: 1));
        Assert.Throws<ArgumentException>(() => new CurrentLeg(
            from,
            to,
            SpatialEdgeKind.Portal,
            portalId: null,
            ModelTime.Zero,
            ModelDuration.FromSeconds(1) + ModelTime.Zero,
            journeyGeneration: 1));
    }

    [Fact]
    public void Journey_RequiresPositiveGenerationMatchingCurrentLeg()
    {
        CurrentLeg leg = SpatialEventTestHarness.Leg(
            TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            TestSpatialDefinitionBuilder.Cell("world", 1, 0),
            generation: 1);

        Assert.Throws<ArgumentException>(() => new JourneyState(
            new JourneyId(1),
            new EntityId(1),
            new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 1, 1)),
            generation: 2,
            leg));
    }

    [Fact]
    public void CellOverride_RejectsNonPositiveMoveCost()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellOverride(moveCost: 0));
    }
}

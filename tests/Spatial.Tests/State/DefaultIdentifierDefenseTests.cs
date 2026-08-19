using System.Runtime.CompilerServices;

namespace DramaBoard.Spatial.Tests;

public sealed class DefaultIdentifierDefenseTests
{
    [Fact]
    public void PersistentValueConstructors_RejectDefaultNumericIdentifiers()
    {
        CellRef cell = TestSpatialDefinitionBuilder.Cell("world", 0, 0);
        CurrentLeg leg = SpatialEventTestHarness.Leg(
            cell,
            TestSpatialDefinitionBuilder.Cell("world", 1, 0),
            generation: 1);

        Assert.Throws<ArgumentException>(() => new SpatialEntityState(default, cell, true, 0));
        Assert.Throws<ArgumentException>(() => new JourneyState(
            default,
            new EntityId(1),
            new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 1, 1)),
            1,
            leg));
        Assert.Throws<ArgumentException>(() => new JourneyState(
            new JourneyId(1),
            default,
            new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 1, 1)),
            1,
            leg));
        Assert.Throws<ArgumentException>(() => new ScheduledSpatialMutationState(
            default,
            SpatialEventTestHarness.AtSecond(1),
            new SetPortalStateMutation(new PortalId("world-to-town"), false)));
    }

    [Fact]
    public void CompleteValidator_DefendsAgainstUninitializedPersistentObject()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition).Rebuild(
            entities:
            [
                (SpatialEntityState)RuntimeHelpers.GetUninitializedObject(typeof(SpatialEntityState)),
            ]);

        Assert.Throws<InvalidOperationException>(() => SpatialStateValidator.ValidateComplete(definition, state));
    }
}

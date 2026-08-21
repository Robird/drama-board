namespace DramaBoard.Spatial.Tests;

public sealed class MutationProjectionTests
{
    [Fact]
    public void Mutation_ScheduleApplyAndConsume_StrictlyAdvancePersistentAllocator()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            SpatialEventTestHarness.AtSecond(5),
            new SetPortalStateMutation(new PortalId("world-to-town"), isEnabled: false));

        state = SpatialEventTestHarness.Apply(definition, state, new MutationScheduledEvent(mutation));
        Assert.Equal(2, state.NextMutationOrdinal);
        Assert.Equal(mutation, Assert.Single(state.ScheduledMutations));

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new PortalStateChangedEvent(
                new PortalId("world-to-town"),
                expectedOverride: null,
                resultingOverride: false),
            SpatialEventTestHarness.AtSecond(5));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationConsumedEvent(mutation),
            SpatialEventTestHarness.AtSecond(5));

        Assert.Empty(state.ScheduledMutations);
        Assert.Equal(2, state.NextMutationOrdinal);
    }

    [Fact]
    public void Mutation_SameTargetAndDue_CannotEnterStateTwice()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(new ScheduledSpatialMutationState(
                new ScheduledMutationId(1),
                SpatialEventTestHarness.AtSecond(5),
                new SetPortalStateMutation(new PortalId("world-to-town"), false))));

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(new ScheduledSpatialMutationState(
                new ScheduledMutationId(2),
                SpatialEventTestHarness.AtSecond(5),
                new SetPortalStateMutation(new PortalId("world-to-town"), true)))));
    }

    [Fact]
    public void Override_ExpectedAndResultingSparseValuesMustMatchExactly()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        var close = new PortalStateChangedEvent(
            new PortalId("world-to-town"),
            expectedOverride: null,
            resultingOverride: false);
        state = SpatialEventTestHarness.Apply(definition, state, close);

        Assert.False(Assert.Single(state.PortalOverrides).IsEnabled);
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            close));
    }
}

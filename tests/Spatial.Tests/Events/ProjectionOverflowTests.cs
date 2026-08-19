namespace DramaBoard.Spatial.Tests;

public sealed class ProjectionOverflowTests
{
    [Fact]
    public void PrimaryRevisionOverflow_ThrowsWithoutChangingPriorState()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition).Rebuild(revision: long.MaxValue);

        Assert.Throws<OverflowException>(() => SpatialEventTestHarness.Place(definition, state));
        Assert.Equal(long.MaxValue, state.Revision);
        Assert.Empty(state.Entities);
    }

    [Fact]
    public void JourneyAllocatorOverflow_ThrowsWithoutPartialStart()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        state = state.Rebuild(nextJourneyOrdinal: long.MaxValue);
        CurrentLeg leg = SpatialEventTestHarness.Leg(
            TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            TestSpatialDefinitionBuilder.Cell("world", 1, 0),
            generation: 1);

        Assert.Throws<OverflowException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(long.MaxValue),
                new EntityId(1),
                new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 1, 1)),
                1,
                leg))));
        Assert.Empty(state.Journeys);
        Assert.Equal(long.MaxValue, state.NextJourneyOrdinal);
    }

    [Fact]
    public void MutationAndMomentAllocatorOverflow_ThrowWithoutPartialAdvance()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState mutationState = SpatialState.Create(definition).Rebuild(nextMutationOrdinal: long.MaxValue);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(long.MaxValue),
            SpatialEventTestHarness.AtSecond(1),
            new SetPortalStateMutation(new PortalId("world-to-town"), false));
        Assert.Throws<OverflowException>(() => SpatialEventTestHarness.Apply(
            definition,
            mutationState,
            new MutationScheduledEvent(mutation)));
        Assert.Empty(mutationState.ScheduledMutations);

        SpatialState momentState = SpatialState.Create(definition).Rebuild(nextMomentOrdinal: long.MaxValue);
        Assert.Throws<OverflowException>(() => SpatialEventTestHarness.Apply(
            definition,
            momentState,
            new MomentResolvedEvent(long.MaxValue, resolvedWorkCount: 1)));
        Assert.Equal(long.MaxValue, momentState.NextMomentOrdinal);
    }

    [Fact]
    public void MovementGenerationOverflow_ThrowsWithoutPartialCompletion()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState placed = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        SpatialEntityState maxGenerationEntity = placed.Entities.Single().With(
            movementGeneration: long.MaxValue);
        SpatialState state = placed.Rebuild(entities: [maxGenerationEntity]);

        Assert.Throws<OverflowException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyCompletedEvent(
                new EntityId(1),
                new JourneyId(1),
                new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 0, 0)),
                expectedGeneration: long.MaxValue,
                resultingGeneration: long.MaxValue,
                JourneyCompletionReason.AssignedAlreadySatisfied)));
        Assert.Equal(long.MaxValue, state.Entities.Single().MovementGeneration);
        Assert.Equal(1, state.NextJourneyOrdinal);
    }
}

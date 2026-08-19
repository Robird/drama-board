namespace DramaBoard.Spatial.Tests;

public sealed class JourneyProjectionTests
{
    [Fact]
    public void Journey_StartStepContinueCancel_UsesStableAllocatorAndGenerations()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        CellRef from = Cell(0, 0);
        CellRef middle = Cell(1, 0);
        CellRef goal = Cell(1, 1);
        CurrentLeg first = SpatialEventTestHarness.Leg(from, middle, 1);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1), new EntityId(1), new CellGoal(goal), 1, first)));

        Assert.Equal(2, state.NextJourneyOrdinal);
        Assert.Equal(1, state.Entities.Single().MovementGeneration);

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntitySteppedEvent(new EntityId(1), new JourneyId(1), from, middle, 1),
            SpatialEventTestHarness.AtSecond(1));
        CurrentLeg second = SpatialEventTestHarness.Leg(middle, goal, 1, 1, 2);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyContinuedEvent(new EntityId(1), new JourneyId(1), first, second),
            SpatialEventTestHarness.AtSecond(1));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyCancelledEvent(new EntityId(1), new JourneyId(1), 1, 2),
            SpatialEventTestHarness.AtSecond(1));

        Assert.Empty(state.Journeys);
        Assert.Equal(2, state.Entities.Single().MovementGeneration);
        Assert.Equal(2, state.NextJourneyOrdinal);

        CurrentLeg third = SpatialEventTestHarness.Leg(middle, goal, 3, 1, 2);
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1), new EntityId(1), new CellGoal(goal), 3, third)),
            SpatialEventTestHarness.AtSecond(1)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(2), new EntityId(1), new CellGoal(goal), 3, third)),
            SpatialEventTestHarness.AtSecond(1));
        Assert.Equal(3, state.NextJourneyOrdinal);
    }

    [Fact]
    public void Journey_RemoveEntity_AtomicallyRemovesExactActiveJourney()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = StartJourney(definition, Cell(1, 1), out _);

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntityRemovedEvent(new EntityId(1), expectedMovementGeneration: 1, expectedActiveJourneyId: null)));

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntityRemovedEvent(new EntityId(1), 1, new JourneyId(1)));

        Assert.Empty(state.Entities);
        Assert.Empty(state.Journeys);
    }

    [Fact]
    public void Journey_CompletionThreeBranches_HaveDistinctAllocatorAndGenerationEffects()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        CellRef origin = Cell(0, 0);

        SpatialState assigned = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        assigned = SpatialEventTestHarness.Apply(
            definition,
            assigned,
            new JourneyCompletedEvent(
                new EntityId(1),
                new JourneyId(1),
                new CellGoal(origin),
                expectedGeneration: 0,
                resultingGeneration: 1,
                JourneyCompletionReason.AssignedAlreadySatisfied));
        Assert.Equal(2, assigned.NextJourneyOrdinal);
        Assert.Equal(1, assigned.Entities.Single().MovementGeneration);

        SpatialState retargeted = StartJourney(definition, Cell(1, 1), out _);
        retargeted = SpatialEventTestHarness.Apply(
            definition,
            retargeted,
            new JourneyCompletedEvent(
                new EntityId(1),
                new JourneyId(1),
                new CellGoal(origin),
                expectedGeneration: 1,
                resultingGeneration: 2,
                JourneyCompletionReason.RetargetedAlreadySatisfied));
        Assert.Equal(2, retargeted.NextJourneyOrdinal);
        Assert.Equal(2, retargeted.Entities.Single().MovementGeneration);
        Assert.Empty(retargeted.Journeys);

        SpatialState reached = StartJourney(definition, Cell(1, 0), out CurrentLeg reachedLeg);
        reached = SpatialEventTestHarness.Apply(
            definition,
            reached,
            new EntitySteppedEvent(new EntityId(1), new JourneyId(1), reachedLeg.From, reachedLeg.To, 1),
            SpatialEventTestHarness.AtSecond(1));
        reached = SpatialEventTestHarness.Apply(
            definition,
            reached,
            new JourneyCompletedEvent(
                new EntityId(1),
                new JourneyId(1),
                new CellGoal(Cell(1, 0)),
                expectedGeneration: 1,
                resultingGeneration: 1,
                JourneyCompletionReason.ReachedGoal,
                reachedLeg),
            SpatialEventTestHarness.AtSecond(1));
        Assert.Equal(2, reached.NextJourneyOrdinal);
        Assert.Equal(1, reached.Entities.Single().MovementGeneration);
        Assert.Empty(reached.Journeys);
    }

    [Fact]
    public void Journey_BlockedBeforeAndAfterStep_BothEndWithoutGenerationChange()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState before = StartJourney(definition, Cell(1, 1), out CurrentLeg beforeLeg);
        before = SpatialEventTestHarness.Apply(
            definition,
            before,
            new CellStateChangedEvent(
                beforeLeg.To,
                expectedOverride: null,
                resultingOverride: new CellOverride(blocksMovement: true)),
            SpatialEventTestHarness.AtSecond(1));
        before = SpatialEventTestHarness.Apply(
            definition,
            before,
            new JourneyBlockedEvent(
                new EntityId(1), new JourneyId(1), beforeLeg, JourneyBlockedReason.LegInvalidNoRoute),
            SpatialEventTestHarness.AtSecond(1));
        Assert.Empty(before.Journeys);
        Assert.Equal(1, before.Entities.Single().MovementGeneration);
        Assert.Equal(Cell(0, 0), before.Entities.Single().Cell);

        SpatialState after = StartJourney(definition, Cell(1, 1), out CurrentLeg afterLeg);
        after = SpatialEventTestHarness.Apply(
            definition,
            after,
            new EntitySteppedEvent(new EntityId(1), new JourneyId(1), afterLeg.From, afterLeg.To, 1),
            SpatialEventTestHarness.AtSecond(1));
        after = SpatialEventTestHarness.Apply(
            definition,
            after,
            new JourneyBlockedEvent(
                new EntityId(1), new JourneyId(1), afterLeg, JourneyBlockedReason.NoContinuationAfterStep),
            SpatialEventTestHarness.AtSecond(1));
        Assert.Empty(after.Journeys);
        Assert.Equal(1, after.Entities.Single().MovementGeneration);
        Assert.Equal(Cell(1, 0), after.Entities.Single().Cell);
    }

    private static SpatialState StartJourney(
        SpatialDefinition definition,
        CellRef goal,
        out CurrentLeg leg)
    {
        SpatialState state = SpatialEventTestHarness.Place(definition, SpatialState.Create(definition));
        leg = SpatialEventTestHarness.Leg(Cell(0, 0), Cell(1, 0), generation: 1);
        return SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1), new EntityId(1), new CellGoal(goal), 1, leg)));
    }

    private static CellRef Cell(int x, int y) => TestSpatialDefinitionBuilder.Cell("world", x, y);
}

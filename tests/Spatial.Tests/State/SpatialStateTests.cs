namespace DramaBoard.Spatial.Tests;

public sealed class SpatialStateTests
{
    [Fact]
    public void Create_BindsRuntimeStampAndInitialOrdinalsWithoutDefinitionRevision()
    {
        SpatialDefinition first = CreateDefinition(revision: 1);
        SpatialDefinition second = CreateDefinition(revision: 2);

        SpatialState firstState = SpatialState.Create(first);
        SpatialState secondState = SpatialState.Create(second);

        Assert.Equal(firstState.Definition, secondState.Definition);
        Assert.Equal(first.Id, firstState.Definition.DefinitionId);
        Assert.Equal(first.ContentHash, firstState.Definition.ContentHash);
        Assert.Equal(first.RulesVersion, firstState.Definition.RulesVersion);
        Assert.Equal(0, firstState.Revision);
        Assert.Equal(1, firstState.NextJourneyOrdinal);
        Assert.Equal(1, firstState.NextMutationOrdinal);
        Assert.Empty(firstState.Entities);
    }

    [Fact]
    public void Projection_CanonicalizesAndProtectsPublicCollections()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Place(definition, state, entityId: 2);
        state = SpatialEventTestHarness.Place(definition, state, entityId: 1);

        Assert.Equal([1L, 2L], state.Entities.Select(entity => entity.Id.Value));
        var collection = Assert.IsAssignableFrom<ICollection<SpatialEntityState>>(state.Entities);
        Assert.Throws<NotSupportedException>(() => collection.Add(new SpatialEntityState(
            new EntityId(3),
            TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            true,
            0)));
    }

    [Fact]
    public void CompleteValidator_RejectsStepPrefixUntilJourneyContinues()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition));
        CellRef from = TestSpatialDefinitionBuilder.Cell("world", 0, 0);
        CellRef middle = TestSpatialDefinitionBuilder.Cell("world", 1, 0);
        CellRef goal = TestSpatialDefinitionBuilder.Cell("world", 1, 1);
        CurrentLeg firstLeg = SpatialEventTestHarness.Leg(from, middle, generation: 1);
        var journey = new JourneyState(
            new JourneyId(1),
            new EntityId(1),
            new CellGoal(goal),
            generation: 1,
            firstLeg);
        state = SpatialEventTestHarness.Apply(definition, state, new JourneyStartedEvent(journey));
        SpatialStateValidator.ValidateComplete(definition, state);

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntitySteppedEvent(new EntityId(1), new JourneyId(1), from, middle, 1),
            SpatialEventTestHarness.AtSecond(1));

        Assert.Throws<InvalidOperationException>(() => SpatialStateValidator.ValidateComplete(definition, state));

        CurrentLeg nextLeg = SpatialEventTestHarness.Leg(
            middle,
            goal,
            generation: 1,
            startedAtSeconds: 1,
            dueSeconds: 2);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyContinuedEvent(new EntityId(1), new JourneyId(1), firstLeg, nextLeg),
            SpatialEventTestHarness.AtSecond(1));

        SpatialStateValidator.ValidateComplete(definition, state);
    }

    private static SpatialDefinition CreateDefinition(long revision) =>
        SpatialDefinition.Create(
            new SpatialDefinitionId("revision-independent"),
            revision,
            rulesVersion: 1,
            maps: [TestSpatialDefinitionBuilder.Map("map", width: 1, height: 1)]);
}

using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class DerivedOutcomeAuditTests
{
    [Fact]
    public void DerivedZoneOutcome_ValidatesIdsButDoesNotRequireEntityInPostState()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        SpatialState projected = SpatialEventTestHarness.Apply(
            definition,
            state,
            new ZoneLeftEvent(new EntityId(99), new ZoneId("town-square")));

        Assert.Same(state, projected);
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new ZoneEnteredEvent(default, new ZoneId("town-square"))));
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new ZoneEnteredEvent(new EntityId(1), new ZoneId("missing-zone"))));
    }

    [Fact]
    public void DerivedCoPresenceOutcome_RejectsDefaultEntityIdentifier()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        var invalid = new CoPresenceEndedEvent(default, new EntityId(2));

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            invalid));
    }

    [Fact]
    public void DerivedZoneOutcome_RejectsDirectionContradictingFinalPostState()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        CellRef townSquare = TestSpatialDefinitionBuilder.Cell("town", 1, 0);
        state = SpatialEventTestHarness.Place(definition, state, entityId: 1, cell: townSquare);

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new ZoneLeftEvent(new EntityId(1), new ZoneId("town-square"))));

        SpatialState absent = SpatialState.Create(definition);
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            absent,
            new ZoneEnteredEvent(new EntityId(1), new ZoneId("town-square"))));

        Assert.Same(state, SpatialEventTestHarness.Apply(
            definition,
            state,
            new ZoneEnteredEvent(new EntityId(1), new ZoneId("town-square"))));
        Assert.Same(absent, SpatialEventTestHarness.Apply(
            definition,
            absent,
            new ZoneLeftEvent(new EntityId(1), new ZoneId("town-square"))));
    }

    [Fact]
    public void DerivedCoPresenceOutcome_RejectsDirectionContradictingFinalPostState()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        CellRef firstCell = TestSpatialDefinitionBuilder.Cell("world", 0, 0);
        CellRef secondCell = TestSpatialDefinitionBuilder.Cell("world", 1, 0);
        state = SpatialEventTestHarness.Place(definition, state, entityId: 1, cell: firstCell);
        state = SpatialEventTestHarness.Place(definition, state, entityId: 2, cell: secondCell);

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new CoPresenceStartedEvent(new EntityId(1), new EntityId(2))));

        state = SpatialEventTestHarness.Place(definition, state, entityId: 3, cell: firstCell);
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new CoPresenceEndedEvent(new EntityId(1), new EntityId(3))));

        Assert.Same(state, SpatialEventTestHarness.Apply(
            definition,
            state,
            new CoPresenceStartedEvent(new EntityId(1), new EntityId(3))));
        Assert.Same(state, SpatialEventTestHarness.Apply(
            definition,
            state,
            new CoPresenceEndedEvent(new EntityId(1), new EntityId(2))));
    }

    [Fact]
    public void DerivedVisibilityOutcome_RejectsDefaultAndObserverSelfTargets()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new GeometricVisibilityChangedEvent(default, [new EntityId(2)], [])));
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new GeometricVisibilityChangedEvent(
                new EntityId(1),
                [new EntityId(1)],
                [])));
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new GeometricVisibilityChangedEvent(
                new EntityId(1),
                [default],
                [])));
    }

    [Fact]
    public void DerivedVisibilityOutcome_DoesNotRecomputeDirectionFromFinalLineOfSight()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Place(
            definition,
            state,
            entityId: 1,
            cell: TestSpatialDefinitionBuilder.Cell("world", 0, 0));
        state = SpatialEventTestHarness.Place(
            definition,
            state,
            entityId: 2,
            cell: TestSpatialDefinitionBuilder.Cell("world", 1, 0));
        state = SpatialEventTestHarness.Place(
            definition,
            state,
            entityId: 3,
            cell: TestSpatialDefinitionBuilder.Cell("town", 0, 0));
        state = SpatialEventTestHarness.Place(
            definition,
            state,
            entityId: 4,
            cell: TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            observationEnabled: false);

        GeometricVisibilityChangedEvent[] historicalOutcomes =
        [
            // Entity 2 is currently visible, but replay must not re-prove a committed removal.
            new GeometricVisibilityChangedEvent(
                new EntityId(1),
                addedEntityIds: [],
                removedEntityIds: [new EntityId(2)]),
            // Entity 3 is on another map, but replay must not run LOS to reject the committed addition.
            new GeometricVisibilityChangedEvent(
                new EntityId(1),
                addedEntityIds: [new EntityId(3)],
                removedEntityIds: []),
            // Disabled and subsequently removed observers remain valid historical correlation IDs.
            new GeometricVisibilityChangedEvent(
                new EntityId(4),
                addedEntityIds: [new EntityId(2)],
                removedEntityIds: []),
            new GeometricVisibilityChangedEvent(
                new EntityId(99),
                addedEntityIds: [new EntityId(2)],
                removedEntityIds: []),
        ];

        foreach (GeometricVisibilityChangedEvent historicalOutcome in historicalOutcomes)
        {
            Assert.Same(
                state,
                SpatialEventTestHarness.Apply(definition, state, historicalOutcome));
        }
    }

    [Fact]
    public void VisibilityEvent_RejectsEmptyDuplicateAndOverlappingSets()
    {
        Assert.Throws<ArgumentException>(() => new GeometricVisibilityChangedEvent(
            new EntityId(1),
            [],
            []));
        Assert.Throws<ArgumentException>(() => new GeometricVisibilityChangedEvent(
            new EntityId(1),
            [new EntityId(2), new EntityId(2)],
            []));
        Assert.Throws<ArgumentException>(() => new GeometricVisibilityChangedEvent(
            new EntityId(1),
            [new EntityId(2)],
            [new EntityId(2)]));
    }

    [Fact]
    public void LegalRemovalAndObserverDisableTransitions_PassDirectionalAudit()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        state = SpatialEventTestHarness.Place(
            definition,
            state,
            entityId: 1,
            cell: TestSpatialDefinitionBuilder.Cell("world", 0, 0));
        state = SpatialEventTestHarness.Place(
            definition,
            state,
            entityId: 2,
            cell: TestSpatialDefinitionBuilder.Cell("world", 1, 0));

        SpatialTransitionResult removed = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new EntityRemovedEvent(
                new EntityId(1),
                expectedMovementGeneration: 0,
                expectedActiveJourneyId: null)]);
        Assert.Contains(removed.Facts, value =>
            value is GeometricVisibilityChangedEvent visibility &&
            visibility.ObserverId == new EntityId(1) &&
            visibility.RemovedEntityIds.SequenceEqual([new EntityId(2)]));

        SpatialTransitionResult disabled = SpatialTransition.Complete(
            definition,
            state,
            ModelTime.Zero,
            [new ObservationStateChangedEvent(new EntityId(1), true, false)]);
        Assert.Contains(disabled.Facts, value =>
            value is GeometricVisibilityChangedEvent visibility &&
            visibility.ObserverId == new EntityId(1) &&
            visibility.RemovedEntityIds.SequenceEqual([new EntityId(2)]));
    }

    [Fact]
    public void VisibilityEvent_UsesStructuralCanonicalValueEqualityAndClonesInputs()
    {
        var firstAdded = new List<EntityId> { new(3), new(2) };
        var firstRemoved = new List<EntityId> { new(5), new(4) };
        var first = new GeometricVisibilityChangedEvent(
            new EntityId(1),
            firstAdded,
            firstRemoved);
        var second = new GeometricVisibilityChangedEvent(
            new EntityId(1),
            [new EntityId(2), new EntityId(3)],
            [new EntityId(4), new EntityId(5)]);

        firstAdded.Add(new EntityId(6));
        firstRemoved.Clear();

        Assert.Equal(second, first);
        Assert.True(first == second);
        Assert.Equal(second.GetHashCode(), first.GetHashCode());
        Assert.Equal([2L, 3L], first.AddedEntityIds.Select(id => id.Value));
        Assert.Equal([4L, 5L], first.RemovedEntityIds.Select(id => id.Value));
    }
}

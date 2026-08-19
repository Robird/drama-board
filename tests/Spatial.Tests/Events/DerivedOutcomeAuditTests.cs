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

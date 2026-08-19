using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class EffectiveSpatialTopologyTests
{
    [Fact]
    public void StaticWallOpenedByOverride_IsSharedByQueriesNavigatorAndProjector()
    {
        CellRef source = Cell("map", 0, 0);
        CellRef target = Cell("map", 1, 0);
        CellRef beyond = Cell("map", 2, 0);
        SpatialDefinition definition = Definition(
            [
                Map(
                    "map",
                    3,
                    1,
                    stepTicks: 10,
                    [
                        Floor(),
                        Floor(moveCost: 2, blocksMovement: true, blocksSight: true),
                        Floor(),
                    ]),
            ]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        var staticTopology = new EffectiveSpatialTopology(definition, state);
        var queries = new SpatialQueries(definition);

        Assert.True(staticTopology.BlocksMovement(target));
        Assert.True(staticTopology.BlocksSight(target));
        Assert.Equal(2, staticTopology.GetMoveCost(target));
        Assert.IsType<PathSearchResult.Unreachable>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));
        Assert.False(queries.IsCellVisible(state, new EntityId(1), beyond));

        var opened = new CellOverride(
            blocksMovement: false,
            blocksSight: false,
            moveCost: 3);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(target, expectedOverride: null, resultingOverride: opened));
        var dynamicTopology = new EffectiveSpatialTopology(definition, state);

        Assert.False(dynamicTopology.BlocksMovement(target));
        Assert.False(dynamicTopology.BlocksSight(target));
        Assert.Equal(3, dynamicTopology.GetMoveCost(target));
        Assert.True(queries.IsCellVisible(state, new EntityId(1), beyond));

        PathSearchResult.NextStep selected = Assert.IsType<PathSearchResult.NextStep>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));
        Assert.Equal(new ModelDuration(30), selected.Edge.Duration);
        CurrentLeg leg = Leg(selected.Edge);
        Assert.True(dynamicTopology.IsLegPassable(leg));
        Assert.Equal(selected.Edge.Duration, dynamicTopology.GetTraversalDuration(leg));

        SpatialState started = StartJourney(definition, state, leg, new CellGoal(target));
        SpatialStateValidator.ValidateComplete(definition, started);
        Assert.Equal(leg, Assert.Single(started.Journeys).CurrentLeg);
    }

    [Fact]
    public void DynamicWall_IsRejectedConsistentlyByQueriesNavigatorAndProjector()
    {
        CellRef source = Cell("map", 0, 0);
        CellRef wall = Cell("map", 1, 0);
        CellRef goal = Cell("map", 2, 0);
        SpatialDefinition definition = Definition([Map("map", 3, 1, stepTicks: 10)]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        var blocked = new CellOverride(blocksMovement: true, blocksSight: true);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(wall, expectedOverride: null, resultingOverride: blocked));
        var topology = new EffectiveSpatialTopology(definition, state);

        Assert.True(topology.BlocksMovement(wall));
        Assert.True(topology.BlocksSight(wall));
        Assert.IsType<PathSearchResult.Unreachable>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(goal)));
        Assert.False(new SpatialQueries(definition).IsCellVisible(state, new EntityId(1), goal));

        var blockedEdge = new NavigationEdge(
            source,
            wall,
            SpatialEdgeKind.Orthogonal,
            PortalId: null,
            topology.GetTraversalDuration(SpatialEdgeKind.Orthogonal, wall, portalId: null));
        CurrentLeg leg = Leg(blockedEdge);
        Assert.False(topology.IsLegPassable(leg));
        Assert.Throws<InvalidOperationException>(() =>
            StartJourney(definition, state, leg, new CellGoal(goal)));
    }

    [Fact]
    public void EnabledPortalDuration_IsSharedByNavigatorAndProjector()
    {
        CellRef source = Cell("from", 0, 0);
        CellRef target = Cell("to", 0, 0);
        var portal = new PortalDefinition(
            new PortalId("gate"),
            source,
            target,
            new ModelDuration(7),
            initiallyEnabled: false);
        SpatialDefinition definition = Definition(
            [Map("from", 1, 1), Map("to", 1, 1)],
            [portal]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        var disabledTopology = new EffectiveSpatialTopology(definition, state);

        Assert.False(disabledTopology.IsPortalEnabled(portal.Id));
        Assert.IsType<PathSearchResult.Unreachable>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new PortalStateChangedEvent(
                portal.Id,
                expectedOverride: null,
                resultingOverride: true));
        var enabledTopology = new EffectiveSpatialTopology(definition, state);
        PathSearchResult.NextStep selected = Assert.IsType<PathSearchResult.NextStep>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));
        CurrentLeg leg = Leg(selected.Edge);

        Assert.True(enabledTopology.IsPortalEnabled(portal.Id));
        Assert.True(enabledTopology.IsLegPassable(leg));
        Assert.Equal(portal.TraversalDuration, selected.Edge.Duration);
        Assert.Equal(portal.TraversalDuration, enabledTopology.GetTraversalDuration(leg));

        SpatialState started = StartJourney(definition, state, leg, new CellGoal(target));
        SpatialStateValidator.ValidateComplete(definition, started);
        Assert.Equal(leg, Assert.Single(started.Journeys).CurrentLeg);
    }

    [Fact]
    public void PortalClosedAtDue_RejectsStepAndAcceptsBlockedOutcome()
    {
        CellRef source = Cell("from", 0, 0);
        CellRef target = Cell("to", 0, 0);
        var portal = new PortalDefinition(
            new PortalId("gate"),
            source,
            target,
            new ModelDuration(7),
            initiallyEnabled: true);
        SpatialDefinition definition = Definition(
            [Map("from", 1, 1), Map("to", 1, 1)],
            [portal]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        PathSearchResult.NextStep selected = Assert.IsType<PathSearchResult.NextStep>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));
        CurrentLeg leg = Leg(selected.Edge);
        state = StartJourney(definition, state, leg, new CellGoal(target));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new PortalStateChangedEvent(
                portal.Id,
                expectedOverride: null,
                resultingOverride: false),
            leg.Due);

        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntitySteppedEvent(
                new EntityId(1),
                new JourneyId(1),
                source,
                target,
                journeyGeneration: 1),
            leg.Due));

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyBlockedEvent(
                new EntityId(1),
                new JourneyId(1),
                leg,
                JourneyBlockedReason.LegInvalidNoRoute),
            leg.Due);
        Assert.Empty(state.Journeys);
        Assert.Equal(source, Assert.Single(state.Entities).Cell);
    }

    [Fact]
    public void BlockedPortalTarget_IsRejectedByNavigatorAndProjector()
    {
        CellRef source = Cell("from", 0, 0);
        CellRef target = Cell("to", 0, 0);
        var portal = new PortalDefinition(
            new PortalId("gate"),
            source,
            target,
            new ModelDuration(7),
            initiallyEnabled: true);
        SpatialDefinition definition = Definition(
            [Map("from", 1, 1), Map("to", 1, 1)],
            [portal]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(
                target,
                expectedOverride: null,
                resultingOverride: new CellOverride(blocksMovement: true)));
        var topology = new EffectiveSpatialTopology(definition, state);
        var edge = new NavigationEdge(
            source,
            target,
            SpatialEdgeKind.Portal,
            portal.Id,
            portal.TraversalDuration);
        CurrentLeg leg = Leg(edge);

        Assert.True(topology.IsPortalEnabled(portal.Id));
        Assert.True(topology.BlocksMovement(target));
        Assert.False(topology.IsLegPassable(leg));
        Assert.IsType<PathSearchResult.Unreachable>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));
        Assert.Throws<InvalidOperationException>(() =>
            StartJourney(definition, state, leg, new CellGoal(target)));
    }

    [Fact]
    public void BlockedOrthogonalSource_DoesNotPreventLeavingItsCell()
    {
        CellRef source = Cell("map", 0, 0);
        CellRef target = Cell("map", 1, 0);
        SpatialDefinition definition = Definition([Map("map", 2, 1, stepTicks: 10)]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(
                source,
                expectedOverride: null,
                resultingOverride: new CellOverride(blocksMovement: true)));

        PathSearchResult.NextStep selected = Assert.IsType<PathSearchResult.NextStep>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));
        CurrentLeg leg = Leg(selected.Edge);

        Assert.True(new EffectiveSpatialTopology(definition, state).IsLegPassable(leg));
        SpatialState started = StartJourney(definition, state, leg, new CellGoal(target));
        Assert.Equal(leg, Assert.Single(started.Journeys).CurrentLeg);
    }

    [Fact]
    public void BlockedPortalSource_DoesNotPreventLeavingItsCell()
    {
        CellRef source = Cell("from", 0, 0);
        CellRef target = Cell("to", 0, 0);
        var portal = new PortalDefinition(
            new PortalId("gate"),
            source,
            target,
            new ModelDuration(7),
            initiallyEnabled: true);
        SpatialDefinition definition = Definition(
            [Map("from", 1, 1), Map("to", 1, 1)],
            [portal]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(
                source,
                expectedOverride: null,
                resultingOverride: new CellOverride(blocksMovement: true)));

        PathSearchResult.NextStep selected = Assert.IsType<PathSearchResult.NextStep>(
            SpatialNavigator.FindNextStep(definition, state, source, new CellGoal(target)));
        CurrentLeg leg = Leg(selected.Edge);

        Assert.Equal(SpatialEdgeKind.Portal, selected.Edge.EdgeKind);
        Assert.True(new EffectiveSpatialTopology(definition, state).IsLegPassable(leg));
        SpatialState started = StartJourney(definition, state, leg, new CellGoal(target));
        Assert.Equal(leg, Assert.Single(started.Journeys).CurrentLeg);
    }

    [Fact]
    public void PortalMutationConsumption_AcceptsIdempotentDefaultAndClearedOverride()
    {
        CellRef source = Cell("from", 0, 0);
        CellRef target = Cell("to", 0, 0);
        var portal = new PortalDefinition(
            new PortalId("gate"),
            source,
            target,
            new ModelDuration(7),
            initiallyEnabled: true);
        SpatialDefinition definition = Definition(
            [Map("from", 1, 1), Map("to", 1, 1)],
            [portal]);
        ModelTime due = SpatialEventTestHarness.AtSecond(5);
        var idempotent = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetPortalStateMutation(portal.Id, isEnabled: true));
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(idempotent));

        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationConsumedEvent(idempotent),
            due);
        Assert.Empty(state.ScheduledMutations);
        Assert.Empty(state.PortalOverrides);

        var cleared = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetPortalStateMutation(portal.Id, isEnabled: true));
        state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new PortalStateChangedEvent(
                portal.Id,
                expectedOverride: null,
                resultingOverride: false));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(cleared));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new PortalStateChangedEvent(
                portal.Id,
                expectedOverride: false,
                resultingOverride: null),
            due);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationConsumedEvent(cleared),
            due);

        Assert.Empty(state.ScheduledMutations);
        Assert.Empty(state.PortalOverrides);
    }

    [Fact]
    public void CellMutationConsumption_AcceptsExactNonEmptyAndClearedSparseValues()
    {
        CellRef cell = Cell("map", 0, 0);
        SpatialDefinition definition = Definition([Map("map", 1, 1)]);
        ModelTime due = SpatialEventTestHarness.AtSecond(5);
        var blocked = new CellOverride(blocksMovement: true);
        var setMutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(cell, blocked));
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(setMutation));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(cell, expectedOverride: null, resultingOverride: blocked),
            due);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationConsumedEvent(setMutation),
            due);

        Assert.Empty(state.ScheduledMutations);
        Assert.Equal(blocked, Assert.Single(state.CellOverrides).Value);

        var clearMutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(cell, Value: null));
        state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new CellStateChangedEvent(cell, expectedOverride: null, resultingOverride: blocked));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationScheduledEvent(clearMutation));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(cell, expectedOverride: blocked, resultingOverride: null),
            due);
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationConsumedEvent(clearMutation),
            due);

        Assert.Empty(state.ScheduledMutations);
        Assert.Empty(state.CellOverrides);
    }

    [Fact]
    public void CellMutationConsumption_RejectsSparseMismatchDespiteSameMovementEffect()
    {
        CellRef cell = Cell("map", 0, 0);
        SpatialDefinition definition = Definition([Map("map", 1, 1)]);
        ModelTime due = SpatialEventTestHarness.AtSecond(5);
        var expected = new CellOverride(blocksMovement: true);
        var actual = new CellOverride(blocksMovement: true, blocksSight: true);
        var mutation = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(cell, expected));
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(mutation));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new CellStateChangedEvent(cell, expectedOverride: null, resultingOverride: actual),
            due);

        Assert.True(new EffectiveSpatialTopology(definition, state).BlocksMovement(cell));
        Assert.Throws<InvalidOperationException>(() => SpatialEventTestHarness.Apply(
            definition,
            state,
            new MutationConsumedEvent(mutation),
            due));
        Assert.Equal(mutation, Assert.Single(state.ScheduledMutations));
        Assert.Equal(actual, Assert.Single(state.CellOverrides).Value);
    }

    [Fact]
    public void NavigatorAndTopology_AcceptLegalSteppedPrefixWithoutCompleteValidation()
    {
        CellRef source = Cell("map", 0, 0);
        CellRef middle = Cell("map", 1, 0);
        CellRef goal = Cell("map", 2, 0);
        SpatialDefinition definition = Definition([Map("map", 3, 1, stepTicks: 10)]);
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            cell: source);
        var firstEdge = new NavigationEdge(
            source,
            middle,
            SpatialEdgeKind.Orthogonal,
            PortalId: null,
            new ModelDuration(10));
        CurrentLeg firstLeg = Leg(firstEdge);
        state = StartJourney(definition, state, firstLeg, new CellGoal(goal));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntitySteppedEvent(
                new EntityId(1),
                new JourneyId(1),
                source,
                middle,
                journeyGeneration: 1),
            firstLeg.Due);

        Assert.Throws<InvalidOperationException>(() =>
            SpatialStateValidator.ValidateComplete(definition, state));
        _ = new EffectiveSpatialTopology(definition, state);
        PathSearchResult.NextStep selected = Assert.IsType<PathSearchResult.NextStep>(
            SpatialNavigator.FindNextStep(definition, state, middle, new CellGoal(goal)));
        Assert.Equal(middle, selected.Edge.From);
        Assert.Equal(goal, selected.Edge.To);
    }

    [Fact]
    public void Constructor_RejectsDefinitionStampMismatch()
    {
        SpatialDefinition expected = Definition([Map("expected", 1, 1)]);
        SpatialDefinition other = Definition([Map("other", 1, 1)]);

        Assert.Throws<InvalidOperationException>(() =>
            new EffectiveSpatialTopology(expected, SpatialState.Create(other)));
    }

    private static SpatialState StartJourney(
        SpatialDefinition definition,
        SpatialState state,
        CurrentLeg leg,
        MoveGoal goal) =>
        SpatialEventTestHarness.Apply(
            definition,
            state,
            new JourneyStartedEvent(new JourneyState(
                new JourneyId(1),
                new EntityId(1),
                goal,
                generation: 1,
                leg)),
            leg.StartedAt);

    private static CurrentLeg Leg(NavigationEdge edge) =>
        new(
            edge.From,
            edge.To,
            edge.EdgeKind,
            edge.PortalId,
            ModelTime.Zero,
            ModelTime.Zero + edge.Duration,
            journeyGeneration: 1);

    private static SpatialDefinition Definition(
        IReadOnlyList<GridMapDefinition> maps,
        IReadOnlyList<PortalDefinition>? portals = null) =>
        TestSpatialDefinitionBuilder.Create(maps, portals);

    private static GridMapDefinition Map(
        string id,
        int width,
        int height,
        long stepTicks = 1,
        IReadOnlyList<CellDefinition>? cells = null) =>
        TestSpatialDefinitionBuilder.Map(
            id,
            width,
            height,
            new ModelDuration(stepTicks),
            visionRange: 4,
            cells);

    private static CellDefinition Floor(
        int moveCost = 1,
        bool blocksMovement = false,
        bool blocksSight = false) =>
        TestSpatialDefinitionBuilder.Floor(
            moveCost: moveCost,
            blocksMovement: blocksMovement,
            blocksSight: blocksSight);

    private static CellRef Cell(string mapId, int x, int y) =>
        TestSpatialDefinitionBuilder.Cell(mapId, x, y);
}

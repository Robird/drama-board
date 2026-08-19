using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialCommandHandlerTests
{
    [Fact]
    public void HandleBatch_PlaceThenAssign_UsesPhaseOrderAndFormalReplayMatches()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState preState = SpatialState.Create(definition);
        SpatialCommand[] commands =
        [
            new AssignMoveGoalCommand(Id("01-assign"), Entity(1), new CellGoal(Cell(1))),
            new PlaceEntityCommand(Id("99-place"), Entity(1), Cell(0), observationEnabled: false),
        ];

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(preState, commands, ModelTime.Zero);

        Assert.Collection(
            batch.Events.Select(value => value.Payload),
            payload => Assert.IsType<EntityPlacedEvent>(payload),
            payload =>
            {
                JourneyStartedEvent started = Assert.IsType<JourneyStartedEvent>(payload);
                Assert.Equal(new JourneyId(1), started.Journey.Id);
                Assert.Equal(Cell(0), started.Journey.CurrentLeg.From);
                Assert.Equal(Cell(1), started.Journey.CurrentLeg.To);
                Assert.Equal(new ModelTime(1), started.Journey.CurrentLeg.Due);
            });
        Assert.All(batch.Results, result => Assert.Equal(SpatialCommandDisposition.Accepted, result.Disposition));
        SpatialState replayed = Fold(definition, preState, batch, ModelTime.Zero);
        Assert.Equal(2, replayed.Revision);
        Assert.Equal(2, replayed.NextJourneyOrdinal);
        Assert.Equal(1, Assert.Single(replayed.Entities).MovementGeneration);
        SpatialStateValidator.ValidateComplete(definition, replayed);
    }

    [Fact]
    public void HandleBatch_CommandPermutation_DoesNotAffectEventsOrResults()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        SpatialCommand[] canonical =
        [
            new PlaceEntityCommand(Id("place-2"), Entity(2), TestSpatialDefinitionBuilder.Cell("world", 0, 0), false),
            new SetPortalStateCommand(Id("portal-2"), new PortalId("world-to-town"), false),
            new AssignMoveGoalCommand(
                Id("assign-1"),
                Entity(1),
                new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 1, 0))),
            new PlaceEntityCommand(Id("place-1"), Entity(1), TestSpatialDefinitionBuilder.Cell("world", 0, 0), true),
            new SetPortalStateCommand(Id("portal-1"), new PortalId("world-to-town"), false),
            new ScheduleSpatialMutationCommand(
                Id("schedule"),
                new ModelTime(10),
                new SetPortalStateMutation(new PortalId("world-to-town"), true)),
        ];
        SpatialCommandBatchResult first = Handler(definition).HandleBatch(state, canonical, ModelTime.Zero);
        SpatialCommandBatchResult second = Handler(definition).HandleBatch(
            state,
            canonical.Reverse().ToArray(),
            ModelTime.Zero);

        Assert.Equal(first.Results, second.Results);
        Assert.Equal(
            first.Events.Select(value => (value.Kind, value.Payload)),
            second.Events.Select(value => (value.Kind, value.Payload)));
        SpatialCommandResult alias = Result(first, "portal-2");
        Assert.Equal(SpatialCommandDisposition.AcceptedAlias, alias.Disposition);
        Assert.Equal(Id("portal-1"), alias.AliasOfCommandId);
    }

    [Fact]
    public void HandleBatch_RemoveAndOtherEntityIntent_RejectsWholeEntityGroup()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState state = Place(definition, Entity(1), Cell(0));
        SpatialCommand[] commands =
        [
            new RemoveEntityCommand(Id("remove"), Entity(1)),
            new SetObservationEnabledCommand(Id("observe"), Entity(1), false),
            new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1))),
        ];

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(state, commands, ModelTime.Zero);

        Assert.Empty(batch.Events);
        Assert.All(batch.Results, result =>
        {
            Assert.Equal(SpatialCommandDisposition.Rejected, result.Disposition);
            Assert.Equal(SpatialCommandRejectionCode.ConflictingCommands, result.RejectionCode);
        });
    }

    [Fact]
    public void HandleBatch_UnreachableAndCostOverflow_DoNotConsumeJourneyState()
    {
        SpatialDefinition unreachableDefinition = LineDefinition(
            width: 2,
            cells: [Floor(), Floor(blocksMovement: true)]);
        SpatialState unreachableState = Place(unreachableDefinition, Entity(1), Cell(0));

        SpatialCommandBatchResult unreachable = Handler(unreachableDefinition).HandleBatch(
            unreachableState,
            [new AssignMoveGoalCommand(Id("move"), Entity(1), new CellGoal(Cell(1)))],
            ModelTime.Zero);

        Assert.Empty(unreachable.Events);
        Assert.Equal(SpatialCommandRejectionCode.JourneyUnreachable, Assert.Single(unreachable.Results).RejectionCode);

        CellRef a = TestSpatialDefinitionBuilder.Cell("a", 0, 0);
        CellRef b = TestSpatialDefinitionBuilder.Cell("b", 0, 0);
        CellRef c = TestSpatialDefinitionBuilder.Cell("c", 0, 0);
        SpatialDefinition overflowDefinition = TestSpatialDefinitionBuilder.Create(
            maps:
            [
                TestSpatialDefinitionBuilder.Map("a", 1, 1),
                TestSpatialDefinitionBuilder.Map("b", 1, 1),
                TestSpatialDefinitionBuilder.Map("c", 1, 1),
            ],
            portals:
            [
                new PortalDefinition(new PortalId("ab"), a, b, new ModelDuration(long.MaxValue), true),
                new PortalDefinition(new PortalId("bc"), b, c, new ModelDuration(1), true),
            ]);
        SpatialState overflowState = Place(overflowDefinition, Entity(1), a);

        SpatialCommandBatchResult overflow = Handler(overflowDefinition).HandleBatch(
            overflowState,
            [new AssignMoveGoalCommand(Id("move"), Entity(1), new CellGoal(c))],
            ModelTime.Zero);

        Assert.Empty(overflow.Events);
        Assert.Equal(SpatialCommandRejectionCode.NavigationCostOverflow, Assert.Single(overflow.Results).RejectionCode);
        Assert.Equal(1, overflowState.NextJourneyOrdinal);
        Assert.Equal(0, Assert.Single(overflowState.Entities).MovementGeneration);
    }

    [Fact]
    public void HandleBatch_AbsoluteLegDueOverflow_RejectsWithoutAllocation()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState state = Place(definition, Entity(1), Cell(0));

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [new AssignMoveGoalCommand(Id("move"), Entity(1), new CellGoal(Cell(1)))],
            new ModelTime(long.MaxValue));

        Assert.Empty(batch.Events);
        Assert.Equal(SpatialCommandRejectionCode.ModelTimeOverflow, Assert.Single(batch.Results).RejectionCode);
        Assert.Equal(1, state.NextJourneyOrdinal);
    }

    [Fact]
    public void HandleBatch_SameBatchScheduleAlias_AllocatesExactlyOnce()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        var mutation = new SetPortalStateMutation(new PortalId("world-to-town"), false);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new ScheduleSpatialMutationCommand(Id("b"), new ModelTime(10), mutation),
                new ScheduleSpatialMutationCommand(Id("a"), new ModelTime(10), mutation),
            ],
            ModelTime.Zero);

        MutationScheduledEvent scheduled = Assert.IsType<MutationScheduledEvent>(Assert.Single(batch.Events).Payload);
        Assert.Equal(new ScheduledMutationId(1), scheduled.Mutation.Id);
        Assert.Equal(SpatialCommandDisposition.Accepted, Result(batch, "a").Disposition);
        SpatialCommandResult alias = Result(batch, "b");
        Assert.Equal(SpatialCommandDisposition.AcceptedAlias, alias.Disposition);
        Assert.Equal(Id("a"), alias.AliasOfCommandId);
        Assert.Equal(new ScheduledMutationId(1), alias.ScheduledMutationId);
        SpatialState replayed = Fold(definition, state, batch, ModelTime.Zero);
        Assert.Equal(2, replayed.NextMutationOrdinal);
        Assert.Single(replayed.ScheduledMutations);
    }

    [Fact]
    public void HandleBatch_ExistingScheduleExactAliasAndDifferentValueConflict_DoNotAllocate()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        var existing = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            new ModelTime(10),
            new SetPortalStateMutation(new PortalId("world-to-town"), false));
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(existing),
            ModelTime.Zero);

        SpatialCommandBatchResult exact = Handler(definition).HandleBatch(
            state,
            [new ScheduleSpatialMutationCommand(
                Id("exact"),
                new ModelTime(10),
                new SetPortalStateMutation(new PortalId("world-to-town"), false))],
            ModelTime.Zero);
        SpatialCommandBatchResult conflict = Handler(definition).HandleBatch(
            state,
            [new ScheduleSpatialMutationCommand(
                Id("conflict"),
                new ModelTime(10),
                new SetPortalStateMutation(new PortalId("world-to-town"), true))],
            ModelTime.Zero);

        Assert.Empty(exact.Events);
        Assert.Equal(SpatialCommandDisposition.AcceptedAlias, Assert.Single(exact.Results).Disposition);
        Assert.Equal(new ScheduledMutationId(1), Assert.Single(exact.Results).ScheduledMutationId);
        Assert.Empty(conflict.Events);
        Assert.Equal(SpatialCommandRejectionCode.ScheduledMutationConflict, Assert.Single(conflict.Results).RejectionCode);
        Assert.Equal(2, state.NextMutationOrdinal);
    }

    [Fact]
    public void HandleBatch_FutureValueEqualToCurrentStillSchedules()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            SpatialState.Create(definition),
            [new ScheduleSpatialMutationCommand(
                Id("schedule"),
                new ModelTime(10),
                new SetPortalStateMutation(new PortalId("world-to-town"), true))],
            ModelTime.Zero);

        Assert.IsType<MutationScheduledEvent>(Assert.Single(batch.Events).Payload);
        Assert.Equal(SpatialCommandDisposition.Accepted, Assert.Single(batch.Results).Disposition);
    }

    [Fact]
    public void HandleBatch_ImmediateSparseValues_AreCanonicalAndNoOpAware()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        CellRef cell = TestSpatialDefinitionBuilder.Cell("world", 0, 0);

        SpatialCommandBatchResult noChange = Handler(definition).HandleBatch(
            state,
            [
                new SetPortalStateCommand(Id("portal"), new PortalId("world-to-town"), true),
                new SetCellOverrideCommand(Id("cell"), cell, new CellOverride(blocksMovement: false)),
            ],
            ModelTime.Zero);
        SpatialCommandBatchResult changed = Handler(definition).HandleBatch(
            state,
            [
                new SetPortalStateCommand(Id("portal"), new PortalId("world-to-town"), false),
                new SetCellOverrideCommand(Id("cell"), cell, new CellOverride(blocksSight: true)),
            ],
            ModelTime.Zero);

        Assert.Empty(noChange.Events);
        Assert.All(noChange.Results, result =>
            Assert.Equal(SpatialCommandDisposition.AcceptedNoChange, result.Disposition));
        Assert.Collection(
            changed.Events.Select(value => value.Payload),
            payload => Assert.IsType<CellStateChangedEvent>(payload),
            payload => Assert.IsType<PortalStateChangedEvent>(payload));
        SpatialState replayed = Fold(definition, state, changed, ModelTime.Zero);
        Assert.False(Assert.Single(replayed.PortalOverrides).IsEnabled);
        Assert.True(Assert.Single(replayed.CellOverrides).Value.BlocksSight);
    }

    [Fact]
    public void HandleBatch_SemanticallyEqualSparseCommands_AliasAfterCanonicalization()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);
        CellRef cell = TestSpatialDefinitionBuilder.Cell("world", 0, 0);

        SpatialCommandBatchResult immediate = Handler(definition).HandleBatch(
            state,
            [
                new SetCellOverrideCommand(Id("b"), cell, null),
                new SetCellOverrideCommand(Id("a"), cell, new CellOverride(moveCost: 1)),
            ],
            ModelTime.Zero);
        SpatialCommandBatchResult scheduled = Handler(definition).HandleBatch(
            state,
            [
                new ScheduleSpatialMutationCommand(
                    Id("b"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(cell, null)),
                new ScheduleSpatialMutationCommand(
                    Id("a"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(cell, new CellOverride(moveCost: 1))),
            ],
            ModelTime.Zero);

        Assert.Empty(immediate.Events);
        Assert.Equal(SpatialCommandDisposition.AcceptedNoChange, Result(immediate, "a").Disposition);
        Assert.Equal(SpatialCommandDisposition.AcceptedAlias, Result(immediate, "b").Disposition);
        MutationScheduledEvent mutation = Assert.IsType<MutationScheduledEvent>(Assert.Single(scheduled.Events).Payload);
        Assert.Null(Assert.IsType<SetCellOverrideMutation>(mutation.Mutation.Mutation).Value);
        Assert.Equal(SpatialCommandDisposition.AcceptedAlias, Result(scheduled, "b").Disposition);
    }

    [Fact]
    public void HandleBatch_PlacementProducesOneFinalRelationshipDiff()
    {
        CellRef cell = Cell(0);
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            maps: [TestSpatialDefinitionBuilder.Map("line", 1, 1, visionRange: 1)],
            zones: [new ZoneDefinition(new ZoneId("home"), [cell])]);
        SpatialState state = SpatialState.Create(definition);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new PlaceEntityCommand(Id("place-2"), Entity(2), cell, true),
                new PlaceEntityCommand(Id("place-1"), Entity(1), cell, true),
            ],
            ModelTime.Zero);

        Assert.Collection(
            batch.Events.Select(value => value.Payload),
            payload => Assert.IsType<EntityPlacedEvent>(payload),
            payload => Assert.IsType<EntityPlacedEvent>(payload),
            payload => Assert.Equal(new ZoneEnteredEvent(Entity(1), new ZoneId("home")), payload),
            payload => Assert.Equal(new ZoneEnteredEvent(Entity(2), new ZoneId("home")), payload),
            payload => Assert.Equal(new CoPresenceStartedEvent(Entity(1), Entity(2)), payload),
            payload => Assert.IsType<GeometricVisibilityChangedEvent>(payload),
            payload => Assert.IsType<GeometricVisibilityChangedEvent>(payload));
        SpatialState replayed = Fold(definition, state, batch, ModelTime.Zero);
        SpatialStateValidator.ValidateComplete(definition, replayed);
    }

    [Fact]
    public void HandleBatch_AssignedAlreadySatisfied_ConsumesJourneyAndGenerationWithoutActiveJourney()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        SpatialState state = Place(definition, Entity(1), Cell(0));

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [new AssignMoveGoalCommand(Id("stay"), Entity(1), new CellGoal(Cell(0)))],
            ModelTime.Zero);

        JourneyCompletedEvent completed = Assert.IsType<JourneyCompletedEvent>(Assert.Single(batch.Events).Payload);
        Assert.Equal(JourneyCompletionReason.AssignedAlreadySatisfied, completed.Reason);
        SpatialState replayed = Fold(definition, state, batch, ModelTime.Zero);
        Assert.Equal(2, replayed.NextJourneyOrdinal);
        Assert.Equal(1, Assert.Single(replayed.Entities).MovementGeneration);
        Assert.Empty(replayed.Journeys);
    }

    [Fact]
    public void HandleBatch_RetargetAlreadySatisfied_RetainsJourneyIdWithoutAllocatorConsumption()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            active,
            [new RetargetMoveGoalCommand(Id("retarget"), Entity(1), new CellGoal(Cell(0)))],
            ModelTime.Zero);

        JourneyCompletedEvent completed = Assert.IsType<JourneyCompletedEvent>(Assert.Single(batch.Events).Payload);
        Assert.Equal(JourneyCompletionReason.RetargetedAlreadySatisfied, completed.Reason);
        Assert.Equal(new JourneyId(1), completed.JourneyId);
        SpatialState replayed = Fold(definition, active, batch, ModelTime.Zero);
        Assert.Equal(2, replayed.NextJourneyOrdinal);
        Assert.Equal(2, Assert.Single(replayed.Entities).MovementGeneration);
        Assert.Empty(replayed.Journeys);
    }

    [Fact]
    public void HandleBatch_RetargetUnreachable_PreservesActiveJourneyExactly()
    {
        SpatialDefinition definition = LineDefinition(
            width: 3,
            cells: [Floor(), Floor(), Floor(blocksMovement: true)]);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialCommandBatchResult assigned = Handler(definition).HandleBatch(
            placed,
            [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
            ModelTime.Zero);
        SpatialState active = Fold(definition, placed, assigned, ModelTime.Zero);
        JourneyState original = Assert.Single(active.Journeys);

        SpatialCommandBatchResult retarget = Handler(definition).HandleBatch(
            active,
            [new RetargetMoveGoalCommand(Id("retarget"), Entity(1), new CellGoal(Cell(2)))],
            ModelTime.Zero);

        Assert.Empty(retarget.Events);
        Assert.Equal(SpatialCommandRejectionCode.JourneyUnreachable, Assert.Single(retarget.Results).RejectionCode);
        Assert.Equal(original, Assert.Single(active.Journeys));
        Assert.Equal(1, Assert.Single(active.Entities).MovementGeneration);
    }

    [Fact]
    public void HandleBatch_CancelAndInterrupt_EmitExactGenerationEvents()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);

        SpatialCommandBatchResult cancelled = Handler(definition).HandleBatch(
            active,
            [new CancelMoveGoalCommand(Id("cancel"), Entity(1))],
            ModelTime.Zero);
        JourneyCancelledEvent cancelEvent = Assert.IsType<JourneyCancelledEvent>(Assert.Single(cancelled.Events).Payload);
        Assert.Equal((1L, 2L), (cancelEvent.ExpectedGeneration, cancelEvent.ResultingGeneration));

        SpatialState cancelledState = Fold(definition, active, cancelled, ModelTime.Zero);
        SpatialState reactivated = Fold(
            definition,
            cancelledState,
            Handler(definition).HandleBatch(
                cancelledState,
                [new AssignMoveGoalCommand(Id("assign-again"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);
        SpatialCommandBatchResult interrupted = Handler(definition).HandleBatch(
            reactivated,
            [new InterruptMovementCommand(Id("interrupt"), Entity(1), "combat.stun")],
            ModelTime.Zero);
        JourneyInterruptedEvent interruptEvent = Assert.IsType<JourneyInterruptedEvent>(
            Assert.Single(interrupted.Events).Payload);
        Assert.Equal("combat.stun", interruptEvent.Reason);
        Assert.Equal((3L, 4L), (interruptEvent.ExpectedGeneration, interruptEvent.ResultingGeneration));
    }

    [Fact]
    public void HandleBatch_RejectsDuplicateIdsNullCommandsAndMismatchedStampBeforePlanning()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        SpatialState state = SpatialState.Create(definition);
        var command = new PlaceEntityCommand(Id("same"), Entity(1), Cell(0), false);

        Assert.Throws<ArgumentException>(() => Handler(definition).HandleBatch(
            state,
            [command, command],
            ModelTime.Zero));
        Assert.Throws<ArgumentException>(() => Handler(definition).HandleBatch(
            state,
            [null!],
            ModelTime.Zero));

        SpatialDefinition other = SpatialDefinition.Create(
            new SpatialDefinitionId("other"),
            revision: 0,
            rulesVersion: 1,
            [TestSpatialDefinitionBuilder.Map("line", 1, 1, stepDuration: new ModelDuration(1))]);
        Assert.Throws<InvalidOperationException>(() => Handler(other).HandleBatch(
            state,
            [],
            ModelTime.Zero));
    }

    [Fact]
    public void HandleBatch_SameTargetDifferentValues_RejectsEveryCommandBeforePhaseExecution()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new SetPortalStateCommand(Id("a"), new PortalId("world-to-town"), false),
                new SetPortalStateCommand(Id("b"), new PortalId("world-to-town"), true),
            ],
            ModelTime.Zero);

        Assert.Empty(batch.Events);
        Assert.All(batch.Results, result =>
            Assert.Equal(SpatialCommandRejectionCode.ConflictingCommands, result.RejectionCode));
    }

    [Fact]
    public void HandleBatch_AllocatorTerminalValuesRejectAllOtherwiseSuccessfulConsumers()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        SpatialState state = Place(definition, Entity(1), Cell(0)).Rebuild(
            nextJourneyOrdinal: long.MaxValue,
            nextMutationOrdinal: long.MaxValue);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(0))),
                new ScheduleSpatialMutationCommand(
                    Id("schedule"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(Cell(0), new CellOverride(blocksSight: true))),
            ],
            ModelTime.Zero);

        Assert.Empty(batch.Events);
        Assert.Equal(
            SpatialCommandRejectionCode.JourneyAllocatorExhausted,
            Result(batch, "assign").RejectionCode);
        Assert.Equal(
            SpatialCommandRejectionCode.ScheduledMutationAllocatorExhausted,
            Result(batch, "schedule").RejectionCode);
    }

    [Fact]
    public void HandleBatch_InsufficientAllocatorCapacityRejectsAllPeersInsteadOfChoosingById()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState state = Place(definition, Entity(1), Cell(0));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntityPlacedEvent(new SpatialEntityState(Entity(2), Cell(1), false, 0)));
        state = state.Rebuild(
            nextJourneyOrdinal: long.MaxValue - 1,
            nextMutationOrdinal: long.MaxValue - 1);
        SpatialCommand[] commands =
        [
            new AssignMoveGoalCommand(Id("assign-a"), Entity(1), new CellGoal(Cell(0))),
            new AssignMoveGoalCommand(Id("assign-b"), Entity(2), new CellGoal(Cell(1))),
            new ScheduleSpatialMutationCommand(
                Id("schedule-a"),
                new ModelTime(10),
                new SetCellOverrideMutation(Cell(0), new CellOverride(blocksSight: true))),
            new ScheduleSpatialMutationCommand(
                Id("schedule-b"),
                new ModelTime(10),
                new SetCellOverrideMutation(Cell(1), new CellOverride(blocksSight: true))),
        ];

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(state, commands, ModelTime.Zero);

        Assert.Empty(batch.Events);
        Assert.Equal(
            [
                SpatialCommandRejectionCode.JourneyAllocatorExhausted,
                SpatialCommandRejectionCode.JourneyAllocatorExhausted,
                SpatialCommandRejectionCode.ScheduledMutationAllocatorExhausted,
                SpatialCommandRejectionCode.ScheduledMutationAllocatorExhausted,
            ],
            batch.Results.Select(result => result.RejectionCode));
    }

    [Fact]
    public void HandleBatch_ImmediateTopologyPhaseAffectsLaterAssignmentNavigation()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        CellRef start = TestSpatialDefinitionBuilder.Cell("world", 1, 0);
        CellRef destination = TestSpatialDefinitionBuilder.Cell("town", 0, 0);
        SpatialState state = Place(definition, Entity(1), start);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new AssignMoveGoalCommand(Id("a-assign"), Entity(1), new CellGoal(destination)),
                new SetPortalStateCommand(Id("z-close"), new PortalId("world-to-town"), false),
            ],
            ModelTime.Zero);

        Assert.IsType<PortalStateChangedEvent>(Assert.Single(batch.Events).Payload);
        Assert.Equal(SpatialCommandDisposition.Accepted, Result(batch, "z-close").Disposition);
        Assert.Equal(SpatialCommandRejectionCode.JourneyUnreachable, Result(batch, "a-assign").RejectionCode);
    }

    [Fact]
    public void HandleBatch_SameScheduledTargetDifferentValuesRejectsWholeConflictGroup()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialState.Create(definition);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new ScheduleSpatialMutationCommand(
                    Id("a"),
                    new ModelTime(10),
                    new SetPortalStateMutation(new PortalId("world-to-town"), false)),
                new ScheduleSpatialMutationCommand(
                    Id("b"),
                    new ModelTime(10),
                    new SetPortalStateMutation(new PortalId("world-to-town"), true)),
            ],
            ModelTime.Zero);

        Assert.Empty(batch.Events);
        Assert.All(batch.Results, result =>
            Assert.Equal(SpatialCommandRejectionCode.ConflictingCommands, result.RejectionCode));
    }

    [Fact]
    public void HandleBatch_OverdueJourneyChangeIsRejectedWithoutEvents()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            active,
            [new CancelMoveGoalCommand(Id("cancel"), Entity(1))],
            new ModelTime(2));

        Assert.Empty(batch.Events);
        Assert.Equal(SpatialCommandRejectionCode.JourneyLegOverdue, Assert.Single(batch.Results).RejectionCode);
    }

    [Fact]
    public void HandleBatch_UnknownReferencesUseStableSpecificCodes()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        SpatialState state = Place(definition, Entity(1), Cell(0));
        SpatialCommand[] commands =
        [
            new AssignMoveGoalCommand(
                Id("anchor"),
                Entity(1),
                new AnchorGoal(new AnchorId("missing"))),
            new SetCellOverrideCommand(
                Id("cell"),
                TestSpatialDefinitionBuilder.Cell("line", 5, 0),
                new CellOverride(blocksSight: true)),
            new SetPortalStateCommand(Id("portal"), new PortalId("missing"), false),
        ];

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(state, commands, ModelTime.Zero);

        Assert.Empty(batch.Events);
        Assert.Equal(SpatialCommandRejectionCode.UnknownAnchor, Result(batch, "anchor").RejectionCode);
        Assert.Equal(SpatialCommandRejectionCode.CellOutOfBounds, Result(batch, "cell").RejectionCode);
        Assert.Equal(SpatialCommandRejectionCode.UnknownPortal, Result(batch, "portal").RejectionCode);
    }

    [Fact]
    public void HandleBatch_JourneyLifecycleCommands_AllowExactDueAndRejectOnlyWhenOverdue()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);
        Assert.Equal(new ModelTime(1), Assert.Single(active.Journeys).CurrentLeg.Due);

        SpatialCommand[] commands =
        [
            new CancelMoveGoalCommand(Id("cancel"), Entity(1)),
            new InterruptMovementCommand(Id("interrupt"), Entity(1), "test.interrupt"),
            new RetargetMoveGoalCommand(Id("retarget"), Entity(1), new CellGoal(Cell(1))),
            new RemoveEntityCommand(Id("remove"), Entity(1)),
        ];

        foreach (SpatialCommand command in commands)
        {
            SpatialCommandBatchResult exact = Handler(definition).HandleBatch(
                active,
                [command],
                new ModelTime(1));
            Assert.Equal(SpatialCommandDisposition.Accepted, Assert.Single(exact.Results).Disposition);
            Assert.NotEmpty(exact.Events);
            SpatialState exactState = Fold(definition, active, exact, new ModelTime(1));
            SpatialStateValidator.ValidateComplete(definition, exactState);

            SpatialCommandBatchResult overdue = Handler(definition).HandleBatch(
                active,
                [command],
                new ModelTime(2));
            Assert.Empty(overdue.Events);
            Assert.Equal(
                SpatialCommandRejectionCode.JourneyLegOverdue,
                Assert.Single(overdue.Results).RejectionCode);
            Assert.Equal(active, Fold(definition, active, overdue, new ModelTime(2)));
        }
    }

    [Fact]
    public void HandleBatch_RetargetNextStep_UsesCommandTimeForAbsoluteDueAndRetainsAllocator()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [
                    new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1))),
                    new SetCellOverrideCommand(Id("cost-10"), Cell(1), new CellOverride(moveCost: 10)),
                ],
                ModelTime.Zero),
            ModelTime.Zero);
        Assert.Equal(new ModelTime(10), Assert.Single(active.Journeys).CurrentLeg.Due);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            active,
            [
                new RetargetMoveGoalCommand(Id("retarget"), Entity(1), new CellGoal(Cell(1))),
                new SetCellOverrideCommand(Id("cost-3"), Cell(1), new CellOverride(moveCost: 3)),
            ],
            new ModelTime(4));

        JourneyRetargetedEvent retargeted = Assert.IsType<JourneyRetargetedEvent>(
            batch.Events.Select(value => value.Payload).Single(value => value is JourneyRetargetedEvent));
        Assert.Equal(new JourneyId(1), retargeted.ResultingJourney.Id);
        Assert.Equal(new ModelTime(4), retargeted.ResultingJourney.CurrentLeg.StartedAt);
        Assert.Equal(new ModelTime(7), retargeted.ResultingJourney.CurrentLeg.Due);
        Assert.Equal(2, retargeted.ResultingJourney.Generation);
        Assert.Equal(new JourneyId(1), Result(batch, "retarget").JourneyId);

        SpatialState replayed = Fold(definition, active, batch, new ModelTime(4));
        Assert.Equal(2, replayed.NextJourneyOrdinal);
        Assert.Equal(retargeted.ResultingJourney, Assert.Single(replayed.Journeys));
        SpatialStateValidator.ValidateComplete(definition, replayed);
    }

    [Fact]
    public void HandleBatch_RetargetCostOverflow_PreservesJourneyEntityAllocatorAndRevision()
    {
        CellRef a = TestSpatialDefinitionBuilder.Cell("a", 0, 0);
        CellRef b = TestSpatialDefinitionBuilder.Cell("b", 0, 0);
        CellRef c = TestSpatialDefinitionBuilder.Cell("c", 0, 0);
        CellRef d = TestSpatialDefinitionBuilder.Cell("d", 0, 0);
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            maps:
            [
                TestSpatialDefinitionBuilder.Map("a", 1, 1),
                TestSpatialDefinitionBuilder.Map("b", 1, 1),
                TestSpatialDefinitionBuilder.Map("c", 1, 1),
                TestSpatialDefinitionBuilder.Map("d", 1, 1),
            ],
            portals:
            [
                new PortalDefinition(new PortalId("a-d"), a, d, new ModelDuration(1), true),
                new PortalDefinition(new PortalId("a-b"), a, b, new ModelDuration(long.MaxValue), true),
                new PortalDefinition(new PortalId("b-c"), b, c, new ModelDuration(1), true),
            ]);
        SpatialState placed = Place(definition, Entity(1), a);
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(d))],
                ModelTime.Zero),
            ModelTime.Zero);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            active,
            [new RetargetMoveGoalCommand(Id("retarget"), Entity(1), new CellGoal(c))],
            ModelTime.Zero);

        Assert.Empty(batch.Events);
        Assert.Equal(SpatialCommandRejectionCode.NavigationCostOverflow, Assert.Single(batch.Results).RejectionCode);
        Assert.Equal(active, Fold(definition, active, batch, ModelTime.Zero));
    }

    [Fact]
    public void HandleBatch_RetargetAbsoluteDueOverflow_PreservesJourneyEntityAllocatorAndRevision()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            [TestSpatialDefinitionBuilder.Map(
                "line",
                2,
                1,
                stepDuration: new ModelDuration(long.MaxValue))]);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);
        Assert.Equal(new ModelTime(long.MaxValue), Assert.Single(active.Journeys).CurrentLeg.Due);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            active,
            [new RetargetMoveGoalCommand(Id("retarget"), Entity(1), new CellGoal(Cell(1)))],
            new ModelTime(long.MaxValue));

        Assert.Empty(batch.Events);
        Assert.Equal(SpatialCommandRejectionCode.ModelTimeOverflow, Assert.Single(batch.Results).RejectionCode);
        Assert.Equal(active, Fold(definition, active, batch, new ModelTime(long.MaxValue)));
    }

    [Fact]
    public void HandleBatch_AllocatorMaxMinusOne_AllowsExactlyOneConsumerPerDomain()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        SpatialState state = Place(definition, Entity(1), Cell(0)).Rebuild(
            nextJourneyOrdinal: long.MaxValue - 1,
            nextMutationOrdinal: long.MaxValue - 1);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(0))),
                new ScheduleSpatialMutationCommand(
                    Id("schedule"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(Cell(0), new CellOverride(blocksSight: true))),
            ],
            ModelTime.Zero);

        Assert.Equal(new JourneyId(long.MaxValue - 1), Result(batch, "assign").JourneyId);
        Assert.Equal(new ScheduledMutationId(long.MaxValue - 1), Result(batch, "schedule").ScheduledMutationId);
        SpatialState replayed = Fold(definition, state, batch, ModelTime.Zero);
        Assert.Equal(long.MaxValue, replayed.NextJourneyOrdinal);
        Assert.Equal(long.MaxValue, replayed.NextMutationOrdinal);
        SpatialStateValidator.ValidateComplete(definition, replayed);
    }

    [Fact]
    public void HandleBatch_AllocatorCapacity_CountsOnlyOtherwiseSuccessfulConsumers()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState state = Place(definition, Entity(1), Cell(0)).Rebuild(
            nextJourneyOrdinal: long.MaxValue - 1,
            nextMutationOrdinal: long.MaxValue - 1);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new AssignMoveGoalCommand(Id("assign-valid"), Entity(1), new CellGoal(Cell(0))),
                new AssignMoveGoalCommand(Id("assign-invalid"), Entity(2), new CellGoal(Cell(1))),
                new ScheduleSpatialMutationCommand(
                    Id("schedule-valid"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(Cell(0), new CellOverride(blocksSight: true))),
                new ScheduleSpatialMutationCommand(
                    Id("schedule-invalid"),
                    ModelTime.Zero,
                    new SetCellOverrideMutation(Cell(1), new CellOverride(blocksSight: true))),
            ],
            ModelTime.Zero);

        Assert.Equal(SpatialCommandDisposition.Accepted, Result(batch, "assign-valid").Disposition);
        Assert.Equal(SpatialCommandRejectionCode.EntityNotFound, Result(batch, "assign-invalid").RejectionCode);
        Assert.Equal(SpatialCommandDisposition.Accepted, Result(batch, "schedule-valid").Disposition);
        Assert.Equal(
            SpatialCommandRejectionCode.ScheduledMutationDueNotFuture,
            Result(batch, "schedule-invalid").RejectionCode);
        SpatialState replayed = Fold(definition, state, batch, ModelTime.Zero);
        Assert.Equal(long.MaxValue, replayed.NextJourneyOrdinal);
        Assert.Equal(long.MaxValue, replayed.NextMutationOrdinal);
    }

    [Fact]
    public void HandleBatch_ExistingCanonicalSchedule_MapsEverySameBatchAliasToExistingIdentity()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        var existing = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            new ModelTime(10),
            new SetCellOverrideMutation(Cell(0), null));
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(existing),
            ModelTime.Zero);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new ScheduleSpatialMutationCommand(
                    Id("c"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(Cell(0), new CellOverride(blocksSight: false))),
                new ScheduleSpatialMutationCommand(
                    Id("a"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(Cell(0), new CellOverride(moveCost: 1))),
                new ScheduleSpatialMutationCommand(
                    Id("b"),
                    new ModelTime(10),
                    new SetCellOverrideMutation(Cell(0), null)),
            ],
            ModelTime.Zero);

        Assert.Empty(batch.Events);
        SpatialCommandResult canonical = Result(batch, "a");
        Assert.Equal(SpatialCommandDisposition.AcceptedAlias, canonical.Disposition);
        Assert.Null(canonical.AliasOfCommandId);
        Assert.Equal(new ScheduledMutationId(1), canonical.ScheduledMutationId);
        foreach (string id in new[] { "b", "c" })
        {
            SpatialCommandResult alias = Result(batch, id);
            Assert.Equal(SpatialCommandDisposition.AcceptedAlias, alias.Disposition);
            Assert.Equal(Id("a"), alias.AliasOfCommandId);
            Assert.Equal(new ScheduledMutationId(1), alias.ScheduledMutationId);
        }

        Assert.Equal(2, state.NextMutationOrdinal);
    }

    [Fact]
    public void HandleBatch_PlaceThenObservation_UsesPlacementPhaseRegardlessOfCommandId()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        SpatialState state = SpatialState.Create(definition);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new SetObservationEnabledCommand(Id("a-observe"), Entity(1), true),
                new PlaceEntityCommand(Id("z-place"), Entity(1), Cell(0), false),
            ],
            ModelTime.Zero);

        Assert.Collection(
            batch.Events.Select(value => value.Payload),
            payload => Assert.IsType<EntityPlacedEvent>(payload),
            payload => Assert.IsType<ObservationStateChangedEvent>(payload));
        Assert.All(batch.Results, result => Assert.Equal(SpatialCommandDisposition.Accepted, result.Disposition));
        SpatialEntityState entity = Assert.Single(Fold(definition, state, batch, ModelTime.Zero).Entities);
        Assert.True(entity.ObservationEnabled);
    }

    [Fact]
    public void HandleBatch_RemoveActiveJourney_UsesOneEventAndRemovesBothEntityAndJourney()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState active = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            active,
            [new RemoveEntityCommand(Id("remove"), Entity(1))],
            ModelTime.Zero);

        EntityRemovedEvent removed = Assert.IsType<EntityRemovedEvent>(Assert.Single(batch.Events).Payload);
        Assert.Equal(Entity(1), removed.EntityId);
        Assert.Equal(1, removed.ExpectedMovementGeneration);
        Assert.Equal(new JourneyId(1), removed.ExpectedActiveJourneyId);
        SpatialState replayed = Fold(definition, active, batch, ModelTime.Zero);
        Assert.Empty(replayed.Entities);
        Assert.Empty(replayed.Journeys);
        Assert.Equal(2, replayed.NextJourneyOrdinal);
        SpatialStateValidator.ValidateComplete(definition, replayed);
    }

    [Fact]
    public void HandleBatch_MovementGenerationTerminal_RejectsMaxAndAllowsMaxMinusOne()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState placed = Place(definition, Entity(1), Cell(0));
        SpatialState ordinaryActive = Fold(
            definition,
            placed,
            Handler(definition).HandleBatch(
                placed,
                [new AssignMoveGoalCommand(Id("assign"), Entity(1), new CellGoal(Cell(1)))],
                ModelTime.Zero),
            ModelTime.Zero);
        SpatialState terminal = WithJourneyGeneration(ordinaryActive, long.MaxValue);

        SpatialCommand[] terminalCommands =
        [
            new CancelMoveGoalCommand(Id("cancel"), Entity(1)),
            new InterruptMovementCommand(Id("interrupt"), Entity(1), "test.interrupt"),
            new RetargetMoveGoalCommand(Id("retarget"), Entity(1), new CellGoal(Cell(1))),
        ];
        foreach (SpatialCommand command in terminalCommands)
        {
            SpatialCommandBatchResult rejected = Handler(definition).HandleBatch(
                terminal,
                [command],
                ModelTime.Zero);
            Assert.Empty(rejected.Events);
            Assert.Equal(
                SpatialCommandRejectionCode.MovementGenerationOverflow,
                Assert.Single(rejected.Results).RejectionCode);
        }

        SpatialState lastGeneration = WithJourneyGeneration(ordinaryActive, long.MaxValue - 1);
        SpatialCommandBatchResult accepted = Handler(definition).HandleBatch(
            lastGeneration,
            [new CancelMoveGoalCommand(Id("cancel-last"), Entity(1))],
            ModelTime.Zero);
        JourneyCancelledEvent cancelled = Assert.IsType<JourneyCancelledEvent>(Assert.Single(accepted.Events).Payload);
        Assert.Equal(long.MaxValue, cancelled.ResultingGeneration);
        SpatialState replayed = Fold(definition, lastGeneration, accepted, ModelTime.Zero);
        Assert.Equal(long.MaxValue, Assert.Single(replayed.Entities).MovementGeneration);
        Assert.Empty(replayed.Journeys);
    }

    [Fact]
    public void HandleBatch_RevisionTerminal_IsCheckedAndDoesNotMutatePreState()
    {
        SpatialDefinition definition = LineDefinition(width: 1);
        SpatialState terminal = SpatialState.Create(definition).Rebuild(revision: long.MaxValue);

        Assert.Throws<OverflowException>(() => Handler(definition).HandleBatch(
            terminal,
            [new PlaceEntityCommand(Id("place"), Entity(1), Cell(0), false)],
            ModelTime.Zero));
        Assert.Equal(long.MaxValue, terminal.Revision);
        Assert.Empty(terminal.Entities);

        SpatialState lastRevision = SpatialState.Create(definition).Rebuild(revision: long.MaxValue - 1);
        SpatialCommandBatchResult accepted = Handler(definition).HandleBatch(
            lastRevision,
            [new PlaceEntityCommand(Id("place"), Entity(1), Cell(0), false)],
            ModelTime.Zero);
        SpatialState replayed = Fold(definition, lastRevision, accepted, ModelTime.Zero);
        Assert.Equal(long.MaxValue, replayed.Revision);
        Assert.Single(replayed.Entities);
    }

    [Fact]
    public void HandleBatch_ScheduleDueAtOrBeforeNow_RejectsWithoutAllocation()
    {
        SpatialDefinition definition = LineDefinition(width: 2);
        SpatialState state = SpatialState.Create(definition);

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new ScheduleSpatialMutationCommand(
                    Id("due-now"),
                    new ModelTime(5),
                    new SetCellOverrideMutation(Cell(0), new CellOverride(blocksSight: true))),
                new ScheduleSpatialMutationCommand(
                    Id("due-past"),
                    new ModelTime(4),
                    new SetCellOverrideMutation(Cell(1), new CellOverride(blocksSight: true))),
            ],
            new ModelTime(5));

        Assert.Empty(batch.Events);
        Assert.All(batch.Results, result =>
        {
            Assert.Equal(SpatialCommandDisposition.Rejected, result.Disposition);
            Assert.Equal(SpatialCommandRejectionCode.ScheduledMutationDueNotFuture, result.RejectionCode);
        });
        Assert.Equal(1, state.NextMutationOrdinal);
    }

    [Fact]
    public void HandleBatch_SightChangeAndObserverDisable_ProduceOneNetVisibilityDiff()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.Create(
            [TestSpatialDefinitionBuilder.Map("line", 3, 1, visionRange: 3)]);
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new EntityPlacedEvent(new SpatialEntityState(Entity(1), Cell(0), true, 0)));
        state = SpatialEventTestHarness.Apply(
            definition,
            state,
            new EntityPlacedEvent(new SpatialEntityState(Entity(2), Cell(2), false, 0)));
        Assert.Equal([Entity(2)], new SpatialQueries(definition).GetVisibleEntities(state, Entity(1)));

        SpatialCommandBatchResult batch = Handler(definition).HandleBatch(
            state,
            [
                new SetObservationEnabledCommand(Id("disable"), Entity(1), false),
                new SetCellOverrideCommand(Id("wall"), Cell(1), new CellOverride(blocksSight: true)),
            ],
            ModelTime.Zero);

        GeometricVisibilityChangedEvent visibility = Assert.IsType<GeometricVisibilityChangedEvent>(
            Assert.Single(
                batch.Events.Select(value => value.Payload),
                value => value is GeometricVisibilityChangedEvent));
        Assert.Equal(Entity(1), visibility.ObserverId);
        Assert.Empty(visibility.AddedEntityIds);
        Assert.Equal([Entity(2)], visibility.RemovedEntityIds);
        Assert.Equal(3, batch.Events.Count);
        SpatialState replayed = Fold(definition, state, batch, ModelTime.Zero);
        Assert.False(replayed.Entities.Single(value => value.Id == Entity(1)).ObservationEnabled);
        SpatialStateValidator.ValidateComplete(definition, replayed);
    }

    private static SpatialCommandHandler Handler(SpatialDefinition definition) => new(definition);

    private static SpatialCommandId Id(string value) => new(value);

    private static EntityId Entity(long value) => new(value);

    private static CellRef Cell(int x) => TestSpatialDefinitionBuilder.Cell("line", x, 0);

    private static CellDefinition Floor(bool blocksMovement = false) =>
        TestSpatialDefinitionBuilder.Floor(blocksMovement: blocksMovement);

    private static SpatialDefinition LineDefinition(
        int width,
        IReadOnlyList<CellDefinition>? cells = null) =>
        TestSpatialDefinitionBuilder.Create(
            [TestSpatialDefinitionBuilder.Map(
                "line",
                width,
                1,
                stepDuration: new ModelDuration(1),
                visionRange: width,
                cells: cells)]);

    private static SpatialState Place(
        SpatialDefinition definition,
        EntityId entityId,
        CellRef cell) =>
        SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new EntityPlacedEvent(new SpatialEntityState(entityId, cell, false, 0)));

    private static SpatialState WithJourneyGeneration(SpatialState state, long generation)
    {
        SpatialEntityState entity = Assert.Single(state.Entities);
        JourneyState journey = Assert.Single(state.Journeys);
        CurrentLeg leg = journey.CurrentLeg;
        var replacementLeg = new CurrentLeg(
            leg.From,
            leg.To,
            leg.EdgeKind,
            leg.PortalId,
            leg.StartedAt,
            leg.Due,
            generation);
        return state.Rebuild(
            entities:
            [
                new SpatialEntityState(
                    entity.Id,
                    entity.Cell,
                    entity.ObservationEnabled,
                    generation),
            ],
            journeys:
            [
                new JourneyState(
                    journey.Id,
                    journey.EntityId,
                    journey.Goal,
                    generation,
                    replacementLeg),
            ]);
    }

    private static SpatialState Fold(
        SpatialDefinition definition,
        SpatialState state,
        SpatialCommandBatchResult batch,
        ModelTime time)
    {
        foreach (var spatialEvent in batch.Events)
        {
            state = SpatialEventTestHarness.Apply(
                definition,
                state,
                spatialEvent.Payload,
                time,
                spatialEvent.Kind);
        }

        return state;
    }

    private static SpatialCommandResult Result(SpatialCommandBatchResult batch, string commandId) =>
        batch.Results.Single(result => result.CommandId == Id(commandId));
}

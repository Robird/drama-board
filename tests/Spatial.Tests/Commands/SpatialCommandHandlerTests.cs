using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialCommandHandlerTests
{
    [Fact]
    public void Handle_PlansExactlyOneCommandAsRawFacts()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialCommandPlan plan = Handler(definition).Handle(
            SpatialState.Create(definition),
            new PlaceEntityCommand(
                Id("place"),
                new EntityId(1),
                TestSpatialDefinitionBuilder.Cell("world", 0, 0),
                observationEnabled: true),
            ModelTime.Zero);

        Assert.Equal(SpatialCommandDisposition.Accepted, plan.Result.Disposition);
        Assert.IsType<EntityPlacedEvent>(plan.Facts[0]);
        Assert.All(plan.Facts, fact => Assert.IsAssignableFrom<SpatialEvent>(fact));
        Assert.DoesNotContain(plan.Facts, fact => fact is null);
    }

    [Fact]
    public void Handle_AssignmentStartsOneJourney_WithoutAnotherWinnerLaw()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            entityId: 1,
            TestSpatialDefinitionBuilder.Cell("world", 0, 0));

        SpatialCommandPlan plan = Handler(definition).Handle(
            state,
            new AssignMoveGoalCommand(
                Id("move"),
                new EntityId(1),
                new CellGoal(TestSpatialDefinitionBuilder.Cell("world", 1, 0))),
            ModelTime.Zero);

        JourneyStartedEvent started = Assert.Single(plan.Facts.OfType<JourneyStartedEvent>());
        Assert.Equal(new JourneyId(1), started.Journey.Id);
        Assert.Equal(SpatialCommandDisposition.Accepted, plan.Result.Disposition);
        Assert.Equal(new JourneyId(1), plan.Result.JourneyId);
    }

    [Fact]
    public void Handle_ScheduleCanonicalizesAndAllocatesOneMutation()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        ModelTime due = SpatialEventTestHarness.AtSecond(2);
        SpatialCommandPlan plan = Handler(definition).Handle(
            SpatialState.Create(definition),
            new ScheduleSpatialMutationCommand(
                Id("schedule"),
                due,
                new SetCellOverrideMutation(
                    TestSpatialDefinitionBuilder.Cell("world", 0, 0),
                    new CellOverride(blocksSight: true))),
            ModelTime.Zero);

        MutationScheduledEvent scheduled = Assert.Single(plan.Facts.OfType<MutationScheduledEvent>());
        Assert.Equal(new ScheduledMutationId(1), scheduled.Mutation.Id);
        Assert.Equal(due, scheduled.Mutation.Due);
        Assert.Equal(new ScheduledMutationId(1), plan.Result.ScheduledMutationId);
    }

    [Fact]
    public void Handle_ExistingScheduleIsNoNewFactAndConflictingValueIsRejected()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        CellRef cell = TestSpatialDefinitionBuilder.Cell("world", 0, 0);
        ModelTime due = SpatialEventTestHarness.AtSecond(2);
        var scheduled = new ScheduledSpatialMutationState(
            new ScheduledMutationId(1),
            due,
            new SetCellOverrideMutation(cell, new CellOverride(blocksSight: true)));
        SpatialState state = SpatialEventTestHarness.Apply(
            definition,
            SpatialState.Create(definition),
            new MutationScheduledEvent(scheduled));

        SpatialCommandPlan same = Handler(definition).Handle(
            state,
            new ScheduleSpatialMutationCommand(Id("same"), due, scheduled.Mutation),
            ModelTime.Zero);
        SpatialCommandPlan conflict = Handler(definition).Handle(
            state,
            new ScheduleSpatialMutationCommand(
                Id("conflict"),
                due,
                new SetCellOverrideMutation(cell, new CellOverride(blocksSight: false))),
            ModelTime.Zero);

        Assert.Empty(same.Facts);
        Assert.Equal(SpatialCommandDisposition.AcceptedNoChange, same.Result.Disposition);
        Assert.Equal(new ScheduledMutationId(1), same.Result.ScheduledMutationId);
        Assert.Empty(conflict.Facts);
        Assert.Equal(SpatialCommandRejectionCode.ScheduledMutationConflict, conflict.Result.RejectionCode);
    }

    [Fact]
    public void Handle_ImmediateTopologyAndObservationUseOnePrimaryFactEach()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = SpatialEventTestHarness.Place(
            definition,
            SpatialState.Create(definition),
            entityId: 1,
            TestSpatialDefinitionBuilder.Cell("world", 0, 0),
            observationEnabled: true);

        SpatialCommandPlan portal = Handler(definition).Handle(
            state,
            new SetPortalStateCommand(Id("portal"), new PortalId("world-to-town"), isEnabled: false),
            ModelTime.Zero);
        SpatialCommandPlan observation = Handler(definition).Handle(
            state,
            new SetObservationEnabledCommand(Id("observe"), new EntityId(1), observationEnabled: false),
            ModelTime.Zero);

        Assert.Single(portal.Facts.OfType<PortalStateChangedEvent>());
        Assert.Single(observation.Facts.OfType<ObservationStateChangedEvent>());
        Assert.Equal(SpatialCommandDisposition.Accepted, portal.Result.Disposition);
        Assert.Equal(SpatialCommandDisposition.Accepted, observation.Result.Disposition);
    }

    [Fact]
    public void Handle_CancelConsumesCurrentJourneyGeneration()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialState state = StartJourney(definition);

        SpatialCommandPlan plan = Handler(definition).Handle(
            state,
            new CancelMoveGoalCommand(Id("cancel"), new EntityId(1)),
            ModelTime.Zero);

        JourneyCancelledEvent cancelled = Assert.Single(plan.Facts.OfType<JourneyCancelledEvent>());
        Assert.Equal((1L, 2L), (cancelled.ExpectedGeneration, cancelled.ResultingGeneration));
        Assert.Equal(new JourneyId(1), plan.Result.JourneyId);
    }

    [Fact]
    public void Handle_RejectionsProduceNoFacts()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialCommandPlan missing = Handler(definition).Handle(
            SpatialState.Create(definition),
            new RemoveEntityCommand(Id("remove"), new EntityId(99)),
            ModelTime.Zero);
        SpatialCommandPlan overdueSchedule = Handler(definition).Handle(
            SpatialState.Create(definition),
            new ScheduleSpatialMutationCommand(
                Id("schedule"),
                ModelTime.Zero,
                new SetPortalStateMutation(new PortalId("world-to-town"), false)),
            ModelTime.Zero);

        Assert.Empty(missing.Facts);
        Assert.Equal(SpatialCommandRejectionCode.EntityNotFound, missing.Result.RejectionCode);
        Assert.Empty(overdueSchedule.Facts);
        Assert.Equal(
            SpatialCommandRejectionCode.ScheduledMutationDueNotFuture,
            overdueSchedule.Result.RejectionCode);
    }

    [Fact]
    public void Handle_ValidatesDefinitionStampAndNulls()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        SpatialDefinition other = TestSpatialDefinitionBuilder.Create(
            maps: [TestSpatialDefinitionBuilder.Map("other", width: 1, height: 1)]);
        var handler = Handler(definition);
        var command = new RemoveEntityCommand(Id("remove"), new EntityId(1));

        Assert.Throws<ArgumentNullException>(() => handler.Handle(null!, command, ModelTime.Zero));
        Assert.Throws<ArgumentNullException>(() => handler.Handle(
            SpatialState.Create(definition), null!, ModelTime.Zero));
        Assert.Throws<InvalidOperationException>(() => handler.Handle(
            SpatialState.Create(other), command, ModelTime.Zero));
    }

    private static SpatialState StartJourney(SpatialDefinition definition)
    {
        CellRef from = TestSpatialDefinitionBuilder.Cell("world", 0, 0);
        CellRef to = TestSpatialDefinitionBuilder.Cell("world", 1, 0);
        SpatialState state = SpatialEventTestHarness.Place(
            definition, SpatialState.Create(definition), entityId: 1, from);
        var leg = new CurrentLeg(
            from,
            to,
            SpatialEdgeKind.Orthogonal,
            null,
            ModelTime.Zero,
            SpatialEventTestHarness.AtSecond(1),
            journeyGeneration: 1);
        return SpatialEventTestHarness.Apply(definition, state, new JourneyStartedEvent(
            new JourneyState(new JourneyId(1), new EntityId(1), new CellGoal(to), 1, leg)));
    }

    private static SpatialCommandHandler Handler(SpatialDefinition definition) => new(definition);

    private static SpatialCommandId Id(string value) => new(value);
}

using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialCommandModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("command\nname")]
    public void CommandId_InvalidStableIdentifier_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => new SpatialCommandId(value));
    }

    [Fact]
    public void CommandId_EnforcesLengthAndWellFormedUtf16()
    {
        Assert.Throws<ArgumentException>(() => new SpatialCommandId(new string('c', 257)));
        Assert.Throws<ArgumentException>(() => new SpatialCommandId(new(['\ud800'])));
        Assert.Throws<ArgumentException>(() => new SpatialCommandId(new(['\udc00'])));
        Assert.Equal("move-\ud83e\udded", new SpatialCommandId("move-\ud83e\udded").Value);
    }

    [Fact]
    public void CommandId_NaturalOrder_IsOrdinal()
    {
        Assert.Equal(
            ["A", "a", "b"],
            new[]
            {
                new SpatialCommandId("b"),
                new SpatialCommandId("a"),
                new SpatialCommandId("A"),
            }.Order().Select(value => value.Value));
    }

    [Fact]
    public void Commands_RetainTheirImmutableIntentValues()
    {
        EntityId entityId = new(41);
        CellRef cell = Cell("missing-map", 200, 300);
        MoveGoal goal = new ZoneGoal(new ZoneId("missing-zone"));
        CellOverride cellOverride = new(blocksMovement: true, moveCost: 7);
        ScheduledSpatialMutation mutation = new SetPortalStateMutation(
            new PortalId("missing-portal"),
            false);

        var place = new PlaceEntityCommand(Id("01"), entityId, cell, observationEnabled: false);
        var remove = new RemoveEntityCommand(Id("02"), entityId);
        var observation = new SetObservationEnabledCommand(Id("03"), entityId, true);
        var assign = new AssignMoveGoalCommand(Id("04"), entityId, goal);
        var retarget = new RetargetMoveGoalCommand(Id("05"), entityId, goal);
        var cancel = new CancelMoveGoalCommand(Id("06"), entityId);
        var portal = new SetPortalStateCommand(Id("07"), new PortalId("missing-portal"), false);
        var cellState = new SetCellOverrideCommand(Id("08"), cell, cellOverride);
        var schedule = new ScheduleSpatialMutationCommand(Id("09"), new ModelTime(-100), mutation);
        var interrupt = new InterruptMovementCommand(Id("10"), entityId, "story.cutscene");

        Assert.Equal((entityId, cell, false), (place.EntityId, place.Cell, place.ObservationEnabled));
        Assert.Equal(entityId, remove.EntityId);
        Assert.Equal((entityId, true), (observation.EntityId, observation.ObservationEnabled));
        Assert.Equal((entityId, goal), (assign.EntityId, assign.Goal));
        Assert.Equal((entityId, goal), (retarget.EntityId, retarget.Goal));
        Assert.Equal(entityId, cancel.EntityId);
        Assert.Equal((new PortalId("missing-portal"), false), (portal.PortalId, portal.IsEnabled));
        Assert.Equal((cell, cellOverride), (cellState.Cell, cellState.Value));
        Assert.Equal((new ModelTime(-100), mutation), (schedule.Due, schedule.Mutation));
        Assert.Equal((entityId, "story.cutscene"), (interrupt.EntityId, interrupt.Reason));
        Assert.Equal(
            Enumerable.Range(1, 10).Select(value => value.ToString("00")),
            new SpatialCommand[]
            {
                place,
                remove,
                observation,
                assign,
                retarget,
                cancel,
                portal,
                cellState,
                schedule,
                interrupt,
            }.Select(command => command.CommandId.Value));
    }

    [Fact]
    public void Commands_DoNotPerformDefinitionStateOrNowValidation()
    {
        EntityId absentEntity = new(long.MaxValue);
        CellRef unknownCell = Cell("not-in-definition", int.MaxValue, int.MaxValue);

        Assert.IsType<PlaceEntityCommand>(
            new PlaceEntityCommand(Id("place"), absentEntity, unknownCell, true));
        Assert.IsType<AssignMoveGoalCommand>(
            new AssignMoveGoalCommand(Id("assign"), absentEntity, new AnchorGoal(new AnchorId("unknown"))));
        Assert.IsType<SetPortalStateCommand>(
            new SetPortalStateCommand(Id("portal"), new PortalId("unknown"), true));
        Assert.IsType<ScheduleSpatialMutationCommand>(new ScheduleSpatialMutationCommand(
            Id("schedule"),
            new ModelTime(long.MinValue),
            new SetCellOverrideMutation(unknownCell, null)));
    }

    [Fact]
    public void Commands_RejectUninitializedStructuralIdentifiers()
    {
        CellRef cell = Cell("world", 0, 0);
        MoveGoal goal = new CellGoal(cell);

        Assert.Throws<ArgumentException>(() => new RemoveEntityCommand(default, new EntityId(1)));
        Assert.Throws<ArgumentException>(() => new PlaceEntityCommand(Id("place"), default, cell, true));
        Assert.Throws<ArgumentException>(() => new RemoveEntityCommand(Id("remove"), default));
        Assert.Throws<ArgumentException>(() => new SetObservationEnabledCommand(Id("observe"), default, true));
        Assert.Throws<ArgumentException>(() => new AssignMoveGoalCommand(Id("assign"), default, goal));
        Assert.Throws<ArgumentException>(() => new RetargetMoveGoalCommand(Id("retarget"), default, goal));
        Assert.Throws<ArgumentException>(() => new CancelMoveGoalCommand(Id("cancel"), default));
        Assert.Throws<ArgumentException>(() => new InterruptMovementCommand(Id("interrupt"), default, "reason"));
        Assert.Throws<ArgumentException>(() => new SetPortalStateCommand(Id("portal"), default, true));
        Assert.Throws<ArgumentException>(() => new SetCellOverrideCommand(Id("cell"), default, null));
    }

    [Fact]
    public void GoalCommands_RejectNullDefaultOrUnsupportedGoalShape()
    {
        EntityId entityId = new(1);

        Assert.Throws<ArgumentNullException>(() =>
            new AssignMoveGoalCommand(Id("null"), entityId, null!));
        Assert.Throws<ArgumentException>(() =>
            new AssignMoveGoalCommand(Id("cell"), entityId, new CellGoal(default)));
        Assert.Throws<ArgumentException>(() =>
            new RetargetMoveGoalCommand(Id("custom"), entityId, new UnsupportedGoal()));
    }

    [Fact]
    public void OverrideCommands_RequireNullInsteadOfAnEmptyOverride()
    {
        CellRef cell = Cell("world", 0, 0);

        Assert.Null(new SetCellOverrideCommand(Id("clear"), cell, null).Value);
        Assert.Throws<ArgumentException>(() =>
            new SetCellOverrideCommand(Id("empty"), cell, new CellOverride()));
        Assert.Throws<ArgumentException>(() => new ScheduleSpatialMutationCommand(
            Id("scheduled-empty"),
            ModelTime.Zero,
            new SetCellOverrideMutation(cell, new CellOverride())));
    }

    [Fact]
    public void ScheduledCommand_RejectsNullDefaultOrUnsupportedMutationShape()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ScheduleSpatialMutationCommand(Id("null"), ModelTime.Zero, null!));
        Assert.Throws<ArgumentException>(() => new ScheduleSpatialMutationCommand(
            Id("cell"),
            ModelTime.Zero,
            new SetCellOverrideMutation(default, null)));
        Assert.Throws<ArgumentException>(() => new ScheduleSpatialMutationCommand(
            Id("custom"),
            ModelTime.Zero,
            new UnsupportedMutation()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("combat\nstun")]
    public void InterruptCommand_InvalidStableReason_Throws(string reason)
    {
        Assert.Throws<ArgumentException>(() =>
            new InterruptMovementCommand(Id("interrupt"), new EntityId(1), reason));
    }

    [Fact]
    public void Result_AcceptsEveryValidDispositionShape()
    {
        var accepted = Result(
            "a",
            SpatialCommandDisposition.Accepted,
            journeyId: new JourneyId(3));
        var noChange = Result("b", SpatialCommandDisposition.AcceptedNoChange);
        var sameBatchAlias = Result(
            "c",
            SpatialCommandDisposition.AcceptedAlias,
            aliasOfCommandId: Id("a"),
            scheduledMutationId: new ScheduledMutationId(5));
        var existingScheduleAlias = Result(
            "d",
            SpatialCommandDisposition.AcceptedAlias,
            scheduledMutationId: new ScheduledMutationId(7));
        var rejected = Result(
            "e",
            SpatialCommandDisposition.Rejected,
            SpatialCommandRejectionCode.JourneyUnreachable);

        Assert.Equal(new JourneyId(3), accepted.JourneyId);
        Assert.Equal(SpatialCommandRejectionCode.None, noChange.RejectionCode);
        Assert.Equal(Id("a"), sameBatchAlias.AliasOfCommandId);
        Assert.Equal(new ScheduledMutationId(5), sameBatchAlias.ScheduledMutationId);
        Assert.Null(existingScheduleAlias.AliasOfCommandId);
        Assert.Equal(SpatialCommandRejectionCode.JourneyUnreachable, rejected.RejectionCode);
    }

    [Fact]
    public void Result_RejectsDispositionCodeAndMetadataContradictions()
    {
        Assert.Throws<ArgumentException>(() =>
            Result("a", SpatialCommandDisposition.Rejected));
        Assert.Throws<ArgumentException>(() => Result(
            "a",
            SpatialCommandDisposition.Rejected,
            SpatialCommandRejectionCode.EntityNotFound,
            journeyId: new JourneyId(1)));
        Assert.Throws<ArgumentException>(() => Result(
            "a",
            SpatialCommandDisposition.Accepted,
            SpatialCommandRejectionCode.EntityNotFound));
        Assert.Throws<ArgumentException>(() => Result(
            "a",
            SpatialCommandDisposition.Accepted,
            aliasOfCommandId: Id("b")));
        Assert.Throws<ArgumentException>(() => Result(
            "a",
            SpatialCommandDisposition.AcceptedNoChange,
            journeyId: new JourneyId(1)));
        Assert.Throws<ArgumentException>(() =>
            Result("a", SpatialCommandDisposition.AcceptedAlias));
        Assert.Throws<ArgumentException>(() => Result(
            "a",
            SpatialCommandDisposition.AcceptedAlias,
            aliasOfCommandId: Id("a")));
        Assert.Throws<ArgumentException>(() => Result(
            "a",
            SpatialCommandDisposition.AcceptedAlias,
            aliasOfCommandId: Id("b")));
        Assert.Throws<ArgumentException>(() => Result(
            "b",
            SpatialCommandDisposition.AcceptedAlias,
            aliasOfCommandId: Id("a"),
            journeyId: new JourneyId(1)));
        Assert.Throws<ArgumentException>(() => Result(
            "a",
            SpatialCommandDisposition.Accepted,
            journeyId: new JourneyId(1),
            scheduledMutationId: new ScheduledMutationId(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Result(
            "a",
            (SpatialCommandDisposition)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => Result(
            "a",
            SpatialCommandDisposition.Rejected,
            (SpatialCommandRejectionCode)999));
    }

    [Fact]
    public void BatchResult_DefensivelySnapshotsEventsAndResults()
    {
        var firstEvent = new UncommittedDomainEvent<SpatialEvent>(
            SpatialEventKinds.ZoneEntered,
            new ZoneEnteredEvent(new EntityId(1), new ZoneId("first")));
        var replacementEvent = new UncommittedDomainEvent<SpatialEvent>(
            SpatialEventKinds.ZoneLeft,
            new ZoneLeftEvent(new EntityId(1), new ZoneId("replacement")));
        SpatialCommandResult firstResult = Result("a", SpatialCommandDisposition.Accepted);
        SpatialCommandResult secondResult = Result("b", SpatialCommandDisposition.AcceptedNoChange);
        var events = new[] { firstEvent };
        var results = new[] { firstResult, secondResult };

        var batch = new SpatialCommandBatchResult(events, results);
        events[0] = replacementEvent;
        results[0] = Result("z", SpatialCommandDisposition.Accepted);

        Assert.Same(firstEvent, Assert.Single(batch.Events));
        Assert.Equal([firstResult, secondResult], batch.Results);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<UncommittedDomainEvent<SpatialEvent>>)batch.Events)[0] = replacementEvent);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SpatialCommandResult>)batch.Results)[0] = secondResult);
    }

    [Fact]
    public void BatchResult_RequiresUniqueStrictOrdinalResultOrder()
    {
        SpatialCommandResult upper = Result("A", SpatialCommandDisposition.AcceptedNoChange);
        SpatialCommandResult lower = Result("a", SpatialCommandDisposition.AcceptedNoChange);

        var valid = new SpatialCommandBatchResult([], [upper, lower]);

        Assert.Equal(["A", "a"], valid.Results.Select(result => result.CommandId.Value));
        Assert.Throws<ArgumentException>(() => new SpatialCommandBatchResult([], [lower, upper]));
        Assert.Throws<ArgumentException>(() => new SpatialCommandBatchResult([], [upper, upper]));
        Assert.Throws<ArgumentException>(() => new SpatialCommandBatchResult(
            [],
            [Result("b", SpatialCommandDisposition.AcceptedAlias, aliasOfCommandId: Id("a"))]));
    }

    [Fact]
    public void BatchResult_RejectsNullCollectionsAndElements()
    {
        Assert.Throws<ArgumentNullException>(() => new SpatialCommandBatchResult(null!, []));
        Assert.Throws<ArgumentNullException>(() => new SpatialCommandBatchResult([], null!));
        Assert.Throws<ArgumentException>(() => new SpatialCommandBatchResult(
            [null!],
            []));
        Assert.Throws<ArgumentException>(() => new SpatialCommandBatchResult(
            [],
            [null!]));
    }

    private static SpatialCommandId Id(string value) => new(value);

    private static CellRef Cell(string mapId, int x, int y) => new(new MapId(mapId), x, y);

    private static SpatialCommandResult Result(
        string commandId,
        SpatialCommandDisposition disposition,
        SpatialCommandRejectionCode rejectionCode = SpatialCommandRejectionCode.None,
        SpatialCommandId? aliasOfCommandId = null,
        JourneyId? journeyId = null,
        ScheduledMutationId? scheduledMutationId = null) =>
        new(
            Id(commandId),
            disposition,
            rejectionCode,
            aliasOfCommandId,
            journeyId,
            scheduledMutationId);

    private sealed record UnsupportedGoal : MoveGoal;

    private sealed record UnsupportedMutation : ScheduledSpatialMutation;
}

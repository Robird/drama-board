using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay.Runtime;

namespace DramaBoard.Server.Tests;

public sealed class PlayerProcessDomainTests {
    [Fact]
    public void EmptyBufferHasNoHeadAndNoItems() {
        Assert.True(InstructionBuffer.Empty.IsEmpty);
        Assert.Null(InstructionBuffer.Empty.Head);
        Assert.Empty(InstructionBuffer.Empty.Items);
    }

    [Fact]
    public void BufferOfTakesFrozenSnapshotOfSourceList() {
        var source = new List<Instruction> { new StayInstruction() };
        InstructionBuffer buffer = InstructionBuffer.Of(source);
        source.Add(new MoveInstruction(new PassageHandle("ab")));
        source.RemoveAt(0);
        Assert.Single(buffer.Items);
        Assert.IsType<StayInstruction>(buffer.Items[0]);
    }

    [Fact]
    public void BufferOfRejectsNullSourceAndNullEntries() {
        Assert.Throws<ArgumentNullException>(() => InstructionBuffer.Of(null!));
        Assert.Throws<ArgumentException>(() => InstructionBuffer.Of([null!]));
    }

    [Fact]
    public void BufferHeadTailAndValueEqualityFollowElementSequence() {
        var move = new MoveInstruction(new PassageHandle("ab"));
        var think = new ThinkInstruction("plan");
        InstructionBuffer buffer = InstructionBuffer.Of([move, think]);
        Assert.False(buffer.IsEmpty);
        Assert.Same(move, buffer.Head);
        Assert.Equal(2, buffer.Items.Count);

        InstructionBuffer tail = buffer.Tail();
        Assert.Same(think, tail.Head);
        Assert.True(tail == InstructionBuffer.Of([think]));
        Assert.True(buffer != tail);

        InstructionBuffer empty = tail.Tail();
        Assert.True(empty.IsEmpty);
        Assert.True(empty == InstructionBuffer.Empty);
        Assert.True(InstructionBuffer.Empty == InstructionBuffer.Of(Array.Empty<Instruction>()));
        Assert.Equal(InstructionBuffer.Of([move, think]), buffer);
        Assert.NotEqual(InstructionBuffer.Of([move]), buffer);
        Assert.False(InstructionBuffer.Empty == null);
        Assert.Equal(buffer.GetHashCode(), InstructionBuffer.Of([move, think]).GetHashCode());
        Assert.Equal(InstructionBuffer.Empty.GetHashCode(), tail.Tail().GetHashCode());
        Assert.NotEqual(buffer.GetHashCode(), InstructionBuffer.Of([move]).GetHashCode());
        Assert.NotEqual(InstructionBuffer.Empty.GetHashCode(), buffer.GetHashCode());
    }

    [Fact]
    public void BufferTailOfEmptyBufferThrows() {
        Assert.Throws<InvalidOperationException>(() => InstructionBuffer.Empty.Tail());
    }

    [Fact]
    public void GenesisStartsAwaitingMissingInstructionWithEmptyWorkspace() {
        PlayerProcessState genesis = PlayerProcessStateGenesis.Create();
        Assert.Equal(string.Empty, genesis.Workspace.Text);
        Assert.True(genesis.Pending.IsEmpty);
        AwaitingProgrammerProcess awaiting = Assert.IsType<AwaitingProgrammerProcess>(genesis.Mode);
        Assert.Equal(ProgrammingReason.MissingInstruction, awaiting.Reason);
        Assert.Equal(0, genesis.ControlRevision);
    }

    [Fact]
    public void StateAndModesRejectInvalidConstruction() {
        Assert.Throws<ArgumentNullException>(() => new PlayerProcessState(null!, InstructionBuffer.Empty, new ReadyProcess(), 0));
        Assert.Throws<ArgumentNullException>(() => new PlayerProcessState(new SubjectiveWorkspace(""), null!, new ReadyProcess(), 0));
        Assert.Throws<ArgumentNullException>(() => new PlayerProcessState(new SubjectiveWorkspace(""), InstructionBuffer.Empty, null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PlayerProcessState(new SubjectiveWorkspace(""), InstructionBuffer.Empty, new ReadyProcess(), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunningMoveProcess(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunningThinkProcess(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunningThinkProcess(-1));
    }

    [Fact]
    public void ProcessFactsRejectInvalidConstruction() {
        Assert.Throws<ArgumentNullException>(() => new ProgramAcceptedFact(null!, new SubjectiveWorkspace("")));
        Assert.Throws<ArgumentException>(() => new ProgramAcceptedFact([], new SubjectiveWorkspace("")));
        Assert.Throws<ArgumentException>(() => new ProgramAcceptedFact([null!], new SubjectiveWorkspace("")));
        Assert.Throws<ArgumentNullException>(() => new ProgramAcceptedFact([new StayInstruction()], null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MoveStartedFact(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MoveArrivedFact(-1));
        Assert.Throws<ArgumentException>(() => new MoveBlockedFact(" "));
        Assert.Throws<ArgumentException>(() => new ThinkStartedFact(" ", 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThinkStartedFact("note", 0));
    }

    [Fact]
    public void ProgramAcceptedFactFreezesProgramSnapshot() {
        var source = new List<Instruction> { new StayInstruction() };
        ProgramAcceptedFact fact = new(source, new SubjectiveWorkspace(""));
        source.Add(new MoveInstruction(new PassageHandle("ab")));
        source.RemoveAt(0);
        Assert.Single(fact.Program);
        Assert.IsType<StayInstruction>(fact.Program[0]);
    }

    [Fact]
    public void ProgramAcceptedInstallsProgramWorkspaceAndReadyMode() {
        PlayerProcessState genesis = PlayerProcessStateGenesis.Create();
        var workspace = new SubjectiveWorkspace("starting notes");
        Instruction[] program = [new MoveInstruction(new PassageHandle("ab")), new ThinkInstruction("plan")];
        PlayerProcessState accepted = PlayerProcessReducer.Apply(genesis, At(0), new ProgramAcceptedFact(program, workspace));
        Assert.IsType<ReadyProcess>(accepted.Mode);
        Assert.Same(workspace, accepted.Workspace);
        Assert.Equal(InstructionBuffer.Of(program), accepted.Pending);
        Assert.Equal(1, accepted.ControlRevision);
    }

    [Fact]
    public void MoveStartedConsumesHeadAndRunsMove() {
        var think = new ThinkInstruction("plan");
        PlayerProcessState ready = ReadyState("", [new MoveInstruction(new PassageHandle("ab")), think]);
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(0), new MoveStartedFact(7));
        RunningMoveProcess running = Assert.IsType<RunningMoveProcess>(started.Mode);
        Assert.Equal(7, running.MovementGeneration);
        Assert.Equal(InstructionBuffer.Of([think]), started.Pending);
        Assert.Equal(2, started.ControlRevision);
    }

    [Fact]
    public void MoveArrivedReturnsToReadyWhenTailRemains() {
        var think = new ThinkInstruction("plan");
        PlayerProcessState ready = ReadyState("seed", [new MoveInstruction(new PassageHandle("ab")), think]);
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(0), new MoveStartedFact(7));
        PlayerProcessState arrived = PlayerProcessReducer.Apply(started, At(1000), new MoveArrivedFact(7));
        Assert.IsType<ReadyProcess>(arrived.Mode);
        Assert.Equal(InstructionBuffer.Of([think]), arrived.Pending);
        Assert.Same(started.Workspace, arrived.Workspace);
        Assert.Equal(3, arrived.ControlRevision);
    }

    [Fact]
    public void MoveArrivedRequestsProgramWhenTailEmpty() {
        PlayerProcessState ready = ReadyState("", [new MoveInstruction(new PassageHandle("ab"))]);
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(0), new MoveStartedFact(1));
        PlayerProcessState arrived = PlayerProcessReducer.Apply(started, At(1000), new MoveArrivedFact(1));
        AwaitingProgrammerProcess awaiting = Assert.IsType<AwaitingProgrammerProcess>(arrived.Mode);
        Assert.Equal(ProgrammingReason.MissingInstruction, awaiting.Reason);
        Assert.True(arrived.Pending.IsEmpty);
        Assert.Equal(3, arrived.ControlRevision);
    }

    [Fact]
    public void MoveBlockedKeepsPendingHeadAndAcceptsAnyAwaitingReasonLater() {
        Instruction[] program = [new MoveInstruction(new PassageHandle("ab")), new ThinkInstruction("later")];
        PlayerProcessState ready = ReadyState("", program);
        PlayerProcessState blocked = PlayerProcessReducer.Apply(ready, At(0), new MoveBlockedFact("ab"));
        AwaitingProgrammerProcess awaiting = Assert.IsType<AwaitingProgrammerProcess>(blocked.Mode);
        Assert.Equal(ProgrammingReason.MoveBlocked, awaiting.Reason);
        Assert.Equal(ready.Pending, blocked.Pending);
        Assert.Equal(2, blocked.ControlRevision);

        PlayerProcessState accepted = PlayerProcessReducer.Apply(blocked, At(0),
            new ProgramAcceptedFact([new MoveInstruction(new PassageHandle("ab"))], new SubjectiveWorkspace("retry")));
        Assert.IsType<ReadyProcess>(accepted.Mode);
        Assert.Equal("retry", accepted.Workspace.Text);
        Assert.Equal(3, accepted.ControlRevision);
    }

    [Fact]
    public void ThinkStartedConsumesHeadAppendsNoteAndSetsDeadline() {
        PlayerProcessState ready = ReadyState("seed", [new ThinkInstruction("note one")]);
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(5000), new ThinkStartedFact("note one", 1000));
        RunningThinkProcess running = Assert.IsType<RunningThinkProcess>(started.Mode);
        Assert.Equal(6000, running.CompletesAtMs);
        Assert.True(started.Pending.IsEmpty);
        Assert.Equal("seed\n[5000] note one", started.Workspace.Text);
        Assert.Equal(2, started.ControlRevision);
    }

    [Fact]
    public void ThinkCompletedEntersAwaitingAtExactDeadline() {
        PlayerProcessState ready = ReadyState("", [new ThinkInstruction("note one")]);
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(0), new ThinkStartedFact("note one", 1000));
        PlayerProcessState completed = PlayerProcessReducer.Apply(started, At(1000), new ThinkCompletedFact());
        AwaitingProgrammerProcess awaiting = Assert.IsType<AwaitingProgrammerProcess>(completed.Mode);
        Assert.Equal(ProgrammingReason.ThinkCompleted, awaiting.Reason);
        Assert.Equal("[0] note one", completed.Workspace.Text);
        Assert.Equal(3, completed.ControlRevision);
    }

    [Fact]
    public void StayStartedConsumesHeadAndRunsStay() {
        PlayerProcessState ready = ReadyState("", [new StayInstruction()]);
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(0), new StayStartedFact());
        Assert.IsType<RunningStayProcess>(started.Mode);
        Assert.True(started.Pending.IsEmpty);
        Assert.Equal(2, started.ControlRevision);
    }

    [Fact]
    public void ProgramAcceptedOutsideAwaitingThrowsAndLeavesStateUntouched() {
        PlayerProcessState ready = ReadyState("", [new StayInstruction()]);
        Assert.Throws<InvalidOperationException>(() =>
            PlayerProcessReducer.Apply(ready, At(0), new ProgramAcceptedFact([new StayInstruction()], new SubjectiveWorkspace(""))));
        Assert.IsType<ReadyProcess>(ready.Mode);
        Assert.Equal(1, ready.ControlRevision);
        Assert.Equal("", ready.Workspace.Text);
    }

    [Fact]
    public void StartFactsRequireReadyModeAndMatchingHeadKind() {
        PlayerProcessState awaiting = PlayerProcessStateGenesis.Create();
        PlayerProcessState awaitingSnapshot = awaiting with { };
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(awaiting, At(0), new MoveStartedFact(0)));
        Assert.Equal(awaitingSnapshot, awaiting);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(awaiting, At(0), new MoveBlockedFact("ab")));
        Assert.Equal(awaitingSnapshot, awaiting);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(awaiting, At(0), new ThinkStartedFact("note", 1000)));
        Assert.Equal(awaitingSnapshot, awaiting);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(awaiting, At(0), new StayStartedFact()));
        Assert.Equal(awaitingSnapshot, awaiting);

        PlayerProcessState readyMove = ReadyState("", [new MoveInstruction(new PassageHandle("ab"))]);
        PlayerProcessState readyMoveSnapshot = readyMove with { };
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(readyMove, At(0), new ThinkStartedFact("note", 1000)));
        Assert.Equal(readyMoveSnapshot, readyMove);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(readyMove, At(0), new StayStartedFact()));
        Assert.Equal(readyMoveSnapshot, readyMove);

        PlayerProcessState readyThink = ReadyState("", [new ThinkInstruction("note")]);
        PlayerProcessState readyThinkSnapshot = readyThink with { };
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(readyThink, At(0), new MoveStartedFact(0)));
        Assert.Equal(readyThinkSnapshot, readyThink);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(readyThink, At(0), new MoveBlockedFact("ab")));
        Assert.Equal(readyThinkSnapshot, readyThink);

        PlayerProcessState readyStay = ReadyState("", [new StayInstruction()]);
        PlayerProcessState readyStaySnapshot = readyStay with { };
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(readyStay, At(0), new MoveStartedFact(0)));
        Assert.Equal(readyStaySnapshot, readyStay);

        PlayerProcessState readyEmpty = new(new SubjectiveWorkspace(""), InstructionBuffer.Empty, new ReadyProcess(), 0);
        PlayerProcessState readyEmptySnapshot = readyEmpty with { };
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(readyEmpty, At(0), new MoveStartedFact(0)));
        Assert.Equal(readyEmptySnapshot, readyEmpty);
    }

    [Fact]
    public void ControlRevisionOverflowThrowsAndLeavesStateUntouched() {
        var maxedOut = new PlayerProcessState(
            new SubjectiveWorkspace(""),
            InstructionBuffer.Of([new StayInstruction()]),
            new ReadyProcess(),
            long.MaxValue);
        PlayerProcessState maxedOutSnapshot = maxedOut with { };
        Assert.Throws<OverflowException>(() => PlayerProcessReducer.Apply(maxedOut, At(0), new StayStartedFact()));
        Assert.Equal(maxedOutSnapshot, maxedOut);
    }

    [Fact]
    public void MoveArrivedRejectsWrongModeAndGenerationMismatchWithoutStateChange() {
        PlayerProcessState ready = ReadyState("", [new MoveInstruction(new PassageHandle("ab")), new ThinkInstruction("plan")]);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(ready, At(0), new MoveArrivedFact(0)));
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(0), new MoveStartedFact(7));
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(started, At(1000), new MoveArrivedFact(8)));
        RunningMoveProcess running = Assert.IsType<RunningMoveProcess>(started.Mode);
        Assert.Equal(7, running.MovementGeneration);
        Assert.Equal(2, started.ControlRevision);
        Assert.Equal(InstructionBuffer.Of([new ThinkInstruction("plan")]), started.Pending);
    }

    [Fact]
    public void ThinkCompletedRejectsWrongModeAndTimeMismatchWithoutStateChange() {
        PlayerProcessState ready = ReadyState("", [new ThinkInstruction("note one")]);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(ready, At(0), new ThinkCompletedFact()));
        PlayerProcessState started = PlayerProcessReducer.Apply(ready, At(0), new ThinkStartedFact("note one", 1000));
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(started, At(999), new ThinkCompletedFact()));
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(started, At(1001), new ThinkCompletedFact()));
        RunningThinkProcess running = Assert.IsType<RunningThinkProcess>(started.Mode);
        Assert.Equal(1000, running.CompletesAtMs);
        Assert.Equal(2, started.ControlRevision);
        Assert.Equal("[0] note one", started.Workspace.Text);
    }

    [Fact]
    public void ReducerRejectsNullArgumentsAndUnknownFactTypes() {
        PlayerProcessState genesis = PlayerProcessStateGenesis.Create();
        Assert.Throws<ArgumentNullException>(() => PlayerProcessReducer.Apply(null!, At(0), new ThinkCompletedFact()));
        Assert.Throws<ArgumentNullException>(() => PlayerProcessReducer.Apply(genesis, At(0), null!));
        Assert.Throws<InvalidOperationException>(() => PlayerProcessReducer.Apply(genesis, At(0), new ProbeFact()));
    }

    [Fact]
    public void StayStartKeepsFollowingMoveAsUnstartedTail() {
        var move = new MoveInstruction(new PassageHandle("cd"));
        PlayerProcessState ready = ReadyState("", [new StayInstruction(), move]);
        PlayerProcessState staying = PlayerProcessReducer.Apply(ready, At(0), new StayStartedFact());
        Assert.IsType<RunningStayProcess>(staying.Mode);
        Assert.Single(staying.Pending.Items);
        Assert.Same(move, staying.Pending.Items[0]);
        Assert.Equal(2, staying.ControlRevision);
    }

    [Fact]
    public void StayStartKeepsFollowingThinkWithoutRecordingItsNote() {
        var think = new ThinkInstruction("deferred");
        PlayerProcessState ready = ReadyState("", [new StayInstruction(), think]);
        PlayerProcessState staying = PlayerProcessReducer.Apply(ready, At(0), new StayStartedFact());
        Assert.Single(staying.Pending.Items);
        Assert.Same(think, staying.Pending.Items[0]);
        Assert.DoesNotContain("deferred", staying.Workspace.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThinkStartedAppendsToEmptyWorkspaceAndExistingTextInFixedFormat() {
        PlayerProcessState empty = ReadyState("", [new ThinkInstruction("first")]);
        PlayerProcessState startedEmpty = PlayerProcessReducer.Apply(empty, At(0), new ThinkStartedFact("first", 1000));
        Assert.Equal("[0] first", startedEmpty.Workspace.Text);

        PlayerProcessState seeded = ReadyState("seed", [new ThinkInstruction("second")]);
        PlayerProcessState startedSeeded = PlayerProcessReducer.Apply(seeded, At(12345), new ThinkStartedFact("second", 1000));
        Assert.Equal("seed\n[12345] second", startedSeeded.Workspace.Text);
    }

    [Fact]
    public void ConsecutiveThinksRecordNotesOnSeparateTimestampedLinesWithMonotonicRevision() {
        var thinkOne = new ThinkInstruction("first");
        var thinkTwo = new ThinkInstruction("second");
        PlayerProcessState state = PlayerProcessStateGenesis.Create();
        state = PlayerProcessReducer.Apply(state, At(0), new ProgramAcceptedFact([thinkOne, thinkTwo], new SubjectiveWorkspace("")));
        Assert.Equal(1, state.ControlRevision);
        state = PlayerProcessReducer.Apply(state, At(0), new ThinkStartedFact("first", 1000));
        Assert.Equal(2, state.ControlRevision);
        state = PlayerProcessReducer.Apply(state, At(1000), new ThinkCompletedFact());
        Assert.Equal(3, state.ControlRevision);
        state = PlayerProcessReducer.Apply(state, At(1000), new ProgramAcceptedFact([thinkTwo], state.Workspace));
        Assert.Equal(4, state.ControlRevision);
        state = PlayerProcessReducer.Apply(state, At(1000), new ThinkStartedFact("second", 1000));
        Assert.Equal(5, state.ControlRevision);
        Assert.Equal("[0] first\n[1000] second", state.Workspace.Text);
        RunningThinkProcess running = Assert.IsType<RunningThinkProcess>(state.Mode);
        Assert.Equal(2000, running.CompletesAtMs);
        Assert.True(state.Pending.IsEmpty);
    }

    [Fact]
    public void ValidatorAcceptsStatesInEveryMode() {
        PlayerProcessValidator.Validate(PlayerProcessStateGenesis.Create());
        PlayerProcessValidator.Validate(ReadyState("", [new MoveInstruction(new PassageHandle("ab"))]));
        PlayerProcessValidator.Validate(PlayerProcessReducer.Apply(
            ReadyState("", [new MoveInstruction(new PassageHandle("ab")), new ThinkInstruction("plan")]), At(0), new MoveStartedFact(7)));
        PlayerProcessValidator.Validate(PlayerProcessReducer.Apply(
            ReadyState("", [new ThinkInstruction("plan")]), At(0), new ThinkStartedFact("plan", 1000)));
        PlayerProcessValidator.Validate(PlayerProcessReducer.Apply(
            ReadyState("", [new StayInstruction()]), At(0), new StayStartedFact()));
        PlayerProcessValidator.Validate(PlayerProcessReducer.Apply(
            ReadyState("", [new MoveInstruction(new PassageHandle("ab"))]), At(0), new MoveBlockedFact("ab")));
    }

    [Fact]
    public void ValidatorRejectsReadyModeWithoutPendingInstructionsAndNullState() {
        PlayerProcessState readyEmpty = new(new SubjectiveWorkspace(""), InstructionBuffer.Empty, new ReadyProcess(), 0);
        Assert.Throws<InvalidOperationException>(() => PlayerProcessValidator.Validate(readyEmpty));
        Assert.Throws<ArgumentNullException>(() => PlayerProcessValidator.Validate(null!));
    }

    private sealed record ProbeFact : FreePlayProcessFact;

    private static LogicalInstant At(long modelTimeMs) => new(new ModelTime(modelTimeMs), 0);

    private static PlayerProcessState ReadyState(string workspaceText, params Instruction[] program) {
        PlayerProcessState genesis = PlayerProcessStateGenesis.Create();
        return PlayerProcessReducer.Apply(genesis, At(0), new ProgramAcceptedFact(program, new SubjectiveWorkspace(workspaceText)));
    }
}

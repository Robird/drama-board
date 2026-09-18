using System.Globalization;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Folds one process fact at a logical instant into the next player process state.</summary>
internal static class PlayerProcessReducer {
    /// <summary>Folds the given fact into the given state and returns the next state without touching the inputs.</summary>
    /// <param name="state">The state to fold into.</param>
    /// <param name="instant">The logical instant of the fold, read for think scheduling and completion.</param>
    /// <param name="fact">The process fact to fold.</param>
    /// <returns>The next player process state with its control revision increased by exactly one.</returns>
    /// <exception cref="ArgumentNullException">The state or fact is null.</exception>
    /// <exception cref="InvalidOperationException">The fact's preconditions do not hold or its type is unknown; no state change happens.</exception>
    public static PlayerProcessState Apply(PlayerProcessState state, LogicalInstant instant, FreePlayProcessFact fact) {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);

        return fact switch {
            ProgramAcceptedFact programAccepted => FoldProgramAccepted(state, programAccepted),
            MoveStartedFact moveStarted => FoldMoveStarted(state, moveStarted),
            MoveArrivedFact moveArrived => FoldMoveArrived(state, moveArrived),
            MoveBlockedFact moveBlocked => FoldMoveBlocked(state, moveBlocked),
            ThinkStartedFact thinkStarted => FoldThinkStarted(state, instant, thinkStarted),
            ThinkCompletedFact => FoldThinkCompleted(state, instant),
            StayStartedFact => FoldStayStarted(state),
            _ => throw new InvalidOperationException(
                $"The player process reducer does not support fact type '{fact.GetType().FullName}'."),
        };
    }

    private static PlayerProcessState FoldProgramAccepted(PlayerProcessState state, ProgramAcceptedFact fact) {
        if (state.Mode is not AwaitingProgrammerProcess) {
            throw new InvalidOperationException(
                $"Accepting a program requires an awaiting-programmer process, but the current mode is '{DescribeMode(state.Mode)}'.");
        }

        return new PlayerProcessState(
            fact.Workspace,
            InstructionBuffer.Of(fact.Program),
            new ReadyProcess(),
            checked(state.ControlRevision + 1));
    }

    private static PlayerProcessState FoldMoveStarted(PlayerProcessState state, MoveStartedFact fact) {
        RequireReadyWithHead<MoveInstruction>(state, "Starting a move");
        return new PlayerProcessState(
            state.Workspace,
            state.Pending.Tail(),
            new RunningMoveProcess(fact.MovementGeneration),
            checked(state.ControlRevision + 1));
    }

    private static PlayerProcessState FoldMoveArrived(PlayerProcessState state, MoveArrivedFact fact) {
        if (state.Mode is not RunningMoveProcess running) {
            throw new InvalidOperationException(
                $"Recording a move arrival requires a running-move process, but the current mode is '{DescribeMode(state.Mode)}'.");
        }

        if (running.MovementGeneration != fact.ExpectedMovementGeneration) {
            throw new InvalidOperationException(
                $"Recording a move arrival requires running movement generation '{fact.ExpectedMovementGeneration}', "
                + $"but the running move has generation '{running.MovementGeneration}'.");
        }

        ProcessMode nextMode = state.Pending.IsEmpty
            ? new AwaitingProgrammerProcess(ProgrammingReason.MissingInstruction)
            : new ReadyProcess();

        return new PlayerProcessState(state.Workspace, state.Pending, nextMode, checked(state.ControlRevision + 1));
    }

    private static PlayerProcessState FoldMoveBlocked(PlayerProcessState state, MoveBlockedFact fact) {
        RequireReadyWithHead<MoveInstruction>(state, "Recording a blocked move");
        return new PlayerProcessState(
            state.Workspace,
            state.Pending,
            new AwaitingProgrammerProcess(ProgrammingReason.MoveBlocked),
            checked(state.ControlRevision + 1));
    }

    private static PlayerProcessState FoldThinkStarted(PlayerProcessState state, LogicalInstant instant, ThinkStartedFact fact) {
        RequireReadyWithHead<ThinkInstruction>(state, "Starting a think");
        return new PlayerProcessState(
            AppendNote(state.Workspace.Text, instant, fact.Note),
            state.Pending.Tail(),
            new RunningThinkProcess(checked(instant.ModelTime.Ticks + fact.DurationMs)),
            checked(state.ControlRevision + 1));
    }

    private static PlayerProcessState FoldThinkCompleted(PlayerProcessState state, LogicalInstant instant) {
        if (state.Mode is not RunningThinkProcess running) {
            throw new InvalidOperationException(
                $"Recording a think completion requires a running-think process, but the current mode is '{DescribeMode(state.Mode)}'.");
        }

        if (instant.ModelTime.Ticks != running.CompletesAtMs) {
            throw new InvalidOperationException(
                $"Recording a think completion requires model time '{running.CompletesAtMs}' milliseconds, "
                + $"but the instant is '{instant.ModelTime.Ticks}' milliseconds.");
        }

        return new PlayerProcessState(
            state.Workspace,
            state.Pending,
            new AwaitingProgrammerProcess(ProgrammingReason.ThinkCompleted),
            checked(state.ControlRevision + 1));
    }

    private static PlayerProcessState FoldStayStarted(PlayerProcessState state) {
        RequireReadyWithHead<StayInstruction>(state, "Starting a stay");
        return new PlayerProcessState(
            state.Workspace,
            state.Pending.Tail(),
            new RunningStayProcess(),
            checked(state.ControlRevision + 1));
    }

    private static void RequireReadyWithHead<TInstruction>(PlayerProcessState state, string action)
        where TInstruction : Instruction {
        if (state.Mode is not ReadyProcess) {
            throw new InvalidOperationException(
                $"{action} requires a ready process, but the current mode is '{DescribeMode(state.Mode)}'.");
        }

        if (state.Pending.Head is not TInstruction) {
            throw new InvalidOperationException(
                $"{action} requires a pending {typeof(TInstruction).Name}, but the pending head is {DescribeHead(state.Pending.Head)}.");
        }
    }

    private static SubjectiveWorkspace AppendNote(string text, LogicalInstant instant, string note) {
        string appended = text.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"[{instant.ModelTime.Ticks}] {note}")
            : string.Create(CultureInfo.InvariantCulture, $"{text}\n[{instant.ModelTime.Ticks}] {note}");

        return new SubjectiveWorkspace(appended);
    }

    private static string DescribeMode(ProcessMode mode) => mode.GetType().Name;

    private static string DescribeHead(Instruction? head) => head is null ? "empty" : $"'{head.GetType().Name}'";
}

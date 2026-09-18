using DramaBoard.Protocol;

namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Represents the mutually exclusive execution mode of one player process.</summary>
internal abstract record ProcessMode;

/// <summary>Represents a process whose pending program is ready to start its head instruction.</summary>
internal sealed record ReadyProcess : ProcessMode;

/// <summary>Represents a process waiting for the programmer to submit one program.</summary>
/// <param name="Reason">The reason the runtime is waiting for a program.</param>
internal sealed record AwaitingProgrammerProcess(ProgrammingReason Reason) : ProcessMode;

/// <summary>Represents a process whose head move instruction has started traversing.</summary>
/// <param name="MovementGeneration">The movement generation that associates this run with the Spatial domain.</param>
internal sealed record RunningMoveProcess(long MovementGeneration) : ProcessMode {
    /// <summary>Gets the movement generation that associates this run with the Spatial domain.</summary>
    public long MovementGeneration { get; } = RequireNonNegative(MovementGeneration, nameof(MovementGeneration));

    private static long RequireNonNegative(long movementGeneration, string parameterName) {
        if (movementGeneration < 0) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                movementGeneration,
                "The movement generation cannot be negative.");
        }

        return movementGeneration;
    }
}

/// <summary>Represents a process whose head think instruction is running until its completion time.</summary>
/// <param name="CompletesAtMs">The model time in milliseconds at which the think completes.</param>
internal sealed record RunningThinkProcess(long CompletesAtMs) : ProcessMode {
    /// <summary>Gets the model time in milliseconds at which the think completes.</summary>
    public long CompletesAtMs { get; } = RequirePositive(CompletesAtMs, nameof(CompletesAtMs));

    private static long RequirePositive(long completesAtMs, string parameterName) {
        if (completesAtMs <= 0) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                completesAtMs,
                "The think completion time must be positive.");
        }

        return completesAtMs;
    }
}

/// <summary>Represents a process whose head stay instruction has started and has no natural completion.</summary>
internal sealed record RunningStayProcess : ProcessMode;

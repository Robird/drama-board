using DramaBoard.Protocol;

namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Represents one process-domain fact folded by the player process reducer.</summary>
internal abstract record FreePlayProcessFact;

/// <summary>Reports that the runtime accepted one complete program and workspace from the programmer.</summary>
/// <param name="Program">The accepted program instructions; never empty.</param>
/// <param name="Workspace">The accepted subjective workspace.</param>
internal sealed record ProgramAcceptedFact(IReadOnlyList<Instruction> Program, SubjectiveWorkspace Workspace) : FreePlayProcessFact {
    /// <summary>Gets the accepted program instructions as one frozen snapshot.</summary>
    public IReadOnlyList<Instruction> Program { get; } = FreezeProgram(Program);

    /// <summary>Gets the accepted subjective workspace.</summary>
    public SubjectiveWorkspace Workspace { get; } = RequireWorkspace(Workspace);

    private static IReadOnlyList<Instruction> FreezeProgram(IReadOnlyList<Instruction> program) {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Count == 0) {
            throw new ArgumentException(
                "Accepted programs cannot be empty; normalize empty programs to a stay instruction before acceptance.",
                nameof(Program));
        }

        foreach (Instruction instruction in program) {
            if (instruction is null) {
                throw new ArgumentException("Accepted programs cannot contain null entries.", nameof(Program));
            }
        }

        return Array.AsReadOnly(program.ToArray());
    }

    private static SubjectiveWorkspace RequireWorkspace(SubjectiveWorkspace workspace) {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace;
    }
}

/// <summary>Reports that the head move instruction started traversing its passage.</summary>
/// <param name="MovementGeneration">The movement generation that associates this run with the Spatial domain.</param>
internal sealed record MoveStartedFact(long MovementGeneration) : FreePlayProcessFact {
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

/// <summary>Reports that the running move arrived at its target place.</summary>
/// <param name="ExpectedMovementGeneration">The movement generation the arrival must match.</param>
internal sealed record MoveArrivedFact(long ExpectedMovementGeneration) : FreePlayProcessFact {
    /// <summary>Gets the movement generation the arrival must match.</summary>
    public long ExpectedMovementGeneration { get; } = RequireNonNegative(ExpectedMovementGeneration, nameof(ExpectedMovementGeneration));

    private static long RequireNonNegative(long expectedMovementGeneration, string parameterName) {
        if (expectedMovementGeneration < 0) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                expectedMovementGeneration,
                "The expected movement generation cannot be negative.");
        }

        return expectedMovementGeneration;
    }
}

/// <summary>Reports that the head move instruction was blocked at its passage.</summary>
/// <param name="PassageId">The identifier of the blocked passage, for display only.</param>
internal sealed record MoveBlockedFact(string PassageId) : FreePlayProcessFact {
    /// <summary>Gets the identifier of the blocked passage, for display only.</summary>
    public string PassageId { get; } = RequirePassageId(PassageId);

    private static string RequirePassageId(string passageId) {
        if (string.IsNullOrWhiteSpace(passageId)) {
            throw new ArgumentException("The blocked passage identifier cannot be empty.", nameof(PassageId));
        }

        return passageId;
    }
}

/// <summary>Reports that the head think instruction started producing one note.</summary>
/// <param name="Note">The natural-language note produced by thinking.</param>
/// <param name="DurationMs">The think duration in milliseconds; always positive.</param>
internal sealed record ThinkStartedFact(string Note, long DurationMs) : FreePlayProcessFact {
    /// <summary>Gets the natural-language note produced by thinking.</summary>
    public string Note { get; } = RequireNote(Note);

    /// <summary>Gets the think duration in milliseconds.</summary>
    public long DurationMs { get; } = RequireDuration(DurationMs, nameof(DurationMs));

    private static string RequireNote(string note) {
        if (string.IsNullOrWhiteSpace(note)) {
            throw new ArgumentException("Think notes cannot be empty.", nameof(Note));
        }

        return note;
    }

    private static long RequireDuration(long durationMs, string parameterName) {
        if (durationMs <= 0) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                durationMs,
                "The think duration must be positive.");
        }

        return durationMs;
    }
}

/// <summary>Reports that the running think instruction completed at its completion time.</summary>
internal sealed record ThinkCompletedFact : FreePlayProcessFact;

/// <summary>Reports that the head stay instruction started and the actor is now staying.</summary>
internal sealed record StayStartedFact : FreePlayProcessFact;

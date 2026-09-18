using DramaBoard.Protocol;

namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Represents one immutable player process snapshot: pending instructions, execution mode and control revision.</summary>
/// <param name="Workspace">The actor's subjective workspace text.</param>
/// <param name="Pending">The instructions that have not started yet.</param>
/// <param name="Mode">The current mutually exclusive execution mode.</param>
/// <param name="ControlRevision">The number of process facts folded into this state.</param>
internal sealed record PlayerProcessState(
    SubjectiveWorkspace Workspace,
    InstructionBuffer Pending,
    ProcessMode Mode,
    long ControlRevision) {
    /// <summary>Gets the actor's subjective workspace text.</summary>
    public SubjectiveWorkspace Workspace { get; } = RequireNotNull(Workspace, nameof(Workspace));

    /// <summary>Gets the instructions that have not started yet.</summary>
    public InstructionBuffer Pending { get; } = RequireNotNull(Pending, nameof(Pending));

    /// <summary>Gets the current mutually exclusive execution mode.</summary>
    public ProcessMode Mode { get; } = RequireNotNull(Mode, nameof(Mode));

    /// <summary>Gets the number of process facts folded into this state; it increases by exactly one per fold.</summary>
    public long ControlRevision { get; } = RequireControlRevision(ControlRevision);

    private static T RequireNotNull<T>(T value, string parameterName) where T : class {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }

    private static long RequireControlRevision(long controlRevision) {
        if (controlRevision < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(ControlRevision),
                controlRevision,
                "The control revision cannot be negative.");
        }

        return controlRevision;
    }
}

/// <summary>Creates the initial player process state.</summary>
internal static class PlayerProcessStateGenesis {
    /// <summary>Creates the state every run starts from: an empty workspace, no pending instructions and a missing-instruction wait.</summary>
    /// <returns>The initial player process state.</returns>
    public static PlayerProcessState Create() =>
        new(new SubjectiveWorkspace(string.Empty), InstructionBuffer.Empty, new AwaitingProgrammerProcess(ProgrammingReason.MissingInstruction), 0);
}

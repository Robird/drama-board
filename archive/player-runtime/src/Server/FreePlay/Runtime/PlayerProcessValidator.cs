namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Validates one player process state against the process domain's invariants.</summary>
internal static class PlayerProcessValidator {
    /// <summary>Validates the given player process state and throws when an invariant is violated.</summary>
    /// <param name="state">The state to validate.</param>
    /// <exception cref="ArgumentNullException">The state is null.</exception>
    /// <exception cref="InvalidOperationException">The state violates a process invariant.</exception>
    public static void Validate(PlayerProcessState state) {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Workspace is null) {
            throw new InvalidOperationException("The player process state requires a workspace.");
        }

        if (state.Mode is ReadyProcess && state.Pending.IsEmpty) {
            throw new InvalidOperationException("A ready process must hold at least one pending instruction.");
        }

        if (state.ControlRevision < 0) {
            throw new InvalidOperationException("The player process control revision cannot be negative.");
        }

        switch (state.Mode) {
            case RunningMoveProcess move:
                if (move.MovementGeneration < 0) {
                    throw new InvalidOperationException("A running move cannot use a negative movement generation.");
                }

                break;

            case RunningThinkProcess think:
                if (think.CompletesAtMs <= 0) {
                    throw new InvalidOperationException("A running think must complete after the model epoch.");
                }

                break;
        }
    }
}

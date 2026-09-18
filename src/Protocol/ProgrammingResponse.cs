namespace DramaBoard.Protocol;

/// <summary>Carries the dynamic programmer's program for one activation.</summary>
/// <param name="ActivationId">The identifier of the activation receiving the program.</param>
/// <param name="Program">The program instructions, which may be empty for a complete stay response.</param>
/// <param name="Workspace">The updated actor's subjective workspace text.</param>
public sealed record ProgrammingResponse {
    /// <summary>Creates one frozen programming response and validates its inputs.</summary>
    public ProgrammingResponse(
        ActivationId ActivationId,
        IReadOnlyList<Instruction> Program,
        SubjectiveWorkspace Workspace) {
        if (string.IsNullOrWhiteSpace(ActivationId.Value)) {
            throw new ArgumentException("Activation identifier must be initialized.", nameof(ActivationId));
        }

        ArgumentNullException.ThrowIfNull(Workspace);
        this.ActivationId = ActivationId;
        this.Program = FreezeProgram(Program, nameof(Program));
        this.Workspace = Workspace;
    }

    public ActivationId ActivationId { get; }

    public IReadOnlyList<Instruction> Program { get; }

    public SubjectiveWorkspace Workspace { get; }

    private static IReadOnlyList<Instruction> FreezeProgram(
        IReadOnlyList<Instruction> instructions,
        string parameterName) {
        ArgumentNullException.ThrowIfNull(instructions);
        foreach (Instruction instruction in instructions) {
            if (instruction is null) {
                throw new ArgumentException("Program instructions cannot contain null entries.", parameterName);
            }
        }

        return FrozenList.Snapshot(instructions);
    }
}

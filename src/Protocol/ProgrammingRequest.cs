namespace DramaBoard.Protocol;

/// <summary>Requests one program from the dynamic programmer for an actor.</summary>
/// <param name="ActivationId">The identifier of the activation awaiting the program.</param>
/// <param name="ActorId">The identifier of the actor awaiting the program.</param>
/// <param name="ModelTimeMs">The request model time, where one unit is one millisecond.</param>
/// <param name="Reason">The reason the runtime is requesting a program.</param>
/// <param name="ReasonDetail">The optional human-readable detail about the reason.</param>
/// <param name="PlaceId">The identifier of the place where the actor currently is.</param>
/// <param name="KnownPassages">The passages known to the actor for movement planning.</param>
/// <param name="Workspace">The actor's subjective workspace text.</param>
/// <param name="RemainingProgram">The program instructions not yet executed.</param>
public sealed record ProgrammingRequest {
    /// <summary>Creates one frozen programming request and validates its inputs.</summary>
    public ProgrammingRequest(
        ActivationId ActivationId,
        string ActorId,
        long ModelTimeMs,
        ProgrammingReason Reason,
        string? ReasonDetail,
        string PlaceId,
        IReadOnlyList<KnownPassage> KnownPassages,
        SubjectiveWorkspace Workspace,
        IReadOnlyList<Instruction> RemainingProgram) {
        if (string.IsNullOrWhiteSpace(ActivationId.Value)) {
            throw new ArgumentException("Activation identifier must be initialized.", nameof(ActivationId));
        }

        if (string.IsNullOrWhiteSpace(ActorId)) {
            throw new ArgumentException("Actor identifier cannot be empty.", nameof(ActorId));
        }

        if (string.IsNullOrWhiteSpace(PlaceId)) {
            throw new ArgumentException("Place identifier cannot be empty.", nameof(PlaceId));
        }

        ArgumentNullException.ThrowIfNull(Workspace);
        this.ActivationId = ActivationId;
        this.ActorId = ActorId;
        this.ModelTimeMs = ModelTimeMs;
        this.Reason = Reason;
        this.ReasonDetail = ReasonDetail;
        this.PlaceId = PlaceId;
        this.KnownPassages = FreezeKnownPassages(KnownPassages, nameof(KnownPassages));
        this.RemainingProgram = FreezeInstructions(RemainingProgram, nameof(RemainingProgram));
        this.Workspace = Workspace;
    }

    public ActivationId ActivationId { get; }

    public string ActorId { get; }

    public long ModelTimeMs { get; }

    public ProgrammingReason Reason { get; }

    public string? ReasonDetail { get; }

    public string PlaceId { get; }

    public IReadOnlyList<KnownPassage> KnownPassages { get; }

    public SubjectiveWorkspace Workspace { get; }

    public IReadOnlyList<Instruction> RemainingProgram { get; }

    private static IReadOnlyList<KnownPassage> FreezeKnownPassages(
        IReadOnlyList<KnownPassage> knownPassages,
        string parameterName) {
        ArgumentNullException.ThrowIfNull(knownPassages);
        var seenHandles = new HashSet<string>(StringComparer.Ordinal);
        foreach (KnownPassage passage in knownPassages) {
            if (passage is null) {
                throw new ArgumentException("Known passages cannot contain null entries.", parameterName);
            }

            if (!seenHandles.Add(passage.Handle.Value)) {
                throw new ArgumentException(
                    $"Known passage '{passage.Handle.Value}' is duplicated.",
                    parameterName);
            }
        }

        return FrozenList.Snapshot(knownPassages);
    }

    private static IReadOnlyList<Instruction> FreezeInstructions(
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

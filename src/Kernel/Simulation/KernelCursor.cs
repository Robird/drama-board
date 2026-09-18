using Atelia.DurableGraph;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>The finite scheduling boundary saved alongside a completed world.</summary>
[DurableType("DramaBoard.Kernel.KernelCursor", 1)]
public readonly partial record struct KernelCursor {
    public KernelCursor(WorldVersion version, ModelTime genesisTime,
        LogicalInstant? lastInstant, CandidateKey? lastCauseKey) {
        Version = version;
        GenesisTime = genesisTime;
        LastInstant = lastInstant;
        LastCauseKey = lastCauseKey;
        Validate();
    }

    [field: DurableField(1)] public WorldVersion Version { get; }
    [field: DurableField(2)] public ModelTime GenesisTime { get; }
    [field: DurableField(3)] public LogicalInstant? LastInstant { get; }
    [field: DurableField(4)] public CandidateKey? LastCauseKey { get; }
    public ModelTime CurrentModelTime => LastInstant?.ModelTime ?? GenesisTime;

    /// <summary>Validates restored values without relying on constructor execution.</summary>
    public void Validate() {
        if (Version.TransitionCount < 0 ||
            (Version.TransitionCount == 0 && (LastInstant is not null || LastCauseKey is not null)) ||
            (Version.TransitionCount > 0 && (LastInstant is null || LastCauseKey is null))) {
            throw new ArgumentException("A cursor must have both a last instant and cause exactly when its transition count is positive.");
        }
        if (LastInstant is { } instant && (instant.ModelTime < GenesisTime || instant.CausalOrdinal < 0 ||
            instant.CausalOrdinal >= Version.TransitionCount)) {
            throw new ArgumentException("The committed instant must not precede Genesis, and its causal ordinal must be nonnegative and below the completed transition count.");
        }
        if (LastCauseKey is { Length: 0 }) {
            throw new ArgumentException("The last cause key cannot be empty.");
        }
    }

    public KernelCursor Advance(CandidateKey causeKey, LogicalInstant targetInstant) => new(
        new WorldVersion(Version.LineageId, checked(Version.TransitionCount + 1)),
        GenesisTime, targetInstant, causeKey);
}

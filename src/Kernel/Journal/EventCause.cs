using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Journal;

/// <summary>Identifies the authoritative cause of one committed event batch.</summary>
public readonly record struct EventCause
{
    /// <summary>Initializes validated provenance for a committed event batch.</summary>
    public EventCause(
        CauseKind kind,
        long sourceId,
        EventCandidateId candidateId,
        ModelTime due,
        long batchOrdinal)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (batchOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchOrdinal), "The batch ordinal cannot be negative.");
        }

        if (kind == CauseKind.ExternalInput &&
            (sourceId != default || candidateId != default || due != default))
        {
            throw new ArgumentException("External input causes cannot carry resolve candidate metadata.", nameof(kind));
        }

        Kind = kind;
        SourceId = sourceId;
        CandidateId = candidateId;
        Due = due;
        BatchOrdinal = batchOrdinal;
    }

    /// <summary>Gets whether the batch came from a candidate resolve or external input.</summary>
    public CauseKind Kind { get; }

    /// <summary>Gets the stable source identity for a resolve batch.</summary>
    public long SourceId { get; }

    /// <summary>Gets the selected candidate identity for a resolve batch.</summary>
    public EventCandidateId CandidateId { get; }

    /// <summary>Gets the selected candidate due time for a resolve batch.</summary>
    public ModelTime Due { get; }

    /// <summary>Gets the globally increasing batch ordinal within the lineage.</summary>
    public long BatchOrdinal { get; }

    /// <summary>Creates provenance for events emitted by one candidate resolve.</summary>
    public static EventCause FromResolve(
        long sourceId,
        EventCandidateId candidateId,
        ModelTime due,
        long batchOrdinal) =>
        new(CauseKind.ResolveBatch, sourceId, candidateId, due, batchOrdinal);

    /// <summary>Creates provenance for one externally supplied input batch.</summary>
    public static EventCause FromExternalInput(long batchOrdinal) =>
        new(CauseKind.ExternalInput, default, default, default, batchOrdinal);
}

/// <summary>Identifies the authoritative origin of a committed event batch.</summary>
public enum CauseKind
{
    /// <summary>The simulation loop resolved a scheduled candidate.</summary>
    ResolveBatch,

    /// <summary>The host supplied an external input batch.</summary>
    ExternalInput,
}
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Scheduling;

/// <summary>Represents a typed forecast result selected by deterministic scheduling metadata.</summary>
public readonly struct EventCandidate<TPayload>
{
    /// <summary>Initializes a forecast candidate and its deterministic scheduling metadata.</summary>
    public EventCandidate(
        EventCandidateId id,
        ModelTime due,
        long sourceId,
        TPayload payload)
    {
        Id = id;
        Due = due;
        SourceId = sourceId;
        Payload = payload;
    }

    /// <summary>Gets the stable candidate identifier.</summary>
    public EventCandidateId Id { get; }

    /// <summary>Gets the earliest model time at which the candidate may be resolved.</summary>
    public ModelTime Due { get; }

    /// <summary>Gets the stable source identifier, whose numeric order participates in deterministic tie-breaking.</summary>
    public long SourceId { get; }

    /// <summary>Gets the typed forecast payload.</summary>
    public TPayload Payload { get; }
}

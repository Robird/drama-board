using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Scheduling;

/// <summary>Represents a typed forecast result that may become stale before it is resolved.</summary>
public readonly struct EventCandidate<TPayload>
{
    /// <summary>Initializes a forecast candidate and its deterministic scheduling metadata.</summary>
    public EventCandidate(
        EventCandidateId id,
        ModelTime due,
        long sourceId,
        long generation,
        TPayload payload)
    {
        Id = id;
        Due = due;
        SourceId = sourceId;
        Generation = generation;
        Payload = payload;
    }

    /// <summary>Gets the stable candidate identifier.</summary>
    public EventCandidateId Id { get; }

    /// <summary>Gets the earliest model time at which the candidate may be resolved.</summary>
    public ModelTime Due { get; }

    /// <summary>Gets the stable source identifier, whose numeric order participates in deterministic tie-breaking.</summary>
    public long SourceId { get; }

    /// <summary>Gets the source generation that produced the candidate.</summary>
    public long Generation { get; }

    /// <summary>Gets the typed forecast payload.</summary>
    public TPayload Payload { get; }
}
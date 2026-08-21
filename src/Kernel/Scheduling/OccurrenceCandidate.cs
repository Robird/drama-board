namespace DramaBoard.Kernel.Scheduling;

/// <summary>Represents one temporary forecast owned by the rule that produced it.</summary>
public sealed record OccurrenceCandidate<TData>
{
    /// <summary>Initializes a forecast candidate.</summary>
    public OccurrenceCandidate(CandidateKey key, CandidateDue due, TData data)
    {
        ArgumentNullException.ThrowIfNull(key);

        Key = key;
        Due = due;
        Data = data;
    }

    /// <summary>Gets the complete stable identity derived by the owning rule.</summary>
    public CandidateKey Key { get; }

    /// <summary>Gets the candidate's quantized due time.</summary>
    public CandidateDue Due { get; }

    /// <summary>Gets immutable data private to the owning rule.</summary>
    public TData Data { get; }
}

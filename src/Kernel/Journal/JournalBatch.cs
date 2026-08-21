using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Journal;

/// <summary>Represents one atomically committed occurrence and its ordered, non-empty facts.</summary>
public sealed class JournalBatch<TFact>
{
    /// <summary>Initializes a committed batch by copying its facts.</summary>
    public JournalBatch(
        LogicalInstant instant,
        CandidateKey causeKey,
        IEnumerable<TFact> facts)
    {
        ArgumentNullException.ThrowIfNull(causeKey);
        ArgumentNullException.ThrowIfNull(facts);

        TFact[] factArray = [.. facts];
        if (factArray.Length == 0)
        {
            throw new ArgumentException("A journal batch must contain at least one fact.", nameof(facts));
        }

        if (factArray.Any(fact => fact is null))
        {
            throw new ArgumentException("A journal batch cannot contain null facts.", nameof(facts));
        }

        Instant = instant;
        CauseKey = causeKey;
        Facts = Array.AsReadOnly(factArray);
    }

    /// <summary>Gets the single logical instant shared by every fact in the batch.</summary>
    public LogicalInstant Instant { get; }

    /// <summary>Gets the selected occurrence candidate's complete canonical key.</summary>
    public CandidateKey CauseKey { get; }

    /// <summary>Gets facts in authoritative fold order.</summary>
    public IReadOnlyList<TFact> Facts { get; }
}

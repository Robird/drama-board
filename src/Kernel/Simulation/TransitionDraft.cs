namespace DramaBoard.Kernel.Simulation;

/// <summary>Contains the complete ordered facts proposed by one selected occurrence rule.</summary>
public sealed class TransitionDraft<TFact>
{
    /// <summary>Initializes a draft by copying a non-empty fact sequence.</summary>
    public TransitionDraft(IEnumerable<TFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        TFact[] factArray = [.. facts];
        if (factArray.Length == 0)
        {
            throw new ArgumentException("A transition draft must contain at least one fact.", nameof(facts));
        }

        if (factArray.Any(fact => fact is null))
        {
            throw new ArgumentException("A transition draft cannot contain null facts.", nameof(facts));
        }

        Facts = Array.AsReadOnly(factArray);
    }

    /// <summary>Gets facts in authoritative fold order.</summary>
    public IReadOnlyList<TFact> Facts { get; }
}

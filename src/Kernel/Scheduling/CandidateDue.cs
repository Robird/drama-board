using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Scheduling;

/// <summary>Represents an occurrence candidate's due time as one integer-millisecond model tick.</summary>
public readonly record struct CandidateDue : IComparable<CandidateDue>
{
    /// <summary>Initializes a due time that is already quantized to a model tick.</summary>
    public CandidateDue(ModelTime modelTime)
    {
        ModelTime = modelTime;
    }

    /// <summary>Gets the quantized model time.</summary>
    public ModelTime ModelTime { get; }

    /// <summary>
    /// Quantizes an exact millisecond value upward to the first integer model tick that is not
    /// earlier than the supplied value.
    /// </summary>
    public static CandidateDue FromExactMilliseconds(decimal exactMilliseconds)
    {
        decimal quantizedMilliseconds = decimal.Ceiling(exactMilliseconds);
        if (quantizedMilliseconds < long.MinValue || quantizedMilliseconds > long.MaxValue)
        {
            throw new OverflowException("The quantized candidate due time exceeds the ModelTime range.");
        }

        return new CandidateDue(new ModelTime(decimal.ToInt64(quantizedMilliseconds)));
    }

    /// <inheritdoc />
    public int CompareTo(CandidateDue other) => ModelTime.CompareTo(other.ModelTime);

    /// <inheritdoc />
    public override string ToString() => ModelTime.ToString();

    /// <summary>Returns whether the left due time precedes the right due time.</summary>
    public static bool operator <(CandidateDue left, CandidateDue right) =>
        left.CompareTo(right) < 0;

    /// <summary>Returns whether the left due time precedes or equals the right due time.</summary>
    public static bool operator <=(CandidateDue left, CandidateDue right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Returns whether the left due time follows the right due time.</summary>
    public static bool operator >(CandidateDue left, CandidateDue right) =>
        left.CompareTo(right) > 0;

    /// <summary>Returns whether the left due time follows or equals the right due time.</summary>
    public static bool operator >=(CandidateDue left, CandidateDue right) =>
        left.CompareTo(right) >= 0;
}

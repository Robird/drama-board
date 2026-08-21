namespace DramaBoard.Kernel.Time;

/// <summary>Identifies one committed occurrence by model time and its causal order at that time.</summary>
public readonly record struct LogicalInstant : IComparable<LogicalInstant>
{
    /// <summary>Initializes a committed logical instant.</summary>
    public LogicalInstant(ModelTime modelTime, long causalOrdinal)
    {
        if (causalOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(causalOrdinal),
                "The causal ordinal cannot be negative.");
        }

        ModelTime = modelTime;
        CausalOrdinal = causalOrdinal;
    }

    /// <summary>Gets the occurrence's integer-millisecond model time.</summary>
    public ModelTime ModelTime { get; }

    /// <summary>Gets the committed causal order within <see cref="ModelTime"/>.</summary>
    public long CausalOrdinal { get; }

    /// <inheritdoc />
    public int CompareTo(LogicalInstant other)
    {
        int timeComparison = ModelTime.CompareTo(other.ModelTime);
        return timeComparison != 0
            ? timeComparison
            : CausalOrdinal.CompareTo(other.CausalOrdinal);
    }

    /// <summary>Returns whether the left instant precedes the right instant.</summary>
    public static bool operator <(LogicalInstant left, LogicalInstant right) =>
        left.CompareTo(right) < 0;

    /// <summary>Returns whether the left instant precedes or equals the right instant.</summary>
    public static bool operator <=(LogicalInstant left, LogicalInstant right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Returns whether the left instant follows the right instant.</summary>
    public static bool operator >(LogicalInstant left, LogicalInstant right) =>
        left.CompareTo(right) > 0;

    /// <summary>Returns whether the left instant follows or equals the right instant.</summary>
    public static bool operator >=(LogicalInstant left, LogicalInstant right) =>
        left.CompareTo(right) >= 0;
}

namespace DramaBoard.Kernel.Time;

/// <summary>Represents a total-order simulation key composed of model time followed by causal microstep.</summary>
public readonly struct LogicalTimestamp : IComparable<LogicalTimestamp>, IEquatable<LogicalTimestamp>
{
    /// <summary>Initializes a logical timestamp from its model time and causal microstep.</summary>
    public LogicalTimestamp(ModelTime modelTime, Microstep microstep)
    {
        ModelTime = modelTime;
        Microstep = microstep;
    }

    /// <summary>Gets the world logical time component.</summary>
    public ModelTime ModelTime { get; }

    /// <summary>Gets the causal order component within the model time.</summary>
    public Microstep Microstep { get; }

    /// <summary>Compares timestamps lexicographically by model time and then by microstep.</summary>
    public int CompareTo(LogicalTimestamp other)
    {
        int modelTimeComparison = ModelTime.CompareTo(other.ModelTime);
        return modelTimeComparison != 0 ? modelTimeComparison : Microstep.CompareTo(other.Microstep);
    }

    /// <summary>Returns whether both components equal those of another logical timestamp.</summary>
    public bool Equals(LogicalTimestamp other) =>
        ModelTime.Equals(other.ModelTime) && Microstep.Equals(other.Microstep);

    /// <summary>Returns whether both components equal those of an object.</summary>
    public override bool Equals(object? obj) => obj is LogicalTimestamp other && Equals(other);

    /// <summary>Returns a hash code composed from the model time and microstep.</summary>
    public override int GetHashCode() => HashCode.Combine(ModelTime, Microstep);

    /// <summary>Returns whether two logical timestamps have equal components.</summary>
    public static bool operator ==(LogicalTimestamp left, LogicalTimestamp right) => left.Equals(right);

    /// <summary>Returns whether two logical timestamps have different components.</summary>
    public static bool operator !=(LogicalTimestamp left, LogicalTimestamp right) => !left.Equals(right);

    /// <summary>Returns whether the left logical timestamp precedes the right logical timestamp.</summary>
    public static bool operator <(LogicalTimestamp left, LogicalTimestamp right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether the left logical timestamp precedes or equals the right logical timestamp.</summary>
    public static bool operator <=(LogicalTimestamp left, LogicalTimestamp right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether the left logical timestamp follows the right logical timestamp.</summary>
    public static bool operator >(LogicalTimestamp left, LogicalTimestamp right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether the left logical timestamp follows or equals the right logical timestamp.</summary>
    public static bool operator >=(LogicalTimestamp left, LogicalTimestamp right) => left.CompareTo(right) >= 0;
}
namespace DramaBoard.Kernel.Time;

/// <summary>Represents a model-time duration as one-millisecond ticks so it shares ModelTime's fixed, calendar-free scale.</summary>
public readonly struct ModelDuration : IComparable<ModelDuration>, IEquatable<ModelDuration>
{
    private const long TicksPerSecond = 1_000;

    /// <summary>Initializes a model-time duration from a number of one-millisecond ticks.</summary>
    public ModelDuration(long ticks)
    {
        Ticks = ticks;
    }

    /// <summary>Gets the duration's number of one-millisecond ticks.</summary>
    public long Ticks { get; }

    /// <summary>Creates a duration from milliseconds.</summary>
    public static ModelDuration FromMilliseconds(long milliseconds) => new(milliseconds);

    /// <summary>Creates a duration from seconds and throws if conversion overflows.</summary>
    public static ModelDuration FromSeconds(long seconds) => new(checked(seconds * TicksPerSecond));

    /// <summary>Compares this duration with another duration.</summary>
    public int CompareTo(ModelDuration other) => Ticks.CompareTo(other.Ticks);

    /// <summary>Returns whether this duration has the same tick value as another duration.</summary>
    public bool Equals(ModelDuration other) => Ticks == other.Ticks;

    /// <summary>Returns whether this duration has the same tick value as an object.</summary>
    public override bool Equals(object? obj) => obj is ModelDuration other && Equals(other);

    /// <summary>Returns the hash code of this duration's tick value.</summary>
    public override int GetHashCode() => Ticks.GetHashCode();

    /// <summary>Returns whether two durations have the same tick value.</summary>
    public static bool operator ==(ModelDuration left, ModelDuration right) => left.Equals(right);

    /// <summary>Returns whether two durations have different tick values.</summary>
    public static bool operator !=(ModelDuration left, ModelDuration right) => !left.Equals(right);

    /// <summary>Returns whether the left duration is shorter than the right duration.</summary>
    public static bool operator <(ModelDuration left, ModelDuration right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether the left duration is shorter than or equal to the right duration.</summary>
    public static bool operator <=(ModelDuration left, ModelDuration right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether the left duration is longer than the right duration.</summary>
    public static bool operator >(ModelDuration left, ModelDuration right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether the left duration is longer than or equal to the right duration.</summary>
    public static bool operator >=(ModelDuration left, ModelDuration right) => left.CompareTo(right) >= 0;

    /// <summary>Adds two durations and throws if the tick value overflows.</summary>
    public static ModelDuration operator +(ModelDuration left, ModelDuration right) =>
        new(checked(left.Ticks + right.Ticks));

    /// <summary>Subtracts one duration from another and throws if the tick value overflows.</summary>
    public static ModelDuration operator -(ModelDuration left, ModelDuration right) =>
        new(checked(left.Ticks - right.Ticks));

    /// <summary>Negates a duration and throws if the tick value overflows.</summary>
    public static ModelDuration operator -(ModelDuration duration) => new(checked(-duration.Ticks));
}
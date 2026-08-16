using System.Globalization;

namespace DramaBoard.Kernel.Time;

/// <summary>Represents world logical time as one-millisecond ticks, allowing physical event timing without introducing calendar semantics.</summary>
public readonly struct ModelTime : IComparable<ModelTime>, IEquatable<ModelTime>
{
    private const long TicksPerSecond = 1_000;
    private const long TicksPerMinute = 60 * TicksPerSecond;
    private const long TicksPerHour = 60 * TicksPerMinute;
    private const long TicksPerDay = 24 * TicksPerHour;

    /// <summary>Initializes a logical time from a number of one-millisecond ticks.</summary>
    public ModelTime(long ticks)
    {
        Ticks = ticks;
    }

    /// <summary>Gets the number of one-millisecond ticks from the model epoch.</summary>
    public long Ticks { get; }

    /// <summary>Gets the model epoch.</summary>
    public static ModelTime Zero => new(0);

    /// <summary>Compares this logical time with another logical time.</summary>
    public int CompareTo(ModelTime other) => Ticks.CompareTo(other.Ticks);

    /// <summary>Returns whether this logical time has the same tick value as another logical time.</summary>
    public bool Equals(ModelTime other) => Ticks == other.Ticks;

    /// <summary>Returns whether this logical time has the same tick value as an object.</summary>
    public override bool Equals(object? obj) => obj is ModelTime other && Equals(other);

    /// <summary>Returns the hash code of this logical time's tick value.</summary>
    public override int GetHashCode() => Ticks.GetHashCode();

    /// <summary>Formats this logical time as a signed day and time-of-day offset from the model epoch.</summary>
    public override string ToString()
    {
        bool isNegative = Ticks < 0;
        ulong magnitude = isNegative ? unchecked((ulong)(-(Ticks + 1))) + 1 : (ulong)Ticks;
        ulong days = magnitude / TicksPerDay;
        ulong timeOfDay = magnitude % TicksPerDay;
        ulong hours = timeOfDay / TicksPerHour;
        ulong minutes = timeOfDay % TicksPerHour / TicksPerMinute;
        ulong seconds = timeOfDay % TicksPerMinute / TicksPerSecond;
        ulong milliseconds = timeOfDay % TicksPerSecond;
        string sign = isNegative ? "-" : string.Empty;

        return milliseconds == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}D{days} {hours:00}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}D{days} {hours:00}:{minutes:00}:{seconds:00}.{milliseconds:000}");
    }

    /// <summary>Returns whether two logical times have the same tick value.</summary>
    public static bool operator ==(ModelTime left, ModelTime right) => left.Equals(right);

    /// <summary>Returns whether two logical times have different tick values.</summary>
    public static bool operator !=(ModelTime left, ModelTime right) => !left.Equals(right);

    /// <summary>Returns whether the left logical time precedes the right logical time.</summary>
    public static bool operator <(ModelTime left, ModelTime right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether the left logical time precedes or equals the right logical time.</summary>
    public static bool operator <=(ModelTime left, ModelTime right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether the left logical time follows the right logical time.</summary>
    public static bool operator >(ModelTime left, ModelTime right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether the left logical time follows or equals the right logical time.</summary>
    public static bool operator >=(ModelTime left, ModelTime right) => left.CompareTo(right) >= 0;

    /// <summary>Adds a duration to a logical time and throws if the tick value overflows.</summary>
    public static ModelTime operator +(ModelTime time, ModelDuration duration) =>
        new(checked(time.Ticks + duration.Ticks));

    /// <summary>Adds a duration to a logical time and throws if the tick value overflows.</summary>
    public static ModelTime operator +(ModelDuration duration, ModelTime time) => time + duration;

    /// <summary>Subtracts a duration from a logical time and throws if the tick value overflows.</summary>
    public static ModelTime operator -(ModelTime time, ModelDuration duration) =>
        new(checked(time.Ticks - duration.Ticks));

    /// <summary>Returns the duration between two logical times and throws if the tick value overflows.</summary>
    public static ModelDuration operator -(ModelTime left, ModelTime right) =>
        new(checked(left.Ticks - right.Ticks));
}
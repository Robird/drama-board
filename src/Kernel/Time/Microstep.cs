namespace DramaBoard.Kernel.Time;

/// <summary>Represents deterministic causal order among events sharing one ModelTime value.</summary>
public readonly struct Microstep : IComparable<Microstep>, IEquatable<Microstep>
{
    /// <summary>Initializes a causal microstep from its integer order value.</summary>
    public Microstep(int value)
    {
        Value = value;
    }

    /// <summary>Gets the causal order value within a logical time.</summary>
    public int Value { get; }

    /// <summary>Compares this causal microstep with another causal microstep.</summary>
    public int CompareTo(Microstep other) => Value.CompareTo(other.Value);

    /// <summary>Returns whether this microstep has the same order value as another microstep.</summary>
    public bool Equals(Microstep other) => Value == other.Value;

    /// <summary>Returns whether this microstep has the same order value as an object.</summary>
    public override bool Equals(object? obj) => obj is Microstep other && Equals(other);

    /// <summary>Returns the hash code of this microstep's order value.</summary>
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Returns whether two microsteps have the same order value.</summary>
    public static bool operator ==(Microstep left, Microstep right) => left.Equals(right);

    /// <summary>Returns whether two microsteps have different order values.</summary>
    public static bool operator !=(Microstep left, Microstep right) => !left.Equals(right);

    /// <summary>Returns whether the left microstep precedes the right microstep.</summary>
    public static bool operator <(Microstep left, Microstep right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether the left microstep precedes or equals the right microstep.</summary>
    public static bool operator <=(Microstep left, Microstep right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether the left microstep follows the right microstep.</summary>
    public static bool operator >(Microstep left, Microstep right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether the left microstep follows or equals the right microstep.</summary>
    public static bool operator >=(Microstep left, Microstep right) => left.CompareTo(right) >= 0;
}
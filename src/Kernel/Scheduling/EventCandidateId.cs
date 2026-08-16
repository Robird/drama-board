namespace DramaBoard.Kernel.Scheduling;

/// <summary>Identifies a forecast candidate with a deterministic sequence number assigned by the queue owner.</summary>
public readonly struct EventCandidateId : IComparable<EventCandidateId>, IEquatable<EventCandidateId>
{
    /// <summary>Initializes a candidate identifier from its deterministic sequence number.</summary>
    public EventCandidateId(long value)
    {
        Value = value;
    }

    /// <summary>Gets the deterministic sequence number.</summary>
    public long Value { get; }

    /// <summary>Compares this identifier with another identifier.</summary>
    public int CompareTo(EventCandidateId other) => Value.CompareTo(other.Value);

    /// <summary>Returns whether this identifier has the same sequence number as another identifier.</summary>
    public bool Equals(EventCandidateId other) => Value == other.Value;

    /// <summary>Returns whether this identifier has the same sequence number as an object.</summary>
    public override bool Equals(object? obj) => obj is EventCandidateId other && Equals(other);

    /// <summary>Returns the hash code of the sequence number.</summary>
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Formats this identifier as its sequence number.</summary>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Returns whether two candidate identifiers have the same sequence number.</summary>
    public static bool operator ==(EventCandidateId left, EventCandidateId right) => left.Equals(right);

    /// <summary>Returns whether two candidate identifiers have different sequence numbers.</summary>
    public static bool operator !=(EventCandidateId left, EventCandidateId right) => !left.Equals(right);

    /// <summary>Returns whether the left candidate identifier precedes the right identifier.</summary>
    public static bool operator <(EventCandidateId left, EventCandidateId right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether the left candidate identifier precedes or equals the right identifier.</summary>
    public static bool operator <=(EventCandidateId left, EventCandidateId right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether the left candidate identifier follows the right identifier.</summary>
    public static bool operator >(EventCandidateId left, EventCandidateId right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether the left candidate identifier follows or equals the right identifier.</summary>
    public static bool operator >=(EventCandidateId left, EventCandidateId right) => left.CompareTo(right) >= 0;
}
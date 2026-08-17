namespace DramaBoard.Kernel.Journal;

/// <summary>
/// Identifies a stable, versioned domain event contract. Equality represents routing identity and
/// compares only <see cref="Id"/>; <see cref="Version"/> declares the payload schema and does not
/// participate in equality.
/// </summary>
public readonly struct EventKind : IEquatable<EventKind>
{
    /// <summary>Initializes an event kind from its stable identifier and schema version.</summary>
    public EventKind(string id, ushort version)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Event kind identifier cannot be empty.", nameof(id));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Event kind version must be at least 1.");
        }

        Id = id;
        Version = version;
    }

    /// <summary>Gets the stable, hand-authored event identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the event payload schema version.</summary>
    public ushort Version { get; }

    /// <inheritdoc />
    public bool Equals(EventKind other) =>
        string.Equals(Id, other.Id, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EventKind other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Id is null ? 0 : StringComparer.Ordinal.GetHashCode(Id);

    /// <summary>Determines whether two event kinds have the same routing identity.</summary>
    public static bool operator ==(EventKind left, EventKind right) => left.Equals(right);

    /// <summary>Determines whether two event kinds have different routing identities.</summary>
    public static bool operator !=(EventKind left, EventKind right) => !left.Equals(right);
}

namespace DramaBoard.Kernel.Journal;

/// <summary>Identifies a stable, versioned domain event contract.</summary>
public readonly record struct EventKind
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
}

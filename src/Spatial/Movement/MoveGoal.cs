namespace DramaBoard.Spatial;

/// <summary>Identifies a semantic or geometric movement destination.</summary>
public abstract record MoveGoal;

/// <summary>Targets one exact cell.</summary>
public sealed record CellGoal(CellRef Cell) : MoveGoal;

/// <summary>Targets the cell named by one anchor.</summary>
public sealed record AnchorGoal : MoveGoal
{
    /// <summary>Initializes an anchor goal.</summary>
    public AnchorGoal(AnchorId anchorId)
    {
        if (string.IsNullOrWhiteSpace(anchorId.Value))
        {
            throw new ArgumentException("Anchor identifier must be initialized.", nameof(anchorId));
        }

        AnchorId = anchorId;
    }

    /// <summary>Gets the target anchor.</summary>
    public AnchorId AnchorId { get; }
}

/// <summary>Targets any cell belonging to one zone.</summary>
public sealed record ZoneGoal : MoveGoal
{
    /// <summary>Initializes a zone goal.</summary>
    public ZoneGoal(ZoneId zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId.Value))
        {
            throw new ArgumentException("Zone identifier must be initialized.", nameof(zoneId));
        }

        ZoneId = zoneId;
    }

    /// <summary>Gets the target zone.</summary>
    public ZoneId ZoneId { get; }
}

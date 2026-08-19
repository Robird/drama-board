namespace DramaBoard.Spatial;

/// <summary>Overrides selected immutable properties of one cell.</summary>
public sealed record CellOverride
{
    /// <summary>Initializes a partial cell override.</summary>
    public CellOverride(
        bool? blocksMovement = null,
        bool? blocksSight = null,
        int? moveCost = null)
    {
        if (moveCost is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moveCost), "Overridden move cost must be positive.");
        }

        BlocksMovement = blocksMovement;
        BlocksSight = blocksSight;
        MoveCost = moveCost;
    }

    /// <summary>Gets an optional replacement for static movement blocking.</summary>
    public bool? BlocksMovement { get; }

    /// <summary>Gets an optional replacement for static sight blocking.</summary>
    public bool? BlocksSight { get; }

    /// <summary>Gets an optional positive replacement for static movement cost.</summary>
    public int? MoveCost { get; }

    /// <summary>Gets whether this value specifies no effective override.</summary>
    public bool IsEmpty => BlocksMovement is null && BlocksSight is null && MoveCost is null;
}

/// <summary>Stores an effective enabled-state override for one portal.</summary>
public sealed record PortalOverrideState
{
    /// <summary>Initializes one sparse portal override.</summary>
    public PortalOverrideState(PortalId portalId, bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(portalId.Value))
        {
            throw new ArgumentException("Portal identifier must be initialized.", nameof(portalId));
        }

        PortalId = portalId;
        IsEnabled = isEnabled;
    }

    /// <summary>Gets the overridden portal.</summary>
    public PortalId PortalId { get; }

    /// <summary>Gets the resulting effective enabled state.</summary>
    public bool IsEnabled { get; }
}

/// <summary>Stores a non-empty dynamic override for one cell.</summary>
public sealed record CellOverrideState
{
    /// <summary>Initializes a cell override state.</summary>
    public CellOverrideState(CellRef cell, CellOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsEmpty)
        {
            throw new ArgumentException("Persisted cell override cannot be empty.", nameof(value));
        }

        Cell = cell;
        Value = value;
    }

    /// <summary>Gets the overridden cell.</summary>
    public CellRef Cell { get; }

    /// <summary>Gets the non-empty override value.</summary>
    public CellOverride Value { get; }
}

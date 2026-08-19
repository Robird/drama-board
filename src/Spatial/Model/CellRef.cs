namespace DramaBoard.Spatial;

/// <summary>Identifies one cell in one finite grid map.</summary>
public readonly record struct CellRef : IComparable<CellRef>
{
    /// <summary>Initializes a cell reference from a map and non-negative coordinates.</summary>
    public CellRef(MapId mapId, int x, int y)
    {
        if (string.IsNullOrWhiteSpace(mapId.Value))
        {
            throw new ArgumentException("Map identifier must be initialized.", nameof(mapId));
        }

        if (x < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Cell X coordinate cannot be negative.");
        }

        if (y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(y), "Cell Y coordinate cannot be negative.");
        }

        MapId = mapId;
        X = x;
        Y = y;
    }

    /// <summary>Gets the containing map identifier.</summary>
    public MapId MapId { get; }

    /// <summary>Gets the zero-based horizontal coordinate.</summary>
    public int X { get; }

    /// <summary>Gets the zero-based vertical coordinate.</summary>
    public int Y { get; }

    /// <inheritdoc />
    public int CompareTo(CellRef other)
    {
        int mapComparison = MapId.CompareTo(other.MapId);
        if (mapComparison != 0)
        {
            return mapComparison;
        }

        int yComparison = Y.CompareTo(other.Y);
        return yComparison != 0 ? yComparison : X.CompareTo(other.X);
    }

    /// <inheritdoc />
    public override string ToString() => $"{MapId}/({X}, {Y})";
}

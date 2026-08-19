using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Defines one stable directed non-grid edge.</summary>
public sealed record PortalDefinition
{
    /// <summary>Initializes a directed portal with a positive traversal duration.</summary>
    public PortalDefinition(
        PortalId id,
        CellRef from,
        CellRef to,
        ModelDuration traversalDuration,
        bool initiallyEnabled)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Portal identifier must be initialized.", nameof(id));
        }

        if (traversalDuration.Ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(traversalDuration),
                "Portal traversal duration must be positive.");
        }

        Id = id;
        From = from;
        To = to;
        TraversalDuration = traversalDuration;
        InitiallyEnabled = initiallyEnabled;
    }

    /// <summary>Gets the stable portal identifier.</summary>
    public PortalId Id { get; }

    /// <summary>Gets the directed source cell.</summary>
    public CellRef From { get; }

    /// <summary>Gets the directed destination cell.</summary>
    public CellRef To { get; }

    /// <summary>Gets the positive traversal duration.</summary>
    public ModelDuration TraversalDuration { get; }

    /// <summary>Gets whether the portal starts enabled.</summary>
    public bool InitiallyEnabled { get; }
}

/// <summary>Defines a stable semantic name for one cell.</summary>
public sealed record AnchorDefinition
{
    /// <summary>Initializes an anchor at one cell.</summary>
    public AnchorDefinition(AnchorId id, CellRef cell)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Anchor identifier must be initialized.", nameof(id));
        }

        Id = id;
        Cell = cell;
    }

    /// <summary>Gets the stable anchor identifier.</summary>
    public AnchorId Id { get; }

    /// <summary>Gets the anchor cell.</summary>
    public CellRef Cell { get; }
}

/// <summary>Defines a stable semantic set of cells.</summary>
public sealed class ZoneDefinition
{
    /// <summary>Initializes a non-empty zone and clones its cells in canonical order.</summary>
    public ZoneDefinition(ZoneId id, IEnumerable<CellRef> cells)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Zone identifier must be initialized.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(cells);
        CellRef[] canonicalCells = [.. cells.Order()];
        if (canonicalCells.Length == 0)
        {
            throw new ArgumentException("Zone must contain at least one cell.", nameof(cells));
        }

        if (canonicalCells.Distinct().Count() != canonicalCells.Length)
        {
            throw new ArgumentException("Zone cannot contain duplicate cells.", nameof(cells));
        }

        Id = id;
        Cells = Array.AsReadOnly(canonicalCells);
    }

    /// <summary>Gets the stable zone identifier.</summary>
    public ZoneId Id { get; }

    /// <summary>Gets immutable cells ordered by map, Y, then X.</summary>
    public IReadOnlyList<CellRef> Cells { get; }
}

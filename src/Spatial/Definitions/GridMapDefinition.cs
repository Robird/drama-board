using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Defines one finite row-major four-direction grid map.</summary>
public sealed class GridMapDefinition
{
    /// <summary>Initializes a finite grid and clones its row-major cells.</summary>
    public GridMapDefinition(
        MapId id,
        int width,
        int height,
        ModelDuration orthogonalStepDuration,
        int visionRange,
        IReadOnlyList<CellDefinition> rowMajorCells)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Map identifier must be initialized.", nameof(id));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Map width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Map height must be positive.");
        }

        if (orthogonalStepDuration.Ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orthogonalStepDuration),
                "Orthogonal step duration must be positive.");
        }

        if (visionRange < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visionRange), "Vision range cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(rowMajorCells);
        int expectedCellCount = checked(width * height);
        if (rowMajorCells.Count != expectedCellCount)
        {
            throw new ArgumentException(
                $"Map requires exactly {expectedCellCount} row-major cells.",
                nameof(rowMajorCells));
        }

        CellDefinition[] cells = [.. rowMajorCells];
        if (cells.Any(cell => cell is null))
        {
            throw new ArgumentException("Map cells cannot contain null entries.", nameof(rowMajorCells));
        }

        foreach (CellDefinition cell in cells)
        {
            _ = checked(orthogonalStepDuration.Ticks * cell.MoveCost);
        }

        Id = id;
        Width = width;
        Height = height;
        OrthogonalStepDuration = orthogonalStepDuration;
        VisionRange = visionRange;
        Cells = Array.AsReadOnly(cells);
    }

    /// <summary>Gets the stable map identifier.</summary>
    public MapId Id { get; }

    /// <summary>Gets the positive map width.</summary>
    public int Width { get; }

    /// <summary>Gets the positive map height.</summary>
    public int Height { get; }

    /// <summary>Gets the base duration of entering an orthogonally adjacent cell.</summary>
    public ModelDuration OrthogonalStepDuration { get; }

    /// <summary>Gets the non-negative Manhattan vision range.</summary>
    public int VisionRange { get; }

    /// <summary>Gets an immutable row-major cell sequence.</summary>
    public IReadOnlyList<CellDefinition> Cells { get; }

    /// <summary>Gets the cell at a valid local coordinate.</summary>
    public CellDefinition GetCell(int x, int y)
    {
        if (x < 0 || x >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        int index = checked((y * Width) + x);
        return Cells[index];
    }
}

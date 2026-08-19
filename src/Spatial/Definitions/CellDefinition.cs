namespace DramaBoard.Spatial;

/// <summary>Defines immutable terrain properties for one grid cell.</summary>
public sealed record CellDefinition
{
    /// <summary>Initializes immutable terrain properties.</summary>
    public CellDefinition(
        TerrainId terrainId,
        int moveCost,
        bool blocksMovement,
        bool blocksSight)
    {
        if (string.IsNullOrWhiteSpace(terrainId.Value))
        {
            throw new ArgumentException("Terrain identifier must be initialized.", nameof(terrainId));
        }

        if (moveCost <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moveCost), "Cell move cost must be positive.");
        }

        TerrainId = terrainId;
        MoveCost = moveCost;
        BlocksMovement = blocksMovement;
        BlocksSight = blocksSight;
    }

    /// <summary>Gets the terrain contract identifier.</summary>
    public TerrainId TerrainId { get; }

    /// <summary>Gets the positive integer movement-cost multiplier.</summary>
    public int MoveCost { get; }

    /// <summary>Gets whether the cell prevents entry.</summary>
    public bool BlocksMovement { get; }

    /// <summary>Gets whether the cell blocks line of sight beyond itself.</summary>
    public bool BlocksSight { get; }
}

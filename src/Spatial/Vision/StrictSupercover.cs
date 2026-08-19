namespace DramaBoard.Spatial;

/// <summary>Enumerates every grid cell touched by a closed center-to-center line segment.</summary>
internal static class StrictSupercover
{
    /// <summary>
    /// Returns touched cells from source to target. At an exact corner crossing, both side cells
    /// precede the diagonal cell. All comparisons use integer arithmetic.
    /// </summary>
    internal static IReadOnlyList<CellRef> GetTouchedCells(CellRef source, CellRef target)
    {
        if (source.MapId != target.MapId)
        {
            throw new ArgumentException("Strict supercover endpoints must belong to the same map.", nameof(target));
        }

        long deltaX = Math.Abs((long)target.X - source.X);
        long deltaY = Math.Abs((long)target.Y - source.Y);
        int stepX = Math.Sign(target.X - source.X);
        int stepY = Math.Sign(target.Y - source.Y);
        long crossedX = 0;
        long crossedY = 0;
        int x = source.X;
        int y = source.Y;
        var touched = new List<CellRef> { source };

        while (crossedX < deltaX || crossedY < deltaY)
        {
            Int128 nextVertical = (((Int128)crossedX * 2) + 1) * deltaY;
            Int128 nextHorizontal = (((Int128)crossedY * 2) + 1) * deltaX;

            if (nextVertical == nextHorizontal)
            {
                touched.Add(new CellRef(source.MapId, checked(x + stepX), y));
                touched.Add(new CellRef(source.MapId, x, checked(y + stepY)));
                x = checked(x + stepX);
                y = checked(y + stepY);
                crossedX++;
                crossedY++;
                touched.Add(new CellRef(source.MapId, x, y));
            }
            else if (nextVertical < nextHorizontal)
            {
                x = checked(x + stepX);
                crossedX++;
                touched.Add(new CellRef(source.MapId, x, y));
            }
            else
            {
                y = checked(y + stepY);
                crossedY++;
                touched.Add(new CellRef(source.MapId, x, y));
            }
        }

        return Array.AsReadOnly(touched.ToArray());
    }

    /// <summary>
    /// Tests intermediate touched cells. Source and target never block their own segment, so an
    /// opaque target remains visible while any opaque corner-side cell blocks it.
    /// </summary>
    internal static bool HasLineOfSight(
        CellRef source,
        CellRef target,
        Func<CellRef, bool> blocksSight)
    {
        ArgumentNullException.ThrowIfNull(blocksSight);
        if (source.MapId != target.MapId)
        {
            return false;
        }

        foreach (CellRef cell in GetTouchedCells(source, target))
        {
            if (cell != source && cell != target && blocksSight(cell))
            {
                return false;
            }
        }

        return true;
    }
}

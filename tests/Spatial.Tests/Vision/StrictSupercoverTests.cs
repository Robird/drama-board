namespace DramaBoard.Spatial.Tests;

public sealed class StrictSupercoverTests
{
    private static readonly MapId Map = new("vision-grid");

    [Fact]
    public void GetTouchedCells_ExactVectorsIncludeBothCornerSides()
    {
        TouchedVector[] vectors =
        [
            new((1, 1), (1, 1), [(1, 1)]),
            new((0, 0), (3, 0), [(0, 0), (1, 0), (2, 0), (3, 0)]),
            new((3, 0), (0, 0), [(3, 0), (2, 0), (1, 0), (0, 0)]),
            new((1, 0), (1, 3), [(1, 0), (1, 1), (1, 2), (1, 3)]),
            new((1, 3), (1, 0), [(1, 3), (1, 2), (1, 1), (1, 0)]),
            new((0, 0), (1, 1), [(0, 0), (1, 0), (0, 1), (1, 1)]),
            new((1, 1), (0, 0), [(1, 1), (0, 1), (1, 0), (0, 0)]),
            new((0, 0), (2, 2),
                [(0, 0), (1, 0), (0, 1), (1, 1), (2, 1), (1, 2), (2, 2)]),
            new((0, 0), (2, 1), [(0, 0), (1, 0), (1, 1), (2, 1)]),
            new((0, 0), (1, 2), [(0, 0), (0, 1), (1, 1), (1, 2)]),
            new((0, 0), (3, 1), [(0, 0), (1, 0), (2, 0), (1, 1), (2, 1), (3, 1)]),
            new((3, 1), (0, 0), [(3, 1), (2, 1), (1, 1), (2, 0), (1, 0), (0, 0)]),
            new((0, 0), (3, 2), [(0, 0), (1, 0), (1, 1), (2, 1), (2, 2), (3, 2)]),
            new((3, 2), (0, 0), [(3, 2), (2, 2), (2, 1), (1, 1), (1, 0), (0, 0)]),
            new((0, 0), (4, 1), [(0, 0), (1, 0), (2, 0), (2, 1), (3, 1), (4, 1)]),
            new((4, 1), (0, 0), [(4, 1), (3, 1), (2, 1), (2, 0), (1, 0), (0, 0)]),
            new((0, 2), (3, 0), [(0, 2), (1, 2), (1, 1), (2, 1), (2, 0), (3, 0)]),
            new((3, 0), (0, 2), [(3, 0), (2, 0), (2, 1), (1, 1), (1, 2), (0, 2)]),
        ];

        foreach (TouchedVector vector in vectors)
        {
            IReadOnlyList<CellRef> actual = StrictSupercover.GetTouchedCells(
                Cell(vector.Source.X, vector.Source.Y),
                Cell(vector.Target.X, vector.Target.Y));

            Assert.Equal(vector.Expected.Select(point => Cell(point.X, point.Y)), actual);
        }
    }

    [Fact]
    public void GetTouchedCells_ReturnsDefensiveReadOnlyCollection()
    {
        IReadOnlyList<CellRef> touched = StrictSupercover.GetTouchedCells(Cell(0, 0), Cell(2, 1));
        var collection = Assert.IsAssignableFrom<ICollection<CellRef>>(touched);

        Assert.Throws<NotSupportedException>(() => collection.Add(Cell(0, 1)));
    }

    [Fact]
    public void HasLineOfSight_ExactBlockingVectorsRespectEndpointsAndCornerTouches()
    {
        VisibilityVector[] vectors =
        [
            new((1, 1), (1, 1), [(1, 1)], true),
            new((0, 0), (3, 0), [], true),
            new((0, 0), (3, 0), [(1, 0)], false),
            new((0, 0), (3, 0), [(3, 0)], true),
            new((0, 0), (1, 1), [(1, 0)], false),
            new((0, 0), (1, 1), [(0, 1)], false),
            new((0, 0), (1, 1), [(1, 1)], true),
            new((0, 0), (2, 2), [(1, 1)], false),
            new((0, 0), (2, 2), [(2, 1)], false),
            new((0, 0), (3, 1), [(2, 0)], false),
            new((0, 0), (3, 1), [(1, 1)], false),
            new((0, 0), (3, 1), [(0, 1)], true),
            new((0, 0), (3, 2), [(2, 2)], false),
            new((3, 2), (0, 0), [(1, 0)], false),
            new((0, 0), (4, 1), [(2, 1)], false),
            new((0, 2), (3, 0), [(1, 1)], false),
        ];

        foreach (VisibilityVector vector in vectors)
        {
            var walls = vector.Walls.ToHashSet();
            bool actual = StrictSupercover.HasLineOfSight(
                Cell(vector.Source.X, vector.Source.Y),
                Cell(vector.Target.X, vector.Target.Y),
                cell => walls.Contains((cell.X, cell.Y)));

            Assert.Equal(vector.Expected, actual);
        }
    }

    [Fact]
    public void ThreeByThreeExhaustion_IsSymmetricAndEquivariantUnderD4()
    {
        Func<(int X, int Y), (int X, int Y)>[] transforms = D4Transforms();
        (int X, int Y)[] points =
        [
            .. Enumerable.Range(0, 3)
                .SelectMany(y => Enumerable.Range(0, 3).Select(x => (X: x, Y: y))),
        ];

        foreach ((int X, int Y) sourcePoint in points)
        {
            foreach ((int X, int Y) targetPoint in points)
            {
                CellRef source = Cell(sourcePoint.X, sourcePoint.Y);
                CellRef target = Cell(targetPoint.X, targetPoint.Y);
                HashSet<CellRef> touched = StrictSupercover.GetTouchedCells(source, target).ToHashSet();
                Assert.True(touched.SetEquals(StrictSupercover.GetTouchedCells(target, source)));

                foreach (Func<(int X, int Y), (int X, int Y)> transform in transforms)
                {
                    (int X, int Y) transformedSource = transform(sourcePoint);
                    (int X, int Y) transformedTarget = transform(targetPoint);
                    var expected = touched
                        .Select(cell => transform((cell.X, cell.Y)))
                        .Select(point => Cell(point.X, point.Y))
                        .ToHashSet();
                    HashSet<CellRef> actual = StrictSupercover.GetTouchedCells(
                        Cell(transformedSource.X, transformedSource.Y),
                        Cell(transformedTarget.X, transformedTarget.Y)).ToHashSet();
                    Assert.True(expected.SetEquals(actual));
                }
            }
        }

        for (int wallMask = 0; wallMask < (1 << 9); wallMask++)
        {
            foreach ((int X, int Y) sourcePoint in points)
            {
                foreach ((int X, int Y) targetPoint in points)
                {
                    bool expected = HasLineOfSight(wallMask, sourcePoint, targetPoint);
                    bool reverse = HasLineOfSight(wallMask, targetPoint, sourcePoint);
                    Assert.Equal(expected, reverse);

                    foreach (Func<(int X, int Y), (int X, int Y)> transform in transforms)
                    {
                        int transformedMask = TransformMask(wallMask, transform);
                        Assert.Equal(
                            expected,
                            HasLineOfSight(
                                transformedMask,
                                transform(sourcePoint),
                                transform(targetPoint)));
                    }
                }
            }
        }
    }

    [Fact]
    public void DifferentMaps_DoNotHaveLineOfSightOrTouchedCellEnumeration()
    {
        CellRef source = Cell(0, 0);
        var target = new CellRef(new MapId("other"), 0, 0);

        Assert.False(StrictSupercover.HasLineOfSight(source, target, _ => false));
        Assert.Throws<ArgumentException>(() => StrictSupercover.GetTouchedCells(source, target));
    }

    private static bool HasLineOfSight(
        int wallMask,
        (int X, int Y) source,
        (int X, int Y) target) =>
        StrictSupercover.HasLineOfSight(
            Cell(source.X, source.Y),
            Cell(target.X, target.Y),
            cell => (wallMask & (1 << ((cell.Y * 3) + cell.X))) != 0);

    private static int TransformMask(
        int mask,
        Func<(int X, int Y), (int X, int Y)> transform)
    {
        int result = 0;
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                if ((mask & (1 << ((y * 3) + x))) == 0)
                {
                    continue;
                }

                (int X, int Y) transformed = transform((x, y));
                result |= 1 << ((transformed.Y * 3) + transformed.X);
            }
        }

        return result;
    }

    private static Func<(int X, int Y), (int X, int Y)>[] D4Transforms() =>
    [
        point => point,
        point => (2 - point.Y, point.X),
        point => (2 - point.X, 2 - point.Y),
        point => (point.Y, 2 - point.X),
        point => (2 - point.X, point.Y),
        point => (2 - point.Y, 2 - point.X),
        point => (point.X, 2 - point.Y),
        point => (point.Y, point.X),
    ];

    private static CellRef Cell(int x, int y) => new(Map, x, y);

    private sealed record TouchedVector(
        (int X, int Y) Source,
        (int X, int Y) Target,
        IReadOnlyList<(int X, int Y)> Expected);

    private sealed record VisibilityVector(
        (int X, int Y) Source,
        (int X, int Y) Target,
        IReadOnlyList<(int X, int Y)> Walls,
        bool Expected);
}

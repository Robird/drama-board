namespace DramaBoard.Spatial.Tests;

public sealed class IdentifierAndCellRefTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("map\nname")]
    public void ContentIdentifier_InvalidValue_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => new MapId(value));
    }

    [Fact]
    public void ContentIdentifier_TooLong_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ZoneId(new string('z', 257)));
    }

    [Fact]
    public void ContentIdentifier_IllFormedUtf16_Throws()
    {
        string[] values =
        [
            new(['\ud800']),
            new(['\udc00']),
            new(['a', '\ud800', 'b']),
        ];

        foreach (string value in values)
        {
            Assert.Throws<ArgumentException>(() => new MapId(value));
        }
    }

    [Fact]
    public void ContentIdentifier_ValidSurrogatePair_IsAccepted()
    {
        const string value = "map-\ud83d\uddfa";

        Assert.Equal(value, new MapId(value).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RuntimeIdentifier_NonPositive_Throws(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntityId(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JourneyId(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduledMutationId(value));
    }

    [Fact]
    public void StableIdentifiers_UseOrdinalAndNumericTotalOrder()
    {
        Assert.Equal(
            ["A", "a", "b"],
            new[] { new MapId("b"), new MapId("a"), new MapId("A") }
                .Order()
                .Select(id => id.Value));
        Assert.Equal(
            [1L, 2L, 10L],
            new[] { new EntityId(10), new EntityId(1), new EntityId(2) }
                .Order()
                .Select(id => id.Value));
    }

    [Fact]
    public void CellRef_NaturalOrder_IsMapThenYThenX()
    {
        CellRef[] cells =
        [
            TestSpatialDefinitionBuilder.Cell("b", 0, 0),
            TestSpatialDefinitionBuilder.Cell("a", 1, 0),
            TestSpatialDefinitionBuilder.Cell("a", 0, 1),
            TestSpatialDefinitionBuilder.Cell("a", 0, 0),
        ];

        Assert.Equal(
            ["a/(0, 0)", "a/(1, 0)", "a/(0, 1)", "b/(0, 0)"],
            cells.Order().Select(cell => cell.ToString()));
    }

    [Fact]
    public void CellRef_NegativeCoordinate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestSpatialDefinitionBuilder.Cell("a", -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestSpatialDefinitionBuilder.Cell("a", 0, -1));
    }
}

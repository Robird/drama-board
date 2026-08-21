using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Time;

public sealed class LogicalInstantTests
{
    [Fact]
    public void Constructor_NegativeCausalOrdinal_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LogicalInstant(new ModelTime(10), causalOrdinal: -1));
    }

    [Fact]
    public void Equality_SameComponents_UsesValueSemantics()
    {
        var first = new LogicalInstant(new ModelTime(10), causalOrdinal: 2);
        var equal = new LogicalInstant(new ModelTime(10), causalOrdinal: 2);
        var different = new LogicalInstant(new ModelTime(10), causalOrdinal: 3);

        Assert.Equal(first, equal);
        Assert.NotEqual(first, different);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
    }

    [Fact]
    public void CompareTo_OrdersByModelTimeThenCausalOrdinal()
    {
        var first = new LogicalInstant(new ModelTime(10), causalOrdinal: 2);
        var second = new LogicalInstant(new ModelTime(10), causalOrdinal: 3);
        var third = new LogicalInstant(new ModelTime(11), causalOrdinal: 0);

        Assert.True(first < second);
        Assert.True(second < third);
        Assert.True(first < third);
    }
}

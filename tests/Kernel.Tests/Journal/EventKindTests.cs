using DramaBoard.Kernel.Journal;

namespace DramaBoard.Kernel.Tests.Journal;

public sealed class EventKindTests
{
    [Fact]
    public void Constructor_EqualValues_UseValueSemantics()
    {
        var first = new EventKind("timer.fired", 1);
        var second = new EventKind("timer.fired", 1);

        Assert.Equal(first, second);
        Assert.Equal("timer.fired", first.Id);
        Assert.Equal((ushort)1, first.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyId_ThrowsArgumentException(string id)
    {
        Assert.Throws<ArgumentException>(() => new EventKind(id, 1));
    }

    [Fact]
    public void Constructor_ZeroVersion_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventKind("timer.fired", 0));
    }
}

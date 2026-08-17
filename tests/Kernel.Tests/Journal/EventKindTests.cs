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
        Assert.True(first == second);
        Assert.Equal("timer.fired", first.Id);
        Assert.Equal((ushort)1, first.Version);
    }

    [Fact]
    public void Equality_SameIdAcrossVersions_UsesRoutingIdentity()
    {
        var versionOne = new EventKind("timer.fired", 1);
        var versionTwo = new EventKind("timer.fired", 2);

        Assert.Equal(versionOne, versionTwo);
        Assert.True(versionOne == versionTwo);
        Assert.Equal(versionOne.GetHashCode(), versionTwo.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentIds_AreNotEqual()
    {
        var fired = new EventKind("timer.fired", 1);
        var cancelled = new EventKind("timer.cancelled", 1);

        Assert.NotEqual(fired, cancelled);
        Assert.True(fired != cancelled);
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

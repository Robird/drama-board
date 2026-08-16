using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Time;

public sealed class ValueEqualityTests
{
    [Fact]
    public void Equals_SameTicks_UsesModelTimeValueSemantics()
    {
        var left = new ModelTime(10);
        var right = new ModelTime(10);
        var distinct = new ModelTime(11);

        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.True(left != distinct);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Equals_SameTicks_UsesModelDurationValueSemantics()
    {
        var left = new ModelDuration(10);
        var right = new ModelDuration(10);
        var distinct = new ModelDuration(11);

        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.True(left != distinct);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Equals_SameValue_UsesMicrostepValueSemantics()
    {
        var left = new Microstep(2);
        var right = new Microstep(2);
        var distinct = new Microstep(3);

        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.True(left != distinct);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Equals_SameComponents_UsesLogicalTimestampValueSemantics()
    {
        var left = new LogicalTimestamp(new ModelTime(10), new Microstep(2));
        var right = new LogicalTimestamp(new ModelTime(10), new Microstep(2));
        var distinct = new LogicalTimestamp(new ModelTime(10), new Microstep(3));

        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.True(left != distinct);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}

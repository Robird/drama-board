using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Time;

public sealed class LogicalTimestampTests
{
    [Fact]
    public void CompareTo_SameTimestamp_ReturnsZero()
    {
        var timestamp = new LogicalTimestamp(new ModelTime(10), new Microstep(2));

        Assert.Equal(0, timestamp.CompareTo(timestamp));
    }

    [Fact]
    public void CompareTo_ReversedDistinctTimestamps_IsAntisymmetric()
    {
        var earlier = new LogicalTimestamp(new ModelTime(10), new Microstep(2));
        var later = new LogicalTimestamp(new ModelTime(10), new Microstep(3));

        int forward = Math.Sign(earlier.CompareTo(later));
        int reverse = Math.Sign(later.CompareTo(earlier));

        Assert.NotEqual(0, forward);
        Assert.Equal(-forward, reverse);
    }

    [Fact]
    public void CompareTo_IncreasingTimestamps_IsTransitive()
    {
        var first = new LogicalTimestamp(new ModelTime(10), new Microstep(2));
        var second = new LogicalTimestamp(new ModelTime(10), new Microstep(3));
        var third = new LogicalTimestamp(new ModelTime(11), new Microstep(0));

        Assert.True(first < second);
        Assert.True(second < third);
        Assert.True(first < third);
    }

    [Fact]
    public void CompareTo_EqualAndDistinctComponents_AgreesWithEquality()
    {
        var timestamp = new LogicalTimestamp(new ModelTime(10), new Microstep(2));
        var equal = new LogicalTimestamp(new ModelTime(10), new Microstep(2));
        var distinct = new LogicalTimestamp(new ModelTime(10), new Microstep(3));

        Assert.Equal(timestamp == equal, timestamp.CompareTo(equal) == 0);
        Assert.Equal(timestamp == distinct, timestamp.CompareTo(distinct) == 0);
    }

    [Fact]
    public void Sort_SameModelTimeDifferentMicrosteps_OrdersByMicrostep()
    {
        var modelTime = new ModelTime(10);
        var first = new LogicalTimestamp(modelTime, new Microstep(0));
        var second = new LogicalTimestamp(modelTime, new Microstep(1));
        var third = new LogicalTimestamp(modelTime, new Microstep(2));
        LogicalTimestamp[] timestamps = [third, first, second];

        Array.Sort(timestamps);

        Assert.Equal([first, second, third], timestamps);
    }

    [Fact]
    public void CompareTo_DifferentModelTimes_OrdersByModelTimeFirst()
    {
        var earlier = new LogicalTimestamp(new ModelTime(10), new Microstep(int.MaxValue));
        var later = new LogicalTimestamp(new ModelTime(11), new Microstep(int.MinValue));

        Assert.True(earlier < later);
    }
}

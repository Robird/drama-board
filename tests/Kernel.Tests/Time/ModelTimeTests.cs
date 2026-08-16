using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Time;

public sealed class ModelTimeTests
{
    [Fact]
    public void Add_DurationWithinRange_ReturnsShiftedModelTime()
    {
        var modelTime = new ModelTime(100);
        var duration = new ModelDuration(25);

        ModelTime result = modelTime + duration;

        Assert.Equal(new ModelTime(125), result);
    }

    [Fact]
    public void Subtract_DurationWithinRange_ReturnsShiftedModelTime()
    {
        var modelTime = new ModelTime(100);
        var duration = new ModelDuration(25);

        ModelTime result = modelTime - duration;

        Assert.Equal(new ModelTime(75), result);
    }

    [Fact]
    public void Subtract_TwoModelTimes_ReturnsDurationBetweenThem()
    {
        var later = new ModelTime(100);
        var earlier = new ModelTime(25);

        ModelDuration result = later - earlier;

        Assert.Equal(new ModelDuration(75), result);
    }

    [Fact]
    public void Add_MaximumTickAndPositiveDuration_ThrowsOverflowException()
    {
        var maximum = new ModelTime(long.MaxValue);
        var duration = new ModelDuration(1);

        Assert.Throws<OverflowException>(() =>
        {
            _ = maximum + duration;
        });
    }

    [Fact]
    public void Subtract_MinimumTickAndPositiveDuration_ThrowsOverflowException()
    {
        var minimum = new ModelTime(long.MinValue);
        var duration = new ModelDuration(1);

        Assert.Throws<OverflowException>(() =>
        {
            _ = minimum - duration;
        });
    }

    [Fact]
    public void ToString_DayAndTimeOffset_UsesFixedFormat()
    {
        var modelTime = new ModelTime(((3 * 86_400L) + (14 * 3_600L) + (13 * 60L)) * 1_000);

        Assert.Equal("D3 14:13:00", modelTime.ToString());
    }

    [Fact]
    public void ToString_NegativeOffset_UsesLeadingSign()
    {
        var modelTime = new ModelTime(-((1 * 86_400L) + (2 * 3_600L) + (3 * 60L) + 4) * 1_000);

        Assert.Equal("-D1 02:03:04", modelTime.ToString());
    }

    [Fact]
    public void ToString_SubsecondOffset_AppendsMilliseconds()
    {
        var modelTime = new ModelTime(1_234);

        Assert.Equal("D0 00:00:01.234", modelTime.ToString());
    }
}
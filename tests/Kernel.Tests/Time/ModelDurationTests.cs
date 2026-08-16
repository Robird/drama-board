using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Time;

public sealed class ModelDurationTests
{
    [Fact]
    public void FromMilliseconds_Value_ReturnsMillisecondTicks()
    {
        Assert.Equal(new ModelDuration(250), ModelDuration.FromMilliseconds(250));
    }

    [Fact]
    public void FromSeconds_Value_ReturnsMillisecondTicks()
    {
        Assert.Equal(new ModelDuration(2_000), ModelDuration.FromSeconds(2));
    }

    [Fact]
    public void Add_DurationsWithinRange_ReturnsSum()
    {
        var left = new ModelDuration(20);
        var right = new ModelDuration(5);

        Assert.Equal(new ModelDuration(25), left + right);
    }

    [Fact]
    public void Subtract_DurationsWithinRange_ReturnsDifference()
    {
        var left = new ModelDuration(20);
        var right = new ModelDuration(5);

        Assert.Equal(new ModelDuration(15), left - right);
    }

    [Fact]
    public void Add_MaximumTickAndPositiveDuration_ThrowsOverflowException()
    {
        var maximum = new ModelDuration(long.MaxValue);
        var one = new ModelDuration(1);

        Assert.Throws<OverflowException>(() =>
        {
            _ = maximum + one;
        });
    }

    [Fact]
    public void Subtract_MinimumTickAndPositiveDuration_ThrowsOverflowException()
    {
        var minimum = new ModelDuration(long.MinValue);
        var one = new ModelDuration(1);

        Assert.Throws<OverflowException>(() =>
        {
            _ = minimum - one;
        });
    }

    [Fact]
    public void Negate_MinimumTick_ThrowsOverflowException()
    {
        var minimum = new ModelDuration(long.MinValue);

        Assert.Throws<OverflowException>(() =>
        {
            _ = -minimum;
        });
    }
}
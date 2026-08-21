using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Scheduling;

public sealed class CandidateDueTests
{
    [Fact]
    public void FromExactMilliseconds_IntegerValuesRemainUnchanged()
    {
        Assert.Equal(new ModelTime(0), CandidateDue.FromExactMilliseconds(0m).ModelTime);
        Assert.Equal(new ModelTime(17), CandidateDue.FromExactMilliseconds(17m).ModelTime);
        Assert.Equal(new ModelTime(-17), CandidateDue.FromExactMilliseconds(-17m).ModelTime);
    }

    [Fact]
    public void FromExactMilliseconds_AnyPositiveFractionRoundsUpToNextTick()
    {
        Assert.Equal(new ModelTime(11), CandidateDue.FromExactMilliseconds(10.0000001m).ModelTime);
        Assert.Equal(new ModelTime(11), CandidateDue.FromExactMilliseconds(10.9999999m).ModelTime);
        Assert.Equal(new ModelTime(0), CandidateDue.FromExactMilliseconds(-0.0000001m).ModelTime);
        Assert.Equal(new ModelTime(-10), CandidateDue.FromExactMilliseconds(-10.9999999m).ModelTime);
    }

    [Fact]
    public void FromExactMilliseconds_LongBoundariesAreAccepted()
    {
        Assert.Equal(
            new ModelTime(long.MaxValue),
            CandidateDue.FromExactMilliseconds(long.MaxValue).ModelTime);
        Assert.Equal(
            new ModelTime(long.MinValue),
            CandidateDue.FromExactMilliseconds(long.MinValue).ModelTime);
    }

    [Fact]
    public void FromExactMilliseconds_QuantizedValueOutsideLongRange_ThrowsOverflowException()
    {
        Assert.Throws<OverflowException>(() =>
            CandidateDue.FromExactMilliseconds((decimal)long.MaxValue + 0.1m));
        Assert.Throws<OverflowException>(() =>
            CandidateDue.FromExactMilliseconds((decimal)long.MinValue - 1m));
    }

    [Fact]
    public void CompareTo_UsesQuantizedModelTime()
    {
        var earlier = new CandidateDue(new ModelTime(10));
        var equal = new CandidateDue(new ModelTime(10));
        var later = new CandidateDue(new ModelTime(11));

        Assert.Equal(earlier, equal);
        Assert.True(earlier < later);
        Assert.True(later > earlier);
    }
}

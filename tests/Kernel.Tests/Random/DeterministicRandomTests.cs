using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Random;

public sealed class DeterministicRandomTests
{
    [Fact]
    public void SampleUInt64_SameCoordinatesAroundExtraCall_ReturnsSameValue()
    {
        ulong first = DeterministicRandom.SampleUInt64(42, 73, 4, 2);

        _ = DeterministicRandom.SampleUInt64(42, 73, 4, 999);
        ulong second = DeterministicRandom.SampleUInt64(42, 73, 4, 2);

        Assert.Equal(first, second);
        Assert.NotEqual(first, DeterministicRandom.SampleUInt64(42, 73, 4, 3));
    }

    [Fact]
    public void SampleUInt64_KnownCoordinates_ReturnsStableBitPattern()
    {
        ulong sample = DeterministicRandom.SampleUInt64(42, 73, 4, 2);

        Assert.Equal(1_468_166_576_533_988_118UL, sample);
    }

    [Fact]
    public void SampleUnitDouble_KnownCoordinates_ReturnsStableBitPatternInRange()
    {
        double sample = DeterministicRandom.SampleUnitDouble(42, 73, 4, 2);

        Assert.Equal(4_590_399_446_352_750_816L, BitConverter.DoubleToInt64Bits(sample));
        Assert.True(sample >= 0.0);
        Assert.True(sample < 1.0);
    }

    [Fact]
    public void DeriveStreamId_PurposeString_ReturnsStableBitPattern()
    {
        ulong streamId = DeterministicRandom.DeriveStreamId(73, "discovery");

        Assert.Equal(4_093_831_665_954_233_643UL, streamId);
        Assert.Equal(streamId, DeterministicRandom.DeriveStreamId(73, "discovery"));
        Assert.NotEqual(streamId, DeterministicRandom.DeriveStreamId(73, "quality"));
    }

    [Fact]
    public void SampleInt32_WideSignedRange_AlwaysStaysWithinBounds()
    {
        int[] samples = Enumerable.Range(0, 100)
            .Select(index => DeterministicRandom.SampleInt32(
                worldSeed: 42,
                streamId: 73,
                generation: (ulong)index,
                minInclusive: -20,
                maxExclusive: 30))
            .ToArray();

        Assert.All(samples, sample => Assert.InRange(sample, -20, 29));
        Assert.Contains(samples, sample => sample < 0);
        Assert.Contains(samples, sample => sample >= 0);
    }

    [Fact]
    public void SampleExponentialDuration_KnownCoordinates_ReturnsStablePositiveTicks()
    {
        ModelDuration sample = DeterministicRandom.SampleExponentialDuration(
            worldSeed: 42,
            streamId: 73,
            generation: 4,
            mean: ModelDuration.FromSeconds(60));

        Assert.Equal(111_726, sample.Ticks);
        Assert.True(sample.Ticks > 0);
    }

    [Fact]
    public void SampleExponentialDuration_NonPositiveMean_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeterministicRandom.SampleExponentialDuration(42, 73, 4, new ModelDuration(0)));
    }
}

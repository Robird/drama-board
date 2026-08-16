using System.Text;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Random;

/// <summary>Provides stable random samples addressed only by explicit deterministic coordinates.</summary>
public static class DeterministicRandom
{
    private const ulong WorldSeedTag = 0x243F6A8885A308D3UL;
    private const ulong StreamTag = 0x13198A2E03707344UL;
    private const ulong GenerationTag = 0xA4093822299F31D0UL;
    private const ulong SampleTag = 0x082EFA98EC4E6C89UL;
    private const ulong ChildStreamTag = 0x452821E638D01377UL;
    private const ulong SplitMixIncrement = 0x9E3779B97F4A7C15UL;
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325UL;
    private const ulong FnvPrime = 0x100000001B3UL;
    private const long DoubleFractionMask = 0x000FFFFFFFFFFFFF;
    private const double InverseTwoToThe53 = 1.1102230246251565E-16;
    private const double InverseTwoToThe52 = 2.2204460492503131E-16;
    private const double NaturalLogOfTwo = 0.6931471805599453;

    /// <summary>
    /// Maps a persistent signed identity to a stable stream identifier using its two's-complement bits,
    /// the fixed stream tag, and the SplitMix64 mixing constants; these constants are part of the replay contract.
    /// </summary>
    public static ulong DeriveStreamId(long persistentId) =>
        Mix(unchecked((ulong)persistentId) + StreamTag);

    /// <summary>
    /// Maps a persistent string identity to a stable stream identifier using UTF-8 FNV-1a followed by the
    /// fixed stream tag and SplitMix64 mixing constants; encoding and constants are part of the replay contract.
    /// </summary>
    public static ulong DeriveStreamId(string persistentId)
    {
        ArgumentNullException.ThrowIfNull(persistentId);
        return Mix(HashUtf8Fnv1A(persistentId) + StreamTag);
    }

    /// <summary>Derives a stable child stream identifier from numeric parent and child identities.</summary>
    public static ulong DeriveStreamId(ulong parentStreamId, ulong childStreamId)
    {
        unchecked
        {
            return Mix(Mix(parentStreamId + ChildStreamTag) ^ Mix(childStreamId + StreamTag));
        }
    }

    /// <summary>Derives a stable child stream identifier from a parent identity and UTF-8 purpose string.</summary>
    public static ulong DeriveStreamId(ulong parentStreamId, string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        return DeriveStreamId(parentStreamId, HashUtf8Fnv1A(purpose));
    }

    /// <summary>Samples all 64 bits from a stable world, stream, generation, and sample-index coordinate.</summary>
    public static ulong SampleUInt64(
        ulong worldSeed,
        ulong streamId,
        ulong generation,
        ulong sampleIndex = 0)
    {
        unchecked
        {
            ulong coordinate = Mix(worldSeed + WorldSeedTag);
            coordinate ^= Mix(streamId + StreamTag);
            coordinate ^= Mix(generation + GenerationTag);
            coordinate ^= Mix(sampleIndex + SampleTag);
            return Mix(coordinate);
        }
    }

    /// <summary>Samples a double uniformly from the half-open interval [0, 1).</summary>
    public static double SampleUnitDouble(
        ulong worldSeed,
        ulong streamId,
        ulong generation,
        ulong sampleIndex = 0) =>
        (SampleUInt64(worldSeed, streamId, generation, sampleIndex) >> 11) * InverseTwoToThe53;

    /// <summary>Samples an integer uniformly from the half-open interval [minInclusive, maxExclusive).</summary>
    public static int SampleInt32(
        ulong worldSeed,
        ulong streamId,
        ulong generation,
        int minInclusive,
        int maxExclusive,
        ulong sampleIndex = 0)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "The upper bound must be greater than the lower bound.");
        }

        ulong range = (ulong)((long)maxExclusive - minInclusive);
        ulong rejectionThreshold = unchecked(0UL - range) % range;
        ulong offset = 0;

        while (true)
        {
            ulong currentSampleIndex = checked(sampleIndex + offset);
            ulong sample = SampleUInt64(worldSeed, streamId, generation, currentSampleIndex);
            if (sample >= rejectionThreshold)
            {
                return (int)(minInclusive + (long)(sample % range));
            }

            offset = checked(offset + 1);
        }
    }

    /// <summary>Samples a positive one-millisecond duration from an exponential distribution with the given mean.</summary>
    public static ModelDuration SampleExponentialDuration(
        ulong worldSeed,
        ulong streamId,
        ulong generation,
        ModelDuration mean,
        ulong sampleIndex = 0)
    {
        if (mean.Ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mean), "The mean duration must be positive.");
        }

        ulong fraction = SampleUInt64(worldSeed, streamId, generation, sampleIndex) >> 12;
        double unitOpen = (fraction + 0.5) * InverseTwoToThe52;
        double sampledTicks = -NaturalLog(unitOpen) * mean.Ticks;

        if (sampledTicks >= long.MaxValue)
        {
            throw new OverflowException("The sampled duration exceeds the model duration range.");
        }

        long wholeTicks = (long)sampledTicks;
        long roundedUpTicks = sampledTicks > wholeTicks ? checked(wholeTicks + 1) : wholeTicks;
        return new ModelDuration(roundedUpTicks == 0 ? 1 : roundedUpTicks);
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += SplitMixIncrement;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    private static ulong HashUtf8Fnv1A(string value)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte byteValue in Encoding.UTF8.GetBytes(value))
        {
            hash = unchecked((hash ^ byteValue) * FnvPrime);
        }

        return hash;
    }

    private static double NaturalLog(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7FF) - 1023;
        long normalizedBits = (bits & DoubleFractionMask) | (1023L << 52);
        double normalized = BitConverter.Int64BitsToDouble(normalizedBits);
        double ratio = (normalized - 1.0) / (normalized + 1.0);
        double ratioSquared = ratio * ratio;
        double term = ratio;
        double sum = term;

        for (int denominator = 3; denominator <= 41; denominator += 2)
        {
            term *= ratioSquared;
            sum += term / denominator;
        }

        return exponent * NaturalLogOfTwo + 2.0 * sum;
    }
}

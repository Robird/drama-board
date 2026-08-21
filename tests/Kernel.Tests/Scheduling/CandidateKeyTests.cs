using System.Text;
using DramaBoard.Kernel.Scheduling;

namespace DramaBoard.Kernel.Tests.Scheduling;

public sealed class CandidateKeyTests
{
    [Fact]
    public void Constructor_CopiesInputAndReturnedBytes()
    {
        byte[] input = [0x10, 0x20, 0x30];
        var key = new CandidateKey(input);

        input[0] = 0xFF;
        byte[] returned = key.ToByteArray();
        returned[1] = 0xFF;

        Assert.Equal([0x10, 0x20, 0x30], key.ToByteArray());
    }

    [Fact]
    public void FromUtf8_UsesExactUtf8Bytes()
    {
        CandidateKey key = CandidateKey.FromUtf8("timer:Alice/一");

        Assert.Equal(Encoding.UTF8.GetBytes("timer:Alice/一"), key.ToByteArray());
    }

    [Fact]
    public void Equality_IsStructuralAcrossIndependentCopies()
    {
        var first = new CandidateKey([0x01, 0x80, 0xFF]);
        var equal = CandidateKey.FromBytes([0x01, 0x80, 0xFF]);
        var different = new CandidateKey([0x01, 0x80, 0xFE]);

        Assert.Equal(first, equal);
        Assert.True(first == equal);
        Assert.NotEqual(first, different);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
    }

    [Fact]
    public void CompareTo_UsesUnsignedLexicographicBytesAndPrefixOrder()
    {
        var lowerUnsigned = new CandidateKey([0x7F]);
        var higherUnsigned = new CandidateKey([0x80]);
        var prefix = new CandidateKey([0x80]);
        var extension = new CandidateKey([0x80, 0x00]);

        Assert.True(lowerUnsigned.CompareTo(higherUnsigned) < 0);
        Assert.True(prefix.CompareTo(extension) < 0);
    }

    [Fact]
    public void Constructor_NullOrEmptyKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CandidateKey((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => new CandidateKey((string)null!));
        Assert.Throws<ArgumentException>(() => new CandidateKey([]));
        Assert.Throws<ArgumentException>(() => CandidateKey.FromUtf8(string.Empty));
    }
}

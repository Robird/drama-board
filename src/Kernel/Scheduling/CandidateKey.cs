using System.Text;

namespace DramaBoard.Kernel.Scheduling;

/// <summary>
/// Owns the canonical bytes that completely and deterministically identify one occurrence candidate.
/// </summary>
public sealed class CandidateKey : IComparable<CandidateKey>, IEquatable<CandidateKey>
{
    private readonly byte[] _canonicalBytes;

    /// <summary>Initializes a key by copying canonical bytes supplied by the caller.</summary>
    public CandidateKey(byte[] canonicalBytes)
        : this(RequiredSpan(canonicalBytes))
    {
    }

    /// <summary>Initializes a key from the UTF-8 encoding of a canonical string.</summary>
    public CandidateKey(string canonicalText)
        : this(EncodeUtf8(canonicalText))
    {
    }

    private CandidateKey(ReadOnlySpan<byte> canonicalBytes)
    {
        if (canonicalBytes.IsEmpty)
        {
            throw new ArgumentException("A candidate key cannot be empty.", nameof(canonicalBytes));
        }

        _canonicalBytes = canonicalBytes.ToArray();
    }

    /// <summary>Gets the number of canonical bytes in this key.</summary>
    public int Length => _canonicalBytes.Length;

    internal ReadOnlySpan<byte> CanonicalBytes => _canonicalBytes;

    /// <summary>Creates a key by copying canonical bytes supplied by the caller.</summary>
    public static CandidateKey FromBytes(ReadOnlySpan<byte> canonicalBytes) => new(canonicalBytes);

    /// <summary>Creates a key from the UTF-8 encoding of a canonical string.</summary>
    public static CandidateKey FromUtf8(string canonicalText) => new(canonicalText);

    /// <summary>Returns a copy of the key's canonical bytes.</summary>
    public byte[] ToByteArray() => [.. _canonicalBytes];

    /// <inheritdoc />
    public int CompareTo(CandidateKey? other) =>
        other is null ? 1 : CanonicalBytes.SequenceCompareTo(other.CanonicalBytes);

    /// <inheritdoc />
    public bool Equals(CandidateKey? other) =>
        other is not null && CanonicalBytes.SequenceEqual(other.CanonicalBytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CandidateKey);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (byte value in _canonicalBytes)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Convert.ToHexString(_canonicalBytes);

    /// <summary>Returns whether two candidate keys contain identical canonical bytes.</summary>
    public static bool operator ==(CandidateKey? left, CandidateKey? right) =>
        EqualityComparer<CandidateKey>.Default.Equals(left, right);

    /// <summary>Returns whether two candidate keys contain different canonical bytes.</summary>
    public static bool operator !=(CandidateKey? left, CandidateKey? right) => !(left == right);

    private static ReadOnlySpan<byte> RequiredSpan(byte[] canonicalBytes)
    {
        ArgumentNullException.ThrowIfNull(canonicalBytes);
        return canonicalBytes;
    }

    private static byte[] EncodeUtf8(string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(canonicalText);
        return Encoding.UTF8.GetBytes(canonicalText);
    }
}

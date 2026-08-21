using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DramaBoard.Kernel.Scheduling;

/// <summary>Selects the unique next occurrence using the build's deterministic scheduler law.</summary>
public static class OccurrenceScheduler
{
    private const int RankLength = 32;

    // This separator, its terminating zero, the field order, and big-endian integer encoding are
    // part of the current build's scheduler semantics and are locked by golden-vector tests.
    private static ReadOnlySpan<byte> DomainSeparator =>
        "DramaBoard.Kernel.OccurrenceScheduler.v1"u8;

    /// <summary>
    /// Selects the unique minimum by due time, keyed HMAC-SHA256 rank, and canonical key bytes.
    /// </summary>
    public static OccurrenceCandidate<TData> SelectWinner<TData>(
        IEnumerable<OccurrenceCandidate<TData>> candidates,
        ulong worldSeed) =>
        SelectWinner(candidates, worldSeed, ComputeRank);

    internal static OccurrenceCandidate<TData> SelectWinner<TData>(
        IEnumerable<OccurrenceCandidate<TData>> candidates,
        ulong worldSeed,
        Func<ulong, CandidateDue, CandidateKey, byte[]> rankProvider)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rankProvider);

        var knownKeys = new HashSet<CandidateKey>();
        var earliestCandidates = new List<OccurrenceCandidate<TData>>();
        CandidateDue? earliestDue = null;

        foreach (OccurrenceCandidate<TData> candidate in candidates)
        {
            if (candidate is null)
            {
                throw new ArgumentException("Candidate collections cannot contain null entries.", nameof(candidates));
            }

            if (!knownKeys.Add(candidate.Key))
            {
                throw new InvalidOperationException(
                    $"Duplicate candidate key '{candidate.Key}' was forecast in one selection round.");
            }

            if (earliestDue is null || candidate.Due < earliestDue.Value)
            {
                earliestDue = candidate.Due;
                earliestCandidates.Clear();
                earliestCandidates.Add(candidate);
            }
            else if (candidate.Due == earliestDue.Value)
            {
                earliestCandidates.Add(candidate);
            }
        }

        if (earliestCandidates.Count == 0)
        {
            throw new InvalidOperationException("Cannot select a winner from an empty candidate set.");
        }

        if (earliestCandidates.Count == 1)
        {
            return earliestCandidates[0];
        }

        OccurrenceCandidate<TData> winner = earliestCandidates[0];
        byte[] winnerRank = RequiredRank(rankProvider(worldSeed, winner.Due, winner.Key));
        for (int index = 1; index < earliestCandidates.Count; index++)
        {
            OccurrenceCandidate<TData> contender = earliestCandidates[index];
            byte[] contenderRank = RequiredRank(rankProvider(worldSeed, contender.Due, contender.Key));
            int rankComparison = contenderRank.AsSpan().SequenceCompareTo(winnerRank);
            if (rankComparison < 0 ||
                (rankComparison == 0 && contender.Key.CompareTo(winner.Key) < 0))
            {
                winner = contender;
                winnerRank = contenderRank;
            }
        }

        return winner;
    }

    internal static byte[] ComputeRank(
        ulong worldSeed,
        CandidateDue due,
        CandidateKey candidateKey)
    {
        ArgumentNullException.ThrowIfNull(candidateKey);

        Span<byte> hmacKey = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(hmacKey, worldSeed);

        int messageLength = checked(
            DomainSeparator.Length +
            1 +
            sizeof(long) +
            sizeof(int) +
            candidateKey.Length);
        byte[] message = new byte[messageLength];
        Span<byte> messageSpan = message;
        int offset = 0;

        DomainSeparator.CopyTo(messageSpan);
        offset += DomainSeparator.Length;
        messageSpan[offset++] = 0;
        BinaryPrimitives.WriteInt64BigEndian(
            messageSpan.Slice(offset, sizeof(long)),
            due.ModelTime.Ticks);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32BigEndian(
            messageSpan.Slice(offset, sizeof(int)),
            candidateKey.Length);
        offset += sizeof(int);
        candidateKey.CanonicalBytes.CopyTo(messageSpan[offset..]);

        return HMACSHA256.HashData(hmacKey, messageSpan);
    }

    private static byte[] RequiredRank(byte[] rank)
    {
        if (rank is null || rank.Length != RankLength)
        {
            throw new InvalidOperationException(
                $"A scheduler rank provider must return exactly {RankLength} bytes.");
        }

        return rank;
    }
}

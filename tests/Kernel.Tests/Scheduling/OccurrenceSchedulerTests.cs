using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Scheduling;

public sealed class OccurrenceSchedulerTests
{
    [Fact]
    public void ComputeRank_KnownCoordinates_ReturnsGoldenVector()
    {
        byte[] rank = OccurrenceScheduler.ComputeRank(
            worldSeed: 0x0102030405060708UL,
            Due(123_456_789),
            CandidateKey.FromUtf8("timer:alice"));

        Assert.Equal(
            "F4E51EFF40F0464C7881D0A2CFA22E488C4A2B9F5D6B384E0FFAA3DAD3A218C0",
            Convert.ToHexString(rank));
    }

    [Fact]
    public void SelectWinner_AllPermutationsChooseSameCandidate()
    {
        OccurrenceCandidate<string>[] candidates =
        [
            Candidate("alpha", due: 10),
            Candidate("bravo", due: 10),
            Candidate("charlie", due: 10),
            Candidate("later", due: 11),
        ];
        CandidateKey expected = OccurrenceScheduler.SelectWinner(candidates, worldSeed: 42).Key;

        foreach (OccurrenceCandidate<string>[] permutation in Permutations(candidates))
        {
            Assert.Equal(expected, OccurrenceScheduler.SelectWinner(permutation, worldSeed: 42).Key);
        }
    }

    [Fact]
    public void SelectWinner_EarlierDueAlwaysWinsBeforeRankComparison()
    {
        OccurrenceCandidate<string> earlier = Candidate("earlier", due: 9);
        OccurrenceCandidate<string> later = Candidate("later", due: 10);
        int rankCalls = 0;

        OccurrenceCandidate<string> winner = OccurrenceScheduler.SelectWinner(
            [later, earlier],
            worldSeed: 42,
            (_, _, _) =>
            {
                rankCalls++;
                return new byte[32];
            });

        Assert.Same(earlier, winner);
        Assert.Equal(0, rankCalls);
    }

    [Fact]
    public void ComputeRank_DifferentWorldSeedChangesRank()
    {
        CandidateDue due = Due(10);
        CandidateKey key = CandidateKey.FromUtf8("same-key");

        byte[] first = OccurrenceScheduler.ComputeRank(1, due, key);
        byte[] second = OccurrenceScheduler.ComputeRank(2, due, key);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SelectWinner_EqualRanksFallBackToCanonicalCandidateKey()
    {
        OccurrenceCandidate<string> highKey =
            new(new CandidateKey([0xFF]), Due(10), "high");
        OccurrenceCandidate<string> lowKey =
            new(new CandidateKey([0x00]), Due(10), "low");

        OccurrenceCandidate<string> winner = OccurrenceScheduler.SelectWinner(
            [highKey, lowKey],
            worldSeed: 42,
            (_, _, _) => new byte[32]);

        Assert.Same(lowKey, winner);
    }

    [Fact]
    public void SelectWinner_DuplicateKeyIsRejectedAcrossDifferentDueTimes()
    {
        CandidateKey key = CandidateKey.FromUtf8("duplicate");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            OccurrenceScheduler.SelectWinner(
                [
                    new OccurrenceCandidate<string>(key, Due(10), "first"),
                    new OccurrenceCandidate<string>(CandidateKey.FromUtf8("duplicate"), Due(11), "second"),
                ],
                worldSeed: 42));

        Assert.Contains("Duplicate candidate key", exception.Message);
    }

    [Fact]
    public void SelectWinner_ComputesRanksOnlyForCandidatesAtEarliestDue()
    {
        var rankedKeys = new List<CandidateKey>();
        OccurrenceCandidate<string> first = Candidate("first", due: 10);
        OccurrenceCandidate<string> second = Candidate("second", due: 10);
        OccurrenceCandidate<string> later = Candidate("later", due: 11);

        _ = OccurrenceScheduler.SelectWinner(
            [later, second, first],
            worldSeed: 42,
            (_, _, key) =>
            {
                rankedKeys.Add(key);
                byte[] rank = new byte[32];
                rank[^1] = key.ToByteArray()[0];
                return rank;
            });

        Assert.Equal(2, rankedKeys.Count);
        Assert.Contains(first.Key, rankedKeys);
        Assert.Contains(second.Key, rankedKeys);
        Assert.DoesNotContain(later.Key, rankedKeys);
    }

    [Fact]
    public void SelectWinner_EmptySetThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OccurrenceScheduler.SelectWinner(Array.Empty<OccurrenceCandidate<string>>(), worldSeed: 42));
    }

    [Fact]
    public void OccurrenceCandidate_NullKeyIsRejectedAndDataIsPreserved()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OccurrenceCandidate<string>(null!, Due(10), "data"));

        OccurrenceCandidate<string> candidate = Candidate("key", due: 10);
        Assert.Equal("key", candidate.Data);
    }

    private static OccurrenceCandidate<string> Candidate(string key, long due) =>
        new(CandidateKey.FromUtf8(key), Due(due), key);

    private static CandidateDue Due(long ticks) => new(new ModelTime(ticks));

    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0)
        {
            yield return [];
            yield break;
        }

        for (int index = 0; index < values.Count; index++)
        {
            T selected = values[index];
            T[] remaining = values.Where((_, otherIndex) => otherIndex != index).ToArray();
            foreach (T[] permutation in Permutations(remaining))
            {
                yield return [selected, .. permutation];
            }
        }
    }
}

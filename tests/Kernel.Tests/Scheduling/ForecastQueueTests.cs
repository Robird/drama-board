using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Scheduling;

public sealed class ForecastQueueTests
{
    [Fact]
    public void Enqueue_PermutationsOfSameCandidates_DequeuesInIdenticalOrder()
    {
        EventCandidate<string>[] candidates =
        [
            Candidate(4, due: 20, sourceId: 1),
            Candidate(2, due: 10, sourceId: 2),
            Candidate(3, due: 10, sourceId: 1),
            Candidate(1, due: 10, sourceId: 1),
        ];
        long[] expectedIds = [1, 3, 2, 4];

        foreach (EventCandidate<string>[] permutation in Permutations(candidates))
        {
            var queue = new ForecastQueue<string>();
            foreach (EventCandidate<string> candidate in permutation)
            {
                queue.Enqueue(candidate);
            }

            Assert.Equal(expectedIds, DrainIds(queue));
        }
    }

    [Fact]
    public void InvalidateSource_OlderGenerations_ExcludesAndExposesInvalidatedCandidates()
    {
        var queue = new ForecastQueue<string>();
        queue.Enqueue(Candidate(3, due: 30, sourceId: 7, generation: 1));
        queue.Enqueue(Candidate(2, due: 20, sourceId: 7, generation: 2));
        queue.Enqueue(Candidate(1, due: 10, sourceId: 7, generation: 3));
        queue.Enqueue(Candidate(4, due: 5, sourceId: 8, generation: 1));

        queue.InvalidateSource(sourceId: 7, olderThanGeneration: 3);

        Assert.Equal([4, 1], DrainIds(queue));
        Assert.Equal([2, 3], queue.InvalidatedCandidates.Select(candidate => candidate.Id.Value));
    }

    [Fact]
    public void Enqueue_CandidateOlderThanSourceGeneration_RecordsCandidateAsInvalidated()
    {
        var queue = new ForecastQueue<string>();
        queue.InvalidateSource(sourceId: 7, olderThanGeneration: 5);

        queue.Enqueue(Candidate(1, due: 10, sourceId: 7, generation: 4));

        Assert.Equal(0, queue.Count);
        Assert.Equal([1], queue.InvalidatedCandidates.Select(candidate => candidate.Id.Value));
    }

    [Fact]
    public void TryPeekEarliest_EmptyQueue_ReturnsFalse()
    {
        var queue = new ForecastQueue<string>();

        bool found = queue.TryPeekEarliest(out EventCandidate<string> candidate);

        Assert.False(found);
        Assert.Equal(default, candidate);
    }

    [Fact]
    public void DequeueEarliest_EmptyQueue_ThrowsInvalidOperationException()
    {
        var queue = new ForecastQueue<string>();

        Assert.Throws<InvalidOperationException>(() => queue.DequeueEarliest());
    }

    [Fact]
    public void Enqueue_SingleCandidate_PeeksAndDequeuesCandidate()
    {
        var queue = new ForecastQueue<string>();
        EventCandidate<string> expected = Candidate(1, due: 10, sourceId: 2);
        queue.Enqueue(expected);

        bool found = queue.TryPeekEarliest(out EventCandidate<string> peeked);
        EventCandidate<string> dequeued = queue.DequeueEarliest();

        Assert.True(found);
        Assert.Equal(expected.Id, peeked.Id);
        Assert.Equal(expected.Id, dequeued.Id);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Enqueue_ManyCandidatesAtSameDue_OrdersBySourceThenCandidateId()
    {
        var queue = new ForecastQueue<string>();
        EventCandidate<string>[] candidates = Enumerable.Range(1, 200)
            .Select(index => Candidate(201 - index, due: 10, sourceId: index % 10))
            .ToArray();

        foreach (EventCandidate<string> candidate in candidates)
        {
            queue.Enqueue(candidate);
        }

        long[] expectedIds = candidates
            .OrderBy(candidate => candidate.SourceId)
            .ThenBy(candidate => candidate.Id.Value)
            .Select(candidate => candidate.Id.Value)
            .ToArray();
        Assert.Equal(expectedIds, DrainIds(queue));
    }

    [Fact]
    public void Enqueue_SameCandidateIdFromDifferentSources_AcceptsBothCandidates()
    {
        var queue = new ForecastQueue<string>();
        queue.Enqueue(Candidate(1, due: 10, sourceId: 2));
        queue.Enqueue(Candidate(1, due: 10, sourceId: 1));

        Assert.Equal([1L, 2L], DrainSourceIds(queue));
    }

    [Fact]
    public void Enqueue_DuplicateCandidateIdentityWithinSource_ThrowsArgumentException()
    {
        var queue = new ForecastQueue<string>();
        queue.Enqueue(Candidate(1, due: 10, sourceId: 1));

        Assert.Throws<ArgumentException>(() => queue.Enqueue(Candidate(1, due: 20, sourceId: 1)));
    }

    private static EventCandidate<string> Candidate(
        long id,
        long due,
        long sourceId,
        long generation = 1) =>
        new(new EventCandidateId(id), new ModelTime(due), sourceId, generation, $"candidate-{id}");

    private static long[] DrainIds(ForecastQueue<string> queue)
    {
        var ids = new List<long>();
        while (queue.TryPeekEarliest(out _))
        {
            ids.Add(queue.DequeueEarliest().Id.Value);
        }

        return [.. ids];
    }

    private static long[] DrainSourceIds(ForecastQueue<string> queue)
    {
        var sourceIds = new List<long>();
        while (queue.TryPeekEarliest(out _))
        {
            sourceIds.Add(queue.DequeueEarliest().SourceId);
        }

        return [.. sourceIds];
    }

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

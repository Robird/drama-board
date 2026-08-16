namespace DramaBoard.Kernel.Scheduling;

/// <summary>Stores forecast candidates and selects them deterministically by due time, source, and candidate identifier.</summary>
public sealed class ForecastQueue<TPayload>
{
    private static readonly CandidateComparer Ordering = new();

    private readonly List<EventCandidate<TPayload>> _candidates = [];
    private readonly HashSet<(long SourceId, EventCandidateId CandidateId)> _knownCandidates = [];

    /// <summary>Gets the number of active candidates available for selection.</summary>
    public int Count => _candidates.Count;

    /// <summary>Adds a candidate.</summary>
    public void Enqueue(EventCandidate<TPayload> candidate)
    {
        if (!_knownCandidates.Add((candidate.SourceId, candidate.Id)))
        {
            throw new ArgumentException(
                $"Candidate identifier {candidate.Id} has already been used by source {candidate.SourceId}.",
                nameof(candidate));
        }

        _candidates.Add(candidate);
    }

    /// <summary>Tries to get the earliest active candidate without removing it.</summary>
    public bool TryPeekEarliest(out EventCandidate<TPayload> candidate)
    {
        if (_candidates.Count == 0)
        {
            candidate = default;
            return false;
        }

        candidate = _candidates[FindEarliestIndex()];
        return true;
    }

    /// <summary>Removes and returns the earliest active candidate.</summary>
    public EventCandidate<TPayload> DequeueEarliest()
    {
        if (_candidates.Count == 0)
        {
            throw new InvalidOperationException("The forecast queue is empty.");
        }

        int earliestIndex = FindEarliestIndex();
        EventCandidate<TPayload> earliest = _candidates[earliestIndex];
        _candidates.RemoveAt(earliestIndex);
        return earliest;
    }

    private int FindEarliestIndex()
    {
        int earliestIndex = 0;

        for (int index = 1; index < _candidates.Count; index++)
        {
            if (Ordering.Compare(_candidates[index], _candidates[earliestIndex]) < 0)
            {
                earliestIndex = index;
            }
        }

        return earliestIndex;
    }

    private sealed class CandidateComparer : IComparer<EventCandidate<TPayload>>
    {
        public int Compare(EventCandidate<TPayload> left, EventCandidate<TPayload> right)
        {
            int dueComparison = left.Due.CompareTo(right.Due);
            if (dueComparison != 0)
            {
                return dueComparison;
            }

            int sourceComparison = left.SourceId.CompareTo(right.SourceId);
            return sourceComparison != 0 ? sourceComparison : left.Id.CompareTo(right.Id);
        }
    }
}

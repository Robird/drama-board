namespace DramaBoard.Kernel.Journal;

/// <summary>Stores committed domain events in memory while enforcing timestamp monotonicity.</summary>
public sealed class InMemoryJournal<TPayload> : IJournalSink<TPayload>
{
    private readonly List<DomainEvent<TPayload>> _events = [];
    private readonly IReadOnlyList<DomainEvent<TPayload>> _eventsView;

    /// <summary>Initializes an empty in-memory journal.</summary>
    public InMemoryJournal()
    {
        _eventsView = _events.AsReadOnly();
    }

    /// <inheritdoc />
    public IReadOnlyList<DomainEvent<TPayload>> Events => _eventsView;

    /// <inheritdoc />
    public void Append(DomainEvent<TPayload> domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (_events.Count > 0 && domainEvent.Timestamp < _events[^1].Timestamp)
        {
            throw new InvalidOperationException("Journal event timestamps must be monotonically nondecreasing.");
        }

        _events.Add(domainEvent);
    }
}

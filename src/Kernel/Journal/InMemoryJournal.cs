using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Journal;

/// <summary>Stores committed domain events in memory while enforcing strictly increasing timestamps.</summary>
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
    public void Append(DomainEvent<TPayload> domainEvent) => AppendBatch([domainEvent]);

    /// <inheritdoc />
    public void AppendBatch(IReadOnlyList<DomainEvent<TPayload>> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        LogicalTimestamp? previousTimestamp = _events.Count == 0 ? null : _events[^1].Timestamp;
        EventCause expectedCause = batch[0]?.Cause
            ?? throw new ArgumentException("A journal batch cannot contain null events.", nameof(batch));
        var validatedBatch = new DomainEvent<TPayload>[batch.Count];
        for (int index = 0; index < batch.Count; index++)
        {
            DomainEvent<TPayload> domainEvent = batch[index]
                ?? throw new ArgumentException("A journal batch cannot contain null events.", nameof(batch));
            if (domainEvent.Cause != expectedCause)
            {
                throw new InvalidOperationException("All events in a journal batch must have the same cause.");
            }

            if (previousTimestamp is LogicalTimestamp previous && domainEvent.Timestamp <= previous)
            {
                throw new InvalidOperationException("Journal event timestamps must be strictly increasing.");
            }

            validatedBatch[index] = domainEvent;
            previousTimestamp = domainEvent.Timestamp;
        }

        _events.AddRange(validatedBatch);
    }
}

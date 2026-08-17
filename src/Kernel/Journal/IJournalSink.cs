namespace DramaBoard.Kernel.Journal;

/// <summary>Accepts immutable domain events and exposes them in commit order.</summary>
public interface IJournalSink<TPayload>
{
    /// <summary>Gets the committed events in append order.</summary>
    IReadOnlyList<DomainEvent<TPayload>> Events { get; }

    /// <summary>Appends one committed event.</summary>
    void Append(DomainEvent<TPayload> domainEvent);

    /// <summary>Appends one complete committed event batch.</summary>
    void AppendBatch(IReadOnlyList<DomainEvent<TPayload>> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (DomainEvent<TPayload> domainEvent in batch)
        {
            Append(domainEvent);
        }
    }
}

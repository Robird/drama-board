namespace DramaBoard.Kernel.Journal;

/// <summary>Accepts immutable domain events and exposes them in commit order.</summary>
/// <remarks>
/// Implementations are not required to be thread-safe. A single driver must serialize appends and
/// must not read <see cref="Events"/> concurrently with an append unless the implementation states
/// a stronger synchronization contract.
/// </remarks>
public interface IJournalSink<TPayload>
{
    /// <summary>Gets the committed events in append order.</summary>
    IReadOnlyList<DomainEvent<TPayload>> Events { get; }

    /// <summary>Appends one committed event.</summary>
    void Append(DomainEvent<TPayload> domainEvent);

    /// <summary>
    /// Atomically publishes one complete committed event batch. Validation and pre-publication
    /// persistence failures leave <see cref="Events"/> unchanged; normal return makes the whole
    /// batch visible and durable according to the implementation's storage contract.
    /// </summary>
    void AppendBatch(IReadOnlyList<DomainEvent<TPayload>> batch);
}

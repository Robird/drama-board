namespace DramaBoard.Kernel.Journal;

/// <summary>Publishes and exposes only complete occurrence batches.</summary>
/// <remarks>
/// Implementations are driven serially. Returning normally from <see cref="AppendBatch"/> means the
/// whole batch is committed; throwing before publication must leave <see cref="Batches"/> unchanged.
/// </remarks>
public interface IJournalSink<TFact>
{
    /// <summary>Gets the lineage whose committed transition history this sink publishes.</summary>
    long LineageId { get; }

    /// <summary>Gets committed batches in transition order without exposing fact prefixes.</summary>
    IReadOnlyList<JournalBatch<TFact>> Batches { get; }

    /// <summary>Atomically publishes one complete non-empty batch in a non-cancellable commit section.</summary>
    void AppendBatch(JournalBatch<TFact> batch);
}

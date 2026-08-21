namespace DramaBoard.Kernel.Journal;

/// <summary>Stores complete committed batches in memory.</summary>
public sealed class InMemoryJournal<TFact> : IJournalSink<TFact>
{
    private readonly List<JournalBatch<TFact>> _batches = [];
    private readonly IReadOnlyList<JournalBatch<TFact>> _batchesView;

    /// <summary>Initializes an empty in-memory journal bound to one lineage.</summary>
    public InMemoryJournal(long lineageId)
    {
        LineageId = lineageId;
        _batchesView = _batches.AsReadOnly();
    }

    /// <inheritdoc />
    public long LineageId { get; }

    /// <inheritdoc />
    public IReadOnlyList<JournalBatch<TFact>> Batches => _batchesView;

    /// <inheritdoc />
    public void AppendBatch(JournalBatch<TFact> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_batches.Count > 0 && batch.Instant <= _batches[^1].Instant)
        {
            throw new InvalidOperationException(
                "Journal batch logical instants must be strictly increasing between batches.");
        }

        _batches.Add(batch);
    }

    /// <summary>Copies a complete transition prefix into an independent in-memory journal.</summary>
    public InMemoryJournal<TFact> ForkPrefix(long transitionCount, long newLineageId)
    {
        if (transitionCount < 0 || transitionCount > _batches.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionCount));
        }

        if (newLineageId == LineageId)
        {
            throw new ArgumentException(
                "A fork journal must use a LineageId distinct from its source.",
                nameof(newLineageId));
        }

        var prefix = new InMemoryJournal<TFact>(newLineageId);
        for (int index = 0; index < transitionCount; index++)
        {
            prefix.AppendBatch(_batches[index]);
        }

        return prefix;
    }
}

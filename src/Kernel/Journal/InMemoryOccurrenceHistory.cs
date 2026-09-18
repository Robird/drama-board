using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Kernel.Journal;

/// <summary>The same E/S contract in memory, with completed events retained for test oracles.
/// Supplied worlds and facts must obey the caller's immutable snapshot contract.</summary>
public sealed class InMemoryOccurrenceHistory<TWorld, TFact> : IOccurrenceHistory<TWorld, TFact> {
    private readonly List<OccurrenceEvent<TFact>> _completedEvents = [];
    private readonly IReadOnlyList<OccurrenceEvent<TFact>> _completedView;

    public InMemoryOccurrenceHistory(TWorld state, KernelCursor cursor) {
        ArgumentNullException.ThrowIfNull(state);
        cursor.Validate();
        State = state;
        Cursor = cursor;
        _completedView = _completedEvents.AsReadOnly();
    }

    public TWorld State { get; private set; }
    public KernelCursor Cursor { get; private set; }
    public OccurrenceEvent<TFact>? PendingEvent { get; private set; }
    public IReadOnlyList<OccurrenceEvent<TFact>> CompletedEvents => _completedView;

    public void CommitEvent(OccurrenceEvent<TFact> occurrence) {
        ArgumentNullException.ThrowIfNull(occurrence);
        occurrence.Validate();
        if (PendingEvent is not null) {
            throw new InvalidOperationException("Complete the pending Event before recording another.");
        }
        PendingEvent = occurrence;
    }

    public void CommitState(TWorld nextState, KernelCursor nextCursor) {
        ArgumentNullException.ThrowIfNull(nextState);
        OccurrenceEvent<TFact> pending = PendingEvent
            ?? throw new InvalidOperationException("A State must complete a pending Event.");
        nextCursor.Validate();
        if (nextCursor != Cursor.Advance(pending.CauseKey, pending.TargetInstant)) {
            throw new ArgumentException("The State cursor must complete exactly the pending occurrence.", nameof(nextCursor));
        }
        _completedEvents.Add(pending);
        State = nextState;
        Cursor = nextCursor;
        PendingEvent = null;
    }
}

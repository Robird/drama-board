using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Kernel.Journal;

/// <summary>Owns the completed State/cursor and at most one published pending occurrence.
/// Commits alternate E and S. Normal return confirms publication; an exception can have an
/// uncertain outcome, so callers must abandon the attempt and reopen persistent history.
/// CommitState installs the supplied State instance (or equal value for a value-type State)
/// with its cursor before returning. The history is exclusively owned by its active Kernel.</summary>
/// <typeparam name="TWorld">A prepared runtime world, which may contain non-persistent derived caches.
/// A persistent adapter may store only its business projection and rebuild the caches when opening.
/// After opening or committing, State must stably return the installed instance, not rebuild it per read.</typeparam>
public interface IOccurrenceHistory<TWorld, TFact> {
    TWorld State { get; }
    KernelCursor Cursor { get; }
    OccurrenceEvent<TFact>? PendingEvent { get; }
    void CommitEvent(OccurrenceEvent<TFact> occurrence);
    void CommitState(TWorld nextState, KernelCursor nextCursor);
}

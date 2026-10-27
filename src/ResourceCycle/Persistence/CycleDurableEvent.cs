using Atelia.DurableGraph;
using DramaBoard.Kernel.Journal;

namespace DramaBoard.ResourceCycle;

/// <summary>Application revision metadata for a separately persisted, already decided occurrence.</summary>
/// <remarks>The Event has its own object graph. Match State references by business ID, not object identity.</remarks>
[DurableType("DramaBoard.ResourceCycle.Event", 1)]
public sealed partial class CycleDurableEvent : IDurableObject {
    [DurableField(1)] private readonly int _gameRevision;
    [DurableField(2)] private readonly OccurrenceEvent<CycleFact> _occurrence;

    public CycleDurableEvent(OccurrenceEvent<CycleFact> occurrence)
        : this(occurrence, CycleGameRevision.Current) { }

    internal CycleDurableEvent(OccurrenceEvent<CycleFact> occurrence, int gameRevision) {
        _gameRevision = gameRevision;
        _occurrence = occurrence;
        ValidateStructure();
    }

    public int GameRevision => _gameRevision;
    public OccurrenceEvent<CycleFact> Occurrence => _occurrence;

    internal void ValidateStructure() {
        if (_gameRevision <= 0) {
            throw new InvalidDataException("A ResourceCycle Event requires a positive GameRevision.");
        }
        ArgumentNullException.ThrowIfNull(_occurrence);
        _occurrence.Validate();
    }
}

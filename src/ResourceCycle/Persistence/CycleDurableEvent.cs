using Atelia.DurableGraph;
using DramaBoard.Kernel.Journal;

namespace DramaBoard.ResourceCycle;

/// <summary>Application revision metadata for a separately persisted, already decided occurrence.</summary>
/// <remarks>The Event has its own object graph. Match State references by business ID, not object identity.</remarks>
[DurableType("DramaBoard.ResourceCycle.Event", 2)]
public sealed partial class CycleDurableEvent : IDurableObject {
    [DurableField(1)] private readonly int _gameRevision;
    [DurableField(2)] private readonly OccurrenceEvent<CycleFact> _occurrence;
    [DurableField(3)] private readonly string _contentId;
    [DurableField(4)] private readonly string _contentFingerprint;

    public CycleDurableEvent(OccurrenceEvent<CycleFact> occurrence, CycleWorldDefinition definition)
        : this(occurrence, definition, CycleGameRevision.Current) { }

    internal CycleDurableEvent(OccurrenceEvent<CycleFact> occurrence, CycleWorldDefinition definition, int gameRevision) {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        _gameRevision = gameRevision;
        _occurrence = occurrence;
        _contentId = definition.ContentId;
        _contentFingerprint = definition.Fingerprint;
        ValidateStructure();
    }

    public int GameRevision => _gameRevision;
    public OccurrenceEvent<CycleFact> Occurrence => _occurrence;
    public string ContentId => _contentId;
    public string ContentFingerprint => _contentFingerprint;

    internal void ValidateContent(CycleWorldDefinition definition) {
        ValidateStructure();
        if (_contentId != definition.ContentId || _contentFingerprint != definition.Fingerprint) {
            throw new InvalidDataException("The Event content identity does not match its planning State.");
        }
    }

    internal void ValidateStructure() {
        if (_gameRevision <= 0) {
            throw new InvalidDataException("A ResourceCycle Event requires a positive GameRevision.");
        }
        ArgumentNullException.ThrowIfNull(_occurrence);
        if (string.IsNullOrWhiteSpace(_contentId) || string.IsNullOrWhiteSpace(_contentFingerprint)) {
            throw new InvalidDataException("A ResourceCycle Event requires its planning content identity.");
        }
        _occurrence.Validate();
    }
}

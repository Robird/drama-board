using Atelia.DurableGraph;
using DramaBoard.Kernel.Journal;

namespace DramaBoard.Playground;

/// <summary>Application revision metadata for a separately persisted, already decided occurrence.</summary>
/// <remarks>The Event has its own object graph. Match State references by business ID, not object identity.</remarks>
[DurableType("DramaBoard.Playground.Event", 1)]
public sealed partial class PlaygroundDurableEvent : IDurableObject {
    [DurableField(1)] private readonly int _gameRevision;
    [DurableField(2)] private readonly OccurrenceEvent<PlaygroundFact> _occurrence;
    [DurableField(3)] private readonly string _contentId;
    [DurableField(4)] private readonly string _contentFingerprint;

    public PlaygroundDurableEvent(OccurrenceEvent<PlaygroundFact> occurrence, PlaygroundWorldDefinition definition)
        : this(occurrence, definition, PlaygroundGameRevision.Current) { }

    internal PlaygroundDurableEvent(OccurrenceEvent<PlaygroundFact> occurrence, PlaygroundWorldDefinition definition, int gameRevision) {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        _gameRevision = gameRevision;
        _occurrence = occurrence;
        _contentId = definition.ContentId;
        _contentFingerprint = definition.Fingerprint;
        ValidateStructure();
    }

    public int GameRevision => _gameRevision;
    public OccurrenceEvent<PlaygroundFact> Occurrence => _occurrence;
    public string ContentId => _contentId;
    public string ContentFingerprint => _contentFingerprint;

    internal void ValidateContent(PlaygroundWorldDefinition definition) {
        ValidateStructure();
        if (_contentId != definition.ContentId || _contentFingerprint != definition.Fingerprint) {
            throw new InvalidDataException("The Event content identity does not match its planning State.");
        }
    }

    internal void ValidateStructure() {
        if (_gameRevision <= 0) {
            throw new InvalidDataException("A Playground Event requires a positive GameRevision.");
        }
        ArgumentNullException.ThrowIfNull(_occurrence);
        if (string.IsNullOrWhiteSpace(_contentId) || string.IsNullOrWhiteSpace(_contentFingerprint)) {
            throw new InvalidDataException("A Playground Event requires its planning content identity.");
        }
        _occurrence.Validate();
    }
}

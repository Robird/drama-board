using Atelia.DurableGraph;
using DramaBoard.Kernel.Journal;

namespace DramaBoard.Microtown;

/// <summary>Already planned work in its own object graph, carrying an independent application revision.</summary>
[DurableType("DramaBoard.Microtown.Event", 1)]
public sealed partial class TownDurableEvent : IDurableObject {
    [DurableField(1)] private readonly string _gameId;
    public string GameId => _gameId;
    [DurableField(2)] private readonly int _gameRevision;
    public int GameRevision => _gameRevision;
    [DurableField(3)] private readonly OccurrenceEvent<TownFact> _occurrence;
    public OccurrenceEvent<TownFact> Occurrence => _occurrence;

    public TownDurableEvent(OccurrenceEvent<TownFact> occurrence,
        string gameId = TownGameRevision.GameId, int gameRevision = TownGameRevision.Current) {
        _occurrence = occurrence; _gameId = gameId; _gameRevision = gameRevision;
    }

    public void Validate() {
        TownGameRevision.RequireSupported(GameId, GameRevision);
        ArgumentNullException.ThrowIfNull(Occurrence);
        Occurrence.Validate();
    }
}

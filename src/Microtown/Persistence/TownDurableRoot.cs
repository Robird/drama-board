using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown;

/// <summary>Completed authoritative state; derived spatial contacts are never stored here.</summary>
[DurableType("DramaBoard.Microtown.Root", 1)]
public sealed partial class TownDurableRoot : IDurableObject {
    [DurableField(1)] private readonly string _gameId;
    public string GameId => _gameId;
    [DurableField(2)] private readonly int _gameRevision;
    public int GameRevision => _gameRevision;
    [DurableField(3)] private readonly TownBusinessState _business;
    public TownBusinessState Business => _business;
    [DurableField(4)] private readonly KernelCursor _cursor;
    public KernelCursor Cursor => _cursor;

    public TownDurableRoot(TownBusinessState business, KernelCursor cursor,
        string gameId = TownGameRevision.GameId, int gameRevision = TownGameRevision.Current) {
        _business = business; _cursor = cursor; _gameId = gameId; _gameRevision = gameRevision;
    }

    public void Validate() {
        TownGameRevision.RequireSupported(GameId, GameRevision);
        ArgumentNullException.ThrowIfNull(Business);
        Cursor.Validate();
        if (Business.Time != Cursor.CurrentModelTime) {
            throw new InvalidDataException("The town state time must match its completed Kernel cursor.");
        }
        Business.Validate();
    }
}

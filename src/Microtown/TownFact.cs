using DramaBoard.Goals;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public enum TownEventKind { DecisionOpened, Operation, ProductionCompleted, WaitCompleted, Arrived, MarketRefreshed, Failed, Ended, ContactOccurred }

/// <summary>Already-decided replacement state plus typed provenance. Recovery applies values, never reruns a command or controller.</summary>
/// <remarks>The initial in-memory slice uses a whole immutable business replacement. This is not a host-side log or another publication authority.</remarks>
public sealed class TownFact {
    public TownEventKind Kind { get; }
    public string? ActorId { get; }
    public bool Accepted { get; }
    public string Result { get; }
    public ModelTime At => NextBusiness.Time;
    public long PreviousGeneration { get; }
    public TownBusinessState NextBusiness { get; }
    public IReadOnlyList<GraphSpatialFact> SpatialFacts { get; }
    public GoalReceipt? GoalReceipt { get; }
    public TownFact(TownEventKind kind, TownBusinessState previous, TownBusinessState nextBusiness,
        string? actorId = null, string result = "", bool accepted = true, IEnumerable<GraphSpatialFact>? spatialFacts = null,
        GoalReceipt? goalReceipt = null) {
        Kind = kind; PreviousGeneration = previous.Generation; NextBusiness = nextBusiness;
        ActorId = actorId; Result = result; Accepted = accepted;
        SpatialFacts = Array.AsReadOnly((spatialFacts ?? []).ToArray()); GoalReceipt = goalReceipt;
    }
}

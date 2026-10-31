using Atelia.DurableGraph;
using DramaBoard.Goals;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

[DurableType("DramaBoard.Microtown.EventKind", 1)]
public enum TownEventKind { DecisionOpened, Operation, ProductionCompleted, WaitCompleted, Arrived, MarketRefreshed, Failed, Ended, ContactOccurred }

/// <summary>Already-decided replacement state plus typed provenance. Recovery applies values, never reruns a command or controller.</summary>
/// <remarks>The initial in-memory slice uses a whole immutable business replacement. This is not a host-side log or another publication authority.</remarks>
[DurableType("DramaBoard.Microtown.Fact", 1)]
public sealed partial class TownFact : IDurableObject {
    [DurableField(1)] private readonly TownEventKind _kind;
    public TownEventKind Kind => _kind;
    [DurableField(2)] private readonly string? _actorId;
    public string? ActorId => _actorId;
    [DurableField(3)] private readonly bool _accepted;
    public bool Accepted => _accepted;
    [DurableField(4)] private readonly string _result;
    public string Result => _result;
    public ModelTime At => NextBusiness.Time;
    [DurableField(5)] private readonly long _previousGeneration;
    public long PreviousGeneration => _previousGeneration;
    [DurableField(6)] private readonly TownBusinessState _nextBusiness;
    public TownBusinessState NextBusiness => _nextBusiness;
    [DurableField(7)] private List<GraphSpatialFact> _spatialFacts;
    public IReadOnlyList<GraphSpatialFact> SpatialFacts => _spatialFacts.AsReadOnly();
    [DurableField(8)] private readonly GoalReceipt? _goalReceipt;
    public GoalReceipt? GoalReceipt => _goalReceipt;
    [DurableField(9)] private readonly TownOperationReceipt? _operationReceipt;
    public TownOperationReceipt? OperationReceipt => _operationReceipt;
    public TownFact(TownEventKind kind, TownBusinessState previous, TownBusinessState nextBusiness,
        string? actorId = null, string result = "", bool accepted = true, IEnumerable<GraphSpatialFact>? spatialFacts = null,
        GoalReceipt? goalReceipt = null, TownOperationReceipt? operationReceipt = null) {
        _kind = kind; _previousGeneration = previous.Generation; _nextBusiness = nextBusiness;
        _actorId = actorId; _result = result; _accepted = accepted;
        _spatialFacts = (spatialFacts ?? []).ToList(); _goalReceipt = goalReceipt; _operationReceipt = operationReceipt;
    }
}

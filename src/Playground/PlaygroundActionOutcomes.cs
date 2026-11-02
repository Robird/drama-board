using Atelia.DurableGraph;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.GatherStopReason", 1)]
public enum PlaygroundGatherStopReason { TargetReached, ResourceEmpty, ItemLimit, CapacityLimit, TimeLimit }
[DurableType("DramaBoard.Playground.OutcomeActivityState", 1)]
public enum PlaygroundOutcomeActivityState { Ongoing, Completed, Terminal }

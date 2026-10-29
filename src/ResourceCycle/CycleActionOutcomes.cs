using Atelia.DurableGraph;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.GatherStopReason", 1)]
public enum CycleGatherStopReason { TargetReached, ResourceEmpty, ItemLimit, CapacityLimit, TimeLimit }
[DurableType("DramaBoard.ResourceCycle.OutcomeActivityState", 1)]
public enum CycleOutcomeActivityState { Ongoing, Completed, Terminal }

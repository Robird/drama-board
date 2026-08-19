using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Describes one deterministic topology edge selected as the next movement step.</summary>
internal sealed record NavigationEdge(
    CellRef From,
    CellRef To,
    SpatialEdgeKind EdgeKind,
    PortalId? PortalId,
    ModelDuration Duration);

/// <summary>Represents the complete outcome of planning only the next authoritative step.</summary>
internal abstract record PathSearchResult
{
    /// <summary>Returns one selected edge and the goal cell whose shortest route owns it.</summary>
    internal sealed record NextStep(
        NavigationEdge Edge,
        CellRef SelectedGoal,
        long TotalCostTicks) : PathSearchResult;

    /// <summary>Reports that the start cell already belongs to the resolved goal.</summary>
    internal sealed record AlreadySatisfied(CellRef SatisfiedGoal) : PathSearchResult;

    /// <summary>Reports that no topological route reaches any resolved goal cell.</summary>
    internal sealed record Unreachable : PathSearchResult;

    /// <summary>Reports that every remaining possible route exceeded representable model duration.</summary>
    internal sealed record CostOverflow : PathSearchResult;
}

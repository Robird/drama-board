using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Interprets static topology together with the state's sparse runtime overrides.</summary>
internal sealed class EffectiveSpatialTopology
{
    private readonly SpatialDefinition _definition;
    private readonly SpatialState _state;

    /// <summary>Creates an effective view over a stamp-compatible state, including legal prefixes.</summary>
    internal EffectiveSpatialTopology(SpatialDefinition definition, SpatialState state)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        SpatialStateValidator.ValidateStamp(definition, state);
        _definition = definition;
        _state = state;
    }

    internal bool BlocksMovement(CellRef cell) =>
        FindCellOverride(cell)?.BlocksMovement ?? _definition.GetCell(cell).BlocksMovement;

    internal bool BlocksSight(CellRef cell) =>
        FindCellOverride(cell)?.BlocksSight ?? _definition.GetCell(cell).BlocksSight;

    internal int GetMoveCost(CellRef cell) =>
        FindCellOverride(cell)?.MoveCost ?? _definition.GetCell(cell).MoveCost;

    internal bool IsPortalEnabled(PortalId portalId) =>
        _state.PortalOverrides.SingleOrDefault(value => value.PortalId == portalId)?.IsEnabled
        ?? SpatialStateValidator.RequirePortal(_definition, portalId).InitiallyEnabled;

    internal bool IsLegPassable(CurrentLeg leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (BlocksMovement(leg.To))
        {
            return false;
        }

        return leg.EdgeKind != SpatialEdgeKind.Portal || IsPortalEnabled(leg.PortalId!.Value);
    }

    internal ModelDuration GetTraversalDuration(CurrentLeg leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        return GetTraversalDuration(leg.EdgeKind, leg.To, leg.PortalId);
    }

    internal ModelDuration GetTraversalDuration(
        SpatialEdgeKind edgeKind,
        CellRef target,
        PortalId? portalId)
    {
        if ((edgeKind == SpatialEdgeKind.Portal) != portalId.HasValue)
        {
            throw new InvalidOperationException(
                "Portal identifier must exist exactly for portal traversal duration.");
        }

        if (edgeKind == SpatialEdgeKind.Portal)
        {
            return SpatialStateValidator.RequirePortal(_definition, portalId!.Value).TraversalDuration;
        }

        if (edgeKind != SpatialEdgeKind.Orthogonal)
        {
            throw new InvalidOperationException($"Unsupported spatial edge kind '{edgeKind}'.");
        }

        GridMapDefinition map = _definition.GetMap(target.MapId);
        long durationTicks = checked(map.OrthogonalStepDuration.Ticks * GetMoveCost(target));
        return new ModelDuration(durationTicks);
    }

    private CellOverride? FindCellOverride(CellRef cell) =>
        _state.CellOverrides.SingleOrDefault(value => value.Cell == cell)?.Value;
}

using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Finds deterministic shortest next steps over the current grid-dominant topology.</summary>
internal static class SpatialNavigator
{
    private static readonly OrthogonalOffset[] OrthogonalOffsets =
    [
        new(Direction.North, 0, -1),
        new(Direction.East, 1, 0),
        new(Direction.South, 0, 1),
        new(Direction.West, -1, 0),
    ];

    /// <summary>Finds one shortest next step without persisting or exposing the complete route.</summary>
    internal static PathSearchResult FindNextStep(
        SpatialDefinition definition,
        SpatialState state,
        CellRef start,
        MoveGoal goal)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(goal);
        var topology = new EffectiveSpatialTopology(definition, state);
        SpatialStateValidator.EnsureCellExists(definition, start, "Navigation start");
        SpatialStateValidator.ValidateGoal(definition, goal);

        IReadOnlySet<CellRef> goals = ResolveGoalCells(definition, goal);
        if (goals.Contains(start))
        {
            return new PathSearchResult.AlreadySatisfied(start);
        }

        var labels = new Dictionary<CellRef, PathLabel>
        {
            [start] = new PathLabel(0, Predecessor: null, IncomingEdge: null, IncomingEdgeKey.Start),
        };
        var frontier = new PriorityQueue<FrontierEntry, FrontierKey>(FrontierKeyComparer.Instance);
        frontier.Enqueue(
            new FrontierEntry(start, 0, IncomingEdgeKey.Start),
            new FrontierKey(0, start, IncomingEdgeKey.Start));

        while (frontier.TryDequeue(out FrontierEntry entry, out _))
        {
            if (!labels.TryGetValue(entry.Cell, out PathLabel? current) ||
                current.TotalCostTicks != entry.TotalCostTicks ||
                current.IncomingKey != entry.IncomingKey)
            {
                continue;
            }

            if (goals.Contains(entry.Cell))
            {
                if (entry.TotalCostTicks > (UInt128)long.MaxValue)
                {
                    return new PathSearchResult.CostOverflow();
                }

                NavigationEdge firstEdge = ReconstructFirstEdge(start, entry.Cell, labels);
                return new PathSearchResult.NextStep(
                    firstEdge,
                    entry.Cell,
                    checked((long)entry.TotalCostTicks));
            }

            foreach (EdgeCandidate edge in EnumerateOutgoingEdges(definition, topology, entry.Cell))
            {
                NavigationEdge navigationEdge = edge.Edge;
                UInt128 candidateCost = checked(
                    entry.TotalCostTicks + (UInt128)(ulong)navigationEdge.Duration.Ticks);

                if (labels.TryGetValue(navigationEdge.To, out PathLabel? known) &&
                    candidateCost >= known.TotalCostTicks)
                {
                    // In particular, equal-cost predecessors never replace the first canonical discovery.
                    continue;
                }

                labels[navigationEdge.To] = new PathLabel(
                    candidateCost,
                    entry.Cell,
                    navigationEdge,
                    edge.IncomingKey);
                var nextEntry = new FrontierEntry(navigationEdge.To, candidateCost, edge.IncomingKey);
                frontier.Enqueue(
                    nextEntry,
                    new FrontierKey(candidateCost, navigationEdge.To, edge.IncomingKey));
            }
        }

        return new PathSearchResult.Unreachable();
    }

    private static IReadOnlySet<CellRef> ResolveGoalCells(SpatialDefinition definition, MoveGoal goal) =>
        goal switch
        {
            CellGoal cellGoal => new HashSet<CellRef> { cellGoal.Cell },
            AnchorGoal anchorGoal => new HashSet<CellRef>
            {
                definition.Anchors.Single(anchor => anchor.Id == anchorGoal.AnchorId).Cell,
            },
            ZoneGoal zoneGoal => definition.Zones.Single(zone => zone.Id == zoneGoal.ZoneId).Cells.ToHashSet(),
            _ => throw new InvalidOperationException($"Unsupported movement goal '{goal.GetType().Name}'."),
        };

    private static IEnumerable<EdgeCandidate> EnumerateOutgoingEdges(
        SpatialDefinition definition,
        EffectiveSpatialTopology topology,
        CellRef from)
    {
        GridMapDefinition map = definition.GetMap(from.MapId);
        foreach (OrthogonalOffset offset in OrthogonalOffsets)
        {
            int targetX = from.X + offset.DeltaX;
            int targetY = from.Y + offset.DeltaY;
            if (targetX < 0 || targetX >= map.Width || targetY < 0 || targetY >= map.Height)
            {
                continue;
            }

            var target = new CellRef(from.MapId, targetX, targetY);
            if (topology.BlocksMovement(target))
            {
                continue;
            }

            ModelDuration duration = topology.GetTraversalDuration(
                SpatialEdgeKind.Orthogonal,
                target,
                portalId: null);

            var edge = new NavigationEdge(
                from,
                target,
                SpatialEdgeKind.Orthogonal,
                PortalId: null,
                duration);
            yield return new EdgeCandidate(edge, IncomingEdgeKey.Orthogonal(offset.Direction));
        }

        foreach (PortalDefinition portal in definition.Portals
                     .Where(portal => portal.From == from)
                     .OrderBy(portal => portal.Id))
        {
            if (!topology.IsPortalEnabled(portal.Id) || topology.BlocksMovement(portal.To))
            {
                continue;
            }

            ModelDuration duration = topology.GetTraversalDuration(
                SpatialEdgeKind.Portal,
                portal.To,
                portal.Id);

            var edge = new NavigationEdge(
                from,
                portal.To,
                SpatialEdgeKind.Portal,
                portal.Id,
                duration);
            yield return new EdgeCandidate(edge, IncomingEdgeKey.Portal(portal.Id));
        }
    }

    private static NavigationEdge ReconstructFirstEdge(
        CellRef start,
        CellRef selectedGoal,
        IReadOnlyDictionary<CellRef, PathLabel> labels)
    {
        CellRef currentCell = selectedGoal;
        PathLabel current = labels[currentCell];
        while (current.Predecessor is CellRef predecessor && predecessor != start)
        {
            currentCell = predecessor;
            current = labels[currentCell];
        }

        return current.IncomingEdge ?? throw new InvalidOperationException(
            "A non-satisfied navigation result must have a first incoming edge.");
    }

    private enum Direction
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3,
    }

    private readonly record struct OrthogonalOffset(
        Direction Direction,
        int DeltaX,
        int DeltaY);

    private sealed record PathLabel(
        UInt128 TotalCostTicks,
        CellRef? Predecessor,
        NavigationEdge? IncomingEdge,
        IncomingEdgeKey IncomingKey);

    private readonly record struct FrontierEntry(
        CellRef Cell,
        UInt128 TotalCostTicks,
        IncomingEdgeKey IncomingKey);

    private readonly record struct EdgeCandidate(
        NavigationEdge Edge,
        IncomingEdgeKey IncomingKey);

    private readonly record struct FrontierKey(
        UInt128 TotalCostTicks,
        CellRef Cell,
        IncomingEdgeKey IncomingKey);

    private readonly record struct IncomingEdgeKey(
        int KindOrder,
        int DirectionOrder,
        PortalId PortalId) : IComparable<IncomingEdgeKey>
    {
        public static IncomingEdgeKey Start => new(0, -1, default);

        public static IncomingEdgeKey Orthogonal(Direction direction) =>
            new(1, (int)direction, default);

        public static IncomingEdgeKey Portal(PortalId portalId) => new(2, -1, portalId);

        public int CompareTo(IncomingEdgeKey other)
        {
            int kind = KindOrder.CompareTo(other.KindOrder);
            if (kind != 0)
            {
                return kind;
            }

            if (KindOrder == 1)
            {
                return DirectionOrder.CompareTo(other.DirectionOrder);
            }

            return KindOrder == 2 ? PortalId.CompareTo(other.PortalId) : 0;
        }
    }

    private sealed class FrontierKeyComparer : IComparer<FrontierKey>
    {
        public static FrontierKeyComparer Instance { get; } = new();

        public int Compare(FrontierKey left, FrontierKey right)
        {
            int cost = left.TotalCostTicks.CompareTo(right.TotalCostTicks);
            if (cost != 0)
            {
                return cost;
            }

            int map = left.Cell.MapId.CompareTo(right.Cell.MapId);
            if (map != 0)
            {
                return map;
            }

            int y = left.Cell.Y.CompareTo(right.Cell.Y);
            if (y != 0)
            {
                return y;
            }

            int x = left.Cell.X.CompareTo(right.Cell.X);
            return x != 0 ? x : left.IncomingKey.CompareTo(right.IncomingKey);
        }
    }
}

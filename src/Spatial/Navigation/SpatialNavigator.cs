using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Finds deterministic minimum-duration routes on the current effective directed graph.</summary>
public sealed class SpatialNavigator
{
    private readonly GraphDefinition _definition;

    public SpatialNavigator(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public RouteResult FindRoute(
        GraphSpatialState state,
        PlaceId startPlaceId,
        PlaceId goalPlaceId,
        long speedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        GraphSpatialStateValidator.ValidateComplete(_definition, state);
        if (!_definition.Contains(startPlaceId))
        {
            return new UnknownStart();
        }

        if (!_definition.Contains(goalPlaceId))
        {
            return new UnknownGoal();
        }

        if (speedSnapshot <= 0)
        {
            return new InvalidSpeed();
        }

        if (startPlaceId == goalPlaceId)
        {
            return new AlreadyAtGoal();
        }

        var best = new Dictionary<PlaceId, PathLabel>();
        var frontier = new PriorityQueue<PathLabel, PathLabel>(PathLabelComparer.Instance);
        var start = new PathLabel(startPlaceId, 0, []);
        best.Add(startPlaceId, start);
        frontier.Enqueue(start, start);

        while (frontier.TryDequeue(out PathLabel? current, out _))
        {
            if (!best.TryGetValue(current.PlaceId, out PathLabel? known) ||
                PathLabelComparer.Instance.Compare(current, known) != 0)
            {
                continue;
            }

            if (current.PlaceId == goalPlaceId)
            {
                return new RouteFound(new ModelDuration(current.Cost), current.Legs);
            }

            foreach (DirectedPassage edge in Outgoing(state, current.PlaceId))
            {
                long resultingCost;
                try
                {
                    resultingCost = checked(
                        current.Cost + SpatialMath.TravelDuration(edge.Passage.Length, speedSnapshot).Ticks);
                }
                catch (OverflowException)
                {
                    continue;
                }

                RouteLeg[] resultingLegs =
                [
                    .. current.Legs,
                    new RouteLeg(edge.Passage.Id, current.PlaceId, edge.Destination),
                ];
                var resulting = new PathLabel(edge.Destination, resultingCost, resultingLegs);
                if (!best.TryGetValue(edge.Destination, out PathLabel? previous) ||
                    PathLabelComparer.Instance.ComparePath(resulting, previous) < 0)
                {
                    best[edge.Destination] = resulting;
                    frontier.Enqueue(resulting, resulting);
                }
            }
        }

        return IsReachable(state, startPlaceId, goalPlaceId)
            ? new CostOverflow()
            : new NoRoute();
    }

    private IEnumerable<DirectedPassage> Outgoing(GraphSpatialState state, PlaceId placeId)
    {
        foreach (PassageDefinition passage in _definition.Passages)
        {
            if (EffectiveGraph.TryResolveDirection(
                    _definition,
                    state,
                    passage,
                    placeId,
                    out PlaceId destination,
                    out bool entryAllowed) &&
                entryAllowed)
            {
                yield return new DirectedPassage(passage, destination);
            }
        }
    }

    private bool IsReachable(GraphSpatialState state, PlaceId start, PlaceId goal)
    {
        var visited = new HashSet<PlaceId> { start };
        var pending = new Queue<PlaceId>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out PlaceId current))
        {
            foreach (DirectedPassage edge in Outgoing(state, current))
            {
                if (edge.Destination == goal)
                {
                    return true;
                }

                if (visited.Add(edge.Destination))
                {
                    pending.Enqueue(edge.Destination);
                }
            }
        }

        return false;
    }

    private sealed record DirectedPassage(PassageDefinition Passage, PlaceId Destination);

    private sealed record PathLabel(PlaceId PlaceId, long Cost, IReadOnlyList<RouteLeg> Legs);

    private sealed class PathLabelComparer : IComparer<PathLabel>
    {
        internal static PathLabelComparer Instance { get; } = new();

        public int Compare(PathLabel? left, PathLabel? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int pathComparison = ComparePath(left, right);
            return pathComparison != 0
                ? pathComparison
                : left.PlaceId.CompareTo(right.PlaceId);
        }

        internal int ComparePath(PathLabel left, PathLabel right)
        {
            int costComparison = left.Cost.CompareTo(right.Cost);
            if (costComparison != 0)
            {
                return costComparison;
            }

            int common = Math.Min(left.Legs.Count, right.Legs.Count);
            for (int index = 0; index < common; index++)
            {
                int legComparison = CompareLeg(left.Legs[index], right.Legs[index]);
                if (legComparison != 0)
                {
                    return legComparison;
                }
            }

            return left.Legs.Count.CompareTo(right.Legs.Count);
        }

        private static int CompareLeg(RouteLeg left, RouteLeg right)
        {
            int passageComparison = left.PassageId.CompareTo(right.PassageId);
            if (passageComparison != 0)
            {
                return passageComparison;
            }

            int fromComparison = left.FromPlaceId.CompareTo(right.FromPlaceId);
            return fromComparison != 0
                ? fromComparison
                : left.ToPlaceId.CompareTo(right.ToPlaceId);
        }
    }
}

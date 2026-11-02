namespace DramaBoard.Experiments.PredictiveTimeParallelism.Influence;

public sealed record InfluenceEdge(int From, int To, int MinimumDelay);
public sealed record InfluenceSeed(int Node, int EarliestOffset);

// Nodes represent semantic keys, including shared resources and remote relations.
public sealed class InfluenceGraph {
    public int NodeCount { get; }
    public long Generation { get; }
    public bool MechanismsComplete { get; }
    public IReadOnlyList<InfluenceEdge> Edges { get; }

    public InfluenceGraph(int nodeCount, IEnumerable<InfluenceEdge> edges, long generation = 0, bool mechanismsComplete = true) {
        if (nodeCount <= 0) throw new ArgumentOutOfRangeException(nameof(nodeCount));
        var copy = edges.ToArray();
        if (copy.Any(e => e.From < 0 || e.From >= nodeCount || e.To < 0 || e.To >= nodeCount || e.MinimumDelay < 0))
            throw new ArgumentException("Edges require valid nodes and nonnegative delay bounds.", nameof(edges));
        NodeCount = nodeCount;
        Edges = Array.AsReadOnly(copy);
        Generation = generation;
        MechanismsComplete = mechanismsComplete;
    }
}

public sealed record InfluenceEnvelope(bool Unknown, string? Reason, long Generation, int Horizon, long?[] EarliestOffsets) {
    // Half open: an arrival at the target tick cannot certify that target's input.
    public bool ProvesNoArrivalBefore(int node, long exclusiveOffset) => !Unknown && exclusiveOffset >= 0 && exclusiveOffset <= Horizon &&
        (EarliestOffsets[node] is not long arrival || arrival >= exclusiveOffset);
}

public static class InfluenceEstimator {
    public static InfluenceEnvelope Outward(InfluenceGraph graph, IEnumerable<InfluenceSeed> seeds, int horizon, long expectedGeneration, bool useDelays) =>
        Estimate(graph, seeds, horizon, expectedGeneration, useDelays, reverse: false);

    // For each possible external source, lower bound to any selected input/wake key.
    public static InfluenceEnvelope Inward(InfluenceGraph graph, IEnumerable<int> targets, int horizon, long expectedGeneration, bool useDelays) =>
        Estimate(graph, targets.Select(n => new InfluenceSeed(n, 0)), horizon, expectedGeneration, useDelays, reverse: true);

    private static InfluenceEnvelope Estimate(InfluenceGraph graph, IEnumerable<InfluenceSeed> seeds, int horizon, long expectedGeneration, bool useDelays, bool reverse) {
        if (horizon < 0) throw new ArgumentOutOfRangeException(nameof(horizon));
        var seedArray = seeds.ToArray();
        if (seedArray.Any(s => s.Node < 0 || s.Node >= graph.NodeCount || s.EarliestOffset < 0))
            throw new ArgumentException("Invalid influence seed.", nameof(seeds));
        if (!graph.MechanismsComplete || graph.Generation != expectedGeneration) {
            return new(true, graph.Generation != expectedGeneration ? "topology-generation-mismatch" : "incomplete-mechanism-contract", graph.Generation, horizon,
                Enumerable.Repeat<long?>(0, graph.NodeCount).ToArray());
        }
        var distance = new long?[graph.NodeCount];
        var queue = new PriorityQueue<int, long>();
        foreach (var seed in seedArray) {
            if (seed.EarliestOffset > horizon || distance[seed.Node] is long previous && previous <= seed.EarliestOffset) continue;
            distance[seed.Node] = seed.EarliestOffset;
            queue.Enqueue(seed.Node, seed.EarliestOffset);
        }
        while (queue.TryDequeue(out int node, out long time)) {
            if (distance[node] != time) continue;
            foreach (var edge in graph.Edges) {
                if ((reverse ? edge.To : edge.From) != node) continue;
                int next = reverse ? edge.From : edge.To;
                long arrival = time + (useDelays ? edge.MinimumDelay : 0);
                if (arrival > horizon || distance[next] is long old && old <= arrival) continue;
                distance[next] = arrival;
                queue.Enqueue(next, arrival);
            }
        }
        return new(false, null, graph.Generation, horizon, distance);
    }
}

public static class InfluenceExperiments {
    public static object Run() {
        long queries = 0, reachable = 0, coarseMembership = 0, timedMembership = 0, coarseFalsePositive = 0, timedFalsePositive = 0;
        long conservativeChecks = 0, strictImprovements = 0;
        // Every directed three-node graph; absent / zero / one tick minimum.
        var pairs = (from a in Enumerable.Range(0, 3) from b in Enumerable.Range(0, 3) where a != b select (a, b)).ToArray();
        for (int encoding = 0; encoding < 729; encoding++) {
            int digits = encoding;
            var edges = new List<InfluenceEdge>();
            foreach (var (a, b) in pairs) {
                int choice = digits % 3;
                digits /= 3;
                if (choice > 0) edges.Add(new(a, b, choice - 1));
            }
            var graph = new InfluenceGraph(3, edges);
            for (int slack = 0; slack <= 1; slack++) {
                for (int source = 0; source < 3; source++) {
                    for (int horizon = 0; horizon <= 4; horizon++) {
                        var seeds = new[] { new InfluenceSeed(source, 0) };
                        var actual = EnumerateEvents(graph, seeds, horizon, slack);
                        var coarse = InfluenceEstimator.Outward(graph, seeds, horizon, 0, false);
                        var timed = InfluenceEstimator.Outward(graph, seeds, horizon, 0, true);
                        queries++;
                        for (int target = 0; target < 3; target++) {
                            if (actual[target] is not null) reachable++;
                            if (coarse.EarliestOffsets[target] is not null) coarseMembership++;
                            if (timed.EarliestOffsets[target] is not null) timedMembership++;
                            if (actual[target] is null && coarse.EarliestOffsets[target] is not null) coarseFalsePositive++;
                            if (actual[target] is null && timed.EarliestOffsets[target] is not null) timedFalsePositive++;
                            if (coarse.EarliestOffsets[target] is not null && timed.EarliestOffsets[target] is null) strictImprovements++;
                            RequireLowerBound(coarse.EarliestOffsets[target], actual[target]);
                            RequireLowerBound(timed.EarliestOffsets[target], actual[target]);
                            // Independently enumerate forward from each source; reverse query must agree.
                            var inward = InfluenceEstimator.Inward(graph, new[] { target }, horizon, 0, true);
                            RequireLowerBound(inward.EarliestOffsets[source], actual[target]);
                            if (inward.EarliestOffsets[source] != timed.EarliestOffsets[target]) throw new InvalidOperationException("Reverse mismatch.");
                            conservativeChecks += 3;
                        }
                    }
                }
            }
        }
        var shared = new InfluenceGraph(4, new[] { new InfluenceEdge(0, 2, 0), new InfluenceEdge(1, 2, 0), new InfluenceEdge(2, 3, 0), new InfluenceEdge(3, 0, 0) });
        var zero = InfluenceEstimator.Outward(shared, new[] { new InfluenceSeed(1, 0) }, 0, 0, true);
        if (zero.EarliestOffsets.Any(x => x != 0)) throw new InvalidOperationException("Zero-time shared-resource closure failed.");
        var queued = InfluenceEstimator.Outward(shared, new[] { new InfluenceSeed(2, 2) }, 2, 0, true);
        var queuedOracle = EnumerateEvents(shared, new[] { new InfluenceSeed(2, 2) }, 2, 0);
        if (!queued.EarliestOffsets.SequenceEqual(queuedOracle)) throw new InvalidOperationException("Queued event mismatch.");
        var unknown = InfluenceEstimator.Outward(new InfluenceGraph(3, Array.Empty<InfluenceEdge>(), mechanismsComplete: false), new[] { new InfluenceSeed(0, 0) }, 2, 0, true);
        var stale = InfluenceEstimator.Inward(shared, new[] { 3 }, 2, 1, true);
        if (!unknown.Unknown || !stale.Unknown || unknown.EarliestOffsets.Any(x => x != 0) || unknown.ProvesNoArrivalBefore(1, 1))
            throw new InvalidOperationException("Unknown must deny safety certificates.");
        var boundary = InfluenceEstimator.Outward(new InfluenceGraph(2, new[] { new InfluenceEdge(0, 1, 2) }), new[] { new InfluenceSeed(0, 0) }, 2, 0, true);
        if (!boundary.ProvesNoArrivalBefore(1, 2) || boundary.ProvesNoArrivalBefore(1, 3)) throw new InvalidOperationException("Half-open boundary mismatch.");
        return new { Graphs = 729, DelaySlackVariants = 2, Horizons = new[] { 0, 1, 2, 3, 4 }, Queries = queries, ConservativeChecks = conservativeChecks,
            OracleReachableMembership = reachable, CoarseMembership = coarseMembership, TimedMembership = timedMembership,
            CoarseFalsePositiveMembership = coarseFalsePositive, TimedFalsePositiveMembership = timedFalsePositive,
            TimedExcludedCoarseMembership = strictImprovements, ZeroDelaySharedResource = true, QueuedArrival = true, UnknownFallback = true, GenerationInvalidation = true,
            HalfOpenBoundary = true, Failures = 0 };
    }

    private static void RequireLowerBound(long? estimated, long? actual) {
        if (actual is long arrival && (estimated is not long lower || lower > arrival)) throw new InvalidOperationException("Influence under-approximation.");
    }

    // Independent finite event-state enumeration: explores every (node,tick), not
    // Dijkstra, reverse edges, shortest-path pruning, or estimator memberships.
    private static long?[] EnumerateEvents(InfluenceGraph graph, IEnumerable<InfluenceSeed> seeds, int horizon, int slack) {
        var events = new bool[graph.NodeCount, horizon + 1];
        foreach (var seed in seeds) if (seed.EarliestOffset <= horizon) events[seed.Node, seed.EarliestOffset] = true;
        bool changed;
        do {
            changed = false;
            for (int tick = 0; tick <= horizon; tick++) {
                foreach (var edge in graph.Edges) {
                    int arrival = tick + edge.MinimumDelay + slack;
                    if (arrival <= horizon && events[edge.From, tick] && !events[edge.To, arrival]) {
                        events[edge.To, arrival] = true;
                        changed = true;
                    }
                }
            }
        } while (changed);
        var earliest = new long?[graph.NodeCount];
        for (int node = 0; node < graph.NodeCount; node++) {
            for (int tick = 0; tick <= horizon; tick++) {
                if (events[node, tick]) { earliest[node] = tick; break; }
            }
        }
        return earliest;
    }
}

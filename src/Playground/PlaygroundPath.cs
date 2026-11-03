using Atelia.DurableGraph;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.PathStep", 1)]
public sealed partial record PlaygroundPathStep(
    [field: DurableField(1)] string FromPlaceId,
    [field: DurableField(2)] string EntranceId,
    [field: DurableField(3)] string ToPlaceId,
    [field: DurableField(4)] long DurationTicks) : IDurableObject;

/// <summary>An immutable personal query result, also suitable for retaining an active navigation route.</summary>
[DurableType("DramaBoard.Playground.Path", 1)]
public sealed partial record PlaygroundPath(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string ActorId,
    [field: DurableField(3)] long ContextSequence,
    [field: DurableField(4)] long OperationSequence,
    [field: DurableField(5)] string OriginPlaceId,
    [field: DurableField(6)] string TargetPlaceId,
    IReadOnlyList<PlaygroundPathStep> Steps,
    [field: DurableField(8)] long TotalTicks) : IDurableObject {
    [DurableField(7)] private List<PlaygroundPathStep> _steps = new(Steps);
    public IReadOnlyList<PlaygroundPathStep> Steps { get => _steps.AsReadOnly(); init => _steps = new(value); }
    internal static bool Same(PlaygroundPath? a, PlaygroundPath? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        a.Id == b.Id && a.ActorId == b.ActorId && a.ContextSequence == b.ContextSequence && a.OperationSequence == b.OperationSequence &&
        a.OriginPlaceId == b.OriginPlaceId && a.TargetPlaceId == b.TargetPlaceId && a.TotalTicks == b.TotalTicks && a.Steps.SequenceEqual(b.Steps);
}

/// <summary>A* with h=0 over the supplied directed personal adjacency, never the world's hidden graph.</summary>
internal static class PlaygroundPathSearch {
    internal static (IReadOnlyList<PlaygroundPathStep>? Steps, bool Overflow) Find(
        IReadOnlyList<PlaygroundKnownPlace> map, string origin, string target) {
        var nodes = map.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var distances = new Dictionary<string, Int128>(StringComparer.Ordinal) { [origin] = 0 };
        var previous = new Dictionary<string, PlaygroundPathStep>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, (Int128 Cost, long Order)>();
        long order = 0;
        queue.Enqueue(origin, (0, order++));
        while (queue.TryDequeue(out string? place, out var priority)) {
            if (priority.Cost != distances[place]) { continue; }
            if (place == target) {
                if (priority.Cost > long.MaxValue) { return (null, true); }
                var steps = new List<PlaygroundPathStep>();
                for (string cursor = target; cursor != origin;) {
                    PlaygroundPathStep step = previous[cursor]; steps.Add(step); cursor = step.FromPlaceId;
                }
                steps.Reverse(); return (Array.AsReadOnly(steps.ToArray()), false);
            }
            // Ordinal entrance order plus a stable insertion counter decides ties. Equal-cost
            // discoveries never replace the first route; no whole-path comparison protocol is needed.
            foreach (PlaygroundKnownEntrance entrance in nodes[place].Entrances.OrderBy(e => e.Id, StringComparer.Ordinal)) {
                if (entrance.KnownDestinationId is not { } destination || !nodes.ContainsKey(destination)) { continue; }
                if (entrance.DurationTicks <= 0) { throw new InvalidOperationException("Search costs must be positive."); }
                // Wide checked addition lets an overflowing branch coexist with a representable route.
                Int128 cost = checked(priority.Cost + entrance.DurationTicks);
                if (distances.TryGetValue(destination, out Int128 known) && known <= cost) { continue; }
                distances[destination] = cost;
                previous[destination] = new(place, entrance.Id, destination, entrance.DurationTicks);
                queue.Enqueue(destination, (cost, checked(order++)));
            }
        }
        return (null, false);
    }
}

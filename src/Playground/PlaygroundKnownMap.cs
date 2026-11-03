using Atelia.DurableGraph;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.KnownEntrance", 1)]
public sealed partial record PlaygroundKnownEntrance(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Description,
    [field: DurableField(3)] long DurationTicks,
    [field: DurableField(4)] string? KnownDestinationId,
    [field: DurableField(5)] string? KnownDestinationName) : IDurableObject;

[DurableType("DramaBoard.Playground.KnownPlace", 1)]
public sealed partial record PlaygroundKnownPlace(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Name,
    IReadOnlyList<PlaygroundKnownEntrance> Entrances) : IDurableObject {
    [DurableField(3)] private List<PlaygroundKnownEntrance> _entrances = new(Entrances);
    public IReadOnlyList<PlaygroundKnownEntrance> Entrances { get => _entrances.AsReadOnly(); init => _entrances = new(value); }
}

/// <summary>Pure personal map projection. Visits reveal local entrances; only confirmed passages reveal their endpoints.</summary>
internal static class PlaygroundKnownMap {
    internal static IReadOnlyList<PlaygroundKnownPlace> Observe(PlaygroundBusinessState state, string actorId) {
        if (state.Expedition is not { } expedition) { return []; }
        return Array.AsReadOnly(expedition.Visits.Where(v => v.ActorId == actorId).Select(v => v.PlaceId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(place => new PlaygroundKnownPlace(
                place, state.Definition.GetPlace(place).Name,
                Array.AsReadOnly(state.Definition.GetEntrances(place).OrderBy(p => p.EntranceId, StringComparer.Ordinal)
                    .Select(p => new PlaygroundKnownEntrance(p.EntranceId, p.Description, p.DurationTicks,
                        expedition.KnowsPassage(actorId, p.Id) ? p.To : null,
                        expedition.KnowsPassage(actorId, p.Id) ? state.Definition.GetPlace(p.To).Name : null)).ToArray()))).ToArray());
    }
    internal static bool Same(IReadOnlyList<PlaygroundKnownPlace> a, IReadOnlyList<PlaygroundKnownPlace> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.Entrances.SequenceEqual(pair.Second.Entrances));
}

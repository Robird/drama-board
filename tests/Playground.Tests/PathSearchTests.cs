namespace DramaBoard.Playground.Tests;

public sealed class PathSearchTests {
    [Fact]
    public void Directed_known_adjacency_does_not_invent_reverse_edges_or_unknown_shortcuts() {
        PlaygroundKnownPlace[] map = [
            Node("a", Edge("e-z", "b", 5), Edge("e-hidden", null, 1)),
            Node("b", Edge("e-next", "c", 4)), Node("c"), Node("isolated")
        ];
        var result = PlaygroundPathSearch.Find(map, "a", "c");
        Assert.Equal(new[] { "e-z", "e-next" }, result.Steps!.Select(s => s.EntranceId));
        Assert.Null(PlaygroundPathSearch.Find(map, "c", "a").Steps);
        Assert.Null(PlaygroundPathSearch.Find(map, "a", "isolated").Steps);
        Assert.Empty(PlaygroundPathSearch.Find(map, "a", "a").Steps!);
    }

    [Fact]
    public void Parallel_edges_cycles_and_equal_cost_paths_have_stable_minimal_selection() {
        PlaygroundKnownPlace[] map = [
            Node("a", Edge("e-z", "c", 2), Edge("e-b", "b", 2), Edge("e-a", "b", 2), Edge("e-long", "d", 20)),
            Node("b", Edge("e-return", "a", 1), Edge("e-bd", "d", 3)),
            Node("c", Edge("e-cd", "d", 3)), Node("d")
        ];
        for (int repeat = 0; repeat < 3; repeat++) {
            var result = PlaygroundPathSearch.Find(repeat % 2 == 0 ? map : map.Reverse().ToArray(), "a", "d");
            Assert.Equal(new[] { "e-a", "e-bd" }, result.Steps!.Select(s => s.EntranceId));
            Assert.Equal(5, result.Steps!.Sum(s => s.DurationTicks));
        }
    }

    [Fact]
    public void Overflowing_branch_does_not_hide_a_representable_route_and_overflow_is_distinct_from_no_route() {
        PlaygroundKnownPlace[] map = [Node("a", Edge("e-wide", "b", long.MaxValue), Edge("e-safe", "c", 4)),
            Node("b", Edge("e-over", "d", 1)), Node("c", Edge("e-finish", "d", 4)), Node("d")];
        var safe = PlaygroundPathSearch.Find(map, "a", "d");
        Assert.False(safe.Overflow);
        Assert.Equal(new[] { "e-safe", "e-finish" }, safe.Steps!.Select(s => s.EntranceId));
        var overflow = PlaygroundPathSearch.Find(map.Where(p => p.Id != "c").ToArray(), "a", "d");
        Assert.True(overflow.Overflow);
        Assert.Null(overflow.Steps);
        Assert.False(PlaygroundPathSearch.Find(map, "d", "a").Overflow);
    }

    private static PlaygroundKnownPlace Node(string id, params PlaygroundKnownEntrance[] edges) => new(id, id, edges);
    private static PlaygroundKnownEntrance Edge(string id, string? to, long cost) => new(id, id, cost, to, to);
}

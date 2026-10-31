using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public static class TownGameFactory {
    public static (TownWorld World, KernelCursor Cursor) Create(int durationHours = 48) {
        if (durationHours <= 0 || durationHours > 24 * 30) { throw new ArgumentOutOfRangeException(nameof(durationHours)); }
        TownCharacter[] characters = new[] { ("a", "阿青"), ("b", "阿白"), ("c", "阿岚") }.Select(pair => new TownCharacter(
            pair.Item1, pair.Item2, new ItemInventory(new Dictionary<string, int> { ["meal"] = 1 }), 18,
            60 * TownContent.Hour, 60 * TownContent.Hour, new GoalSet())).ToArray();
        var spatial = GraphSpatialState.Create(TownContent.Graph, characters.Select(actor => new EntityPlacement(new(actor.Id), new("market"))));
        var business = new TownBusinessState(ModelTime.Zero, new(durationHours * TownContent.Hour), characters,
            TownMarket.Fresh(new(TownContent.Day)), new(54, new Dictionary<string, long> { ["meal"] = 3 }), spatial);
        var world = TownEngine.Rebuild(business);
        return (world, new(new WorldVersion(1, 0), ModelTime.Zero, null, null));
    }
}

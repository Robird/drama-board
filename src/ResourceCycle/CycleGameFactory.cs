using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Constructs validated new-game boundaries for either memory or durable histories.</summary>
/// <remarks>Does not load, revise or publish saves. Default mutable subgraphs belong to each
/// invocation. Supplied entity instances are retained, not cloned; callers must not mutate
/// them after handing the world to a history. Input sequences are copied.</remarks>
public static class CycleGameFactory {
    /// <summary>Creates the scripted resource-cycle experiment and its initial cursor.</summary>
    public static (CycleWorld World, KernelCursor Cursor) CreateBatch(ModelTime? start = null,
        IEnumerable<CycleDestruction>? script = null) {
        ModelTime time = start ?? CycleWorld.DefaultStart;
        var entries = (script ?? DefaultScript(time)).OrderBy(e => e.Due).ThenBy(e => e.ResourceId, StringComparer.Ordinal);
        return Prepare(new(time, new(CycleTime.IsDay(time), CycleTime.NextLightChange(time)),
            DefaultResources(time), new(entries)));
    }

    /// <summary>Creates the gathering scenario and its initial cursor.</summary>
    public static (CycleWorld World, KernelCursor Cursor) CreatePlayable(ModelTime? start = null,
        IEnumerable<CycleCharacter>? characters = null, IEnumerable<CycleResource>? resources = null) {
        ModelTime time = start ?? CycleWorld.DefaultStart;
        return Prepare(new(time, new(CycleTime.IsDay(time), CycleTime.NextLightChange(time)),
            resources ?? DefaultResources(time), new([]), characters ?? [new("a", "阿青"), new("b", "阿白", 1), new("c", "阿岚", 2)]));
    }

    private static (CycleWorld World, KernelCursor Cursor) Prepare(CycleBusinessState business) {
        CycleWorld world = CycleEngine.Rebuild(business, business.Time);
        var cursor = new KernelCursor(new WorldVersion(1, 0), world.Time, null, null);
        return (world, cursor);
    }

    private static IEnumerable<CycleResource> DefaultResources(ModelTime time) => [
        new("sunflower", "太阳花", "item.sunflower", true, 3 * CycleTime.Hour, 5, 3, CycleTime.IsDay(time) ? time : null),
        new("moongrass", "月光草", "item.moongrass", false, 4 * CycleTime.Hour, 5, 3, CycleTime.IsDay(time) ? null : time)
    ];

    private static IEnumerable<CycleDestruction> DefaultScript(ModelTime start) {
        yield return new("sunflower", new(checked(start.Ticks + 7 * CycleTime.Hour)));
        yield return new("moongrass", new(checked(start.Ticks + 14 * CycleTime.Hour)));
        yield return new("sunflower", new(checked(start.Ticks + 17 * CycleTime.Hour)));
        yield return new("moongrass", new(checked(start.Ticks + 30 * CycleTime.Hour)));
        yield return new("sunflower", new(checked(start.Ticks + 47 * CycleTime.Hour)));
    }
}

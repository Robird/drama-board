using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using DramaBoard.Inventory;

namespace DramaBoard.ResourceCycle;

/// <summary>Constructs validated new-game boundaries for either memory or durable histories.</summary>
/// <remarks>Does not load, revise or publish saves. Default mutable subgraphs belong to each
/// invocation. Supplied entity instances are retained, not cloned; callers must not mutate
/// them after handing the world to a history. Input sequences are copied.</remarks>
public static class CycleGameFactory {
    /// <summary>Creates a fixed, revision-bound mountain scenario with one explorer and no revealed distant truth.</summary>
    public static (CycleWorld World, KernelCursor Cursor) CreateSurvival(ModelTime? start = null, bool enableNeeds = true,
        bool enableWeather = true, ulong weatherSeed = CycleEnvironmentRules.DefaultSeed) {
        ModelTime time = start ?? new ModelTime(15 * CycleTime.Hour);
        CycleResource[] resources = [
            new("berries", "野浆果", "item.food", true, 3 * CycleTime.Hour, 6, 6, harvestTicks: CycleTime.Hour / 2),
            new("water", "溪水", "item.water", true, CycleTime.Hour, 12, 12, harvestTicks: CycleTime.Hour / 4),
            new("wood", "干木", "item.wood", true, 6 * CycleTime.Hour, 6, 6, harvestTicks: CycleTime.Hour / 2),
            new("ridge-herbs", "山脊野菜", "item.food", true, 4 * CycleTime.Hour, 4, 4, harvestTicks: CycleTime.Hour / 2)
        ];
        GraphSpatialState spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new("camp"))]);
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial, [new("a", "camp", time, [])],
            environment: enableNeeds && enableWeather ? new(weatherSeed, 0,
                time.Ticks > long.MaxValue - CycleEnvironmentRules.IntervalTicks ? null : new(time.Ticks + CycleEnvironmentRules.IntervalTicks)) : null);
        return Prepare(new(time, new(CycleTime.IsDay(time), CycleTime.NextLightChange(time)), resources,
            new([]), [new("a", "阿青", backpack: enableNeeds ? new ItemInventory(new Dictionary<string, int> {
                ["item.water"] = 1
            }) : null, survival: enableNeeds ? CycleSurvival.Create(time, food: 30, water: 45, warmth: 60) : null)], expedition: expedition));
    }

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

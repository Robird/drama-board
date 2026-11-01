using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Authored initial content for Hillside. Each call creates independent mutable subgraphs.</summary>
/// <remarks>Map definitions live in CycleExpeditionMap. These are finite starting possessions,
/// not replenishment performed when a character returns to town.</remarks>
public static class LivingWorldContent {
    public const string Name = "山野镇";
    public const string StartingPlace = "home";
    public const long StartingTicks = 8 * CycleTime.Hour;

    public static CycleResource[] CreateResources() => [
        new("berries", "野浆果", "item.food", true, CycleTime.Hour, 12, 12, harvestTicks: CycleTime.Hour / 2),
        new("water", "溪水", "item.water", true, CycleTime.Hour / 2, 24, 24, harvestTicks: CycleTime.Hour / 4),
        new("wood", "干木", "item.wood", true, 6 * CycleTime.Hour, 6, 6, harvestTicks: CycleTime.Hour / 2),
        new("ridge-herbs", "山脊野菜", "item.food", true, 2 * CycleTime.Hour, 8, 8, harvestTicks: CycleTime.Hour / 2),
        new("well", "井台储水", "item.water", true, CycleTime.Hour / 2, 24, 24, harvestTicks: CycleTime.Hour / 4)
    ];

    public static CycleCharacter[] CreateCharacters(ModelTime at, int count = 3, bool enableNeeds = true) {
        if (count is < 1 or > 3) { throw new ArgumentOutOfRangeException(nameof(count)); }
        string[] ids = ["a", "b", "c"];
        string[] names = ["阿青", "阿白", "阿岚"];
        return Enumerable.Range(0, count).Select(index => new CycleCharacter(ids[index], names[index],
            backpack: new ItemInventory(new Dictionary<string, int> { ["item.food"] = 3, ["item.water"] = 3 }),
            survival: enableNeeds ? CycleSurvival.Create(at, food: 80, water: 80, warmth: 80,
                warmthRate: CycleSurvivalRules.WarmthRate(true, CycleTime.IsDay(at))) : null, money: 12)).ToArray();
    }
}

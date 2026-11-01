using System.Collections.ObjectModel;
using Atelia.DurableGraph;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.Recipe", 1)]
public sealed partial record CycleRecipe(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string InputItem,
    [field: DurableField(3)] int InputCount,
    [field: DurableField(4)] string OutputItem,
    [field: DurableField(5)] int OutputCount,
    [field: DurableField(6)] long DurationTicks) : IDurableObject;

/// <summary>The initial small-world economy. Prices and limits are scenario content, not external assets.</summary>
public static class LivingWorldEconomyContent {
    public static IReadOnlyDictionary<string, string> Items { get; } = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["item.food"] = "食物", ["item.water"] = "饮水", ["item.wood"] = "干木",
            ["item.ration"] = "口粮", ["item.woodware"] = "木器"
        });
    public static IReadOnlyList<CycleRecipe> Recipes { get; } = Array.AsReadOnly(new[] {
        new CycleRecipe("ration", "item.food", 2, "item.ration", 2, CycleTime.Hour),
        new CycleRecipe("woodware", "item.wood", 2, "item.woodware", 1, 2 * CycleTime.Hour)
    });
    public static IReadOnlyDictionary<string, int> MarketStock { get; } = new ReadOnlyDictionary<string, int>(
        new Dictionary<string, int>(StringComparer.Ordinal) { ["item.food"] = 36, ["item.water"] = 60 });
    public static IReadOnlyDictionary<string, int> MarketDemand { get; } = new ReadOnlyDictionary<string, int>(
        new Dictionary<string, int>(StringComparer.Ordinal) { ["item.ration"] = 18, ["item.woodware"] = 12, ["item.wood"] = 12 });
    public static CycleRecipe? Recipe(string id) => Recipes.FirstOrDefault(recipe => recipe.Id == id);
    public static int BuyPrice(string item) => item switch { "item.food" => 3, "item.water" => 1, _ => 0 };
    public static int SellPrice(string item) => item switch { "item.ration" => 5, "item.woodware" => 14, "item.wood" => 3, _ => 0 };
    public static int SupplyFood(string item) => item switch { "item.food" => 35, "item.ration" => 55, _ => 0 };
    public static int SupplyWater(string item) => item == "item.water" ? 45 : 0;
}

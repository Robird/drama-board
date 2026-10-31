using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public sealed record TownRecipe(string Id, string Input, int InputCount, string Output, int OutputCount, long DurationTicks);

/// <summary>Immutable scenario content. This game owns its economy and map; shared modules own their local mechanisms.</summary>
public static class TownContent {
    public const long Hour = 3_600_000, Day = 24 * Hour;
    public const int FoodDecayPerHour = 3, MealFood = 40;
    public static IReadOnlyList<string> Items { get; } = Array.AsReadOnly(new[] { "grain", "wood", "meal", "woodware" });
    public static IReadOnlyList<TownRecipe> Recipes { get; } = Array.AsReadOnly(new[] {
        new TownRecipe("meal", "grain", 2, "meal", 2, Hour),
        new TownRecipe("woodware", "wood", 2, "woodware", 1, 2 * Hour)
    });
    public static GraphDefinition Graph { get; } = GraphDefinition.Create(
        new[] { new PlaceId("market"), new PlaceId("workshop"), new PlaceId("home") },
        new[] {
            new PassageDefinition(new("market-workshop"), new("market"), new("workshop"), Hour / 4, new(true, true)),
            new PassageDefinition(new("market-home"), new("market"), new("home"), Hour / 4, new(true, true)),
            new PassageDefinition(new("home-workshop"), new("home"), new("workshop"), Hour / 4, new(true, true))
        });
    internal static SpatialSimulation Simulation { get; } = new(Graph);
    internal static SpatialPlanner Planner { get; } = new(Graph);
    internal static GraphSpatialReducer Reducer { get; } = new(Graph);
    public static TownRecipe? Recipe(string id) => Recipes.FirstOrDefault(recipe => recipe.Id == id);
    public static int BuyPrice(string id) => id switch { "grain" => 2, "wood" => 3, _ => 0 };
    public static int SellPrice(string id) => id switch { "meal" => 4, "woodware" => 12, _ => 0 };
}

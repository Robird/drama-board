using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Ordinary C# authoring entrypoints. Runtime consumers use the definition saved in their own world.</summary>
public static class CycleWorldDefinitions {
    public static CycleWorldDefinition LivingWorld(ModelTime? start = null, int characterCount = 3,
        bool enableNeeds = true, bool enableWeather = true, ulong weatherSeed = CycleEnvironmentRules.DefaultSeed) {
        if (characterCount is < 1 or > 3) { throw new ArgumentOutOfRangeException(nameof(characterCount)); }
        string[] ids = ["a", "b", "c"];
        string[] names = ["阿青", "阿白", "阿岚"];
        var characters = Enumerable.Range(0, characterCount).Select(i => new CycleInitialCharacterDefinition(
            ids[i], names[i], LivingWorldContent.StartingPlace,
            backpack: new Dictionary<string, int> { ["item.food"] = 3, ["item.water"] = 3 }));
        return Build("hillside-v1", LivingWorldContent.Name, start ?? new(LivingWorldContent.StartingTicks),
            LivingWorldContent.CreateResources(), characters, LivingWorldEconomyContent.Items, enableNeeds,
            new(enableNeeds && enableWeather, Seed: weatherSeed), economyEnabled: true);
    }

    /// <summary>Creates a selectable content preset; every override contributes to its effective identity.</summary>
    public static CycleWorldDefinition Named(string name, ModelTime? start = null, int characterCount = 3,
        bool enableNeeds = true, bool enableWeather = true, ulong weatherSeed = CycleEnvironmentRules.DefaultSeed) {
        if (name is not ("hillside" or "gentle")) { throw new ArgumentException("Unknown world definition; choose hillside or gentle.", nameof(name)); }
        CycleWorldDefinition normal = LivingWorld(start, characterCount, enableNeeds, enableWeather, weatherSeed);
        if (name == "hillside") { return normal; }
        var sell = new Dictionary<string, int>(normal.SellPrices, StringComparer.Ordinal) { ["item.ration"] = 6 };
        return normal.Change(contentId: "hillside-gentle-v1", name: "山野镇·舒缓",
            passages: normal.Passages.Select(p => p.Id == "road-camp" ? p with { DurationTicks = CycleTime.Hour / 2 } : p),
            resources: normal.Resources.Select(r => r.Id == "berries" ? r with { RegenerationTicks = CycleTime.Hour / 2 } : r),
            initialCharacters: normal.InitialCharacters.Select(c => c.Change(money: 20)),
            recipes: normal.Recipes.Select(r => r.Id == "ration" ? r with { DurationTicks = CycleTime.Hour / 2 } : r),
            sellPrices: sell, body: new(FoodPerHour: -3, WaterPerHour: -5, NightWarmthPerHour: -6),
            weather: normal.Weather with { IntervalTicks = 4 * CycleTime.Hour, ExposureLoss = 5,
                ColdProbabilityNumerator = 1, ColdProbabilityDenominator = 4 });
    }

    /// <summary>Captures legacy fixture inputs once. Gameplay never resolves content through this adapter.</summary>
    public static CycleWorldDefinition FromLegacy(ModelTime time, IEnumerable<CycleResource> resources,
        IEnumerable<CycleCharacter> characters, CycleExpedition? expedition = null, bool economyEnabled = false) {
        CycleResource[] resourceArray = resources.ToArray();
        CycleCharacter[] characterArray = characters.ToArray();
        var items = new Dictionary<string, string>(LivingWorldEconomyContent.Items, StringComparer.Ordinal);
        foreach (CycleResource resource in resourceArray) { items.TryAdd(resource.ItemId, resource.Name); }
        foreach (CycleCharacter character in characterArray) {
            foreach (string item in character.Backpack.Items.Keys) { items.TryAdd(item, item); }
        }
        var initial = characterArray.Select(actor => new CycleInitialCharacterDefinition(actor.Id, actor.Name,
            (expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actor.Id)?.Location as AtPlaceLocation)?.PlaceId.Value
                ?? (actor.Ending?.Location as AtPlaceLocation)?.PlaceId.Value ?? LivingWorldContent.StartingPlace,
            actor.SkillLevel, actor.Money,
            (int)((actor.Survival?.FoodUnits ?? 80 * CycleSurvival.Scale) / CycleSurvival.Scale),
            (int)((actor.Survival?.WaterUnits ?? 80 * CycleSurvival.Scale) / CycleSurvival.Scale),
            (int)((actor.Survival?.WarmthUnits ?? 80 * CycleSurvival.Scale) / CycleSurvival.Scale), actor.Backpack.Items, actor.Backpack.UnitCapacity));
        return Build(expedition?.ScenarioId ?? "hillside-fixture-v1", LivingWorldContent.Name, time, resourceArray, initial, items,
            characterArray.Any(c => c.Survival is not null), new(expedition?.Environment is not null,
                Seed: expedition?.Environment?.Seed ?? CycleEnvironmentRules.DefaultSeed), economyEnabled);
    }

    private static CycleWorldDefinition Build(string id, string name, ModelTime at, IEnumerable<CycleResource> resources,
        IEnumerable<CycleInitialCharacterDefinition> characters, IReadOnlyDictionary<string, string> items,
        bool needsEnabled, CycleWeatherDefinition weather, bool economyEnabled) {
        var templates = resources.Select(r => new CycleResourceDefinition(r.Id, r.Name, r.ItemId,
            CycleExpeditionMap.GetResourcePlace(r.Id) ?? LivingWorldContent.StartingPlace,
            r.GrowsInDaylight, r.RegenerationTicks, r.Capacity, r.Stock, r.HarvestTicks, r.SkillId, r.RequiredLevel));
        Dictionary<string, int> Table(Func<string, int> value) => items.Keys.Where(item => value(item) > 0)
            .ToDictionary(item => item, value, StringComparer.Ordinal);
        return new(id, name, at, CycleExpeditionMap.DefaultPlaces, CycleExpeditionMap.DefaultPassages,
            CycleExpeditionMap.Graph.Relations.Select(r => new CycleRelationDefinition(r.EndpointA.Value, r.EndpointB.Value, r.Flags)),
            templates, characters, items, LivingWorldEconomyContent.Recipes, LivingWorldEconomyContent.MarketStock,
            LivingWorldEconomyContent.MarketDemand, Table(LivingWorldEconomyContent.BuyPrice), Table(LivingWorldEconomyContent.SellPrice),
            Table(LivingWorldEconomyContent.SupplyFood), Table(LivingWorldEconomyContent.SupplyWater), new(), weather,
            needsEnabled, economyEnabled);
    }
}

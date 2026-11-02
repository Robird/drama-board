using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class HillsideContentDefinitionTests {
    [Fact]
    public void Unordered_authoring_inputs_and_relation_orientation_have_one_canonical_identity() {
        PlaygroundWorldDefinition original = PlaygroundWorldDefinitions.LivingWorld();
        PlaygroundWorldDefinition reordered = original.Change(
            places: original.Places.Reverse(), passages: original.Passages.Reverse(),
            relations: original.Relations.Reverse().Select(edge => edge with { EndpointA = edge.EndpointB, EndpointB = edge.EndpointA }),
            resources: original.Resources.Reverse(), recipes: original.Recipes.Reverse(),
            items: original.Items.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value),
            buyPrices: original.BuyPrices.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value));
        Assert.Equal(original.Fingerprint, reordered.Fingerprint);
        Assert.Equal(original.Fingerprint, original.ComputeFingerprint());
        Assert.Matches("^[0-9A-F]{64}$", original.Fingerprint);
        reordered.Validate();
    }

    [Fact]
    public void Character_service_order_descriptions_capacity_and_tuning_are_fingerprinted() {
        PlaygroundWorldDefinition original = PlaygroundWorldDefinitions.LivingWorld();
        PlaygroundWorldDefinition[] variants = [
            original.Change(initialCharacters: original.InitialCharacters.Reverse()),
            original.Change(passages: original.Passages.Select(p => p.Id == "market-home" ? p with { ReverseDescription = "另一条回家的描述" } : p)),
            original.Change(initialCharacters: original.InitialCharacters.Select((actor, index) => index == 0 ? actor.Change(unitCapacity: 20) : actor)),
            original.Change(weather: original.Weather with { Seed = original.Weather.Seed + 1 }),
            original.Change(body: original.Body with { FoodPerHour = -3 }),
            original.Change(foodSupplies: new Dictionary<string, int>(original.FoodSupplies) { ["item.food"] = 37 }),
            original.Change(marketRefreshTicks: PlaygroundTime.Day / 2)
        ];
        Assert.All(variants, variant => Assert.NotEqual(original.Fingerprint, variant.Fingerprint));
        Assert.Equal(variants.Length, variants.Select(variant => variant.Fingerprint).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Definition_copies_authoring_collections_and_exposes_no_mutation_path() {
        PlaygroundWorldDefinition original = PlaygroundWorldDefinitions.LivingWorld(characterCount: 1);
        PlaygroundPlaceDefinition[] places = original.Places.ToArray();
        var supplies = new Dictionary<string, int>(original.FoodSupplies);
        var backpack = new Dictionary<string, int> { ["item.food"] = 3 };
        PlaygroundInitialCharacterDefinition resident = original.InitialCharacters[0].Change(backpack: backpack);
        PlaygroundWorldDefinition authored = original.Change(places: places, foodSupplies: supplies, initialCharacters: [resident]);
        string fingerprint = authored.Fingerprint;
        places[0] = places[0] with { Name = "外部改名" };
        supplies["item.food"] = 99;
        backpack["item.food"] = 90;
        Assert.Equal(35, authored.SupplyFood("item.food"));
        Assert.Equal(3, authored.InitialCharacters[0].Backpack["item.food"]);
        Assert.DoesNotContain(authored.Places, place => place.Name == "外部改名");
        Assert.Throws<NotSupportedException>(() => ((IList<PlaygroundPlaceDefinition>)authored.Places)[0] = places[0]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)authored.Items)["item.food"] = "改名");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, int>)authored.InitialCharacters[0].Backpack)["item.food"] = 9);
        authored.Validate();
        Assert.Equal(fingerprint, authored.ComputeFingerprint());
    }

    [Fact]
    public void Invalid_references_cycles_and_parameters_fail_before_a_world_is_created() {
        PlaygroundWorldDefinition original = PlaygroundWorldDefinitions.LivingWorld();
        Assert.Throws<InvalidDataException>(() => original.Change(places: original.Places.Append(original.Places[0])));
        Assert.Throws<InvalidDataException>(() => original.Change(resources: original.Resources.Select((r, i) => i == 0 ? r with { PlaceId = "missing" } : r)));
        Assert.Throws<InvalidDataException>(() => original.Change(recipes: original.Recipes.Select((r, i) => i == 0 ? r with { InputItem = "item.missing" } : r)));
        Assert.Throws<InvalidDataException>(() => original.Change(passages: original.Passages.Select((p, i) => i == 0 ? p with { DurationTicks = 0 } : p)));
        Assert.Throws<InvalidDataException>(() => original.Change(weather: original.Weather with { IntervalTicks = 0 }));
        Assert.Throws<InvalidDataException>(() => original.Change(weather: original.Weather with { ColdProbabilityNumerator = 3, ColdProbabilityDenominator = 2 }));
        Assert.Throws<InvalidDataException>(() => original.Change(marketPlaceId: "missing"));
        Assert.Throws<InvalidDataException>(() => original.Change(initialCharacters: [original.InitialCharacters[0].Change(unitCapacity: 1)]));
    }

    [Fact]
    public void Per_definition_runtime_caches_and_business_replacements_preserve_bound_content() {
        PlaygroundWorldDefinition normal = PlaygroundWorldDefinitions.Named("hillside", characterCount: 1);
        PlaygroundWorldDefinition gentle = PlaygroundWorldDefinitions.Named("gentle", characterCount: 1);
        Assert.Same(normal.Runtime, normal.Runtime);
        Assert.NotSame(normal.Runtime, gentle.Runtime);
        Assert.Equal(PlaygroundTime.Hour, normal.GetPassages("road").Single(p => p.Id == "road-camp").DurationTicks);
        Assert.Equal(PlaygroundTime.Hour / 2, gentle.GetPassages("road").Single(p => p.Id == "road-camp").DurationTicks);
        Assert.Equal(PlaygroundTime.Hour, normal.Runtime.Graph.GetPassage(new("road-camp")).Length);
        Assert.Equal(PlaygroundTime.Hour / 2, gentle.Runtime.Graph.GetPassage(new("road-camp")).Length);
        PlaygroundWorld world = PlaygroundGameFactory.Create(gentle).World;
        Assert.Same(gentle, world.Definition);
        Assert.Same(gentle, world.Business.Replace(world.Time).Definition);
        Assert.Equal(20, world.GetCharacter("a").Money);
        Assert.Equal(-3, world.GetCharacter("a").Survival!.FoodPerHour);
        Assert.Equal(Hour(12), world.Expedition!.Environment!.NextDue);
    }

    [Fact]
    public void Initial_overrides_are_normalized_before_identity_and_multi_need_supplies_are_valid() {
        PlaygroundWorldDefinition normalized = PlaygroundWorldDefinitions.LivingWorld(Hour(9), characterCount: 2, enableNeeds: false, enableWeather: true, weatherSeed: 13);
        PlaygroundWorld constructed = PlaygroundGameFactory.CreateLivingWorld(Hour(9), characterCount: 2, enableNeeds: false, enableWeather: false, weatherSeed: 13).World;
        Assert.Equal(normalized.Fingerprint, constructed.Definition.Fingerprint);
        Assert.NotEqual(normalized.Fingerprint, PlaygroundWorldDefinitions.LivingWorld(Hour(9), characterCount: 1, enableNeeds: false, weatherSeed: 13).Fingerprint);
        Assert.False(normalized.Weather.Enabled);
        PlaygroundWorldDefinition combined = normalized.Change(waterSupplies: new Dictionary<string, int>(normalized.WaterSupplies) { ["item.food"] = 5 });
        Assert.Equal(35, combined.SupplyFood("item.food"));
        Assert.Equal(5, combined.SupplyWater("item.food"));
        combined.Validate();
    }
}

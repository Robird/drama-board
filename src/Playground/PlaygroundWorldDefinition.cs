using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.RelationDefinition", 1)]
public sealed partial record PlaygroundRelationDefinition(
    [field: DurableField(1)] string EndpointA,
    [field: DurableField(2)] string EndpointB,
    [field: DurableField(3)] ulong Flags) : IDurableObject;

[DurableType("DramaBoard.Playground.BodyDefinition", 1)]
public sealed partial record PlaygroundBodyDefinition(
    [field: DurableField(1)] int FoodPerHour = -5,
    [field: DurableField(2)] int WaterPerHour = -8,
    [field: DurableField(3)] int ShelteredWarmthPerHour = 12,
    [field: DurableField(4)] int DayWarmthPerHour = 6,
    [field: DurableField(5)] int NightWarmthPerHour = -12) : IDurableObject;

[DurableType("DramaBoard.Playground.WeatherDefinition", 1)]
public sealed partial record PlaygroundWeatherDefinition(
    [field: DurableField(1)] bool Enabled = true,
    [field: DurableField(2)] long IntervalTicks = 2 * PlaygroundTime.Hour,
    [field: DurableField(3)] int ExposureLoss = 10,
    [field: DurableField(4)] int ColdProbabilityNumerator = 1,
    [field: DurableField(5)] int ColdProbabilityDenominator = 2,
    [field: DurableField(6)] ulong Seed = PlaygroundEnvironmentRules.DefaultSeed) : IDurableObject;

[DurableType("DramaBoard.Playground.ResourceDefinition", 1)]
public sealed partial record PlaygroundResourceDefinition(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Name,
    [field: DurableField(3)] string ItemId,
    [field: DurableField(4)] string PlaceId,
    [field: DurableField(5)] bool GrowsInDaylight,
    [field: DurableField(6)] long RegenerationTicks,
    [field: DurableField(7)] int Capacity,
    [field: DurableField(8)] int Stock,
    [field: DurableField(9)] long HarvestTicks,
    [field: DurableField(10)] string SkillId = "herbalism",
    [field: DurableField(11)] int RequiredLevel = 0) : IDurableObject {
    public PlaygroundResource Create(ModelTime at) => new(Id, Name, ItemId, GrowsInDaylight, RegenerationTicks, Capacity, Stock,
        Stock < Capacity && GrowsInDaylight == PlaygroundTime.IsDay(at) ? at : null, harvestTicks: HarvestTicks,
        skillId: SkillId, requiredLevel: RequiredLevel);
}

[DurableType("DramaBoard.Playground.InitialCharacterDefinition", 1)]
public sealed partial class PlaygroundInitialCharacterDefinition : IDurableObject {
    [DurableField(1)] private readonly string _id;
    [DurableField(2)] private readonly string _name;
    [DurableField(3)] private readonly string _placeId;
    [DurableField(4)] private readonly int _skillLevel;
    [DurableField(5)] private readonly long _money;
    [DurableField(6)] private readonly int _food;
    [DurableField(7)] private readonly int _water;
    [DurableField(8)] private readonly int _warmth;
    [DurableField(9)] private readonly Dictionary<string, int> _backpack;
    [DurableField(10)] private readonly long? _unitCapacity;
    public string Id => _id;
    public string Name => _name;
    public string PlaceId => _placeId;
    public int SkillLevel => _skillLevel;
    public long Money => _money;
    public int Food => _food;
    public int Water => _water;
    public int Warmth => _warmth;
    public IReadOnlyDictionary<string, int> Backpack => new ReadOnlyDictionary<string, int>(_backpack);
    public long? UnitCapacity => _unitCapacity;
    public PlaygroundInitialCharacterDefinition(string id, string name, string placeId, int skillLevel = 0, long money = 12,
        int food = 80, int water = 80, int warmth = 80, IReadOnlyDictionary<string, int>? backpack = null, long? unitCapacity = null) {
        _id = id; _name = name; _placeId = placeId; _skillLevel = skillLevel; _money = money;
        _food = food; _water = water; _warmth = warmth;
        _backpack = new(backpack ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        _unitCapacity = unitCapacity;
    }
    public PlaygroundInitialCharacterDefinition Change(string? id = null, string? name = null, string? placeId = null,
        int? skillLevel = null, long? money = null, int? food = null, int? water = null, int? warmth = null,
        IReadOnlyDictionary<string, int>? backpack = null, long? unitCapacity = null, bool clearUnitCapacity = false) => new(id ?? Id, name ?? Name, placeId ?? PlaceId,
            skillLevel ?? SkillLevel, money ?? Money, food ?? Food, water ?? Water, warmth ?? Warmth, backpack ?? Backpack,
            clearUnitCapacity ? null : unitCapacity ?? UnitCapacity);
    internal void ValidateStructure() {
        if (_backpack is null || UnitCapacity < 0 || _backpack.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value <= 0) ||
            UnitCapacity is { } capacity && _backpack.Values.Sum(value => (long)value) > capacity) {
            throw new InvalidDataException("Invalid initial character inventory definition.");
        }
    }
}

/// <summary>The complete immutable authored definition, persisted once as a shared business subgraph.</summary>
/// <remarks>Canonical encoding v1 uses BinaryWriter little-endian integers and length-prefixed UTF-8 strings.
/// Unordered definitions are ordinally sorted; initial character order remains semantically significant.
/// Command-visible IDs contain no whitespace; the exact item ID "money" is reserved for currency.</remarks>
[DurableType("DramaBoard.Playground.WorldDefinition", 1)]
public sealed partial class PlaygroundWorldDefinition : IDurableObject {
    [DurableField(1)] private readonly string _contentId;
    [DurableField(2)] private readonly string _fingerprint;
    [DurableField(3)] private readonly string _name;
    [DurableField(4)] private readonly ModelTime _startTime;
    [DurableField(5)] private readonly List<PlaygroundPlaceDefinition> _places;
    [DurableField(6)] private readonly List<PlaygroundPassageDefinition> _passages;
    [DurableField(7)] private readonly List<PlaygroundRelationDefinition> _relations;
    [DurableField(8)] private readonly List<PlaygroundResourceDefinition> _resources;
    [DurableField(9)] private readonly List<PlaygroundInitialCharacterDefinition> _initialCharacters;
    [DurableField(10)] private readonly Dictionary<string, string> _items;
    [DurableField(11)] private readonly List<PlaygroundRecipe> _recipes;
    [DurableField(12)] private readonly Dictionary<string, int> _marketStock;
    [DurableField(13)] private readonly Dictionary<string, int> _marketDemand;
    [DurableField(14)] private readonly Dictionary<string, int> _buyPrices;
    [DurableField(15)] private readonly Dictionary<string, int> _sellPrices;
    [DurableField(16)] private readonly Dictionary<string, int> _foodSupplies;
    [DurableField(17)] private readonly Dictionary<string, int> _waterSupplies;
    [DurableField(18)] private readonly PlaygroundBodyDefinition _body;
    [DurableField(19)] private readonly PlaygroundWeatherDefinition _weather;
    [DurableField(20)] private readonly bool _needsEnabled;
    [DurableField(21)] private readonly bool _economyEnabled;
    [DurableField(22)] private readonly string _marketPlaceId;
    [DurableField(23)] private readonly string _workshopPlaceId;
    [DurableField(24)] private readonly long _marketRefreshTicks;
    private static readonly ConditionalWeakTable<PlaygroundWorldDefinition, PlaygroundDefinitionRuntime> Runtimes = new();
    public string ContentId => _contentId;
    public string Fingerprint => _fingerprint;
    public string Name => _name;
    public ModelTime StartTime => _startTime;
    public IReadOnlyList<PlaygroundPlaceDefinition> Places => _places.AsReadOnly();
    public IReadOnlyList<PlaygroundPassageDefinition> Passages => _passages.AsReadOnly();
    public IReadOnlyList<PlaygroundRelationDefinition> Relations => _relations.AsReadOnly();
    public IReadOnlyList<PlaygroundResourceDefinition> Resources => _resources.AsReadOnly();
    public IReadOnlyList<PlaygroundInitialCharacterDefinition> InitialCharacters => _initialCharacters.AsReadOnly();
    public IReadOnlyDictionary<string, string> Items => new ReadOnlyDictionary<string, string>(_items);
    public IReadOnlyList<PlaygroundRecipe> Recipes => _recipes.AsReadOnly();
    public IReadOnlyDictionary<string, int> MarketStock => Read(_marketStock);
    public IReadOnlyDictionary<string, int> MarketDemand => Read(_marketDemand);
    public IReadOnlyDictionary<string, int> BuyPrices => Read(_buyPrices);
    public IReadOnlyDictionary<string, int> SellPrices => Read(_sellPrices);
    public IReadOnlyDictionary<string, int> FoodSupplies => Read(_foodSupplies);
    public IReadOnlyDictionary<string, int> WaterSupplies => Read(_waterSupplies);
    public PlaygroundBodyDefinition Body => _body;
    public PlaygroundWeatherDefinition Weather => _weather;
    public bool NeedsEnabled => _needsEnabled;
    public bool EconomyEnabled => _economyEnabled;
    public string MarketPlaceId => _marketPlaceId;
    public string WorkshopPlaceId => _workshopPlaceId;
    public long MarketRefreshTicks => _marketRefreshTicks;
    internal PlaygroundDefinitionRuntime Runtime => Runtimes.GetValue(this, static definition => new(definition));

    public PlaygroundWorldDefinition(string contentId, string name, ModelTime startTime,
        IEnumerable<PlaygroundPlaceDefinition> places, IEnumerable<PlaygroundPassageDefinition> passages,
        IEnumerable<PlaygroundRelationDefinition> relations, IEnumerable<PlaygroundResourceDefinition> resources,
        IEnumerable<PlaygroundInitialCharacterDefinition> initialCharacters, IReadOnlyDictionary<string, string> items,
        IEnumerable<PlaygroundRecipe> recipes, IReadOnlyDictionary<string, int> marketStock, IReadOnlyDictionary<string, int> marketDemand,
        IReadOnlyDictionary<string, int> buyPrices, IReadOnlyDictionary<string, int> sellPrices,
        IReadOnlyDictionary<string, int> foodSupplies, IReadOnlyDictionary<string, int> waterSupplies,
        PlaygroundBodyDefinition body, PlaygroundWeatherDefinition weather, bool needsEnabled = true, bool economyEnabled = true,
        string marketPlaceId = "market", string workshopPlaceId = "workshop", long marketRefreshTicks = PlaygroundTime.Day) {
        _contentId = contentId; _name = name; _startTime = startTime;
        _places = places.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        _passages = passages.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        _relations = relations.Select(r => StringComparer.Ordinal.Compare(r.EndpointA, r.EndpointB) < 0
            ? r : new PlaygroundRelationDefinition(r.EndpointB, r.EndpointA, r.Flags))
            .OrderBy(r => r.EndpointA, StringComparer.Ordinal).ThenBy(r => r.EndpointB, StringComparer.Ordinal).ToList();
        _resources = resources.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        _initialCharacters = initialCharacters.ToList();
        _items = new(items, StringComparer.Ordinal);
        _recipes = recipes.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        _marketStock = Copy(marketStock); _marketDemand = Copy(marketDemand); _buyPrices = Copy(buyPrices);
        _sellPrices = Copy(sellPrices); _foodSupplies = Copy(foodSupplies); _waterSupplies = Copy(waterSupplies);
        _body = body; _weather = weather; _needsEnabled = needsEnabled; _economyEnabled = economyEnabled;
        _marketPlaceId = marketPlaceId; _workshopPlaceId = workshopPlaceId; _marketRefreshTicks = marketRefreshTicks;
        ValidateStructure();
        _fingerprint = ComputeFingerprint();
    }

    public PlaygroundWorldDefinition Change(string? contentId = null, string? name = null, ModelTime? startTime = null,
        IEnumerable<PlaygroundPlaceDefinition>? places = null, IEnumerable<PlaygroundPassageDefinition>? passages = null,
        IEnumerable<PlaygroundRelationDefinition>? relations = null, IEnumerable<PlaygroundResourceDefinition>? resources = null,
        IEnumerable<PlaygroundInitialCharacterDefinition>? initialCharacters = null, IReadOnlyDictionary<string, string>? items = null,
        IEnumerable<PlaygroundRecipe>? recipes = null, IReadOnlyDictionary<string, int>? marketStock = null,
        IReadOnlyDictionary<string, int>? marketDemand = null, IReadOnlyDictionary<string, int>? buyPrices = null,
        IReadOnlyDictionary<string, int>? sellPrices = null, IReadOnlyDictionary<string, int>? foodSupplies = null,
        IReadOnlyDictionary<string, int>? waterSupplies = null, PlaygroundBodyDefinition? body = null, PlaygroundWeatherDefinition? weather = null,
        bool? needsEnabled = null, bool? economyEnabled = null, string? marketPlaceId = null, string? workshopPlaceId = null,
        long? marketRefreshTicks = null) => new(contentId ?? ContentId, name ?? Name, startTime ?? StartTime,
            places ?? Places, passages ?? Passages, relations ?? Relations, resources ?? Resources, initialCharacters ?? InitialCharacters,
            items ?? Items, recipes ?? Recipes, marketStock ?? MarketStock, marketDemand ?? MarketDemand,
            buyPrices ?? BuyPrices, sellPrices ?? SellPrices, foodSupplies ?? FoodSupplies, waterSupplies ?? WaterSupplies,
            body ?? Body, weather ?? Weather, needsEnabled ?? NeedsEnabled, economyEnabled ?? EconomyEnabled,
            marketPlaceId ?? MarketPlaceId, workshopPlaceId ?? WorkshopPlaceId, marketRefreshTicks ?? MarketRefreshTicks);

    public PlaygroundPlaceDefinition GetPlace(string id) => _places.Single(p => p.Id == id);
    public IReadOnlyList<PlaygroundPassageDefinition> GetEntrances(string placeId) => _passages
        .Where(p => p.From == placeId || p.To == placeId).Select(p => p.From == placeId ? p
            : p with { From = p.To, To = p.From, Description = p.ReverseDescription, ReverseDescription = p.Description, EntranceId = p.ReverseEntranceId, ReverseEntranceId = p.EntranceId }).ToArray();
    public string? GetResourcePlace(string id) => _resources.SingleOrDefault(r => r.Id == id)?.PlaceId;
    public PlaygroundRecipe? Recipe(string id) => _recipes.SingleOrDefault(r => r.Id == id);
    public int BuyPrice(string id) => _buyPrices.GetValueOrDefault(id);
    public int SellPrice(string id) => _sellPrices.GetValueOrDefault(id);
    public int SupplyFood(string id) => _foodSupplies.GetValueOrDefault(id);
    public int SupplyWater(string id) => _waterSupplies.GetValueOrDefault(id);
    public void Validate() {
        ValidateStructure();
        if (!StringComparer.Ordinal.Equals(Fingerprint, ComputeFingerprint())) {
            throw new InvalidDataException("World content does not match its canonical fingerprint.");
        }
    }

    private void ValidateStructure() {
        if (_places is null || _passages is null || _relations is null || _resources is null || _initialCharacters is null ||
            _items is null || _recipes is null || _marketStock is null || _marketDemand is null || _buyPrices is null ||
            _sellPrices is null || _foodSupplies is null || _waterSupplies is null || Body is null || Weather is null ||
            _places.Any(p => p is null) || _passages.Any(p => p is null) || _relations.Any(r => r is null) ||
            _resources.Any(r => r is null) || _initialCharacters.Any(c => c is null) || _recipes.Any(r => r is null)) { Invalid(); }
        if (string.IsNullOrWhiteSpace(ContentId) || string.IsNullOrWhiteSpace(Name) || StartTime.Ticks < 0 ||
            _places.Count == 0 || _initialCharacters.Count > 3 || _resources.Count == 0 || _items.Count == 0 ||
            MarketRefreshTicks <= 0 || Weather.IntervalTicks <= 0 ||
            Weather.ExposureLoss is < 0 or > 100 || Weather.ColdProbabilityDenominator <= 0 ||
            Weather.ColdProbabilityNumerator < 0 || Weather.ColdProbabilityNumerator > Weather.ColdProbabilityDenominator ||
            !NeedsEnabled && Weather.Enabled || Body.FoodPerHour is < -100 or > 0 || Body.WaterPerHour is < -100 or > 0 ||
            Body.ShelteredWarmthPerHour is < 0 or > 100 || Body.DayWarmthPerHour is < 0 or > 100 ||
            Body.NightWarmthPerHour is < -100 or > 0) { Invalid(); }
        Unique(_places.Select(p => p.Id)); Unique(_passages.Select(p => p.Id)); Unique(_resources.Select(r => r.Id));
        Unique(_passages.SelectMany(p => new[] { p.EntranceId, p.ReverseEntranceId }));
        if (_passages.SelectMany(p => new[] { p.EntranceId, p.ReverseEntranceId }).Any(id => _passages.Any(p => p.Id == id))) { Invalid(); }
        Unique(_recipes.Select(r => r.Id)); Unique(_initialCharacters.Select(c => c.Id));
        var placeIds = _places.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (!placeIds.Contains(MarketPlaceId) || !placeIds.Contains(WorkshopPlaceId) ||
            _places.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Description is null || p.IsExit) ||
            _passages.Any(p => !placeIds.Contains(p.From) || !placeIds.Contains(p.To) || p.From == p.To || p.DurationTicks <= 0 ||
                p.Description is null || p.ReverseDescription is null) ||
            _relations.Any(r => !placeIds.Contains(r.EndpointA) || !placeIds.Contains(r.EndpointB) ||
                StringComparer.Ordinal.Compare(r.EndpointA, r.EndpointB) >= 0) ||
            _relations.Select(r => (r.EndpointA, r.EndpointB)).Distinct().Count() != _relations.Count ||
            _items.Any(p => !IsCommandToken(p.Key) || p.Key == "money" || string.IsNullOrWhiteSpace(p.Value))) { Invalid(); }
        foreach (var resource in _resources) {
            if (!placeIds.Contains(resource.PlaceId) || !_items.ContainsKey(resource.ItemId) || string.IsNullOrWhiteSpace(resource.Name) ||
                resource.RegenerationTicks <= 0 || resource.HarvestTicks <= 0 || resource.Capacity <= 0 || resource.Stock < 0 ||
                resource.Stock > resource.Capacity || resource.SkillId != "herbalism" || resource.RequiredLevel < 0) { Invalid(); }
        }
        foreach (var actor in _initialCharacters) {
            actor.ValidateStructure();
            if (!placeIds.Contains(actor.PlaceId) || string.IsNullOrWhiteSpace(actor.Name) || actor.SkillLevel < 0 || actor.Money < 0 ||
                actor.Food is < 0 or > 100 || actor.Water is < 0 or > 100 || actor.Warmth is < 0 or > 100 ||
                actor.Backpack.Any(p => !_items.ContainsKey(p.Key) || p.Value <= 0)) { Invalid(); }
        }
        foreach (var recipe in _recipes) {
            if (!_items.ContainsKey(recipe.InputItem) || !_items.ContainsKey(recipe.OutputItem) || recipe.InputCount <= 0 ||
                recipe.OutputCount <= 0 || recipe.DurationTicks <= 0) { Invalid(); }
        }
        foreach (var table in new[] { _marketStock, _marketDemand, _buyPrices, _sellPrices, _foodSupplies, _waterSupplies }) {
            if (table.Any(p => !_items.ContainsKey(p.Key) || p.Value <= 0)) { Invalid(); }
        }
        if (_marketStock.Keys.Any(id => BuyPrice(id) <= 0) || _marketDemand.Keys.Any(id => SellPrice(id) <= 0) ||
            _buyPrices.Keys.Any(id => !_marketStock.ContainsKey(id)) || _sellPrices.Keys.Any(id => !_marketDemand.ContainsKey(id)) ||
            _foodSupplies.Any(p => p.Value > 100) || _waterSupplies.Any(p => p.Value > 100)) { Invalid(); }
        // The Spatial module remains the authority for graph identifier and topology invariants.
        _ = BuildGraph();
    }

    internal GraphDefinition BuildGraph() => GraphDefinition.Create(_places.Select(p => new PlaceId(p.Id)),
        _passages.Select(p => new PassageDefinition(new(p.Id), new(p.From), new(p.To), p.DurationTicks, new(true, true))),
        _relations.Select(r => new PlaceRelationDefinition(new(r.EndpointA), new(r.EndpointB), r.Flags)));

    public string ComputeFingerprint() {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true)) {
            writer.Write("Hillside.WorldDefinition/v2"); writer.Write(ContentId); writer.Write(Name); writer.Write(StartTime.Ticks);
            WriteList(_places.OrderBy(p => p.Id, StringComparer.Ordinal), p => { writer.Write(p.Id); writer.Write(p.Name); writer.Write(p.Description); writer.Write(p.IsExit); writer.Write(p.Sheltered); });
            WriteList(_passages.OrderBy(p => p.Id, StringComparer.Ordinal), p => { writer.Write(p.Id); writer.Write(p.From); writer.Write(p.To); writer.Write(p.Description); writer.Write(p.DurationTicks); writer.Write(p.ReverseDescription); writer.Write(p.EntranceId); writer.Write(p.ReverseEntranceId); });
            WriteList(_relations.OrderBy(r => r.EndpointA, StringComparer.Ordinal).ThenBy(r => r.EndpointB, StringComparer.Ordinal), r => { writer.Write(r.EndpointA); writer.Write(r.EndpointB); writer.Write(r.Flags); });
            WriteList(_resources.OrderBy(r => r.Id, StringComparer.Ordinal), r => { writer.Write(r.Id); writer.Write(r.Name); writer.Write(r.ItemId); writer.Write(r.PlaceId); writer.Write(r.GrowsInDaylight); writer.Write(r.RegenerationTicks); writer.Write(r.Capacity); writer.Write(r.Stock); writer.Write(r.HarvestTicks); writer.Write(r.SkillId); writer.Write(r.RequiredLevel); });
            WriteList(_initialCharacters, c => { writer.Write(c.Id); writer.Write(c.Name); writer.Write(c.PlaceId); writer.Write(c.SkillLevel); writer.Write(c.Money); writer.Write(c.Food); writer.Write(c.Water); writer.Write(c.Warmth); WriteInts(c.Backpack); writer.Write(c.UnitCapacity.HasValue); if (c.UnitCapacity is { } capacity) { writer.Write(capacity); } });
            WriteList(_items.OrderBy(p => p.Key, StringComparer.Ordinal), p => { writer.Write(p.Key); writer.Write(p.Value); });
            WriteList(_recipes.OrderBy(r => r.Id, StringComparer.Ordinal), r => { writer.Write(r.Id); writer.Write(r.InputItem); writer.Write(r.InputCount); writer.Write(r.OutputItem); writer.Write(r.OutputCount); writer.Write(r.DurationTicks); });
            WriteInts(_marketStock); WriteInts(_marketDemand); WriteInts(_buyPrices); WriteInts(_sellPrices); WriteInts(_foodSupplies); WriteInts(_waterSupplies);
            writer.Write(Body.FoodPerHour); writer.Write(Body.WaterPerHour); writer.Write(Body.ShelteredWarmthPerHour); writer.Write(Body.DayWarmthPerHour); writer.Write(Body.NightWarmthPerHour);
            writer.Write(Weather.Enabled); writer.Write(Weather.IntervalTicks); writer.Write(Weather.ExposureLoss); writer.Write(Weather.ColdProbabilityNumerator); writer.Write(Weather.ColdProbabilityDenominator); writer.Write(Weather.Seed);
            writer.Write(NeedsEnabled); writer.Write(EconomyEnabled); writer.Write(MarketPlaceId); writer.Write(WorkshopPlaceId); writer.Write(MarketRefreshTicks);
            void WriteList<T>(IEnumerable<T> entries, Action<T> write) {
                T[] array = entries.ToArray(); writer.Write(array.Length); foreach (T entry in array) { write(entry); }
            }
            void WriteInts(IReadOnlyDictionary<string, int> entries) => WriteList(entries.OrderBy(p => p.Key, StringComparer.Ordinal), p => { writer.Write(p.Key); writer.Write(p.Value); });
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static Dictionary<string, int> Copy(IReadOnlyDictionary<string, int> source) => new(source, StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, int> Read(Dictionary<string, int> source) => new ReadOnlyDictionary<string, int>(source);
    private static bool IsCommandToken(string? value) => !string.IsNullOrEmpty(value) && !value.Any(char.IsWhiteSpace);
    private static void Unique(IEnumerable<string> ids) {
        string[] values = ids.ToArray();
        if (values.Any(id => !IsCommandToken(id) || id.Contains(':')) || values.Distinct(StringComparer.Ordinal).Count() != values.Length) { Invalid(); }
    }
    [DoesNotReturn]
    private static void Invalid() => throw new InvalidDataException("Invalid immutable world content definition.");
}

/// <summary>Derived per-definition helpers. This object is never part of a durable graph.</summary>
internal sealed class PlaygroundDefinitionRuntime {
    public GraphDefinition Graph { get; }
    public SpatialSimulation Simulation { get; }
    public SpatialPlanner Planner { get; }
    public GraphSpatialReducer Reducer { get; }
    public SpatialQueries Queries { get; }
    public SpatialOccurrenceRule OccurrenceRule { get; }
    internal PlaygroundDefinitionRuntime(PlaygroundWorldDefinition definition) {
        Graph = definition.BuildGraph(); Simulation = new(Graph); Planner = new(Graph); Reducer = new(Graph);
        Queries = new(Graph); OccurrenceRule = new(Graph);
    }
}

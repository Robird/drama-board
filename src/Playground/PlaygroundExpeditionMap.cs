using DramaBoard.Spatial;
using Atelia.DurableGraph;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.PlaceDefinition", 1)]
public sealed partial record PlaygroundPlaceDefinition(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string Name,
    [field: DurableField(3)] string Description,
    [field: DurableField(4)] bool IsExit = false,
    [field: DurableField(5)] bool Sheltered = false) : IDurableObject;
[DurableType("DramaBoard.Playground.PassageDefinition", 1)]
public sealed partial record PlaygroundPassageDefinition(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string From,
    [field: DurableField(3)] string To,
    [field: DurableField(4)] string Description,
    [field: DurableField(5)] long DurationTicks,
    [field: DurableField(6)] string ReverseDescription = "沿来路返回") : IDurableObject;

/// <summary>Immutable scenario content. Its identity and game revision bind restored spatial IDs to this graph.</summary>
/// <remarks>This is world truth, not a controller view. Unknown destination names must not be projected.</remarks>
public static class PlaygroundExpeditionMap {
    public const string ScenarioId = "hillside-v1";
    private static readonly PlaygroundPlaceDefinition[] Places = [
        new("home", "山野镇住处", "屋内能遮风休息。门外街道通向镇里的市场与工坊。", Sheltered: true),
        new("market", "山野镇市场", "有顶棚的集市连着住处、工坊与镇口，井台可采集饮水。", Sheltered: true),
        new("workshop", "山野镇工坊", "有顶棚的工作间可供停留，街道通向市场和住处。", Sheltered: true),
        new("camp", "林间营地", "林间空地通向山脚小镇。溪水声从下坡传来，另一条窄径通向高处。"),
        new("stream", "溪流湾", "清澈溪水流向狭谷。岸边有浆果，石头上留着一枚模糊的路标。"),
        new("ravine", "狭谷旧桥", "溪水从旧桥下穿过。远处似有车声，但树影遮住了去路。"),
        new("road", "山脚镇口", "公路旁是小镇入口，街道通向市场。一条山路直达林间营地，另一条旧路沿狭谷上行。"),
        new("ridge", "风口山脊", "山风很冷。林间隐约露出一段屋顶，更高处有人工设施的轮廓。"),
        new("cabin", "废弃林屋", "木屋能遮风，停留时身体会逐渐回暖。墙上有褪色的巡山路线指示，附近能找到干木。", Sheltered: true),
        new("signal", "巡山信号站", "山站的屋檐可以遮风休息，来时的巡山路仍通向林屋。", Sheltered: true)
    ];
    private static readonly PlaygroundPassageDefinition[] Passages = [
        new("market-home", "market", "home", "沿街道前往住处", PlaygroundTime.Hour / 4),
        new("market-workshop", "market", "workshop", "沿街道前往工坊", PlaygroundTime.Hour / 4),
        new("home-workshop", "home", "workshop", "沿小巷前往工坊", PlaygroundTime.Hour / 4),
        new("market-road", "market", "road", "沿街道前往山脚镇口", PlaygroundTime.Hour / 4),
        new("road-camp", "road", "camp", "沿山路前往林间营地", PlaygroundTime.Hour),
        new("trail-1", "camp", "stream", "沿溪水声下坡", PlaygroundTime.Hour),
        new("trail-2", "camp", "ridge", "沿窄径攀上风口", 2 * PlaygroundTime.Hour),
        new("trail-3", "stream", "ravine", "顺溪流穿过密林", 2 * PlaygroundTime.Hour),
        new("trail-4", "ravine", "road", "越过旧桥，追寻远处的车声", 3 * PlaygroundTime.Hour),
        new("trail-5", "ridge", "cabin", "向林间屋顶下行", 2 * PlaygroundTime.Hour),
        new("trail-6", "cabin", "signal", "循巡山标记继续向高处", 3 * PlaygroundTime.Hour),
        new("trail-7", "stream", "cabin", "沿石阶攀向林间", 3 * PlaygroundTime.Hour)
    ];
    public static GraphDefinition Graph { get; } = GraphDefinition.Create(
        Places.Select(p => new PlaceId(p.Id)), Passages.Select(p => new PassageDefinition(
            new(p.Id), new(p.From), new(p.To), p.DurationTicks, new(true, true))),
        relations: [
            new(new("camp"), new("stream"), PlaceRelationCapabilities.Sound),
            new(new("ridge"), new("cabin"), PlaceRelationCapabilities.Sound)
        ]);
    internal static SpatialSimulation Simulation { get; } = new(Graph);
    internal static SpatialPlanner Planner { get; } = new(Graph);
    internal static GraphSpatialReducer Reducer { get; } = new(Graph);
    internal static IReadOnlyList<PlaygroundPlaceDefinition> DefaultPlaces => Array.AsReadOnly(Places);
    internal static IReadOnlyList<PlaygroundPassageDefinition> DefaultPassages => Array.AsReadOnly(Passages
        .Select(p => p with { ReverseDescription = ReverseDescription(p.Id) }).ToArray());

    public static PlaygroundPlaceDefinition GetPlace(string id) => Places.Single(p => p.Id == id);
    public static IReadOnlyList<PlaygroundPassageDefinition> GetPassages(string placeId) => Passages
        .Where(p => p.From == placeId || p.To == placeId)
        .Select(p => p.From == placeId ? p : new(p.Id, p.To, p.From, ReverseDescription(p.Id), p.DurationTicks)).ToArray();
    private static string ReverseDescription(string passageId) => passageId switch {
        "market-home" => "沿街道前往市场", "market-workshop" => "沿街道前往市场",
        "home-workshop" => "沿小巷前往住处", "market-road" => "沿街道进入小镇市场",
        "road-camp" => "沿山路向小镇方向下坡",
        "trail-1" => "沿林间缓坡向上", "trail-2" => "沿窄径下山", "trail-3" => "逆溪流穿过密林",
        "trail-4" => "沿旧路上山", "trail-5" => "沿陡坡爬向风口", "trail-6" => "沿巡山标记下行",
        "trail-7" => "沿石阶下到流水声方向", _ => throw new InvalidOperationException("Unknown scenario passage.")
    };
    public static string? GetResourcePlace(string resourceId) => resourceId switch {
        "berries" => "stream", "water" => "stream", "wood" => "cabin", "ridge-herbs" => "ridge", "well" => "market", _ => null
    };
}

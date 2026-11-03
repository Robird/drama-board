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
[DurableType("DramaBoard.Playground.PassageDefinition", 2)]
public sealed partial record PlaygroundPassageDefinition(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string From,
    [field: DurableField(3)] string To,
    [field: DurableField(4)] string Description,
    [field: DurableField(5)] long DurationTicks,
    [field: DurableField(6)] string ReverseDescription = "沿树影里的小径前行",
    [field: DurableField(7)] string EntranceId = "",
    [field: DurableField(8)] string ReverseEntranceId = "") : IDurableObject;

/// <summary>Immutable scenario content. Its identity and game revision bind restored spatial IDs to this graph.</summary>
/// <remarks>This is world truth, not a controller view. Unknown destination names must not be projected.</remarks>
public static class PlaygroundExpeditionMap {
    public const string ScenarioId = "hillside-v1";
    private static readonly PlaygroundPlaceDefinition[] Places = [
        new("home", "山野镇住处", "屋内能遮风休息。门外可见铺石街道与安静的小巷。", Sheltered: true),
        new("market", "山野镇市场", "有顶棚的集市人声交错，井台可采集饮水。", Sheltered: true),
        new("workshop", "山野镇工坊", "有顶棚的工作间可供停留，门外传来脚步与车轮声。", Sheltered: true),
        new("camp", "林间营地", "林间空地洒着斑驳阳光。下坡传来溪水声，高处可见窄径。"),
        new("stream", "溪流湾", "清澈溪水穿过嶙峋山石。岸边有浆果，石头上留着一枚模糊的路标。"),
        new("ravine", "狭谷旧桥", "溪水从旧桥下穿过。远处似有车声，但树影遮住了去路。"),
        new("road", "山脚镇口", "公路旁可见低矮房屋。石板街与两条山路各自延伸进远处的树影。"),
        new("ridge", "风口山脊", "山风很冷。林间隐约露出一段屋顶，更高处有人工设施的轮廓。"),
        new("cabin", "废弃林屋", "木屋能遮风，停留时身体会逐渐回暖。墙上有褪色的巡山路线指示，附近能找到干木。", Sheltered: true),
        new("signal", "巡山信号站", "山站的屋檐可以遮风休息，脚下的巡山路隐入树影。", Sheltered: true)
    ];
    private static readonly PlaygroundPassageDefinition[] Passages = [
        new("market-home", "market", "home", "沿安静的铺石街道前行", PlaygroundTime.Hour / 4, EntranceId: "e-k7", ReverseEntranceId: "e-m2"),
        new("market-workshop", "market", "workshop", "沿有车辙的街道前行", PlaygroundTime.Hour / 4, EntranceId: "e-v4", ReverseEntranceId: "e-a9"),
        new("home-workshop", "home", "workshop", "沿窄小的石巷前行", PlaygroundTime.Hour / 4, EntranceId: "e-r6", ReverseEntranceId: "e-j3"),
        new("market-road", "market", "road", "沿开阔的街道前行", PlaygroundTime.Hour / 4, EntranceId: "e-d8", ReverseEntranceId: "e-p5"),
        new("road-camp", "road", "camp", "沿落叶覆盖的山路上行", PlaygroundTime.Hour, EntranceId: "e-w2", ReverseEntranceId: "e-g6"),
        new("trail-1", "camp", "stream", "沿溪水声下坡", PlaygroundTime.Hour, EntranceId: "e-c7", ReverseEntranceId: "e-n4"),
        new("trail-2", "camp", "ridge", "沿窄径攀上风口", 2 * PlaygroundTime.Hour, EntranceId: "e-b9", ReverseEntranceId: "e-f2"),
        new("trail-3", "stream", "ravine", "顺溪流穿过密林", 2 * PlaygroundTime.Hour, EntranceId: "e-h5", ReverseEntranceId: "e-q8"),
        new("trail-4", "ravine", "road", "越过旧桥，追寻远处的车声", 3 * PlaygroundTime.Hour, EntranceId: "e-s3", ReverseEntranceId: "e-z6"),
        new("trail-5", "ridge", "cabin", "向林间屋顶下行", 2 * PlaygroundTime.Hour, EntranceId: "e-u8", ReverseEntranceId: "e-t4"),
        new("trail-6", "cabin", "signal", "循巡山标记继续向高处", 3 * PlaygroundTime.Hour, EntranceId: "e-x5", ReverseEntranceId: "e-l9"),
        new("trail-7", "stream", "cabin", "沿石阶攀向林间", 3 * PlaygroundTime.Hour, EntranceId: "e-y2", ReverseEntranceId: "e-o7")
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
    public static IReadOnlyList<PlaygroundPassageDefinition> GetEntrances(string placeId) => DefaultPassages
        .Where(p => p.From == placeId || p.To == placeId)
        .Select(p => p.From == placeId ? p : p with { From = p.To, To = p.From, Description = p.ReverseDescription, ReverseDescription = p.Description, EntranceId = p.ReverseEntranceId, ReverseEntranceId = p.EntranceId }).ToArray();
    private static string ReverseDescription(string passageId) => passageId switch {
        "market-home" => "沿喧声渐起的街道前行", "market-workshop" => "沿喧声渐起的街道前行",
        "home-workshop" => "沿安静的小巷前行", "market-road" => "沿铺石街道前行",
        "road-camp" => "沿山路缓缓下坡",
        "trail-1" => "沿林间缓坡向上", "trail-2" => "沿窄径下山", "trail-3" => "逆溪流穿过密林",
        "trail-4" => "沿旧路上山", "trail-5" => "沿陡坡爬向风口", "trail-6" => "沿巡山标记下行",
        "trail-7" => "沿石阶下到流水声方向", _ => throw new InvalidOperationException("Unknown scenario passage.")
    };
    public static string? GetResourcePlace(string resourceId) => resourceId switch {
        "berries" => "stream", "water" => "stream", "wood" => "cabin", "ridge-herbs" => "ridge", "well" => "market", _ => null
    };
}

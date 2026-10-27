using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

public sealed record CyclePlaceDefinition(string Id, string Name, string Description, bool IsExit = false, bool Sheltered = false);
public sealed record CyclePassageDefinition(string Id, string From, string To, string Description, long DurationTicks);

/// <summary>Immutable scenario content. Its identity and game revision bind restored spatial IDs to this graph.</summary>
/// <remarks>This is world truth, not a controller view. Unknown destination names and exits must not be projected.</remarks>
public static class CycleExpeditionMap {
    public const string ScenarioId = "lost-mountain-v1";
    private static readonly CyclePlaceDefinition[] Places = [
        new("camp", "迷途营地", "醒来时篝火已熄。溪水声从下坡传来，另一条窄径通向高处。"),
        new("stream", "溪流湾", "清澈溪水流向狭谷。岸边有浆果，石头上留着一枚模糊的路标。"),
        new("ravine", "狭谷旧桥", "溪水从旧桥下穿过。远处似有车声，但树影遮住了去路。"),
        new("road", "山脚公路", "公路养护站亮着灯，工作人员发现了你。", true),
        new("ridge", "风口山脊", "山风很冷。林间隐约露出一段屋顶，更高处有人工设施的轮廓。"),
        new("cabin", "废弃林屋", "木屋能遮风，停留时身体会逐渐回暖。墙上有褪色的巡山路线指示，附近能找到干木。", Sheltered: true),
        new("signal", "巡山信号站", "值守员听见你的呼喊，打开了信号站的门。", true)
    ];
    private static readonly CyclePassageDefinition[] Passages = [
        new("trail-1", "camp", "stream", "沿溪水声下坡", CycleTime.Hour),
        new("trail-2", "camp", "ridge", "沿窄径攀上风口", 2 * CycleTime.Hour),
        new("trail-3", "stream", "ravine", "顺溪流穿过密林", 2 * CycleTime.Hour),
        new("trail-4", "ravine", "road", "越过旧桥，追寻远处的车声", 3 * CycleTime.Hour),
        new("trail-5", "ridge", "cabin", "向林间屋顶下行", 2 * CycleTime.Hour),
        new("trail-6", "cabin", "signal", "循巡山标记继续向高处", 3 * CycleTime.Hour),
        new("trail-7", "stream", "cabin", "沿石阶攀向林间", 3 * CycleTime.Hour)
    ];
    public static GraphDefinition Graph { get; } = GraphDefinition.Create(
        Places.Select(p => new PlaceId(p.Id)), Passages.Select(p => new PassageDefinition(
            new(p.Id), new(p.From), new(p.To), p.DurationTicks, new(true, true))));
    internal static SpatialSimulation Simulation { get; } = new(Graph);
    internal static SpatialPlanner Planner { get; } = new(Graph);
    internal static GraphSpatialReducer Reducer { get; } = new(Graph);

    public static CyclePlaceDefinition GetPlace(string id) => Places.Single(p => p.Id == id);
    public static IReadOnlyList<CyclePassageDefinition> GetPassages(string placeId) => Passages
        .Where(p => p.From == placeId || p.To == placeId)
        .Select(p => p.From == placeId ? p : new(p.Id, p.To, p.From, ReverseDescription(p.Id), p.DurationTicks)).ToArray();
    private static string ReverseDescription(string passageId) => passageId switch {
        "trail-1" => "沿林间缓坡向上", "trail-2" => "沿窄径下山", "trail-3" => "逆溪流穿过密林",
        "trail-4" => "沿旧路上山", "trail-5" => "沿陡坡爬向风口", "trail-6" => "沿巡山标记下行",
        "trail-7" => "沿石阶下到流水声方向", _ => throw new InvalidOperationException("Unknown scenario passage.")
    };
    public static string? GetResourcePlace(string resourceId) => resourceId switch {
        "berries" => "stream", "water" => "stream", "wood" => "cabin", "ridge-herbs" => "ridge", _ => null
    };
}

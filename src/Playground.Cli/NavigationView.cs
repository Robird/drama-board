using System.Globalization;
using System.Text;
using DramaBoard.Playground;

/// <summary>Map and route planning use only the character's frozen knowledge and candidate.</summary>
internal static class NavigationView {
    internal const string EstimateMeaning = "行程合计只计算各段公开耗时，不含确认停留、补给或他人干预，不保证安全或途中存活。";

    internal static string RenderMap(PlaygroundObservation observation) {
        if (observation.KnownMap.Count == 0) { return ""; }
        var text = new StringBuilder();
        text.AppendLine("## 已知地图");
        text.AppendLine("只含本人去过的地点和实际走通的连接；未知入口的线索不证明去向。");
        foreach (var place in observation.KnownMap) {
            text.AppendLine($"- **{place.Name}** `{place.Id}`");
            foreach (var entrance in place.Entrances) {
                string destination = entrance.KnownDestinationId is { } id
                    ? $"已确认 → {entrance.KnownDestinationName}（`{id}`）" : "去向未知";
                text.AppendLine($"  - 入口 `{entrance.Id}`：{entrance.Description}；耗时 {Minutes(entrance.DurationTicks)} 分钟；{destination}。");
            }
        }
        text.AppendLine();
        return text.ToString();
    }

    internal static string RenderCandidate(PlaygroundObservation observation) {
        if (!observation.IsExpedition) { return ""; }
        var text = new StringBuilder();
        text.AppendLine("## 当前候选路径");
        if (observation.CandidatePath is not { } path) {
            text.AppendLine("当前无候选路径；先前被替换或清空的候选已失效。");
        } else {
            text.AppendLine($"路径 ID：`{path.Id}`。");
            text.AppendLine($"规划起点：{Place(observation, path.OriginPlaceId)} → 终点：{Place(observation, path.TargetPlaceId)}。");
            if (path.Steps.Count == 0) { text.AppendLine("规划起终点相同，无需移动。"); }
            for (int index = 0; index < path.Steps.Count; index++) {
                var step = path.Steps[index];
                text.AppendLine($"- 第 {index + 1} 段：{Place(observation, step.FromPlaceId)}，入口 `{step.EntranceId}` → {Place(observation, step.ToPlaceId)}；{Minutes(step.DurationTicks)} 分钟。");
            }
            text.AppendLine($"行程合计：{Minutes(path.TotalTicks)} 分钟。");
            text.AppendLine(EstimateMeaning);
            text.AppendLine("这是规划记录，不会自动出发；各段入口须实际到达该段起点后按当地入口列表手动 go。当前不提供自动导航命令。");
        }
        text.AppendLine("成功 path 会替换当前候选；在已知地图中无路会清空候选，语法或未知地点拒绝保留原候选。");
        text.AppendLine();
        return text.ToString();
    }

    internal static string RenderHelp(PlaygroundObservation observation) {
        if (!observation.IsExpedition) { return ""; }
        return "- `map`：查看本人已知地点、已确认连接与未知入口。\n"
            + "- `path <目标地点ID> [from <起点地点ID>]`：只在已知连接上规划最短行程，地点 ID 见已知地图。省略起点使用实际所在地点；途中必须显式 from。\n"
            + "map/path 是普通角色命令，不耗时、不确认提醒、不改变活动；只有现有决策窗口可输入。规划不自动移动，拒绝不改候选；在已知地图中无路不证明世界中不存在道路。\n";
    }

    private static string Place(PlaygroundObservation observation, string id) =>
        $"{observation.KnownMap.FirstOrDefault(place => place.Id == id)?.Name ?? id}（`{id}`）";
    private static string Minutes(long ticks) => (ticks / 60_000m).ToString("0.##", CultureInfo.InvariantCulture);
}

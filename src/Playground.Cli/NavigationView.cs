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
            text.AppendLine("这是候选规划，不会自动出发；用 navigate 加此路径 ID 启动，或实际到当地后手动 go。候选替换不改变已经启动的活动路线。");
        }
        text.AppendLine("成功 path 会替换当前候选；在已知地图中无路会清空候选，语法或未知地点拒绝保留原候选。");
        text.AppendLine();
        return text.ToString();
    }

    internal static string RenderHelp(PlaygroundObservation observation) {
        if (!observation.IsExpedition) { return ""; }
        return "- `map`：查看本人已知地点、已确认连接与未知入口。\n"
            + "- `path <目标地点ID> [from <起点地点ID>]`：只在已知连接上规划最短行程，地点 ID 见已知地图。省略起点使用实际所在地点；途中必须显式 from。\n"
            + "- `navigate <路径ID>`：在候选起点空闲且未跟随时启动当前候选，首段立即出发；每到中间点先观察，再 continue 出发下一段。零段只完成规划，不放行时间。\n"
            + "活动导航期间需先 cancel 才能改用 go、follow、采集、生产或等待。map/path 和即时交互不改变活动路线。\n"
            + "map/path 是普通角色命令，不耗时、不确认提醒、不改变活动；只有现有决策窗口可输入。规划不自动移动，拒绝不改候选；在已知地图中无路不证明世界中不存在道路。\n";
    }

    internal static string RenderActive(PlaygroundObservation observation) {
        if (!observation.IsExpedition) { return ""; }
        var text = new StringBuilder();
        text.AppendLine("## 活动导航");
        if (observation.Navigation is not { } navigation) {
            text.AppendLine(observation.ArrivalDue is not null
                ? "当前无活动导航；正在进行的本段移动继续至终点，到点不会自动接续路线。"
                : "当前无活动导航。");
        } else {
            text.AppendLine($"活动路线 ID：`{navigation.Path.Id}`；终点：{Place(observation, navigation.Path.TargetPlaceId)}。");
            text.AppendLine($"已完成 {navigation.CompletedSteps}/{navigation.Path.Steps.Count} 段。");
            var step = navigation.Path.Steps[navigation.CompletedSteps];
            text.AppendLine($"{(observation.ArrivalDue is not null ? "正在本段" : "等待确认的下一段")}：{Place(observation, step.FromPlaceId)}，入口 `{step.EntranceId}` → {Place(observation, step.ToPlaceId)}；{Minutes(step.DurationTicks)} 分钟。");
            text.AppendLine(ContinueMeaning(observation));
            text.AppendLine(CancelMeaning(observation));
            text.AppendLine("活动路线独立于当前候选；新的 path 只替换候选，不改本路线，也不自动改道。");
        }
        text.AppendLine("只有现有决策窗口可输入；途中没有提醒等唤醒时，将继续至下一事件或地点。");
        text.AppendLine();
        return text.ToString();
    }

    internal static string ContinueMeaning(PlaygroundObservation observation) => observation.Navigation is not null
        ? observation.ArrivalDue is not null
            ? "本次 continue 只确认待响应提醒，维持正在进行的本段和原定抵达期限，不另开下一段。"
            : "本次 continue 确认待响应提醒并尝试从此处出发下一段；失败保留导航与提醒，可处理原因后再试或 cancel。"
        : "确认感知与通信，保留活动、进度及原期限；跟随时继续同行，闲置时确认不推进时间。";

    internal static string CancelMeaning(PlaygroundObservation observation) => observation.Navigation is not null
        ? observation.ArrivalDue is not null
            ? "cancel 只取消剩余导航；本段仍按原期限到达，待响应提醒保留，不会自动确认，到点不再接续。"
            : "cancel 只清除剩余导航，在此停下并恢复空闲；待响应提醒保留，不会自动确认。"
        : "取消当前活动：采集保留已获物品，生产退回预留投入，等待直接结束；已经出发的路段不能取消。";

    internal static string RenderNavigateCommands(PlaygroundObservation observation) {
        var text = new StringBuilder();
        foreach (var check in observation.Commands.Where(check => check.Command.Kind == PlaygroundCommandKind.Navigate)) {
            text.AppendLine($"- `{PlaygroundCommandParser.Format(check.Command)}`：{(check.Rejection is null ? "可执行" : "受阻：" + check.Rejection)}。");
            if (check.Warning is { } warning) { text.AppendLine("  行动提示（不禁止）：" + warning); }
        }
        return text.ToString();
    }

    private static string Place(PlaygroundObservation observation, string id) =>
        $"{observation.KnownMap.FirstOrDefault(place => place.Id == id)?.Name ?? id}（`{id}`）";
    private static string Minutes(long ticks) => (ticks / 60_000m).ToString("0.##", CultureInfo.InvariantCulture);
}

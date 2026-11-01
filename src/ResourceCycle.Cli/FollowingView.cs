using System.Text;
using DramaBoard.ResourceCycle;

/// <summary>Following status and commands from the same frozen material for Console and LLM.</summary>
internal static class FollowingView {
    public static void RenderStatus(StringBuilder text, CycleObservation observation) {
        if (!observation.IsExpedition) { return; }
        text.AppendLine("### 同行状态");
        text.AppendLine(observation.FollowingLeaderId is { } leader
            ? $"正在跟随 {observation.FollowingLeaderName ?? leader}（`{leader}`）；由领队决定移动，抵达后仍保持跟随。不能独立 go / gather / wait。"
            : "当前自主行动，没有跟随领队。");
        text.AppendLine(observation.Followers.Count == 0 ? "直属队员：无。" : "直属队员：" +
            string.Join("、", observation.Followers.Select(pair => $"{pair.Value}（`{pair.Key}`）")) + "；普通 go 将带这些队员一起出发。");
        text.AppendLine();
    }

    public static void RenderCommands(StringBuilder text, CycleObservation observation) {
        if (!observation.IsExpedition || observation.Outcome != CycleExpeditionOutcome.Exploring) { return; }
        text.AppendLine("### 集合与跟随");
        text.AppendLine("`follow <actor-id>`：同地且自己无活动时主动跟随领队，本次结束决策并等待同行；领队可以正在采集或等待。不能自跟随、转跟或形成链式队伍。");
        text.AppendLine("`unfollow`：在地点内解除自己的跟随，恢复独立行动。`dismiss-followers`：在地点内解散全部直属队员，不取消自己的活动；这两条命令不确认已有提醒。");
        text.AppendLine("到达工作地点后显式解除跟随，各自采集；完成或取消活动后再 follow 集合。途中不能改变关系；收到提醒时可补给后 continue 继续跟随。say 与 give 仍仅限同地；地点内可 shout 召集声音一跳范围内的同伴。无人提醒时不会逐段询问队员。");
        foreach (CycleCommandCheck check in observation.Commands.Where(check => check.Command.Kind is
            CycleCommandKind.Follow or CycleCommandKind.Unfollow or CycleCommandKind.DismissFollowers)) {
            text.AppendLine($"- `{CycleViewSnapshot.CommandSyntax(check.Command)}`：{(check.Rejection is null ? "可执行" : "受阻：" + check.Rejection)}。");
        }
        if (!observation.Commands.Any(check => check.Command.Kind == CycleCommandKind.Follow)) {
            text.AppendLine("follow 的 actor-id：空域；当前没有可见的同地领队候选。");
        }
        text.AppendLine();
    }
}

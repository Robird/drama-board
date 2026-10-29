using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>Both layouts render the recorded opening and its experiences, never a second accumulated summary.</summary>
internal static class ContextHistoryPlayView {
    public static string RenderHistory(CycleObservation observation, CharacterInteractionState? interactionState = null) {
        if (interactionState?.CurrentContext is not { } context) { return ""; }
        var text = new StringBuilder();
        CharacterContextOpening opening = context.Opening;
        string resultLabel = context.Status == CharacterContextStatus.Terminal ? "终局结果" : "上次推进的结果";
        text.AppendLine($"## 决策回合 {context.ContextSequence} · {resultLabel}");
        if (opening.PreviousAdvance is { } advance) {
            text.AppendLine($"历史区间：{Time(advance.AcceptedAt)} → {Time(opening.DeliveredAt)}；行动：`{CycleViewSnapshot.CommandSyntax(advance.Command)}`（{advance.Description}）。");
            if (advance.OriginPlaceName is { } origin) { text.AppendLine($"出发位置：{origin}。"); }
            text.AppendLine(opening.ActivityState switch {
                CycleOutcomeActivityState.Ongoing => "**本段推进已返回，原活动仍在进行**；继续不会新建活动或重置期限。",
                CycleOutcomeActivityState.Completed => "原活动已结束。",
                _ => "旅程已终止；未完成的活动不再推进。"
            });
            text.AppendLine("上回合的交接文字（角色主观记录，并非游戏确认的事实）：");
            Quote(text, advance.Handoff ?? "（无）");
        } else {
            text.AppendLine(context.Status == CharacterContextStatus.Terminal
                ? "旅程已结束；以下是本次终局交付的记录。"
                : context.ContextSequence == 1 ? "这是首次决策，尚无上次推进的结果。"
                : "本次没有待交付的上次推进记录。");
        }
        if (opening.PreviousAdvance is not null || context.Status == CharacterContextStatus.Terminal) {
            text.AppendLine($"返回原因：{Reasons(opening)}。");
            foreach (CycleNoticeObservation notice in opening.Observation.Notices) {
                text.AppendLine("- 交付时通知：" + PlayView.RenderNotice(notice, context.Status == CharacterContextStatus.Terminal));
            }
        }
        if (opening.Experiences.Count > 0) {
            text.AppendLine("本次交付的经历（以下为发生时的记录，当前值见当前状态）：");
            var snapshot = CycleViewSnapshot.Create(opening.Observation);
            foreach (CharacterExperience experience in opening.Experiences) {
                CycleObservedEvent e = experience.Event;
                text.AppendLine("- " + CycleUpdateRenderer.EventDescription(e));
                foreach (CycleObservedEffect effect in e.Effects) {
                    string label = effect.Key.Category == "inventory" && e.SubjectId == effect.Key.ObjectId && e.SubjectName is { } itemName
                        ? itemName + "库存" : snapshot.Label(effect.Key);
                    text.AppendLine($"  - {label}：{CycleUpdateRenderer.Format(effect.Before, effect.Key)} → {CycleUpdateRenderer.Format(effect.After, effect.Key)}");
                }
            }
        }
        text.AppendLine();
        return text.ToString();
    }

    public static string RenderCurrent(CycleObservation observation, CharacterInteractionState? interactionState = null) {
        if (interactionState is null || !observation.Commands.Any(entry => entry.Command.Kind == CycleCommandKind.Handoff)) { return ""; }
        var text = new StringBuilder();
        text.AppendLine("## 本轮交接");
        if (interactionState.PendingAdvance is { } pending) {
            text.AppendLine($"已接受的行动：`{CycleViewSnapshot.CommandSyntax(pending.Command)}`（{pending.Description}）；正在推进，结果将在下次决策时交付。");
        }
        text.AppendLine();
        text.AppendLine("本轮交接草稿（下一次放行模拟时发送，旧交接不会自动续传）：");
        Quote(text, interactionState.DraftHandoff ?? "（空）");
        text.AppendLine();
        return text.ToString();
    }

    internal static string ItemName(CycleObservation observation, string itemId) =>
        observation.ItemNames.GetValueOrDefault(itemId) ?? itemId switch {
            "item.food" => "食物", "item.water" => "饮水", "item.wood" => "干木", _ => itemId
        };

    private static string Reasons(CharacterContextOpening opening) {
        var kinds = opening.Experiences.Select(experience => experience.Event.Kind).ToHashSet();
        var reasons = new List<string>();
        if (kinds.Contains(CycleObservedEventKind.MoveArrived)) { reasons.Add("抵达"); }
        if (kinds.Contains(CycleObservedEventKind.WaitCompleted)) { reasons.Add("活动完成"); }
        if (kinds.Contains(CycleObservedEventKind.GatherFinished)) { reasons.Add("采集停止"); }
        if (kinds.Contains(CycleObservedEventKind.SurvivalWarning) || opening.Observation.Survival is { PendingWarnings.Count: > 0 }) {
            reasons.Add("身体警告");
        }
        if (opening.Observation.Notices.Any(notice => notice.Notice.Kind == CycleNoticeKind.CollectorJoined)) { reasons.Add("有人加入采集"); }
        if (opening.Observation.Notices.Any(notice => notice.Notice.Kind == CycleNoticeKind.ResourceDepleted)) { reasons.Add("资源耗尽"); }
        if (opening.ActivityState == CycleOutcomeActivityState.Terminal) { reasons.Add("旅程终局"); }
        return reasons.Count > 0 ? string.Join("、", reasons) : "已到可决策边界";
    }

    private static void Quote(StringBuilder text, string value) {
        foreach (string line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')) {
            text.AppendLine("> " + line);
        }
    }
    private static string Time(ModelTime time) => CycleUpdateRenderer.Format(CycleVisibleValue.Numeric(time.Ticks, "ticks"));
}

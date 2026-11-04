using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

/// <summary>Both layouts render the recorded opening and its experiences, never a second accumulated summary.</summary>
internal static class ContextHistoryPlayView {
    public static string RenderHistory(PlaygroundObservation observation, CharacterInteractionState? interactionState = null) {
        return RenderHistory(observation, interactionState?.CurrentContext);
    }

    public static string RenderHistory(PlaygroundObservation observation, CharacterContext? context) {
        if (context is null) { return ""; }
        var text = new StringBuilder();
        CharacterContextOpening opening = context.Opening;
        string resultLabel = context.Status == CharacterContextStatus.Terminal ? "终局结果" : "上次推进的结果";
        text.AppendLine($"## 决策回合 {context.ContextSequence} · {resultLabel}");
        if (opening.PreviousAdvance is { } advance) {
            text.AppendLine($"历史区间：{Time(advance.AcceptedAt)} → {Time(opening.DeliveredAt)}；行动：`{PlaygroundCommandParser.Format(advance.Command)}`（{advance.Description}）。");
            text.AppendLine("上回合提交内容（原始输入，仅为记录）：");
            Quote(text, advance.RawInput);
            if (advance.OriginPlaceName is { } origin) { text.AppendLine($"出发位置：{origin}。"); }
            text.AppendLine(opening.ActivityState switch {
                PlaygroundOutcomeActivityState.Ongoing when opening.Observation.Navigation is not null => opening.Observation.ArrivalDue is null
                    ? "交付时导航尚未结束，已在该中间点等待下一段确认；后续变化见当前活动导航。"
                    : "交付时导航尚未结束，仍在进行当时的路段；后续变化见当前活动导航。",
                PlaygroundOutcomeActivityState.Ongoing when opening.Observation.FollowingLeaderId is not null => "交付时仍在跟随领队；后续关系变化见当前同行状态。",
                PlaygroundOutcomeActivityState.Ongoing => "交付时原活动仍在进行，保留当时的进度与期限；后续变化见当前活动。",
                PlaygroundOutcomeActivityState.Completed => "原活动已结束。",
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
        if (opening.PreviousAdvance is not null || context.Status == CharacterContextStatus.Terminal ||
            opening.Experiences.Any(experience => IsReceivedCommunication(experience.Event, observation.Actor.Id) || experience.Event.TradeChanges.Count > 0)) {
            text.AppendLine($"返回原因：{Reasons(opening)}。");
            foreach (PlaygroundNoticeObservation notice in opening.Observation.Notices) {
                text.AppendLine("- 交付时通知：" + PlayView.RenderNotice(notice, context.Status == CharacterContextStatus.Terminal));
            }
        }
        if (opening.Experiences.Count > 0) {
            text.AppendLine("本次交付的经历（以下为发生时的记录，当前值见当前状态）：");
            var snapshot = PlaygroundViewSnapshot.Create(opening.Observation);
            foreach (CharacterExperience experience in opening.Experiences) {
                PlaygroundObservedEvent e = experience.Event;
                text.AppendLine("- " + PlaygroundUpdateRenderer.EventDescription(e));
                foreach (PlaygroundObservedEffect effect in e.Effects) {
                    string label = effect.Key.Category == "inventory" && e.SubjectId == effect.Key.ObjectId && e.SubjectName is { } itemName
                        ? itemName + "库存" : snapshot.Label(effect.Key);
                    text.AppendLine($"  - {label}：{PlaygroundUpdateRenderer.Format(effect.Before, effect.Key)} → {PlaygroundUpdateRenderer.Format(effect.After, effect.Key)}");
                }
            }
        }
        text.AppendLine();
        return text.ToString();
    }

    public static string RenderCurrent(PlaygroundObservation observation, CharacterInteractionState? interactionState = null) {
        return RenderCurrent(observation, interactionState?.CurrentContext, interactionState?.DraftHandoff);
    }

    public static string RenderCurrent(PlaygroundObservation observation, CharacterContext? context, string? draftHandoff) {
        if (context is null || context.Status == CharacterContextStatus.Terminal ||
            observation.Outcome != PlaygroundExpeditionOutcome.Exploring ||
            !observation.Commands.Any(entry => entry.Command.Kind == PlaygroundCommandKind.Handoff)) { return ""; }
        var text = new StringBuilder();
        text.AppendLine("## 本轮交接");
        text.AppendLine();
        text.AppendLine("本轮交接草稿（下一次放行模拟时发送，旧交接不会自动续传）：");
        Quote(text, draftHandoff ?? "（空）");
        text.AppendLine();
        return text.ToString();
    }

    internal static string ItemName(PlaygroundObservation observation, string itemId) =>
        observation.ItemNames.GetValueOrDefault(itemId, itemId);

    private static string Reasons(CharacterContextOpening opening) {
        var kinds = opening.Experiences.Select(experience => experience.Event.Kind).ToHashSet();
        var reasons = new List<string>();
        if (kinds.Contains(PlaygroundObservedEventKind.MoveArrived)) { reasons.Add("抵达"); }
        if (kinds.Contains(PlaygroundObservedEventKind.WaitCompleted)) { reasons.Add("活动完成"); }
        if (kinds.Contains(PlaygroundObservedEventKind.ProductionCompleted)) { reasons.Add("生产完成"); }
        if (kinds.Contains(PlaygroundObservedEventKind.GatherFinished)) { reasons.Add("采集停止"); }
        if (opening.Observation.FollowingLeaderId is null && kinds.Any(kind =>
            kind is PlaygroundObservedEventKind.FollowEnded or PlaygroundObservedEventKind.FollowersDismissed)) { reasons.Add("跟随结束"); }
        if (opening.Experiences.Any(experience => IsReceivedCommunication(experience.Event, opening.Observation.Actor.Id))) { reasons.Add("收到通信"); }
        if (opening.Experiences.Any(experience => experience.Event.TradeChanges.Count > 0)) { reasons.Add("报价有新进展"); }
        if (kinds.Contains(PlaygroundObservedEventKind.SurvivalWarning)) {
            string attributes = SurvivalWarningView.Attributes(opening.Experiences.Select(e => e.Event));
            reasons.Add(attributes.Length > 0 ? $"身体警告（{attributes}触及警戒线）" : "身体警告");
        }
        if (opening.Observation.Notices.Any(notice => notice.Notice.Kind == PlaygroundNoticeKind.CollectorJoined)) { reasons.Add("有人加入采集"); }
        if (opening.Observation.Notices.Any(notice => notice.Notice.Kind == PlaygroundNoticeKind.ResourceDepleted)) { reasons.Add("资源耗尽"); }
        if (opening.ActivityState == PlaygroundOutcomeActivityState.Terminal) { reasons.Add("旅程终局"); }
        return reasons.Count > 0 ? string.Join("、", reasons) : "已到可决策边界";
    }

    private static bool IsReceivedCommunication(PlaygroundObservedEvent observed, string actorId) =>
        observed.Communication?.RecipientId == actorId ||
        observed.Kind == PlaygroundObservedEventKind.SpeechShouted && observed.ObserverId == actorId && observed.SubjectId != actorId;

    private static void Quote(StringBuilder text, string value) {
        text.AppendLine("> " + CharacterContextRenderer.DataLiteral(value));
    }
    private static string Time(ModelTime time) => PlaygroundUpdateRenderer.Format(PlaygroundVisibleValue.Numeric(time.Ticks, "ticks"));
}

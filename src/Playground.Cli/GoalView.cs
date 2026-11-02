using DramaBoard.Goals;
using System.Globalization;
using System.Text;
using DramaBoard.Playground;

/// <summary>Projects only recorded, actor-private goal material. No world or mutable controller state is consulted.</summary>
internal static class GoalView {
    public static string RenderDefault(GoalSet? goals) {
        if (goals is null) { return ""; }
        var text = new StringBuilder();
        text.AppendLine("## 目标记录");
        text.AppendLine("角色自述；库存、危险与行动结果以世界观察为准。修改目标不取消活动、不确认警告。");
        text.AppendLine($"未结束 {goals.UnfinishedCount}/{GoalLimits.MaximumUnfinished}；保留目标 {goals.Goals.Count}/{GoalLimits.MaximumGoals}；关联 {goals.Links.Count}/{GoalLimits.MaximumLinks}。");
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (goals.Focus.Count == 0) {
            text.AppendLine("当前焦点：无。");
        } else {
            text.AppendLine("当前焦点路径：");
            foreach (string id in goals.Focus) {
                Goal goal = goals.Goals.Single(goal => goal.Id == id);
                text.AppendLine("- " + Summary(goal));
            }
            for (int i = 1; i < goals.Focus.Count; i++) {
                GoalLink link = goals.Links.Single(link => link.ParentId == goals.Focus[i - 1] && link.ChildId == goals.Focus[i]);
                text.AppendLine($"  {link.Id}：{link.ParentId} → {link.ChildId}；理由：{Data(link.Reason)}");
            }
            Goal focus = goals.Goals.Single(goal => goal.Id == goals.Focus[^1]);
            AppendFields(text, focus);
            shown.Add(focus.Id);
            AppendRelations(text, goals, focus.Id, shown);
        }
        text.AppendLine("### 未结束事项（完整目录）");
        foreach (Goal goal in Ordered(goals.Goals).Where(goal => IsUnfinished(goal.Status))) {
            // A child or ancestor in the focus explanation is still an independently visible commitment.
            text.AppendLine("- " + Summary(goal) + (goals.Focus.LastOrDefault() == goal.Id ? "（当前焦点）" : ""));
            Optional(text, "挂起原因", goal.PauseReason);
            Optional(text, "复查", goal.Revisit);
            var parents = OrderedLinks(goals.Links.Where(link => link.ChildId == goal.Id)).ToArray();
            if (parents.Length > 0) { text.AppendLine("  来源：" + string.Join("、", parents.Select(link => $"{link.ParentId}（{link.Id}）"))); }
        }
        if (goals.UnfinishedCount == 0) { text.AppendLine("- 无。"); }
        Goal[] recent = goals.Goals.Where(goal => !IsUnfinished(goal.Status))
            .OrderByDescending(goal => goal.FinishedContextSequence).ThenByDescending(goal => goal.FinishedOperationSequence)
            .ThenBy(goal => Number(goal.Id)).Take(GoalLimits.RecentFinishedCount).ToArray();
        text.AppendLine("### 最近结束");
        foreach (Goal goal in recent.Where(goal => !shown.Contains(goal.Id))) { AppendConclusion(text, goal); }
        if (recent.Length == 0) { text.AppendLine("- 无。"); }
        else if (recent.All(goal => shown.Contains(goal.Id))) { text.AppendLine("- 已在上述关联结论中显示。"); }
        text.AppendLine("整理目录：`show-goal {}`；详情：`show-goal {\"id\":\"G1\"}`（替换为实际 ID）。结束不删除；先 unlink 再 forget 清理孤立的已结束目标。");
        text.AppendLine();
        return text.ToString();
    }

    public static string RenderDetail(GoalSet goals, string? id) {
        var text = new StringBuilder();
        if (id is null) {
            text.AppendLine("### 目标整理目录");
            foreach (Goal goal in Ordered(goals.Goals)) { text.AppendLine("- " + Summary(goal)); }
            if (goals.Goals.Count == 0) { text.AppendLine("- 无目标。"); }
            foreach (GoalLink link in OrderedLinks(goals.Links)) { text.AppendLine($"- {link.Id}：{link.ParentId} → {link.ChildId}"); }
            if (goals.Links.Count == 0) { text.AppendLine("- 无关联。"); }
        } else {
            Goal goal = goals.Goals.Single(goal => goal.Id == id);
            text.AppendLine("### 目标详情 " + goal.Id);
            text.AppendLine(Summary(goal));
            AppendFields(text, goal);
            AppendRelations(text, goals, id, []);
            text.AppendLine("敏感修订（有限保留，并非完整历史）：");
            foreach (GoalRevision revision in goal.Revisions) {
                text.AppendLine($"- 回合 {revision.ContextSequence} / 操作 {revision.OperationSequence}；原状态 {revision.OldStatus}；理由 {Data(revision.Reason)}");
                text.AppendLine($"  原标题：{Data(revision.OldTitle)}；原标准：{Data(revision.OldCriteria)}");
                Optional(text, "原结束说明", revision.OldEndNote);
            }
            if (goal.Revisions.Count == 0) { text.AppendLine("- 无。"); }
        }
        text.AppendLine();
        return text.ToString();
    }

    public static string RenderCurrent(CharacterContext context, long throughItemSequence = long.MaxValue, bool includeHelp = true) {
        CharacterContextItem[] items = context.Items.Where(item => item.ItemSequence <= throughItemSequence).ToArray();
        PlaygroundObservation observation = items.LastOrDefault(item => item.Observation is not null)?.Observation ?? context.Opening.Observation;
        if (observation.Goals is null) { return ""; }
        var text = new StringBuilder(RenderDefault(observation.Goals));
        CharacterContextItem[] receipts = items.Where(item => item.GoalOperation is not null).TakeLast(GoalLimits.RecentReceiptCount).ToArray();
        if (receipts.Length > 0) {
            text.AppendLine("### 最近目标操作回执");
            foreach (CharacterContextItem item in receipts) { text.Append(RenderReceipt(item)); }
        }
        text.Append(RenderQuery(context, throughItemSequence));
        if (includeHelp && context.Status == CharacterContextStatus.Open) { text.Append(RenderHelp(observation)); }
        return text.ToString();
    }

    public static string RenderQuery(CharacterContext context, long throughItemSequence = long.MaxValue) {
        CharacterContextItem[] items = context.Items.Where(item => item.ItemSequence <= throughItemSequence).ToArray();
        CharacterContextItem? show = items.LastOrDefault(item => item.GoalOperation is { Kind: GoalOperationKind.Show, Success: true });
        if (show is null) { return ""; }
        string label = show.GoalOperation!.GoalId ?? "整理目录";
        string header = $"### 最近查询：{label}（操作 {show.OperationSequence}）\n";
        if (items.Any(item => item.ItemSequence > show.ItemSequence && item.GoalOperation is { Changed: true })) {
            return header + "详情已过期：查询后目标记录发生变化，可重新 show-goal 查询。\n\n";
        }
        return header + RenderDetail(show.Observation!.Goals!, show.GoalOperation.GoalId);
    }

    public static string RenderReceipt(CharacterContextItem item) {
        GoalReceipt receipt = item.GoalOperation!;
        string result = !receipt.Success ? "拒绝" : receipt.Kind == GoalOperationKind.Show ? "已查询" : receipt.Changed ? "已改变" : "未改变";
        string ids = string.Join(" / ", new[] { receipt.GoalId, receipt.LinkId }.Where(id => id is not null));
        var text = new StringBuilder($"- 操作 {item.OperationSequence} · {receipt.Kind} · {(ids.Length == 0 ? "目标集合" : ids)} · {result}：{Data(receipt.Message)}\n");
        if (receipt.ChangedFields.Count > 0) { text.AppendLine("  变更字段：" + string.Join("、", receipt.ChangedFields.Select(Data))); }
        if (!receipt.FocusBefore.SequenceEqual(receipt.FocusAfter)) {
            static string Path(IReadOnlyList<string> path) => path.Count == 0 ? "无" : "[" + string.Join(" → ", path) + "]";
            text.AppendLine($"  焦点：原 {Path(receipt.FocusBefore)}；现 {Path(receipt.FocusAfter)}");
        }
        return text.ToString();
    }

    public static string RenderHelp(PlaygroundObservation observation) {
        if (observation.Goals is null || observation.Outcome != PlaygroundExpeditionOutcome.Exploring) { return ""; }
        return "### 目标操作（零耗时）\n"
            + "每次提交一个命令和严格 JSON 对象；以下 ID 是示例，须换成现有 ID。未提供字段保留，progress/nextStep/revisit 的 null 表示清空。\n"
            + "状态规则：pause 仅 Open；resume 仅 Paused；finish 仅 Open/Paused；reopen 仅 Completed/Abandoned；focus 路径末端须 Open。\n"
            + "finish 的 outcome 可选 completed 或 abandoned；结束记录仅可带 reason 勘误 endNote，修改其他正文须先 reopen。\n"
            + "```text\n"
            + "create-goal {\"title\":\"准备饮水\",\"criteria\":\"出发时携带两份水\",\"focus\":true}\n"
            + "create-goal {\"title\":\"确认水源\",\"criteria\":\"查明是否有水并记录依据\",\"parent\":\"G1\",\"linkReason\":\"决定补给路线\"}\n"
            + "update-goal {\"id\":\"G1\",\"progress\":\"正在准备\",\"nextStep\":\"前往水源\",\"revisit\":null}\n"
            + "update-goal {\"id\":\"G1\",\"criteria\":\"出发时携带三份水\",\"reason\":\"路线比预期长\"}\n"
            + "link-goal {\"parent\":\"G1\",\"child\":\"G2\",\"reason\":\"共享调查结论\"}\n"
            + "update-goal-link {\"id\":\"L1\",\"reason\":\"新的来源说明\"}\n"
            + "unlink-goal {\"id\":\"L1\"}\n"
            + "focus-goal {\"id\":\"G2\",\"via\":[\"G1\",\"G2\"]}\n"
            + "focus-goal {\"id\":null}\n"
            + "pause-goal {\"id\":\"G1\",\"reason\":\"暂时缺少水源\",\"revisit\":\"发现水源后重看\"}\n"
            + "resume-goal {\"id\":\"G1\"}\n"
            + "finish-goal {\"id\":\"G1\",\"outcome\":\"completed\",\"note\":\"记录完成依据\"}\n"
            + "reopen-goal {\"id\":\"G1\",\"reason\":\"此前误判完成\"}\n"
            + "forget-goal {\"id\":\"G1\"}\n"
            + "show-goal {}\nshow-goal {\"id\":\"G1\"}\n```\n"
            + $"正文上限（Unicode 字符）：title {GoalLimits.TitleScalars}、criteria {GoalLimits.CriteriaScalars}、progress {GoalLimits.ProgressScalars}、nextStep/revisit {GoalLimits.NextStepScalars}、挂起/结束说明 {GoalLimits.StateNoteScalars}、关联/修订理由 {GoalLimits.ReasonScalars}。\n"
            + "只能维护自己的目标；结束与挂起不影响活动，show 也记录一次交互，不确认警告或返还调用预算。\n\n";
    }

    private static void AppendFields(StringBuilder text, Goal goal) {
        text.AppendLine("标准：" + Data(goal.Criteria));
        Optional(text, "进展/依据", goal.Progress);
        Optional(text, "下一步", goal.NextStep);
        Optional(text, "复查", goal.Revisit);
        Optional(text, "挂起原因", goal.PauseReason);
        Optional(text, "结束说明", goal.EndNote);
    }

    private static void AppendRelations(StringBuilder text, GoalSet goals, string id, HashSet<string> shown) {
        text.AppendLine("直接父来源 / 子目标（不代表执行顺序或自动完成条件）：");
        foreach (GoalLink link in OrderedLinks(goals.Links.Where(link => link.ParentId == id || link.ChildId == id))) {
            string otherId = link.ParentId == id ? link.ChildId : link.ParentId;
            Goal other = goals.Goals.Single(goal => goal.Id == otherId);
            text.AppendLine($"- {link.Id}：{link.ParentId} → {link.ChildId}；理由：{Data(link.Reason)}；{Summary(other)}");
            Optional(text, "结论", other.EndNote);
            shown.Add(other.Id);
        }
    }

    private static void AppendConclusion(StringBuilder text, Goal goal) {
        text.AppendLine("- " + Summary(goal));
        Optional(text, "结束说明", goal.EndNote);
    }
    private static void Optional(StringBuilder text, string label, string? value) {
        if (value is not null) { text.AppendLine($"  {label}：{Data(value)}"); }
    }
    private static bool IsUnfinished(GoalStatus status) => status is GoalStatus.Open or GoalStatus.Paused;
    private static string Summary(Goal goal) => $"{goal.Id} [{goal.Status}] {Data(goal.Title)}";
    // JSON's safe encoder escapes non-BMP characters as two six-character sequences. Restore only
    // genuine surrogate-pair escapes; controls, Markdown delimiters and literal backslashes stay encoded.
    // This keeps scalar-limited goal prose readable without a sixfold UTF-16 material expansion.
    internal static string Data(string value) {
        string encoded = CharacterContextRenderer.DataLiteral(value);
        var text = new StringBuilder(encoded.Length);
        for (int i = 0; i < encoded.Length; i++) {
            if (encoded[i] == '\\' && i + 1 < encoded.Length) {
                if (i + 11 < encoded.Length && encoded[i + 1] == 'u' && encoded[i + 6] == '\\' && encoded[i + 7] == 'u' &&
                    ushort.TryParse(encoded.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort high) &&
                    ushort.TryParse(encoded.AsSpan(i + 8, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort low) &&
                    char.IsHighSurrogate((char)high) && char.IsLowSurrogate((char)low)) {
                    text.Append((char)high).Append((char)low);
                    i += 11;
                } else {
                    text.Append(encoded[i]).Append(encoded[++i]);
                }
            } else { text.Append(encoded[i]); }
        }
        return text.ToString();
    }
    private static long Number(string id) => long.Parse(id.AsSpan(1), CultureInfo.InvariantCulture);
    private static IOrderedEnumerable<Goal> Ordered(IEnumerable<Goal> goals) => goals.OrderBy(goal => Number(goal.Id));
    private static IOrderedEnumerable<GoalLink> OrderedLinks(IEnumerable<GoalLink> links) => links.OrderBy(link => Number(link.Id));
}

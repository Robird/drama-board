using System.Globalization;
using System.Text.Json;

namespace DramaBoard.ResourceCycle;

/// <summary>Pure graph editing. Callers publish the returned state and receipt together; no world time or activity is consulted.</summary>
public static class CycleGoalRules {
    public static CycleGoalReceipt Reject(CycleGoalOperationKind kind, string error) => new(kind, false, false, error);

    public static CycleGoalOperationResult Apply(CycleGoalSet before, CycleGoalCommand command, long contextSequence, long operationSequence) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(command);
        before.Validate();
        if (!Enum.IsDefined(command.Kind)) { throw new ArgumentException("Unknown goal operation kind.", nameof(command)); }
        if (!CycleGoalCommandParser.TryRead(command, out Dictionary<string, JsonElement> fields, out string? parseError)) {
            return new(before, Reject(command.Kind, parseError!), parseError);
        }
        if (contextSequence <= 0 || operationSequence <= 0) {
            return new(before, Reject(command.Kind, "目标操作需要有效的 Context/操作序号。"), "目标操作需要有效的 Context/操作序号。");
        }
        (long, long) operationPosition = (contextSequence, operationSequence);
        if (before.Goals.Any(goal => goal.FinishedContextSequence > 0 &&
                operationPosition.CompareTo((goal.FinishedContextSequence, goal.FinishedOperationSequence)) <= 0 ||
                goal.Revisions.Any(revision => operationPosition.CompareTo((revision.ContextSequence, revision.OperationSequence)) <= 0))) {
            const string sequenceError = "目标操作序号必须晚于所有已有完成/修订记录。";
            return new(before, Reject(command.Kind, sequenceError), sequenceError);
        }

        List<CycleGoal> goals = before.Goals.ToList();
        List<CycleGoalLink> links = before.Links.ToList();
        List<string> focus = before.Focus.ToList();
        long nextGoal = before.NextGoalId, nextLink = before.NextLinkId;
        string? goalId = null, linkId = null;
        var changedFields = new List<string>();
        bool trimmedRevision = false;
        string? Text(string name) => fields.TryGetValue(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? CycleGoalLimits.Normalize(value.GetString()) : null;
        CycleGoal Goal(string id) => goals.FirstOrDefault(goal => goal.Id == id) ?? throw new Rejected("目标不存在；请 show-goal {} 查看本角色目录。");
        CycleGoalLink Link(string id) => links.FirstOrDefault(link => link.Id == id) ?? throw new Rejected("关联不存在；请 show-goal {} 查看本角色目录。");
        string AllocateGoal() {
            long allocated = nextGoal;
            if (nextGoal == long.MaxValue) { throw new Rejected("Goal 身份序号已耗尽。"); }
            nextGoal = checked(nextGoal + 1); return "G" + allocated.ToString(CultureInfo.InvariantCulture);
        }
        string AllocateLink() {
            long allocated = nextLink;
            if (nextLink == long.MaxValue) { throw new Rejected("Link 身份序号已耗尽。"); }
            nextLink = checked(nextLink + 1); return "L" + allocated.ToString(CultureInfo.InvariantCulture);
        }
        void RequireUnfinishedSlot() {
            if (goals.Count(goal => goal.IsUnfinished) >= CycleGoalLimits.MaximumUnfinished) {
                throw new Rejected($"未结束目标上限为 {CycleGoalLimits.MaximumUnfinished}；Open/Paused 都占额度，请先整理。");
            }
        }
        void RequireLinkSlot() {
            if (links.Count >= CycleGoalLimits.MaximumLinks) { throw new Rejected($"关联上限为 {CycleGoalLimits.MaximumLinks}，请先 unlink-goal。"); }
        }
        List<CycleGoalRevision> Revisions(CycleGoal old, bool sensitive, string? reason) {
            var revisions = old.Revisions.ToList();
            if (!sensitive) { return revisions; }
            if (reason is null) { throw new Rejected("修改目的/标准、重开或勘误结束说明必须提供 reason。"); }
            (long, long) current = (contextSequence, operationSequence);
            if (revisions.LastOrDefault() is { } last && current.CompareTo((last.ContextSequence, last.OperationSequence)) <= 0 ||
                old.FinishedContextSequence > 0 && current.CompareTo((old.FinishedContextSequence, old.FinishedOperationSequence)) <= 0) {
                throw new Rejected("修订序号必须晚于已有记录。");
            }
            revisions.Add(new(old.Status, old.Title, old.Criteria, old.EndNote, reason, contextSequence, operationSequence));
            if (revisions.Count > CycleGoalLimits.MaximumRevisions) { revisions.RemoveAt(0); trimmedRevision = true; }
            return revisions;
        }
        void Put(CycleGoal replacement) { goals[goals.FindIndex(goal => goal.Id == replacement.Id)] = replacement; }
        void RetreatIfNeeded() {
            if (focus.Count == 0 || Goal(focus[^1]).Status == CycleGoalStatus.Open) { return; }
            int index = focus.Count - 2;
            while (index >= 0 && Goal(focus[index]).Status != CycleGoalStatus.Open) { index--; }
            focus = focus.Take(index + 1).ToList();
        }

        try {
            switch (command.Kind) {
                case CycleGoalOperationKind.Create: {
                    if (goals.Count >= CycleGoalLimits.MaximumGoals) { throw new Rejected($"保留目标上限为 {CycleGoalLimits.MaximumGoals}，请先 unlink/forget 已结束记录。"); }
                    RequireUnfinishedSlot();
                    string? parent = Text("parent");
                    if (parent is not null) { Goal(parent); RequireLinkSlot(); }
                    goalId = AllocateGoal();
                    goals.Add(new(goalId, Text("title")!, Text("criteria")!, progress: Text("progress"), nextStep: Text("nextStep"), revisit: Text("revisit")));
                    changedFields.Add("goal");
                    if (parent is not null) {
                        linkId = AllocateLink(); links.Add(new(linkId, parent, goalId, Text("linkReason")!)); changedFields.Add("link");
                    }
                    if (fields.TryGetValue("focus", out JsonElement selected) && selected.GetBoolean()) {
                        focus = parent is null ? [goalId] : [parent, goalId];
                    }
                    break;
                }
                case CycleGoalOperationKind.Update: {
                    goalId = Text("id")!;
                    CycleGoal old = Goal(goalId);
                    foreach (string name in fields.Keys.Where(name => name is not ("id" or "reason"))) {
                        if (!old.IsUnfinished && name != "endNote" || name == "endNote" && old.IsUnfinished ||
                            name == "pauseReason" && old.Status != CycleGoalStatus.Paused) { throw new Rejected($"当前状态不允许修改 {name}。"); }
                    }
                    string? Value(string name, string? prior) => fields.ContainsKey(name) ? Text(name) : prior;
                    string title = Value("title", old.Title)!, criteria = Value("criteria", old.Criteria)!;
                    string? progress = Value("progress", old.Progress), nextStep = Value("nextStep", old.NextStep), revisit = Value("revisit", old.Revisit),
                        pauseReason = Value("pauseReason", old.PauseReason), endNote = Value("endNote", old.EndNote);
                    bool sensitive = title != old.Title || criteria != old.Criteria || endNote != old.EndNote;
                    var newValues = new Dictionary<string, (string? Before, string? After)> {
                        ["title"] = (old.Title, title), ["criteria"] = (old.Criteria, criteria), ["progress"] = (old.Progress, progress),
                        ["nextStep"] = (old.NextStep, nextStep), ["revisit"] = (old.Revisit, revisit),
                        ["pauseReason"] = (old.PauseReason, pauseReason), ["endNote"] = (old.EndNote, endNote)
                    };
                    changedFields.AddRange(newValues.Where(pair => pair.Value.Before != pair.Value.After).Select(pair => pair.Key));
                    Put(new(goalId, title, criteria, old.Status, progress, nextStep, revisit, pauseReason, endNote,
                        Revisions(old, sensitive, Text("reason")), old.FinishedContextSequence, old.FinishedOperationSequence));
                    break;
                }
                case CycleGoalOperationKind.Link: {
                    string parent = Text("parent")!, child = Text("child")!;
                    Goal(parent); Goal(child);
                    if (parent == child || links.Any(link => link.ParentId == parent && link.ChildId == child)) { throw new Rejected("不能自身关联或重复建立同一父子关联。"); }
                    var seen = new HashSet<string>();
                    bool Reaches(string from) => from == parent || seen.Add(from) && links.Where(link => link.ParentId == from).Any(link => Reaches(link.ChildId));
                    if (Reaches(child)) { throw new Rejected("关联会形成环，已拒绝。"); }
                    RequireLinkSlot();
                    linkId = AllocateLink(); goalId = child;
                    links.Add(new(linkId, parent, child, Text("reason")!)); changedFields.Add("link");
                    break;
                }
                case CycleGoalOperationKind.UpdateLink: {
                    linkId = Text("id")!;
                    CycleGoalLink old = Link(linkId); goalId = old.ChildId;
                    string reason = Text("reason")!;
                    if (reason != old.Reason) { links[links.IndexOf(old)] = old with { Reason = reason }; changedFields.Add("linkReason"); }
                    break;
                }
                case CycleGoalOperationKind.Unlink: {
                    linkId = Text("id")!;
                    CycleGoalLink old = Link(linkId); goalId = old.ChildId;
                    links.Remove(old); changedFields.Add("link");
                    int childIndex = focus.IndexOf(old.ChildId);
                    if (childIndex > 0 && focus[childIndex - 1] == old.ParentId) { focus = focus.Skip(childIndex).ToList(); }
                    break;
                }
                case CycleGoalOperationKind.Focus: {
                    goalId = Text("id");
                    if (goalId is null) { focus = []; break; }
                    if (Goal(goalId).Status != CycleGoalStatus.Open) { throw new Rejected("焦点末端必须是 Open 目标。"); }
                    List<string> path = fields.TryGetValue("via", out JsonElement via) ? via.EnumerateArray().Select(value => value.GetString()!).ToList() : [goalId];
                    if (path[^1] != goalId || path.Any(id => !goals.Any(goal => goal.Id == id)) ||
                        path.Zip(path.Skip(1)).Any(pair => !links.Any(link => link.ParentId == pair.First && link.ChildId == pair.Second))) {
                        throw new Rejected("via 必须是以目标结尾的实际父子路径。");
                    }
                    focus = path; break;
                }
                case CycleGoalOperationKind.Pause:
                case CycleGoalOperationKind.Resume:
                case CycleGoalOperationKind.Finish:
                case CycleGoalOperationKind.Reopen: {
                    goalId = Text("id")!;
                    CycleGoal old = Goal(goalId);
                    bool valid = command.Kind switch {
                        CycleGoalOperationKind.Pause => old.Status == CycleGoalStatus.Open,
                        CycleGoalOperationKind.Resume => old.Status == CycleGoalStatus.Paused,
                        CycleGoalOperationKind.Finish => old.IsUnfinished,
                        _ => !old.IsUnfinished
                    };
                    if (!valid) { throw new Rejected("当前目标状态不允许这项转换。"); }
                    if (command.Kind == CycleGoalOperationKind.Reopen) { RequireUnfinishedSlot(); }
                    CycleGoalStatus status = command.Kind switch {
                        CycleGoalOperationKind.Pause => CycleGoalStatus.Paused,
                        CycleGoalOperationKind.Finish => Text("outcome") == "completed" ? CycleGoalStatus.Completed : CycleGoalStatus.Abandoned,
                        _ => CycleGoalStatus.Open
                    };
                    bool finishing = command.Kind == CycleGoalOperationKind.Finish;
                    Put(new(goalId, old.Title, old.Criteria, status, old.Progress, old.NextStep,
                        command.Kind == CycleGoalOperationKind.Pause && fields.ContainsKey("revisit") ? Text("revisit") : old.Revisit,
                        status == CycleGoalStatus.Paused ? Text("reason") : null, finishing ? Text("note") : null,
                        Revisions(old, command.Kind == CycleGoalOperationKind.Reopen, Text("reason")),
                        finishing ? contextSequence : 0, finishing ? operationSequence : 0));
                    changedFields.Add("status"); RetreatIfNeeded(); break;
                }
                case CycleGoalOperationKind.Forget: {
                    goalId = Text("id")!;
                    CycleGoal old = Goal(goalId);
                    if (old.IsUnfinished) { throw new Rejected("只能 forget 已结束且孤立的目标。"); }
                    string[] blockers = links.Where(link => link.ParentId == goalId || link.ChildId == goalId).Select(link => link.Id).ToArray();
                    if (blockers.Length > 0) { throw new Rejected("仍有关联 " + string.Join(", ", blockers) + "；请先 unlink-goal。"); }
                    goals.Remove(old); changedFields.Add("goal"); break;
                }
                case CycleGoalOperationKind.Show:
                    goalId = Text("id"); if (goalId is not null) { Goal(goalId); } break;
            }

            if (!focus.SequenceEqual(before.Focus)) { changedFields.Add("focus"); }
            var after = new CycleGoalSet(goals, links, focus, nextGoal, nextLink);
            after.Validate();
            bool changed = !after.SameAs(before);
            if (!changed) { after = before; }
            string message = command.Kind == CycleGoalOperationKind.Show ? goalId is null ? "已查看目标整理目录。" : "已查看目标详情。" :
                changed ? $"目标操作成功；未结束 {after.UnfinishedCount}/{CycleGoalLimits.MaximumUnfinished}，保留 {after.Goals.Count}/{CycleGoalLimits.MaximumGoals}，关联 {after.Links.Count}/{CycleGoalLimits.MaximumLinks}。" : "未改变。";
            if (trimmedRevision) { message += $"敏感修订仅保留最近 {CycleGoalLimits.MaximumRevisions} 条。"; }
            var receipt = new CycleGoalReceipt(command.Kind, true, changed, message, goalId, linkId,
                changedFields, before.Focus, after.Focus);
            receipt.Validate();
            return new(after, receipt, null);
        } catch (Rejected rejected) {
            if (command.Kind == CycleGoalOperationKind.Create) { goalId = null; linkId = null; }
            return new(before, new(command.Kind, false, false, rejected.Message, goalId, linkId,
                focusBefore: before.Focus, focusAfter: before.Focus), rejected.Message);
        }
    }

    private sealed class Rejected(string message) : Exception(message) { }
}

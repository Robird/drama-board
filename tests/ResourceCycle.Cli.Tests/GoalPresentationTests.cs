using System.Text;
using DramaBoard.Goals;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;
using DramaBoard.ResourceCycle.Completion;
using Xunit.Abstractions;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class GoalPresentationTests(ITestOutputHelper output) {
    [Fact]
    public void Focus_receipt_distinguishes_previous_path_from_fallback_or_another_source_branch() {
        string Receipt(string[] before, string[] after) => GoalView.RenderReceipt(new CharacterContextItem(
            1, 1, "focus operation", "call", "operation", "焦点已改变", observation: Observe(OneGoal("进展")),
            goalOperation: new(GoalOperationKind.Focus, true, true, "焦点已改变", goalId: "G2",
                changedFields: ["focus"], focusBefore: before, focusAfter: after)));

        Assert.Contains("焦点：原 [G3 → G2]；现 [G3]", Receipt(["G3", "G2"], ["G3"]));
        Assert.Contains("焦点：原 [G3 → G2]；现 [G4 → G2]", Receipt(["G3", "G2"], ["G4", "G2"]));
        Assert.Contains("焦点：原 [G3 → G2]；现 无", Receipt(["G3", "G2"], []));
    }

    [Fact]
    public void Goal_data_preserves_non_BMP_prose_but_not_literal_escape_sequences_or_control_structure() {
        const string raw = "树🌳\\uD83C\\uDF33\n## injected\u001b[2J`";
        string encoded = GoalView.Data(raw);
        Assert.Contains("树🌳", encoded);
        Assert.Contains("\\\\uD83C\\\\uDF33", encoded);
        Assert.DoesNotContain('\u001b', encoded);
        Assert.DoesNotContain('\n', encoded);
        Assert.Equal(raw, System.Text.Json.JsonSerializer.Deserialize<string>(encoded));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_unfinished_goal_revisit_and_parent_is_visible_and_old_shared_conclusion_survives_recent_window(bool decision) {
        var goals = new GoalSet([
            new("G1", "补水目的", "有水", revisit: "发现水源时重看"),
            new("G2", "会合目的", "会合", GoalStatus.Paused, revisit: "看到同伴时检查", pauseReason: "同伴不在"),
            new("G3", "共同调查", "查明水源", GoalStatus.Completed, endNote: "此地没有可用水源，清晨已核实", finishedContextSequence: 1, finishedOperationSequence: 1),
            new("G4", "共享未结束事项", "继续调查"),
            new("G5", "后来结束一", "达成", GoalStatus.Completed, endNote: "后来一", finishedContextSequence: 2, finishedOperationSequence: 1),
            new("G6", "后来结束二", "达成", GoalStatus.Completed, endNote: "后来二", finishedContextSequence: 2, finishedOperationSequence: 2),
            new("G7", "后来结束三", "达成", GoalStatus.Completed, endNote: "后来三", finishedContextSequence: 2, finishedOperationSequence: 3)
        ], [new("L1", "G1", "G3", "决定补给路线"), new("L2", "G2", "G3", "决定会合路线"),
            new("L3", "G1", "G4", "来源甲"), new("L4", "G2", "G4", "来源乙")], ["G1"], 8, 5);
        goals.Validate();
        CycleObservation observation = Observe(goals);
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        string text = layout.Render(observation);
        Assert.Contains("G4 [Open]", text);
        Assert.Contains("G1（L3）", text);
        Assert.Contains("G2（L4）", text);
        Assert.Contains("发现水源时重看", text);
        Assert.Contains("看到同伴时检查", text);
        Assert.Contains("同伴不在", text);
        Assert.Contains("此地没有可用水源，清晨已核实", text);
        Assert.Contains("此地没有可用水源，清晨已核实", GoalView.RenderDetail(goals, "G2"));
        foreach (string command in new[] { "create-goal", "link-goal", "unlink-goal", "focus-goal", "reopen-goal", "forget-goal", "show-goal {}" }) {
            Assert.Contains(command, layout.RenderActionHelp(observation));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Maximum_graph_longest_path_and_unicode_fit_host_budget_without_cutting_unfinished_items(bool decision) {
        GoalSet goals = MaximumGraph();
        goals.Validate();
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        string detail = GoalView.RenderDetail(goals, "G1");
        var state = State([GoalItem(1, GoalOperationKind.Show, goals),
            GoalItem(2, GoalOperationKind.Update, goals), GoalItem(3, GoalOperationKind.Show, goals)], contextSequence: 3);
        string rendered = CharacterContextRenderer.RenderFull(state, layout);
        int oldEncodingLength = rendered.Length + rendered.EnumerateRunes().Count(rune => !rune.IsBmp) * 10;
        output.WriteLine($"{layout.Id}: maximum graph + detail UTF-16={rendered.Length}, scalars={rendered.EnumerateRunes().Count()}, original surrogate-escape UTF-16={oldEncodingLength}");
        Assert.True(rendered.Length < new LlmControllerOptions().MaxContextCharacters, $"Rendered {rendered.Length} UTF-16 code units.");
        for (int i = 21; i <= 32; i++) { Assert.Contains($"G{i} [", rendered); }
        Assert.Contains("32/32", rendered);
        Assert.Contains("48/48", rendered);
        Assert.Contains("12/12", rendered);
        Assert.Contains("当前状态与环境", rendered);
        Assert.Contains("wait", rendered);
        Assert.DoesNotContain('\u001b', rendered);
        Assert.DoesNotContain("\n## injected", rendered);
        Assert.Contains("\\n", rendered);
        Assert.Contains("敏感修订", detail);
        Assert.Contains("- 操作 1 ·", rendered);
        Assert.Contains("- 操作 2 ·", rendered);
        Assert.Contains("- 操作 3 ·", rendered);
    }

    [Fact]
    public void Direct_delta_compares_frozen_goal_graphs_and_hides_no_change() {
        CycleObservation before = Observe(GoalSet.Empty);
        CycleObservation after = Observe(new([new("G1", "新的目的", "标准")], nextGoalId: 2));
        CycleViewUpdate Changed(CycleObservation a, CycleObservation b) => CycleViewUpdate.Create(
            CycleViewSnapshot.Create(a), CycleViewSnapshot.Create(b), [], PlayDeliveryPhase.ControllerInput);
        Assert.Contains("新的目的", CycleUpdateRenderer.Render(Changed(before, after)));
        Assert.DoesNotContain("## 目标记录", CycleUpdateRenderer.Render(Changed(after, after)));
        Assert.DoesNotContain("新的目的", GoalView.RenderDefault(before.Goals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_material_has_one_current_graph_and_bounded_receipts_without_old_raw_edit_text(bool decision) {
        var old = OneGoal("long-old-prose-only-in-history");
        var latest = OneGoal("current-progress");
        CharacterContextItem[] items = [
            GoalItem(1, GoalOperationKind.Create, old, changed: true, raw: "create-goal long-old-prose-only-in-history"),
            GoalItem(2, GoalOperationKind.Show, old),
            OrdinaryItem(3, old, "use item.water", "保留普通结果与警告"),
            GoalItem(4, GoalOperationKind.Update, old, message: "无变化回执"),
            GoalItem(5, GoalOperationKind.Show, null, id: "G99", success: false, message: "不存在 G99"),
            GoalItem(6, GoalOperationKind.Update, latest, changed: true, raw: "update-goal raw-should-not-return")
        ];
        CharacterInteractionState state = State(items);
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        string text = CharacterContextRenderer.RenderFull(state, layout);
        Assert.Equal(1, text.Split("## 目标记录", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("long-old-prose-only-in-history", text);
        Assert.DoesNotContain("raw-should-not-return", text);
        Assert.Contains("current-progress", text);
        Assert.Contains("use item.water", text);
        Assert.Contains("保留普通结果与警告", text);
        Assert.Contains("不存在 G99", text);
        Assert.DoesNotContain("- 操作 1 ·", text);
        Assert.DoesNotContain("- 操作 2 ·", text);
        Assert.Contains("- 操作 4 ·", text);
        Assert.Contains("- 操作 5 ·", text);
        Assert.Contains("- 操作 6 ·", text);
        Assert.Contains("详情已过期", text);
        string preview = CharacterContextRenderer.RenderLatestState(state, layout);
        Assert.Equal(1, preview.Split("## 目标记录", StringSplitOptions.None).Length - 1);
        Assert.Contains("详情已过期", preview);

        string prefix = CharacterContextRenderer.RenderBodyThrough(state, layout, 2);
        Assert.Contains("long-old-prose-only-in-history", prefix);
        Assert.DoesNotContain("current-progress", prefix);
        Assert.DoesNotContain("详情已过期", prefix);
        Assert.DoesNotContain("不存在 G99", prefix);
        Assert.Equal(prefix, CharacterContextRenderer.RenderBodyThrough(State(items[..2]), layout, 2));
    }

    [Fact]
    public void Successful_show_survives_noops_rejection_and_window_rollout_but_change_back_cannot_revive_it() {
        GoalSet original = OneGoal("original");
        GoalSet changed = OneGoal("changed");
        CharacterContextItem[] prefix = [
            GoalItem(1, GoalOperationKind.Create, original, changed: true),
            GoalItem(2, GoalOperationKind.Show, original),
            OrdinaryItem(3, original, "use item.water", "普通补给"),
            GoalItem(4, GoalOperationKind.Update, original),
            GoalItem(5, GoalOperationKind.Show, null, id: "G99", success: false),
            GoalItem(6, GoalOperationKind.Focus, original),
            GoalItem(7, GoalOperationKind.Update, original)
        ];
        string query = GoalView.RenderCurrent(State(prefix).CurrentContext!);
        Assert.Contains("最近查询：G1（操作 2）", query);
        Assert.Contains("目标详情 G1", query);
        Assert.DoesNotContain("详情已过期", query);
        CharacterContextItem[] edited = [.. prefix, GoalItem(8, GoalOperationKind.Update, changed, changed: true),
            GoalItem(9, GoalOperationKind.Update, original, changed: true)];
        Assert.Contains("详情已过期", GoalView.RenderCurrent(State(edited).CurrentContext!));
        Assert.DoesNotContain("目标详情 G1", GoalView.RenderCurrent(State(edited).CurrentContext!));
        Assert.Contains("详情已过期", CharacterContextRenderer.RenderItems(State(edited), new DecisionPlayLayout(), 7));
        CharacterContextItem[] rechecked = [.. edited, GoalItem(10, GoalOperationKind.Show, original)];
        Assert.Contains("目标详情 G1", GoalView.RenderCurrent(State(rechecked).CurrentContext!));
        Assert.DoesNotContain("详情已过期", GoalView.RenderCurrent(State(rechecked).CurrentContext!));

        var nextContext = ViewContextFixture.State(Observe(original), sequence: 2);
        string next = CharacterContextRenderer.RenderFull(nextContext, new DecisionPlayLayout());
        Assert.Contains("G1 [Open]", next);
        Assert.DoesNotContain("最近查询", next);
    }

    [Fact]
    public void Console_full_and_delta_use_same_query_contract_and_preview_does_not_consume_items() {
        var original = OneGoal("original");
        var initial = State([GoalItem(1, GoalOperationKind.Create, original, changed: true), GoalItem(2, GoalOperationKind.Show, original)]);
        var changed = State([.. initial.CurrentContext!.Items, GoalItem(3, GoalOperationKind.Focus,
            new(original.Goals, focus: [], nextGoalId: 2), changed: true)]);
        foreach (string mode in new[] { "full", "delta" }) {
            PlayPresentation presentation = PlayPresentation.ForMode(true);
            Assert.True(presentation.TrySelectMode(mode));
            using var writer = new StringWriter();
            var session = new PlayPresentationSession(presentation, ConsoleMetaCommands.HumanDefault, writer);
            session.EnterInput(initial);
            Assert.Contains("目标详情 G1", writer.ToString());
            writer.GetStringBuilder().Clear();
            session.ShowCurrent(changed);
            Assert.Equal(2, session.ConsumedItemSequence);
            Assert.Contains("详情已过期", writer.ToString());
            writer.GetStringBuilder().Clear();
            session.Commit(changed);
            Assert.Equal(3, session.ConsumedItemSequence);
            Assert.Contains("详情已过期", writer.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_show_material_is_identical_after_state_or_pending_event_recovery(bool publishState) {
        string path = Path.Combine(Path.GetTempPath(), "drama-goal-query-" + Guid.NewGuid().ToString("N"));
        try {
            var initial = CycleGameFactory.CreateSurvival();
            var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
                characterController: new Inputs("create-goal {\"title\":\"记住约定\",\"criteria\":\"赠水给同伴\",\"focus\":true}", "show-goal {\"id\":\"G1\"}"));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            CycleWorld before = kernel.World;
            KernelCursor cursor = kernel.Cursor;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            string expected = CharacterContextRenderer.RenderFull(kernel.World.Business.InteractionStates["a"], new DecisionPlayLayout());
            Assert.Contains("目标详情 G1", expected);
            using (var history = CycleDurableHistory.Create(path, before, cursor)) {
                history.CommitEvent(kernel.LastCompletion!.Event);
                if (publishState) { history.CommitState(kernel.World, kernel.Cursor); }
            }
            using var reopened = CycleDurableHistory.Open(path);
            var restored = CycleEngine.CreateKernel(reopened);
            Assert.Equal(!publishState, restored.RecoverPending());
            Assert.Equal(expected, CharacterContextRenderer.RenderFull(restored.World.Business.InteractionStates["a"], new DecisionPlayLayout()));
        } finally {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
    }

    private sealed class Inputs(params string[] commands) : ICycleCharacterController {
        private int _index;
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            int index = _index++;
            return ValueTask.FromResult(new CycleControllerOperation($"query-call-{index}", commands[index], $"query-record-{index}"));
        }
    }

    private static GoalSet OneGoal(string progress) => new([new("G1", "目的", "标准", progress: progress)], focus: ["G1"], nextGoalId: 2);

    private static CharacterContextItem GoalItem(long sequence, GoalOperationKind kind, GoalSet? goals,
        string? id = "G1", bool success = true, bool changed = false, string? raw = null, string message = "短反馈") =>
        new(sequence, sequence, raw ?? $"goal raw {sequence}", $"call-{sequence}", $"op-{sequence}", message,
            observation: success ? Observe(goals!) : null, isRejected: !success,
            goalOperation: new(kind, success, changed, message, goalId: id, changedFields: changed ? ["progress"] : []));

    private static CharacterContextItem OrdinaryItem(long sequence, GoalSet goals, string raw, string message) =>
        new(sequence, sequence, raw, $"call-{sequence}", $"op-{sequence}", message, observation: Observe(goals));

    private static CharacterInteractionState State(IEnumerable<CharacterContextItem> items, long contextSequence = 1) {
        CharacterContextItem[] saved = items.ToArray();
        var opening = ViewContextFixture.State(Observe(GoalSet.Empty)).CurrentContext!.Opening;
        return new("a", new(contextSequence, CharacterContextStatus.Open, opening, saved,
            saved.Length == 0 ? 1 : saved[^1].ItemSequence + 1, saved.Length == 0 ? 0 : saved[^1].OperationSequence!.Value),
            goals: saved.LastOrDefault(item => item.Observation is not null)?.Observation!.Goals ?? GoalSet.Empty);
    }

    private static GoalSet MaximumGraph() {
        static string Long(int count) => string.Concat(Enumerable.Repeat("\U0001F333", count - 18)) + "\n## injected\u001b[2J\"`";
        var goals = Enumerable.Range(1, GoalLimits.MaximumGoals).Select(i => {
            GoalStatus status = i <= 20 ? GoalStatus.Completed : i == 32 ? GoalStatus.Open : GoalStatus.Paused;
            return new Goal($"G{i}", Long(GoalLimits.TitleScalars), Long(GoalLimits.CriteriaScalars), status,
                Long(GoalLimits.ProgressScalars), Long(GoalLimits.NextStepScalars), Long(GoalLimits.RevisitScalars),
                status == GoalStatus.Paused ? Long(GoalLimits.StateNoteScalars) : null,
                status == GoalStatus.Completed ? Long(GoalLimits.StateNoteScalars) : null,
                Enumerable.Range(1, GoalLimits.MaximumRevisions).Select(n => new GoalRevision(GoalStatus.Completed,
                    Long(GoalLimits.TitleScalars), Long(GoalLimits.CriteriaScalars), Long(GoalLimits.StateNoteScalars),
                    Long(GoalLimits.ReasonScalars), 1, n)), status == GoalStatus.Completed ? 2 : 0,
                status == GoalStatus.Completed ? i : 0);
        }).ToArray();
        var links = new List<GoalLink>();
        for (int i = 1; i < goals.Length; i++) { links.Add(new($"L{links.Count + 1}", $"G{i}", $"G{i + 1}", Long(GoalLimits.ReasonScalars))); }
        for (int i = 3; links.Count < GoalLimits.MaximumLinks; i++) { links.Add(new($"L{links.Count + 1}", "G1", $"G{i}", Long(GoalLimits.ReasonScalars))); }
        return new(goals, links, goals.Select(goal => goal.Id), goals.Length + 1, links.Count + 1);
    }

    private static CycleObservation Observe(GoalSet goals) {
        CycleObservation source = CycleDecisionContext.Observe(CycleGameFactory.CreateSurvival().World, "a");
        return new() {
            Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
            Resources = source.Resources, HarvestDurations = source.HarvestDurations, ItemNames = source.ItemNames,
            OtherCharacters = source.OtherCharacters, Notices = source.Notices, Commands = source.Commands,
            IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
            PlaceDescription = source.PlaceDescription, ArrivalDue = source.ArrivalDue, Passages = source.Passages,
            Memories = source.Memories, Outcome = source.Outcome, EndingReason = source.EndingReason,
            Survival = source.Survival, Weather = source.Weather, Journey = source.Journey, Communication = source.Communication,
            Goals = goals
        };
    }
}

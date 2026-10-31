using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DramaBoard.ResourceCycle;

/// <summary>Pure character material. Console append delivery and complete previews read the same saved Model.</summary>
internal static class CharacterContextRenderer {
    private static readonly JsonSerializerOptions DataOptions = new() {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static string BeginMarker(CharacterInteractionState state) =>
        Marker("开始", state.ActorId, RequireContext(state).ContextSequence);
    public static string EndMarker(CharacterInteractionState state) =>
        EndMarker(state.ActorId, RequireContext(state).ContextSequence);
    public static string EndMarker(string actorId, long contextSequence) => Marker("结束", actorId, contextSequence);

    public static string RenderFull(CharacterInteractionState state, IPlayLayout layout) =>
        BeginMarker(state) + Environment.NewLine + RenderBody(state, layout) + EndMarker(state) + Environment.NewLine;

    public static string RenderBody(CharacterInteractionState state, IPlayLayout layout) =>
        RenderBodyThrough(state, layout, long.MaxValue);

    /// <summary>Pure replay of a frozen prefix. Unlike append's after cursor, this is an inclusive upper boundary.</summary>
    internal static string RenderBodyThrough(CharacterInteractionState state, IPlayLayout layout, long throughItemSequence) {
        CharacterContext context = RequireContext(state);
        return layout.RenderFull(CycleViewSnapshot.CreateRecorded(context.Opening.Observation, context, null, includeGoals: false))
            + RenderItemsCore(state, layout, 0, false, throughItemSequence, includeGoals: false)
            + GoalView.RenderCurrent(context, throughItemSequence);
    }

    public static string RenderOpening(CharacterInteractionState state, IPlayLayout layout) {
        CharacterContext context = RequireContext(state);
        // Each opening starts a new draft. The currently edited draft belongs to later items.
        return layout.RenderFull(CycleViewSnapshot.CreateRecorded(context.Opening.Observation, context, null));
    }

    public static string RenderLatestState(CharacterInteractionState state, IPlayLayout layout) {
        CharacterContext context = RequireContext(state);
        return layout.RenderFull(CycleViewSnapshot.CreateRecorded(context.LatestObservation, context, LatestDraft(context), includeGoals: false))
            + GoalView.RenderCurrent(context);
    }

    public static string RenderItems(CharacterInteractionState state, IPlayLayout layout,
        long afterItemSequence = 0, bool full = false) =>
        RenderItemsCore(state, layout, afterItemSequence, full, long.MaxValue, includeGoals: true);

    private static string RenderItemsCore(CharacterInteractionState state, IPlayLayout layout,
        long afterItemSequence, bool full, long throughItemSequence, bool includeGoals) {
        CharacterContext context = RequireContext(state);
        var text = new StringBuilder();
        CycleObservation previousObservation = context.Opening.Observation;
        string? draft = null;
        foreach (CharacterContextItem item in context.Items.Where(item => item.ItemSequence <= throughItemSequence)) {
            var previous = CycleViewSnapshot.CreateRecorded(previousObservation, context, draft, includeHistory: false, includeGoals: includeGoals);
            draft = ApplyDraft(draft, item);
            CycleObservation observation = item.Observation ?? previousObservation;
            var current = CycleViewSnapshot.CreateRecorded(observation, context, draft, includeHistory: false, includeGoals: includeGoals);
            if (item.ItemSequence > afterItemSequence) {
                if (item.GoalOperation is { } receipt) {
                    if (includeGoals) {
                        text.Append(GoalView.RenderReceipt(item));
                        if (receipt.Changed) { text.Append(GoalView.RenderDefault(observation.Goals)); }
                        if (receipt.Changed || receipt is { Kind: CycleGoalOperationKind.Show, Success: true }) {
                            text.Append(GoalView.RenderQuery(context, item.ItemSequence));
                        }
                    }
                    // Goal operations carry no world experiences. Their complete raw input remains in the saved Item.
                    previousObservation = observation;
                    continue;
                }
                text.AppendLine();
                if (item.OperationSequence is not null) {
                    text.AppendLine($"### 本轮操作 {item.OperationSequence}（{(item.IsRejected ? "未执行" : "已执行")}）");
                    text.AppendLine("提交内容（原始输入，仅为记录）：");
                    text.AppendLine("> " + DataLiteral(item.RawInput!));
                    text.AppendLine("结果：");
                    if (item.Experiences.Count == 0) { text.AppendLine("> " + DataLiteral(item.Result!)); }
                } else {
                    text.AppendLine("### 新的可感知信息");
                }
                if (!item.IsRejected) {
                    var update = CycleViewUpdate.Create(previous, current, item.Experiences.Select(experience => experience.Event),
                        PlayDeliveryPhase.ControllerInput, fullTail: full);
                    text.Append(layout.RenderUpdate(update));
                }
            }
            previousObservation = observation;
        }
        return text.ToString();
    }

    /// <summary>A single quoted data literal; newlines, terminal controls and Markdown delimiters cannot become structure.</summary>
    internal static string DataLiteral(string value) => JsonSerializer.Serialize(value, DataOptions);

    private static string? LatestDraft(CharacterContext context) {
        string? draft = null;
        foreach (CharacterContextItem item in context.Items) { draft = ApplyDraft(draft, item); }
        return draft;
    }

    private static string? ApplyDraft(string? draft, CharacterContextItem item) {
        foreach (CycleObservedEffect effect in item.Experiences.SelectMany(experience => experience.Event.Effects)) {
            if (effect.Key is { Category: "continuity", Field: "draft" }) {
                draft = effect.After.Kind == CycleVisibleValueKind.Text ? effect.After.Text : null;
            }
        }
        return draft;
    }

    private static CharacterContext RequireContext(CharacterInteractionState state) =>
        state.CurrentContext ?? throw new InvalidOperationException("Character material requires a committed Context.");
    private static string Marker(string kind, string actorId, long contextSequence) =>
        $"━━━━━━━━━━━━━━━━━━━━ LLM 上下文{kind} · {DataLiteral(actorId)} · {contextSequence} ━━━━━━━━━━━━━━━━━━━━";
}

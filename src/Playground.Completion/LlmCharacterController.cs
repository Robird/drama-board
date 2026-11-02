using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.Playground.Completion;

/// <summary>Supplies Hillside policies and frozen materials to the shared model tool loop.</summary>
/// <remarks>The game remains responsible for parsing, validation, effects and persistence.</remarks>
public sealed class LlmCharacterController : IPlaygroundCharacterController {
    public const string ToolName = LlmDecisionLoop.ToolName;
    public const string FeedbackToolName = LlmDecisionLoop.FeedbackToolName;
    private const string PromptBody = "你是当前世界的一位居民，在同一个连续世界里生活、交流、进山探索并返回。没有强制逃生终点；根据自己的处境和目标行动。"
        + "即时行动或拒绝之后仍可继续决策；耗时行动会放行世界。自由文本不会成为游戏对白、事实或持久记忆。"
        + "注意当前身体危险、活动、最近结果和既有承诺，区分当前事实、历史成果、他人陈述和推测。危险需要取舍，不等于必须取消当前活动。"
        + "若界面提供同地同伴与交流行动，可用 say 沟通计划、用 give 分享补给；对方的话是署名陈述，不等于已经亲眼证实。"
        + "交流次数有限，收到交流无需取消原活动；按界面说明用 continue 确认后继续。"
        + "检查同回合反馈：已成功的操作不要重复，拒绝原因未变化时不要原样重试。没有需要记录的新变化时直接行动，无需输出检查清单或完整推理。"
        + "需要补给时可使用界面提供的即时 use 行动。不要调用 Console 元命令。";

    private const string EconomyGuidance = "在镇里可按当前界面买卖与生产。市场的买入价是你付款，卖出价是市场向你付款；库存和收购额度有限，以提交结果为准。"
        + "生产开始时预留全部投入，完成时取得产物；cancel 退回本次未完成生产的预留材料，不产生成品。买卖、补给和通信不会重置原活动期限。"
        + "只使用眼前提供的市场、配方和物品标识；use 可使用界面列出的补给，物品效果以当前冻结材料为准。"
        + "可用 give <actor-id> money <amount> 赠送自己的钱；接收或赠与不代表知道他人的余额或背包。";

    private const string TradeGuidance = "居民报价由 offer 的给予 / 索取条款组成；看清自己支付与收到什么，以及报价身份、参与者和到期时间。"
        + "报价不预留资产，不证明对方余额或背包。只有收件人可 accept / reject，只有发起方可 withdraw；可尝试接受不保证成交，以提交回执为准。"
        + "offer / accept 需要同地，reject / withdraw 可异地，途中不可处理；只有 offer 消耗共享发送额度。"
        + "交易操作不耗模拟时间、不取消生产或其他活动、不重置期限，也不确认已有警告；需要放行时 continue。"
        + "已拒绝、撤回、到期或参与方不可继续的报价没有转移资产；已成交操作不要重复。";

    private const string GoalGuidance = "界面已提供目标管理。对照所有未结束事项与复查线索；方法失效不自动取消仍成立的目的。"
        + "新采纳的承诺、重要进展、方法变更或调查结论需要跨回合保留时，用界面中的目标命令记录简短内容和依据。"
        + "一次提交一条固定目标命令及 JSON 对象；以实际帮助中的字段和容量为准。成功编辑后根据结果继续行动，不因下一次独立调用重新创建。"
        + "Focus 只是注意力，挂起或结束目标不会取消活动；无焦点时仍要检查未结束目录。Completed 是你的判断，不会创造世界效果。"
        + "原完成判断错误可带理由 reopen；确实取得的成果后来耗尽，则为新的需要另建目标。调查得到可靠否定答案也能完成调查，但不自动完成父目的。"
        + "show 只展开已有目标记录，不能探索未知世界；标为过期的详情需要重查，当前世界信息仍以观察为准。"
        + "handoff 只留短期一次性交接；目标已经保存的长期事项不必再次抄写。旧回合完整对话不会自动带入。";

    private const string HandoffGuidance = "首次规划路线或补给安排时，先用 handoff 留下希望下一决策回合继续记得的简短计划。"
        + "新通信改变你采纳的分工或承诺时，若草稿尚未覆盖，先更新一次 handoff：记明谁承诺何物、自己的下一步及待核实事项。"
        + "旧回合完整对话不会自动带入下一回合；无新增关键信息时直接行动，不重复改写草稿。交接是主观笔记，不是确认过的世界事实。";

    private const string FollowingGuidance = "结伴出行可用 follow 主动集合，把移动交给领队；跟随期间不可独立 go、gather、produce 或 wait。"
        + "领队普通 go 带实际队员一起走；抵达不会自动脱队。在工作地点用 unfollow 或 dismiss-followers 分头工作，完成或取消活动后再集合。"
        + "跟随者收到提醒仍需处理，可 use 补给后 continue 继续跟随；say / give 仍仅限同地，途中不能改变跟随关系。";

    private const string ShoutGuidance = "地点内的决策窗口可用 shout 大喊召集：署名话语覆盖本地点及声音连接的一跳地点，不继续传播，途中不能发送或接收。"
        + "shout 与 say / give 共用发送额度，一次大喊只扣一次，无人听见也成功；回执不提供听众名单或接收确认。"
        + "听到大喊不取消原活动，也不自动集合；完成或取消活动后再自主返回和跟随。听见远处角色不代表同地可赠与或跟随，话语不等于世界证实。";

    private static string GuidanceFor(PlaygroundObservation observation) => $"当前世界：{observation.WorldName}。" + PromptBody + (observation.IsExpedition ? FollowingGuidance + ShoutGuidance + EconomyGuidance + TradeGuidance : "") + (observation.Goals is not null
        ? GoalGuidance : observation.Commands.Any(entry => entry.Command.Kind == PlaygroundCommandKind.Handoff) ? HandoffGuidance : "");

    private readonly LlmDecisionLoop _loop;
    private readonly Func<PlaygroundDecisionContext, string> _renderContext;
    private int _active;
    public int CallsMade => _loop.CallsMade;

    public LlmCharacterController(ICompletionClient client, string modelId,
        Func<PlaygroundDecisionContext, string> renderContext, LlmControllerOptions? options = null,
        Action<LlmControllerTrace>? trace = null,
        Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? feedbackSink = null) {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        _loop = new(client, modelId, options, trace, feedbackSink);
    }

    public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        // Rendering belongs to this controller service too: a shared renderer may own host-local
        // presentation state and must not run concurrently before the loop rejects a second caller.
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) {
            throw new InvalidOperationException("The LLM controller requires sequential invocation.");
        }
        try {
            string material = _renderContext(context) ?? throw new InvalidOperationException("The context renderer returned null.");
            CommandSubmission operation = await _loop.ChooseCommandAsync(new(context.InteractionState.ActorId,
                context.Context.ContextSequence, material, GuidanceFor(context.Observation)), cancellationToken).ConfigureAwait(false);
            return new(operation.ToolCallId, operation.RawInput, operation.OperationRecordId);
        } finally { Volatile.Write(ref _active, 0); }
    }
}

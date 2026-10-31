using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.Microtown.Completion;

/// <summary>Supplies one resident's frozen observation and town policy to the shared Completion tool loop.</summary>
/// <remarks>No world or cross-decision semantic memory is held. One instance may serve all residents
/// sequentially; its call budgets are operational and shared. The game validates and commits returned
/// operations. The injected client is borrowed, while feedback persistence belongs to the host.</remarks>
public sealed class TownLlmCharacterController {
    private const string GameGuidance = "You control only the current resident of this small town. Maintain your own food supply and build useful reserves, "
        + "using the visible recipes, market offers and routes. You may choose your own plans; other residents decide independently. "
        + "Only the current observation and your private goals are available across decisions. Check the latest recorded result before acting: "
        + "a displayed successful operation has already happened. Do not repeat rejected commands unchanged when the cause remains. "
        + "Food decreases during movement, waiting and production. Count travel and the full production duration before starting; "
        + "a legal action can still outlast your food. You are normally called again only after a timed activity completes. "
        + "Use eat to consume an owned meal; buying, selling and eating are instant when a decision is offered. "
        + "Production consumes the reserved ingredients and delivers the whole batch at completion. Waiting creates no money or food. "
        + "Use only commands shown in the observation. Do not invent speech, gifts, cancellation, remote trading or Console commands. "
        + "Other residents' private needs, goals and inventory are unknown; your private goals cannot edit them. "
        + "Store adopted intentions, progress and changes of plan with the provided goal commands when they need to survive future decisions. "
        + "Reuse existing goals instead of creating duplicates. Focus is attention, and goal completion is your judgment rather than a world effect. "
        + "When circumstances change after a genuine success, record the new need; reopen only when the earlier judgment was mistaken. "
        + "Do not spend every decision rewriting notes. When no new lasting information needs recording, take a useful world action. "
        + "Free-form assistant text does not become dialogue, world facts or persistent memory.";

    private readonly LlmDecisionLoop _loop;
    private readonly Func<TownObservation, string> _renderObservation;
    private int _active;
    public int CallsMade => _loop.CallsMade;
    public bool FeedbackEnabled { get; }

    public TownLlmCharacterController(ICompletionClient client, string modelId, LlmControllerOptions? options = null,
        Action<LlmControllerTrace>? trace = null,
        Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? feedbackSink = null,
        Func<TownObservation, string>? renderObservation = null) {
        _loop = new(client, modelId, options, trace, feedbackSink);
        _renderObservation = renderObservation ?? TownTextRenderer.Render;
        FeedbackEnabled = feedbackSink is not null;
    }

    public async ValueTask<TownControllerOperation> ChooseCommandAsync(TownObservation observation, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!observation.IsDecisionOpen || observation.WorldEnded || observation.Failed) {
            throw new InvalidOperationException("A town controller requires an open decision for an active resident.");
        }
        // Include custom rendering in the serial service boundary, not only the provider call.
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) {
            throw new InvalidOperationException("The town LLM controller requires sequential invocation.");
        }
        try {
            string material = _renderObservation(observation) ?? throw new InvalidOperationException("The observation renderer returned null.");
            CommandSubmission submission = await _loop.ChooseCommandAsync(new(observation.ActorId,
                observation.DecisionSequence, material, GameGuidance), cancellationToken).ConfigureAwait(false);
            return new(submission.RawInput, submission.ToolCallId, submission.OperationRecordId);
        } finally { Volatile.Write(ref _active, 0); }
    }
}

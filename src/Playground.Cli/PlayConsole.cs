using DramaBoard.Playground.Cli;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

/// <summary>Console input and operation helpers; the unified host owns world execution.</summary>
internal static class PlayConsole {
    internal static void CompleteOperations(PlaygroundOperationJournal operations, OccurrenceCompletion<PlaygroundFact> completion) {
        foreach (PlaygroundFact fact in completion.Event.Facts) {
            if (fact.OperationRecordId is not { } recordId) { continue; }
            operations.Complete(recordId, PlaygroundOperationCommit.StatusOf(fact.OperationDisposition
                ?? throw new InvalidOperationException("An operation fact has no committed disposition.")), PlaygroundOperationResults.Describe(fact),
                $"{completion.Cursor.Version.LineageId}:{completion.Cursor.Version.TransitionCount}",
                completion.Event.CauseKey.ToString());
        }
    }

    internal sealed class PlayEndedException : Exception { }

    internal static void WriteBoundary(string text) {
        if (Console.IsOutputRedirected) {
            Console.WriteLine(text);
            return;
        }
        ConsoleColor previous = Console.ForegroundColor;
        try {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(text);
        } finally {
            Console.ForegroundColor = previous;
        }
    }

    // Shared across characters; each invocation owns its input loop and renders the requested actor's view.
    internal sealed class ConsoleCharacterController(PlayPresentation presentation, ConsoleMetaCommands metaCommands, TextReader? inputReader = null,
        TextWriter? outputWriter = null, Action<string>? writeBoundary = null, PlaygroundOperationJournal? operations = null,
        Func<string>? worldVersion = null,
        Func<PlaygroundDecisionContext, string, CancellationToken, ValueTask<string>>? feedbackSink = null) : IPlaygroundCharacterController {
        private readonly TextReader _input = inputReader ?? Console.In;
        private readonly TextWriter _output = outputWriter ?? Console.Out;
        private readonly PlaygroundOperationJournal _operations = operations ?? PlaygroundOperationJournal.CreateInMemory();
        private readonly string _batchId = Guid.NewGuid().ToString("N");
        private readonly PlayPresentationSession _session = new(presentation, metaCommands, outputWriter ?? Console.Out, writeBoundary);
        internal string? StartupText { get => _session.StartupText; set => _session.StartupText = value; }
        internal bool TerminalDelivered => _session.TerminalDelivered;
        internal PlaygroundViewSnapshot? Baseline => _session.Baseline;

        internal void RenderObservation(PlaygroundDecisionContext context) => _session.ShowFull(context.InteractionState);

        internal void HostReply(string text) => _session.Reply(text);

        internal void RenderCommitted(PlaygroundWorld world, string? actorId = null) {
            // Delivery reads committed per-character material only. World facts and diagnostics are not UI inputs.
            foreach (CharacterInteractionState state in world.Business.InteractionStates.Values) {
                if (actorId is not null && state.ActorId != actorId) { continue; }
                _session.Commit(state);
            }
        }

        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            PlaygroundObservation observation = context.Observation;
            _session.EnterInput(context.InteractionState);
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                _output.Write(_session.Prompt(observation));
                _output.Flush();
                string? input = await _input.ReadLineAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (input is null) { _session.CloseSession(); throw new PlayEndedException(); }
                if (string.IsNullOrWhiteSpace(input)) { continue; }
                string toolName = input.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                // Feedback owns its independent durable receipt; unrelated operation audit failures must not
                // prevent saving or turn a saved report into an apparent failure.
                if (toolName == ":feedback") {
                    ConsoleMetaCommand? feedback = metaCommands.Parse(input);
                    if (feedback?.Kind != ConsoleMetaCommandKind.Feedback) {
                        _session.Reply("无法识别命令。" + metaCommands.RejectionHint + "时间未推进；未确认消息。");
                    } else if (string.IsNullOrWhiteSpace(feedback.Argument) || feedback.Argument.Length > FeedbackRecord.MaxTextCharacters) {
                        _session.Reply("反馈正文不能为空，且不得超过 4000 字符。时间未推进；未确认消息。");
                    } else if (feedbackSink is null) {
                        _session.Reply("反馈未保存：当前宿主没有反馈保存位置。时间未推进；未确认消息。");
                    } else {
                        string? feedbackId = null;
                        try {
                            feedbackId = await feedbackSink(context, feedback.Argument, cancellationToken);
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception) {
                            _session.Reply("反馈保存未确认；请检查保存位置后再提交。时间未推进；未确认消息。");
                        }
                        if (feedbackId is not null) { _session.Reply($"反馈已保存：{feedbackId}。继续选择行动；时间未推进，未确认消息。"); }
                    }
                    continue;
                }
                PlaygroundOperationReceipt receipt = _operations.Receive(_batchId, "call_" + Guid.NewGuid().ToString("N"),
                    observation.Actor.Id, context.Context.ContextSequence, toolName, input);
                void Reply(string result, PlaygroundOperationStatus status = PlaygroundOperationStatus.Succeeded) {
                    _operations.Complete(receipt.OperationRecordId, status, result, worldVersion?.Invoke());
                    _session.Reply(result);
                }
                void Resynchronize(string? prefix = null) => _session.ShowCurrent(context.InteractionState,
                    result => _operations.Complete(receipt.OperationRecordId, PlaygroundOperationStatus.Succeeded, result, worldVersion?.Invoke()), prefix);
                void RejectSyntax() => Reply("无法识别命令。" + metaCommands.RejectionHint +
                    "时间未推进；未确认消息。", PlaygroundOperationStatus.Rejected);
                if (input.TrimStart().StartsWith(':')) {
                    ConsoleMetaCommand? parsed = metaCommands.Parse(input);
                    if (parsed is null) { RejectSyntax(); continue; }
                    switch (parsed.Kind) {
                        case ConsoleMetaCommandKind.Exit:
                            Reply("Console 会话关闭；退出当前控制台，角色上下文保持已提交状态。");
                            throw new PlayEndedException();
                        case ConsoleMetaCommandKind.Look:
                            Resynchronize();
                            break;
                        case ConsoleMetaCommandKind.Help:
                            Reply(metaCommands.RenderHelp(presentation) + "\n" + presentation.Current.RenderActionHelp(observation));
                            break;
                        case ConsoleMetaCommandKind.Layout:
                            if (parsed.Argument is null) { Reply(metaCommands.DescribeLayouts(presentation)); }
                            else if (presentation.FindLayout(parsed.Argument) is not { } selected) {
                                Reply($"未知布局：{parsed.Argument}。\n{metaCommands.DescribeLayouts(presentation)}", PlaygroundOperationStatus.Rejected);
                            } else if (parsed.Once) {
                                Reply($"临时查看布局：{selected.Id}（{selected.Name}）；当前布局仍为 {presentation.Current.Id}。\n" +
                                    CharacterContextRenderer.RenderLatestState(context.InteractionState, selected));
                            } else {
                                presentation.TrySelect(parsed.Argument);
                                Resynchronize($"已切换布局：{presentation.Current.Id}（{presentation.Current.Name}）。\n");
                            }
                            break;
                        case ConsoleMetaCommandKind.Mode:
                            if (parsed.Argument is null) { Reply(metaCommands.DescribeModes(presentation)); }
                            else if (presentation.TrySelectMode(parsed.Argument)) {
                                Resynchronize($"已切换输出模式：{parsed.Argument}。\n");
                            } else {
                                Reply($"此场景不支持输出模式：{parsed.Argument}。\n{metaCommands.DescribeModes(presentation)}", PlaygroundOperationStatus.Rejected);
                            }
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown Console meta command: {parsed.Kind}.");
                    }
                    continue;
                }
                // No uncommitted preview is appended to the character transcript. The domain
                // commits the operation result or release before saved Model delivery occurs.
                return new PlaygroundControllerOperation(receipt.ToolCallId, input, receipt.OperationRecordId);
            }
        }
    }
}

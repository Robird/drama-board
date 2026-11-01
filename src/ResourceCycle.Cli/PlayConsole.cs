using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>One asynchronous owner drives the Kernel; input and rendering never mutate the world.</summary>
internal static class PlayConsole {
    public static async Task<int> RunAsync(string? store, CancellationToken cancellationToken,
        Action<CycleDiagnosticRecord>? diagnosticSink = null, TextReader? inputReader = null, TextWriter? outputWriter = null,
        int? characterCount = null, string? branch = null, ModelTime? until = null, int maxSteps = 10_000) {
        TextWriter output = outputWriter ?? Console.Out;
        bool restoring = store is not null && Directory.Exists(store);
        using CycleDurableHistory? durable = store is null ? null : restoring
            ? branch is null ? CycleDurableHistory.Open(store) : CycleDurableHistory.Open(store, branch)
            : branch is null ? CreateStore(store, characterCount) : throw new ArgumentException("指定分支需要已有存档。");
        IOccurrenceHistory<CycleWorld, CycleFact> history;
        if (durable is not null) { history = durable; }
        else {
            var initial = CycleGameFactory.CreateLivingWorld(characterCount: characterCount ?? 3);
            history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        }
        WorldHost.RequireWorld(history.State, characterCount);
        WorldHost.RequireBudget(history.State.Time, until, maxSteps);
        using CycleOperationJournal operations = store is null ? CycleOperationJournal.CreateInMemory()
            : CycleOperationJournal.Open(GymSession.OperationsPath(store, durable!));
        var controller = new ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            inputReader: inputReader, outputWriter: output, writeBoundary: outputWriter is null ? WriteBoundary : null,
            operations: operations, worldVersion: () => $"{history.Cursor.Version.LineageId}:{history.Cursor.Version.TransitionCount}");
        var kernel = CycleEngine.CreateKernel(history, characterController: controller);
        string startup = $"山野镇 / Hillside · {history.State.Characters.Count} 位居民 · " + (store is null
            ? "本次仅在内存中运行；退出后不保留进度。"
            : $"存档：{store} · 每次成功转换自动保存。")
            + (restoring ? " 已保存的角色上下文、活动、目标与交接已恢复。" : "")
            + "\n共用 Human Console 轮流操作角色；等待输入期间世界暂停。眼前事实与旧观察分开显示。";
        if (operations.DiscardedTailBytes > 0) {
            startup += $"\n操作记录末尾 {operations.DiscardedTailBytes} 字节未完整写入；游戏进度按已提交 E/S 恢复。";
        }
        if (kernel.RecoverPending(cancellationToken)) {
            startup += "\n已完成上次中断时保存的行动；没有重新询问控制器。";
        }
        if (durable is not null) {
            operations.Reconcile(durable.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new CycleOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
        }
        controller.StartupText = startup;
        WorldHost.RequireWorld(kernel.World, characterCount);
        int completed = 0;
        try {
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                controller.RenderCommitted(kernel.World);
                if (!kernel.World.Characters.Any(character => character.IsActive)) {
                    controller.HostReply(WorldHost.Summary(kernel.World));
                    return 0;
                }
                if (completed >= maxSteps || until is { } limit && kernel.CurrentModelTime >= limit) {
                    controller.HostReply("本次宿主预算已到，世界暂停；已提交状态保留。使用相同存档继续。");
                    return 0;
                }
                StepStatus status = await kernel.StepAsync(until ?? new ModelTime(long.MaxValue), cancellationToken);
                if (status != StepStatus.Committed) {
                    controller.HostReply($"世界暂停：{status}。当前时间为最后完成事件的时刻；可用相同存档继续。");
                    return 0;
                }
                completed++;
                CycleContextDiagnostics.Publish(kernel.LastCompletion!, diagnosticSink);
                CompleteOperations(operations, kernel.LastCompletion!);
                controller.RenderCommitted(kernel.World);
            }
        } catch (PlayEndedException) {
            controller.HostReply(store is null ? "已暂停；内存进度未保存。" : "已暂停。已提交行动已保存，可使用同一 --store 继续并重新选择控制绑定。");
            return 0;
        }
    }

    private static CycleDurableHistory CreateStore(string path, int? characterCount) {
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: characterCount ?? 3);
        return CycleDurableHistory.Create(path, initial.World, initial.Cursor);
    }

    internal static void CompleteOperations(CycleOperationJournal operations, OccurrenceCompletion<CycleFact> completion) {
        foreach (CycleFact fact in completion.Event.Facts) {
            if (fact.OperationRecordId is not { } recordId) { continue; }
            operations.Complete(recordId, CycleOperationCommit.StatusOf(fact.OperationDisposition
                ?? throw new InvalidOperationException("An operation fact has no committed disposition.")), CycleOperationResults.Describe(fact),
                $"{completion.Cursor.Version.LineageId}:{completion.Cursor.Version.TransitionCount}",
                completion.Event.CauseKey.ToString());
        }
    }

    internal sealed class PlayEndedException : Exception { }

    private static void WriteBoundary(string text) {
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
        TextWriter? outputWriter = null, Action<string>? writeBoundary = null, CycleOperationJournal? operations = null,
        Func<string>? worldVersion = null) : ICycleCharacterController {
        private readonly TextReader _input = inputReader ?? Console.In;
        private readonly TextWriter _output = outputWriter ?? Console.Out;
        private readonly CycleOperationJournal _operations = operations ?? CycleOperationJournal.CreateInMemory();
        private readonly string _batchId = Guid.NewGuid().ToString("N");
        private readonly PlayPresentationSession _session = new(presentation, metaCommands, outputWriter ?? Console.Out, writeBoundary);
        internal string? StartupText { get => _session.StartupText; set => _session.StartupText = value; }
        internal bool TerminalDelivered => _session.TerminalDelivered;
        internal CycleViewSnapshot? Baseline => _session.Baseline;

        internal void RenderObservation(CycleDecisionContext context) => _session.ShowFull(context.InteractionState);

        internal void HostReply(string text) => _session.Reply(text);

        internal void RenderCommitted(CycleWorld world, string? actorId = null) {
            // Delivery reads committed per-character material only. World facts and diagnostics are not UI inputs.
            foreach (CharacterInteractionState state in world.Business.InteractionStates.Values) {
                if (actorId is not null && state.ActorId != actorId) { continue; }
                _session.Commit(state);
            }
        }

        public async ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            CycleObservation observation = context.Observation;
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
                CycleOperationReceipt receipt = _operations.Receive(_batchId, "call_" + Guid.NewGuid().ToString("N"),
                    observation.Actor.Id, context.Context.ContextSequence, toolName, input);
                void Reply(string result, CycleOperationStatus status = CycleOperationStatus.Succeeded) {
                    _operations.Complete(receipt.OperationRecordId, status, result, worldVersion?.Invoke());
                    _session.Reply(result);
                }
                void Resynchronize(string? prefix = null) => _session.ShowCurrent(context.InteractionState,
                    result => _operations.Complete(receipt.OperationRecordId, CycleOperationStatus.Succeeded, result, worldVersion?.Invoke()), prefix);
                void RejectSyntax() => Reply("无法识别命令。" + metaCommands.RejectionHint +
                    "时间未推进；未确认消息。", CycleOperationStatus.Rejected);
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
                                Reply($"未知布局：{parsed.Argument}。\n{metaCommands.DescribeLayouts(presentation)}", CycleOperationStatus.Rejected);
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
                                Reply($"此场景不支持输出模式：{parsed.Argument}。\n{metaCommands.DescribeModes(presentation)}", CycleOperationStatus.Rejected);
                            }
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown Console meta command: {parsed.Kind}.");
                    }
                    continue;
                }
                // No uncommitted preview is appended to the character transcript. The domain
                // commits the operation result or release before saved Model delivery occurs.
                return new CycleControllerOperation(receipt.ToolCallId, input, receipt.OperationRecordId);
            }
        }
    }
}

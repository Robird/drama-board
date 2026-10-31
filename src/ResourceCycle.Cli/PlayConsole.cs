using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>One asynchronous owner drives the Kernel; input and rendering never mutate the world.</summary>
internal static class PlayConsole {
    public static async Task<int> RunAsync(string? store, CancellationToken cancellationToken, bool survival = false,
        Action<CycleDiagnosticRecord>? diagnosticSink = null, TextReader? inputReader = null, TextWriter? outputWriter = null,
        bool duo = false, string? branch = null) {
        TextWriter output = outputWriter ?? Console.Out;
        bool restoring = store is not null && Directory.Exists(store);
        using CycleDurableHistory? durable = store is null ? null : Directory.Exists(store)
            ? branch is null ? CycleDurableHistory.Open(store) : CycleDurableHistory.Open(store, branch)
            : branch is null ? CreateStore(store, survival, duo) : throw new ArgumentException("指定分支需要已有存档。");
        IOccurrenceHistory<CycleWorld, CycleFact> history;
        if (durable is not null) {
            history = durable;
        } else {
            var initial = survival ? CycleGameFactory.CreateSurvival(characterCount: duo ? 2 : 1) : CycleGameFactory.CreatePlayable();
            history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        }
        if (history.State.Characters.Count == 0) {
            throw new ArgumentException("这是批处理实验存档，不能用 --play 打开。请省略 --play 续跑，或选择新目录开始采集。");
        }
        if ((history.State.Expedition is not null) != survival) {
            throw new ArgumentException(survival ? "这是原采集场景存档，请使用 --play。" : "这是生存场景存档，请使用 --survival。");
        }
        if (survival) { SurvivalHost.RequireMode(history.State, duo); }

        using CycleOperationJournal operations = store is null ? CycleOperationJournal.CreateInMemory()
            : CycleOperationJournal.Open(GymSession.OperationsPath(store, durable!));
        var presentation = PlayPresentation.ForMode(survival);
        var controller = new ConsoleCharacterController(presentation, ConsoleMetaCommands.HumanDefault, inputReader: inputReader, outputWriter: output,
            writeBoundary: outputWriter is null ? WriteBoundary : null,
            operations: operations, worldVersion: () => $"{history.Cursor.Version.LineageId}:{history.Cursor.Version.TransitionCount}");
        var kernel = CycleEngine.CreateKernel(history, characterController: controller);
        string title = survival ? duo ? "双人山林求生" : "山林求生" : "多人采集";
        string startup = (store is null
            ? $"{title} · 本次仅在内存中运行；退出后不保留进度。"
            : $"{title} · 存档：{store} · 每次成功转换自动保存。" +
                (restoring ? survival
                    ? "将从保存的角色上下文重绘；已保存的行动结果、交接文字与草稿已恢复。"
                    : "将从保存的角色上下文重绘；已提交的游戏状态已恢复。" : "")) + "\n" + (survival
            ? "眼前事实与旧观察分开显示；等待输入期间世界暂停。"
            : "共用控制台轮流扮演角色；画面只显示当前角色的技能、活动细节和背包。等待输入期间世界暂停。");
        if (operations.DiscardedTailBytes > 0) {
            startup += $"\n操作记录末尾有 {operations.DiscardedTailBytes} 字节未完整写入，已丢弃；游戏进度仍按已提交 E/S 恢复。";
        }
        if (kernel.RecoverPending(cancellationToken)) {
            startup += "\n已完成上次中断时保存的行动；接下来按已保存的活动与请求继续。";
        }
        if (durable is not null) {
            operations.Reconcile(durable.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new CycleOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
        }
        controller.StartupText = startup;
        if (survival) { SurvivalHost.RequireMode(kernel.World, duo); }

        try {
            while (true) {
                controller.RenderCommitted(kernel.World);
                if (survival && !kernel.World.Characters.Any(character => character.IsActive)) {
                    controller.HostReply(SurvivalHost.Summary(kernel.World));
                    return 0;
                }
                StepStatus status = await kernel.StepAsync(new ModelTime(long.MaxValue), cancellationToken);
                if (status != StepStatus.Committed) {
                    throw new InvalidOperationException($"采集世界未能产生下一步行动：{status}。");
                }
                CycleContextDiagnostics.Publish(kernel.LastCompletion!, diagnosticSink);
                CompleteOperations(operations, kernel.LastCompletion!);
                controller.RenderCommitted(kernel.World);
            }
        } catch (PlayEndedException) {
            controller.HostReply(store is null ? $"已退出。内存中的{(survival ? "生存" : "采集")}进度未保存。" :
                $"已退出。已完成的行动均已保存，可使用同一 {(duo ? "--duo" : survival ? "--survival" : "--play")} --store 路径继续。");
            return 0;
        }
    }

    public static void RequireBatchWorld(CycleWorld world) {
        if (world.Characters.Count != 0) {
            throw new ArgumentException(world.Expedition is not null
                ? "这是生存场景存档；请使用 --survival --store 打开。批处理不会推进此存档。"
                : "这是交互采集存档；请使用 --play --store 打开。批处理不会推进此存档。");
        }
    }

    private static CycleDurableHistory CreateStore(string path, bool survival, bool duo) {
        var initial = survival ? CycleGameFactory.CreateSurvival(characterCount: duo ? 2 : 1) : CycleGameFactory.CreatePlayable();
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

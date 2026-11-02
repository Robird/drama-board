using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Completion;
using DramaBoard.Playground.Cli;

/// <summary>Single serial runtime host. Only committed game state is recovery authority; model audit is evidence.</summary>
internal static class PlaygroundHost {
    public static async Task<int> RunAsync(string? store, PlaygroundHostOptions options, CancellationToken cancellationToken,
        TextWriter? outputWriter = null, ICompletionClient? suppliedClient = null, int? characterCount = null, TextReader? inputReader = null,
        ICompletionClientFactory? clientFactory = null, Action<PlaygroundDiagnosticRecord>? diagnosticSink = null,
        Func<string?, PlaygroundHostJournal>? journalFactory = null) {
        cancellationToken.ThrowIfCancellationRequested();
        TextWriter output = outputWriter ?? Console.Out;
        if ((options.Gym == true || options.Branch is not null) && store is null) {
            throw new ArgumentException("详细人物记录和分支运行需要 --store。");
        }
        // This check is deliberately before both world Open/Create and journal initialization. A new
        // filename cannot silently abandon pending receipt/reconciliation obligations in an old store.
        if (store is not null) { RequireCurrentHostFormat(store); }
        LlmConnectionConfiguration? configuration = options.ConnectionsPath is { } configurationPath
            ? LlmConnectionConfiguration.Load(configurationPath) : null;
        CharacterControlConfiguration controls = options.Controls ?? (options.CharacterControlsPath is { } controlsPath
            ? CharacterControlConfiguration.Load(controlsPath, configuration ?? throw new ArgumentException("角色控制文件需要连接目录。"))
            : CharacterControlConfiguration.Empty);
        if (options.MaxCalls < 1 || options.MaxFailures < 1 || options.MaxContextCharacters < 1) {
            throw new ArgumentOutOfRangeException(nameof(options), "宿主预算必须为正数。");
        }
        using PlaygroundDurableHistory? durable = store is null ? null : Directory.Exists(store)
            ? options.Branch is null ? PlaygroundDurableHistory.Open(store) : PlaygroundDurableHistory.Open(store, options.Branch)
            : options.Branch is null ? CreateStore(store, characterCount, options, controls, configuration) : throw new ArgumentException("指定分支需要已有存档。");
        IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history;
        if (durable is not null) { history = durable; }
        else {
            var initial = PlaygroundGameFactory.Create(WorldHost.Definition(options.Content, characterCount));
            history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        }
        var bindings = controls.Resolve(history.State.Characters.Select(character => character.Id), configuration?.DefaultConnectionId ?? "fixture");
        return await RunCoreAsync(history, store, durable, options, bindings, configuration, cancellationToken,
            output, suppliedClient, characterCount, inputReader, clientFactory, diagnosticSink, journalFactory);
    }

    public static Task<int> RunAsync(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history,
        IReadOnlyDictionary<string, CharacterControlBinding> bindings, PlaygroundHostOptions options, CancellationToken cancellationToken,
        TextWriter? outputWriter = null, ICompletionClient? suppliedClient = null, TextReader? inputReader = null,
        ICompletionClientFactory? clientFactory = null, Action<PlaygroundDiagnosticRecord>? diagnosticSink = null,
        Func<string?, PlaygroundHostJournal>? journalFactory = null) =>
        RunCoreAsync(history, null, null, options, bindings, null, cancellationToken, outputWriter ?? Console.Out,
            suppliedClient, null, inputReader, clientFactory, diagnosticSink, journalFactory);

    private static async Task<int> RunCoreAsync(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history,
        string? store, PlaygroundDurableHistory? durable, PlaygroundHostOptions options,
        IReadOnlyDictionary<string, CharacterControlBinding> bindings, LlmConnectionConfiguration? configuration,
        CancellationToken cancellationToken, TextWriter output, ICompletionClient? suppliedClient, int? characterCount,
        TextReader? inputReader, ICompletionClientFactory? clientFactory, Action<PlaygroundDiagnosticRecord>? diagnosticSink = null,
        Func<string?, PlaygroundHostJournal>? journalFactory = null) {
        cancellationToken.ThrowIfCancellationRequested();
        WorldHost.RequireWorld(history.State, characterCount);
        WorldHost.RequireContent(history.State, options.Content);
        WorldHost.RequireBudget(history.State.Time, options.Until, options.MaxSteps);
        if (options.MaxCalls < 1 || options.MaxFailures < 1 || options.MaxContextCharacters < 1) { throw new ArgumentOutOfRangeException(nameof(options)); }
        if ((options.Gym == true || options.Branch is not null) && store is null) { throw new ArgumentException("详细人物记录和分支运行需要存档。"); }
        if (bindings.Count != history.State.Characters.Count || history.State.Characters.Any(character => !bindings.ContainsKey(character.Id)) ||
            bindings.Values.Any(binding => binding is not CharacterControlBinding.HumanConsole and not CharacterControlBinding.Llm)) {
            throw new ArgumentException("控制绑定必须恰好覆盖本局全部角色。");
        }
        ValidateConnections(bindings, configuration);
        bindings = bindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        string[] humanActors = bindings.Where(pair => pair.Value is CharacterControlBinding.HumanConsole).Select(pair => pair.Key).ToArray();
        bool detailed = options.Gym ?? (store is not null);
        string runId = Guid.NewGuid().ToString("N");
        string? logDirectory = store ?? options.HomePath;
        string? logPath = options.LogPath ?? (logDirectory is null ? null : Path.Combine(logDirectory, "llm-runs", runId + ".jsonl"));
        using PlaygroundHostJournal journal = journalFactory is not null ? journalFactory(store)
            : store is null ? PlaygroundHostJournal.CreateInMemory()
                : PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(store));
        var session = new PlaygroundHostSession(journal, durable, runId, detailed);
        var operations = new PlaygroundOperationAudit(journal, session.BranchId, runId);
        using var raw = new LlmRawAudit(logPath, runId);
        var presenter = new LlmTracePresenter(output, showModelDetails: humanActors.Length == 0);
        string? feedbackDirectory = store ?? options.HomePath;
        using FeedbackSession? feedback = feedbackDirectory is null ? null
            : new FeedbackSession(FeedbackSession.JournalPath(feedbackDirectory), durable, runId, store);
        // The forwarding controller cannot invoke a provider until recovery and reconciliation finish.
        var forwarding = new RecordedController(operations);
        var human = humanActors.Length > 0 ? new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            inputReader: inputReader, outputWriter: output, writeBoundary: ReferenceEquals(output, Console.Out) ? PlayConsole.WriteBoundary : null, operations: operations,
            worldVersion: () => $"{history.Cursor.Version.LineageId}:{history.Cursor.Version.TransitionCount}",
            feedbackSink: feedback is null ? null : feedback.SaveHuman,
            feedbackSaved: (context, feedbackId) => session.FeedbackSaved(context.InteractionState.ActorId, context.Context.ContextSequence, feedbackId)) : null;
        IPlaygroundCharacterController routed = human is null ? forwarding : new CharacterControllerRouter(
            history.State.Characters.ToDictionary(character => character.Id,
                character => bindings[character.Id] is CharacterControlBinding.HumanConsole ? (IPlaygroundCharacterController)human : forwarding, StringComparer.Ordinal));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: routed);
        session.Start(history.State, options, bindings, logPath, store);
        bool recovered = kernel.RecoverPending(cancellationToken);
        WorldHost.RequireWorld(kernel.World, characterCount);
        if (durable is not null) {
            operations.Reconcile(durable.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new PlaygroundOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
        }
        session.CaptureCommitted(kernel.World);
        // Pool construction does not resolve credentials or instantiate a provider, and it occurs
        // only after pending recovery. A terminal LLM actor and early Human EOF require no client.
        LlmConnectionPool? pool = configuration is null ? null : new(configuration, clientFactory);
        return await RunWithOwnedPoolAsync(pool, async () => {
            void Trace(LlmControllerTrace trace) {
                long? rawSequence = raw.Trace(trace);
                try {
                    session.Trace(trace, rawSequence);
                } catch {
                    if (trace.Kind == LlmControllerTraceKind.Feedback) {
                        output.WriteLine($"反馈已保存：{trace.FeedbackId}；宿主关联未能确认，已暂停，不要重复提交。是否交付回执未知。");
                    }
                    throw;
                }
                presenter.Trace(trace);
            }
            void Stop(string reason) => session.Stop(reason);
            void StopAfterFailure(string reason) {
                if (journal.IsFaulted) { return; }
                try { Stop(reason); }
                catch (Exception error) {
                    Console.Error.WriteLine($"宿主结束记录未确认（{error.GetType().Name}）；未自动重试。");
                }
            }
            string bindingSummary = string.Join("，", bindings.Select(pair => $"{pair.Key} = {pair.Value}"));
            output.WriteLine($"山野镇 / Hillside · home：{options.HomePath ?? "(程序注入)"} · store：{store ?? "(内存)"} · branch：{durable?.CurrentBranch.BranchId ?? "(内存)"}\n"
                + $"内容：{kernel.World.Definition.ContentId} / {kernel.World.Definition.Fingerprint} · 角色绑定：{bindingSummary}\n"
                + $"预算：calls={options.MaxCalls}, failures={options.MaxFailures}, context={options.MaxContextCharacters}, steps={options.MaxSteps}, until={options.Until} · Gym：{(detailed ? "record" : "off")}\n"
                + $"连接配置：{options.ConnectionsPath ?? "(程序注入)"} · 控制配置：{options.CharacterControlsPath ?? "(默认/程序注入)"} · 模型原始审计：{(logPath is null ? "(未配置位置)" : Path.GetFullPath(logPath))}（首次调用创建）。Ctrl+C 暂停。");
            if (recovered) { output.WriteLine("已完成上次中断时保存的行动；没有重新询问控制器。"); }
            if (journal.DiscardedTailBytes > 0) { output.WriteLine($"宿主记录末尾 {journal.DiscardedTailBytes} 字节未完整写入；已丢弃未完成尾，游戏进度按已提交 E/S 恢复。"); }
            if (human is null) { output.WriteLine("全模型观战：以下模型材料为宿主观察证据，不是任何单个角色拥有的知识。"); }
            var deliveredTerminals = new HashSet<string>(StringComparer.Ordinal);
            void DeliverTerminals() {
                if (human is not null) { foreach (string actorId in humanActors) { human.RenderCommitted(kernel.World, actorId); } }
                foreach (PlaygroundCharacter character in kernel.World.Characters.Where(character => !character.IsActive)) {
                    if (bindings[character.Id] is not CharacterControlBinding.HumanConsole && deliveredTerminals.Add(character.Id)) {
                        presenter.Terminal(kernel.World, character.Id);
                    }
                }
            }
            bool publishingDiagnostics = false;
            try {
                forwarding.Inner = new LlmCharacterController(async (actorId, token) => {
                    if (configuration is not null) {
                        return await pool!.GetAsync(((CharacterControlBinding.Llm)bindings[actorId]).ConnectionId, token);
                    }
                    if (suppliedClient is not null) { return new LlmDecisionBinding(suppliedClient, "fixture-model", "fixture"); }
                    throw new ArgumentException("LLM 决策需要显式连接配置。");
                }, context => CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout()),
                    new LlmControllerOptions { MaxCalls = options.MaxCalls, MaxConsecutiveProtocolFailures = options.MaxFailures,
                        MaxContextCharacters = options.MaxContextCharacters, MaxFeedbackCharacters = FeedbackRecord.MaxTextCharacters }, Trace,
                    feedbackSink: feedback is null ? null : feedback.SaveLlm);
                int completed = 0;
                while (true) {
                    cancellationToken.ThrowIfCancellationRequested();
                    DeliverTerminals();
                    if (!kernel.World.Characters.Any(character => character.IsActive)) {
                        Stop("summary");
                        presenter.Write("summary", new { Outcome = kernel.World.ExpeditionSummary.ToString() }, WorldHost.Summary(kernel.World));
                        return 0;
                    }
                    if (completed >= options.MaxSteps || options.Until is { } limit && kernel.CurrentModelTime >= limit) {
                        Stop(completed >= options.MaxSteps ? "step-budget" : "time-budget");
                        output.WriteLine("本次宿主预算已到，世界暂停；可从同一存档继续。");
                        return 0;
                    }
                    StepStatus status = await kernel.StepAsync(options.Until ?? new ModelTime(long.MaxValue), cancellationToken);
                    if (status != StepStatus.Committed) {
                        Stop(status.ToString());
                        output.WriteLine($"世界暂停：{status}。保留最后完成事件的时刻，可从同一存档继续。");
                        return 0;
                    }
                    completed++;
                    publishingDiagnostics = true;
                    PlaygroundContextDiagnostics.Publish(kernel.LastCompletion!, diagnosticSink);
                    publishingDiagnostics = false;
                    PlayConsole.CompleteOperations(operations, kernel.LastCompletion!);
                    session.CaptureCommitted(kernel.World);
                    foreach (PlaygroundFact fact in kernel.LastCompletion!.Event.Facts) {
                        presenter.Write("committed", new { At = fact.At.Ticks, Kind = fact.Kind.ToString(), fact.ActorId,
                            fact.ToolCallId, fact.OperationRecordId, Disposition = fact.OperationDisposition?.ToString(),
                            Result = fact.OperationRecordId is null ? null : PlaygroundOperationResults.Describe(fact),
                            Version = kernel.Version.ToString() },
                            $"{fact.At} · {fact.Kind}" + (fact.OperationRecordId is null ? "" : $" · {fact.OperationDisposition}\n{PlaygroundOperationResults.Describe(fact)}"));
                    }
                }
            } catch (PlayConsole.PlayEndedException) {
                StopAfterFailure("human-session-ended");
                output.WriteLine("Human 会话已结束；世界暂停。续局使用同一实例与所需控制配置。");
                return journal.IsFaulted ? 1 : 0;
            } catch (LlmControllerPausedException error) {
                StopAfterFailure(error.ReasonCode);
                output.WriteLine($"LLM 已暂停（{error.ReasonCode}），未代选行动。已提交状态保持；续局需重新指定连接与预算。");
                return journal.IsFaulted ? 1 : 2;
            } catch (OperationCanceledException) {
                StopAfterFailure("cancelled");
                output.WriteLine(store is null ? "宿主已取消（cancelled）；本次内存进度未保存。" : "宿主已取消（cancelled）；已提交进度保存在同一 --store 中。");
                return 130;
            } catch (Exception error) when (!publishingDiagnostics) {
                // Provider/configuration exception messages may contain endpoint payloads. Never print them here.
                string reason = error switch {
                    CodexSubscriptionCredentialException credential => "credential/" + credential.Reason,
                    FileNotFoundException => "configuration-file-missing",
                    UnauthorizedAccessException => "access-denied",
                    InvalidDataException => "invalid-configuration-or-data",
                    ArgumentException => "invalid-configuration",
                    IOException => "io-failure",
                    _ => "host-failure"
                };
                StopAfterFailure(reason);
                Console.Error.WriteLine($"宿主已停止（{error.GetType().Name} / {reason}）；已提交结果保留，未自动重试。");
                output.WriteLine($"宿主已停止（{error.GetType().Name}）；未自动代选行动。请检查配置或证据记录后重开。");
                return 1;
            }
        });
    }

    private static async Task<int> RunWithOwnedPoolAsync(LlmConnectionPool? pool, Func<Task<int>> run) {
        int exitCode;
        try {
            exitCode = await run();
        } catch {
            // Provider cleanup has no authority to replace a known publication/diagnostic failure.
            await DisposePoolAsync(pool);
            throw;
        }
        bool cleaned = await DisposePoolAsync(pool);
        // Preserve pause, cancellation and failure results. A cleanup failure after an otherwise
        // clean loop is a failed host shutdown, while all committed world progress remains valid.
        return !cleaned && exitCode == 0 ? 1 : exitCode;
    }

    private static async Task<bool> DisposePoolAsync(LlmConnectionPool? pool) {
        if (pool is null) { return true; }
        try {
            await pool.DisposeAsync();
            return true;
        } catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) {
            // Do not log to the journal: it may already be retired. Provider exception text can
            // contain payloads, so only the exception type and fixed diagnostic are exposed.
            try {
                Console.Error.WriteLine($"LLM 连接清理失败（{error.GetType().Name}）；未自动重试。已知宿主失败或暂停结果及已提交进度保留；原正常结束改为失败退出。");
            } catch (Exception diagnosticError) when (diagnosticError is IOException or ObjectDisposedException) {
                // A broken stderr must not replace the original host outcome either.
            }
            return false;
        }
    }

    private static void RequireCurrentHostFormat(string store) {
        if (!Directory.Exists(store)) { return; }
        bool oldOperations = File.Exists(Path.Combine(store, "operations.jsonl"));
        string oldBranchOperations = Path.Combine(store, "branch-operations");
        bool oldBranchRecords = Directory.Exists(oldBranchOperations)
            && Directory.EnumerateFiles(oldBranchOperations, "*.jsonl", SearchOption.AllDirectories).Any();
        string oldGym = Path.Combine(store, "gym");
        bool oldGymRecords = Directory.Exists(oldGym)
            && Directory.EnumerateFiles(oldGym, "*.jsonl", SearchOption.AllDirectories).Any();
        if (!oldOperations && !oldBranchRecords && !oldGymRecords) { return; }
        string path = PlaygroundHostJournal.JournalPath(store);
        if (File.Exists(path) && PlaygroundHostJournal.Read(path).JournalId is not null) { return; }
        throw new InvalidDataException("此 store 含旧宿主 operations/Gym 日志，不能忽略其待对账记录继续运行。请保留旧 store，使用新的 store 开始；home 与连接配置可继续使用。旧版本工具可从 Git 获取，本版本不迁移或改写旧文件。");
    }

    private static PlaygroundDurableHistory CreateStore(string path, int? characterCount, PlaygroundHostOptions options,
        CharacterControlConfiguration controls, LlmConnectionConfiguration? configuration) {
        var initial = PlaygroundGameFactory.Create(WorldHost.Definition(options.Content, characterCount));
        WorldHost.RequireWorld(initial.World, characterCount);
        WorldHost.RequireBudget(initial.World.Time, options.Until, options.MaxSteps);
        ValidateConnections(controls.Resolve(initial.World.Characters.Select(actor => actor.Id), configuration?.DefaultConnectionId ?? "fixture"), configuration);
        return PlaygroundDurableHistory.Create(path, initial.World, initial.Cursor);
    }

    private static void ValidateConnections(IReadOnlyDictionary<string, CharacterControlBinding> bindings, LlmConnectionConfiguration? configuration) {
        foreach (CharacterControlBinding.Llm binding in bindings.Values.OfType<CharacterControlBinding.Llm>()) {
            if (string.IsNullOrWhiteSpace(binding.ConnectionId) || configuration is not null &&
                !configuration.ConnectionIds.Contains(binding.ConnectionId, StringComparer.Ordinal)) {
                throw new ArgumentException("控制绑定引用了无效连接。");
            }
        }
    }

    private sealed class RecordedController(PlaygroundOperationAudit operations) : IPlaygroundCharacterController {
        public IPlaygroundCharacterController? Inner { get; set; }
        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            PlaygroundControllerOperation operation = await (Inner ?? throw new InvalidOperationException("Recovery must precede model binding."))
                .ChooseCommandAsync(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            operations.Receive("llm/" + operation.OperationRecordId, operation.ToolCallId, context.InteractionState.ActorId,
                context.Context.ContextSequence, LlmCharacterController.ToolName, operation.RawInput, operation.OperationRecordId);
            return operation;
        }
    }
}

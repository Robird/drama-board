using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        ICompletionClientFactory? clientFactory = null, Action<PlaygroundDiagnosticRecord>? diagnosticSink = null) {
        cancellationToken.ThrowIfCancellationRequested();
        TextWriter output = outputWriter ?? Console.Out;
        if ((options.Gym || options.Branch is not null) && store is null) {
            throw new ArgumentException("Gym 和分支运行需要 --store。");
        }
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
            output, suppliedClient, characterCount, inputReader, clientFactory, diagnosticSink);
    }

    public static Task<int> RunAsync(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history,
        IReadOnlyDictionary<string, CharacterControlBinding> bindings, PlaygroundHostOptions options, CancellationToken cancellationToken,
        TextWriter? outputWriter = null, ICompletionClient? suppliedClient = null, TextReader? inputReader = null,
        ICompletionClientFactory? clientFactory = null, Action<PlaygroundDiagnosticRecord>? diagnosticSink = null) =>
        RunCoreAsync(history, null, null, options, bindings, null, cancellationToken, outputWriter ?? Console.Out,
            suppliedClient, null, inputReader, clientFactory, diagnosticSink);

    private static async Task<int> RunCoreAsync(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history,
        string? store, PlaygroundDurableHistory? durable, PlaygroundHostOptions options,
        IReadOnlyDictionary<string, CharacterControlBinding> bindings, LlmConnectionConfiguration? configuration,
        CancellationToken cancellationToken, TextWriter output, ICompletionClient? suppliedClient, int? characterCount,
        TextReader? inputReader, ICompletionClientFactory? clientFactory, Action<PlaygroundDiagnosticRecord>? diagnosticSink = null) {
        cancellationToken.ThrowIfCancellationRequested();
        WorldHost.RequireWorld(history.State, characterCount);
        WorldHost.RequireContent(history.State, options.Content);
        WorldHost.RequireBudget(history.State.Time, options.Until, options.MaxSteps);
        if (options.MaxCalls < 1 || options.MaxFailures < 1 || options.MaxContextCharacters < 1) { throw new ArgumentOutOfRangeException(nameof(options)); }
        if ((options.Gym || options.Branch is not null) && store is null) { throw new ArgumentException("Gym 和分支运行需要存档。"); }
        if (bindings.Count != history.State.Characters.Count || history.State.Characters.Any(character => !bindings.ContainsKey(character.Id)) ||
            bindings.Values.Any(binding => binding is not CharacterControlBinding.HumanConsole and not CharacterControlBinding.Llm)) {
            throw new ArgumentException("控制绑定必须恰好覆盖本局全部角色。");
        }
        ValidateConnections(bindings, configuration);
        bindings = bindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        string[] humanActors = bindings.Where(pair => pair.Value is CharacterControlBinding.HumanConsole).Select(pair => pair.Key).ToArray();
        await using LlmConnectionPool? pool = configuration is null ? null : new(configuration, clientFactory);
        string logPath = options.LogPath ?? Path.Combine(store ?? options.HomePath ?? "artifacts", "llm-runs", Guid.NewGuid().ToString("N") + ".jsonl");
        using var audit = new LlmRunAudit(logPath, output, showModelDetails: humanActors.Length == 0);
        using PlaygroundOperationJournal operations = store is null ? PlaygroundOperationJournal.CreateInMemory()
            : PlaygroundOperationJournal.Open(GymSession.OperationsPath(store, durable!));
        string? feedbackDirectory = store ?? options.HomePath;
        using FeedbackSession? feedback = feedbackDirectory is null ? null
            : new FeedbackSession(FeedbackSession.JournalPath(feedbackDirectory), durable, storePath: store);
        // The forwarding controller cannot invoke a provider until recovery and reconciliation finish.
        var forwarding = new RecordedController(operations);
        var human = humanActors.Length > 0 ? new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            inputReader: inputReader, outputWriter: output, writeBoundary: ReferenceEquals(output, Console.Out) ? PlayConsole.WriteBoundary : null, operations: operations,
            worldVersion: () => $"{history.Cursor.Version.LineageId}:{history.Cursor.Version.TransitionCount}",
            feedbackSink: feedback is null ? null : feedback.SaveHuman) : null;
        IPlaygroundCharacterController routed = human is null ? forwarding : new CharacterControllerRouter(
            history.State.Characters.ToDictionary(character => character.Id,
                character => bindings[character.Id] is CharacterControlBinding.HumanConsole ? (IPlaygroundCharacterController)human : forwarding, StringComparer.Ordinal));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: routed);
        bool recovered = kernel.RecoverPending(cancellationToken);
        WorldHost.RequireWorld(kernel.World, characterCount);
        if (durable is not null) {
            operations.Reconcile(durable.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new PlaygroundOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
        }
        using GymSession? gym = options.Gym ? new GymSession(store!, durable!, options, bindings, logPath, recovered) : null;
        audit.Gym = gym;
        gym?.CaptureCommitted(kernel.World, operations);
        audit.Write("host", new { State = "ready", RecoveredPending = recovered, Home = options.HomePath, Store = store,
            Branch = durable?.CurrentBranch.BranchId, options.ConnectionsPath, options.CharacterControlsPath,
            Gym = options.Gym ? "record" : "off", LogPath = Path.GetFullPath(logPath),
            Version = kernel.Version.ToString(), Content = new { kernel.World.Definition.ContentId, kernel.World.Definition.Fingerprint }, Actors = kernel.World.Characters.Select(character => new { ActorId = character.Id, Controller = bindings[character.Id] is CharacterControlBinding.HumanConsole ? "Human" : "LLM",
                ConnectionId = (bindings[character.Id] as CharacterControlBinding.Llm)?.ConnectionId }).ToArray(),
            Budget = new { options.MaxCalls, options.MaxFailures, options.MaxContextCharacters, options.Until, options.MaxSteps } });
        string bindingSummary = string.Join("，", bindings.Select(pair => $"{pair.Key} = {pair.Value}"));
        output.WriteLine($"山野镇 / Hillside · home：{options.HomePath ?? "(程序注入)"} · store：{store ?? "(内存)"} · branch：{durable?.CurrentBranch.BranchId ?? "(内存)"}\n"
            + $"内容：{kernel.World.Definition.ContentId} / {kernel.World.Definition.Fingerprint} · 角色绑定：{bindingSummary}\n"
            + $"预算：calls={options.MaxCalls}, failures={options.MaxFailures}, context={options.MaxContextCharacters}, steps={options.MaxSteps}, until={options.Until} · Gym：{(options.Gym ? "record" : "off")}\n"
            + $"连接配置：{options.ConnectionsPath ?? "(程序注入)"} · 控制配置：{options.CharacterControlsPath ?? "(默认/程序注入)"} · 日志：{Path.GetFullPath(logPath)}。Ctrl+C 暂停。");
        if (recovered) { output.WriteLine("已完成上次中断时保存的行动；没有重新询问控制器。"); }
        if (operations.DiscardedTailBytes > 0) { output.WriteLine($"操作记录末尾 {operations.DiscardedTailBytes} 字节未完整写入；游戏进度按已提交 E/S 恢复。"); }
        if (human is null) { output.WriteLine("全模型观战：以下模型材料为宿主观察证据，不是任何单个角色拥有的知识。"); }
        var deliveredTerminals = new HashSet<string>(StringComparer.Ordinal);
        void DeliverTerminals() {
            if (human is not null) { foreach (string actorId in humanActors) { human.RenderCommitted(kernel.World, actorId); } }
            foreach (PlaygroundCharacter character in kernel.World.Characters.Where(character => !character.IsActive)) {
                if (bindings[character.Id] is not CharacterControlBinding.HumanConsole && deliveredTerminals.Add(character.Id)) {
                    Terminal(kernel.World, character.Id, audit);
                }
            }
        }
        bool publishingDiagnostics = false;
        try {
            // Initialize the external client only on an actual live LLM decision. A saved terminal b
            // and an early Human EOF require neither credentials nor a network client.
            forwarding.Inner = new LlmCharacterController(async (actorId, token) => {
                if (configuration is not null) {
                    return await pool!.GetAsync(((CharacterControlBinding.Llm)bindings[actorId]).ConnectionId, token);
                }
                if (suppliedClient is not null) {
                    return new LlmDecisionBinding(suppliedClient, "fixture-model", "fixture");
                }
                throw new ArgumentException("LLM 决策需要显式连接配置。");
            }, context => CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout()),
                new LlmControllerOptions { MaxCalls = options.MaxCalls, MaxConsecutiveProtocolFailures = options.MaxFailures,
                    MaxContextCharacters = options.MaxContextCharacters, MaxFeedbackCharacters = FeedbackRecord.MaxTextCharacters }, audit.Trace,
                feedbackSink: feedback is null ? null : feedback.SaveLlm);
            int completed = 0;
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                DeliverTerminals();
                if (!kernel.World.Characters.Any(character => character.IsActive)) {
                    audit.Write("summary", new { Outcome = kernel.World.ExpeditionSummary.ToString() }, WorldHost.Summary(kernel.World));
                    return 0;
                }
                if (completed >= options.MaxSteps || options.Until is { } limit && kernel.CurrentModelTime >= limit) {
                    audit.Write("host-stop", new { Reason = completed >= options.MaxSteps ? "step-budget" : "time-budget", Saved = store is not null });
                    output.WriteLine("本次宿主预算已到，世界暂停；可从同一存档继续。");
                    return 0;
                }
                StepStatus status = await kernel.StepAsync(options.Until ?? new ModelTime(long.MaxValue), cancellationToken);
                if (status != StepStatus.Committed) {
                    audit.Write("host-stop", new { Reason = status.ToString(), Saved = store is not null });
                    output.WriteLine($"世界暂停：{status}。保留最后完成事件的时刻，可从同一存档继续。");
                    return 0;
                }
                completed++;
                publishingDiagnostics = true;
                PlaygroundContextDiagnostics.Publish(kernel.LastCompletion!, diagnosticSink);
                publishingDiagnostics = false;
                PlayConsole.CompleteOperations(operations, kernel.LastCompletion!);
                gym?.CaptureCommitted(kernel.World, operations);
                foreach (PlaygroundFact fact in kernel.LastCompletion!.Event.Facts) {
                    audit.Write("committed", new { At = fact.At.Ticks, Kind = fact.Kind.ToString(), fact.ActorId,
                        fact.ToolCallId, fact.OperationRecordId, Disposition = fact.OperationDisposition?.ToString(),
                        Result = fact.OperationRecordId is null ? null : PlaygroundOperationResults.Describe(fact),
                        Version = kernel.Version.ToString() },
                        $"{fact.At} · {fact.Kind}" + (fact.OperationRecordId is null ? "" : $" · {fact.OperationDisposition}\n{PlaygroundOperationResults.Describe(fact)}"));
                }
            }
        } catch (PlayConsole.PlayEndedException) {
            audit.Write("host-stop", new { Reason = "human-session-ended", Saved = store is not null });
            output.WriteLine("Human 会话已结束；世界暂停。续局使用同一实例与所需控制配置。");
            return 0;
        } catch (LlmControllerPausedException error) {
            audit.Write("host-stop", new { Reason = error.ReasonCode, error.CallsMade, Saved = store is not null });
            output.WriteLine("LLM 已暂停，未代选行动。已提交状态保持；续局需重新指定连接与预算。");
            return 2;
        } catch (OperationCanceledException) {
            audit.Write("host-stop", new { Reason = "cancelled", Saved = store is not null });
            output.WriteLine(store is null ? "宿主已取消；本次内存进度未保存。" : "宿主已取消；已提交进度保存在同一 --store 中。");
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
            audit.Write("host-stop", new { Reason = reason, ErrorType = error.GetType().Name, Saved = store is not null });
            output.WriteLine($"宿主已停止（{error.GetType().Name}）；未自动代选行动。请检查配置或证据记录后重开。");
            return 1;
        }
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

    private static void Terminal(PlaygroundWorld world, string actorId, LlmRunAudit audit) {
        string text = CharacterContextRenderer.RenderFull(world.Business.InteractionStates[actorId], new DecisionPlayLayout());
        audit.Write("terminal", new { ActorId = actorId, Outcome = world.GetCharacter(actorId).Ending!.Outcome.ToString(), Context = text });
        if (audit.ShowModelDetails) { audit.Output.WriteLine(text); }
    }

    private sealed class RecordedController(PlaygroundOperationJournal journal) : IPlaygroundCharacterController {
        public IPlaygroundCharacterController? Inner { get; set; }
        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            PlaygroundControllerOperation operation = await (Inner ?? throw new InvalidOperationException("Recovery must precede model binding."))
                .ChooseCommandAsync(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            journal.Receive("llm/" + operation.OperationRecordId, operation.ToolCallId, context.InteractionState.ActorId,
                context.Context.ContextSequence, LlmCharacterController.ToolName, operation.RawInput, operation.OperationRecordId);
            return operation;
        }
    }
}

/// <summary>One fresh file per host run. Not a replay log, game authority, or credential dump.</summary>
internal sealed class LlmRunAudit : IDisposable {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly StreamWriter _file;
    public TextWriter Output { get; }
    public bool ShowModelDetails { get; }
    public GymSession? Gym { get; set; }
    public LlmRunAudit(string path, TextWriter output, bool showModelDetails = true) {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        Output = output;
        ShowModelDetails = showModelDetails;
    }
    public void Write(string kind, object payload, string? summary = null) {
        if (kind == "host-stop" || kind == "summary") {
            JsonElement details = JsonSerializer.SerializeToElement(payload, JsonOptions);
            Gym?.Stop(details.TryGetProperty("Reason", out var reason) ? reason.GetString() ?? kind : kind);
        }
        string json = JsonSerializer.Serialize(new { Kind = kind, Payload = payload }, JsonOptions);
        _file.WriteLine(json);
        // A Human Console sees only that character's committed material. Complete model/operation
        // evidence remains available to the developer in the separate audit, never as player perception.
        if (!ShowModelDetails && kind is "model-input" or "model-output" or "terminal" or "committed" or "feedback-receipt") { return; }
        // Full request/response evidence stays in JSONL; do not dump opaque provider replay/reasoning blobs to the terminal.
        Output.WriteLine(summary is not null ? $"[{kind}] {summary}"
            : kind is "model-input" or "model-output" or "terminal" ? $"[{kind}]" : $"[{kind}] {json}");
        Output.Flush();
    }
    public void Trace(LlmControllerTrace trace) {
        Gym?.Trace(trace);
        if (trace.Kind == LlmControllerTraceKind.Feedback) {
            Write("feedback-receipt", new { trace.RequestId, trace.FeedbackId }, "反馈已保存；继续选择行动。是否收到回执以随后请求为准。");
            return;
        }
        if (trace.Kind == LlmControllerTraceKind.Request) {
            Write("model-input", new { trace.RequestId, trace.CallNumber, trace.CharacterId, trace.ContextSequence,
                trace.ConnectionId, trace.ApiSpecId, trace.Request.ModelId, trace.Request.PromptPrefix.SystemPrompt,
                OutputContract = GymMessageProjection.Contract(trace.Request.PromptPrefix.OutputContract),
                Shared = trace.Request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
                Tail = trace.Request.TailMessages.Select(GymMessageProjection.Message).ToArray() });
            if (!ShowModelDetails) { return; }
            Output.WriteLine(trace.Request.PromptPrefix.SystemPrompt);
            foreach (ObservationMessage material in trace.Request.PromptPrefix.SharedContextMessages.Concat(trace.Request.TailMessages).OfType<ObservationMessage>()) {
                Output.WriteLine(material.Content);
            }
        } else {
            Write("model-output", new { trace.RequestId, trace.CallNumber, trace.CharacterId, trace.ContextSequence,
                trace.ConnectionId, trace.ApiSpecId, trace.ModelId,
                Stage = trace.Kind.ToString(), trace.ProtocolError, trace.Operation,
                Invocation = trace.Result?.Invocation, Termination = trace.Result?.Termination, Usage = trace.Result?.Usage,
                Blocks = trace.Result?.Message.Blocks.Select(block => GymMessageProjection.Runtime(block)).ToArray() });
            if (!ShowModelDetails) { return; }
            Output.WriteLine($"调用 {trace.CallNumber}：{trace.Result?.Termination.Kind.ToString() ?? trace.Kind.ToString()}；"
                + $"{trace.ProtocolError ?? (trace.Operation is null ? "未形成行动" : "待游戏校验，尚未提交")}");
            if (trace.Result is { } result) {
                foreach (ActionBlock.Text text in result.Message.Blocks.OfType<ActionBlock.Text>()) {
                    Output.WriteLine("模型说明（非游戏对白）：" + CharacterContextRenderer.DataLiteral(text.Content));
                }
                foreach (RawToolCall call in result.Message.ToolCalls) {
                    Output.WriteLine($"工具：{CharacterContextRenderer.DataLiteral(call.ToolName)}；ID：{CharacterContextRenderer.DataLiteral(call.ToolCallId)}；"
                        + (trace.Operation is { } operation ? $"待校验行动：{CharacterContextRenderer.DataLiteral(operation.RawInput)}"
                            : $"参数：{CharacterContextRenderer.DataLiteral(call.RawInput)}"));
                }
            }
            Output.Flush();
        }
    }
    public void Dispose() => _file.Dispose();
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Completion;

internal sealed record LlmHostOptions(string? Model = null, string? AuthFile = null, string? Catalog = null,
    string? Connection = null, int MaxCalls = 128, int MaxFailures = 3, int MaxContextCharacters = 200_000,
    string? LogPath = null, bool Gym = false, bool Feedback = false, string? Branch = null,
    string? HumanActor = null, ModelTime? Until = null, int MaxSteps = 10_000, string? Content = null);

/// <summary>Serial spectator host. Only committed game state is recovery authority; model audit is evidence.</summary>
internal static class LlmConsole {
    public static async Task<int> RunAsync(string? store, LlmHostOptions options, CancellationToken cancellationToken,
        TextWriter? outputWriter = null, ICompletionClient? suppliedClient = null, int? characterCount = null, TextReader? inputReader = null) {
        TextWriter output = outputWriter ?? Console.Out;
        if ((options.Gym || options.Feedback || options.Branch is not null) && store is null) {
            throw new ArgumentException("Gym、反馈和分支运行需要 --store。");
        }
        using PlaygroundDurableHistory? durable = store is null ? null : Directory.Exists(store)
            ? options.Branch is null ? PlaygroundDurableHistory.Open(store) : PlaygroundDurableHistory.Open(store, options.Branch)
            : options.Branch is null ? CreateStore(store, characterCount, options.Content) : throw new ArgumentException("指定分支需要已有存档。");
        IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history;
        if (durable is not null) { history = durable; }
        else {
            var initial = PlaygroundGameFactory.Create(WorldHost.Definition(options.Content, characterCount));
            history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        }
        WorldHost.RequireWorld(history.State, characterCount);
        WorldHost.RequireContent(history.State, options.Content);
        WorldHost.RequireBudget(history.State.Time, options.Until, options.MaxSteps);
        if (options.HumanActor is { } selectedHuman && !history.State.Characters.Any(actor => actor.Id == selectedHuman)) {
            throw new ArgumentException($"Human 角色 {selectedHuman} 不在此存档中。");
        }
        string logPath = options.LogPath ?? Path.Combine(store ?? "artifacts", "llm-runs", Guid.NewGuid().ToString("N") + ".jsonl");
        using var audit = new LlmRunAudit(logPath, output, showModelDetails: options.HumanActor is null);
        using PlaygroundOperationJournal operations = store is null ? PlaygroundOperationJournal.CreateInMemory()
            : PlaygroundOperationJournal.Open(GymSession.OperationsPath(store, durable!));
        // The forwarding controller cannot invoke a provider until recovery and reconciliation finish.
        var forwarding = new RecordedController(operations);
        var human = options.HumanActor is not null ? new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            inputReader: inputReader, outputWriter: output, operations: operations,
            worldVersion: () => $"{history.Cursor.Version.LineageId}:{history.Cursor.Version.TransitionCount}") : null;
        IPlaygroundCharacterController routed = human is null ? forwarding : new CharacterControllerRouter(
            history.State.Characters.ToDictionary(character => character.Id,
                character => character.Id == options.HumanActor ? (IPlaygroundCharacterController)human : forwarding, StringComparer.Ordinal));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: routed);
        bool recovered = kernel.RecoverPending(cancellationToken);
        WorldHost.RequireWorld(kernel.World, characterCount);
        if (durable is not null) {
            operations.Reconcile(durable.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new PlaygroundOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
        }
        using GymSession? gym = options.Gym || options.Feedback ? new GymSession(store!, durable!, options, logPath, recovered) : null;
        audit.Gym = gym;
        gym?.CaptureCommitted(kernel.World, operations);
        audit.Write("host", new { State = "ready", RecoveredPending = recovered, Store = store,
            Version = kernel.Version.ToString(), Content = new { kernel.World.Definition.ContentId, kernel.World.Definition.Fingerprint }, Actors = kernel.World.Characters.Select(character => new { ActorId = character.Id, Controller = human is not null && character.Id == options.HumanActor ? "Human" : "LLM" }).ToArray(),
            Budget = new { options.MaxCalls, options.MaxFailures, options.MaxContextCharacters, options.Until, options.MaxSteps } });
        string bindings = string.Join("，", kernel.World.Characters.Select(character => $"{character.Id} = {(human is not null && character.Id == options.HumanActor ? "Human" : "LLM")}"));
        output.WriteLine($"山野镇 / Hillside · 内容 {kernel.World.Definition.ContentId} / {kernel.World.Definition.Fingerprint} · 角色绑定：{bindings}；证据：{Path.GetFullPath(logPath)}。Ctrl+C 暂停；重开时重新指定 --llm、可选 --human 及连接配置。");
        if (human is null) { output.WriteLine("全模型观战：以下模型材料为宿主观察证据，不是任何单个角色拥有的知识。"); }
        CompletionConnectionBinding? binding = null;
        var deliveredTerminals = new HashSet<string>(StringComparer.Ordinal);
        void DeliverTerminals() {
            if (human is not null) { human.RenderCommitted(kernel.World, options.HumanActor!); }
            foreach (PlaygroundCharacter character in kernel.World.Characters.Where(character => !character.IsActive)) {
                if ((human is null || character.Id != options.HumanActor) && deliveredTerminals.Add(character.Id)) {
                    Terminal(kernel.World, character.Id, audit);
                }
            }
        }
        try {
            // Initialize the external client only on an actual live LLM decision. A saved terminal b
            // and an early Human EOF require neither credentials nor a network client.
            forwarding.Inner = new LazyController(async () => {
                string model;
                ICompletionClient client;
                if (suppliedClient is not null) {
                    client = suppliedClient;
                    model = options.Model ?? "gpt-6.1-sol";
                } else {
                    binding = await CompletionConnectionBinding.ConnectAsync(new(options.Model, options.AuthFile, options.Catalog, options.Connection), cancellationToken);
                    string connectionId = binding.ConnectionId;
                    model = binding.Model;
                    client = binding.Client;
                    audit.Write("binding", new { ActorIds = kernel.World.Characters.Where(character => human is null || character.Id != options.HumanActor).Select(character => character.Id).ToArray(), ConnectionId = connectionId, ModelId = model });
                }
                return new LlmCharacterController(client, model,
                    context => CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout()),
                    new LlmControllerOptions { MaxCalls = options.MaxCalls, MaxConsecutiveProtocolFailures = options.MaxFailures,
                        MaxContextCharacters = options.MaxContextCharacters }, audit.Trace,
                    feedbackSink: options.Feedback ? gym!.SaveFeedback : null);
            });
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
            output.WriteLine("Human 会话已结束；世界暂停。续局使用 --llm、可选 --human 与同一存档及连接配置。");
            return 0;
        } catch (LlmControllerPausedException error) {
            audit.Write("host-stop", new { Reason = error.ReasonCode, error.CallsMade, Saved = store is not null });
            output.WriteLine("LLM 已暂停，未代选行动。已提交状态保持；续局需重新指定连接与预算。");
            return 2;
        } catch (OperationCanceledException) {
            audit.Write("host-stop", new { Reason = "cancelled", Saved = store is not null });
            output.WriteLine(store is null ? "LLM 已取消；本次内存进度未保存。" : "LLM 已取消；已提交进度保存在同一 --store 中。");
            return 130;
        } catch (Exception error) {
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
            output.WriteLine($"LLM 宿主已停止（{error.GetType().Name}）；未自动代选行动。请检查配置或证据记录后重开。");
            return 1;
        } finally {
            if (binding is not null) { await binding.DisposeAsync(); }
        }
    }

    private static PlaygroundDurableHistory CreateStore(string path, int? characterCount, string? content) {
        var initial = PlaygroundGameFactory.Create(WorldHost.Definition(content, characterCount));
        return PlaygroundDurableHistory.Create(path, initial.World, initial.Cursor);
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

    private sealed class LazyController(Func<Task<IPlaygroundCharacterController>> create) : IPlaygroundCharacterController {
        private IPlaygroundCharacterController? _inner;
        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            _inner ??= await create();
            return await _inner.ChooseCommandAsync(context, cancellationToken);
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
                trace.Request.ModelId, trace.Request.PromptPrefix.SystemPrompt,
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

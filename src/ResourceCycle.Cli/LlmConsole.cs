using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;
using DramaBoard.ResourceCycle.Completion;

internal sealed record LlmHostOptions(string? Model = null, string? AuthFile = null, string? Catalog = null,
    string? Connection = null, int MaxCalls = 128, int MaxFailures = 3, int MaxContextCharacters = 200_000,
    string? LogPath = null, bool Gym = false, bool Feedback = false, string? Branch = null);

/// <summary>Serial spectator host. Only committed game state is recovery authority; model audit is evidence.</summary>
internal static class LlmConsole {
    public static async Task<int> RunAsync(string? store, LlmHostOptions options, CancellationToken cancellationToken,
        TextWriter? outputWriter = null, ICompletionClient? suppliedClient = null, bool duo = false, TextReader? inputReader = null) {
        TextWriter output = outputWriter ?? Console.Out;
        if ((options.Gym || options.Feedback || options.Branch is not null) && store is null) {
            throw new ArgumentException("Gym、反馈和分支运行需要 --store。");
        }
        using CycleDurableHistory? durable = store is null ? null : Directory.Exists(store)
            ? options.Branch is null ? CycleDurableHistory.Open(store) : CycleDurableHistory.Open(store, options.Branch)
            : options.Branch is null ? CreateStore(store, duo) : throw new ArgumentException("指定分支需要已有存档。");
        IOccurrenceHistory<CycleWorld, CycleFact> history;
        if (durable is not null) { history = durable; }
        else {
            var initial = CycleGameFactory.CreateSurvival(characterCount: duo ? 2 : 1);
            history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        }
        SurvivalHost.RequireMode(history.State, duo);
        string logPath = options.LogPath ?? Path.Combine(store ?? "artifacts", duo ? "duo-llm-runs" : "llm-runs", Guid.NewGuid().ToString("N") + ".jsonl");
        using var audit = new LlmRunAudit(logPath, output);
        using CycleOperationJournal operations = store is null ? CycleOperationJournal.CreateInMemory()
            : CycleOperationJournal.Open(GymSession.OperationsPath(store, durable!));
        // The forwarding controller cannot invoke a provider until recovery and reconciliation finish.
        var forwarding = new RecordedController(operations);
        var human = duo ? new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            inputReader: inputReader, outputWriter: output, operations: operations,
            worldVersion: () => $"{history.Cursor.Version.LineageId}:{history.Cursor.Version.TransitionCount}") : null;
        ICycleCharacterController routed = human is null ? forwarding : new CharacterControllerRouter(
            new Dictionary<string, ICycleCharacterController> { ["a"] = human, ["b"] = forwarding });
        var kernel = CycleEngine.CreateKernel(history, characterController: routed);
        bool recovered = kernel.RecoverPending(cancellationToken);
        SurvivalHost.RequireMode(kernel.World, duo);
        if (durable is not null) {
            operations.Reconcile(durable.ReadCommittedOperations(operations.PendingOperationRecordIds)
                .Select(commit => new CycleOperationCommit(commit.OperationRecordId, commit.ToolCallId, commit.ActorId,
                    commit.Result, $"{commit.LineageId}:{commit.TransitionCount}", commit.DomainEvent, commit.Disposition)));
        }
        using GymSession? gym = options.Gym || options.Feedback ? new GymSession(store!, durable!, options, logPath, recovered) : null;
        audit.Gym = gym;
        gym?.CaptureCommitted(kernel.World, operations);
        audit.Write("host", new { State = "ready", RecoveredPending = recovered, Store = store,
            Version = kernel.Version.ToString(), Budget = new { options.MaxCalls, options.MaxFailures, options.MaxContextCharacters } });
        output.WriteLine($"{(duo ? "双角色模式：a = Human，b = LLM" : "LLM 旁观模式")}；证据：{Path.GetFullPath(logPath)}。Ctrl+C 暂停；重开时重新指定 {(duo ? "--duo" : "--survival")} --llm 和连接配置。");
        CompletionConnectionBinding? binding = null;
        var deliveredTerminals = new HashSet<string>(StringComparer.Ordinal);
        void DeliverTerminals() {
            human?.RenderCommitted(kernel.World, "a");
            foreach (CycleCharacter character in kernel.World.Characters.Where(character => !character.IsActive)) {
                if ((human is null || character.Id != "a") && deliveredTerminals.Add(character.Id)) {
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
                    audit.Write("binding", new { ActorId = duo ? "b" : "a", ConnectionId = connectionId, ModelId = model });
                }
                return new LlmCharacterController(client, model,
                    context => CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout()),
                    new LlmControllerOptions { MaxCalls = options.MaxCalls, MaxConsecutiveProtocolFailures = options.MaxFailures,
                        MaxContextCharacters = options.MaxContextCharacters }, audit.Trace,
                    feedbackSink: options.Feedback ? gym!.SaveFeedback : null);
            });
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                DeliverTerminals();
                if (!kernel.World.Characters.Any(character => character.IsActive)) {
                    audit.Write("summary", new { Outcome = kernel.World.ExpeditionSummary.ToString() }, SurvivalHost.Summary(kernel.World));
                    return 0;
                }
                StepStatus status = await kernel.StepAsync(new ModelTime(long.MaxValue), cancellationToken);
                if (status != StepStatus.Committed) { throw new InvalidOperationException($"生存世界无法继续：{status}。"); }
                PlayConsole.CompleteOperations(operations, kernel.LastCompletion!);
                gym?.CaptureCommitted(kernel.World, operations);
                foreach (CycleFact fact in kernel.LastCompletion!.Event.Facts) {
                    audit.Write("committed", new { At = fact.At.Ticks, Kind = fact.Kind.ToString(), fact.ActorId,
                        fact.ToolCallId, fact.OperationRecordId, Disposition = fact.OperationDisposition?.ToString(),
                        Result = fact.OperationRecordId is null ? null : CycleOperationResults.Describe(fact),
                        Version = kernel.Version.ToString() },
                        $"{fact.At} · {fact.Kind}" + (fact.OperationRecordId is null ? "" : $" · {fact.OperationDisposition}\n{CycleOperationResults.Describe(fact)}"));
                }
            }
        } catch (PlayConsole.PlayEndedException) {
            audit.Write("host-stop", new { Reason = "human-session-ended", Saved = store is not null });
            output.WriteLine("Human 会话已结束；世界暂停。续局使用 --duo --llm 与同一存档及连接配置。");
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

    private static CycleDurableHistory CreateStore(string path, bool duo) {
        var initial = CycleGameFactory.CreateSurvival(characterCount: duo ? 2 : 1);
        return CycleDurableHistory.Create(path, initial.World, initial.Cursor);
    }

    private static void Terminal(CycleWorld world, string actorId, LlmRunAudit audit) {
        string text = CharacterContextRenderer.RenderFull(world.Business.InteractionStates[actorId], new DecisionPlayLayout());
        audit.Write("terminal", new { ActorId = actorId, Outcome = world.GetCharacter(actorId).Ending!.Outcome.ToString(), Context = text });
        audit.Output.WriteLine(text);
    }

    private sealed class RecordedController(CycleOperationJournal journal) : ICycleCharacterController {
        public ICycleCharacterController? Inner { get; set; }
        public async ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            CycleControllerOperation operation = await (Inner ?? throw new InvalidOperationException("Recovery must precede model binding."))
                .ChooseCommandAsync(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            journal.Receive("llm/" + operation.OperationRecordId, operation.ToolCallId, context.InteractionState.ActorId,
                context.Context.ContextSequence, LlmCharacterController.ToolName, operation.RawInput, operation.OperationRecordId);
            return operation;
        }
    }

    private sealed class LazyController(Func<Task<ICycleCharacterController>> create) : ICycleCharacterController {
        private ICycleCharacterController? _inner;
        public async ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            _inner ??= await create();
            return await _inner.ChooseCommandAsync(context, cancellationToken);
        }
    }
}

/// <summary>One fresh file per host run. Not a replay log, game authority, or credential dump.</summary>
internal sealed class LlmRunAudit : IDisposable {
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly StreamWriter _file;
    public TextWriter Output { get; }
    public GymSession? Gym { get; set; }
    public LlmRunAudit(string path, TextWriter output) {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        Output = output;
    }
    public void Write(string kind, object payload, string? summary = null) {
        if (kind == "host-stop" || kind == "summary") {
            JsonElement details = JsonSerializer.SerializeToElement(payload);
            Gym?.Stop(details.TryGetProperty("Reason", out var reason) ? reason.GetString() ?? kind : kind);
        }
        string json = JsonSerializer.Serialize(new { Kind = kind, Payload = payload }, JsonOptions);
        _file.WriteLine(json);
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
            Output.WriteLine(trace.Request.PromptPrefix.SystemPrompt);
            foreach (ObservationMessage material in trace.Request.PromptPrefix.SharedContextMessages.Concat(trace.Request.TailMessages).OfType<ObservationMessage>()) {
                Output.WriteLine(material.Content);
            }
        } else {
            Write("model-output", new { trace.RequestId, trace.CallNumber, trace.CharacterId, trace.ContextSequence,
                Stage = trace.Kind.ToString(), trace.ProtocolError, trace.Operation,
                Invocation = trace.Result?.Invocation, Termination = trace.Result?.Termination, Usage = trace.Result?.Usage,
                Blocks = trace.Result?.Message.Blocks.Select(block => GymMessageProjection.Runtime(block)).ToArray() });
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

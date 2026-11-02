using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;
using DramaBoard.Playground.Completion;

namespace DramaBoard.PlaygroundParallelism;

internal static class Program {
    public static async Task<int> Main(string[] args) {
        try {
            if (args.Length == 2 && args[0] == "--self-check") { return await SelfCheck.Run(Path.GetFullPath(args[1])); }
            Arguments options = Arguments.Parse(args);
            return options.Workload == "fixed-inputs" ? await FixedInputProbe.Execute(options) : await Execute(options);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().FullName + ": experiment did not complete; inspect its evidence directory."); return 1; }
    }

    internal static async Task<int> Execute(Arguments options, RunnerFixture? fixture = null) {
        if (fixture is not null && options.Mode != "fixture") throw new ArgumentException("Injected factories require explicit offline fixture mode.");
        if (Directory.Exists(options.Output) && Directory.EnumerateFileSystemEntries(options.Output).Any()) {
            throw new IOException("Use a fresh output directory.");
        }
        Directory.CreateDirectory(options.Output);
        using var evidence = new ProbeEvidence(Path.Combine(options.Output, "timeline.jsonl"));
        using OnlineBudget? budget = options.Mode == "online" || fixture is not null
            ? new(fixture?.GrantPath ?? options.BudgetGrant!, options.Strategy) : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(budget?.Grant.TimeoutSeconds ?? 30));
        using var dispatcher = new ProviderDispatcher(evidence, budget, budget?.Grant.MaxInFlight ?? 2);
        LlmConnectionPool? pool = null;
        PlaygroundDurableHistory? history = null;
        FeedbackSession? feedback = null;
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var gates = new Dictionary<string, DemandGate>(StringComparer.Ordinal);
        var facts = new List<JsonElement>();
        var actualInputs = new List<string>();
        var boundaries = new List<PlaygroundDecisionBoundary>();
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        var cleanupErrors = new List<string>();
        var poolCleanupCauses = new List<string>();
        Exception? primary = null;
        double? workCompletedMilliseconds = null;
        double drainedAtMilliseconds;
        string? finalMaterial = null;
        string store = Path.Combine(options.Output, "store");
        try {
            var bindings = new Dictionary<string, LlmDecisionBinding>(StringComparer.Ordinal);
            string configurationHash;
            if (options.Mode == "online" || fixture is not null) {
                string configurationPath = fixture?.Connections ?? options.Connections!;
                LlmConnectionConfiguration configuration = LlmConnectionConfiguration.Load(configurationPath);
                configurationHash = ProbeEvidence.Hash(File.ReadAllText(configurationPath));
                pool = new(configuration, fixture?.Factory);
                foreach (string actor in new[] { "a", "b" }) {
                    bindings.Add(actor, await pool.GetAsync(fixture is null ? configuration.DefaultConnectionId : actor, deadline.Token));
                }
            } else {
                configurationHash = ProbeEvidence.Hash($"fixture-v1-input-keyed-delay-{options.DelayMilliseconds}");
                foreach (string actor in new[] { "a", "b" }) {
                    bindings.Add(actor, new(new FixtureClient(actor, options.DelayMilliseconds), "fixture-model", "fixture"));
                }
            }
            var identities = bindings.ToDictionary(pair => pair.Key,
                pair => new ConnectionIdentity(pair.Value.ConnectionId!, pair.Value.Client.ApiSpecId, pair.Value.ModelId, configurationHash), StringComparer.Ordinal);
            var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false, characterCount: 2);
            history = PlaygroundDurableHistory.Create(store, initial.World, initial.Cursor);
            string sourceCommit = PlaygroundDurableHistory.FormatCommitId(history.CompletedStateCommitId);
            string sourceBranch = PlaygroundDurableHistory.FormatBranchId(history.BranchId);
            File.WriteAllText(Path.Combine(options.Output, "manifest.json"), JsonSerializer.Serialize(new {
                Version = 1, options.Mode, options.Strategy, options.DelayMilliseconds, options.MaxTransitions,
                Workload = "first-successful-operation-per-actor-a-b", Actors = new[] { "a", "b" },
                ExpectedFixtureCommand = "handoff fixture-<actor>", OnlineTaskIsGuidanceNotEnforcement = true,
                SourceStateCommitId = sourceCommit, SourceBranchId = sourceBranch,
                Rules = "unmodified-survival-no-needs-no-weather-two-colocated-actors",
                CompletionInvocationOptions = "default; connection settings applied by existing factory; no output-token-cap",
                Connections = identities, Budget = budget?.Grant,
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Sources = SourceStamps(), Assemblies = new[] { typeof(Program).Assembly, typeof(PlaygroundEngine).Assembly, typeof(LlmCharacterController).Assembly,
                    typeof(CompletionRequest).Assembly }.Select(assembly => new { assembly.FullName, Sha256 = ProbeEvidence.FileHash(assembly.Location) }).ToArray()
            }, ProbeEvidence.Options));
            feedback = new(Path.Combine(options.Output, "feedback.jsonl"), history, "parallelism-experiment", store);
            evidence.Write("source-state", new { SourceStateCommitId = sourceCommit, SourceBranchId = sourceBranch, history.Cursor });
            if (options.Strategy == "candidate") {
                if (PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) is null || history.PendingEvent is not null) {
                    throw new InvalidOperationException("Initial source is not a decision frontier.");
                }
                PlaygroundWorld source = history.State;
                KernelCursor sourceCursor = history.Cursor;
                foreach (string actor in new[] { "a", "b" }) {
                    PlaygroundDecisionContext projected = evidence.Measure("predicted-context", () => Project(source, actor));
                    candidates.Add(actor, new(projected, sourceCommit, sourceBranch, bindings[actor], identities[actor], dispatcher, evidence, deadline.Token));
                }
                await Task.WhenAll(candidates.Values.Select(candidate => candidate.Captured.Task)).WaitAsync(deadline.Token);
                if (!ReferenceEquals(source, history.State) || sourceCursor != history.Cursor ||
                    history.State.Business.InteractionStates.Values.Any(state => state.CurrentContext is not null)) {
                    throw new InvalidOperationException("Scratch preparation changed the real source.");
                }
                evidence.Write("source-preserved-after-preview", new { SourceStateCommitId = sourceCommit, history.Cursor });
            }
            var controllers = new Dictionary<string, LlmCharacterController>(StringComparer.Ordinal);
            foreach (string actor in new[] { "a", "b" }) {
                candidates.TryGetValue(actor, out Candidate? candidate);
                var gate = new DemandGate(actor, bindings[actor], identities[actor], candidate, dispatcher, evidence, actualInputs);
                gates.Add(actor, gate);
                controllers.Add(actor, ControllerFactory.Create(gate, bindings[actor], feedback.SaveLlm, evidence));
            }
            var actual = new ActualController(history, controllers, gates, boundaries, evidence, options.Mode == "fixture");
            var recording = new RecordingHistory(history, evidence, facts, accepted);
            var kernel = PlaygroundEngine.CreateKernel(recording, characterController: actual);
            int transitions = 0;
            while (accepted.Count < 2 && transitions++ < options.MaxTransitions) {
                StepStatus status = await kernel.StepAsync(new ModelTime(long.MaxValue), deadline.Token);
                recording.FlushCommitted();
                if (status != StepStatus.Committed) { throw new InvalidOperationException("Kernel stopped before the declared workload completed."); }
            }
            if (accepted.Count != 2) { throw new InvalidOperationException("Transition limit reached before both actors had successful operations."); }
            workCompletedMilliseconds = evidence.ElapsedMilliseconds;
            finalMaterial = string.Join("\n", history.State.Business.InteractionStates.OrderBy(pair => pair.Key)
                .Select(pair => CharacterContextRenderer.RenderFull(pair.Value, new DecisionPlayLayout())));
            File.WriteAllText(Path.Combine(options.Output, "final-material.txt"), finalMaterial);
            evidence.Write("workload-completed", new { AcceptedActors = accepted.Order().ToArray(), history.Cursor, workCompletedMilliseconds });
        } catch (Exception error) {
            primary = error;
            try { evidence.Write("workload-failed", new { ErrorType = error.GetType().FullName, Reason = (error as LlmControllerPausedException)?.ReasonCode }); }
            catch (Exception diagnosticError) { cleanupErrors.Add(diagnosticError.GetType().FullName!); }
        } finally {
            foreach (Candidate candidate in candidates.Values) {
                try { await candidate.DisposeAsync(); } catch (Exception error) { cleanupErrors.Add(error.GetType().FullName!); }
            }
            try { feedback?.Dispose(); } catch (Exception error) { cleanupErrors.Add(error.GetType().FullName!); }
            try { history?.Dispose(); } catch (Exception error) { cleanupErrors.Add(error.GetType().FullName!); }
            if (pool is not null) {
                try { await pool.DisposeAsync(); } catch (Exception error) {
                    cleanupErrors.Add(error.GetType().FullName!);
                    poolCleanupCauses.AddRange((error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : new[] { error }.AsEnumerable())
                        .Select(cause => cause.GetType().FullName!));
                }
            }
        }
        drainedAtMilliseconds = evidence.ElapsedMilliseconds;
        if (primary is null && cleanupErrors.Count == 0) {
            try {
                foreach (PlaygroundDecisionBoundary boundary in boundaries) {
                    if (boundary != PlaygroundDurableHistory.InspectDecisionBoundary(store, boundary.BranchId, boundary.StateCommitId, boundary.ActorId)) {
                        throw new InvalidDataException("Cold boundary verification failed.");
                    }
                }
                using var cold = PlaygroundDurableHistory.Open(store);
                if (cold.PendingEvent is not null) { throw new InvalidDataException("Successful run left a pending Event."); }
                string reopened = string.Join("\n", cold.State.Business.InteractionStates.OrderBy(pair => pair.Key)
                    .Select(pair => CharacterContextRenderer.RenderFull(pair.Value, new DecisionPlayLayout())));
                if (reopened != finalMaterial) { throw new InvalidDataException("Cold state material differs."); }
                evidence.Write("cold-state-verified", new { cold.Cursor, Boundaries = boundaries.Count });
            } catch (Exception error) { primary = error; }
        }
        string outcome = primary is null && cleanupErrors.Count == 0 ? "completed" : "failed";
        try { budget?.FinishRun(outcome); }
        catch (Exception diagnosticError) { cleanupErrors.Add(diagnosticError.GetType().FullName!); outcome = "failed"; }
        // This is a research comparison artifact, not a second world recovery log.
        File.WriteAllText(Path.Combine(options.Output, "semantic-trace.json"), JsonSerializer.Serialize(new {
            Inputs = actualInputs, Facts = facts,
            FinalMaterial = finalMaterial
        }, ProbeEvidence.Options));
        ProbeEvidence.SaveSummary(options.Output, new {
            Outcome = outcome, options.Mode, options.Strategy, dispatcher.Calls, dispatcher.PeakInFlight,
            Adopted = gates.Values.Sum(gate => gate.Adopted), Discarded = gates.Values.Sum(gate => gate.Discarded),
            WorkCompletedMilliseconds = workCompletedMilliseconds, DrainedAndDisposedMilliseconds = drainedAtMilliseconds,
            TotalBeforeSummaryMilliseconds = evidence.ElapsedMilliseconds,
            AcceptedActors = accepted.Order().ToArray(), PrimaryError = primary?.GetType().FullName, CleanupErrors = cleanupErrors,
            PoolCleanupCauses = poolCleanupCauses,
            OnlineTrajectoryEqualityClaimed = false, C6CompleteClaimed = false,
            SyntheticFactoryInjected = fixture is not null, Metrics = evidence.MetricsSnapshot()
        });
        Console.WriteLine(JsonSerializer.Serialize(new { outcome, dispatcher.Calls, dispatcher.PeakInFlight, Output = options.Output }));
        return outcome == "completed" ? 0 : 1;
    }

    private static PlaygroundDecisionContext Project(PlaygroundWorld source, string actor) {
        if (source.Business.InteractionStates[actor].CurrentContext is { Status: CharacterContextStatus.Open }) {
            return PlaygroundDecisionContext.Create(source, actor);
        }
        PlaygroundFact opened = PlaygroundInteractionRules.OpenContext(source, actor);
        var instant = new LogicalInstant(source.Time, 0); // Scratch-local only, never a real boundary.
        PlaygroundWorld scratch = PlaygroundEngine.Complete(source, instant, [opened], PlaygroundEngine.Fold(source, instant, opened));
        PlaygroundEngine.Validate(scratch, source.Time);
        return PlaygroundDecisionContext.Create(scratch, actor);
    }

    internal static object[] SourceStamps() {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) { current = current.Parent; }
        if (current is null) { throw new InvalidDataException("Cannot locate source root for provenance."); }
        string root = current.FullName;
        return new[] { root, Path.GetFullPath(Path.Combine(root, "../durable-graph")), Path.GetFullPath(Path.Combine(root, "../atelia-storage")),
            Path.GetFullPath(Path.Combine(root, "../atelia-completion")) }.Select(path => (object)new {
                Name = Path.GetFileName(path), Commit = Git(path, "rev-parse", "HEAD").Trim(),
                Status = Git(path, "status", "--porcelain"), PatchHash = ProbeEvidence.Hash(Git(path, "diff", "--binary", "HEAD")),
                ExperimentSourceFiles = path == root ? Directory.EnumerateFiles(Path.Combine(root, "experiments/PlaygroundParallelism"))
                    .Order(StringComparer.Ordinal).Select(file => new { Path = Path.GetRelativePath(root, file), Sha256 = ProbeEvidence.FileHash(file) }).ToArray() : null
            }).ToArray();
    }
    private static string Git(string root, params string[] arguments) {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start) ?? throw new IOException("Cannot start git provenance reader.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) { throw new IOException("Git provenance read failed."); }
        return output;
    }
}

// No CLI option can supply this. Self-check alone provides a non-network factory and synthetic ledger.
internal sealed record RunnerFixture(ICompletionClientFactory Factory, string Connections, string GrantPath);

internal sealed record Arguments(string Mode, string Strategy, string? Connections, string Output, string? BudgetGrant, int DelayMilliseconds, int MaxTransitions, string Workload) {
    public static Arguments Parse(string[] args) {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2) {
            if (i + 1 >= args.Length || !values.TryAdd(args[i], args[i + 1])) { throw new ArgumentException("Expected unique --name value arguments."); }
        }
        string[] known = ["--mode", "--strategy", "--connections", "--output", "--budget-grant", "--delay-ms", "--max-transitions", "--workload"];
        if (values.Keys.Except(known).Any()) { throw new ArgumentException("Unknown argument."); }
        string mode = values.GetValueOrDefault("--mode", "fixture");
        string strategy = values.GetValueOrDefault("--strategy", "serial");
        string? connections = values.GetValueOrDefault("--connections");
        string? grant = values.GetValueOrDefault("--budget-grant");
        string workload = values.GetValueOrDefault("--workload", "playground");
        int delay = int.Parse(values.GetValueOrDefault("--delay-ms", "250"));
        int max = int.Parse(values.GetValueOrDefault("--max-transitions", "24"));
        if (mode is not ("fixture" or "online") || strategy is not ("serial" or "candidate") || workload is not ("playground" or "fixed-inputs") || delay < 0 || max <= 0 ||
            mode == "online" && (connections is null || grant is null)) { throw new ArgumentException("Invalid experiment options."); }
        return new(mode, strategy, connections, Path.GetFullPath(values["--output"]), grant, delay, max, workload);
    }
}

internal sealed class FixtureClient(string actor, int delayMilliseconds) : ICompletionClient {
    public string Name => "input-keyed-fixture";
    public string ApiSpecId => "fixture-v1";
    public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default) {
        string hash = ProbeEvidence.Hash(ProbeEvidence.Request(request));
        await Task.Delay(delayMilliseconds, cancellationToken);
        var call = new RawToolCall("act", "fixture-call-" + hash[..16], JsonSerializer.Serialize(new { command = "handoff fixture-" + actor }));
        return new(new ActionMessage([new ActionBlock.ToolCall(call)]), new(Name, ApiSpecId, request.ModelId));
    }
}

internal sealed class ActualController(PlaygroundDurableHistory history, Dictionary<string, LlmCharacterController> controllers,
    Dictionary<string, DemandGate> gates, List<PlaygroundDecisionBoundary> boundaries, ProbeEvidence evidence, bool fixture) : IPlaygroundCharacterController {
    public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
        string actor = context.InteractionState.ActorId;
        PlaygroundDecisionBoundary boundary = history.CaptureDecisionBoundary(actor);
        boundaries.Add(boundary);
        gates[actor].Boundary = boundary;
        PlaygroundControllerOperation operation = await controllers[actor].ChooseCommandAsync(context, cancellationToken);
        string original = operation.OperationRecordId;
        if (fixture) { operation = operation with { OperationRecordId = $"fixture-operation-{actor}-{boundary.ContextSequence}-{boundary.OperationSequence}" }; }
        evidence.Write("actual-operation", new { boundary, OriginalHostOperationRecordId = original, Operation = operation });
        return operation;
    }
}

internal sealed class RecordingHistory(PlaygroundDurableHistory inner, ProbeEvidence evidence, List<JsonElement> facts,
    HashSet<string> accepted) : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
    private JsonElement? _prepared;
    private object? _committedDiagnostic;
    public PlaygroundWorld State => inner.State;
    public KernelCursor Cursor => inner.Cursor;
    public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
    public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
        JsonElement[] projected = evidence.Measure("fact-projection", () => occurrence.Facts.Select(ProbeEvidence.Fact).ToArray());
        JsonElement transition = JsonSerializer.SerializeToElement(new {
            occurrence.CauseKey, occurrence.TargetInstant,
            nextCursor = inner.Cursor.Advance(occurrence.CauseKey, occurrence.TargetInstant), Facts = projected
        }, ProbeEvidence.Options);
        evidence.Write("world-event-planned", new { occurrence.CauseKey, occurrence.TargetInstant, Facts = projected });
        evidence.Measure("durable-event-publication", () => inner.CommitEvent(occurrence));
        _prepared = transition;
    }
    public void CommitState(PlaygroundWorld nextState, KernelCursor nextCursor) {
        var occurrence = inner.PendingEvent!;
        JsonElement projected = _prepared ?? throw new InvalidOperationException("Missing prepared fact projection.");
        evidence.Measure("durable-state-publication", () => inner.CommitState(nextState, nextCursor));
        facts.Add(projected);
        foreach (PlaygroundFact fact in occurrence.Facts) {
            if (fact.OperationDisposition == PlaygroundOperationDisposition.Succeeded && fact.ActorId is not null) { accepted.Add(fact.ActorId); }
        }
        _committedDiagnostic = new { StateCommitId = PlaygroundDurableHistory.FormatCommitId(inner.CompletedStateCommitId), nextCursor };
        _prepared = null;
    }
    public void FlushCommitted() {
        // Called after Step returned successfully, outside the Kernel's publication failure scope.
        if (_committedDiagnostic is not null) { evidence.Write("world-state-committed", _committedDiagnostic); _committedDiagnostic = null; }
    }
}

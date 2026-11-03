using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterGym;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;

namespace DramaBoard.PlaygroundLookahead;

internal static class OnlineRunner {
    public static async Task<int> Run(string[] args, ICompletionClientFactory? fixture = null, Action<string>? budgetBeforeFlush = null) {
        var values = new Dictionary<string, string>();
        bool resume = args.Contains("--resume");
        for (int i = 0; i < args.Length; i++) {
            if (args[i] is "--online" or "--resume") { continue; }
            if (!new[] { "--output", "--home", "--connections", "--lookahead", "--max-calls", "--grant" }.Contains(args[i]) || i + 1 >= args.Length || !values.TryAdd(args[i], args[++i])) {
                throw new ArgumentException("Expected --online --output FRESH --home PATH --connections PATH --lookahead on|off --max-calls N --grant PATH [--resume].");
            }
        }
        string output = Path.GetFullPath(values["--output"]), home = Path.GetFullPath(values["--home"]);
        string connections = Path.GetFullPath(values["--connections"]), store = Path.Combine(home, "store");
        string strategy = values["--lookahead"];
        if (strategy is not "on" and not "off") { throw new ArgumentException("lookahead must be on or off."); }
        int maxCalls = int.Parse(values["--max-calls"]);
        if (Directory.Exists(output) || !File.Exists(connections) || maxCalls < 1 || resume != Directory.Exists(store)) { throw new ArgumentException("Require fresh output, existing connections, positive calls, and explicit resume for existing store."); }
        using var budget = new OnlineBudget(Path.GetFullPath(values["--grant"]), strategy, budgetBeforeFlush);
        if (budget.Grant.MaxCalls != maxCalls || budget.Grant.MaxInFlight != 2) { throw new InvalidDataException("Host/grant call allowance must agree and physical limit must be two."); }
        Directory.CreateDirectory(output); Directory.CreateDirectory(home);
        if (!resume) {
            string homeConfig = Path.Combine(home, "playground.json");
            if (File.Exists(homeConfig)) { throw new IOException("New run requires an unconfigured independent home."); }
            Program.Save(homeConfig, new { version = 1, store = "store", llmConnections = connections, lookahead = strategy });
            var normal = PlaygroundWorldDefinitions.LivingWorld(new ModelTime(10 * PlaygroundTime.Hour), characterCount: 2);
            var definition = normal.Change(initialCharacters: normal.InitialCharacters.Select((actor, i) => actor.Change(placeId: i == 0 ? "camp" : "cabin")));
            var initial = PlaygroundGameFactory.Create(definition);
            using var created = PlaygroundDurableHistory.Create(store, initial.World, initial.Cursor);
        }
        string initialJson, initialBusinessHash;
        using (var history = PlaygroundDurableHistory.Open(store)) {
            initialJson = JsonSerializer.Serialize(new { history.State.Business, history.Cursor }, Program.Json);
            initialBusinessHash = Program.Hash(JsonSerializer.Serialize(history.State.Business, Program.Json));
        }
        File.WriteAllText(Path.Combine(output, "initial.json"), initialJson);
        Program.Save(Path.Combine(output, "manifest.json"), new { ResearchId = budget.Grant.ResearchId, budget.Grant.BatchId,
            Strategy = strategy, maxCalls, resume, Home = home, Store = store, ConnectionsPath = connections,
            InitialSha256 = Program.Hash(initialJson), InitialBusinessSha256 = initialBusinessHash,
            SourceFiles = Directory.GetFiles("src/Playground.Cli", "*.cs").Order().Select(p => new { Path = p, Hash = Program.Hash(File.ReadAllText(p)) }).ToArray(),
            Linkage = "physical.jsonl RequestId joins ledger Detail.RequestId; PhysicalOrdinal equals the suffix N in raw lookahead physical-request PhysicalId <run>-physical-N. Both start at one per host run and provider is called synchronously in Invoke order; full input validates the join. Raw actual-dispatch links PhysicalId to logical request. No usage double counting.",
            Rules = "Ordinary living world rules, needs/weather/economy retained; only initial placements differ. All roles use configured default connection. No experiment prompt." });
        using var evidence = new PhysicalEvidence(Path.Combine(output, "physical.jsonl"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(budget.Grant.TimeoutSeconds));
        using var transcript = new StreamWriter(Path.Combine(output, "console.txt"));
        var watch = Stopwatch.StartNew();
        string outcome = "host-failed"; int exit = 1; Exception? primary = null;
        try {
            exit = await PlaygroundHost.RunAsync(store, new PlaygroundHostOptions(MaxCalls: maxCalls, ConnectionsPath: connections,
                HomePath: home, LogPath: Path.Combine(output, "raw.jsonl"), Lookahead: strategy == "on", MaxSteps: 1000),
                timeout.Token, outputWriter: transcript, clientFactory: new BudgetFactory(fixture ?? new DefaultCompletionClientFactory(), budget, evidence));
            outcome = "host-returned";
        } catch (Exception error) {
            outcome = error is OperationCanceledException ? "cancelled-remote-outcomes-see-ledger" : "host-failed";
            primary = error;
        }
        CompleteRun(budget, output, outcome, exit, watch.Elapsed.TotalMilliseconds, store, primary);
        return exit;
    }

    // Always attempt summary even when the ledger is retired; never retry an uncertain append.
    internal static void CompleteRun(OnlineBudget budget, string output, string outcome, int exit, double milliseconds, string store, Exception? primary) {
        var bookkeeping = new List<Exception>();
        try { budget.FinishRun(outcome); } catch (Exception error) { bookkeeping.Add(error); }
        try {
            Program.Save(Path.Combine(output, "summary.json"), new { Outcome = outcome, ExitCode = exit, budget.Calls,
                WallMilliseconds = milliseconds, Store = store,
                Events = Path.Combine(store, "host", "events.jsonl"), Feedback = Path.Combine(store, "feedback"),
                PrimaryFailureType = primary?.GetType().FullName,
                BookkeepingFailureTypes = bookkeeping.Select(error => error.GetType().FullName).ToArray(),
                LedgerRetired = budget.IsFaulted, OnlineTrajectoryEqualityClaimed = false, UnknownUsageIsNotZero = true });
        } catch (Exception error) { bookkeeping.Add(error); }
        ThrowPreserving(primary, bookkeeping);
    }
    internal static void ThrowPreserving(Exception? primary, List<Exception> bookkeeping) {
        if (bookkeeping.Count > 0) {
            IEnumerable<Exception> failures = primary is null ? bookkeeping : new[] { primary }.Concat(bookkeeping);
            throw new AggregateException("Primary outcome and bookkeeping failures; no automatic retry.", failures);
        }
        if (primary is not null) { ExceptionDispatchInfo.Capture(primary).Throw(); }
    }
}

internal sealed class PhysicalEvidence(string path) : IDisposable {
    private readonly StreamWriter _writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    private readonly object _gate = new();
    public void Write(string kind, object payload) { lock (_gate) { _writer.WriteLine(JsonSerializer.Serialize(new { Kind = kind, Utc = DateTimeOffset.UtcNow, Timestamp = Stopwatch.GetTimestamp(), Payload = payload }, Program.Json)); } }
    public void Dispose() => _writer.Dispose();
}

internal sealed class BudgetFactory(ICompletionClientFactory inner, OnlineBudget budget, PhysicalEvidence evidence) : ICompletionClientFactory {
    public ICompletionClient Create(CompletionConnectionConfig connection) {
        return new BudgetClient(inner.Create(connection), budget, evidence);
    }
}
internal sealed class BudgetClient(ICompletionClient inner, OnlineBudget budget, PhysicalEvidence evidence) : ICompletionClient, IDisposable, IAsyncDisposable {
    public string Name => inner.Name;
    public string ApiSpecId => inner.ApiSpecId;
    public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) =>
        StreamCompletionAsync(request, CompletionInvocationOptions.Default, observer, cancellationToken);
    public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionInvocationOptions options, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var input = new {
            request.ModelId, request.PromptPrefix.SystemPrompt,
            Contract = GymMessageProjection.Contract(request.PromptPrefix.OutputContract),
            Shared = request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
            Tail = request.TailMessages.Select(GymMessageProjection.Message).ToArray()
        };
        string requestJson = JsonSerializer.Serialize(input);
        string json = JsonSerializer.Serialize(new { Request = input, Options = options }, Program.Json);
        string id = budget.BeforeInvoke("see-full-request", Program.Hash(json));
        bool terminal = false;
        try {
            evidence.Write("request-invoked", new { RequestId = id, PhysicalOrdinal = int.Parse(id[(id.LastIndexOf('-') + 1)..]), InputSha256 = Program.Hash(json), RequestSha256 = Program.Hash(requestJson), Request = input, Options = options });
            CompletionResult result = await inner.StreamCompletionAsync(request, options, observer, cancellationToken);
            terminal = true;
            budget.Finished(id, "terminal-" + result.Termination.Kind, result.Usage);
            evidence.Write("request-finished", new { RequestId = id, Result = new { Message = GymMessageProjection.Message(result.Message), result.Invocation, result.Termination, result.Usage, result.Failure, result.Errors } });
            return result;
        } catch (Exception error) {
            if (!terminal) {
                var bookkeeping = new List<Exception>();
                try { budget.Finished(id, "remote-unknown", null); } catch (Exception failure) { bookkeeping.Add(failure); }
                try { evidence.Write("request-failed", new { RequestId = id, Outcome = "remote-unknown", Type = error.GetType().FullName,
                    Usage = (object?)null, BookkeepingFailureTypes = bookkeeping.Select(failure => failure.GetType().FullName).ToArray() }); }
                catch (Exception failure) { bookkeeping.Add(failure); }
                OnlineRunner.ThrowPreserving(error, bookkeeping);
            }
            throw;
        }
    }
    public async ValueTask DisposeAsync() {
        if (inner is IAsyncDisposable asyncDisposable) { await asyncDisposable.DisposeAsync(); }
        else { Dispose(); }
    }
    public void Dispose() { if (inner is IDisposable disposable) { disposable.Dispose(); } }
}

using System.Diagnostics;
using System.Text.Json;
using Atelia.Completion;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;

namespace DramaBoard.PlaygroundLookahead;

// One fresh durable production Host per invocation. The coordinator reserves batches separately.
internal static class RealPerformanceRunner {
    public static async Task<int> SelfCheck(string[] args) {
        string output = Path.GetFullPath(args.SkipWhile(a => a != "--output").Skip(1).First());
        if (Directory.Exists(output)) { throw new IOException("Require fresh output."); }
        Directory.CreateDirectory(output);
        string connections = Path.Combine(output, "connections.json");
        File.WriteAllText(connections, """
            {"version":1,"defaultConnectionId":"fixture","catalog":{"v":3,"connections":[{"id":"fixture","kind":"openai-responses","completionSurfaceId":"openai-responses","modelId":"fixture-model","baseAddress":"https://fixture.invalid/"}]}}
            """);
        foreach (string scenario in new[] { "llm", "human-script" }) {
            foreach (string strategy in new[] { "off", "on" }) {
                string name = scenario + "-" + strategy, ledger = Path.Combine(output, name + ".ledger.jsonl"), grantPath = Path.Combine(output, name + ".grant.json");
                var grant = new OnlineGrant("playground-speculative-lookahead", "offline-performance-" + name, ledger, strategy, 12, 2, 30);
                File.WriteAllText(ledger, JsonSerializer.Serialize(new { Kind = "batch-reserved", grant.BatchId, Grant = grant }) + "\n");
                Program.Save(grantPath, grant);
                using var timeline = new Timeline(Path.Combine(output, name + ".fixture.jsonl"));
                var client = new Fixture(false, false, 10, timeline);
                int exit = await Run(["--real-performance", "--output", Path.Combine(output, name), "--connections", connections,
                    "--grant", grantPath, "--scenario", scenario, "--operations", "4", "--think-ms", scenario == "human-script" ? "5000" : "30"], new OfflineFactory(client));
                using var work = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, name, "work.json")));
                if (exit != 0 || !work.RootElement.GetProperty("ReachedTarget").GetBoolean() || client.InFlight != 0) { throw new InvalidOperationException("Performance runner did not reach target/drain."); }
                if (scenario == "human-script" && strategy == "on") {
                    var physical = LlmRawAudit.Read(Path.Combine(output, name, "raw.jsonl")).Records
                        .Where(r => r.Kind == "lookahead-event" && r.Payload.GetProperty("Kind").GetString() == "lookahead-physical-request").First();
                    if (physical.Payload.GetProperty("Data").GetProperty("Detail").GetProperty("Purpose").GetString() != "candidate") {
                        throw new InvalidOperationException("Cold B was not prefetched during the first scripted Human window.");
                    }
                }
            }
        }
        Program.Save(Path.Combine(output, "summary.json"), new { Passed = true, Scope = "Four fresh durable production Host runs: llm/human-script x off/on, fixed committed target, fixture provider only; no network." });
        return 0;
    }

    public static async Task<int> Run(string[] args, ICompletionClientFactory? fixture = null) {
        var values = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--real-performance") { continue; }
            if (!new[] { "--output", "--connections", "--grant", "--scenario", "--operations", "--think-ms" }.Contains(args[i]) ||
                i + 1 >= args.Length || !values.TryAdd(args[i], args[++i])) { throw new ArgumentException("Invalid real-performance arguments."); }
        }
        string output = Path.GetFullPath(values["--output"]), connections = Path.GetFullPath(values["--connections"]);
        string scenario = values["--scenario"];
        if (scenario is not "llm" and not "human-script") { throw new ArgumentException("scenario must be llm or human-script."); }
        int target = int.Parse(values.GetValueOrDefault("--operations", "8"));
        int think = int.Parse(values.GetValueOrDefault("--think-ms", "5000"));
        if (Directory.Exists(output) || target < 1 || think < 0) { throw new ArgumentException("Require fresh output and valid target/wait."); }
        string grantPath = Path.GetFullPath(values["--grant"]);
        var grant = JsonSerializer.Deserialize<OnlineGrant>(File.ReadAllText(grantPath))!;
        using var budget = new OnlineBudget(grantPath, grant.Strategy);
        Directory.CreateDirectory(output);
        string home = Path.Combine(output, "home"), store = Path.Combine(home, "store");
        Directory.CreateDirectory(home);
        Program.Save(Path.Combine(home, "playground.json"), new { version = 1, store = "store", llmConnections = connections, lookahead = grant.Strategy });
        var normal = PlaygroundWorldDefinitions.LivingWorld(new ModelTime(10 * PlaygroundTime.Hour), characterCount: 2);
        var definition = normal.Change(initialCharacters: normal.InitialCharacters.Select((actor, i) => actor.Change(placeId: i == 0 ? "camp" : "cabin")));
        var initial = PlaygroundGameFactory.Create(definition);
        using (var created = PlaygroundDurableHistory.Create(store, initial.World, initial.Cursor)) { }
        string initialJson = JsonSerializer.Serialize(new { initial.World.Business, initial.Cursor }, Program.Json);
        File.WriteAllText(Path.Combine(output, "initial.json"), initialJson);
        Program.Save(Path.Combine(output, "manifest.json"), new { Scenario = scenario, TargetSuccessfulOperations = target, ThinkMilliseconds = think,
            InitialSha256 = Program.Hash(initialJson), InitialBusinessSha256 = Program.Hash(JsonSerializer.Serialize(initial.World.Business, Program.Json)),
            Grant = grant, ConnectionsPath = connections, ConnectionsSha256 = Program.Hash(File.ReadAllText(connections)), Source = Program.Provenance(),
            Runtime = Environment.Version.ToString(), StopwatchFrequency = Stopwatch.Frequency,
            HumanPolicy = "Script via production ConsoleCharacterController.ReadLineAsync; fixed wait then alternate wait 1 / continue on successive reads. Rejections retained. Not a real human playtest.",
            Rules = "Normal living-world needs/weather/economy; camp/cabin initial placement. No provider delay, prompt change or retry. Different natural trajectories; no causal same-trajectory speedup claim." });
        using var evidence = new PhysicalEvidence(Path.Combine(output, "physical.jsonl"));
        using var services = new PhysicalEvidence(Path.Combine(output, "services.jsonl"));
        using var lookahead = new PhysicalEvidence(Path.Combine(output, "lookahead.jsonl"));
        using var transcript = new StreamWriter(Path.Combine(output, "console.txt")) { AutoFlush = true };
        using var input = new ScriptInput(think, services);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(grant.TimeoutSeconds));
        PlaygroundHostJournal? journal = null;
        int Successful() => journal?.Records.Count(r => r.Kind == "operation-completed" &&
            r.Payload.GetProperty("Completed").GetProperty("Status").GetInt32() == 0 &&
            r.Payload.GetProperty("Completed").TryGetProperty("DomainEvent", out var domain) && domain.ValueKind == JsonValueKind.String) ?? 0;
        var controls = scenario == "human-script" ? CharacterControlConfiguration.Create(new Dictionary<string, CharacterControlBinding> { ["a"] = new CharacterControlBinding.HumanConsole() }) : CharacterControlConfiguration.Empty;
        string outcome = "host-failed"; int exit = 1; Exception? primary = null;
        int successful = 0;
        var watch = Stopwatch.StartNew();
        try {
            exit = await PlaygroundHost.RunAsync(store, new PlaygroundHostOptions(MaxCalls: grant.MaxCalls, ConnectionsPath: connections,
                HomePath: home, Controls: controls, LogPath: Path.Combine(output, "raw.jsonl"), Lookahead: grant.Strategy == "on", MaxSteps: 1000,
                StopRequested: () => { successful = Successful(); return successful >= target; },
                LookaheadEvidence: (kind, payload) => lookahead.Write(kind, payload),
                ServiceCompleted: (context, elapsed) => services.Write("actual-service-completed", new { Actor = context.InteractionState.ActorId,
                    context.Context.ContextSequence, context.Context.LastOperationSequence, ServiceMilliseconds = elapsed.TotalMilliseconds })),
                timeout.Token, outputWriter: transcript, inputReader: scenario == "human-script" ? input : null,
                clientFactory: new BudgetFactory(fixture ?? new DefaultCompletionClientFactory(), budget, evidence),
                journalFactory: path => journal = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(path!)));
            outcome = "host-returned";
        } catch (Exception error) { primary = error; outcome = error is OperationCanceledException ? "cancelled-remote-unknown" : "host-failed"; }
        watch.Stop();
        // Journal is disposed by Host; read final durable evidence rather than reusing its runtime view.
        try {
            var records = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(store)).Records;
            Program.Save(Path.Combine(output, "operations.json"), records.Where(r => r.Kind is "operation-received" or "operation-completed" or "feedback-saved").ToArray());
            using (var final = PlaygroundDurableHistory.Open(store)) {
                Program.Save(Path.Combine(output, "final.json"), new { final.State.Business, final.Cursor });
            }
            successful = records.Count(r => r.Kind == "operation-completed" && r.Payload.GetProperty("Completed").GetProperty("Status").GetInt32() == 0 &&
                r.Payload.GetProperty("Completed").TryGetProperty("DomainEvent", out var domain) && domain.ValueKind == JsonValueKind.String);
            Program.Save(Path.Combine(output, "work.json"), new { SuccessfulCommittedOperations = successful, Target = target, ReachedTarget = successful >= target,
                HostWallMilliseconds = watch.Elapsed.TotalMilliseconds, ScriptInputCount = input.Count, ThinkMilliseconds = think });
        } catch (Exception postprocessing) {
            primary = primary is null ? postprocessing : new AggregateException("Host and postprocessing failures; no automatic retry.", primary, postprocessing);
            outcome += ";postprocessing-failed";
        }
        OnlineRunner.CompleteRun(budget, output, outcome, exit, watch.Elapsed.TotalMilliseconds, store, primary);
        return exit;
    }
}

internal sealed class ScriptInput(int thinkMilliseconds, PhysicalEvidence evidence) : TextReader {
    public int Count { get; private set; }
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) {
        if (++Count > 32) { evidence.Write("human-script-eof", new { Count }); return null; }
        long start = Stopwatch.GetTimestamp();
        evidence.Write("human-input-wait-start", new { Count, ThinkMilliseconds = thinkMilliseconds });
        await Task.Delay(thinkMilliseconds, cancellationToken);
        // wait is legal at idle; continue also handles a still-running activity or incoming warning.
        string command = Count % 2 == 1 ? "wait 1" : "continue";
        evidence.Write("human-script-input", new { Count, Command = command, ElapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
        return command;
    }
}

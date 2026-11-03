using Atelia.Completion;
using Atelia.Completion.Abstractions;
using System.Text.Json;
namespace DramaBoard.PlaygroundLookahead;

internal static class OnlineSelfCheck {
    public static async Task<int> Run(string[] args) {
        string output = Path.GetFullPath(args.SkipWhile(a => a != "--output").Skip(1).First());
        if (Directory.Exists(output)) { throw new IOException("Require fresh output."); }
        Directory.CreateDirectory(output);
        foreach (string strategy in new[] { "on", "off" }) {
            string ledger = Path.Combine(output, strategy + ".jsonl"), grantPath = Path.Combine(output, strategy + ".grant.json");
            var grant = new OnlineGrant("playground-speculative-lookahead", "offline-" + strategy, ledger, strategy, 2, 2, 10);
            File.WriteAllText(ledger, JsonSerializer.Serialize(new { Kind = "batch-reserved", grant.BatchId, Grant = grant }) + "\n");
            File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            using (var budget = new OnlineBudget(grantPath, strategy)) {
                string a = budget.BeforeInvoke("a", "input-a"), b = budget.BeforeInvoke("b", "input-b");
                RequireRejected(() => budget.BeforeInvoke("c", "input-c"));
                budget.Finished(a, "remote-unknown", null);
                budget.Finished(b, "terminal-Failed", null);
                RequireRejected(() => budget.Finished(a, "duplicate", null));
                budget.FinishRun("offline-check");
            }
            RequireRejected(() => { using var reused = new OnlineBudget(grantPath, strategy); });
            var wrong = grant with { BatchId = "missing" };
            File.WriteAllText(grantPath, JsonSerializer.Serialize(wrong));
            RequireRejected(() => { using var invalid = new OnlineBudget(grantPath, strategy); });
            File.AppendAllText(ledger, "{");
            File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            RequireRejected(() => { using var tail = new OnlineBudget(grantPath, strategy); });
        }
        foreach (string strategy in new[] { "on", "off" }) {
            string root = Path.Combine(output, "host-" + strategy);
            Directory.CreateDirectory(root);
            string ledger = Path.Combine(root, "ledger.jsonl"), grantPath = Path.Combine(root, "grant.json"), config = Path.Combine(root, "connections.json");
            var grant = new OnlineGrant("playground-speculative-lookahead", "host-check-" + strategy, ledger, strategy, 2, 2, 30);
            File.WriteAllText(ledger, JsonSerializer.Serialize(new { Kind = "batch-reserved", grant.BatchId, Grant = grant }) + "\n");
            File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            File.WriteAllText(config, """
                {"version":1,"defaultConnectionId":"fixture","catalog":{"v":3,"connections":[{"id":"fixture","kind":"openai-responses","completionSurfaceId":"openai-responses","modelId":"fixture-model","baseAddress":"https://fixture.invalid/"}]}}
                """);
            using var timeline = new Timeline(Path.Combine(root, "fixture.jsonl"));
            var fixture = new Fixture(false, false, 20, timeline);
            await OnlineRunner.Run(["--online", "--output", Path.Combine(root, "run"), "--home", Path.Combine(root, "home"), "--connections", config, "--lookahead", strategy, "--max-calls", "2", "--grant", grantPath], new OfflineFactory(fixture));
            if (fixture.Calls != 2 || fixture.InFlight != 0) { throw new InvalidOperationException("Actual Host did not obey physical allowance/drain."); }
        }
        foreach (string fault in new[] { "run-finished", "request-finished" }) {
            string root = Path.Combine(output, "fault-" + fault);
            Directory.CreateDirectory(root);
            string ledger = Path.Combine(root, "ledger.jsonl"), grantPath = Path.Combine(root, "grant.json");
            var grant = new OnlineGrant("playground-speculative-lookahead", "fault-" + fault, ledger, "off", 2, 2, 10);
            File.WriteAllText(ledger, JsonSerializer.Serialize(new { Kind = "batch-reserved", grant.BatchId, Grant = grant }) + "\n");
            File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            var appendFailure = new IOException("Injected append failure.");
            var providerFailure = new InvalidOperationException("Injected provider failure.");
            using var budget = new OnlineBudget(grantPath, "off", kind => { if (kind == fault) { throw appendFailure; } });
            Exception? primary = null;
            if (fault == "request-finished") {
                using var evidence = new PhysicalEvidence(Path.Combine(root, "physical.jsonl"));
                using var client = new BudgetClient(new FailingClient(providerFailure), budget, evidence);
                try {
                    await client.StreamCompletionAsync(new CompletionRequest("fixture-model",
                        new CompletionPromptPrefix("fixture", CompletionOutputContract.ProviderDefault([]), []), []), null);
                } catch (AggregateException error) {
                    var failures = error.Flatten().InnerExceptions;
                    if (!failures.Contains(providerFailure) || !failures.Contains(appendFailure)) { throw; }
                    primary = error;
                }
                if (primary is null || budget.Calls != 1) { throw new InvalidOperationException("Provider failure was masked or retried."); }
                RequireRejected(() => budget.BeforeInvoke("a", "after-retirement"));
            }
            try {
                OnlineRunner.CompleteRun(budget, root, fault == "run-finished" ? "host-returned" : "host-failed", 0, 0, root, primary);
                throw new InvalidOperationException("Expected bookkeeping failure.");
            } catch (AggregateException error) {
                if (!error.Flatten().InnerExceptions.Contains(appendFailure)) { throw; }
                if (primary is not null && !error.Flatten().InnerExceptions.Contains(providerFailure)) { throw; }
            }
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "summary.json")));
            if (!summary.RootElement.GetProperty("LedgerRetired").GetBoolean() ||
                summary.RootElement.GetProperty("BookkeepingFailureTypes").GetArrayLength() != 1) {
                throw new InvalidOperationException("Best-effort summary lost bookkeeping failure.");
            }
        }
        Program.Save(Path.Combine(output, "summary.json"), new { Passed = true, Scope = "Offline ledger guards plus both strategies through real durable Host with decorated fixture factory: two physical calls, drained cleanup, no network. Includes single outcome, no reuse, invalid grant and unterminated tail." });
        return 0;
    }
    private static void RequireRejected(Action action) {
        try { action(); } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected guard rejection.");
    }
}

internal sealed class OfflineFactory(ICompletionClient client) : ICompletionClientFactory {
    public ICompletionClient Create(CompletionConnectionConfig connection) => client;
}



internal sealed class FailingClient(Exception failure) : ICompletionClient {
    public string Name => "offline-failure";
    public string ApiSpecId => "fixture";
    public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) => Task.FromException<CompletionResult>(failure);
}

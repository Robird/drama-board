using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.PlaygroundParallelism;

/// <summary>Offline deterministic I/O faults against the actual admission/dispatcher implementation.
/// Synthetic ledgers under this directory never authorize a real connection pool.</summary>
internal static class SelfCheck {
    public static async Task<int> Run(string output) {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) { throw new IOException("Use a fresh self-check directory."); }
        Directory.CreateDirectory(output);
        var cases = new List<object>();
        bool passed = true;
        foreach (string fault in new[] { "budget-invoked", "budget-finished", "evidence-finished" }) {
            string root = Path.Combine(output, fault);
            Directory.CreateDirectory(root);
            string ledger = Path.Combine(root, "synthetic-offline-ledger.jsonl");
            string grantPath = Path.Combine(root, "grant.json");
            var grant = new OnlineGrant("predictive-time-parallelism", "self-check-" + fault, ledger, "serial", 4, 2, 10);
            File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            File.WriteAllText(ledger, JsonSerializer.Serialize(new { Kind = "batch-reserved", grant.ResearchId, grant.BatchId, Grant = grant }) + "\n");
            using var budget = new OnlineBudget(grantPath, "serial", kind => {
                if (fault == "budget-invoked" && kind == "request-invoked" || fault == "budget-finished" && kind == "request-finished") {
                    throw new IOException("Injected offline ledger flush failure.");
                }
            });
            using var evidence = new ProbeEvidence(Path.Combine(root, "timeline.jsonl"), kind => {
                if (fault == "evidence-finished" && kind == "provider-finished") { throw new IOException("Injected offline evidence flush failure."); }
            });
            using var dispatcher = new ProviderDispatcher(evidence, budget, 2);
            var client = new FailingClient();
            var binding = new LlmDecisionBinding(client, "fixture-model", "synthetic-offline-connection");
            var identity = new ConnectionIdentity(binding.ConnectionId!, client.ApiSpecId, binding.ModelId, "synthetic");
            var request = new CompletionRequest(binding.ModelId,
                new("offline-self-check", new([], CompletionToolChoice.Auto), [new ObservationMessage("offline")]), []);
            Exception? primary = null;
            Exception? refusal = null;
            var cleanup = new List<string>();
            try { await dispatcher.Invoke("a", "self-check", binding, identity, request, default); }
            catch (Exception error) { primary = error; }
            int callsAfterFailure = client.Calls;
            try { await dispatcher.Invoke("b", "self-check-after-retirement", binding, identity, request, default); }
            catch (Exception error) { refusal = error; }
            try { budget.FinishRun("failed-offline-injection"); }
            catch (Exception error) { cleanup.Add(error.GetType().FullName!); }
            bool originalPreserved = fault == "budget-invoked" || primary is AggregateException aggregate && aggregate.InnerExceptions.Contains(client.Original);
            bool retired = budget.IsFaulted || evidence.IsFaulted;
            bool noNewPhysicalCalls = client.Calls == callsAfterFailure && callsAfterFailure == (fault == "budget-invoked" ? 0 : 1);
            bool casePassed = retired && noNewPhysicalCalls && originalPreserved && refusal is not null;
            var result = new {
                Fault = fault, Passed = casePassed, BudgetRetired = budget.IsFaulted, EvidenceRetired = evidence.IsFaulted,
                CallsAfterFailure = callsAfterFailure, CallsAfterRefusedAttempt = client.Calls, OriginalProviderErrorPreserved = originalPreserved,
                PrimaryError = primary?.GetType().FullName, RefusedError = refusal?.GetType().FullName, CleanupErrors = cleanup,
                ActualNetworkRequests = 0, SyntheticFixtureOnly = true
            };
            ProbeEvidence.SaveSummary(root, result);
            using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "summary.json")));
            casePassed &= summary.RootElement.GetProperty("Passed").GetBoolean();
            cases.Add(result);
            passed &= casePassed;
        }
        object admission = await CheckAdmission(Path.Combine(output, "admission"));
        ProbeEvidence.SaveSummary(output, new { Outcome = passed ? "passed" : "failed", Cases = cases, Admission = admission, ActualNetworkRequests = 0 });
        Console.WriteLine(JsonSerializer.Serialize(new { Outcome = passed ? "passed" : "failed", SelfCheck = output }));
        return passed ? 0 : 1;
    }

    private static async Task<object> CheckAdmission(string root) {
        Directory.CreateDirectory(root);
        string ledger = Path.Combine(root, "synthetic-offline-ledger.jsonl");
        string grantPath = Path.Combine(root, "grant.json");
        var grant = new OnlineGrant("predictive-time-parallelism", "self-check-admission", ledger, "serial", 2, 2, 10);
        File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
        File.WriteAllText(ledger, JsonSerializer.Serialize(new { Kind = "batch-reserved", grant.ResearchId, grant.BatchId, Grant = grant }) + "\n");
        var client = new FailingClient();
        var binding = new LlmDecisionBinding(client, "fixture-model", "synthetic-offline-connection");
        var identity = new ConnectionIdentity(binding.ConnectionId!, client.ApiSpecId, binding.ModelId, "synthetic");
        var request = new CompletionRequest(binding.ModelId,
            new("offline-budget-check", new([], CompletionToolChoice.Auto), [new ObservationMessage("offline")]), []);
        bool capRefused = false;
        using (var budget = new OnlineBudget(grantPath, "serial")) {
            using var evidence = new ProbeEvidence(Path.Combine(root, "timeline.jsonl"));
            using var dispatcher = new ProviderDispatcher(evidence, budget, 2);
            for (int i = 0; i < 2; i++) {
                try { await dispatcher.Invoke("a", "cap-check", binding, identity, request, default); }
                catch (Exception error) when (ReferenceEquals(error, client.Original)) { }
            }
            try { await dispatcher.Invoke("b", "over-cap", binding, identity, request, default); }
            catch (InvalidOperationException) { capRefused = true; }
            if (!capRefused || client.Calls != 2 || budget.Calls != 2) { throw new InvalidDataException("Failed requests did not consume the shared physical call cap."); }
            budget.FinishRun("failed-fixture-no-retry");
        }
        bool reuseRefused = false;
        try { using var reused = new OnlineBudget(grantPath, "serial"); }
        catch (InvalidDataException) { reuseRefused = true; }
        string truncated = Path.Combine(root, "synthetic-truncated.jsonl");
        File.WriteAllText(truncated, File.ReadAllText(ledger).TrimEnd('\n'));
        string truncatedGrant = Path.Combine(root, "truncated-grant.json");
        File.WriteAllText(truncatedGrant, JsonSerializer.Serialize(grant with { LedgerPath = truncated }));
        bool tailRefused = false;
        try { using var corrupt = new OnlineBudget(truncatedGrant, "serial"); }
        catch (InvalidDataException) { tailRefused = true; }
        string missingGrant = Path.Combine(root, "missing-reservation-grant.json");
        File.WriteAllText(missingGrant, JsonSerializer.Serialize(grant with { BatchId = "never-reserved" }));
        bool missingRefused = false;
        try { using var missing = new OnlineBudget(missingGrant, "serial"); }
        catch (InvalidDataException) { missingRefused = true; }
        if (!reuseRefused || !tailRefused || !missingRefused) { throw new InvalidDataException("Ledger admission failed closed incorrectly."); }
        return new { FailedRequestsConsumeCap = capRefused, ReusedBatchRefused = reuseRefused,
            UnterminatedTailRefused = tailRefused, UnreservedBatchRefused = missingRefused, ActualNetworkRequests = 0 };
    }

    private sealed class FailingClient : ICompletionClient {
        public Exception Original { get; } = new InvalidDataException("Original offline provider failure.");
        public int Calls { get; private set; }
        public string Name => "offline-self-check";
        public string ApiSpecId => "offline-self-check";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromException<CompletionResult>(Original);
        }
    }
}

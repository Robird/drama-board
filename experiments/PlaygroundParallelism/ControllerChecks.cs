using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;

namespace DramaBoard.PlaygroundParallelism;

/// <summary>Actual runner/pool/controllers with exclusively injected offline clients.
/// Synthetic grants here exercise shared accounting; they authorize no real endpoint.</summary>
internal static class ControllerChecks {
    public static async Task<object> Run(string output) {
        Directory.CreateDirectory(output);
        var results = new List<object>();
        foreach (string kind in new[] { "invalid-text", "invalid-parameters", "feedback", "provider-and-dispose", "dispose-only", "stale-provider" }) {
            string root = Path.Combine(output, kind);
            Directory.CreateDirectory(root);
            string run = Path.Combine(root, "run");
            string strategy = kind == "provider-and-dispose" ? "serial" : "candidate";
            string ledger = Path.Combine(root, "synthetic-offline-ledger.jsonl");
            string grantPath = Path.Combine(root, "synthetic-grant.json");
            var grant = new OnlineGrant("predictive-time-parallelism", "offline-controller-" + kind, ledger, strategy, 8, 2, 15);
            File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            File.WriteAllText(ledger, JsonSerializer.Serialize(new { Kind = "batch-reserved", grant.ResearchId, grant.BatchId, Grant = grant }) + "\n");
            string configuration = Path.Combine(root, "synthetic-connections.json");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new {
                version = 1, defaultConnectionId = "a", catalog = new { v = 3, connections = new[] { "a", "b" }.Select(actor => new {
                    id = actor, kind = "openai-responses", modelId = "fixture-" + actor,
                    completionSurfaceId = "openai-responses", baseAddress = "https://fixture.invalid/"
                }).ToArray() }
            }));
            var bInitialCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool feedbackInitiallyUnsaved = false, feedbackReceiptObserved = false;
            var factory = new Factory((actor, request, token) => {
                token.ThrowIfCancellationRequested();
                string input = ProbeEvidence.Request(request);
                string hash = ProbeEvidence.Hash(input);
                bool continuation = request.TailMessages.OfType<ToolResultsMessage>().Any();
                // This runner always installs feedbackSink: protocol corrections are Observation
                // messages in Tail (LlmDecisionLoop). Sink-free controllers instead use Shared.
                bool correction = request.TailMessages.OfType<ObservationMessage>()
                    .Concat(request.PromptPrefix.SharedContextMessages.Skip(1).OfType<ObservationMessage>())
                    .Any(message => message.Content?.Contains("协议错误：", StringComparison.Ordinal) == true);
                string id = "offline-" + hash[..16];
                if (kind == "provider-and-dispose") throw new FixtureProviderException();
                if (kind == "stale-provider" && actor == "b" && !input.Contains("new-information-for-b", StringComparison.Ordinal)) {
                    bInitialCalled.TrySetResult();
                    throw new FixtureProviderException();
                }
                if (actor == "a" && !correction && !continuation) {
                    if (kind == "invalid-text") return Task.FromResult(Result(new ActionBlock.Text("handoff illegal-plain-text"), request));
                    if (kind == "invalid-parameters") return Task.FromResult(Result(new ActionBlock.ToolCall(new("act", id, "{\"command\":1}")), request));
                    if (kind == "feedback") {
                        string feedbackPath = Path.Combine(run, "feedback.jsonl");
                        feedbackInitiallyUnsaved = FeedbackJournal.Read(feedbackPath).Count == 0;
                        Require(feedbackInitiallyUnsaved, "Feedback was saved before candidate adoption.");
                        return Task.FromResult(Result(new ActionBlock.ToolCall(new("feedback", id, "{\"text\":\"offline-adopted-feedback\"}")), request));
                    }
                }
                if (actor == "a" && kind is "invalid-text" or "invalid-parameters") {
                    Require(correction && !continuation, "Protocol correction must be an Observation, not a fabricated ToolResults receipt.");
                    Require(request.TailMessages.OfType<ObservationMessage>().Any()
                        && request.PromptPrefix.SharedContextMessages.Length == 1,
                        "The actual feedback-enabled runner must retain protocol correction in Tail.");
                    string expected = kind == "invalid-text" ? "expected-single-call" : "expected-command-string";
                    Require(input.Contains(expected, StringComparison.Ordinal), "Real correction code is missing.");
                }
                if (actor == "a" && kind == "feedback") {
                    Require(continuation, "Feedback did not continue with the real Tail.");
                    var receipts = request.TailMessages.OfType<ToolResultsMessage>().SelectMany(message => message.Results).ToArray();
                    Require(receipts.Length == 1, "Feedback continuation requires exactly one saved receipt.");
                    var saved = FeedbackJournal.Read(Path.Combine(run, "feedback.jsonl"));
                    feedbackReceiptObserved = saved.Count == 1 && saved[0].Text == "offline-adopted-feedback"
                        && saved[0].ActorId == "a" && saved[0].Boundary is not null
                        && receipts[0].GetFlattenedText().Contains(saved[0].FeedbackId, StringComparison.Ordinal)
                        && receipts[0].ToolCallId == saved[0].Model!.ToolCallId;
                    Require(feedbackReceiptObserved, "Receipt arrived before actual feedback persistence.");
                }
                if (actor == "a" && kind == "stale-provider") return AfterB();
                return Task.FromResult(Command("handoff checked-" + actor, id, request));
                async Task<CompletionResult> AfterB() {
                    await bInitialCalled.Task.WaitAsync(token);
                    return Command("say b new-information-for-b", id, request);
                }
            }, throwDispose: kind is "provider-and-dispose" or "dispose-only");
            int exit = await Program.Execute(new("fixture", strategy, null, run, null, 0, 24, "playground"), new(factory, configuration, grantPath));
            using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "summary.json")));
            JsonElement value = summary.RootElement;
            bool failed = kind == "provider-and-dispose";
            bool cleanupOnly = kind == "dispose-only";
            int expectedCalls = failed ? 1 : cleanupOnly ? 2 : 3;
            Require(exit == (failed || cleanupOnly ? 1 : 0), "Runner returned an unexpected status.");
            Require(value.GetProperty("Calls").GetInt32() == expectedCalls && factory.Clients.Sum(client => client.Requests.Count) == expectedCalls,
                "Correction/feedback/failure calls escaped the common dispatcher or were retried.");
            int charged = File.ReadLines(ledger).Count(line => {
                using JsonDocument record = JsonDocument.Parse(line);
                return record.RootElement.GetProperty("Kind").GetString() == "request-invoked";
            });
            Require(charged == expectedCalls, "Synthetic shared ledger disagrees with physical fake-provider calls.");
            if (kind is "invalid-text" or "invalid-parameters" or "feedback") {
                var requests = factory.Clients.SelectMany(client => client.Requests).Where(request => request.ModelId == "fixture-a").ToArray();
                Require(requests.Length == 2 && ProbeEvidence.Request(requests[0]) != ProbeEvidence.Request(requests[1]),
                    "Correction/receipt continuation must change the full effective input.");
                File.WriteAllText(Path.Combine(root, "full-input-difference.json"), JsonSerializer.Serialize(new {
                    Initial = ProbeEvidence.Request(requests[0]), Continuation = ProbeEvidence.Request(requests[1]),
                    InitialHash = ProbeEvidence.Hash(ProbeEvidence.Request(requests[0])), ContinuationHash = ProbeEvidence.Hash(ProbeEvidence.Request(requests[1]))
                }, ProbeEvidence.Options));
            }
            Require(factory.Clients.Count == 2 && factory.Clients.All(client => client.Disposed == 1), "Actual pool did not dispose both owned clients exactly once.");
            if (failed || cleanupOnly) Require(value.GetProperty("PoolCleanupCauses").EnumerateArray()
                .Count(error => error.GetString() == typeof(FixtureDisposeException).FullName) == 2,
                "Summary did not retain each actual client disposal failure cause.");
            using var cold = PlaygroundDurableHistory.Open(Path.Combine(run, "store"));
            Require(cold.PendingEvent is null, "Runner left a pending event.");
            if (failed) {
                Require(value.GetProperty("PrimaryError").GetString() == typeof(FixtureProviderException).FullName,
                    "Provider primary error was replaced by cleanup failure.");
                Require(value.GetProperty("CleanupErrors").EnumerateArray().Any(error => error.GetString() == typeof(AggregateException).FullName),
                    "Pool disposal failure was not separately retained.");
                Require(!value.GetProperty("AcceptedActors").EnumerateArray().Any(), "A provider failure produced a successful operation.");
                Require(cold.State.Business.InteractionStates["a"].CurrentContext!.Items.Count == 0, "A failed response entered actual character history.");
            } else {
                Require(value.GetProperty("PrimaryError").ValueKind == JsonValueKind.Null, "Successful workload retained a primary error.");
                if (cleanupOnly) {
                    Require(value.GetProperty("Outcome").GetString() == "failed"
                        && value.GetProperty("CleanupErrors").EnumerateArray().Any(error => error.GetString() == typeof(AggregateException).FullName),
                        "Cleanup-only failure must fail the outcome while preserving successful world commits.");
                }
                Require(value.GetProperty("AcceptedActors").GetArrayLength() == 2, "Both real actors must successfully commit.");
                string material = File.ReadAllText(Path.Combine(run, "final-material.txt"));
                Require(material.Contains("checked-b", StringComparison.Ordinal), "Fresh b operation was not committed.");
                Require(!material.Contains("illegal-plain-text", StringComparison.Ordinal), "Invalid cached response entered world history.");
                Require(!material.Contains("offline-adopted-feedback", StringComparison.Ordinal), "Feedback was exposed as world experience.");
                if (kind == "stale-provider") {
                    Require(value.GetProperty("Discarded").GetInt32() == 1 && value.GetProperty("Adopted").GetInt32() == 1,
                        "Stale b error was not discarded before fresh demand.");
                } else Require(value.GetProperty("Adopted").GetInt32() == 2, "Cached initial requests were not actually adopted.");
                if (kind == "feedback") Require(feedbackInitiallyUnsaved && feedbackReceiptObserved, "Feedback adoption/receipt checks were not exercised.");
            }
            object result = new { Case = kind, Passed = true, Calls = expectedCalls, SharedLedgerCalls = charged,
                FeedbackInitiallyUnsaved = feedbackInitiallyUnsaved, FeedbackReceiptObserved = feedbackReceiptObserved,
                PrimaryError = value.GetProperty("PrimaryError").Clone(), CleanupErrors = value.GetProperty("CleanupErrors").Clone(),
                RealRunnerAndPool = true, ActualNetworkRequests = 0, SyntheticFactoryOnly = true };
            ProbeEvidence.SaveSummary(root, result);
            results.Add(result);
        }
        return new { Passed = true, Cases = results, ActualNetworkRequests = 0 };
    }

    private static CompletionResult Command(string command, string id, CompletionRequest request) =>
        Result(new ActionBlock.ToolCall(new("act", id, JsonSerializer.Serialize(new { command }))), request);
    private static CompletionResult Result(ActionBlock block, CompletionRequest request) =>
        new(new ActionMessage([block]), new("offline-controller-check", "fixture-v1", request.ModelId));
    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidDataException(message);
    }
    private sealed class FixtureProviderException : Exception { }
    private sealed class FixtureDisposeException : Exception { }
    private sealed class Factory(Func<string, CompletionRequest, CancellationToken, Task<CompletionResult>> respond, bool throwDispose) : ICompletionClientFactory {
        public List<Client> Clients { get; } = [];
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            var client = new Client(connection.Id, respond, throwDispose);
            Clients.Add(client);
            return client;
        }
    }
    private sealed class Client(string actor, Func<string, CompletionRequest, CancellationToken, Task<CompletionResult>> respond, bool throwDispose)
        : ICompletionClient, IDisposable {
        public List<CompletionRequest> Requests { get; } = [];
        public int Disposed { get; private set; }
        public string Name => "offline-controller-check";
        public string ApiSpecId => "fixture-v1";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return respond(actor, request, cancellationToken);
        }
        public void Dispose() {
            Disposed++;
            if (throwDispose) throw new FixtureDisposeException();
        }
    }
}

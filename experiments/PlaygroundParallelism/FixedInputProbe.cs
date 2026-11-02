using System.Runtime.InteropServices;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.PlaygroundParallelism;

/// <summary>Endpoint capacity probe with fixed short inputs. No game, controller tools or feedback
/// execute here. This does not establish equality between random online response trajectories.</summary>
internal static class FixedInputProbe {
    public static async Task<int> Execute(Arguments options) {
        if (Directory.Exists(options.Output) && Directory.EnumerateFileSystemEntries(options.Output).Any()) {
            throw new IOException("Use a fresh fixed-input probe directory.");
        }
        Directory.CreateDirectory(options.Output);
        using var evidence = new ProbeEvidence(Path.Combine(options.Output, "timeline.jsonl"));
        using OnlineBudget? budget = options.Mode == "online" ? new(options.BudgetGrant!, options.Strategy) : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(budget?.Grant.TimeoutSeconds ?? 30));
        using var dispatcher = new ProviderDispatcher(evidence, budget, budget?.Grant.MaxInFlight ?? 2);
        LlmConnectionPool? pool = null;
        Exception? primary = null;
        var cleanupErrors = new List<string>();
        double? workCompleted = null;
        double drainedAt;
        var results = new Dictionary<string, CompletionResult>(StringComparer.Ordinal);
        try {
            if (budget is not null && budget.Grant.MaxCalls != 2) { throw new InvalidDataException("Fixed-input online probe requires exactly two reserved calls."); }
            LlmDecisionBinding binding;
            string configurationHash;
            if (options.Mode == "online") {
                LlmConnectionConfiguration configuration = LlmConnectionConfiguration.Load(options.Connections!);
                configurationHash = ProbeEvidence.FileHash(options.Connections!);
                pool = new(configuration);
                binding = await pool.GetAsync(configuration.DefaultConnectionId, deadline.Token);
            } else {
                configurationHash = ProbeEvidence.Hash($"fixed-text-fixture-v1-delay-{options.DelayMilliseconds}");
                binding = new(new TextFixture(options.DelayMilliseconds), "fixture-model", "fixture-fixed-text");
            }
            var identity = new ConnectionIdentity(binding.ConnectionId!, binding.Client.ApiSpecId, binding.ModelId, configurationHash);
            var requests = new[] { "a", "b" }.ToDictionary(actor => actor, actor => new CompletionRequest(binding.ModelId,
                new("Reply with exactly OK. This is a short endpoint capacity probe; do not call tools.",
                    new([], CompletionToolChoice.None, allowParallelToolCalls: false),
                    [new ObservationMessage("Fixed probe actor: " + actor)]), []), StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(options.Output, "manifest.json"), JsonSerializer.Serialize(new {
                Version = 1, options.Mode, options.Strategy, Workload = "fixed-inputs", identity,
                CompletionInvocationOptions = "default; existing connection factory settings; no output-token-cap",
                Budget = budget?.Grant, options.DelayMilliseconds, Sources = Program.SourceStamps(),
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Inputs = requests.Select(pair => new { ActorId = pair.Key, InputHash = ProbeEvidence.Hash(ProbeEvidence.Request(pair.Value)), Input = ProbeEvidence.Request(pair.Value) }).ToArray(),
                GameWorldCreated = false, FeedbackEnabled = false, ExpectedOperations = 2
            }, ProbeEvidence.Options));
            async Task Call(string actor) {
                CompletionResult result = await dispatcher.Invoke(actor, "fixed-input-endpoint-probe", binding, identity, requests[actor], deadline.Token);
                lock (results) { results.Add(actor, result); }
            }
            if (options.Strategy == "serial") {
                foreach (string actor in new[] { "a", "b" }) { await Call(actor); }
            } else {
                // WhenAll observes both tasks, including a slower failure, before pool disposal.
                await Task.WhenAll(Call("a"), Call("b"));
            }
            if (results.Values.Any(result => result.Termination.Kind != CompletionTerminationKind.Completed)) {
                throw new InvalidDataException("Endpoint returned a non-Completed terminal; this probe cannot report successful responses.");
            }
            workCompleted = evidence.ElapsedMilliseconds;
            evidence.Write("fixed-input-work-completed", new { Calls = dispatcher.Calls, WorkCompletedMilliseconds = workCompleted });
        } catch (Exception error) {
            primary = error;
            try { evidence.Write("fixed-input-work-failed", new { ErrorType = error.GetType().FullName }); }
            catch (Exception diagnosticError) { cleanupErrors.Add(diagnosticError.GetType().FullName!); }
        } finally {
            if (pool is not null) {
                try { await pool.DisposeAsync(); } catch (Exception error) { cleanupErrors.Add(error.GetType().FullName!); }
            }
        }
        drainedAt = evidence.ElapsedMilliseconds;
        string outcome = primary is null && cleanupErrors.Count == 0 ? "completed" : "failed";
        try { budget?.FinishRun(outcome); }
        catch (Exception error) { cleanupErrors.Add(error.GetType().FullName!); outcome = "failed"; }
        ProbeEvidence.SaveSummary(options.Output, new {
            Outcome = outcome, options.Mode, options.Strategy, Workload = "fixed-inputs", dispatcher.Calls, dispatcher.PeakInFlight,
            WorkCompletedMilliseconds = workCompleted, DrainedAndDisposedMilliseconds = drainedAt,
            PrimaryError = primary?.GetType().FullName, CleanupErrors = cleanupErrors,
            Results = results.OrderBy(pair => pair.Key).Select(pair => new { ActorId = pair.Key, Result = ProbeEvidence.Result(pair.Value),
                ExpectedTextMatched = pair.Value.Message.GetFlattenedText().Trim() == "OK" }).ToArray(),
            ActualGameEvidence = false, OnlineTrajectoryEqualityClaimed = false, C6CompleteClaimed = false
        });
        Console.WriteLine(JsonSerializer.Serialize(new { outcome, Workload = "fixed-inputs", dispatcher.Calls, dispatcher.PeakInFlight, Output = options.Output }));
        return outcome == "completed" ? 0 : 1;
    }

    private sealed class TextFixture(int delayMilliseconds) : ICompletionClient {
        public string Name => "fixed-text-offline-fixture";
        public string ApiSpecId => "fixture-v1";
        public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            await Task.Delay(delayMilliseconds, cancellationToken);
            return new(new ActionMessage([new ActionBlock.Text("OK")]), new(Name, ApiSpecId, request.ModelId));
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace DramaBoard.Experiments.PredictiveTimeParallelism.Lifecycle;

public static class WakeLifecycle {
    private sealed record Wake(string Id, int Tick, int Generation);
    private sealed record Scheduled(int Tick, string Kind, Wake? Wake = null);
    public sealed record CallRecord(int Id, string Purpose, string Input, string Outcome, int? Command);
    public sealed record CandidateRecord(string WakeId, string State, string Reason);
    public sealed record Result(string Case, string Strategy, string CompletionOrder, bool CooperativeCancellation,
        string[] Events, CandidateRecord[] Candidates, CallRecord[] Calls, string[] Inputs, string[] Commits,
        int Cursor, int PendingWakes, int UnsettledCandidates, string InputHash, string FactHash);

    private sealed class World {
        public List<Scheduled> Queue = new();
        public List<string> Facts = new();
        public int Branch;
        public int Binding;
        public bool Alive = true;
        public int Cursor;
        public int Now;
        public void ReplaceWake(string id, int tick) {
            Queue.RemoveAll(e => e.Kind == "wake");
            Queue.Add(new(tick, "wake", new(id, tick, 1)));
        }
    }

    public static async Task<object> RunAsync() {
        string[] cases = ["advance", "delay", "merge", "cancel", "death", "cancel-and-rewake", "branch-cold-open", "binding-generation", "unchanged-control"];
        var results = new List<Result>();
        foreach (string name in cases) {
            foreach (bool before in new[] { true, false }) {
                foreach (bool cooperative in new[] { true, false }) {
                    var serial = await ExecuteAsync(name, false, before, cooperative);
                    var spec = await ExecuteAsync(name, true, before, cooperative);
                    if (serial.InputHash != spec.InputHash || serial.FactHash != spec.FactHash || serial.Cursor != spec.Cursor || !serial.Commits.SequenceEqual(spec.Commits))
                        throw new InvalidOperationException($"Lifecycle divergence: {name}/{before}/{cooperative}");
                    if (spec.UnsettledCandidates != 0 || spec.PendingWakes != 0) throw new InvalidOperationException("Unsettled lifecycle.");
                    bool adopted = spec.Candidates.Any(e => e.State == "adopted");
                    if (adopted != (name == "unchanged-control")) throw new InvalidOperationException("Stale candidate adoption or missed unchanged control.");
                    int expected = name is "cancel" or "death" ? 0 : 1;
                    if (spec.Cursor != expected || spec.Commits.Length != expected) throw new InvalidOperationException("Duplicate or unexpected wake operation.");
                    if (expected == 0 && spec.Calls.Any(c => c.Purpose == "demand")) throw new InvalidOperationException("Cancelled/dead actor caused demand.");
                    results.Add(serial);
                    results.Add(spec);
                }
            }
        }
        // Unknown mechanism means no prediction; the actual queue remains authority.
        bool unknownCanPredict = CanPredict(mechanismsKnown: false, alive: true);
        if (unknownCanPredict) throw new InvalidOperationException("Unknown mechanism accepted.");
        var unknownSerial = await ExecuteAsync("unknown", false, false, false);
        var unknownSpec = await ExecuteAsync("unknown", true, false, false);
        if (unknownSerial.InputHash != unknownSpec.InputHash || unknownSerial.FactHash != unknownSpec.FactHash ||
            unknownSpec.Candidates.Length != 0 || unknownSpec.Calls.Any(c => c.Purpose != "demand"))
            throw new InvalidOperationException("Unknown fallback changed queue semantics.");
        return new { Cases = cases, CompletionOrders = new[] { "before-invalidation", "after-invalidation" },
            CancellationModes = new[] { "cooperative", "ignores-cancellation" }, PairedTraceChecks = 36,
            UnknownFallsBackToDemand = !unknownCanPredict, UnknownRuns = new[] { unknownSerial, unknownSpec },
            Results = results, Failures = 0 };
    }

    private static bool CanPredict(bool mechanismsKnown, bool alive) => mechanismsKnown && alive;

    private static async Task<Result> ExecuteAsync(string name, bool speculative, bool before, bool cooperative) {
        var world = new World();
        var original = new Wake("wake-original", 5, 0);
        world.Queue.Add(new(5, "wake", original));
        world.Queue.Add(new(2, "mutation"));
        if (name == "merge") world.Queue.Add(new(7, "wake", new("wake-secondary", 7, 0)));
        var candidates = new List<CandidateRecord>();
        var calls = new ConcurrentDictionary<int, CallRecord>();
        var inputs = new List<string>();
        var commits = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string predictedInput = Input(world, original);
        Task<int>? response = null;
        bool eligible = false;
        int nextCall = 0;

        async Task<int> Invoke(string input, string purpose, bool controlled) {
            int id = ++nextCall;
            calls[id] = new(id, purpose, input, "in-flight", null);
            try {
                if (controlled) {
                    if (cooperative) await gate.Task.WaitAsync(cancellation.Token);
                    else await gate.Task;
                }
                int command = SHA256.HashData(Encoding.UTF8.GetBytes(input))[0] % 7 + 1;
                calls[id] = new(id, purpose, input, "completed", command);
                return command;
            } catch (OperationCanceledException) {
                calls[id] = new(id, purpose, input, "cancelled-remote-unknown", null);
                throw;
            }
        }

        if (speculative && CanPredict(name != "unknown", world.Alive)) {
            eligible = true;
            candidates.Add(new(original.Id, "invoked", "initial-first-unresolved-wake"));
            response = Invoke(predictedInput, "candidate", true);
            if (before) {
                gate.TrySetResult(true);
                await response;
                candidates.Add(new(original.Id, "ready", "provider-completed-before-mutation"));
            }
        }
        while (world.Queue.Count > 0) {
            var e = world.Queue.OrderBy(x => x.Tick).ThenBy(x => x.Kind == "wake" ? 1 : 0).ThenBy(x => x.Wake?.Id, StringComparer.Ordinal).First();
            world.Queue.Remove(e);
            world.Now = e.Tick;
            if (e.Kind != "wake") {
                world.Facts.Add($"{e.Tick}:{e.Kind}:{name}");
                if (e.Kind == "rewake") world.ReplaceWake("wake-reawakened", 6);
                else {
                    switch (name) {
                        case "advance": world.ReplaceWake("wake-advanced", 3); break;
                        case "delay": world.ReplaceWake("wake-delayed", 8); break;
                        case "merge": world.ReplaceWake("wake-merged", 4); break;
                        case "cancel": world.Queue.RemoveAll(x => x.Kind == "wake"); break;
                        case "death": world.Alive = false; world.Queue.RemoveAll(x => x.Kind == "wake"); break;
                        case "cancel-and-rewake": world.Queue.RemoveAll(x => x.Kind == "wake"); world.Queue.Add(new(3, "rewake")); break;
                        case "branch-cold-open":
                            // New branch reconstructs only durable world/queue; no candidate is restored.
                            world = new World {
                                Queue = JsonSerializer.Deserialize<List<Scheduled>>(JsonSerializer.Serialize(world.Queue))!,
                                Facts = new(world.Facts), Branch = world.Branch + 1, Binding = world.Binding,
                                Alive = world.Alive, Cursor = world.Cursor, Now = world.Now
                            };
                            break;
                        case "binding-generation": world.Binding++; break;
                    }
                }
                if (eligible && (name != "unchanged-control" || e.Kind == "rewake")) {
                    eligible = false;
                    candidates.Add(new(original.Id, "discarded", name == "branch-cold-open" ? "cold-open-clears-runtime-candidate" : "queue-or-identity-invalidated"));
                    cancellation.Cancel();
                }
                // Deliberately release after the invalidation, even if cancellation is ignored.
                gate.TrySetResult(true);
                continue;
            }
            var wake = e.Wake!;
            if (!world.Alive) throw new InvalidOperationException("Dead actor retained a wake.");
            string actual = Input(world, wake);
            int command;
            if (eligible && response is not null && wake == original && actual == predictedInput) {
                command = await response;
                eligible = false;
                candidates.Add(new(original.Id, "adopted", "actual-queue-winner-and-complete-input-match"));
            } else command = await Invoke(actual, "demand", false);
            inputs.Add(actual);
            world.Cursor++;
            string commit = $"{world.Now}:commit:{wake.Id}:command={command}:cursor={world.Cursor}:branch={world.Branch}:binding={world.Binding}";
            world.Facts.Add(commit);
            commits.Add(commit);
        }
        gate.TrySetResult(true);
        if (response is not null) {
            try { await response; } catch (OperationCanceledException) when (!eligible) { }
            candidates.Add(new(original.Id, "settled", "provider-task-observed-after-stop"));
        }
        if (eligible || calls.Values.Any(c => c.Outcome == "in-flight")) throw new InvalidOperationException("Candidate drain incomplete.");
        return new(name, speculative ? "bounded-speculation" : "serial", before ? "before-invalidation" : "after-invalidation", cooperative,
            world.Facts.ToArray(), candidates.ToArray(), calls.Values.OrderBy(c => c.Id).ToArray(), inputs.ToArray(), commits.ToArray(), world.Cursor,
            world.Queue.Count(x => x.Kind == "wake"), 0, Hash(inputs), Hash(world.Facts));
    }

    private static string Input(World world, Wake wake) => JsonSerializer.Serialize(new {
        Version = 1, Actor = "actor-a", Wake = wake.Id, WakeTick = wake.Tick, WakeGeneration = wake.Generation,
        BranchGeneration = world.Branch, BindingGeneration = world.Binding, world.Alive, world.Cursor,
        Tool = "commit(sha256(full-input)[0]%7+1)", Protocol = "lifecycle-fixture-v1"
    });

    private static string Hash(IEnumerable<string> value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

}

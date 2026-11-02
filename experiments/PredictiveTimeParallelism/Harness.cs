using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PredictiveTimeParallelism;

public static class JsonFormat {
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record Options(string Scenario = "independent", string Strategy = "speculative", string Backend = "independent",
    int Actors = 8, int Rounds = 2, int Seed = 17, int LatencyMs = 20, int Slots = 2, int Budget = 4,
    int Repeats = 5, bool SelfCheck = false, bool Matrix = false, string? Output = null, int TotalCallLimit = 1024, bool InfluenceCheck = false,
    string CancelMode = "cooperative", string FaultMode = "none", bool ModelTimeCheck = false, bool LifecycleCheck = false, bool ScaleCheck = false) {
    public static readonly string[] Scenarios = ["independent", "shared-resource", "scarce-resource", "new-message", "zero-chain", "delayed-message", "future-time"];
    public static Options Parse(string[] args) {
        var o = new Options();
        for (int i = 0; i < args.Length; i++) {
            var key = args[i];
            if (key == "--self-check") { o = o with { SelfCheck = true }; continue; }
            if (key == "--influence-check") { o = o with { InfluenceCheck = true }; continue; }
            if (key == "--model-time-check") { o = o with { ModelTimeCheck = true }; continue; }
            if (key == "--lifecycle-check") { o = o with { LifecycleCheck = true }; continue; }
            if (key == "--scale-check") { o = o with { ScaleCheck = true }; continue; }
            if (key == "--matrix") { o = o with { Matrix = true }; continue; }
            if (++i == args.Length) throw new ArgumentException($"Missing value for {key}.");
            var value = args[i];
            o = key switch {
                "--scenario" => o with { Scenario = value }, "--strategy" => o with { Strategy = value },
                "--backend" => o with { Backend = value }, "--actors" => o with { Actors = int.Parse(value) },
                "--rounds" => o with { Rounds = int.Parse(value) }, "--seed" => o with { Seed = int.Parse(value) },
                "--latency-ms" => o with { LatencyMs = int.Parse(value) }, "--slots" => o with { Slots = int.Parse(value) },
                "--candidate-limit" => o with { Budget = int.Parse(value) }, "--repeats" => o with { Repeats = int.Parse(value) },
                "--total-call-limit" => o with { TotalCallLimit = int.Parse(value) },
                "--cancel-mode" => o with { CancelMode = value }, "--fault-mode" => o with { FaultMode = value },
                "--output" => o with { Output = value }, _ => throw new ArgumentException($"Unknown option {key}.")
            };
        }
        if (!Scenarios.Contains(o.Scenario) || !new[] { "serial", "safe", "speculative" }.Contains(o.Strategy)
            || !new[] { "independent", "slots", "shared" }.Contains(o.Backend)
            || !new[] { "cooperative", "noncooperative" }.Contains(o.CancelMode)
            || !new[] { "none", "stale-shared", "actor-zero" }.Contains(o.FaultMode)
            || o.Actors < 2 || o.Actors > 128 || o.Rounds < 1 || o.Rounds > 100 || o.Budget < 1 || o.Budget > 128
            || o.LatencyMs < 0 || o.LatencyMs > 10000 || o.Slots < 1 || o.Repeats < 1 || o.TotalCallLimit < 1) throw new ArgumentException("Unsupported configuration.");
        return o;
    }
}

// This entire value is the effective synthetic request. Hashes never substitute for equality.
public sealed record Request(string Branch, int BindingGeneration, string Protocol, string Model, string Tools,
    string Options, string Shared, string NativeTailOrigin, string NativeTail, string OperationId, int Actor,
    int WakeGeneration, int ModelTime, int LocalValue, int Resource, string[] Messages, int Seed);
public sealed record Reply(string OperationId, int Amount);
public sealed record Commit(int Sequence, string OperationId, string Input, Reply Reply, int Resource, int[] Values);
public sealed record Trace(long Sequence, double Milliseconds, string Kind, int Target, string? Detail);
public sealed record DecisionTiming(int Target, int? CandidateSource, bool AdoptedPrefetch, double WaitMilliseconds, double? LeadMilliseconds);
public sealed record ServiceRecord(int Reservation, int Target, string InputHash, string Outcome,
    double StartedMilliseconds, double FinishedMilliseconds, double? HostCanceledMilliseconds) {
    public double ServiceMilliseconds => FinishedMilliseconds - StartedMilliseconds;
    public double ServiceAfterHostCancelMilliseconds => HostCanceledMilliseconds is double canceled
        ? Math.Max(0, FinishedMilliseconds - canceled) : 0;
}
public sealed record RunResult(Options Configuration, double WorkMilliseconds, double DrainMilliseconds,
    double ValidationMilliseconds, long ValidationAllocatedBytes, int AdoptedCandidates, int DiscardedCandidates,
    int DemandCalls, int Reservations, int UninvokedReservations, int Invoked, int Completed, int Canceled, int PeakInFlight, int RemainingCandidates,
    string TrajectoryHash, Commit[] Commits, Trace[] Timeline, string Outcome, string? ErrorType, string? ErrorMessage,
    int PrefetchedCandidates, int AdoptedPrefetchedCandidates, int ImmediateCandidates, DecisionTiming[] DecisionTimings,
    int AbandonedCandidates, int StaleFailures, int PhysicalFaulted, int RemainingPhysical, ServiceRecord[] Services,
    double ServiceMilliseconds, double ServiceAfterHostCancelMilliseconds);

internal sealed class World(Options options) {
    private readonly int[] values = new int[options.Actors];
    private readonly List<string>[] messages = Enumerable.Range(0, options.Actors).Select(_ => new List<string>()).ToArray();
    private readonly List<(int At, int Actor, string Text)> pending = [];
    private int resource = options.Scenario == "scarce-resource" ? 1 : 10000;
    public Request Input(int target, int actualBoundary) {
        int actor = target % options.Actors, round = target / options.Actors;
        // Target time is explicitly projected; wall-clock completion has no effect on it.
        int time = options.Scenario == "future-time" ? target : round;
        var visible = messages[actor].Concat(pending.Where(p => p.Actor == actor && p.At <= target).Select(p => p.Text)).ToArray();
        return new("synthetic-main", 1, "synthetic-v1", "deterministic", options.Scenario == "scarce-resource" ? "take-one-or-wait(amount:0|1)" : "act(amount:int)", "temperature=0",
            "synthetic-rules-v1:" + options.Scenario, "synthetic-v1", "opaque-tail", $"actor-{actor}/wake-{round}", actor,
            round, time, values[actor], options.Scenario is "shared-resource" or "scarce-resource" ? resource : 0, visible, options.Seed);
    }
    public void Deliver(int boundary) {
        foreach (var p in pending.Where(p => p.At <= boundary).ToArray()) {
            messages[p.Actor].Add(p.Text);
            pending.Remove(p);
        }
    }
    public Commit Apply(int target, Request input, Reply reply) {
        bool scarce = options.Scenario == "scarce-resource";
        if (reply.OperationId != input.OperationId || (scarce ? reply.Amount is < 0 or > 1 || reply.Amount > resource : reply.Amount is < 1 or > 3))
            throw new InvalidOperationException("Invalid reply or insufficient inventory.");
        values[input.Actor] += reply.Amount;
        resource -= reply.Amount;
        int next = (input.Actor + 1) % options.Actors;
        if (options.Scenario == "new-message") messages[next].Add($"from-{input.Actor}:{reply.Amount}:{target}");
        if (options.Scenario == "zero-chain") {
            // Instantaneous transitive closure: every actor receives the effect in this boundary.
            foreach (var inbox in messages) inbox.Add($"closure:{target}:{reply.Amount}");
        }
        if (options.Scenario == "delayed-message") pending.Add((target + 3, next, $"delayed:{target}:{reply.Amount}"));
        return new(target, input.OperationId, JsonFormat.Encode(input), reply, resource, values.ToArray());
    }
}

internal sealed class Backend : IDisposable {
    private readonly Options options;
    private readonly SemaphoreSlim slots;
    private readonly Action<string, int, string?> log;
    private int active, invoked, completed, canceled, peak, reservations, faulted;
    private readonly List<Task<Reply>> physical = [];
    private readonly List<ServiceRecord> services = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public int Faulted => faulted;
    public int RemainingPhysical => physical.Count(t => !t.IsCompleted);
    public ServiceRecord[] Services { get { lock (services) return services.ToArray(); } }
    public int Reservations => reservations;
    public int Invoked => invoked;
    public int Completed => completed;
    public int Canceled => canceled;
    public int Peak => peak;
    public Backend(Options options, Action<string, int, string?> log) {
        this.options = options; this.log = log;
        // Shared is a fixed-capacity FIFO-like single service station: all work consumes one throughput budget.
        slots = new(options.Backend == "independent" ? options.Budget : options.Backend == "shared" ? 1 : options.Slots);
    }
    public Task<Reply> InvokeAsync(Request request, int target, CancellationToken token) {
        // Only the coordinator calls this synchronous reservation boundary.
        if (reservations >= options.TotalCallLimit) throw new RequestBudgetExceededException(options.TotalCallLimit);
        reservations++;
        log("reserved", target, $"reservation={reservations};limit={options.TotalCallLimit}");
        var task = InvokeCoreAsync(request, target, reservations, token);
        physical.Add(task);
        return task;
    }
    private async Task<Reply> InvokeCoreAsync(Request request, int target, int reservation, CancellationToken token) {
        bool acquired = false, started = false;
        double startedAt = 0;
        double canceledAt = double.NaN;
        string outcome = "not-invoked";
        string inputHash = JsonFormat.Hash(JsonFormat.Encode(request));
        using var registration = token.Register(() => {
            Interlocked.CompareExchange(ref canceledAt, clock.Elapsed.TotalMilliseconds, double.NaN);
            log("host-cancel-requested", target, $"reservation={reservation};input={inputHash}");
        });
        log("queued", target, $"reservation={reservation};operation={request.OperationId}");
        try {
            await slots.WaitAsync(token); acquired = true;
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref invoked); started = true;
            startedAt = clock.Elapsed.TotalMilliseconds;
            int n = Interlocked.Increment(ref active);
            int observed;
            do { observed = peak; } while (n > observed && Interlocked.CompareExchange(ref peak, n, observed) != observed);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonFormat.Encode(request)));
            int delay = options.LatencyMs + (options.LatencyMs == 0 ? 0 : hash[0] % (options.LatencyMs + 1));
            bool injectFault = options.FaultMode == "actor-zero" && request.Actor == 0 && request.WakeGeneration == 0
                || options.FaultMode == "stale-shared" && request.Shared == "synthetic-rules-v1:shared-resource"
                    && request.Actor == 1 && request.Resource == 10000;
            if (injectFault) delay = Math.Max(1, delay * 4);
            log("invoked", target, $"reservation={reservation};delay={delay};input={inputHash}");
            await Task.Delay(delay, options.CancelMode == "noncooperative" ? CancellationToken.None : token);
            if (injectFault) throw new SyntheticProviderException(inputHash);
            Interlocked.Increment(ref completed);
            outcome = "completed";
            log("completed", target, $"reservation={reservation};input={inputHash}");
            return new(request.OperationId, options.Scenario == "scarce-resource" ? (request.Resource > 0 ? 1 : 0) : 1 + hash[1] % 3);
        } catch (OperationCanceledException) {
            if (started) Interlocked.Increment(ref canceled);
            outcome = started ? "cooperatively-canceled" : "not-invoked";
            log(started ? "canceled-remote-unknown" : "canceled-before-invoke", target, $"reservation={reservation}");
            throw;
        } catch (Exception ex) {
            if (started) Interlocked.Increment(ref faulted);
            outcome = "failed";
            log("physical-failed", target, $"reservation={reservation};{ex.GetType().Name};input={inputHash}");
            throw;
        } finally {
            if (started) {
                double end = clock.Elapsed.TotalMilliseconds;
                double canceledValue = Volatile.Read(ref canceledAt);
                lock (services) services.Add(new(reservation, target, inputHash, outcome, startedAt, end,
                    double.IsNaN(canceledValue) ? null : Math.Max(startedAt, canceledValue)));
                log("physical-finished", target, $"reservation={reservation};outcome={outcome}");
            }
            if (started) Interlocked.Decrement(ref active);
            if (acquired) slots.Release();
        }
    }
    public async Task DrainAsync() {
        foreach (var task in physical) {
            try { await task; } catch (Exception) { /* Every fault is logged; matching faults propagate at adoption. */ }
        }
    }
    public void Dispose() => slots.Dispose();
}

public sealed class SyntheticProviderException(string inputHash) : Exception($"Deterministic synthetic provider failure for input {inputHash}.");
public sealed class RequestBudgetExceededException(int limit) : Exception($"Synthetic request reservation limit {limit} exhausted before queue/invoke.");
public sealed class RunFailedException(RunResult snapshot, Exception cause) : Exception(cause.Message, cause) {
    public RunResult Snapshot { get; } = snapshot;
}

public static class Experiments {
    private sealed record Candidate(Request Input, CancellationTokenSource Cancellation, Task<Reply> Task, int Source, double StartedMilliseconds);
    public static async Task<RunResult> RunAsync(Options options, CancellationToken token = default) {
        var clock = Stopwatch.StartNew();
        var traces = new List<Trace>();
        object traceGate = new();
        long sequence = 0;
        void Log(string kind, int target, string? detail) {
            lock (traceGate) traces.Add(new(++sequence, clock.Elapsed.TotalMilliseconds, kind, target, detail));
        }
        using var backend = new Backend(options, Log);
        var world = new World(options);
        var candidates = new Dictionary<int, Candidate>();
        var commits = new List<Commit>();
        var timings = new List<DecisionTiming>();
        int adopted = 0, discarded = 0, demand = 0;
        int prefetched = 0, adoptedPrefetched = 0, immediate = 0;
        int abandoned = 0, staleFailures = 0;
        var retirements = new List<Task>();
        Exception? failure = null;
        double validation = 0;
        long allocations = 0;
        double work = 0;
        async Task ObserveAbandoned(Candidate c, int target) {
            try {
                try { await c.Task; } catch (OperationCanceledException) { }
                catch (Exception ex) {
                    Interlocked.Increment(ref staleFailures);
                    Log("stale-failure-isolated", target, $"{ex.GetType().Name}: {ex.Message}");
                }
            } finally { c.Cancellation.Dispose(); }
        }
        void Abandon(Candidate c, int target) {
            abandoned++;
            Log("candidate-abandoned", target, $"source={c.Source};physical-completed={c.Task.IsCompleted}");
            c.Cancellation.Cancel();
            retirements.Add(ObserveAbandoned(c, target));
        }
        try {
            for (int target = 0; target < options.Actors * options.Rounds; target++) {
                token.ThrowIfCancellationRequested();
                double readyAt = clock.Elapsed.TotalMilliseconds;
                int? candidateSource = null;
                double? lead = null;
                bool usedPrefetch = false;
                world.Deliver(target);
                // Never project a second wake for an actor whose prior decision has not committed.
                if (options.Strategy != "serial") {
                    int limit = Math.Min(options.Actors * options.Rounds, target + options.Budget);
                    for (int ahead = target; ahead < limit && candidates.Count < options.Budget; ahead++) {
                        if (candidates.ContainsKey(ahead) || ahead / options.Actors != target / options.Actors) continue;
                        bool safe = options.Scenario is "independent" or "future-time";
                        if (ahead != target && options.Strategy == "safe" && !safe) continue;
                        var predicted = world.Input(ahead, target);
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        Log("admitted", ahead, $"source={target};target={ahead};operation={predicted.OperationId}");
                        try {
                            candidates.Add(ahead, new(predicted, cts, backend.InvokeAsync(predicted, ahead, cts.Token), target, clock.Elapsed.TotalMilliseconds));
                            if (ahead > target) prefetched++; else immediate++;
                        }
                        catch { cts.Dispose(); throw; }
                    }
                }
                var actual = world.Input(target, target);
                Reply reply;
                if (candidates.Remove(target, out var candidate)) {
                    candidateSource = candidate.Source;
                    lead = Math.Max(0, readyAt - candidate.StartedMilliseconds);
                    long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                    long before = Stopwatch.GetTimestamp();
                    var expected = JsonFormat.Encode(candidate.Input);
                    var received = JsonFormat.Encode(actual);
                    bool equal = expected == received;
                    validation += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                    allocations += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                    if (equal) {
                        bool retiredInBackground = false;
                        try { reply = await candidate.Task.WaitAsync(token); }
                        catch (Exception ex) {
                            discarded++; Log("discarded-adoption-failed", target, null);
                            if (ex is OperationCanceledException && !candidate.Task.IsCompleted) {
                                Abandon(candidate, target);
                                retiredInBackground = true;
                            }
                            throw;
                        }
                        finally { if (!retiredInBackground) candidate.Cancellation.Dispose(); }
                        adopted++;
                        if (candidate.Source < target) { adoptedPrefetched++; usedPrefetch = true; }
                        Log("adopted", target, JsonFormat.Hash(received));
                    } else {
                        discarded++;
                        Log("discarded-input-mismatch", target, JsonFormat.Encode(new { Expected = candidate.Input, Actual = actual }));
                        Abandon(candidate, target);
                        demand++; reply = await backend.InvokeAsync(actual, target, token).WaitAsync(token);
                    }
                } else { demand++; reply = await backend.InvokeAsync(actual, target, token).WaitAsync(token); }
                timings.Add(new(target, candidateSource, usedPrefetch, clock.Elapsed.TotalMilliseconds - readyAt, lead));
                token.ThrowIfCancellationRequested();
                commits.Add(world.Apply(target, actual, reply));
                Log("committed", target, actual.OperationId);
            }
            work = clock.Elapsed.TotalMilliseconds;
        } catch (Exception ex) {
            failure = ex;
            work = clock.Elapsed.TotalMilliseconds;
            Log("run-failed", -1, $"{ex.GetType().Name}: {ex.Message}");
        } finally {
            foreach (var pair in candidates) {
                discarded++; Log("discarded-stop", pair.Key, null);
                try { Abandon(pair.Value, pair.Key); }
                catch (Exception ex) {
                    failure ??= ex;
                    Log("drain-failed", pair.Key, $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            candidates.Clear();
            try {
                await Task.WhenAll(retirements);
                await backend.DrainAsync();
            } catch (Exception ex) {
                failure ??= ex;
                Log("drain-failed", -1, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        var serviceRecords = backend.Services;
        var snapshot = new RunResult(options, work, clock.Elapsed.TotalMilliseconds - work, validation, allocations, adopted, discarded,
            demand, backend.Reservations, backend.Reservations - backend.Invoked, backend.Invoked, backend.Completed, backend.Canceled, backend.Peak, candidates.Count,
            JsonFormat.Hash(JsonFormat.Encode(commits)), commits.ToArray(), traces.ToArray(), failure is null ? "completed" : "failed",
            failure?.GetType().Name, failure?.Message, prefetched, adoptedPrefetched, immediate, timings.ToArray(),
            abandoned, staleFailures, backend.Faulted, backend.RemainingPhysical, serviceRecords,
            serviceRecords.Sum(s => s.ServiceMilliseconds), serviceRecords.Sum(s => s.ServiceAfterHostCancelMilliseconds));
        if (failure is not null) throw new RunFailedException(snapshot, failure);
        return snapshot;
    }
    public static async Task<object> MatrixAsync(Options options) {
        var results = new List<RunResult>();
        var warmups = new List<RunResult>();
        var orders = new List<object>();
        string[] strategies = ["serial", "safe", "speculative"];
        foreach (var scenario in Options.Scenarios)
            foreach (var backend in new[] { "independent", "slots", "shared" }) {
                // Equal conditions for every strategy; retained separately from measured samples.
                foreach (var strategy in strategies)
                    warmups.Add(await RunAsync(options with { Scenario = scenario, Backend = backend, Strategy = strategy }));
                for (int repeat = 0; repeat < options.Repeats; repeat++) {
                    string? baseline = null;
                    var order = Enumerable.Range(0, strategies.Length).Select(i => strategies[(repeat + i) % strategies.Length]).ToArray();
                    orders.Add(new { Scenario = scenario, Backend = backend, Repeat = repeat, Strategies = order });
                    foreach (var strategy in order) {
                        var result = await RunAsync(options with { Scenario = scenario, Backend = backend, Strategy = strategy, Seed = options.Seed + repeat });
                        baseline ??= result.TrajectoryHash;
                        if (baseline != result.TrajectoryHash) throw new InvalidOperationException($"Trajectory divergence: {scenario}/{backend}/{repeat}/{strategy}");
                        results.Add(result);
                    }
                }
            }
        return new { Kind = "synthetic-paired-matrix-v2", Planned = options, Warmups = warmups, Orders = orders, Results = results };
    }
}

public static class Checks {
    public static async Task<object> RunAsync() {
        int comparisons = 0, mismatches = 0;
        foreach (var scenario in Options.Scenarios) {
            var options = new Options(Scenario: scenario, Actors: 4, Rounds: 2, LatencyMs: 2, Budget: 4);
            var serial = await Experiments.RunAsync(options with { Strategy = "serial" });
            foreach (var strategy in new[] { "safe", "speculative" }) {
                var run = await Experiments.RunAsync(options with { Strategy = strategy });
                if (run.TrajectoryHash != serial.TrajectoryHash || run.RemainingCandidates != 0 || run.PeakInFlight > options.Budget)
                    throw new InvalidOperationException($"Self-check failed for {scenario}/{strategy}.");
                if (scenario == "independent" && run.PeakInFlight < 2) throw new InvalidOperationException("No request overlap.");
                mismatches += run.DiscardedCandidates; comparisons++;
            }
        }
        if (mismatches == 0) throw new InvalidOperationException("Invalidation was not exercised.");
        foreach (var backend in new[] { "slots", "shared" }) {
            var limited = await Experiments.RunAsync(new Options(Backend: backend, Slots: 2, LatencyMs: 2));
            int maximum = backend == "shared" ? 1 : 2;
            if (limited.PeakInFlight > maximum || limited.Invoked != limited.Completed + limited.Canceled)
                throw new InvalidOperationException("Backend accounting/capacity check failed.");
        }
        var request = new World(new Options()).Input(0, 0);
        var variants = new[] {
            request with { Branch = "other" }, request with { BindingGeneration = 2 }, request with { WakeGeneration = 2 },
            request with { Protocol = "other" }, request with { Model = "other" }, request with { Tools = "other" },
            request with { Options = "other" }, request with { Shared = "other" }, request with { NativeTailOrigin = "other" },
            request with { NativeTail = "other" }, request with { OperationId = "other" }, request with { Actor = 2 },
            request with { ModelTime = 2 }, request with { LocalValue = 2 }, request with { Resource = 2 },
            request with { Messages = ["new"] }, request with { Seed = 2 }
        };
        if (variants.Any(v => JsonFormat.Encode(v) == JsonFormat.Encode(request)))
            throw new InvalidOperationException("An effective input field was ignored.");
        bool budgetStopped = false;
        try { await Experiments.RunAsync(new Options(TotalCallLimit: 1, LatencyMs: 2)); }
        catch (RunFailedException ex) when (ex.InnerException is RequestBudgetExceededException) {
            var s = ex.Snapshot;
            budgetStopped = s.RemainingCandidates == 0 && s.Reservations == 1 && s.Invoked <= 1
                && s.Invoked == s.Completed + s.Canceled && s.Commits.Length == 0 && s.Timeline.Any(t => t.Kind == "run-failed");
        }
        if (!budgetStopped) throw new InvalidOperationException("Request reservation limit was not enforced.");
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(10);
        bool canceled = false;
        try { await Experiments.RunAsync(new Options(LatencyMs: 100), cancel.Token); }
        catch (RunFailedException ex) when (ex.InnerException is OperationCanceledException) {
            var s = ex.Snapshot;
            canceled = s.RemainingCandidates == 0 && s.Invoked == s.Completed + s.Canceled
                && s.Commits.Length == 0 && s.Timeline.Any(t => t.Kind == "run-failed");
        }
        if (!canceled) throw new InvalidOperationException("Cancellation was not observed.");
        var staleOptions = new Options(Scenario: "shared-resource", Actors: 4, Rounds: 1, LatencyMs: 3,
            CancelMode: "noncooperative", FaultMode: "stale-shared");
        var stale = await Experiments.RunAsync(staleOptions);
        var reference = await Experiments.RunAsync(staleOptions with { Strategy = "serial" });
        bool staleIsolated = stale.StaleFailures == 1 && stale.PhysicalFaulted == 1
            && stale.TrajectoryHash == reference.TrajectoryHash && stale.RemainingPhysical == 0
            && stale.RemainingCandidates == 0 && stale.AbandonedCandidates > 0
            && stale.Invoked == stale.Completed + stale.Canceled + stale.PhysicalFaulted;
        if (!staleIsolated) throw new InvalidOperationException("Stale physical failure was not isolated/accounted.");
        bool matchingFailed = false;
        try { await Experiments.RunAsync(new Options(Actors: 4, Rounds: 1, LatencyMs: 2, FaultMode: "actor-zero", CancelMode: "noncooperative")); }
        catch (RunFailedException ex) when (ex.InnerException is SyntheticProviderException) {
            var s = ex.Snapshot;
            matchingFailed = s.Commits.Length == 0 && s.RemainingCandidates == 0 && s.RemainingPhysical == 0
                && s.PhysicalFaulted >= 1 && s.Invoked == s.Completed + s.Canceled + s.PhysicalFaulted;
        }
        if (!matchingFailed) throw new InvalidOperationException("Matching failure did not stop without retry.");
        bool remoteDrained = false;
        using var noncooperativeCancel = new CancellationTokenSource();
        noncooperativeCancel.CancelAfter(10);
        try { await Experiments.RunAsync(new Options(LatencyMs: 100, Backend: "slots", Slots: 2,
            CancelMode: "noncooperative"), noncooperativeCancel.Token); }
        catch (RunFailedException ex) when (ex.InnerException is OperationCanceledException) {
            var s = ex.Snapshot;
            remoteDrained = s.Commits.Length == 0 && s.RemainingCandidates == 0 && s.RemainingPhysical == 0
                && s.Invoked > 0 && s.Canceled == 0 && s.Completed == s.Invoked
                && s.ServiceAfterHostCancelMilliseconds > 0 && s.DrainMilliseconds > 0
                && s.PeakInFlight <= 2 && s.Services.Length == s.Invoked;
        }
        if (!remoteDrained) throw new InvalidOperationException("Noncooperative physical service did not drain/account after host cancellation.");
        var scarce = await Experiments.RunAsync(new Options(Scenario: "scarce-resource", Actors: 2, Rounds: 1, Budget: 2, LatencyMs: 2));
        var scarceReference = await Experiments.RunAsync(new Options(Scenario: "scarce-resource", Strategy: "serial", Actors: 2, Rounds: 1, Budget: 2, LatencyMs: 2));
        bool conserved = scarce.TrajectoryHash == scarceReference.TrajectoryHash && scarce.DiscardedCandidates == 1
            && scarce.Commits.Select(c => c.Reply.Amount).SequenceEqual(new[] { 1, 0 })
            && scarce.Commits.All(c => c.Resource >= 0 && c.Resource + c.Values.Sum() == 1)
            && scarce.Invoked == 3;
        if (!conserved) throw new InvalidOperationException("Scarce inventory conflict was not re-decided with conservation.");
        return new { Passed = true, Comparisons = comparisons, DiscardedCandidates = mismatches, EffectiveInputMutations = variants.Length,
            BackendCapacityChecks = 2, RequestBudgetStopObserved = budgetStopped, CancellationObserved = canceled,
            StaleFailureIsolated = staleIsolated, MatchingFailureStopped = matchingFailed, NoncooperativeRemoteDrained = remoteDrained,
            ScarceInventoryConserved = conserved,
            Scope = "synthetic harness only; not Playground integration or general conservatism proof" };
    }
}

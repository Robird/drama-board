using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DramaBoard.Experiments.PredictiveTimeParallelism.Influence;

namespace DramaBoard.Experiments.PredictiveTimeParallelism.Causal;

public static class ModelTimeWindows {
    private const int Horizon = 3;
    private sealed record Event(int Tick, int Kind, int Actor, int Round, int Value, int Visited, int Sequence);
    private sealed record Candidate(string Input, int Generation, Task<int> Response);
    private sealed record Rule(string Name, InfluenceEdge[] Edges, bool Complete = true, bool Dynamic = false);
    public sealed record CallTrace(int Id, double StartedMs, double CompletedMs, string InputHash);
    public sealed record Result(string Rule, string Wakes, string Strategy, int Seed, string InputHash, string FactHash,
        int Calls, int Adopted, int Invalidated, int Certificates, int EarlyCertificates, int Rejections, int UnknownRejections,
        int ExactBoundaryRejections, int ArrivalRejections, int MaxInFlight, double ElapsedMs, double EstimatorMs, double ProjectionMs, double ValidationMs, int ModelTime,
        string[] Inputs, string[] Facts, CallTrace[] CallTimeline);

    private sealed class World {
        public int[] Values = new int[4];
        public List<Event> Queue = new();
        public List<InfluenceEdge> Edges = new();
        public int Generation;
        public int Sequence;
        public int Now;
        public List<string> Facts = new();
        public World Clone() => new() { Values = (int[])Values.Clone(), Queue = new(Queue), Edges = new(Edges), Generation = Generation, Sequence = Sequence, Now = Now };
        public Event Next() => Queue.OrderBy(e => e.Tick).ThenBy(e => e.Kind).ThenBy(e => e.Actor).ThenBy(e => e.Sequence).First();
        public void Mechanical(Event e) {
            Queue.Remove(e);
            Now = e.Tick;
            if (e.Kind == -1) {
                Edges.Add(new(0, 2, 0));
                Generation++;
                Facts.Add($"{e.Tick}:topology:{Generation}");
                return;
            }
            Values[e.Actor] += e.Value;
            Facts.Add($"{e.Tick}:delivery:{e.Actor}:{e.Value}");
            Emit(e.Tick, e.Actor, e.Value, e.Visited | 1 << e.Actor);
        }
        public void Emit(int tick, int source, int value, int visited) {
            foreach (var edge in Edges.Where(x => x.From == source)) {
                if ((visited & 1 << edge.To) == 0) Queue.Add(new(tick + edge.MinimumDelay, 0, edge.To, 0, value, visited, Sequence++));
            }
        }
    }

    public static async Task<object> RunAsync() {
        var rules = new[] {
            new Rule("finite-range/no-relation", Array.Empty<InfluenceEdge>()),
            new Rule("instantaneous-transitive", new[] { new InfluenceEdge(0, 1, 0), new InfluenceEdge(1, 2, 0) }),
            new Rule("delay-one", new[] { new InfluenceEdge(0, 1, 1), new InfluenceEdge(1, 2, 1) }),
            new Rule("shared-hub", new[] { new InfluenceEdge(0, 3, 0), new InfluenceEdge(1, 3, 0), new InfluenceEdge(2, 3, 0), new InfluenceEdge(3, 0, 0), new InfluenceEdge(3, 1, 0), new InfluenceEdge(3, 2, 0) }),
            new Rule("unknown", new[] { new InfluenceEdge(0, 2, 0) }, Complete: false),
            new Rule("dynamic-generation", Array.Empty<InfluenceEdge>(), Dynamic: true)
        };
        var results = new List<Result>();
        foreach (var rule in rules) {
            foreach (bool simultaneous in new[] { true, false }) {
                string[] strategies = ["serial", "coarse-safe", "timed-safe", "validated-speculation"];
                foreach (string strategy in strategies) await ExecuteAsync(rule, simultaneous, strategy, -1);
                for (int seed = 0; seed < 5; seed++) {
                    Result? reference = null;
                    for (int index = 0; index < strategies.Length; index++) {
                        string strategy = strategies[(index + seed) % strategies.Length];
                        var result = await ExecuteAsync(rule, simultaneous, strategy, seed);
                        reference ??= result;
                        if (reference.InputHash != result.InputHash || reference.FactHash != result.FactHash)
                            throw new InvalidOperationException($"Causal trace divergence: {rule.Name}/{simultaneous}/{strategy}/{seed}");
                        results.Add(result);
                    }
                }
            }
        }
        int delayCoarse = results.Where(x => x.Rule == "delay-one" && x.Wakes == "simultaneous" && x.Strategy == "coarse-safe").Sum(x => x.EarlyCertificates);
        int delayTimed = results.Where(x => x.Rule == "delay-one" && x.Wakes == "simultaneous" && x.Strategy == "timed-safe").Sum(x => x.EarlyCertificates);
        if (delayTimed <= delayCoarse) throw new InvalidOperationException("Delay bounds provided no additional safe certificates.");
        if (!results.Any(x => x.Strategy == "validated-speculation" && x.Invalidated > 0)) throw new InvalidOperationException("Speculation did not exercise invalidation.");
        if (results.Any(x => x.Strategy.EndsWith("safe", StringComparison.Ordinal) && x.Invalidated != 0)) throw new InvalidOperationException("A certified candidate became invalid.");
        if (!results.Any(x => x.MaxInFlight > 1)) throw new InvalidOperationException("No actual call overlap.");
        return new { Horizon, Provider = "deterministic-input-addressed-Task.Delay(5ms)", Results = results,
            PairedSeeds = 5, WarmupsPerCondition = 4, RotatingStrategyOrder = true,
            SameRuleTraceChecks = 180, StrictArrivalBoundary = true, MechanicalBeforeWake = true,
            DelayAdditionalCertificates = delayTimed - delayCoarse, Failures = 0 };
    }

    private static async Task<Result> ExecuteAsync(Rule rule, bool simultaneous, string strategy, int seed) {
        var wall = Stopwatch.StartNew();
        var world = new World { Edges = new(rule.Edges) };
        for (int round = 0; round < 2; round++) {
            for (int actor = 0; actor < 3; actor++) world.Queue.Add(new(round * 4 + (simultaneous ? 0 : actor), 1, actor, round, 0, 0, world.Sequence++));
        }
        // Already scheduled independent environment effect: forecast must include it.
        world.Queue.Add(new(1, 0, 2, 0, 7, 0, world.Sequence++));
        if (rule.Dynamic) world.Queue.Add(new(1, -1, 0, 0, 0, 0, world.Sequence++));
        var candidates = new Dictionary<int, Candidate>();
        var inputs = new List<string>();
        var callTimeline = new ConcurrentQueue<CallTrace>();
        int calls = 0, adopted = 0, invalidated = 0, certificates = 0, earlyCertificates = 0, rejections = 0, unknown = 0, active = 0, maxActive = 0;
        int exactBoundary = 0, arrivalRejections = 0;
        long estimatorTicks = 0, projectionTicks = 0, validationTicks = 0;
        async Task<int> Invoke(string input) {
            int id = ++calls;
            double start = wall.Elapsed.TotalMilliseconds;
            int inFlight = Interlocked.Increment(ref active);
            int seen;
            do { seen = Volatile.Read(ref maxActive); if (seen >= inFlight) break; } while (Interlocked.CompareExchange(ref maxActive, inFlight, seen) != seen);
            try {
                await Task.Delay(5);
                return SHA256.HashData(Encoding.UTF8.GetBytes(input))[0] % 3 + 1;
            } finally {
                callTimeline.Enqueue(new(id, start, wall.Elapsed.TotalMilliseconds, Hash(new[] { input })));
                Interlocked.Decrement(ref active);
            }
        }
        while (world.Queue.Count > 0) {
            if (strategy != "serial") {
                foreach (var wake in world.Queue.Where(e => e.Kind == 1).GroupBy(e => e.Actor).Select(g => g.OrderBy(e => e.Tick).First()).OrderBy(e => e.Tick).ThenBy(e => e.Actor).ToArray()) {
                    if (candidates.ContainsKey(wake.Sequence) || wake.Tick - world.Now > Horizon) continue;
                    long started = Stopwatch.GetTimestamp();
                    bool safe = IsSafe(world, wake, rule, strategy == "timed-safe", out string reason);
                    estimatorTicks += Stopwatch.GetTimestamp() - started;
                    if (!safe && strategy != "validated-speculation") {
                        rejections++;
                        if (reason == "unknown") unknown++;
                        else if (reason == "exact-boundary") exactBoundary++;
                        else arrivalRejections++;
                        continue;
                    }
                    if (safe) {
                        certificates++;
                        if (world.Queue.Any(e => e.Kind == 1 && (e.Tick < wake.Tick || e.Tick == wake.Tick && e.Actor < wake.Actor))) earlyCertificates++;
                    }
                    started = Stopwatch.GetTimestamp();
                    var projected = Project(world, wake.Tick);
                    string input = Input(projected, wake, rule.Name, seed);
                    projectionTicks += Stopwatch.GetTimestamp() - started;
                    candidates.Add(wake.Sequence, new(input, projected.Generation, Invoke(input)));
                }
            }
            var next = world.Next();
            if (next.Kind != 1) { world.Mechanical(next); continue; }
            world.Queue.Remove(next);
            world.Now = next.Tick;
            string actual = Input(world, next, rule.Name, seed);
            int command;
            if (candidates.Remove(next.Sequence, out var candidate)) {
                int predicted = await candidate.Response;
                long validationStart = Stopwatch.GetTimestamp();
                bool valid = candidate.Input == actual && candidate.Generation == world.Generation;
                validationTicks += Stopwatch.GetTimestamp() - validationStart;
                if (valid) { command = predicted; adopted++; }
                else { invalidated++; command = await Invoke(actual); }
            } else command = await Invoke(actual);
            inputs.Add(actual);
            world.Facts.Add($"{next.Tick}:decision:{next.Actor}:{next.Round}:{command}");
            world.Emit(next.Tick, next.Actor, command, 1 << next.Actor);
        }
        if (candidates.Count != 0 || active != 0) throw new InvalidOperationException("Unsettled candidates.");
        wall.Stop();
        return new(rule.Name, simultaneous ? "simultaneous" : "staggered", strategy, seed, Hash(inputs), Hash(world.Facts), calls, adopted, invalidated, certificates, earlyCertificates, rejections, unknown,
            exactBoundary, arrivalRejections, maxActive, wall.Elapsed.TotalMilliseconds, estimatorTicks * 1000d / Stopwatch.Frequency, projectionTicks * 1000d / Stopwatch.Frequency,
            validationTicks * 1000d / Stopwatch.Frequency, world.Now, inputs.ToArray(), world.Facts.ToArray(), callTimeline.OrderBy(c => c.Id).ToArray());
    }

    private static bool IsSafe(World world, Event target, Rule rule, bool timed, out string reason) {
        int offset = target.Tick - world.Now;
        var seeds = world.Queue.Where(e => e.Kind == 1 && e.Sequence != target.Sequence &&
            (e.Tick < target.Tick || e.Tick == target.Tick && e.Actor < target.Actor))
            .Select(e => new InfluenceSeed(e.Actor, e.Tick - world.Now)).ToArray();
        bool topologyChanges = world.Queue.Any(e => e.Kind == -1 && e.Tick <= target.Tick);
        var graph = new InfluenceGraph(4, world.Edges, world.Generation, rule.Complete);
        var envelope = InfluenceEstimator.Outward(graph, seeds, Horizon, topologyChanges ? world.Generation + 1 : world.Generation, timed);
        bool unknown = envelope.Unknown;
        reason = unknown ? "unknown" : envelope.EarliestOffsets[target.Actor] == offset ? "exact-boundary" : "arrival";
        // Equality cannot certify safety: mechanical deliveries run before decisions.
        return !unknown && offset <= Horizon && (envelope.EarliestOffsets[target.Actor] is not long arrival || arrival > offset);
    }

    private static World Project(World source, int targetTick) {
        var copy = source.Clone();
        copy.Queue.RemoveAll(e => e.Kind == 1);
        while (copy.Queue.Count > 0 && copy.Next().Tick <= targetTick) copy.Mechanical(copy.Next());
        copy.Now = targetTick;
        return copy;
    }

    private static string Input(World world, Event wake, string rule, int seed) => JsonSerializer.Serialize(new {
        Version = 1, Rule = rule, Actor = wake.Actor, Round = wake.Round, ModelTime = wake.Tick,
        LocalValue = world.Values[wake.Actor], TopologyGeneration = world.Generation,
        Tool = "emit(value=sha256(full-input)[0]%3+1)", Protocol = "fixture-v1", FixtureSeed = seed
    });

    private static string Hash(IEnumerable<string> values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
}

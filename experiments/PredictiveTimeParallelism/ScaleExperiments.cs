namespace PredictiveTimeParallelism;

/// <summary>Prespecified scale slice; does not increase real Playground actor limits.</summary>
public static class ScaleExperiments {
    public static async Task<object> RunAsync() {
        int[] actors = [2, 8, 32];
        string[] scenarios = ["independent", "shared-resource"];
        string[] backends = ["independent", "shared"];
        string[] strategies = ["serial", "safe", "speculative"];
        var warmups = new List<RunResult>();
        var results = new List<RunResult>();
        var orders = new List<object>();
        int comparisons = 0;
        foreach (int count in actors) {
            foreach (string scenario in scenarios) {
                foreach (string backend in backends) {
                    var options = new Options(Actors: count, Rounds: 2, Scenario: scenario, Backend: backend,
                        Budget: Math.Min(4, count), LatencyMs: 5, CancelMode: "noncooperative");
                    foreach (string strategy in strategies) warmups.Add(await Experiments.RunAsync(options with { Strategy = strategy }));
                    for (int repeat = 0; repeat < 5; repeat++) {
                        string[] order = Enumerable.Range(0, strategies.Length).Select(i => strategies[(i + repeat) % strategies.Length]).ToArray();
                        orders.Add(new { Actors = count, Scenario = scenario, Backend = backend, Repeat = repeat, Strategies = order });
                        RunResult? baseline = null;
                        foreach (string strategy in order) {
                            var result = await Experiments.RunAsync(options with { Strategy = strategy, Seed = options.Seed + repeat });
                            if (baseline is not null) {
                                if (result.TrajectoryHash != baseline.TrajectoryHash) throw new InvalidOperationException("Scale slice changed the committed trajectory.");
                                comparisons++;
                            }
                            baseline ??= result;
                            if (result.Commits.Length != count * 2 || result.RemainingCandidates != 0 || result.RemainingPhysical != 0)
                                throw new InvalidOperationException("Scale slice did not complete/drain the declared work.");
                            results.Add(result);
                        }
                    }
                }
            }
        }
        return new { Kind = "prespecified-scale-slice-v1", Actors = actors, Scenarios = scenarios, Backends = backends,
            Strategies = strategies, PairedRepeats = 5, CandidateLimit = "min(4, actors)", Rounds = 2, LatencyMs = 5,
            CancelMode = "noncooperative", Comparisons = comparisons, Warmups = warmups, Orders = orders, Results = results };
    }
}

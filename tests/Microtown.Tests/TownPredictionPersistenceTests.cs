using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownPredictionPersistenceTests {
    private static readonly SimulationRules Rules = new(0, 1024);

    [Fact]
    public async Task Every_completed_state_cold_rebuilds_full_owner_cache_and_preserves_the_uninterrupted_trace() {
        using var files = new Files();
        var start = TownGameFactory.Create(26);
        var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var baseline = TownEngine.CreateKernel(memory, Scenario());
        Func<TownDecision, CancellationToken, ValueTask<TownCommand>> persistedScript = Scenario();
        using (TownDurableHistory.Create(files.Store, start.World, start.Cursor)) { }
        var kinds = new HashSet<TownEventKind>();
        var retained = new List<(TownWorld World, string Business, string Cache)>();

        for (int step = 0; step < 600; step++) {
            using var history = TownDurableHistory.Open(files.Store);
            Assert.Null(history.PendingEvent);
            Assert.NotSame(memory.State, history.State);
            Assert.NotSame(memory.State.Forecasts, history.State.Forecasts);
            EqualWorld(memory.State, history.State);
            Assert.Equal(memory.Cursor, history.Cursor);
            CheckOracle(history.State);
            retained.Add((history.State, Business(history.State), Cache(history.State)));
            if (history.State.Business.Ended) {
                Assert.Contains(TownEventKind.TradeExpired, kinds);
                Assert.Contains(TownEventKind.ProductionCompleted, kinds);
                Assert.Contains(TownEventKind.Arrived, kinds);
                Assert.Contains(TownEventKind.MarketRefreshed, kinds);
                Assert.Contains(TownEventKind.Ended, kinds);
                Assert.All(history.State.Business.Characters, actor => Assert.False(actor.Failed));
                Assert.Empty(history.State.Forecasts.Entries);
                foreach (var old in retained) {
                    Assert.Equal(old.Business, Business(old.World));
                    Assert.Equal(old.Cache, Cache(old.World));
                }
                return;
            }

            var resumed = TownEngine.CreateKernel(history, persistedScript);
            Assert.False(resumed.RecoverPending());
            Assert.Equal(StepStatus.Committed, await baseline.StepAsync(start.World.Business.EndTime));
            Assert.Equal(StepStatus.Committed, await resumed.StepAsync(start.World.Business.EndTime));
            Assert.Equal(baseline.LastCompletion!.Event.CauseKey, resumed.LastCompletion!.Event.CauseKey);
            Assert.Equal(baseline.LastCompletion.Event.TargetInstant, resumed.LastCompletion.Event.TargetInstant);
            EqualWorld(memory.State, history.State);
            CheckOracle(memory.State);
            CheckOracle(history.State);
            foreach (TownFact fact in resumed.LastCompletion.Event.Facts) {
                Assert.True(fact.Accepted, fact.Result);
                kinds.Add(fact.Kind);
            }
        }
        throw new InvalidOperationException("Prediction persistence scenario did not reach its finite endpoint.");
    }

    [Theory]
    [InlineData("production-start")]
    [InlineData("production-complete")]
    [InlineData("trade-expiry")]
    [InlineData("movement-start")]
    [InlineData("meal-used")]
    public async Task Event_only_cold_recovery_rederives_cache_without_controller_and_matches_uninterrupted_boundary(string interruption) {
        using var files = new Files();
        var start = TownGameFactory.Create(26);
        var memory = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var baseline = TownEngine.CreateKernel(memory, Scenario());
        TownWorld? oldWorld = null;
        string? oldBusiness = null, oldCache = null;
        KernelCursor? oldCursor = null;
        bool interrupted = false;
        using (var history = TownDurableHistory.Create(files.Store, start.World, start.Cursor)) {
            for (int step = 0; step < 600 && !history.State.Business.Ended; step++) {
                EqualWorld(memory.State, history.State);
                CheckOracle(history.State);
                oldWorld = history.State;
                oldBusiness = Business(oldWorld);
                oldCache = Cache(oldWorld);
                oldCursor = history.Cursor;
                Assert.Equal(StepStatus.Committed, await baseline.StepAsync(start.World.Business.EndTime));
                TownFact fact = Assert.Single(baseline.LastCompletion!.Event.Facts);
                Assert.True(fact.Accepted, fact.Result);
                history.CommitEvent(baseline.LastCompletion.Event);
                if (Matches(interruption, fact)) {
                    interrupted = true;
                    Assert.NotNull(history.PendingEvent);
                    Assert.Same(oldWorld, history.State);
                    Assert.Equal(oldBusiness, Business(history.State));
                    Assert.Equal(oldCache, Cache(history.State));
                    break; // True disk E is deliberately left without its completing S.
                }
                history.CommitState(memory.State, memory.Cursor);
            }
        }
        Assert.True(interrupted, $"The scenario never produced the requested interruption: {interruption}.");

        using (var history = TownDurableHistory.Open(files.Store)) {
            Assert.NotNull(history.PendingEvent);
            Assert.Equal(oldCursor, history.Cursor);
            Assert.Equal(oldBusiness, Business(history.State));
            Assert.Equal(oldCache, Cache(history.State));
            CheckOracle(history.State);
            TownWorld coldBefore = history.State;
            var recovery = TownEngine.CreateControlledKernel(history,
                (_, _) => throw new InvalidOperationException("Pending recovery invoked a character controller."));
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(memory.Cursor, history.Cursor);
            EqualWorld(memory.State, history.State);
            CheckOracle(history.State);
            Assert.Equal(oldBusiness, Business(coldBefore));
            Assert.Equal(oldCache, Cache(coldBefore));
        }
        using var final = TownDurableHistory.Open(files.Store);
        Assert.Null(final.PendingEvent);
        EqualWorld(memory.State, final.State);
        CheckOracle(final.State);
        Assert.Equal(oldBusiness, Business(oldWorld!));
        Assert.Equal(oldCache, Cache(oldWorld!));
    }

    [Fact]
    public async Task Exact_state_fork_rebuilds_only_source_predictions_and_keeps_both_cached_futures_independent() {
        using var files = new Files();
        var start = TownGameFactory.Create(4);
        TownDecisionBoundary source;
        TownWorld sourceWorld;
        KernelCursor sourceCursor;
        string sourceBusiness, sourceCache, parentBusiness, parentCache, parentHead;
        using (var history = TownDurableHistory.Create(files.Store, start.World, start.Cursor)) {
            var opening = TownEngine.CreateControlledKernel(history,
                (_, _) => throw new InvalidOperationException("Opening unexpectedly invoked the controller."));
            Assert.Equal(StepStatus.Committed, await opening.StepAsync(start.World.Business.EndTime));
            source = history.CaptureDecisionBoundary("a");
            sourceWorld = history.State;
            sourceCursor = history.Cursor;
            sourceBusiness = Business(sourceWorld);
            sourceCache = Cache(sourceWorld);
            CheckOracle(sourceWorld);

            var commands = new Queue<string>(["buy grain 2", "go market-workshop", "produce meal 1"]);
            var parent = TownEngine.CreateKernel(history, (decision, _) => ValueTask.FromResult(
                TownCommand.Parse(decision.ActorId == "a" ? commands.Dequeue() : "wait 4")));
            for (int step = 0; step < 50 && history.State.Business.Character("a").Activity is null; step++) {
                TownWorld previous = history.State;
                string previousCache = Cache(previous);
                Assert.Equal(StepStatus.Committed, await parent.StepAsync(start.World.Business.EndTime));
                Assert.All(parent.LastCompletion!.Event.Facts, fact => Assert.True(fact.Accepted, fact.Result));
                CheckOracle(history.State);
                Assert.Equal(previousCache, Cache(previous));
            }
            Assert.Equal(TownActivityKind.Produce, history.State.Business.Character("a").Activity!.Kind);
            Assert.True(history.State.Forecasts.Entries.ContainsKey("actor:a:activity"));
            parentBusiness = Business(history.State);
            parentCache = Cache(history.State);
            parentHead = TownDurableHistory.FormatCommitId(history.HeadCommitId);
        }

        TownBranchInfo child = TownDurableHistory.Fork(files.Store, source.BranchId, source.StateCommitId, "prediction-source");
        string childBusiness, childCache, childHead;
        using (var history = TownDurableHistory.Open(files.Store, child.Id)) {
            Assert.Equal(source.StateCommitId, history.CompletedStateCommitId);
            Assert.Equal(sourceCursor, history.Cursor);
            Assert.Equal(sourceBusiness, Business(history.State));
            Assert.Equal(sourceCache, Cache(history.State));
            Assert.False(history.State.Forecasts.Entries.ContainsKey("actor:a:activity"));
            CheckOracle(history.State);
            var childKernel = TownEngine.CreateKernel(history, (decision, _) => ValueTask.FromResult(
                TownCommand.Parse(decision.ActorId == "a" ? "wait 2" : "wait 4")));
            for (int step = 0; step < 50 && history.State.Time.Ticks < 2 * TownContent.Hour; step++) {
                TownWorld previous = history.State;
                string previousBusiness = Business(previous), previousCache = Cache(previous);
                Assert.Equal(StepStatus.Committed, await childKernel.StepAsync(start.World.Business.EndTime));
                CheckOracle(history.State);
                Assert.Equal(previousBusiness, Business(previous));
                Assert.Equal(previousCache, Cache(previous));
            }
            Assert.Equal(2 * TownContent.Hour, history.State.Time.Ticks);
            Assert.Null(history.State.Business.Character("a").Activity);
            Assert.False(history.State.Forecasts.Entries.ContainsKey("actor:a:activity"));
            childBusiness = Business(history.State);
            childCache = Cache(history.State);
            childHead = TownDurableHistory.FormatCommitId(history.HeadCommitId);
        }
        using (var parent = TownDurableHistory.Open(files.Store, source.BranchId)) {
            Assert.Equal(parentHead, TownDurableHistory.FormatCommitId(parent.HeadCommitId));
            Assert.Equal(parentBusiness, Business(parent.State));
            Assert.Equal(parentCache, Cache(parent.State));
            CheckOracle(parent.State);
        }
        using var coldChild = TownDurableHistory.Open(files.Store, child.Id);
        Assert.Equal(childHead, TownDurableHistory.FormatCommitId(coldChild.HeadCommitId));
        Assert.Equal(childBusiness, Business(coldChild.State));
        Assert.Equal(childCache, Cache(coldChild.State));
        CheckOracle(coldChild.State);
        Assert.Equal(sourceBusiness, Business(sourceWorld));
        Assert.Equal(sourceCache, Cache(sourceWorld));
    }

    private static void CheckOracle(TownWorld world) {
        // Rebuild spatial predictions as well: comparing two consumers of the same stale spatial cache is not an oracle.
        TownWorld rebuilt = TownEngine.Rebuild(world.Business);
        Assert.Equal(Cache(rebuilt), Cache(world));
        Assert.Equal(Candidates(TownEngine.ForecastOracle(rebuilt, Rules)), Candidates(TownEngine.Forecast(world, Rules)));
        Assert.Same(world.Business.Spatial, world.Spatial.Business);
    }

    private static void EqualWorld(TownWorld expected, TownWorld actual) {
        Assert.Equal(Business(expected), Business(actual));
        Assert.Equal(Cache(expected), Cache(actual));
        Assert.Equal(Candidates(TownEngine.Forecast(expected, Rules)), Candidates(TownEngine.Forecast(actual, Rules)));
    }

    private static string Cache(TownWorld world) => JsonSerializer.Serialize(world.Forecasts.Entries
        .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { Owner = pair.Key, Prediction = Describe(pair.Value) }));

    private static string Candidates(IEnumerable<OccurrenceCandidate<TownPrediction>> candidates) => JsonSerializer.Serialize(candidates
        .Select(candidate => new {
            Key = candidate.Key.ToString(), Due = candidate.Due.ModelTime.Ticks, Prediction = Describe(candidate.Data)
        }));

    private static object Describe(TownPrediction prediction) => new {
        prediction.Kind, Due = prediction.Due.Ticks, prediction.ActorId, prediction.TradeId,
        SpatialKey = prediction.SpatialCandidate?.Key.ToString(),
        SpatialDue = prediction.SpatialCandidate?.Due.ModelTime.Ticks,
        SpatialData = (object?)prediction.SpatialCandidate?.Data
    };

    private static string Business(TownWorld world) => JsonSerializer.Serialize(new {
        world.Business,
        Locations = world.Business.Spatial.Entities.Select(entity => new { entity.Id, Location = (object)entity.Location })
    });

    private static bool Matches(string interruption, TownFact fact) => interruption switch {
        "production-start" => fact.Kind == TownEventKind.Operation && fact.Result.StartsWith("Started 1 batch", StringComparison.Ordinal),
        "production-complete" => fact.Kind == TownEventKind.ProductionCompleted,
        "trade-expiry" => fact.Kind == TownEventKind.TradeExpired,
        "movement-start" => fact.Kind == TownEventKind.Operation && fact.Result.StartsWith("Started movement", StringComparison.Ordinal),
        "meal-used" => fact.Kind == TownEventKind.Operation && fact.Result.StartsWith("Consumed one meal", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(interruption))
    };

    private static Func<TownDecision, CancellationToken, ValueTask<TownCommand>> Scenario() {
        var queues = new Dictionary<string, Queue<string>>();
        return (decision, _) => {
            if (!queues.TryGetValue(decision.ActorId, out Queue<string>? commands)) {
                commands = new Queue<string>();
                if (decision.ActorId == "a") { commands.Enqueue("offer b meal 1 money 5 1"); }
                foreach (string command in new[] { "buy grain 2", "buy wood 2", "go market-workshop", "produce meal 1",
                    "produce woodware 1", "go market-workshop", "sell woodware 1" }) { commands.Enqueue(command); }
                queues.Add(decision.ActorId, commands);
            }
            string raw = decision.Character.Activity is not null ? "continue"
                : decision.Character.Food <= 40 && decision.Character.Inventory.Count("meal") > 0 ? "eat"
                : commands.TryDequeue(out string? next) ? next : "wait 1";
            return ValueTask.FromResult(TownCommand.Parse(raw));
        };
    }

    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "microtown-prediction-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}

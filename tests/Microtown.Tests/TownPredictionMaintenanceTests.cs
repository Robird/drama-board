using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown.Tests;

public sealed class TownPredictionMaintenanceTests {
    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(2L, 1L)]
    [InlineData(4L, 2L)]
    public async Task Sub_tick_food_rounding_and_clamp_preserve_remaining_same_tick_failures(long reserve, long due) {
        var start = TownGameFactory.Create(1);
        TownWorld world = Copy(start.World.Business, actors: start.World.Business.Characters.Select(actor =>
            actor with { FoodReserve = reserve, MinimumFoodReserve = reserve }));
        var run = new Run((world, start.Cursor));
        await run.Act("a", "wait 1");
        await run.Act("b", "wait 1");
        await run.Act("c", "wait 1");
        var originals = run.World.Business.Characters.ToDictionary(actor => actor.Id,
            actor => run.World.Forecasts.Entries[$"actor:{actor.Id}:failure"]);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 3; i++) {
            await run.Step();
            Assert.Equal(TownEventKind.Failed, run.Last.Kind);
            Assert.NotNull(run.Last.ActorId);
            Assert.True(failed.Add(run.Last.ActorId));
            Assert.Equal(new ModelTime(due), run.World.Time);
            foreach (TownCharacter survivor in run.World.Business.Characters.Where(actor => !actor.Failed)) {
                string owner = $"actor:{survivor.Id}:failure";
                Assert.Same(originals[survivor.Id], run.World.Forecasts.Entries[owner]);
                Assert.Equal(new ModelTime(due), run.World.Forecasts.Entries[owner].Due);
                Assert.DoesNotContain(owner, run.World.Forecasts.LastRecomputedOwners);
            }
        }
        Assert.Equal(new[] { "a", "b", "c" }, failed.Order(StringComparer.Ordinal));
        Assert.All(run.World.Business.Characters, actor => Assert.True(actor.Failed));
        Assert.DoesNotContain(run.World.Forecasts.Entries.Values, prediction => prediction.Kind == TownPredictionKind.Failure);
    }

    [Fact]
    public async Task Fold_only_wrapper_cannot_be_forecast_or_validated_until_complete_prepares_its_cache() {
        var start = TownGameFactory.Create(1);
        var history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var kernel = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Opening does not call controller."));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
        var recorded = kernel.LastCompletion!.Event;
        TownWorld folded = TownEngine.Fold(start.World, recorded.TargetInstant, Assert.Single(recorded.Facts));
        Assert.Throws<InvalidOperationException>(() => TownEngine.Forecast(folded, Rules));
        Assert.Throws<InvalidOperationException>(() => TownEngine.Validate(folded, folded.Time));
        TownWorld ready = TownEngine.Complete(start.World, recorded.TargetInstant, recorded.Facts, folded);
        AssertOracle(ready);
        Assert.Equal(history.State.Forecasts.Entries.OrderBy(pair => pair.Key), ready.Forecasts.Entries.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Inventory_goal_and_rejected_operations_reuse_unaffected_owners_without_recomputing_them() {
        var run = new Run(TownGameFactory.Create(4));
        TownWorld initial = run.World;
        await run.Act("a", "buy grain 1");
        AssertUnchanged(initial, run.World, "end", "market", "actor:a:failure", "actor:b:failure", "actor:c:failure");
        Assert.Equal(17, run.World.Business.Market.Stock["grain"]);
        Assert.Equal(new ModelTime(TownContent.Day), run.World.Business.Market.NextRefresh);
        TownWorld beforeGoal = run.World;
        await run.Act("b", "create-goal {\"title\":\"Keep food\",\"criteria\":\"Have two meals\",\"focus\":true}");
        AssertUnchanged(beforeGoal, run.World, "end", "market", "actor:a:failure", "actor:b:failure", "actor:c:failure");
        TownWorld beforeRejection = run.World;
        TownFact rejected = await run.Act("c", "buy grain 999", accepted: false);
        Assert.False(rejected.Accepted);
        Assert.Equal("c", run.World.Business.OpenDecisionActorId);
        AssertUnchanged(beforeRejection, run.World, "end", "market", "actor:a:failure", "actor:b:failure", "actor:c:failure");
        Assert.Same(beforeRejection.Forecasts.Entries["decision"], run.World.Forecasts.Entries["decision"]);
        await run.Act("c", "wait 2");
        TownWorld beforeMeal = run.World;
        await run.Act("a", "eat");
        Assert.NotSame(beforeMeal.Forecasts.Entries["actor:a:failure"], run.World.Forecasts.Entries["actor:a:failure"]);
        Assert.Contains("actor:a:failure", run.World.Forecasts.LastRecomputedOwners);
        AssertUnchanged(beforeMeal, run.World, "end", "market", "actor:b:failure", "actor:c:failure", "actor:c:activity");
        Assert.Equal(18, initial.Business.Market.Stock["grain"]);
        Assert.Equal(60, initial.Business.Character("a").Food);
    }

    [Fact]
    public async Task Offer_delivery_and_another_offers_terminal_update_do_not_recompute_active_expiry() {
        var run = new Run(TownGameFactory.Create(4));
        await run.Act("a", "offer b meal 1 money 2 3");
        TownWorld offered = run.World;
        const string owner = "trade:trade:a:1:expiry";
        Assert.Contains(owner, offered.Forecasts.Entries.Keys);
        await run.Open("b");
        AssertUnchanged(offered, run.World, owner);
        Assert.Equal(1, Assert.Single(run.World.Business.Trades.Offers).RecipientDeliveredVersion);
        await run.Act("b", "offer a money 1 meal 1 2");
        AssertUnchanged(offered, run.World, owner);
        await run.Act("c", "wait 4");
        await run.Act("a", "reject trade:b:1");
        AssertUnchanged(offered, run.World, owner);
        Assert.DoesNotContain("trade:trade:b:1:expiry", run.World.Forecasts.Entries.Keys);
        Assert.Contains("trade:trade:b:1:expiry", run.World.Forecasts.LastRecomputedOwners);
        Assert.Equal(TownTradeStatus.Active, offered.Business.Trades.Offers.Single().Status);
    }

    [Fact]
    public async Task Natural_time_decay_preserves_absolute_food_deadlines_even_with_tick_rounding() {
        var start = TownGameFactory.Create(2);
        TownBusinessState seed = start.World.Business;
        long reserve = 3 * TownContent.Hour + 1; // Exhaustion rounds up to hour 1 plus one millisecond.
        TownWorld world = Copy(seed, actors: seed.Characters.Select(actor => actor.Id == "c"
            ? actor with { FoodReserve = reserve, MinimumFoodReserve = reserve } : actor));
        var run = new Run((world, start.Cursor));
        TownPrediction aFailure = world.Forecasts.Entries["actor:a:failure"];
        TownPrediction bFailure = world.Forecasts.Entries["actor:b:failure"];
        var originals = world.Forecasts.Entries.ToDictionary(pair => pair.Key, pair => pair.Value);
        await run.Act("a", "wait 2");
        await run.Act("b", "wait 2");
        await run.Act("c", "wait 2");
        await run.Step();
        Assert.Equal(TownEventKind.Failed, run.Last.Kind);
        Assert.Equal("c", run.Last.ActorId);
        Assert.Equal(new ModelTime(TownContent.Hour + 1), run.World.Time);
        Assert.Same(aFailure, run.World.Forecasts.Entries["actor:a:failure"]);
        Assert.Same(bFailure, run.World.Forecasts.Entries["actor:b:failure"]);
        Assert.DoesNotContain("actor:a:failure", run.World.Forecasts.LastRecomputedOwners);
        Assert.DoesNotContain("actor:b:failure", run.World.Forecasts.LastRecomputedOwners);
        Assert.DoesNotContain("actor:c:failure", run.World.Forecasts.Entries.Keys);
        Assert.DoesNotContain("actor:c:activity", run.World.Forecasts.Entries.Keys);
        Assert.Equal(new ModelTime(20 * TownContent.Hour), aFailure.Due);
        Assert.Equal(originals.OrderBy(pair => pair.Key), world.Forecasts.Entries.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Same_tick_failure_then_multiple_expiries_market_activities_and_end_match_full_oracle() {
        var start = TownGameFactory.Create(1);
        TownBusinessState seed = start.World.Business;
        TownWorld world = Copy(seed,
            actors: seed.Characters.Select(actor => actor.Id == "c"
                ? actor with { FoodReserve = 3 * TownContent.Hour, MinimumFoodReserve = 3 * TownContent.Hour } : actor),
            market: TownMarket.Fresh(new(TownContent.Hour)));
        var scripts = Scripts(
            ["offer b meal 1 money 1 1", "offer b meal 1 money 1 1", "wait 1"],
            ["wait 1", "continue"], ["wait 1"]);
        var run = new Run((world, start.Cursor), actor => scripts[actor].Dequeue());
        await run.ToEnd();
        TownFact[] tied = run.Facts.Where(fact => fact.At == new ModelTime(TownContent.Hour)).ToArray();
        Assert.Equal(new[] { TownEventKind.Failed, TownEventKind.TradeExpired, TownEventKind.TradeExpired,
            TownEventKind.MarketRefreshed, TownEventKind.WaitCompleted, TownEventKind.WaitCompleted, TownEventKind.Ended }, tied.Select(fact => fact.Kind));
        Assert.Equal(new[] { "a", "b" }, tied.Where(fact => fact.Kind == TownEventKind.WaitCompleted).Select(fact => fact.ActorId).Order(StringComparer.Ordinal));
        string[] expired = tied.Where(fact => fact.Kind == TownEventKind.TradeExpired).Select(fact => fact.Result).ToArray();
        Assert.Single(expired, result => result.Contains("trade:a:1", StringComparison.Ordinal));
        Assert.Single(expired, result => result.Contains("trade:a:2", StringComparison.Ordinal));
        Assert.Empty(run.World.Forecasts.Entries);
        Assert.Empty(TownEngine.Forecast(run.World, Rules));
        Assert.All(scripts.Values, queue => Assert.Empty(queue));
    }

    [Fact]
    public async Task Arrival_at_activity_and_end_tick_is_consumed_before_activity_and_no_stale_decision_survives_end() {
        var start = TownGameFactory.Create(2);
        TownWorld world = Copy(start.World.Business, end: new(5 * TownContent.Hour / 4));
        var scripts = Scripts(["go market-workshop", "wait 1"],
            ["go market-home", "go home-workshop", "go market-workshop", "go market-home", "go market-home"], ["wait 2"]);
        var run = new Run((world, start.Cursor), actor => scripts[actor].Dequeue());
        await run.ToEnd();
        TownFact[] tied = run.Facts.Where(fact => fact.At == world.Business.EndTime).ToArray();
        Assert.Equal(new[] { TownEventKind.Arrived, TownEventKind.WaitCompleted, TownEventKind.Ended }, tied.Select(fact => fact.Kind));
        Assert.Equal("b", tied[0].ActorId);
        Assert.Equal("a", tied[1].ActorId);
        Assert.Empty(run.World.Forecasts.Entries);
    }

    [Fact]
    public async Task Head_on_contact_is_not_hidden_by_cached_town_predictions_before_unrelated_failure() {
        var start = TownGameFactory.Create(2);
        long reserve = 14 * TownContent.Hour / 10 * TownContent.FoodDecayPerHour;
        TownWorld world = Copy(start.World.Business, actors: start.World.Business.Characters.Select(actor => actor.Id == "c"
            ? actor with { FoodReserve = reserve, MinimumFoodReserve = reserve } : actor));
        var scripts = Scripts(["go market-workshop", "wait 1", "go market-workshop"],
            ["go market-home", "go home-workshop", "go market-workshop", "go market-home", "go market-home", "go market-workshop"], ["wait 2"]);
        var run = new Run((world, start.Cursor), actor => scripts[actor].TryDequeue(out string? command) ? command : "wait 2");
        await run.ToEnd();
        TownFact contact = Assert.Single(run.Facts, fact => fact.Kind == TownEventKind.ContactOccurred);
        Assert.Equal(new ModelTime(11 * TownContent.Hour / 8), contact.At);
        Assert.IsType<PassageContactOccurredFact>(Assert.Single(contact.SpatialFacts));
        TownFact failure = Assert.Single(run.Facts, fact => fact.Kind == TownEventKind.Failed);
        Assert.Equal(new ModelTime(14 * TownContent.Hour / 10), failure.At);
        Assert.True(run.Facts.IndexOf(contact) < run.Facts.IndexOf(failure));
    }

    [Fact]
    public async Task Completed_wait_reopens_a_new_current_time_decision_instead_of_reusing_old_due_time() {
        var run = new Run(TownGameFactory.Create(3));
        TownPrediction genesisDecision = run.World.Forecasts.Entries["decision"];
        await run.Act("a", "wait 1");
        await run.Act("b", "wait 2");
        await run.Act("c", "wait 2");
        Assert.DoesNotContain("decision", run.World.Forecasts.Entries.Keys);
        await run.Open("a");
        TownPrediction reopened = run.World.Forecasts.Entries["decision"];
        Assert.Equal(new ModelTime(TownContent.Hour), reopened.Due);
        Assert.NotSame(genesisDecision, reopened);
        await run.Act("a", "wait 2");
        Assert.DoesNotContain("decision", run.World.Forecasts.Entries.Keys);
    }

    private static readonly SimulationRules Rules = new(0, 1024);

    private static void AssertOracle(TownWorld world) {
        // Rebuild the spatial wrapper too: sharing a defective Contacts cache could make both
        // candidate paths miss the same occurrence and produce a false-positive comparison.
        TownWorld full = TownEngine.Rebuild(world.Business);
        Assert.Equal(full.Forecasts.Entries.OrderBy(pair => pair.Key), world.Forecasts.Entries.OrderBy(pair => pair.Key));
        Assert.Equal(TownEngine.ForecastOracle(full, Rules), TownEngine.Forecast(world, Rules));
        TownEngine.Validate(world, world.Time);
    }

    private static void AssertUnchanged(TownWorld before, TownWorld after, params string[] owners) {
        foreach (string owner in owners) {
            Assert.Same(before.Forecasts.Entries[owner], after.Forecasts.Entries[owner]);
            Assert.DoesNotContain(owner, after.Forecasts.LastRecomputedOwners);
        }
    }

    private static Dictionary<string, Queue<string>> Scripts(string[] a, string[] b, string[] c) =>
        new() { ["a"] = new(a), ["b"] = new(b), ["c"] = new(c) };

    private static TownWorld Copy(TownBusinessState seed, IEnumerable<TownCharacter>? actors = null,
        TownMarket? market = null, ModelTime? end = null) => TownEngine.Rebuild(new(seed.Time, end ?? seed.EndTime,
            actors ?? seed.Characters, market ?? seed.Market, seed.Economy, seed.Spatial, seed.LastServedActorId,
            seed.OpenDecisionActorId, seed.Generation, seed.Ended, seed.Trades));

    private sealed class Run {
        private readonly InMemoryOccurrenceHistory<TownWorld, TownFact> _history;
        private readonly SimulationKernel<TownWorld, TownPrediction, TownFact> _kernel;
        private string? _expectedActor, _command;
        private int _calls;
        public TownWorld World => _history.State;
        public List<TownFact> Facts { get; } = [];
        public TownFact Last => Facts[^1];
        public Run((TownWorld World, KernelCursor Cursor) initial, Func<string, string>? script = null) {
            _history = new(initial.World, initial.Cursor);
            _kernel = TownEngine.CreateControlledKernel(_history, (observation, _) => {
                if (script is null) { Assert.Equal(_expectedActor, observation.ActorId); Assert.NotNull(_command); }
                string command = script?.Invoke(observation.ActorId) ?? _command!;
                int call = ++_calls;
                return ValueTask.FromResult(new TownControllerOperation(command, $"prediction-tool-{call}", $"prediction-op-{call}"));
            });
            AssertOracle(World);
        }
        public async Task Step() {
            TownWorld old = World;
            var oldEntries = old.Forecasts.Entries.ToArray();
            var winner = OccurrenceScheduler.SelectWinner(TownEngine.ForecastOracle(TownEngine.Rebuild(old.Business), Rules), worldSeed: 0);
            LogicalInstant? last = _history.Cursor.LastInstant;
            var target = new LogicalInstant(winner.Due.ModelTime,
                last is { } previous && previous.ModelTime == winner.Due.ModelTime ? previous.CausalOrdinal + 1 : 0);
            Assert.Equal(StepStatus.Committed, await _kernel.StepAsync(World.Business.EndTime));
            Assert.Equal(winner.Key, _kernel.LastCompletion!.Event.CauseKey);
            Assert.Equal(target, _kernel.LastCompletion.Event.TargetInstant);
            Facts.Add(Assert.Single(_kernel.LastCompletion!.Event.Facts));
            AssertOracle(World);
            Assert.Equal(oldEntries, old.Forecasts.Entries.ToArray());
        }
        public async Task<TownFact> Act(string actor, string command, bool accepted = true) {
            _expectedActor = actor; _command = command;
            int before = _calls;
            for (int i = 0; i < 100; i++) {
                await Step();
                if (_calls != before) {
                    _expectedActor = null; _command = null;
                    Assert.Equal(accepted, Last.Accepted);
                    return Last;
                }
            }
            throw new InvalidOperationException("Expected operation was not reached.");
        }
        public async Task Open(string actor) {
            for (int i = 0; i < 100; i++) {
                if (World.Business.OpenDecisionActorId is { } open) { Assert.Equal(actor, open); return; }
                await Step();
            }
            Assert.Fail("Expected opening was not reached.");
        }
        public async Task ToEnd() {
            for (int i = 0; i < 250 && !World.Business.Ended; i++) { await Step(); }
            Assert.True(World.Business.Ended);
        }
    }
}

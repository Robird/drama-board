using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown.Tests;

public sealed class TownSimulationTests {
    [Fact]
    public void InvalidInventoryMarketAndActivityAreRejectedBeforeRuntime() {
        TownBusinessState state = TownGameFactory.Create().World.Business;
        TownCharacter unknown = state.Character("a") with { Inventory = state.Character("a").Inventory.Add("unknown") };
        Assert.Throws<InvalidOperationException>(() => TownEngine.Rebuild(Copy(state,
            characters: state.Characters.Select(actor => actor.Id == "a" ? unknown : actor))));

        var incomplete = new TownMarket(new Dictionary<string, int> { ["grain"] = 18 }, state.Market.Demand, state.Market.NextRefresh, 0);
        Assert.Throws<InvalidOperationException>(() => TownEngine.Rebuild(Copy(state, market: incomplete)));

        TownCharacter invalid = state.Character("a") with { NextActivityId = 2,
            Activity = new TownActivity(1, TownActivityKind.Produce, new ModelTime(TownContent.Hour), "missing", 1, "grain", 2) };
        Assert.Throws<InvalidOperationException>(() => TownEngine.Rebuild(Copy(state,
            characters: state.Characters.Select(actor => actor.Id == "a" ? invalid : actor))));
    }

    [Fact]
    public async Task ThreeResidentsCompleteTwoDayEconomyWithFoodAndMarketRefresh() {
        RunResult run = await Run(EconomicScript());
        TownBusinessState final = run.History.State.Business;

        Assert.True(final.Ended);
        Assert.Equal(48 * TownContent.Hour, final.Time.Ticks);
        Assert.All(final.Characters, actor => { Assert.False(actor.Failed); Assert.True(actor.Food > 0); });
        Assert.Contains(run.Facts, fact => fact.Kind == TownEventKind.MarketRefreshed && fact.At.Ticks == TownContent.Day);
        Assert.Equal(60, final.Economy.ImportExpense);
        Assert.Equal(84, final.Economy.ExportIncome);
        Assert.Equal(78, final.Characters.Sum(actor => actor.Money));
        Assert.Equal(12, final.Economy.Produced["meal"]);
        Assert.Equal(6, final.Economy.Produced["woodware"]);
        Assert.Equal(6, final.Economy.Exported["woodware"]);
        Assert.Equal(3, final.Economy.Exported["meal"]);
        Assert.DoesNotContain(run.Facts, fact => fact.Kind == TownEventKind.Operation && !fact.Accepted);
        foreach (TownFact fact in run.Facts) { VerifyAccounting(fact.NextBusiness); }
    }

    [Fact]
    public async Task RejectionKeepsActorAndSuccessfulInstantActionsRotate() {
        var seen = new List<string>();
        var counts = new Dictionary<string, int>();
        RunResult run = await Run(decision => {
            seen.Add(decision.ActorId);
            counts[decision.ActorId] = counts.GetValueOrDefault(decision.ActorId) + 1;
            return counts[decision.ActorId] switch {
                1 when seen.Count == 1 => "buy grain 1000",
                <= 2 when decision.ActorId == "a" => "buy grain 1",
                1 => "buy grain 1",
                _ => "wait 1"
            };
        }, durationHours: 1);

        Assert.Equal(new[] { "a", "a", "b", "c" }, seen.Take(4));
        TownFact rejected = Assert.Single(run.Facts, fact => !fact.Accepted);
        Assert.Equal("a", rejected.NextBusiness.OpenDecisionActorId);
        Assert.Equal(18, rejected.NextBusiness.Character("a").Money);
        Assert.Equal(0, rejected.NextBusiness.Character("a").Inventory.Count("grain"));
    }

    [Fact]
    public async Task LastMarketUnitHasOneBuyerAndLosingPurchasesAreAtomic() {
        var created = TownGameFactory.Create(1);
        TownBusinessState initial = created.World.Business;
        var market = new TownMarket(new Dictionary<string, int> { ["grain"] = 1, ["wood"] = 18 },
            initial.Market.Demand, initial.Market.NextRefresh, 0);
        TownWorld world = TownEngine.Rebuild(Copy(initial, market: market));
        var tried = new HashSet<string>();
        RunResult run = await Run(decision => tried.Add(decision.ActorId) ? "buy grain 1" : "wait 1",
            initial: (world, created.Cursor));

        TownBusinessState final = run.History.State.Business;
        Assert.Equal(0, final.Market.Stock["grain"]);
        Assert.Equal(1, final.Characters.Sum(actor => actor.Inventory.Count("grain")));
        Assert.Equal(2, final.Economy.ImportExpense);
        Assert.Equal(2, run.Facts.Count(fact => !fact.Accepted));
        Assert.All(final.Characters.Where(actor => actor.Inventory.Count("grain") == 0), actor => Assert.Equal(18, actor.Money));
        VerifyAccounting(final);
    }

    [Fact]
    public async Task EndBoundaryKeepsUnfinishedProductionAsWorkInProgress() {
        RunResult run = await Run(Queues(new[] { "buy wood 2", "go market-workshop", "produce woodware 1" }), durationHours: 1);
        TownBusinessState final = run.History.State.Business;

        Assert.True(final.Ended);
        Assert.All(final.Characters, actor => {
            Assert.NotNull(actor.Activity);
            Assert.Equal(TownActivityKind.Produce, actor.Activity.Kind);
            Assert.Equal(2, actor.Activity.InputCount);
            Assert.Equal(0, actor.Inventory.Count("wood"));
            Assert.Equal(0, actor.Inventory.Count("woodware"));
        });
        Assert.Empty(final.Economy.Produced);
        Assert.Empty(final.Economy.RecipeInputs);
        VerifyAccounting(final);
    }

    [Fact]
    public async Task FailedResidentsRemainInPopulationAndWorstOffMetrics() {
        RunResult run = await Run(_ => "wait 48");
        TownBusinessState final = run.History.State.Business;

        Assert.Equal(3, final.Characters.Count);
        Assert.All(final.Characters, actor => { Assert.True(actor.Failed); Assert.Equal(0, actor.Food); Assert.Null(actor.Activity); });
        Assert.True(final.WorstOff.Failed);
        Assert.Equal(0, final.WorstOff.MinimumFood);
        Assert.Equal(3, run.Facts.Count(fact => fact.Kind == TownEventKind.Failed));
        Assert.Equal(54, final.Characters.Sum(actor => actor.Money));
        VerifyAccounting(final);
    }

    [Fact]
    public async Task FailureReturnsReservedInputsWithoutCreatingFinishedGoods() {
        var created = TownGameFactory.Create(2);
        TownBusinessState initial = created.World.Business;
        TownCharacter[] hungry = initial.Characters.Select(actor => actor with {
            FoodReserve = 3 * TownContent.Hour, MinimumFoodReserve = 3 * TownContent.Hour
        }).ToArray();
        TownWorld world = TownEngine.Rebuild(Copy(initial, characters: hungry));
        RunResult run = await Run(Queues(new[] { "buy wood 2", "go market-workshop", "produce woodware 1" }), initial: (world, created.Cursor));

        Assert.All(run.History.State.Business.Characters, actor => {
            Assert.True(actor.Failed);
            Assert.Null(actor.Activity);
            Assert.Equal(2, actor.Inventory.Count("wood"));
            Assert.Equal(0, actor.Inventory.Count("woodware"));
        });
        Assert.Empty(run.History.State.Business.Economy.Produced);
        VerifyAccounting(run.History.State.Business);
    }

    [Fact]
    public async Task GoalCommandsEditOnlyTheActingResidentsGoalGraph() {
        bool created = false;
        RunResult run = await Run(decision => {
            if (decision.ActorId == "a" && !created) {
                created = true;
                return "create-goal {\"title\":\"储存三份饭\",\"criteria\":\"背包有三份饭\",\"focus\":true}";
            }
            return "wait 1";
        }, durationHours: 1);

        Assert.Single(run.History.State.Business.Character("a").Goals.Goals);
        Assert.Empty(run.History.State.Business.Character("b").Goals.Goals);
        Assert.Empty(run.History.State.Business.Character("c").Goals.Goals);
        Assert.Empty(run.Initial.Business.Character("a").Goals.Goals);
        Assert.Contains(run.Facts, fact => fact.GoalReceipt is not null && fact.Accepted);
    }

    [Fact]
    public async Task PublishedDecisionExistsBeforeControllerAndOldSnapshotsStayUnchanged() {
        RunResult run = await Run(EconomicScript());

        Assert.Equal(ModelTime.Zero, run.Initial.Time);
        Assert.Equal(18, run.Initial.Business.Market.Stock["grain"]);
        Assert.Equal(0, run.Initial.Business.Economy.ImportExpense);
        Assert.All(run.Initial.Business.Characters, actor => {
            Assert.Equal(18, actor.Money); Assert.Equal(1, actor.Inventory.Count("meal")); Assert.Null(actor.Activity);
        });
        foreach (TownFact fact in run.Facts) {
            fact.NextBusiness.Validate();
            VerifyAccounting(fact.NextBusiness);
        }
        Assert.Equal(run.Decisions, run.Facts.Count(fact => fact.Kind == TownEventKind.DecisionOpened));
    }

    [Fact]
    public async Task IdenticalScriptsYieldIdenticalOccurrenceOrderAndFinalEconomy() {
        RunResult first = await Run(EconomicScript());
        RunResult second = await Run(EconomicScript());

        Assert.Equal(first.History.Cursor, second.History.Cursor);
        Assert.Equal(first.History.CompletedEvents.Select(occurrence => occurrence.CauseKey),
            second.History.CompletedEvents.Select(occurrence => occurrence.CauseKey));
        Assert.Equal(first.Facts.Select(Signature), second.Facts.Select(Signature));
    }

    private static Func<TownDecision, string> EconomicScript() {
        var first = new Dictionary<string, Queue<string>>();
        var second = new Dictionary<string, Queue<string>>();
        return decision => {
            string id = decision.ActorId;
            if (!first.ContainsKey(id)) {
                first[id] = new(new[] { "buy grain 4", "buy wood 2", "go market-workshop", "produce meal 2",
                    "produce woodware 1", "go market-workshop", "sell woodware 1", "sell meal 1" });
                second[id] = new(new[] { "buy wood 2", "go market-workshop", "produce woodware 1", "go market-workshop", "sell woodware 1" });
            }
            if (decision.Character.Food <= 40 && decision.Character.Inventory.Count("meal") > 0) { return "eat"; }
            if (first[id].TryDequeue(out string? command)) { return command; }
            if (decision.World.Time.Ticks >= TownContent.Day && second[id].TryDequeue(out command)) { return command; }
            return "wait 1";
        };
    }

    private static Func<TownDecision, string> Queues(string[] commands) {
        var queues = new Dictionary<string, Queue<string>>();
        return decision => {
            if (!queues.TryGetValue(decision.ActorId, out Queue<string>? queue)) { queues[decision.ActorId] = queue = new(commands); }
            return queue.TryDequeue(out string? command) ? command : "wait 1";
        };
    }

    private static async Task<RunResult> Run(Func<TownDecision, string> script, int durationHours = 48,
        (TownWorld World, KernelCursor Cursor)? initial = null) {
        var start = initial ?? TownGameFactory.Create(durationHours);
        var history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        int decisions = 0;
        var kernel = TownEngine.CreateKernel(history, (decision, _) => {
            decisions++;
            Assert.Same(history.State, decision.World);
            Assert.Equal(decision.ActorId, history.State.Business.OpenDecisionActorId);
            Assert.True(history.State.Business.Character(decision.ActorId).DecisionSequence > 0);
            Assert.Null(history.PendingEvent);
            return ValueTask.FromResult(TownCommand.Parse(script(decision)));
        });
        for (int step = 0; step < 4000; step++) {
            if (history.State.Business.Ended) { return new(start.World, history, decisions); }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(start.World.Business.EndTime));
        }
        throw new InvalidOperationException("The finite town script did not reach its endpoint within 4000 occurrences.");
    }

    private static TownBusinessState Copy(TownBusinessState before, IEnumerable<TownCharacter>? characters = null,
        TownMarket? market = null) => new(before.Time, before.EndTime, characters ?? before.Characters,
            market ?? before.Market, before.Economy, before.Spatial, before.LastServedActorId,
            before.OpenDecisionActorId, before.Generation, before.Ended);

    // Independent test-side conservation equations also count ingredients held outside inventory in unfinished work.
    private static void VerifyAccounting(TownBusinessState state) {
        TownEconomicTotals totals = state.Economy;
        Assert.Equal(totals.InitialMoney + totals.ExportIncome - totals.ImportExpense, state.Characters.Sum(actor => actor.Money));
        foreach (string item in TownContent.Items) {
            long expected = totals.InitialItems.GetValueOrDefault(item) + totals.Imported.GetValueOrDefault(item)
                + totals.Produced.GetValueOrDefault(item) - totals.Exported.GetValueOrDefault(item)
                - totals.Consumed.GetValueOrDefault(item) - totals.RecipeInputs.GetValueOrDefault(item);
            long held = state.Characters.Sum(actor => (long)actor.Inventory.Count(item)
                + (actor.Activity?.InputItem == item ? actor.Activity.InputCount : 0));
            Assert.Equal(expected, held);
        }
    }

    private static string Signature(TownFact fact) => $"{fact.Kind}|{fact.ActorId}|{fact.At.Ticks}|{fact.Accepted}|{fact.Result}|" +
        string.Join(";", fact.NextBusiness.Characters.Select(actor => $"{actor.Id}:{actor.Money}:{actor.FoodReserve}:" +
            string.Join(",", TownContent.Items.Select(item => $"{item}={actor.Inventory.Count(item)}"))));

    private sealed record RunResult(TownWorld Initial, InMemoryOccurrenceHistory<TownWorld, TownFact> History, int Decisions) {
        public IReadOnlyList<TownFact> Facts => History.CompletedEvents.SelectMany(occurrence => occurrence.Facts).ToArray();
    }
}

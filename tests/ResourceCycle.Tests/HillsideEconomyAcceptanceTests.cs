using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Independent acceptance through character-scoped commands and the publishing kernel.</summary>
public sealed class HillsideEconomyAcceptanceTests {
    [Fact]
    public async Task A_resident_processes_mountain_inputs_and_pays_for_the_next_trip_without_resetting_on_cold_open() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1);
        Assert.Equal(12, initial.World.GetCharacter("a").Money);
        Queue<CycleCommand> route = new([
            Go("home", "workshop"), CycleCommand.Produce("ration"), CycleCommand.Use("item.ration"), Go("workshop", "home"),
            Go("home", "market"), Go("market", "road"), Go("road", "camp"), Go("camp", "stream"),
            CycleCommand.Gather("berries", 2), CycleCommand.Gather("water", 2), Go("stream", "cabin"),
            // The long return route would expose the resident overnight. Rest in the cabin;
            // ordinary warning decisions consume carried supplies without cancelling this wait.
            CycleCommand.Gather("wood", 2), CycleCommand.Wait(13), Go("cabin", "stream"), Go("stream", "camp"),
            Go("camp", "road"), Go("road", "market"), Go("market", "workshop"),
            CycleCommand.Produce("woodware"), Go("workshop", "market"), CycleCommand.Sell("item.woodware"),
            CycleCommand.Buy("item.food"), Go("market", "home")
        ]);
        var controller = new Script("journey", context => {
            CycleObservation observation = context.Observation;
            if (observation.Survival!.Water <= 55 && observation.Actor.Backpack.Count("item.water") > 0) {
                return CycleCommand.Use("item.water");
            }
            if (observation.Survival.Food <= 45 && observation.Actor.Backpack.Count("item.ration") > 0) {
                return CycleCommand.Use("item.ration");
            }
            if (observation.Survival.Food <= 65 && observation.Actor.Backpack.Count("item.food") > 0) {
                return CycleCommand.Use("item.food");
            }
            if (observation.Actor.Activity is not null || observation.ArrivalDue is not null) { return CycleCommand.Continue(); }
            return route.Dequeue();
        });
        string state, economy, materials;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = CycleEngine.CreateKernel(history, characterController: controller);
            await UntilState(kernel, world => route.Count == 0 && CycleActions.CurrentPlace(world, "a") == "home");
            Assert.True(kernel.World.GetCharacter("a").IsActive);
            Assert.Equal(23, kernel.World.GetCharacter("a").Money);
            CycleEconomicTotals totals = kernel.World.Business.Economy!.Totals;
            Assert.Equal(2, totals.Harvested["item.wood"]);
            Assert.Equal(2, totals.Harvested["item.food"]);
            Assert.Equal(2, totals.Harvested["item.water"]);
            Assert.Equal(2, totals.RecipeInputs["item.wood"]);
            Assert.Equal(2, totals.RecipeInputs["item.food"]);
            Assert.Equal(1, totals.Produced["item.woodware"]);
            Assert.Equal(2, totals.Produced["item.ration"]);
            Assert.Equal(1, totals.Exported["item.woodware"]);
            Assert.Equal(1, totals.Imported["item.food"]);
            Assert.True(totals.Consumed.GetValueOrDefault("item.ration") > 0);
            Assert.Equal(14, totals.ExportIncome);
            Assert.Equal(3, totals.ImportExpense);
            Assert.Contains(controller.Views, view => view.Recipes.Any(recipe => recipe.Id == "ration"));
            Assert.Contains(controller.Views, view => view.MarketQuotes.Any(quote => quote.ItemId == "item.woodware"));
            state = kernel.World.DescribeState();
            economy = MaterialSnapshot(kernel.World.Business.Economy);
            materials = MaterialSnapshot(kernel.World.Business.InteractionStates);
        }
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(state, history.State.DescribeState());
            Assert.Equal(economy, MaterialSnapshot(history.State.Business.Economy));
            Assert.Equal(materials, MaterialSnapshot(history.State.Business.InteractionStates));
            var kernel = CycleEngine.CreateKernel(history, characterController: new Script("next-day", _ => CycleCommand.Wait(1)));
            Assert.False(kernel.RecoverPending());
            await UntilState(kernel, world => world.GetCharacter("a").Activity is { Kind: CycleActivityKind.Wait });
            Assert.Equal(economy, MaterialSnapshot(kernel.World.Business.Economy));
            Assert.Equal(23, kernel.World.GetCharacter("a").Money);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_start_and_completion_event_only_recovery_never_replans_or_settles_twice(bool completion) {
        using var store = new TemporaryStore();
        CycleWorld initial = CycleGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false).World;
        bool Reached(CycleWorld world) => completion
            ? world.GetCharacter("a").Backpack.Count("item.ration") == 2
            : world.GetCharacter("a").Activity?.Kind == CycleActivityKind.Produce;
        bool Matches(CycleFact fact) => fact.ChangedCharacters.Any(actor => completion
            ? actor.Backpack.Count("item.ration") == 2 && actor.Activity is null
            : actor.Activity?.Kind == CycleActivityKind.Produce);
        Script Producer() {
            Queue<CycleCommand> commands = new([Go("home", "workshop"), CycleCommand.Produce("ration")]);
            return new("producer", _ => commands.Dequeue());
        }
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: Producer());
        await UntilState(baseline, Reached);
        KernelCursor before = default;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = CycleEngine.CreateKernel(new StopAfterEvent(history, Matches), characterController: Producer());
            for (int step = 0; step < 60 && !kernel.IsFaulted; step++) {
                before = history.Cursor;
                try { await Advance(kernel); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
            }
            Assert.True(kernel.IsFaulted);
            Assert.False(Reached(history.State));
            Assert.NotNull(history.PendingEvent);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(history.PendingEvent));
        }
        string settled;
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(before, history.Cursor);
            var recovery = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(history,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
                (world, at, facts, folded) => CycleEngine.Complete(world, at, facts, folded, PredictionMode.Incremental), CycleEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(baseline.Cursor, recovery.Cursor);
            Assert.Equal(MaterialSnapshot(baseline.World.Business), MaterialSnapshot(recovery.World.Business));
            AssertAccounting(recovery.World);
            if (!completion) {
                var resumed = CycleEngine.CreateKernel(history, characterController: new ForbiddenController());
                await UntilState(resumed, world => world.GetCharacter("a").Backpack.Count("item.ration") == 2);
            }
            Assert.Equal(2, history.State.Business.Economy!.Totals.Produced["item.ration"]);
            Assert.Equal(2, history.State.Business.Economy.Totals.RecipeInputs["item.food"]);
            Assert.Equal(1, history.State.GetCharacter("a").Backpack.Count("item.food"));
            settled = MaterialSnapshot(history.State.Business);
        }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.False(CycleEngine.CreateKernel(reopened, characterController: new ForbiddenController()).RecoverPending());
        Assert.Equal(settled, MaterialSnapshot(reopened.State.Business));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gift_that_fits_output_but_blocks_refund_is_rejected_without_disturbing_production(bool cancel) {
        CycleCharacter a = new("a", "Producer", backpack: new ItemInventory(
            new Dictionary<string, int> { ["item.wood"] = 2, ["item.food"] = 2 }, unitCapacity: 4), money: 12);
        CycleCharacter b = new("b", "Neighbour", backpack: new ItemInventory(new Dictionary<string, int> { ["item.water"] = 1 }), money: 12);
        CycleWorld initial = AtPlace("workshop", [a, b]);
        bool rejected = false, started = false, answered = false;
        int bTurn = 0;
        var controller = new Script("capacity", context => {
            if (context.Observation.Actor.Id == "a") {
                if (!started) { started = true; return CycleCommand.Produce("woodware"); }
                Assert.NotNull(context.Observation.Actor.Activity);
                Assert.Equal(Hour(10), context.Observation.Actor.Activity.Due);
                answered = true;
                return cancel ? CycleCommand.Cancel() : CycleCommand.Continue();
            }
            switch (++bTurn) {
                case 1:
                    Assert.NotNull(context.Check(CycleCommand.Give("a", "item.water", 1)));
                    rejected = true;
                    return CycleCommand.Give("a", "item.water", 1);
                case 2: return CycleCommand.Say("a", "Please check your workshop order.");
                default: return CycleCommand.Wait(4);
            }
        }, allowRejected: true);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilState(kernel, world => answered && world.GetCharacter("a").Activity is null);
        Assert.True(rejected);
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(2, kernel.World.GetCharacter("a").Backpack.Count("item.food"));
        Assert.Equal(cancel ? 2 : 0, kernel.World.GetCharacter("a").Backpack.Count("item.wood"));
        Assert.Equal(cancel ? 0 : 1, kernel.World.GetCharacter("a").Backpack.Count("item.woodware"));
        Assert.Equal(cancel ? Hour(8) : Hour(10), kernel.World.Time);
        Assert.Equal(cancel ? 0 : 2, kernel.World.Business.Economy!.Totals.RecipeInputs.GetValueOrDefault("item.wood"));
    }

    [Fact]
    public async Task Three_residents_compete_for_one_market_unit_then_observe_refresh_before_same_time_purchases() {
        CycleCharacter[] residents = [new("a", "A", money: 12), new("b", "B", money: 12), new("c", "C", money: 12)];
        var stock = new Dictionary<string, int>(LivingWorldEconomyContent.MarketStock) { ["item.water"] = 1 };
        CycleWorld initial = AtPlace("market", residents, new CycleMarket(stock, LivingWorldEconomyContent.MarketDemand, Hour(9), 0));
        Dictionary<string, int> turns = [];
        List<(string Actor, ModelTime At, int Stock, bool Legal)> attempts = [];
        var controller = new Script("market", context => {
            string actor = context.Observation.Actor.Id;
            int turn = turns.GetValueOrDefault(actor) + 1;
            turns[actor] = turn;
            if (turn == 2 || turn >= 4) { return CycleCommand.Wait(1); }
            CycleCommand command = CycleCommand.Buy("item.water");
            int visibleStock = Assert.Single(context.Observation.MarketQuotes, quote => quote.ItemId == "item.water").Stock;
            attempts.Add((actor, context.Observation.Time, visibleStock, context.Check(command) is null));
            return command;
        }, allowRejected: true);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilState(kernel, world => world.Business.Economy!.Totals.Imported.GetValueOrDefault("item.water") == 4);
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(1, kernel.World.Business.Economy!.Market.Cycle);
        Assert.Equal(57, kernel.World.Business.Economy.Market.Stock["item.water"]);
        Assert.Equal(4, kernel.World.Business.Economy.Totals.ImportExpense);
        Assert.Equal(32, kernel.World.Characters.Sum(actor => actor.Money));
        Assert.Equal(3, attempts.Count(attempt => attempt.At == Hour(8)));
        Assert.Single(attempts, attempt => attempt.At == Hour(8) && attempt.Legal);
        Assert.Equal(new[] { 60, 59, 58 }, attempts.Where(attempt => attempt.At == Hour(9)).Select(attempt => attempt.Stock));
        Assert.All(kernel.World.Characters, actor => Assert.True(actor.Backpack.Count("item.water") >= 1));
    }

    [Fact]
    public async Task Cash_gift_and_rejected_overpayment_preserve_the_recipients_busy_commitment_and_external_flow_ledger() {
        CycleCharacter a = new("a", "Producer", backpack: new ItemInventory(new Dictionary<string, int> { ["item.food"] = 2 }), money: 12);
        CycleCharacter b = new("b", "Neighbour", money: 12);
        CycleWorld initial = AtPlace("workshop", [a, b]);
        string totals = MaterialSnapshot(initial.Business.Economy!.Totals);
        bool started = false, responded = false, overpayment = false;
        int bTurn = 0;
        var controller = new Script("money", context => {
            if (context.Observation.Actor.Id == "a") {
                if (!started) { started = true; return CycleCommand.Produce("ration"); }
                Assert.Equal(17, context.Observation.Actor.Money);
                Assert.Equal(Hour(9), context.Observation.Actor.Activity!.Due);
                responded = true;
                return CycleCommand.Continue();
            }
            if (++bTurn == 1) { return CycleCommand.GiveMoney("a", 5); }
            if (bTurn == 2) {
                Assert.NotNull(context.Check(CycleCommand.GiveMoney("a", 100)));
                overpayment = true;
                return CycleCommand.GiveMoney("a", 100);
            }
            return CycleCommand.Wait(2);
        }, allowRejected: true);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilState(kernel, _ => responded && overpayment);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(17, kernel.World.GetCharacter("a").Money);
        Assert.Equal(7, kernel.World.GetCharacter("b").Money);
        Assert.Equal(Hour(9), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Equal(2, kernel.World.GetCharacter("a").Activity!.Production!.InputCount);
        Assert.Equal(totals, MaterialSnapshot(kernel.World.Business.Economy!.Totals));
    }

    private static CycleWorld AtPlace(string place, CycleCharacter[] residents, CycleMarket? market = null) {
        CycleWorld basis = CycleGameFactory.CreateLivingWorld(characterCount: residents.Length, enableNeeds: false).World;
        var totals = new CycleEconomicTotals(residents.Sum(actor => actor.Money), residents.SelectMany(actor => actor.Backpack.Items)
            .GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Value), StringComparer.Ordinal));
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, residents.Select(actor => new EntityPlacement(new(actor.Id), new(place))));
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            residents.Select(actor => new CycleVisit(actor.Id, place, basis.Time,
                basis.Resources.Where(resource => CycleExpeditionMap.GetResourcePlace(resource.Id) == place))));
        return CycleEngine.Rebuild(basis.Business.Replace(basis.Time, changedCharacters: residents, expedition: expedition,
            economy: new CycleEconomyState(market ?? CycleMarket.Fresh(Hour(24)), totals)), basis.Time);
    }

    private static CycleCommand Go(string from, string to) => CycleCommand.Go(CycleExpeditionMap.GetPassages(from).Single(p => p.To == to).Id);

    private static async Task UntilState(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> reached) {
        for (int step = 0; step < 500; step++) {
            if (reached(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The ordinary-command scenario did not reach its finite acceptance boundary.");
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(72)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        AssertAccounting(kernel.World);
    }

    // Compute the equations here instead of accepting only the production verifier's verdict.
    private static void AssertAccounting(CycleWorld world) {
        CycleEconomicTotals totals = world.Business.Economy!.Totals;
        Assert.Equal(totals.InitialMoney + totals.ExportIncome - totals.ImportExpense, world.Characters.Sum(actor => actor.Money));
        string[] items = ["item.food", "item.water", "item.wood", "item.ration", "item.woodware"];
        foreach (string item in items) {
            long held = world.Characters.Sum(actor => (long)actor.Backpack.Count(item) +
                (actor.Activity?.Production is { } order && order.InputItem == item ? order.InputCount : 0));
            long supplied = totals.InitialItems.GetValueOrDefault(item) + totals.Imported.GetValueOrDefault(item) +
                totals.Harvested.GetValueOrDefault(item) + totals.Produced.GetValueOrDefault(item);
            long removed = totals.Exported.GetValueOrDefault(item) + totals.Consumed.GetValueOrDefault(item) + totals.RecipeInputs.GetValueOrDefault(item);
            Assert.Equal(supplied - removed, held);
        }
    }

    private sealed class Script(string session, Func<CycleDecisionContext, CycleCommand> choose, bool allowRejected = false) : ICycleCharacterController {
        private int _calls;
        public List<CycleObservation> Views { get; } = [];
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Views.Add(context.Observation);
            CycleCommand command = choose(context);
            if (!allowRejected) { Assert.Null(context.Check(command)); }
            int ordinal = ++_calls;
            return ValueTask.FromResult(new CycleControllerOperation($"{session}/call/{ordinal}", CycleCommandParser.Format(command), $"{session}/operation/{ordinal}"));
        }
    }

    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A committed production must settle without asking a new controller.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Event-only recovery must not forecast.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world, OccurrenceCandidate<CyclePrediction> winner,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Event-only recovery must not re-plan.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner, Func<CycleFact, bool> matches)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(matches)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

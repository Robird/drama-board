using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

/// <summary>Independent acceptance through character-scoped commands and the publishing kernel.</summary>
public sealed class HillsideEconomyAcceptanceTests {
    [Fact]
    public async Task A_resident_processes_mountain_inputs_and_pays_for_the_next_trip_without_resetting_on_cold_open() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1);
        Assert.Equal(12, initial.World.GetCharacter("a").Money);
        Queue<PlaygroundCommand> route = new([
            Go("home", "workshop"), PlaygroundCommand.Produce("ration"), PlaygroundCommand.Use("item.ration"), Go("workshop", "home"),
            Go("home", "market"), Go("market", "road"), Go("road", "camp"), Go("camp", "stream"),
            PlaygroundCommand.Gather("berries", 2), PlaygroundCommand.Gather("water", 2), Go("stream", "cabin"),
            // The long return route would expose the resident overnight. Rest in the cabin;
            // ordinary warning decisions consume carried supplies without cancelling this wait.
            PlaygroundCommand.Gather("wood", 2), PlaygroundCommand.Wait(13), Go("cabin", "stream"), Go("stream", "camp"),
            Go("camp", "road"), Go("road", "market"), Go("market", "workshop"),
            PlaygroundCommand.Produce("woodware"), Go("workshop", "market"), PlaygroundCommand.Sell("item.woodware"),
            PlaygroundCommand.Buy("item.food"), Go("market", "home")
        ]);
        var controller = new Script("journey", context => {
            PlaygroundObservation observation = context.Observation;
            if (observation.Survival!.Water <= 55 && observation.Actor.Backpack.Count("item.water") > 0) {
                return PlaygroundCommand.Use("item.water");
            }
            if (observation.Survival.Food <= 45 && observation.Actor.Backpack.Count("item.ration") > 0) {
                return PlaygroundCommand.Use("item.ration");
            }
            if (observation.Survival.Food <= 65 && observation.Actor.Backpack.Count("item.food") > 0) {
                return PlaygroundCommand.Use("item.food");
            }
            if (observation.Actor.Activity is not null || observation.ArrivalDue is not null) { return PlaygroundCommand.Continue(); }
            return route.Dequeue();
        });
        string state, economy, materials;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
            await UntilState(kernel, world => route.Count == 0 && PlaygroundActions.CurrentPlace(world, "a") == "home");
            Assert.True(kernel.World.GetCharacter("a").IsActive);
            Assert.Equal(23, kernel.World.GetCharacter("a").Money);
            PlaygroundEconomicTotals totals = kernel.World.Business.Economy!.Totals;
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
        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(state, history.State.DescribeState());
            Assert.Equal(economy, MaterialSnapshot(history.State.Business.Economy));
            Assert.Equal(materials, MaterialSnapshot(history.State.Business.InteractionStates));
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: new Script("next-day", _ => PlaygroundCommand.Wait(1)));
            Assert.False(kernel.RecoverPending());
            await UntilState(kernel, world => world.GetCharacter("a").Activity is { Kind: PlaygroundActivityKind.Wait });
            Assert.Equal(economy, MaterialSnapshot(kernel.World.Business.Economy));
            Assert.Equal(23, kernel.World.GetCharacter("a").Money);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_start_and_completion_event_only_recovery_never_replans_or_settles_twice(bool completion) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false).World;
        bool Reached(PlaygroundWorld world) => completion
            ? world.GetCharacter("a").Backpack.Count("item.ration") == 2
            : world.GetCharacter("a").Activity?.Kind == PlaygroundActivityKind.Produce;
        bool Matches(PlaygroundFact fact) => fact.ChangedCharacters.Any(actor => completion
            ? actor.Backpack.Count("item.ration") == 2 && actor.Activity is null
            : actor.Activity?.Kind == PlaygroundActivityKind.Produce);
        Script Producer() {
            Queue<PlaygroundCommand> commands = new([Go("home", "workshop"), PlaygroundCommand.Produce("ration")]);
            return new("producer", _ => commands.Dequeue());
        }
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Producer());
        await UntilState(baseline, Reached);
        KernelCursor before = default;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = PlaygroundEngine.CreateKernel(new StopAfterEvent(history, Matches), characterController: Producer());
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
        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(before, history.Cursor);
            var recovery = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(history,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (world, at, facts, folded) => PlaygroundEngine.Complete(world, at, facts, folded, PredictionMode.Incremental), PlaygroundEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(baseline.Cursor, recovery.Cursor);
            Assert.Equal(MaterialSnapshot(baseline.World.Business), MaterialSnapshot(recovery.World.Business));
            AssertAccounting(recovery.World);
            if (!completion) {
                var resumed = PlaygroundEngine.CreateKernel(history, characterController: new ForbiddenController());
                await UntilState(resumed, world => world.GetCharacter("a").Backpack.Count("item.ration") == 2);
            }
            Assert.Equal(2, history.State.Business.Economy!.Totals.Produced["item.ration"]);
            Assert.Equal(2, history.State.Business.Economy.Totals.RecipeInputs["item.food"]);
            Assert.Equal(1, history.State.GetCharacter("a").Backpack.Count("item.food"));
            settled = MaterialSnapshot(history.State.Business);
        }
        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.False(PlaygroundEngine.CreateKernel(reopened, characterController: new ForbiddenController()).RecoverPending());
        Assert.Equal(settled, MaterialSnapshot(reopened.State.Business));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gift_that_fits_output_but_blocks_refund_is_rejected_without_disturbing_production(bool cancel) {
        PlaygroundCharacter a = new("a", "Producer", backpack: new ItemInventory(
            new Dictionary<string, int> { ["item.wood"] = 2, ["item.food"] = 2 }, unitCapacity: 4), money: 12);
        PlaygroundCharacter b = new("b", "Neighbour", backpack: new ItemInventory(new Dictionary<string, int> { ["item.water"] = 1 }), money: 12);
        PlaygroundWorld initial = AtPlace("workshop", [a, b]);
        bool rejected = false, started = false, answered = false;
        int bTurn = 0;
        var controller = new Script("capacity", context => {
            if (context.Observation.Actor.Id == "a") {
                if (!started) { started = true; return PlaygroundCommand.Produce("woodware"); }
                Assert.NotNull(context.Observation.Actor.Activity);
                Assert.Equal(Hour(10), context.Observation.Actor.Activity.Due);
                answered = true;
                return cancel ? PlaygroundCommand.Cancel() : PlaygroundCommand.Continue();
            }
            switch (++bTurn) {
                case 1:
                    Assert.NotNull(context.Check(PlaygroundCommand.Give("a", "item.water", 1)));
                    rejected = true;
                    return PlaygroundCommand.Give("a", "item.water", 1);
                case 2: return PlaygroundCommand.Say("a", "Please check your workshop order.");
                default: return PlaygroundCommand.Wait(4);
            }
        }, allowRejected: true);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: controller);
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
        PlaygroundCharacter[] residents = [new("a", "A", money: 12), new("b", "B", money: 12), new("c", "C", money: 12)];
        var stock = new Dictionary<string, int>(LivingWorldEconomyContent.MarketStock) { ["item.water"] = 1 };
        PlaygroundWorld initial = AtPlace("market", residents, new PlaygroundMarket(stock, LivingWorldEconomyContent.MarketDemand, Hour(9), 0));
        Dictionary<string, int> turns = [];
        List<(string Actor, ModelTime At, int Stock, bool Legal)> attempts = [];
        var controller = new Script("market", context => {
            string actor = context.Observation.Actor.Id;
            int turn = turns.GetValueOrDefault(actor) + 1;
            turns[actor] = turn;
            if (turn == 2 || turn >= 4) { return PlaygroundCommand.Wait(1); }
            PlaygroundCommand command = PlaygroundCommand.Buy("item.water");
            int visibleStock = Assert.Single(context.Observation.MarketQuotes, quote => quote.ItemId == "item.water").Stock;
            attempts.Add((actor, context.Observation.Time, visibleStock, context.Check(command) is null));
            return command;
        }, allowRejected: true);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: controller);
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
        PlaygroundCharacter a = new("a", "Producer", backpack: new ItemInventory(new Dictionary<string, int> { ["item.food"] = 2 }), money: 12);
        PlaygroundCharacter b = new("b", "Neighbour", money: 12);
        PlaygroundWorld initial = AtPlace("workshop", [a, b]);
        string totals = MaterialSnapshot(initial.Business.Economy!.Totals);
        bool started = false, responded = false, overpayment = false;
        int bTurn = 0;
        var controller = new Script("money", context => {
            if (context.Observation.Actor.Id == "a") {
                if (!started) { started = true; return PlaygroundCommand.Produce("ration"); }
                Assert.Equal(17, context.Observation.Actor.Money);
                Assert.Equal(Hour(9), context.Observation.Actor.Activity!.Due);
                responded = true;
                return PlaygroundCommand.Continue();
            }
            if (++bTurn == 1) { return PlaygroundCommand.GiveMoney("a", 5); }
            if (bTurn == 2) {
                Assert.NotNull(context.Check(PlaygroundCommand.GiveMoney("a", 100)));
                overpayment = true;
                return PlaygroundCommand.GiveMoney("a", 100);
            }
            return PlaygroundCommand.Wait(2);
        }, allowRejected: true);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilState(kernel, _ => responded && overpayment);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(17, kernel.World.GetCharacter("a").Money);
        Assert.Equal(7, kernel.World.GetCharacter("b").Money);
        Assert.Equal(Hour(9), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Equal(2, kernel.World.GetCharacter("a").Activity!.Production!.InputCount);
        Assert.Equal(totals, MaterialSnapshot(kernel.World.Business.Economy!.Totals));
    }

    private static PlaygroundWorld AtPlace(string place, PlaygroundCharacter[] residents, PlaygroundMarket? market = null) {
        PlaygroundWorld basis = PlaygroundGameFactory.CreateLivingWorld(characterCount: residents.Length, enableNeeds: false).World;
        var totals = new PlaygroundEconomicTotals(residents.Sum(actor => actor.Money), residents.SelectMany(actor => actor.Backpack.Items)
            .GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Value), StringComparer.Ordinal));
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, residents.Select(actor => new EntityPlacement(new(actor.Id), new(place))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            residents.Select(actor => new PlaygroundVisit(actor.Id, place, basis.Time,
                basis.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == place))));
        return PlaygroundEngine.Rebuild(basis.Business.Replace(basis.Time, changedCharacters: residents, expedition: expedition,
            economy: new PlaygroundEconomyState(market ?? PlaygroundMarket.Fresh(basis.Definition, Hour(24)), totals)), basis.Time);
    }

    private static PlaygroundCommand Go(string from, string to) => PlaygroundCommand.Go(PlaygroundExpeditionMap.GetPassages(from).Single(p => p.To == to).Id);

    private static async Task UntilState(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> reached) {
        for (int step = 0; step < 500; step++) {
            if (reached(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The ordinary-command scenario did not reach its finite acceptance boundary.");
    }

    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(72)));
        kernel.World.Validate();
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        AssertAccounting(kernel.World);
    }

    // Compute the equations here instead of accepting only the production verifier's verdict.
    private static void AssertAccounting(PlaygroundWorld world) {
        PlaygroundEconomicTotals totals = world.Business.Economy!.Totals;
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

    private sealed class Script(string session, Func<PlaygroundDecisionContext, PlaygroundCommand> choose, bool allowRejected = false) : IPlaygroundCharacterController {
        private int _calls;
        public List<PlaygroundObservation> Views { get; } = [];
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Views.Add(context.Observation);
            PlaygroundCommand command = choose(context);
            if (!allowRejected) { Assert.Null(context.Check(command)); }
            int ordinal = ++_calls;
            return ValueTask.FromResult(new PlaygroundControllerOperation($"{session}/call/{ordinal}", PlaygroundCommandParser.Format(command), $"{session}/operation/{ordinal}"));
        }
    }

    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A committed production must settle without asking a new controller.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Event-only recovery must not forecast.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world, OccurrenceCandidate<PlaygroundPrediction> winner,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Event-only recovery must not re-plan.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterEvent(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, Func<PlaygroundFact, bool> matches)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(matches)) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class SurvivalTests {
    [Theory]
    [InlineData("item.food")]
    [InlineData("item.water")]
    public void Full_need_rejects_consumption_without_changing_inventory_time_or_decision(string itemId) {
        CycleWorld world = World(Hour(8), Needs(Hour(8)), items: new() { [itemId] = 2 });
        CycleCharacter actor = world.GetCharacter("a");
        string before = world.DescribeState();
        long decision = actor.DecisionGeneration;
        CycleDecisionContext context = CycleDecisionContext.Create(world, "a");
        for (int check = 0; check < 2; check++) {
            Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Use(itemId)));
            Assert.NotNull(context.Check(CycleCommand.Use(itemId)));
        }
        Assert.Equal(before, world.DescribeState());
        Assert.Equal(Hour(8), world.Time);
        Assert.Equal(2, actor.Backpack.Count(itemId));
        Assert.Equal(decision, actor.DecisionGeneration);
        Assert.Same(actor, world.GetCharacter("a"));
    }

    [Theory]
    [InlineData("item.food")]
    [InlineData("item.water")]
    public async Task Near_full_need_consumes_one_item_and_clamps_at_maximum_without_elapsed_time(string itemId) {
        CycleWorld initial = World(Hour(8), Needs(Hour(8), food: 96, water: 96), items: new() { [itemId] = 2 });
        var history = Memory(initial);
        var source = Commands(CycleCommand.Use(itemId));
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        Assert.Null(CycleActions.Check(initial, "a", CycleCommand.Use(itemId)));
        await Advance(kernel);
        Assert.Equal(CycleEventKind.ItemUsed, LastKind(kernel));
        Assert.Equal(CycleSurvival.MaximumUnits, itemId == "item.food" ? Body(kernel.World).FoodUnits : Body(kernel.World).WaterUnits);
        Assert.Equal(96 * CycleSurvival.Scale, itemId == "item.food" ? Body(kernel.World).WaterUnits : Body(kernel.World).FoodUnits);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count(itemId));
        Assert.Equal(2, initial.GetCharacter("a").Backpack.Count(itemId));
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(1, source.Calls);
        Assert.Single(history.CompletedEvents);
        Assert.NotNull(CycleActions.Check(kernel.World, "a", CycleCommand.Use(itemId)));
    }

    [Fact]
    public async Task Acknowledging_low_water_preserves_activity_and_does_not_warn_again_in_the_same_low_interval() {
        CycleWorld initial = World(Hour(8), Needs(Hour(8), water: 33), Wait(Hour(8), 4));
        var history = Memory(initial);
        var source = Commands(CycleCommand.Continue());
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(25 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(2, Body(kernel.World).PendingMask);
        CycleActivity activity = kernel.World.GetCharacter("a").Activity!;
        await Advance(kernel);
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Same(activity, kernel.World.GetCharacter("a").Activity);
        Assert.Equal(0, Body(kernel.World).PendingMask);
        Assert.Equal(2, Body(kernel.World).WarnedMask);
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(Hour(10)));
        Assert.Single(history.CompletedEvents.SelectMany(e => e.Facts), fact => fact.Kind == CycleEventKind.SurvivalWarning);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Exact_warning_level_is_not_rearmed_but_water_supply_above_it_allows_one_later_warning() {
        var low = new CycleSurvival(Hour(8), 60 * CycleSurvival.Scale, 25 * CycleSurvival.Scale, 80 * CycleSurvival.Scale,
            warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
        CycleWorld initial = World(Hour(8), low, Wait(Hour(8), 12), items: new() { ["item.food"] = 1, ["item.water"] = 1 });
        var source = Commands(CycleCommand.Use("item.food"), CycleCommand.Use("item.water"), CycleCommand.Continue(), CycleCommand.Continue());
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: source);
        await Advance(kernel);
        Assert.Equal(25 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(2, Body(kernel.World).WarnedMask);
        Assert.Equal(2, Body(kernel.World).PendingMask);
        await Advance(kernel);
        Assert.Equal(70 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(0, Body(kernel.World).WarnedMask);
        Assert.Equal(2, Body(kernel.World).PendingMask);
        await Advance(kernel);
        Assert.Equal(0, Body(kernel.World).PendingMask);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.SurvivalWarning, LastKind(kernel));
        // Losing 45 points at 8 points/hour takes 5 h 37 min 30 s.
        Assert.Equal(new ModelTime(Hour(13).Ticks + 37 * 60_000 + 30_000), kernel.World.Time);
        Assert.Equal(25 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
        await Advance(kernel);
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(new(kernel.World.Time.Ticks + 1)));
        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public async Task Drinking_during_a_travel_warning_keeps_exact_spatial_segment_and_arrival_deadline() {
        CycleWorld initial = World(Hour(8), Needs(Hour(8), water: 29), items: new() { ["item.water"] = 2 });
        var source = Commands(CycleCommand.Go(Passage("camp", "stream")), CycleCommand.Use("item.water"), CycleCommand.Continue());
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: source);
        await Advance(kernel);
        GraphSpatialState spatial = kernel.World.Expedition!.Spatial;
        var location = Assert.IsType<TraversingLocation>(Assert.Single(spatial.Entities).Location);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(Minute(510), kernel.World.Time);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.ItemUsed, LastKind(kernel));
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(70 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(spatial, kernel.World.Expedition!.Spatial);
        Assert.Equal(location, Assert.Single(kernel.World.Expedition.Spatial.Entities).Location);
        Assert.Equal(Hour(9), location.ArrivalDue);
        await Advance(kernel);
        await AdvanceUntil(kernel, world => CycleActions.CurrentPlace(world, "a") == "stream");
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(66 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task Dusk_integrates_daytime_warming_before_switching_to_nighttime_cooling() {
        CycleWorld initial = World(Hour(17), Needs(Hour(17), warmth: 60), Wait(Hour(17), 4));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        await Advance(kernel);
        Assert.Equal(CycleEventKind.LightChanged, LastKind(kernel));
        Assert.Equal(Hour(18), kernel.World.Time);
        Assert.Equal(66 * CycleSurvival.Scale, Body(kernel.World).WarmthUnits);
        Assert.Equal(-12, Body(kernel.World).WarmthPerHour);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.WaitCompleted, LastKind(kernel));
        Assert.Equal(Hour(21), kernel.World.Time);
        Assert.Equal(30 * CycleSurvival.Scale, Body(kernel.World).WarmthUnits); // 60 + 6 - 3 * 12.
        Assert.Equal(80 * CycleSurvival.Scale, Body(kernel.World).FoodUnits);
        Assert.Equal(68 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
    }

    [Fact]
    public async Task Shelter_entry_settles_outdoor_loss_then_warms_while_food_and_water_keep_decreasing() {
        CycleWorld initial = World(Hour(17), Needs(Hour(17), warmth: 60), place: "ridge");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController:
            Commands(CycleCommand.Go(Passage("ridge", "cabin")), CycleCommand.Wait(2)));
        await AdvanceUntil(kernel, world => CycleActions.CurrentPlace(world, "a") == "cabin");
        Assert.Equal(Hour(19), kernel.World.Time);
        Assert.Equal(54 * CycleSurvival.Scale, Body(kernel.World).WarmthUnits); // +6 day, -12 night outdoors.
        Assert.Equal(12, Body(kernel.World).WarmthPerHour);
        await Advance(kernel);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.WaitCompleted, LastKind(kernel));
        Assert.Equal(Hour(21), kernel.World.Time);
        Assert.Equal(78 * CycleSurvival.Scale, Body(kernel.World).WarmthUnits); // Two hours of sheltered warming.
        Assert.Equal(80 * CycleSurvival.Scale, Body(kernel.World).FoodUnits);
        Assert.Equal(68 * CycleSurvival.Scale, Body(kernel.World).WaterUnits);
    }

    [Fact]
    public async Task Incremental_and_full_survival_traces_match_through_night_supply_warnings_and_death() {
        static CycleWorld Initial() => World(Hour(17), Needs(Hour(17), water: 33, warmth: 60), Wait(Hour(17), 12),
            items: new() { ["item.water"] = 1 });
        static StubCharacterController Driver() => Commands(CycleCommand.Use("item.water"), CycleCommand.Continue(), CycleCommand.Continue());
        var incrementalHistory = Memory(Initial());
        var incremental = CycleEngine.CreateKernel(incrementalHistory, PredictionMode.Incremental, characterController: Driver());
        var full = CycleEngine.CreateKernel(Memory(Initial()), PredictionMode.Full, characterController: Driver());
        for (int steps = 0; incremental.World.Expedition!.Outcome == CycleExpeditionOutcome.Exploring; steps++) {
            Assert.True(steps < 30);
            await Advance(incremental);
            await Advance(full);
            Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
            Assert.Equal(full.World.Expedition!.Spatial, incremental.World.Expedition!.Spatial);
            Assert.Equal(full.World.Spatial!.Contacts, incremental.World.Spatial!.Contacts);
            Assert.Equal(full.Cursor, incremental.Cursor);
            Assert.Equal(Describe(full.LastCompletion!.Event), Describe(incremental.LastCompletion!.Event));
        }
        Assert.Equal(CycleExpeditionOutcome.Dead, incremental.World.Expedition!.Outcome);
        Assert.Equal(Minute(23 * 60 + 30), incremental.World.Time);
        Assert.Equal(0, Body(incremental.World).WarmthUnits);
        Assert.Equal(26 * CycleSurvival.Scale, Body(incremental.World).WaterUnits);
        Assert.Equal(2, incrementalHistory.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == CycleEventKind.SurvivalWarning));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Exit_is_safe_only_when_it_precedes_water_depletion_by_at_least_one_tick(long deathOffset, bool escaped) {
        long duration = 3 * CycleTime.Hour;
        // Water changes by 8 fixed units per tick: death is exactly arrival + deathOffset.
        var needs = new CycleSurvival(Hour(8), 100 * CycleSurvival.Scale, 8 * (duration + deathOffset),
            100 * CycleSurvival.Scale, warnedMask: 2);
        CycleWorld initial = World(Hour(8), needs, place: "ravine");
        var source = Commands(CycleCommand.Go(Passage("ravine", "road")));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: source);
        await AdvanceUntil(kernel, world => world.Expedition!.Outcome != CycleExpeditionOutcome.Exploring);
        Assert.Equal(escaped ? CycleExpeditionOutcome.Escaped : CycleExpeditionOutcome.Dead, kernel.World.Expedition!.Outcome);
        Assert.Equal(new ModelTime(Hour(11).Ticks + (escaped ? 0 : deathOffset)), kernel.World.Time);
        Assert.Equal(escaped ? 8 : 0, Body(kernel.World).WaterUnits);
        Assert.Equal(escaped ? CycleEventKind.MoveArrived : CycleEventKind.SurvivalFailed, LastKind(kernel));
        Assert.Equal(1, source.Calls);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task State_cold_open_before_or_at_warning_preserves_the_unexecuted_threshold(long tickOffset) {
        using var store = new TemporaryStore();
        var savedTime = new ModelTime(Hour(9).Ticks + tickOffset);
        CycleWorld initial = World(savedTime, Needs(Hour(8), water: 33), Wait(Hour(8), 4));
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        using (CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) { }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Equal(savedTime, reopened.Cursor.CurrentModelTime);
        Assert.Equal(25 * CycleSurvival.Scale - 8 * tickOffset, Body(reopened.State).WaterUnits);
        Assert.Equal(0, Body(reopened.State).PendingMask);
        Assert.Null(reopened.PendingEvent);
        var resumed = CycleEngine.CreateKernel(reopened, characterController: new ForbiddenController());
        Assert.False(resumed.RecoverPending());
        await Advance(baseline);
        await Advance(resumed);
        Assert.Equal(Hour(9), resumed.World.Time);
        Assert.Equal(baseline.World.DescribeState(), resumed.World.DescribeState());
        Assert.Equal(baseline.Cursor, resumed.Cursor);
        Assert.Equal(CycleEventKind.SurvivalWarning, LastKind(resumed));
    }

    [Theory]
    [InlineData(CycleEventKind.SurvivalWarning)]
    [InlineData(CycleEventKind.ItemUsed)]
    public async Task Warning_and_consumption_event_only_recovery_neither_replans_nor_reapplies_effects(CycleEventKind stopAt) {
        using var store = new TemporaryStore();
        CycleWorld initial = World(Hour(8), Needs(Hour(8), water: 33), Wait(Hour(8), 4), items: new() { ["item.water"] = 2 });
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Use("item.water")));
        do { await Advance(baseline); } while (LastKind(baseline) != stopAt);
        string saved;
        KernelCursor savedCursor;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var source = Commands(CycleCommand.Use("item.water"));
            var interrupted = CycleEngine.CreateKernel(new StopAfterEvent(history, stopAt), characterController: source);
            for (int step = 0; ; step++) {
                Assert.True(step < 5);
                saved = history.State.DescribeState();
                savedCursor = history.Cursor;
                try { await Advance(interrupted); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { break; }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(stopAt == CycleEventKind.ItemUsed ? 1 : 0, source.Calls);
            Assert.Equal(saved, history.State.DescribeState());
        }
        using (var reopened = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(saved, reopened.State.DescribeState());
            Assert.Equal(savedCursor, reopened.Cursor);
            int completions = 0;
            var recovery = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
                (before, instant, facts, folded) => {
                    completions++;
                    return CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental);
                }, CycleEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(1, completions);
            Assert.Equal(baseline.World.DescribeState(), recovery.World.DescribeState());
            Assert.Equal(baseline.World.Expedition!.Spatial, recovery.World.Expedition!.Spatial);
            Assert.Equal(baseline.Cursor, recovery.Cursor);
            Assert.Equal(stopAt == CycleEventKind.ItemUsed ? 1 : 2, recovery.World.GetCharacter("a").Backpack.Count("item.water"));
            Assert.Equal((stopAt == CycleEventKind.ItemUsed ? 70 : 25) * CycleSurvival.Scale, Body(recovery.World).WaterUnits);
            Assert.Equal(2, Body(recovery.World).PendingMask);
        }
        using var final = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(baseline.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(baseline.Cursor, final.Cursor);
    }

    [Fact]
    public async Task Death_event_only_recovery_preserves_unfinished_traversal_and_never_turns_it_into_an_arrival() {
        using var store = new TemporaryStore();
        var needs = new CycleSurvival(Hour(8), 100 * CycleSurvival.Scale, 8 * CycleSurvival.Scale,
            80 * CycleSurvival.Scale, warnedMask: 2);
        CycleWorld initial = World(Hour(8), needs);
        GraphSpatialState travelling;
        KernelCursor beforeDeath;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var source = Commands(CycleCommand.Go(Passage("camp", "ridge")));
            var kernel = CycleEngine.CreateKernel(new StopAfterEvent(history, CycleEventKind.SurvivalFailed), characterController: source);
            await Advance(kernel);
            travelling = kernel.World.Expedition!.Spatial;
            beforeDeath = kernel.Cursor;
            Assert.Equal(Hour(10), Assert.IsType<TraversingLocation>(Assert.Single(travelling.Entities).Location).ArrivalDue);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(Hour(24)).AsTask());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(kernel.IsFaulted);
            Assert.Equal(1, source.Calls);
            Assert.Equal(CycleExpeditionOutcome.Exploring, history.State.Expedition!.Outcome);
            Assert.Equal(beforeDeath, history.Cursor);
        }

        KernelCursor afterDeath;
        using (var reopened = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(travelling, reopened.State.Expedition!.Spatial);
            Assert.Equal(beforeDeath, reopened.Cursor);
            Assert.Equal(CycleEventKind.SurvivalFailed, Assert.Single(reopened.PendingEvent!.Facts).Kind);
            Assert.Empty(Assert.Single(reopened.PendingEvent.Facts).SpatialFacts);
            var recovery = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
                (before, instant, facts, folded) => CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
                CycleEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(Hour(9), recovery.World.Time);
            Assert.Equal(0, Body(recovery.World).WaterUnits);
            Assert.Equal(CycleExpeditionOutcome.Dead, recovery.World.Expedition!.Outcome);
            Assert.Equal(travelling, recovery.World.Expedition.Spatial);
            Assert.Single(recovery.World.Expedition.Visits); // No invented destination observation.
            Assert.Null(CycleActions.CurrentPlace(recovery.World, "a"));
            afterDeath = recovery.Cursor;
            var terminal = CycleEngine.CreateKernel(reopened, characterController: new ForbiddenController());
            Assert.Equal(StepStatus.Exhausted, await terminal.StepAsync(Hour(24)));
            Assert.Equal(afterDeath, terminal.Cursor);
        }

        using var final = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(afterDeath, final.Cursor);
        Assert.Equal(travelling, final.State.Expedition!.Spatial);
        Assert.Equal(CycleExpeditionOutcome.Dead, final.State.Expedition.Outcome);
        Assert.Equal(StepStatus.Exhausted, await CycleEngine.CreateKernel(final,
            characterController: new ForbiddenController()).StepAsync(Hour(24)));
    }

    private static CycleSurvival Needs(ModelTime at, int food = 100, int water = 100, int warmth = 80, int warmthRate = 6) =>
        CycleSurvival.Create(at, food, water, warmth, warmthRate);
    private static CycleSurvival Body(CycleWorld world) => world.GetCharacter("a").Survival!.Project(world.Time);
    private static CycleActivity Wait(ModelTime at, int hours) =>
        new(CycleActivityKind.Wait, 1, at, hours * CycleTime.Hour, new(at.Ticks + hours * CycleTime.Hour));
    private static string Passage(string from, string to) => Assert.Single(CycleExpeditionMap.GetPassages(from), p => p.To == to).Id;
    private static CycleEventKind LastKind(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Single(kernel.LastCompletion!.Event.Facts).Kind;
    private static CycleWorld World(ModelTime time, CycleSurvival needs, CycleActivity? activity = null,
        string place = "camp", Dictionary<string, int>? items = null) {
        var initial = CycleGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false);
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            [new("a", place, time, initial.World.Resources.Where(r => CycleExpeditionMap.GetResourcePlace(r.Id) == place))]);
        var actor = new CycleCharacter("a", "Survival fixture", backpack: new ItemInventory(items), activity: activity,
            nextActivityId: activity is null ? 1 : 2, survival: needs);
        CycleBusinessState business = initial.World.Business.Replace(time, changedCharacters: [actor], expedition: expedition);
        return CycleEngine.Rebuild(business, time);
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Same(kernel.World.Expedition!.Spatial, kernel.World.Spatial.Business);
    }
    private static async Task AdvanceUntil(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> done) {
        for (int count = 0; count < 100; count++) {
            if (done(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected survival boundary was not reached.");
    }
    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleCommand> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This threshold must not request a character decision.");
    }
    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not forecast.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not plan or call a controller.");
    }
    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner, CycleEventKind stopAt)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == stopAt)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

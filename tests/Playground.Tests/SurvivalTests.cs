using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class SurvivalTests {
    [Theory]
    [InlineData("item.food")]
    [InlineData("item.water")]
    public void Full_need_rejects_consumption_without_changing_inventory_time_or_decision(string itemId) {
        PlaygroundWorld world = World(Hour(8), Needs(Hour(8)), items: new() { [itemId] = 2 });
        PlaygroundCharacter actor = world.GetCharacter("a");
        string before = world.DescribeState();
        long decision = actor.DecisionGeneration;
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(world, "a");
        for (int check = 0; check < 2; check++) {
            Assert.NotNull(PlaygroundActions.Check(world, "a", PlaygroundCommand.Use(itemId)));
            Assert.NotNull(observation.CheckDisplayed(PlaygroundCommand.Use(itemId)));
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
        PlaygroundWorld initial = World(Hour(8), Needs(Hour(8), food: 96, water: 96), items: new() { [itemId] = 2 });
        var history = Memory(initial);
        var source = Commands(PlaygroundCommand.Use(itemId));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        Assert.Null(PlaygroundActions.Check(initial, "a", PlaygroundCommand.Use(itemId)));
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.ItemUsed, LastKind(kernel));
        Assert.Equal(PlaygroundSurvival.MaximumUnits, itemId == "item.food" ? Body(kernel.World).FoodUnits : Body(kernel.World).WaterUnits);
        Assert.Equal(96 * PlaygroundSurvival.Scale, itemId == "item.food" ? Body(kernel.World).WaterUnits : Body(kernel.World).FoodUnits);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count(itemId));
        Assert.Equal(2, initial.GetCharacter("a").Backpack.Count(itemId));
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(1, source.Calls);
        Assert.Single(history.CompletedEvents.SelectMany(e => e.Facts), fact => fact.Kind == PlaygroundEventKind.ItemUsed);
        Assert.NotNull(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Use(itemId)));
    }

    [Fact]
    public async Task Acknowledging_low_water_preserves_activity_and_does_not_warn_again_in_the_same_low_interval() {
        PlaygroundWorld initial = World(Hour(8), Needs(Hour(8), water: 33), Wait(Hour(8), 4));
        var history = Memory(initial);
        var source = Commands(PlaygroundCommand.Continue());
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(25 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(2, Body(kernel.World).PendingMask);
        PlaygroundActivity activity = kernel.World.GetCharacter("a").Activity!;
        await Advance(kernel);
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Same(activity, kernel.World.GetCharacter("a").Activity);
        Assert.Equal(0, Body(kernel.World).PendingMask);
        Assert.Equal(2, Body(kernel.World).WarnedMask);
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(Hour(10)));
        Assert.Single(history.CompletedEvents.SelectMany(e => e.Facts), fact => fact.Kind == PlaygroundEventKind.SurvivalWarning);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Exact_warning_level_is_not_rearmed_but_water_supply_above_it_allows_one_later_warning() {
        var low = new PlaygroundSurvival(Hour(8), 60 * PlaygroundSurvival.Scale, 25 * PlaygroundSurvival.Scale, 80 * PlaygroundSurvival.Scale,
            PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
        PlaygroundWorld initial = World(Hour(8), low, Wait(Hour(8), 12), items: new() { ["item.food"] = 1, ["item.water"] = 1 });
        var source = Commands(PlaygroundCommand.Use("item.food"), PlaygroundCommand.Use("item.water"), PlaygroundCommand.Continue(), PlaygroundCommand.Continue());
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: source);
        await Advance(kernel);
        Assert.Equal(25 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(2, Body(kernel.World).WarnedMask);
        Assert.Equal(2, Body(kernel.World).PendingMask);
        await Advance(kernel);
        Assert.Equal(70 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(0, Body(kernel.World).WarnedMask);
        Assert.Equal(2, Body(kernel.World).PendingMask);
        await Advance(kernel);
        Assert.Equal(0, Body(kernel.World).PendingMask);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.SurvivalWarning, LastKind(kernel));
        // Losing 45 points at 8 points/hour takes 5 h 37 min 30 s.
        Assert.Equal(new ModelTime(Hour(13).Ticks + 37 * 60_000 + 30_000), kernel.World.Time);
        Assert.Equal(25 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
        await Advance(kernel);
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(new(kernel.World.Time.Ticks + 1)));
        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public async Task Drinking_during_a_travel_warning_keeps_exact_spatial_segment_and_arrival_deadline() {
        PlaygroundWorld initial = World(Hour(8), Needs(Hour(8), water: 29), items: new() { ["item.water"] = 2 });
        var source = Commands(PlaygroundCommand.Go(Passage("camp", "stream")), PlaygroundCommand.Use("item.water"), PlaygroundCommand.Continue());
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: source);
        await Advance(kernel);
        GraphSpatialState spatial = kernel.World.Expedition!.Spatial;
        var location = Assert.IsType<TraversingLocation>(Assert.Single(spatial.Entities).Location);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(Minute(510), kernel.World.Time);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.ItemUsed, LastKind(kernel));
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(70 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(spatial, kernel.World.Expedition!.Spatial);
        Assert.Equal(location, Assert.Single(kernel.World.Expedition.Spatial.Entities).Location);
        Assert.Equal(Hour(9), location.ArrivalDue);
        await Advance(kernel);
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "stream");
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(66 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task Dusk_integrates_daytime_warming_before_switching_to_nighttime_cooling() {
        PlaygroundWorld initial = World(Hour(17), Needs(Hour(17), warmth: 60), Wait(Hour(17), 4));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.LightChanged, LastKind(kernel));
        Assert.Equal(Hour(18), kernel.World.Time);
        Assert.Equal(66 * PlaygroundSurvival.Scale, Body(kernel.World).WarmthUnits);
        Assert.Equal(-12, Body(kernel.World).WarmthPerHour);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WaitCompleted, LastKind(kernel));
        Assert.Equal(Hour(21), kernel.World.Time);
        Assert.Equal(30 * PlaygroundSurvival.Scale, Body(kernel.World).WarmthUnits); // 60 + 6 - 3 * 12.
        Assert.Equal(80 * PlaygroundSurvival.Scale, Body(kernel.World).FoodUnits);
        Assert.Equal(68 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
    }

    [Fact]
    public async Task Shelter_entry_settles_outdoor_loss_then_warms_while_food_and_water_keep_decreasing() {
        PlaygroundWorld initial = World(Hour(17), Needs(Hour(17), warmth: 60), place: "ridge");
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController:
            Commands(PlaygroundCommand.Go(Passage("ridge", "cabin")), PlaygroundCommand.Wait(2)));
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "cabin");
        Assert.Equal(Hour(19), kernel.World.Time);
        Assert.Equal(54 * PlaygroundSurvival.Scale, Body(kernel.World).WarmthUnits); // +6 day, -12 night outdoors.
        Assert.Equal(12, Body(kernel.World).WarmthPerHour);
        await Advance(kernel);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WaitCompleted, LastKind(kernel));
        Assert.Equal(Hour(21), kernel.World.Time);
        Assert.Equal(78 * PlaygroundSurvival.Scale, Body(kernel.World).WarmthUnits); // Two hours of sheltered warming.
        Assert.Equal(80 * PlaygroundSurvival.Scale, Body(kernel.World).FoodUnits);
        Assert.Equal(68 * PlaygroundSurvival.Scale, Body(kernel.World).WaterUnits);
    }

    [Fact]
    public async Task Incremental_and_full_survival_traces_match_through_night_supply_warnings_and_death() {
        static PlaygroundWorld Initial() => World(Hour(17), Needs(Hour(17), water: 33, warmth: 60), Wait(Hour(17), 12),
            items: new() { ["item.water"] = 1 });
        static StubCharacterController Driver() => Commands(PlaygroundCommand.Use("item.water"), PlaygroundCommand.Continue(), PlaygroundCommand.Continue());
        var incrementalHistory = Memory(Initial());
        var incremental = PlaygroundEngine.CreateKernel(incrementalHistory, PredictionMode.Incremental, characterController: Driver());
        var full = PlaygroundEngine.CreateKernel(Memory(Initial()), PredictionMode.Full, characterController: Driver());
        for (int steps = 0; incremental.World.GetCharacter("a").IsActive; steps++) {
            Assert.True(steps < 30);
            await Advance(incremental);
            await Advance(full);
            Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
            Assert.Equal(full.World.Expedition!.Spatial, incremental.World.Expedition!.Spatial);
            Assert.Equal(full.World.Spatial!.Contacts, incremental.World.Spatial!.Contacts);
            Assert.Equal(full.Cursor, incremental.Cursor);
            Assert.Equal(Describe(full.LastCompletion!.Event), Describe(incremental.LastCompletion!.Event));
        }
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, incremental.World.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(Minute(23 * 60 + 30), incremental.World.Time);
        Assert.Equal(0, Body(incremental.World).WarmthUnits);
        Assert.Equal(26 * PlaygroundSurvival.Scale, Body(incremental.World).WaterUnits);
        Assert.Equal(2, incrementalHistory.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == PlaygroundEventKind.SurvivalWarning));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Former_exit_arrival_does_not_prevent_death_and_same_tick_depletion_still_wins(long deathOffset, bool arrived) {
        long duration = 3 * PlaygroundTime.Hour;
        // Water changes by 8 fixed units per tick: death is exactly arrival + deathOffset.
        var needs = new PlaygroundSurvival(Hour(8), 100 * PlaygroundSurvival.Scale, 8 * (duration + deathOffset),
            100 * PlaygroundSurvival.Scale, PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2);
        PlaygroundWorld initial = World(Hour(8), needs, place: "ravine");
        var source = Commands(PlaygroundCommand.Go(Passage("ravine", "road")), PlaygroundCommand.Wait(1));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: source);
        await AdvanceUntil(kernel, world => !world.GetCharacter("a").IsActive);
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, kernel.World.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(new ModelTime(Hour(11).Ticks + deathOffset), kernel.World.Time);
        Assert.Equal(0, Body(kernel.World).WaterUnits);
        Assert.Equal(PlaygroundEventKind.SurvivalFailed, LastKind(kernel));
        Assert.Equal(arrived, kernel.World.GetCharacter("a").Ending!.Location is AtPlaceLocation);
        Assert.Equal(arrived ? 2 : 1, source.Calls);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task State_cold_open_before_or_at_warning_preserves_the_unexecuted_threshold(long tickOffset) {
        using var store = new TemporaryStore();
        var savedTime = new ModelTime(Hour(9).Ticks + tickOffset);
        PlaygroundWorld initial = World(savedTime, Needs(Hour(8), water: 33), Wait(Hour(8), 4));
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        using (PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) { }
        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Equal(savedTime, reopened.Cursor.CurrentModelTime);
        Assert.Equal(25 * PlaygroundSurvival.Scale - 8 * tickOffset, Body(reopened.State).WaterUnits);
        Assert.Equal(0, Body(reopened.State).PendingMask);
        Assert.Null(reopened.PendingEvent);
        var resumed = PlaygroundEngine.CreateKernel(reopened, characterController: new ForbiddenController());
        Assert.False(resumed.RecoverPending());
        await Advance(baseline);
        await Advance(resumed);
        Assert.Equal(Hour(9), resumed.World.Time);
        Assert.Equal(baseline.World.DescribeState(), resumed.World.DescribeState());
        Assert.Equal(baseline.Cursor, resumed.Cursor);
        Assert.Equal(PlaygroundEventKind.SurvivalWarning, LastKind(resumed));
    }

    [Theory]
    [InlineData(PlaygroundEventKind.SurvivalWarning)]
    [InlineData(PlaygroundEventKind.ItemUsed)]
    public async Task Warning_and_consumption_event_only_recovery_neither_replans_nor_reapplies_effects(PlaygroundEventKind stopAt) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = World(Hour(8), Needs(Hour(8), water: 33), Wait(Hour(8), 4), items: new() { ["item.water"] = 2 });
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Use("item.water")));
        do { await Advance(baseline); } while (LastKind(baseline) != stopAt);
        string saved;
        KernelCursor savedCursor;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var source = Commands(PlaygroundCommand.Use("item.water"));
            var interrupted = PlaygroundEngine.CreateKernel(new StopAfterEvent(history, stopAt), characterController: source);
            for (int step = 0; ; step++) {
                Assert.True(step < 5);
                saved = history.State.DescribeState();
                savedCursor = history.Cursor;
                // Capture the State immediately before each real occurrence, including the separate opening.
                try { Assert.Equal(StepStatus.Committed, await interrupted.StepAsync(Hour(240))); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { break; }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(stopAt == PlaygroundEventKind.ItemUsed ? 1 : 0, source.Calls);
            Assert.Equal(saved, history.State.DescribeState());
        }
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(saved, reopened.State.DescribeState());
            Assert.Equal(savedCursor, reopened.Cursor);
            int completions = 0;
            var recovery = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (before, instant, facts, folded) => {
                    completions++;
                    return PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental);
                }, PlaygroundEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(1, completions);
            Assert.Equal(baseline.World.DescribeState(), recovery.World.DescribeState());
            Assert.Equal(baseline.World.Expedition!.Spatial, recovery.World.Expedition!.Spatial);
            Assert.Equal(baseline.Cursor, recovery.Cursor);
            Assert.Equal(stopAt == PlaygroundEventKind.ItemUsed ? 1 : 2, recovery.World.GetCharacter("a").Backpack.Count("item.water"));
            Assert.Equal((stopAt == PlaygroundEventKind.ItemUsed ? 70 : 25) * PlaygroundSurvival.Scale, Body(recovery.World).WaterUnits);
            Assert.Equal(2, Body(recovery.World).PendingMask);
        }
        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(baseline.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(baseline.Cursor, final.Cursor);
    }

    [Fact]
    public async Task Death_event_only_recovery_retires_traversal_and_keeps_its_history_without_inventing_arrival() {
        using var store = new TemporaryStore();
        var needs = new PlaygroundSurvival(Hour(8), 100 * PlaygroundSurvival.Scale, 8 * PlaygroundSurvival.Scale,
            80 * PlaygroundSurvival.Scale, PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2);
        PlaygroundWorld initial = World(Hour(8), needs);
        GraphSpatialState travelling;
        KernelCursor beforeDeath;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var source = Commands(PlaygroundCommand.Go(Passage("camp", "ridge")));
            var kernel = PlaygroundEngine.CreateKernel(new StopAfterEvent(history, PlaygroundEventKind.SurvivalFailed), characterController: source);
            await Advance(kernel);
            travelling = kernel.World.Expedition!.Spatial;
            beforeDeath = kernel.Cursor;
            Assert.Equal(Hour(10), Assert.IsType<TraversingLocation>(Assert.Single(travelling.Entities).Location).ArrivalDue);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(Hour(24)).AsTask());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(kernel.IsFaulted);
            Assert.Equal(1, source.Calls);
            Assert.True(history.State.GetCharacter("a").IsActive);
            Assert.Equal(beforeDeath, history.Cursor);
        }

        KernelCursor afterDeath;
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(travelling, reopened.State.Expedition!.Spatial);
            Assert.Equal(beforeDeath, reopened.Cursor);
            Assert.Equal(PlaygroundEventKind.SurvivalFailed, Assert.Single(reopened.PendingEvent!.Facts).Kind);
            Assert.Equal("a", Assert.IsType<EntityRemovedFact>(Assert.Single(Assert.Single(reopened.PendingEvent.Facts).SpatialFacts)).EntityId.Value);
            var recovery = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (before, instant, facts, folded) => PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
                PlaygroundEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(Hour(9), recovery.World.Time);
            Assert.Equal(0, Body(recovery.World).WaterUnits);
            Assert.Equal(PlaygroundExpeditionOutcome.Dead, recovery.World.GetCharacter("a").Ending!.Outcome);
            Assert.Empty(recovery.World.Expedition!.Spatial.Entities);
            Assert.Equal(Assert.Single(travelling.Entities).Location, recovery.World.GetCharacter("a").Ending!.Location);
            Assert.Equal(Hour(9), recovery.World.GetCharacter("a").Ending!.At);
            Assert.Equal(PlaygroundExpeditionSummary.NoneEscaped, recovery.World.ExpeditionSummary);
            Assert.Single(recovery.World.Expedition.Visits); // No invented destination observation.
            Assert.Null(PlaygroundActions.CurrentPlace(recovery.World, "a"));
            afterDeath = recovery.Cursor;
            var terminal = PlaygroundEngine.CreateKernel(reopened, characterController: new ForbiddenController());
            Assert.Equal(StepStatus.Exhausted, await terminal.StepAsync(Hour(24)));
            Assert.Equal(afterDeath, terminal.Cursor);
        }

        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(afterDeath, final.Cursor);
        Assert.Empty(final.State.Expedition!.Spatial.Entities);
        Assert.Equal(Assert.Single(travelling.Entities).Location, final.State.GetCharacter("a").Ending!.Location);
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, final.State.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(StepStatus.Exhausted, await PlaygroundEngine.CreateKernel(final,
            characterController: new ForbiddenController()).StepAsync(Hour(24)));
    }

    private static PlaygroundSurvival Needs(ModelTime at, int food = 100, int water = 100, int warmth = 80, int warmthRate = 6) =>
        PlaygroundSurvival.Create(at, food, water, warmth, Rate(warmthRate));
    private static PlaygroundSurvival Body(PlaygroundWorld world) => world.GetCharacter("a").Survival!.Project(world.Time);
    private static PlaygroundActivity Wait(ModelTime at, int hours) =>
        new(PlaygroundActivityKind.Wait, 1, at, hours * PlaygroundTime.Hour, new(at.Ticks + hours * PlaygroundTime.Hour));
    private static string Passage(string from, string to) => Assert.Single(PlaygroundExpeditionMap.GetPassages(from), p => p.To == to).Id;
    private static PlaygroundEventKind LastKind(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        Assert.Single(kernel.LastCompletion!.Event.Facts).Kind;
    private static PlaygroundWorld World(ModelTime time, PlaygroundSurvival needs, PlaygroundActivity? activity = null,
        string place = "camp", Dictionary<string, int>? items = null) {
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false);
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            [new("a", place, time, initial.World.Resources.Where(r => PlaygroundExpeditionMap.GetResourcePlace(r.Id) == place))]);
        var actor = new PlaygroundCharacter("a", "Survival fixture", backpack: new ItemInventory(items), activity: activity,
            nextActivityId: activity is null ? 1 : 2, survival: needs);
        PlaygroundBusinessState business = initial.World.Business.Replace(time, changedCharacters: [actor], expedition: expedition);
        return PlaygroundEngine.Rebuild(business, time);
    }

    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        await StepWorldEvent(kernel);
        kernel.World.Validate();
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Same(kernel.World.Expedition!.Spatial, kernel.World.Spatial.Business);
    }
    private static async Task AdvanceUntil(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> done) {
        for (int count = 0; count < 100; count++) {
            if (done(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected survival boundary was not reached.");
    }
    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This threshold must not request a character decision.");
    }
    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not forecast.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not plan or call a controller.");
    }
    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterEvent(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, PlaygroundEventKind stopAt)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == stopAt)) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

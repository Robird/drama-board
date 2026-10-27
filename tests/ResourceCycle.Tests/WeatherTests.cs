using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class WeatherTests {
    // Fixed Kernel vectors for the weather/v1 stream: seed 0 => cold, calm, calm, cold; seed 1 => calm first.
    private const ulong ColdSeed = 0;
    private const ulong CalmSeed = 1;

    [Fact]
    public void Cold_open_rejects_a_silently_disabled_weather_stream_but_exhausted_streams_are_valid() {
        CycleWorld initial = World(Hour(8), Hour(10));
        CycleExpedition original = initial.Expedition!;
        var invalid = new CycleExpedition(original.ScenarioId, original.Spatial, original.Visits,
            environment: new(ColdSeed, 0, null));
        CycleBusinessState business = initial.Business.Replace(initial.Time, expedition: invalid);
        using var store = new TemporaryStore();
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            repository.CreateRootBranchFromState("missing-weather-deadline",
                CycleDurableRoot.CreateRevisionCandidate(business, InitialCursor(initial), CycleGameRevision.Current));
        }
        Assert.Throws<InvalidOperationException>(() => CycleDurableHistory.Open(store.StorePath));

        // Null is reserved for limits of the address/time representation, not a gameplay off-switch.
        new CycleEnvironment(ColdSeed, long.MaxValue, null, lastAt: Hour(8)).Validate(Hour(8));
        var lastRepresentableWindow = new ModelTime(long.MaxValue - CycleEnvironmentRules.IntervalTicks + 1);
        new CycleEnvironment(ColdSeed, 1, null, lastAt: lastRepresentableWindow).Validate(lastRepresentableWindow);
        Assert.Throws<InvalidOperationException>(() => new CycleEnvironment(ColdSeed, 1, null, lastAt: Hour(8)).Validate(Hour(8)));
    }

    [Theory]
    [InlineData(ColdSeed, true)]
    [InlineData(CalmSeed, false)]
    public async Task Same_seed_gives_the_same_weather_in_shelter_and_outdoors_but_only_exposure_loses_warmth(ulong seed, bool cold) {
        CycleWorld outdoors = World(Hour(8), Hour(10), seed: seed);
        CycleWorld shelter = World(Hour(8), Hour(10), place: "cabin", seed: seed);
        var exposed = CycleEngine.CreateKernel(Memory(outdoors), characterController: new ForbiddenController());
        var protectedKernel = CycleEngine.CreateKernel(Memory(shelter), characterController: new ForbiddenController());
        await Advance(exposed);
        await Advance(protectedKernel);
        Assert.Equal(CycleEventKind.WeatherResolved, LastKind(exposed));
        Assert.Equal(cold, Environment(exposed.World).LastCold);
        Assert.Equal(cold, Environment(protectedKernel.World).LastCold);
        Assert.False(Environment(exposed.World).LastSheltered);
        Assert.True(Environment(protectedKernel.World).LastSheltered);
        Assert.Equal((cold ? 10 : 0) * CycleSurvival.Scale, Environment(exposed.World).LastWarmthLossUnits);
        Assert.Equal(0, Environment(protectedKernel.World).LastWarmthLossUnits);
        Assert.Equal((cold ? 62 : 72) * CycleSurvival.Scale, Body(exposed.World).WarmthUnits);
        Assert.Equal(84 * CycleSurvival.Scale, Body(protectedKernel.World).WarmthUnits);
        Assert.Equal(1, Environment(exposed.World).Sequence);
        Assert.Equal(1, Environment(protectedKernel.World).Sequence);
        Assert.Equal(Hour(12), Environment(exposed.World).NextDue);
    }

    [Fact]
    public async Task Observation_prediction_checks_and_rejected_commands_do_not_advance_random_state() {
        CycleWorld initial = World(Hour(8), Hour(10), idle: true);
        var history = Memory(initial);
        string before = initial.DescribeState();
        for (int repeat = 0; repeat < 3; repeat++) {
            CycleDecisionContext context = CycleDecisionContext.Create(initial, "a");
            Assert.NotNull(context.Check(CycleCommand.Go("not-visible")));
            Assert.NotNull(context.Check(CycleCommand.Use("not-owned")));
            Assert.NotNull(CycleEnvironmentRules.Observe(initial, "a"));
            Assert.Equal(initial.Forecasts.ToString(), CycleEngine.Rebuild(initial.Business, initial.Time).Forecasts.ToString());
            string projection = JsonSerializer.Serialize(context.Observation);
            Assert.DoesNotContain("\"Seed\"", projection, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Sequence\"", projection, StringComparison.Ordinal);
        }
        var rejected = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Go("not-visible")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.StepAsync(Hour(12)).AsTask());
        Assert.Equal(before, history.State.DescribeState());
        Assert.Equal(0, Environment(history.State).Sequence);
        Assert.Equal(Hour(10), Environment(history.State).NextDue);
        Assert.Empty(history.CompletedEvents);
        Assert.Null(history.PendingEvent);
        var accepted = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Wait(4)));
        await Advance(accepted);
        await Advance(accepted);
        Assert.Equal(CycleEventKind.WeatherResolved, LastKind(accepted));
        Assert.True(Environment(accepted.World).LastCold);
        Assert.Equal(1, Environment(accepted.World).Sequence);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Entering_shelter_protects_from_weather_at_or_after_arrival_but_not_one_tick_before(long weatherOffset, bool sheltered) {
        CycleWorld initial = World(Hour(8), new(Hour(10).Ticks + weatherOffset), place: "ridge", idle: true);
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController:
            Commands(CycleCommand.Go(Passage("ridge", "cabin")), CycleCommand.Wait(1)));
        await Until(kernel, world => Environment(world).Sequence == 1 && CycleActions.CurrentPlace(world, "a") == "cabin");
        Assert.Equal(sheltered, Environment(kernel.World).LastSheltered);
        Assert.True(Environment(kernel.World).LastCold);
        Assert.Equal((sheltered ? 0 : 10) * CycleSurvival.Scale, Environment(kernel.World).LastWarmthLossUnits);
        if (weatherOffset == 0) {
            Assert.Equal(new[] { CycleEventKind.MoveArrived, CycleEventKind.WeatherResolved }, history.CompletedEvents
                .Where(e => e.TargetInstant.ModelTime == Hour(10)).SelectMany(e => e.Facts).Select(f => f.Kind));
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public async Task Reaching_safety_at_or_before_weather_does_not_draw_that_window(long weatherOffset, long expectedSequence) {
        CycleWorld initial = World(Hour(8), new(Hour(11).Ticks + weatherOffset), place: "ravine", idle: true);
        var source = Commands(CycleCommand.Go(Passage("ravine", "road")));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: source);
        await Until(kernel, world => world.Expedition!.Outcome == CycleExpeditionOutcome.Escaped);
        Assert.Equal(Hour(11), kernel.World.Time);
        Assert.Equal(expectedSequence, Environment(kernel.World).Sequence);
        Assert.Equal(1, source.Calls);
        KernelCursor cursor = kernel.Cursor;
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(240)));
        Assert.Equal(cursor, kernel.Cursor);
        Assert.Equal(expectedSequence, Environment(kernel.World).Sequence);
    }

    [Fact]
    public async Task Continuous_depletion_at_arrival_and_weather_time_wins_without_arrival_or_random_draw() {
        var needs = new CycleSurvival(Hour(8), 100 * CycleSurvival.Scale, 16 * CycleSurvival.Scale,
            60 * CycleSurvival.Scale, warnedMask: 2);
        CycleWorld initial = World(Hour(8), Hour(10), place: "ridge", idle: true, needs: needs);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Go(Passage("ridge", "cabin"))));
        await Advance(kernel);
        GraphSpatialState travelling = kernel.World.Expedition!.Spatial;
        await Advance(kernel);
        Assert.Equal(CycleEventKind.SurvivalFailed, LastKind(kernel));
        Assert.Equal(CycleExpeditionOutcome.Dead, kernel.World.Expedition!.Outcome);
        Assert.Equal(Hour(10), kernel.World.Time);
        Assert.Equal(0, Environment(kernel.World).Sequence);
        Assert.Equal(travelling, kernel.World.Expedition.Spatial);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(12)));
    }

    [Fact]
    public async Task Cold_crossing_warning_line_is_recorded_before_separate_warning_and_character_input() {
        CycleWorld initial = World(Hour(8), Hour(8), needs: CycleSurvival.Create(Hour(8), 100, 100, 29));
        var history = Memory(initial);
        var source = Commands(CycleCommand.Continue());
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.WeatherResolved, LastKind(kernel));
        Assert.Equal(19 * CycleSurvival.Scale, Body(kernel.World).WarmthUnits);
        Assert.Equal(0, Body(kernel.World).PendingMask);
        Assert.Equal(0, source.Calls);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(4, Body(kernel.World).PendingMask);
        Assert.Equal(Hour(8), kernel.World.Time);
        await Advance(kernel);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, Environment(kernel.World).Sequence);
        Assert.Equal(0, Body(kernel.World).PendingMask);
    }

    [Fact]
    public async Task Weather_then_activity_completion_then_new_warning_all_settle_before_same_tick_input() {
        CycleWorld initial = World(Hour(18), Hour(20), waitHours: 2,
            needs: CycleSurvival.Create(Hour(18), 100, 100, 59, warmthPerHour: -12));
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: new ForbiddenController());
        await Advance(kernel);
        Assert.Equal(CycleEventKind.WeatherResolved, LastKind(kernel));
        Assert.Equal(25 * CycleSurvival.Scale, Body(kernel.World).WarmthUnits); // 59 - 24 elapsed - 10 exposure.
        await Advance(kernel);
        Assert.Equal(CycleEventKind.WaitCompleted, LastKind(kernel));
        Assert.Null(kernel.World.GetCharacter("a").Activity);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(Hour(20), kernel.World.Time);
        Assert.Equal(4, Body(kernel.World).PendingMask);
        Assert.Equal(new[] { CycleEventKind.WeatherResolved, CycleEventKind.WaitCompleted, CycleEventKind.SurvivalWarning },
            history.CompletedEvents.SelectMany(e => e.Facts).Select(fact => fact.Kind));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task State_cold_open_before_or_at_weather_does_not_draw_until_normal_step(long tickOffset) {
        using var store = new TemporaryStore();
        ModelTime savedTime = new(Hour(10).Ticks + tickOffset);
        CycleWorld initial = World(savedTime, Hour(10));
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        using (CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) { }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Equal(savedTime, reopened.Cursor.CurrentModelTime);
        Assert.Equal(0, Environment(reopened.State).Sequence);
        Assert.Null(Environment(reopened.State).LastAt);
        Assert.Null(reopened.PendingEvent);
        var resumed = CycleEngine.CreateKernel(reopened, characterController: new ForbiddenController());
        Assert.False(resumed.RecoverPending());
        await Advance(baseline);
        await Advance(resumed);
        Assert.Equal(Hour(10), resumed.World.Time);
        Assert.Equal(1, Environment(resumed.World).Sequence);
        Assert.True(Environment(resumed.World).LastCold);
        Assert.Equal(baseline.World.DescribeState(), resumed.World.DescribeState());
        Assert.Equal(baseline.Cursor, resumed.Cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Weather_event_only_recovery_uses_recorded_random_result_and_warmth_loss_exactly_once(bool fatal) {
        using var store = new TemporaryStore();
        var needs = new CycleSurvival(Hour(8), 100 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            (fatal ? 9 : 29) * CycleSurvival.Scale, warnedMask: fatal ? 4 : 0);
        CycleWorld initial = World(Hour(8), Hour(8), needs: needs);
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        await Advance(baseline);
        Assert.Equal(CycleEventKind.WeatherResolved, LastKind(baseline));
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var interrupted = CycleEngine.CreateKernel(new StopAfterWeather(history), characterController: new ForbiddenController());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.StepAsync(Hour(12)).AsTask());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(0, Environment(history.State).Sequence);
            Assert.Equal(initial.DescribeState(), history.State.DescribeState());
        }
        using (var reopened = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(0, Environment(reopened.State).Sequence);
            Assert.Equal(InitialCursor(initial), reopened.Cursor);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(reopened.PendingEvent!));
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
            Assert.Equal(1, Environment(recovery.World).Sequence);
            Assert.Equal((fatal ? 9 : 10) * CycleSurvival.Scale, Environment(recovery.World).LastWarmthLossUnits);
            Assert.Equal((fatal ? 0 : 19) * CycleSurvival.Scale, Body(recovery.World).WarmthUnits);
            Assert.Equal(fatal ? CycleExpeditionOutcome.Dead : CycleExpeditionOutcome.Exploring, recovery.World.Expedition.Outcome);
        }
        using var final = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(baseline.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(1, Environment(final.State).Sequence);
        if (fatal) {
            Assert.Equal(StepStatus.Exhausted, await CycleEngine.CreateKernel(final,
                characterController: new ForbiddenController()).StepAsync(Hour(24)));
            Assert.Equal(1, Environment(final.State).Sequence);
        }
    }

    [Fact]
    public async Task Incremental_and_full_weather_traces_match_four_fixed_windows_with_identical_draw_count() {
        static CycleWorld Initial() => World(Hour(8), Hour(10), waitHours: 10);
        var incremental = CycleEngine.CreateKernel(Memory(Initial()), PredictionMode.Incremental, characterController: new ForbiddenController());
        var full = CycleEngine.CreateKernel(Memory(Initial()), PredictionMode.Full, characterController: new ForbiddenController());
        var results = new List<bool>();
        for (int index = 0; index < 4; index++) {
            await Advance(incremental);
            await Advance(full);
            Assert.Equal(CycleEventKind.WeatherResolved, LastKind(incremental));
            Assert.Equal(index + 1, Environment(incremental.World).Sequence);
            results.Add(Environment(incremental.World).LastCold);
            Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
            Assert.Equal(full.World.Expedition!.Spatial, incremental.World.Expedition!.Spatial);
            Assert.Equal(full.World.Spatial!.Contacts, incremental.World.Spatial!.Contacts);
            Assert.Equal(full.Cursor, incremental.Cursor);
            Assert.Equal(Describe(full.LastCompletion!.Event), Describe(incremental.LastCompletion!.Event));
        }
        Assert.Equal(new[] { true, false, false, true }, results);
        Assert.Equal(Hour(16), incremental.World.Time);
        Assert.Equal(88 * CycleSurvival.Scale, Body(incremental.World).WarmthUnits);
        Assert.Equal(36 * CycleSurvival.Scale, Body(incremental.World).WaterUnits);
    }

    private static CycleEnvironment Environment(CycleWorld world) => world.Expedition!.Environment!;
    private static CycleSurvival Body(CycleWorld world) => world.GetCharacter("a").Survival!.Project(world.Time);
    private static string Passage(string from, string to) => Assert.Single(CycleExpeditionMap.GetPassages(from), p => p.To == to).Id;
    private static CycleEventKind LastKind(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Single(kernel.LastCompletion!.Event.Facts).Kind;

    private static CycleWorld World(ModelTime at, ModelTime nextWeather, string place = "camp", ulong seed = ColdSeed,
        bool idle = false, CycleSurvival? needs = null, int waitHours = 4) {
        var initial = CycleGameFactory.CreateSurvival(at, enableNeeds: false, enableWeather: false);
        GraphSpatialState spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            [new("a", place, at, initial.World.Resources.Where(resource => CycleExpeditionMap.GetResourcePlace(resource.Id) == place))],
            environment: new(seed, 0, nextWeather));
        CycleActivity? activity = idle ? null : new(CycleActivityKind.Wait, 1, at, waitHours * CycleTime.Hour, new(at.Ticks + waitHours * CycleTime.Hour));
        var actor = new CycleCharacter("a", "Weather fixture", activity: activity, nextActivityId: idle ? 1 : 2,
            survival: needs ?? CycleSurvival.Create(at, 100, 100, 60, place == "cabin" ? 12 : CycleTime.IsDay(at) ? 6 : -12));
        return CycleEngine.Rebuild(initial.World.Business.Replace(at, changedCharacters: [actor], expedition: expedition), at);
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Same(kernel.World.Expedition!.Spatial, kernel.World.Spatial.Business);
    }

    private static async Task Until(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> done) {
        for (int steps = 0; steps < 30; steps++) {
            if (done(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected weather boundary was not reached.");
    }

    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleCommand> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This weather boundary must not call the character controller.");
    }
    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not forecast or sample.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not replan weather or sample again.");
    }
    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterWeather(IOccurrenceHistory<CycleWorld, CycleFact> inner) : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == CycleEventKind.WeatherResolved)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

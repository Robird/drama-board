using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class WeatherTests {
    // Fixed Kernel vectors for the weather/v1 stream: seed 0 => cold, calm, calm, cold; seed 1 => calm first.
    private const ulong ColdSeed = 0;
    private const ulong CalmSeed = 1;

    [Fact]
    public void Cold_open_rejects_a_silently_disabled_weather_stream_but_exhausted_streams_are_valid() {
        PlaygroundWorld initial = World(Hour(8), Hour(10));
        PlaygroundExpedition original = initial.Expedition!;
        var invalid = new PlaygroundExpedition(original.ScenarioId, original.Spatial, original.Visits,
            environment: new(ColdSeed, 0, null));
        PlaygroundBusinessState business = initial.Business.Replace(initial.Time, expedition: invalid);
        using var store = new TemporaryStore();
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            repository.CreateRootBranchFromState("missing-weather-deadline",
                PlaygroundDurableRoot.CreateRevisionCandidate(business, InitialCursor(initial), PlaygroundGameRevision.Current));
        }
        Assert.Throws<InvalidOperationException>(() => PlaygroundDurableHistory.Open(store.StorePath));

        // Null is reserved for limits of the address/time representation, not a gameplay off-switch.
        PlaygroundWeatherEffect[] previousEffects = [new("a", 0, false)];
        new PlaygroundEnvironment(ColdSeed, long.MaxValue, null, lastAt: Hour(8), lastEffects: previousEffects).Validate(Hour(8), initial.Characters);
        var lastRepresentableWindow = new ModelTime(long.MaxValue - PlaygroundEnvironmentRules.IntervalTicks + 1);
        new PlaygroundEnvironment(ColdSeed, 1, null, lastAt: lastRepresentableWindow, lastEffects: previousEffects).Validate(lastRepresentableWindow, initial.Characters);
        Assert.Throws<InvalidOperationException>(() => new PlaygroundEnvironment(ColdSeed, 1, null, lastAt: Hour(8), lastEffects: previousEffects).Validate(Hour(8), initial.Characters));
    }

    [Theory]
    [InlineData(ColdSeed, true)]
    [InlineData(CalmSeed, false)]
    public async Task Same_seed_gives_the_same_weather_in_shelter_and_outdoors_but_only_exposure_loses_warmth(ulong seed, bool cold) {
        PlaygroundWorld outdoors = World(Hour(8), Hour(10), seed: seed);
        PlaygroundWorld shelter = World(Hour(8), Hour(10), place: "cabin", seed: seed);
        var exposed = PlaygroundEngine.CreateKernel(Memory(outdoors), characterController: new ForbiddenController());
        var protectedKernel = PlaygroundEngine.CreateKernel(Memory(shelter), characterController: new ForbiddenController());
        await Advance(exposed);
        await Advance(protectedKernel);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, LastKind(exposed));
        Assert.Equal(cold, Environment(exposed.World).LastCold);
        Assert.Equal(cold, Environment(protectedKernel.World).LastCold);
        Assert.False(Environment(exposed.World).LastEffects.Single().Sheltered);
        Assert.True(Environment(protectedKernel.World).LastEffects.Single().Sheltered);
        Assert.Equal((cold ? 10 : 0) * PlaygroundSurvival.Scale, Environment(exposed.World).LastEffects.Single().WarmthLossUnits);
        Assert.Equal(0, Environment(protectedKernel.World).LastEffects.Single().WarmthLossUnits);
        Assert.Equal((cold ? 62 : 72) * PlaygroundSurvival.Scale, Body(exposed.World).WarmthUnits);
        Assert.Equal(84 * PlaygroundSurvival.Scale, Body(protectedKernel.World).WarmthUnits);
        Assert.Equal(1, Environment(exposed.World).Sequence);
        Assert.Equal(1, Environment(protectedKernel.World).Sequence);
        Assert.Equal(Hour(12), Environment(exposed.World).NextDue);
    }

    [Fact]
    public async Task Observation_prediction_checks_and_rejected_commands_do_not_advance_random_state() {
        PlaygroundWorld initial = World(Hour(8), Hour(10), idle: true);
        var history = Memory(initial);
        string before = initial.DescribeState();
        for (int repeat = 0; repeat < 3; repeat++) {
            PlaygroundObservation observation = PlaygroundDecisionContext.Observe(initial, "a");
            Assert.NotNull(PlaygroundActions.Check(initial, "a", PlaygroundCommand.Go("not-visible")));
            Assert.NotNull(PlaygroundActions.Check(initial, "a", PlaygroundCommand.Use("not-owned")));
            Assert.NotNull(PlaygroundEnvironmentRules.Observe(initial, "a"));
            Assert.Equal(initial.Forecasts.ToString(), PlaygroundEngine.Rebuild(initial.Business, initial.Time).Forecasts.ToString());
            string projection = JsonSerializer.Serialize(observation);
            Assert.DoesNotContain("\"Seed\"", projection, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Sequence\"", projection, StringComparison.Ordinal);
        }
        Assert.Equal(before, initial.DescribeState());
        var rejected = PlaygroundEngine.CreateKernel(history, characterController: Commands(PlaygroundCommand.Go("not-visible")));
        Assert.Equal(StepStatus.Committed, await rejected.StepAsync(Hour(240)));
        var opened = rejected.World;
        Assert.Equal(StepStatus.Committed, await rejected.StepAsync(Hour(12)));
        Assert.Equal(opened.Time, history.State.Time);
        Assert.Equal(MaterialSnapshot(opened.Expedition), MaterialSnapshot(history.State.Expedition));
        Assert.Same(opened.Expedition!.Environment, history.State.Expedition!.Environment);
        Assert.Same(opened.GetCharacter("a").Survival, history.State.GetCharacter("a").Survival);
        Assert.Equal(0, Environment(history.State).Sequence);
        Assert.Equal(Hour(10), Environment(history.State).NextDue);
        Assert.Equal(PlaygroundEventKind.OperationRejected, Assert.Single(history.CompletedEvents[^1].Facts).Kind);
        Assert.Null(history.PendingEvent);
        var accepted = PlaygroundEngine.CreateKernel(history, characterController: Commands(PlaygroundCommand.Wait(4)));
        await Advance(accepted);
        await Advance(accepted);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, LastKind(accepted));
        Assert.True(Environment(accepted.World).LastCold);
        Assert.Equal(1, Environment(accepted.World).Sequence);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Entering_shelter_protects_from_weather_at_or_after_arrival_but_not_one_tick_before(long weatherOffset, bool sheltered) {
        PlaygroundWorld initial = World(Hour(8), new(Hour(10).Ticks + weatherOffset), place: "ridge", idle: true);
        var history = Memory(initial);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController:
            Commands(PlaygroundCommand.Go(Passage("ridge", "cabin")), PlaygroundCommand.Wait(1)));
        await Until(kernel, world => Environment(world).Sequence == 1 && PlaygroundActions.CurrentPlace(world, "a") == "cabin");
        Assert.Equal(sheltered, Environment(kernel.World).LastEffects.Single().Sheltered);
        Assert.True(Environment(kernel.World).LastCold);
        Assert.Equal((sheltered ? 0 : 10) * PlaygroundSurvival.Scale, Environment(kernel.World).LastEffects.Single().WarmthLossUnits);
        if (weatherOffset == 0) {
            Assert.Equal(new[] { PlaygroundEventKind.MoveArrived, PlaygroundEventKind.WeatherResolved }, history.CompletedEvents
                .Where(e => e.TargetInstant.ModelTime == Hour(10)).SelectMany(e => e.Facts).Select(f => f.Kind));
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public async Task Personal_death_at_or_before_weather_does_not_draw_that_window(long weatherOffset, long expectedSequence) {
        var needs = new PlaygroundSurvival(Hour(8), 100 * PlaygroundSurvival.Scale, 24 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate,
            PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2);
        PlaygroundWorld initial = World(Hour(8), new(Hour(11).Ticks + weatherOffset), place: "ravine", idle: true, needs: needs);
        var source = Commands(PlaygroundCommand.Go(Passage("ravine", "road")));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: source);
        await Until(kernel, world => world.GetCharacter("a").Ending?.Outcome == PlaygroundExpeditionOutcome.Dead);
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
        var needs = new PlaygroundSurvival(Hour(8), 100 * PlaygroundSurvival.Scale, 16 * PlaygroundSurvival.Scale,
            60 * PlaygroundSurvival.Scale, PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2);
        PlaygroundWorld initial = World(Hour(8), Hour(10), place: "ridge", idle: true, needs: needs);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go(Passage("ridge", "cabin"))));
        await Advance(kernel);
        GraphSpatialState travelling = kernel.World.Expedition!.Spatial;
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.SurvivalFailed, LastKind(kernel));
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, kernel.World.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(Hour(10), kernel.World.Time);
        Assert.Equal(0, Environment(kernel.World).Sequence);
        Assert.Empty(kernel.World.Expedition!.Spatial.Entities);
        Assert.Equal(Assert.Single(travelling.Entities).Location, kernel.World.GetCharacter("a").Ending!.Location);
        Assert.Single(kernel.World.Expedition.Visits); // Death did not produce the coincident arrival.
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(12)));
    }

    [Fact]
    public async Task Cold_crossing_warning_line_is_recorded_before_separate_warning_and_character_input() {
        PlaygroundWorld initial = World(Hour(8), Hour(8), needs: PlaygroundSurvival.Create(Hour(8), 100, 100, 29));
        var history = Memory(initial);
        var source = Commands(PlaygroundCommand.Continue());
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, LastKind(kernel));
        Assert.Equal(19 * PlaygroundSurvival.Scale, Body(kernel.World).WarmthUnits);
        Assert.Equal(0, Body(kernel.World).PendingMask);
        Assert.Equal(0, source.Calls);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(4, Body(kernel.World).PendingMask);
        Assert.Equal(Hour(8), kernel.World.Time);
        await Advance(kernel);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, Environment(kernel.World).Sequence);
        Assert.Equal(0, Body(kernel.World).PendingMask);
    }

    [Fact]
    public async Task Weather_then_activity_completion_then_new_warning_all_settle_before_same_tick_input() {
        PlaygroundWorld initial = World(Hour(18), Hour(20), waitHours: 2,
            needs: PlaygroundSurvival.Create(Hour(18), 100, 100, 59, warmthRate: PlaygroundTestSupport.Rate(-12)));
        var history = Memory(initial);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: new ForbiddenController());
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, LastKind(kernel));
        Assert.Equal(25 * PlaygroundSurvival.Scale, Body(kernel.World).WarmthUnits); // 59 - 24 elapsed - 10 exposure.
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WaitCompleted, LastKind(kernel));
        Assert.Null(kernel.World.GetCharacter("a").Activity);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.SurvivalWarning, LastKind(kernel));
        Assert.Equal(Hour(20), kernel.World.Time);
        Assert.Equal(4, Body(kernel.World).PendingMask);
        Assert.Equal(new[] { PlaygroundEventKind.WeatherResolved, PlaygroundEventKind.WaitCompleted, PlaygroundEventKind.SurvivalWarning },
            history.CompletedEvents.SelectMany(e => e.Facts).Select(fact => fact.Kind));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task State_cold_open_before_or_at_weather_does_not_draw_until_normal_step(long tickOffset) {
        using var store = new TemporaryStore();
        ModelTime savedTime = new(Hour(10).Ticks + tickOffset);
        PlaygroundWorld initial = World(savedTime, Hour(10));
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        using (PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) { }
        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Equal(savedTime, reopened.Cursor.CurrentModelTime);
        Assert.Equal(0, Environment(reopened.State).Sequence);
        Assert.Null(Environment(reopened.State).LastAt);
        Assert.Null(reopened.PendingEvent);
        var resumed = PlaygroundEngine.CreateKernel(reopened, characterController: new ForbiddenController());
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
        var needs = new PlaygroundSurvival(Hour(8), 100 * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale,
            (fatal ? 9 : 29) * PlaygroundSurvival.Scale, PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: fatal ? 4 : 0);
        PlaygroundWorld initial = World(Hour(8), Hour(8), needs: needs);
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        await Advance(baseline);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, LastKind(baseline));
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var interrupted = PlaygroundEngine.CreateKernel(new StopAfterWeather(history), characterController: new ForbiddenController());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.StepAsync(Hour(12)).AsTask());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(0, Environment(history.State).Sequence);
            Assert.Equal(initial.DescribeState(), history.State.DescribeState());
        }
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(0, Environment(reopened.State).Sequence);
            Assert.Equal(InitialCursor(initial), reopened.Cursor);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(reopened.PendingEvent!));
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
            Assert.Equal(1, Environment(recovery.World).Sequence);
            Assert.Equal((fatal ? 9 : 10) * PlaygroundSurvival.Scale, Environment(recovery.World).LastEffects.Single().WarmthLossUnits);
            Assert.Equal((fatal ? 0 : 19) * PlaygroundSurvival.Scale, Body(recovery.World).WarmthUnits);
            Assert.Equal(fatal ? PlaygroundExpeditionOutcome.Dead : (PlaygroundExpeditionOutcome?)null, recovery.World.GetCharacter("a").Ending?.Outcome);
        }
        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(baseline.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(1, Environment(final.State).Sequence);
        if (fatal) {
            Assert.Equal(StepStatus.Exhausted, await PlaygroundEngine.CreateKernel(final,
                characterController: new ForbiddenController()).StepAsync(Hour(24)));
            Assert.Equal(1, Environment(final.State).Sequence);
        }
    }

    [Fact]
    public async Task Incremental_and_full_weather_traces_match_four_fixed_windows_with_identical_draw_count() {
        static PlaygroundWorld Initial() => World(Hour(8), Hour(10), waitHours: 10);
        var incremental = PlaygroundEngine.CreateKernel(Memory(Initial()), PredictionMode.Incremental, characterController: new ForbiddenController());
        var full = PlaygroundEngine.CreateKernel(Memory(Initial()), PredictionMode.Full, characterController: new ForbiddenController());
        var results = new List<bool>();
        for (int index = 0; index < 4; index++) {
            await Advance(incremental);
            await Advance(full);
            Assert.Equal(PlaygroundEventKind.WeatherResolved, LastKind(incremental));
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
        Assert.Equal(88 * PlaygroundSurvival.Scale, Body(incremental.World).WarmthUnits);
        Assert.Equal(36 * PlaygroundSurvival.Scale, Body(incremental.World).WaterUnits);
    }

    private static PlaygroundEnvironment Environment(PlaygroundWorld world) => world.Expedition!.Environment!;
    private static PlaygroundSurvival Body(PlaygroundWorld world) => world.GetCharacter("a").Survival!.Project(world.Time);
    private static string Passage(string from, string to) => Assert.Single(PlaygroundExpeditionMap.GetEntrances(from), p => p.To == to).EntranceId;
    private static PlaygroundEventKind LastKind(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        Assert.Single(kernel.LastCompletion!.Event.Facts).Kind;

    private static PlaygroundWorld World(ModelTime at, ModelTime nextWeather, string place = "camp", ulong seed = ColdSeed,
        bool idle = false, PlaygroundSurvival? needs = null, int waitHours = 4) {
        var initial = PlaygroundGameFactory.CreateSurvival(at, enableNeeds: false, enableWeather: false);
        GraphSpatialState spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            [new("a", place, at, initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == place))],
            environment: new(seed, 0, nextWeather));
        PlaygroundActivity? activity = idle ? null : new(PlaygroundActivityKind.Wait, 1, at, waitHours * PlaygroundTime.Hour, new(at.Ticks + waitHours * PlaygroundTime.Hour));
        var actor = new PlaygroundCharacter("a", "Weather fixture", activity: activity, nextActivityId: idle ? 1 : 2,
            survival: needs ?? PlaygroundSurvival.Create(at, 100, 100, 60, PlaygroundSurvivalRules.WarmthRate(place == "cabin", PlaygroundTime.IsDay(at))));
        PlaygroundWorldDefinition definition = PlaygroundWorldDefinitions.FromLegacy(at, initial.World.Resources, [actor], expedition);
        var business = new PlaygroundBusinessState(at, initial.World.Light, initial.World.Resources, initial.World.Script,
            [actor], expedition: expedition, definition: definition);
        return PlaygroundEngine.Rebuild(business, at);
    }

    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        await StepWorldEvent(kernel);
        kernel.World.Validate();
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Same(kernel.World.Expedition!.Spatial, kernel.World.Spatial.Business);
    }

    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> done) {
        for (int steps = 0; steps < 30; steps++) {
            if (done(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected weather boundary was not reached.");
    }

    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This weather boundary must not call the character controller.");
    }
    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not forecast or sample.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not replan weather or sample again.");
    }
    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterWeather(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner) : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == PlaygroundEventKind.WeatherResolved)) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}

using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class SurvivalForecastTests {
    [Fact]
    public async Task Sunset_estimate_matches_actual_no_weather_depletion_without_repeated_action_warning() {
        ModelTime at = Minute(1050); // 17:30
        PlaygroundWorld world = World(PlaygroundSurvival.Create(at, food: 100, water: 100, warmth: 60));
        ModelTime expected = Minute(1395); // 18:00 warmth 63; 63 / 12 hours later is 23:15.
        Assert.Equal(expected, PlaygroundSurvivalRules.Observe(world, "a")!.FatalAt);
        Assert.Null(PlaygroundActions.Check(world, "a", PlaygroundCommand.Wait(6)));
        Assert.Null(PlaygroundSurvivalRules.ActionWarning(world, "a", PlaygroundCommand.Wait(6)));

        var kernel = PlaygroundEngine.CreateKernel(Memory(world),
            characterController: Commands(PlaygroundCommand.Wait(6), PlaygroundCommand.Continue()));
        bool sawSunset = false;
        for (int steps = 0; steps < 12 && kernel.World.GetCharacter("a").IsActive; steps++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            if (Assert.Single(kernel.LastCompletion!.Event.Facts).Kind == PlaygroundEventKind.LightChanged) {
                sawSunset = true;
                Assert.Equal(Hour(18), kernel.World.Time);
                Assert.Equal(63 * PlaygroundSurvival.Scale, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
                Assert.Equal(expected, PlaygroundSurvivalRules.Observe(kernel.World, "a")!.FatalAt);
            }
        }
        Assert.True(sawSunset);
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, kernel.World.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(expected, kernel.World.Time);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Exposed_estimate_survives_residual_night_and_day_before_the_next_night_exhausts_warmth() {
        // Isolate warmth: 23:00=100, next 06:00=16, next 18:00=88, then 01:20=0.
        PlaygroundWorld world = World(new(Hour(23), 100 * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, foodRate: PlaygroundTestSupport.Rate(0), waterRate: PlaygroundTestSupport.Rate(0), warmthRate: PlaygroundTestSupport.Rate(-12)));
        ModelTime expected = Minute(2960); // Day 2, 01:20.
        Assert.Equal(expected, PlaygroundSurvivalRules.Observe(world, "a")!.FatalAt);
        Assert.Null(PlaygroundActions.Check(world, "a", PlaygroundCommand.Wait(24)));
        Assert.Null(PlaygroundSurvivalRules.ActionWarning(world, "a", PlaygroundCommand.Wait(24)));
        Assert.Equal(Minute(1755), PlaygroundSurvivalRules.Observe(world, "a")!.WarmthForecast.WarningAt); // Day 1, 05:15.
    }

    [Fact]
    public void Shelter_removes_night_cooling_but_water_still_exhausts() {
        PlaygroundWorld world = World(PlaygroundSurvival.Create(Hour(17), food: 100, water: 40, warmth: 10, warmthRate: PlaygroundTestSupport.Rate(12)), "cabin");
        PlaygroundSurvivalObservation observation = PlaygroundSurvivalRules.Observe(world, "a")!;
        Assert.True(observation.Sheltered);
        Assert.Equal(Hour(22), observation.FatalAt);
        Assert.Null(PlaygroundActions.Check(world, "a", PlaygroundCommand.Wait(6)));
        Assert.Null(PlaygroundSurvivalRules.ActionWarning(world, "a", PlaygroundCommand.Wait(6)));
        Assert.Null(observation.WarmthForecast.DepletionAt);
        Assert.Equal(world.Time, observation.WarmthForecast.WarningAt);
    }

    [Fact]
    public async Task Already_dead_zero_warmth_is_not_revived_by_a_daylight_estimate() {
        PlaygroundWorld initial = World(PlaygroundSurvival.Create(Hour(8), food: 100, water: 100, warmth: 0));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(8)));
        PlaygroundWorld world = kernel.World;
        Assert.Equal(CharacterContextStatus.Terminal, world.Business.InteractionStates["a"].CurrentContext!.Status);
        PlaygroundSurvivalObservation observation = PlaygroundSurvivalRules.Observe(world, "a")!;
        Assert.Equal(0m, observation.Warmth);
        Assert.Equal(Hour(8), observation.FatalAt);
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, world.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(0, world.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Repeated_queries_leave_survival_and_weather_unchanged_and_exclude_random_draws_from_estimate() {
        PlaygroundWorld world = World(PlaygroundSurvival.Create(Minute(1050), food: 100, water: 100, warmth: 60), weather: true);
        PlaygroundSurvival body = world.GetCharacter("a").Survival!;
        PlaygroundEnvironment weather = world.Expedition!.Environment!;
        string before = world.DescribeState();
        PlaygroundForecasts forecasts = world.Forecasts;
        for (int repeat = 0; repeat < 3; repeat++) {
            PlaygroundObservation observation = PlaygroundDecisionContext.Observe(world, "a");
            Assert.Null(observation.CheckDisplayed(PlaygroundCommand.Wait(6)));
            Assert.Equal(Minute(1395), PlaygroundSurvivalRules.Observe(world, "a")!.FatalAt);
            Assert.Null(PlaygroundSurvivalRules.ActionWarning(world, "a", PlaygroundCommand.Wait(6)));
            Assert.Equal(Minute(1395), observation.Survival!.WarmthForecast.DepletionAt);
        }
        Assert.Same(body, world.GetCharacter("a").Survival);
        Assert.Same(weather, world.Expedition.Environment);
        Assert.Equal(0, weather.Sequence);
        Assert.Null(weather.LastAt);
        Assert.Equal(before, world.DescribeState());
        AssertForecasts(forecasts, world.Forecasts);
    }

    [Fact]
    public void Each_attribute_retains_its_own_threshold_times_after_another_would_have_depleted() {
        PlaygroundWorld world = World(PlaygroundSurvival.Create(Hour(15), food: 30, water: 45, warmth: 60));
        PlaygroundSurvivalObservation observed = PlaygroundSurvivalRules.Observe(world, "a")!;
        Assert.Equal(new PlaygroundAttributeForecast(Hour(16), Hour(21)), observed.FoodForecast);
        Assert.Equal(new PlaygroundAttributeForecast(Minute(1050), new(Hour(20).Ticks + 75 * PlaygroundTime.Hour / 120)), observed.WaterForecast);
        Assert.Equal(new PlaygroundAttributeForecast(new(Hour(18).Ticks + 53 * PlaygroundTime.Hour / 12), Minute(1470)), observed.WarmthForecast);
        Assert.Equal(observed.WaterForecast.DepletionAt, observed.FatalAt);
        Assert.True(observed.FoodForecast.DepletionAt > observed.FatalAt);
        Assert.True(observed.WarmthForecast.DepletionAt > observed.FatalAt);
    }

    [Fact]
    public void Threshold_estimate_is_independent_of_warning_delivery_and_acknowledgement() {
        ModelTime at = Hour(15);
        PlaygroundSurvival initial = PlaygroundSurvival.Create(at, food: 25, water: 50);
        PlaygroundSurvival warned = initial.Warn(at);
        PlaygroundSurvivalObservation before = PlaygroundSurvivalRules.Observe(World(initial), "a")!;
        PlaygroundSurvivalObservation pending = PlaygroundSurvivalRules.Observe(World(warned), "a")!;
        PlaygroundSurvivalObservation acknowledged = PlaygroundSurvivalRules.Observe(World(warned.Acknowledge(at)), "a")!;
        Assert.Equal(new PlaygroundAttributeForecast(at, Hour(20)), before.FoodForecast);
        Assert.Equal(before.FoodForecast, pending.FoodForecast);
        Assert.Equal(before.FoodForecast, acknowledged.FoodForecast);
        Assert.Empty(before.PendingWarnings);
        Assert.NotEmpty(pending.PendingWarnings);
        Assert.Empty(acknowledged.PendingWarnings);
    }

    [Fact]
    public void Supply_changes_only_relevant_forecasts_and_preserves_pending_historical_warning() {
        ModelTime at = Hour(15);
        PlaygroundSurvival initial = PlaygroundSurvival.Create(at, food: 100, water: 25).Warn(at);
        PlaygroundSurvivalObservation before = PlaygroundSurvivalRules.Observe(World(initial), "a")!;
        PlaygroundSurvivalObservation after = PlaygroundSurvivalRules.Observe(World(initial.Supply(at, "item.water")), "a")!;
        Assert.Equal(before.FoodForecast, after.FoodForecast);
        Assert.Equal(before.WarmthForecast, after.WarmthForecast);
        Assert.NotEqual(before.WaterForecast, after.WaterForecast);
        Assert.Equal(Hour(15).Ticks + 45 * PlaygroundTime.Hour / 8, after.WaterForecast.WarningAt!.Value.Ticks);
        Assert.Equal(before.PendingWarnings, after.PendingWarnings);
    }

    [Fact]
    public void Shelter_departure_shows_only_in_segment_warmth_exception_without_hidden_destination() {
        PlaygroundWorld world = World(PlaygroundSurvival.Create(Hour(20), warmth: 30), "cabin", weather: true);
        string warning = PlaygroundSurvivalRules.ActionWarning(world, "a", PlaygroundCommand.Go("e-o7"))!;
        Assert.Contains("失去庇护", warning);
        Assert.Contains($"温暖预计降至 25：{Minute(1225)}", warning);
        Assert.Contains($"温暖预计耗尽至 0：{Minute(1350)}", warning);
        Assert.DoesNotContain("食物", warning);
        Assert.DoesNotContain("饮水", warning);
        Assert.DoesNotContain("溪流湾", warning);
        Assert.Null(PlaygroundSurvivalRules.ActionWarning(world, "a", PlaygroundCommand.Wait(24)));
        PlaygroundWorld highWarmth = World(PlaygroundSurvival.Create(Hour(20), warmth: 100), "cabin");
        string highWarning = PlaygroundSurvivalRules.ActionWarning(highWarmth, "a", PlaygroundCommand.Go("e-o7"))!;
        Assert.Contains("失去庇护", highWarning);
        Assert.DoesNotContain("预计", highWarning);
        Assert.Null(PlaygroundSurvivalRules.ActionWarning(World(PlaygroundSurvival.Create(Hour(20))), "a", PlaygroundCommand.Go("e-c7")));
    }

    [Fact]
    public void Warmth_projection_accounts_for_upper_clamp_before_sunset() {
        PlaygroundWorld world = World(PlaygroundSurvival.Create(Hour(6), food: 100, water: 100, warmth: 100));
        PlaygroundAttributeForecast forecast = PlaygroundSurvivalRules.Observe(world, "a")!.WarmthForecast;
        Assert.Equal(Minute(1455), forecast.WarningAt); // Next 00:15, 75 points after sunset.
        Assert.Equal(Minute(1580), forecast.DepletionAt); // Next 02:20.
    }

    [Fact]
    public void Forecasts_clip_at_representable_time_and_reject_malformed_saved_shapes() {
        var at = new ModelTime(long.MaxValue - 1);
        PlaygroundAttributeForecast zero = PlaygroundSurvivalRules.ForecastConstant(PlaygroundSurvival.MaximumUnits, 0, at);
        PlaygroundAttributeForecast positive = PlaygroundSurvivalRules.ForecastConstant(PlaygroundSurvival.MaximumUnits, 1, at);
        PlaygroundAttributeForecast warmth = PlaygroundSurvivalRules.ForecastWarmth(PlaygroundSurvival.MaximumUnits, at, false);
        Assert.Null(zero.WarningAt);
        Assert.Null(positive.DepletionAt);
        Assert.Null(warmth.DepletionAt);
        warmth.Validate(at);
        Assert.Throws<InvalidOperationException>(() => new PlaygroundAttributeForecast(null, Hour(9)).Validate(Hour(8)));
        Assert.Throws<InvalidOperationException>(() => new PlaygroundAttributeForecast(Hour(10), Hour(9)).Validate(Hour(8)));
        Assert.Throws<InvalidOperationException>(() => new PlaygroundAttributeForecast(Hour(7), Hour(9)).Validate(Hour(8)));
    }

    private static PlaygroundWorld World(PlaygroundSurvival body, string place = "camp", bool weather = false) {
        var initial = PlaygroundGameFactory.CreateSurvival(body.BaselineAt, enableWeather: weather);
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            [new("a", place, body.BaselineAt, initial.World.Resources.Where(r => PlaygroundExpeditionMap.GetResourcePlace(r.Id) == place))],
            environment: initial.World.Expedition!.Environment);
        PlaygroundCharacter actor = initial.World.GetCharacter("a").WithSurvival(body);
        return PlaygroundEngine.Rebuild(initial.World.Business.Replace(body.BaselineAt,
            changedCharacters: [actor], expedition: expedition), body.BaselineAt);
    }
}

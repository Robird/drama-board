using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class SurvivalForecastTests {
    [Fact]
    public async Task Sunset_estimate_matches_actual_no_weather_depletion_without_repeated_action_warning() {
        ModelTime at = Minute(1050); // 17:30
        CycleWorld world = World(CycleSurvival.Create(at, food: 100, water: 100, warmth: 60));
        ModelTime expected = Minute(1395); // 18:00 warmth 63; 63 / 12 hours later is 23:15.
        Assert.Equal(expected, CycleSurvivalRules.Observe(world, "a")!.FatalAt);
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Wait(6)));
        Assert.Null(CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(6)));

        var kernel = CycleEngine.CreateKernel(Memory(world),
            characterController: Commands(CycleCommand.Wait(6), CycleCommand.Continue()));
        bool sawSunset = false;
        for (int steps = 0; steps < 12 && kernel.World.Expedition!.Outcome != CycleExpeditionOutcome.Dead; steps++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            if (Assert.Single(kernel.LastCompletion!.Event.Facts).Kind == CycleEventKind.LightChanged) {
                sawSunset = true;
                Assert.Equal(Hour(18), kernel.World.Time);
                Assert.Equal(63 * CycleSurvival.Scale, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
                Assert.Equal(expected, CycleSurvivalRules.Observe(kernel.World, "a")!.FatalAt);
            }
        }
        Assert.True(sawSunset);
        Assert.Equal(CycleExpeditionOutcome.Dead, kernel.World.Expedition!.Outcome);
        Assert.Equal(expected, kernel.World.Time);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Exposed_estimate_survives_residual_night_and_day_before_the_next_night_exhausts_warmth() {
        // Isolate warmth: 23:00=100, next 06:00=16, next 18:00=88, then 01:20=0.
        CycleWorld world = World(new(Hour(23), 100 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            100 * CycleSurvival.Scale, foodRate: CycleTestSupport.Rate(0), waterRate: CycleTestSupport.Rate(0), warmthRate: CycleTestSupport.Rate(-12)));
        ModelTime expected = Minute(2960); // Day 2, 01:20.
        Assert.Equal(expected, CycleSurvivalRules.Observe(world, "a")!.FatalAt);
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Wait(24)));
        Assert.Null(CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(24)));
        Assert.Equal(Minute(1755), CycleSurvivalRules.Observe(world, "a")!.WarmthForecast.WarningAt); // Day 1, 05:15.
    }

    [Fact]
    public void Shelter_removes_night_cooling_but_water_still_exhausts() {
        CycleWorld world = World(CycleSurvival.Create(Hour(17), food: 100, water: 40, warmth: 10, warmthRate: CycleTestSupport.Rate(12)), "cabin");
        CycleSurvivalObservation observation = CycleSurvivalRules.Observe(world, "a")!;
        Assert.True(observation.Sheltered);
        Assert.Equal(Hour(22), observation.FatalAt);
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Wait(6)));
        Assert.Null(CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(6)));
        Assert.Null(observation.WarmthForecast.DepletionAt);
        Assert.Equal(world.Time, observation.WarmthForecast.WarningAt);
    }

    [Fact]
    public void Already_dead_zero_warmth_is_not_revived_by_a_daylight_estimate() {
        CycleWorld world = World(CycleSurvival.Create(Hour(8), food: 100, water: 100, warmth: 0), dead: true);
        CycleSurvivalObservation observation = CycleSurvivalRules.Observe(world, "a")!;
        Assert.Equal(0m, observation.Warmth);
        Assert.Equal(Hour(8), observation.FatalAt);
        Assert.Equal(CycleExpeditionOutcome.Dead, world.Expedition!.Outcome);
        Assert.Equal(0, world.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Repeated_queries_leave_survival_and_weather_unchanged_and_exclude_random_draws_from_estimate() {
        CycleWorld world = World(CycleSurvival.Create(Minute(1050), food: 100, water: 100, warmth: 60), weather: true);
        CycleSurvival body = world.GetCharacter("a").Survival!;
        CycleEnvironment weather = world.Expedition!.Environment!;
        string before = world.DescribeState();
        string forecasts = world.Forecasts.ToString();
        for (int repeat = 0; repeat < 3; repeat++) {
            CycleObservation observation = CycleDecisionContext.Observe(world, "a");
            Assert.Null(observation.CheckDisplayed(CycleCommand.Wait(6)));
            Assert.Equal(Minute(1395), CycleSurvivalRules.Observe(world, "a")!.FatalAt);
            Assert.Null(CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(6)));
            Assert.Equal(Minute(1395), observation.Survival!.WarmthForecast.DepletionAt);
        }
        Assert.Same(body, world.GetCharacter("a").Survival);
        Assert.Same(weather, world.Expedition.Environment);
        Assert.Equal(0, weather.Sequence);
        Assert.Null(weather.LastAt);
        Assert.Equal(before, world.DescribeState());
        Assert.Equal(forecasts, world.Forecasts.ToString());
    }

    [Fact]
    public void Each_attribute_retains_its_own_threshold_times_after_another_would_have_depleted() {
        CycleWorld world = World(CycleSurvival.Create(Hour(15), food: 30, water: 45, warmth: 60));
        CycleSurvivalObservation observed = CycleSurvivalRules.Observe(world, "a")!;
        Assert.Equal(new CycleAttributeForecast(Hour(16), Hour(21)), observed.FoodForecast);
        Assert.Equal(new CycleAttributeForecast(Minute(1050), new(Hour(20).Ticks + 75 * CycleTime.Hour / 120)), observed.WaterForecast);
        Assert.Equal(new CycleAttributeForecast(new(Hour(18).Ticks + 53 * CycleTime.Hour / 12), Minute(1470)), observed.WarmthForecast);
        Assert.Equal(observed.WaterForecast.DepletionAt, observed.FatalAt);
        Assert.True(observed.FoodForecast.DepletionAt > observed.FatalAt);
        Assert.True(observed.WarmthForecast.DepletionAt > observed.FatalAt);
    }

    [Fact]
    public void Threshold_estimate_is_independent_of_warning_delivery_and_acknowledgement() {
        ModelTime at = Hour(15);
        CycleSurvival initial = CycleSurvival.Create(at, food: 25, water: 50);
        CycleSurvival warned = initial.Warn(at);
        CycleSurvivalObservation before = CycleSurvivalRules.Observe(World(initial), "a")!;
        CycleSurvivalObservation pending = CycleSurvivalRules.Observe(World(warned), "a")!;
        CycleSurvivalObservation acknowledged = CycleSurvivalRules.Observe(World(warned.Acknowledge(at)), "a")!;
        Assert.Equal(new CycleAttributeForecast(at, Hour(20)), before.FoodForecast);
        Assert.Equal(before.FoodForecast, pending.FoodForecast);
        Assert.Equal(before.FoodForecast, acknowledged.FoodForecast);
        Assert.Empty(before.PendingWarnings);
        Assert.NotEmpty(pending.PendingWarnings);
        Assert.Empty(acknowledged.PendingWarnings);
    }

    [Fact]
    public void Supply_changes_only_relevant_forecasts_and_preserves_pending_historical_warning() {
        ModelTime at = Hour(15);
        CycleSurvival initial = CycleSurvival.Create(at, food: 100, water: 25).Warn(at);
        CycleSurvivalObservation before = CycleSurvivalRules.Observe(World(initial), "a")!;
        CycleSurvivalObservation after = CycleSurvivalRules.Observe(World(initial.Supply(at, "item.water")), "a")!;
        Assert.Equal(before.FoodForecast, after.FoodForecast);
        Assert.Equal(before.WarmthForecast, after.WarmthForecast);
        Assert.NotEqual(before.WaterForecast, after.WaterForecast);
        Assert.Equal(Hour(15).Ticks + 45 * CycleTime.Hour / 8, after.WaterForecast.WarningAt!.Value.Ticks);
        Assert.Equal(before.PendingWarnings, after.PendingWarnings);
    }

    [Fact]
    public void Shelter_departure_shows_only_in_segment_warmth_exception_without_hidden_destination() {
        CycleWorld world = World(CycleSurvival.Create(Hour(20), warmth: 30), "cabin", weather: true);
        string warning = CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Go("trail-7"))!;
        Assert.Contains("失去庇护", warning);
        Assert.Contains($"温暖预计降至 25：{Minute(1225)}", warning);
        Assert.Contains($"温暖预计耗尽至 0：{Minute(1350)}", warning);
        Assert.DoesNotContain("食物", warning);
        Assert.DoesNotContain("饮水", warning);
        Assert.DoesNotContain("溪流湾", warning);
        Assert.Null(CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(24)));
        CycleWorld highWarmth = World(CycleSurvival.Create(Hour(20), warmth: 100), "cabin");
        string highWarning = CycleSurvivalRules.ActionWarning(highWarmth, "a", CycleCommand.Go("trail-7"))!;
        Assert.Contains("失去庇护", highWarning);
        Assert.DoesNotContain("预计", highWarning);
        Assert.Null(CycleSurvivalRules.ActionWarning(World(CycleSurvival.Create(Hour(20))), "a", CycleCommand.Go("trail-1")));
    }

    [Fact]
    public void Warmth_projection_accounts_for_upper_clamp_before_sunset() {
        CycleWorld world = World(CycleSurvival.Create(Hour(6), food: 100, water: 100, warmth: 100));
        CycleAttributeForecast forecast = CycleSurvivalRules.Observe(world, "a")!.WarmthForecast;
        Assert.Equal(Minute(1455), forecast.WarningAt); // Next 00:15, 75 points after sunset.
        Assert.Equal(Minute(1580), forecast.DepletionAt); // Next 02:20.
    }

    [Fact]
    public void Forecasts_clip_at_representable_time_and_reject_malformed_saved_shapes() {
        var at = new ModelTime(long.MaxValue - 1);
        CycleAttributeForecast zero = CycleSurvivalRules.ForecastConstant(CycleSurvival.MaximumUnits, 0, at);
        CycleAttributeForecast positive = CycleSurvivalRules.ForecastConstant(CycleSurvival.MaximumUnits, 1, at);
        CycleAttributeForecast warmth = CycleSurvivalRules.ForecastWarmth(CycleSurvival.MaximumUnits, at, false);
        Assert.Null(zero.WarningAt);
        Assert.Null(positive.DepletionAt);
        Assert.Null(warmth.DepletionAt);
        warmth.Validate(at);
        Assert.Throws<InvalidOperationException>(() => new CycleAttributeForecast(null, Hour(9)).Validate(Hour(8)));
        Assert.Throws<InvalidOperationException>(() => new CycleAttributeForecast(Hour(10), Hour(9)).Validate(Hour(8)));
        Assert.Throws<InvalidOperationException>(() => new CycleAttributeForecast(Hour(7), Hour(9)).Validate(Hour(8)));
    }

    private static CycleWorld World(CycleSurvival body, string place = "camp", bool dead = false, bool weather = false) {
        var initial = CycleGameFactory.CreateSurvival(body.BaselineAt, enableWeather: weather);
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            [new("a", place, body.BaselineAt, initial.World.Resources.Where(r => CycleExpeditionMap.GetResourcePlace(r.Id) == place))],
            outcome: dead ? CycleExpeditionOutcome.Dead : CycleExpeditionOutcome.Exploring,
            endingReason: dead ? "失温耗尽" : null, environment: initial.World.Expedition!.Environment);
        CycleCharacter actor = initial.World.GetCharacter("a").WithSurvival(body);
        return CycleEngine.Rebuild(initial.World.Business.Replace(body.BaselineAt,
            changedCharacters: [actor], expedition: expedition), body.BaselineAt);
    }
}

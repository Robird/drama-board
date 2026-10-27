using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class SurvivalForecastTests {
    [Fact]
    public async Task Sunset_estimate_matches_actual_no_weather_depletion_and_action_warning() {
        ModelTime at = Minute(1050); // 17:30
        CycleWorld world = World(CycleSurvival.Create(at, food: 100, water: 100, warmth: 60));
        ModelTime expected = Minute(1395); // 18:00 warmth 63; 63 / 12 hours later is 23:15.
        Assert.Equal(expected, CycleSurvivalRules.Observe(world, "a")!.FatalAt);
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Wait(6)));
        Assert.Contains($"身体将在 {expected} 耗尽", CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(6))!);

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
            100 * CycleSurvival.Scale, foodPerHour: 0, waterPerHour: 0, warmthPerHour: -12));
        ModelTime expected = Minute(2960); // Day 2, 01:20.
        Assert.Equal(expected, CycleSurvivalRules.Observe(world, "a")!.FatalAt);
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Wait(24)));
        string warning = CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(24))!;
        Assert.DoesNotContain("身体将在", warning); // Fatality is beyond the proposed 24-hour action.
        Assert.Contains($"预计 {Minute(1755)} 触发身体警告", warning); // Day 1, 05:15.
    }

    [Fact]
    public void Shelter_removes_night_cooling_but_water_still_exhausts() {
        CycleWorld world = World(CycleSurvival.Create(Hour(17), food: 100, water: 40, warmth: 10, warmthPerHour: 12), "cabin");
        CycleSurvivalObservation observation = CycleSurvivalRules.Observe(world, "a")!;
        Assert.True(observation.Sheltered);
        Assert.Equal(Hour(22), observation.FatalAt);
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Wait(6)));
        Assert.Contains($"身体将在 {Hour(22)} 耗尽", CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(6))!);
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
            CycleDecisionContext context = CycleDecisionContext.Create(world, "a");
            Assert.Null(context.Check(CycleCommand.Wait(6)));
            Assert.Equal(Minute(1395), CycleSurvivalRules.Observe(world, "a")!.FatalAt);
            string warning = CycleSurvivalRules.ActionWarning(world, "a", CycleCommand.Wait(6))!;
            Assert.Contains("不计随机寒风", warning);
            Assert.Contains($"身体将在 {Minute(1395)} 耗尽", warning);
        }
        Assert.Same(body, world.GetCharacter("a").Survival);
        Assert.Same(weather, world.Expedition.Environment);
        Assert.Equal(0, weather.Sequence);
        Assert.Null(weather.LastAt);
        Assert.Equal(before, world.DescribeState());
        Assert.Equal(forecasts, world.Forecasts.ToString());
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

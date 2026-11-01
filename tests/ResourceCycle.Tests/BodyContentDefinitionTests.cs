using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class BodyContentDefinitionTests {
    [Fact]
    public async Task Gentle_exposed_cycle_has_no_depletion_and_runs_three_days_without_false_fatal_forecasts() {
        CycleWorldDefinition definition = Outdoors(new(FoodPerHour: 0, WaterPerHour: 0, DayWarmthPerHour: 6, NightWarmthPerHour: -6), Hour(8), 80);
        var initial = CycleGameFactory.Create(definition);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new WaitAndContinue());
        int steps = 0;
        while (kernel.World.Time < Hour(80) && steps++ < 150) {
            CycleSurvivalObservation observed = CycleSurvivalRules.Observe(kernel.World, "a")!;
            Assert.Null(observed.WarmthForecast.DepletionAt);
            Assert.Null(observed.WarmthForecast.WarningAt);
            Assert.True(kernel.World.GetCharacter("a").IsActive);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(80)));
        }
        Assert.Equal(Hour(80), kernel.World.Time);
        Assert.True(kernel.World.GetCharacter("a").IsActive);
        Assert.Null(CycleSurvivalRules.Observe(kernel.World, "a")!.FatalAt);
    }

    [Fact]
    public async Task Slower_negative_daily_drift_predicts_and_reaches_depletion_on_the_third_night() {
        // Dusk warmth: 100, 88, 76. The first two dawns retain 16 and 4;
        // the third night exhausts 76 at seven points/hour, rounded to the first depleted tick.
        CycleWorldDefinition definition = Outdoors(new(FoodPerHour: 0, WaterPerHour: 0, DayWarmthPerHour: 6, NightWarmthPerHour: -7), Hour(18), 100);
        var initial = CycleGameFactory.Create(definition);
        ModelTime warning = new(Hour(18).Ticks + (75 * CycleTime.Hour + 6) / 7);
        ModelTime depleted = new(Hour(66).Ticks + (76 * CycleTime.Hour + 6) / 7);
        CycleSurvivalObservation observed = CycleSurvivalRules.Observe(initial.World, "a")!;
        Assert.Equal(warning, observed.WarmthForecast.WarningAt);
        Assert.Equal(depleted, observed.WarmthForecast.DepletionAt);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new WaitAndContinue());
        for (int step = 0; kernel.World.GetCharacter("a").IsActive && step < 150; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(90)));
        }
        Assert.Equal(depleted, kernel.World.Time);
        Assert.Equal(CycleExpeditionOutcome.Dead, kernel.World.GetCharacter("a").Ending?.Outcome);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Zero_rates_return_absent_thresholds_instead_of_requiring_eventual_depletion() {
        var definition = Outdoors(new(0, 0, 0, 0, 0), Hour(8), 40);
        CycleWorld world = CycleGameFactory.Create(definition).World;
        CycleSurvivalObservation observed = CycleSurvivalRules.Observe(world, "a")!;
        Assert.Equal(0, observed.FoodPerHour);
        Assert.Equal(0, observed.WaterPerHour);
        Assert.Equal(0, observed.WarmthPerHour);
        Assert.Null(observed.FatalAt);
        Assert.Null(observed.WarmthForecast.WarningAt);
        Assert.Null(observed.WarmthForecast.DepletionAt);
    }

    [Fact]
    public void Forecast_near_last_representable_tick_does_not_overflow_or_invent_a_future_boundary() {
        var definition = CycleWorldDefinitions.Named("gentle", characterCount: 1);
        var at = new ModelTime(long.MaxValue - 1);
        CycleAttributeForecast forecast = CycleSurvivalRules.ForecastWarmth(definition, 80 * CycleSurvival.Scale, at, sheltered: false);
        Assert.Null(forecast.WarningAt);
        Assert.Null(forecast.DepletionAt);
        CycleAttributeForecast alreadyLow = CycleSurvivalRules.ForecastWarmth(definition, 20 * CycleSurvival.Scale, at, sheltered: false);
        Assert.Equal(at, alreadyLow.WarningAt);
        Assert.Null(alreadyLow.DepletionAt);
    }

    [Theory]
    [InlineData(0, false, 80)]
    [InlineData(1, true, 75)]
    public async Task Bound_weather_interval_loss_and_certain_probability_control_the_actual_kernel(int numerator, bool cold, int warmth) {
        CycleWorldDefinition definition = Outdoors(new(0, 0, 0, 0, 0), Hour(8), 80);
        definition = definition.Change(weather: new(Enabled: true, IntervalTicks: 4 * CycleTime.Hour,
            ExposureLoss: 5, ColdProbabilityNumerator: numerator, ColdProbabilityDenominator: 1, Seed: 123));
        var initial = CycleGameFactory.Create(definition);
        CycleWeatherObservation observed = CycleEnvironmentRules.Observe(initial.World, "a")!;
        Assert.Equal(Hour(12), observed.NextWindow);
        Assert.Equal(4 * CycleTime.Hour, observed.IntervalTicks);
        Assert.Equal(5, observed.ExposedWarmthLoss);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new WaitAndContinue());
        for (int step = 0; kernel.World.Expedition!.Environment!.Sequence == 0 && step < 30; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(12)));
        }
        CycleEnvironment environment = kernel.World.Expedition!.Environment!;
        Assert.Equal(1, environment.Sequence);
        Assert.Equal(cold, environment.LastCold);
        Assert.Equal(Hour(12), environment.LastAt);
        Assert.Equal(Hour(16), environment.NextDue);
        Assert.Equal(warmth * CycleSurvival.Scale, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Supply_and_description_use_the_requested_definition_without_cross_world_static_values() {
        CycleWorldDefinition normal = CycleWorldDefinitions.LivingWorld(characterCount: 1);
        var food = new Dictionary<string, int>(normal.FoodSupplies, StringComparer.Ordinal) { ["item.ration"] = 17 };
        CycleWorldDefinition variant = normal.Change(foodSupplies: food);
        CycleSurvival body = CycleSurvival.Create(variant, Hour(8), 20, 50, 80, sheltered: true);
        Assert.Equal(37 * CycleSurvival.Scale, body.Supply(Hour(8), "item.ration", variant).FoodUnits);
        Assert.Equal(75 * CycleSurvival.Scale, body.Supply(Hour(8), "item.ration", normal).FoodUnits);
        Assert.Contains("+17", CycleSurvivalRules.UseDescription(variant, "item.ration"));
        Assert.Contains("+55", CycleSurvivalRules.UseDescription(normal, "item.ration"));
        Assert.Equal(20 * CycleSurvival.Scale, body.FoodUnits);
    }

    private static ModelTime Hour(long hour) => new(hour * CycleTime.Hour);
    private static CycleWorldDefinition Outdoors(CycleBodyDefinition body, ModelTime at, int warmth) {
        CycleWorldDefinition definition = CycleWorldDefinitions.Named("gentle", characterCount: 1, enableWeather: false);
        return definition.Change(startTime: at, body: body,
            initialCharacters: definition.InitialCharacters.Select(actor => actor.Change(placeId: "camp", food: 100, water: 100, warmth: warmth)));
    }
    private sealed class WaitAndContinue : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation(Guid.NewGuid().ToString("N"),
                context.Observation.Actor.Activity is null ? "wait 24" : "continue", Guid.NewGuid().ToString("N")));
    }
}

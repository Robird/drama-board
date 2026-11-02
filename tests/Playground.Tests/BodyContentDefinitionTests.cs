using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground.Tests;

public sealed class BodyContentDefinitionTests {
    [Fact]
    public async Task Gentle_exposed_cycle_has_no_depletion_and_runs_three_days_without_false_fatal_forecasts() {
        PlaygroundWorldDefinition definition = Outdoors(new(FoodPerHour: 0, WaterPerHour: 0, DayWarmthPerHour: 6, NightWarmthPerHour: -6), Hour(8), 80);
        var initial = PlaygroundGameFactory.Create(definition);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new WaitAndContinue());
        int steps = 0;
        while (kernel.World.Time < Hour(80) && steps++ < 150) {
            PlaygroundSurvivalObservation observed = PlaygroundSurvivalRules.Observe(kernel.World, "a")!;
            Assert.Null(observed.WarmthForecast.DepletionAt);
            Assert.Null(observed.WarmthForecast.WarningAt);
            Assert.True(kernel.World.GetCharacter("a").IsActive);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(80)));
        }
        Assert.Equal(Hour(80), kernel.World.Time);
        Assert.True(kernel.World.GetCharacter("a").IsActive);
        Assert.Null(PlaygroundSurvivalRules.Observe(kernel.World, "a")!.FatalAt);
    }

    [Fact]
    public async Task Slower_negative_daily_drift_predicts_and_reaches_depletion_on_the_third_night() {
        // Dusk warmth: 100, 88, 76. The first two dawns retain 16 and 4;
        // the third night exhausts 76 at seven points/hour, rounded to the first depleted tick.
        PlaygroundWorldDefinition definition = Outdoors(new(FoodPerHour: 0, WaterPerHour: 0, DayWarmthPerHour: 6, NightWarmthPerHour: -7), Hour(18), 100);
        var initial = PlaygroundGameFactory.Create(definition);
        ModelTime warning = new(Hour(18).Ticks + (75 * PlaygroundTime.Hour + 6) / 7);
        ModelTime depleted = new(Hour(66).Ticks + (76 * PlaygroundTime.Hour + 6) / 7);
        PlaygroundSurvivalObservation observed = PlaygroundSurvivalRules.Observe(initial.World, "a")!;
        Assert.Equal(warning, observed.WarmthForecast.WarningAt);
        Assert.Equal(depleted, observed.WarmthForecast.DepletionAt);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new WaitAndContinue());
        for (int step = 0; kernel.World.GetCharacter("a").IsActive && step < 150; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(90)));
        }
        Assert.Equal(depleted, kernel.World.Time);
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, kernel.World.GetCharacter("a").Ending?.Outcome);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Zero_rates_return_absent_thresholds_instead_of_requiring_eventual_depletion() {
        var definition = Outdoors(new(0, 0, 0, 0, 0), Hour(8), 40);
        PlaygroundWorld world = PlaygroundGameFactory.Create(definition).World;
        PlaygroundSurvivalObservation observed = PlaygroundSurvivalRules.Observe(world, "a")!;
        Assert.Equal(0, observed.FoodPerHour);
        Assert.Equal(0, observed.WaterPerHour);
        Assert.Equal(0, observed.WarmthPerHour);
        Assert.Null(observed.FatalAt);
        Assert.Null(observed.WarmthForecast.WarningAt);
        Assert.Null(observed.WarmthForecast.DepletionAt);
    }

    [Fact]
    public void Forecast_near_last_representable_tick_does_not_overflow_or_invent_a_future_boundary() {
        var definition = PlaygroundWorldDefinitions.Named("gentle", characterCount: 1);
        var at = new ModelTime(long.MaxValue - 1);
        PlaygroundAttributeForecast forecast = PlaygroundSurvivalRules.ForecastWarmth(definition, 80 * PlaygroundSurvival.Scale, at, sheltered: false);
        Assert.Null(forecast.WarningAt);
        Assert.Null(forecast.DepletionAt);
        PlaygroundAttributeForecast alreadyLow = PlaygroundSurvivalRules.ForecastWarmth(definition, 20 * PlaygroundSurvival.Scale, at, sheltered: false);
        Assert.Equal(at, alreadyLow.WarningAt);
        Assert.Null(alreadyLow.DepletionAt);
    }

    [Theory]
    [InlineData(0, false, 80)]
    [InlineData(1, true, 75)]
    public async Task Bound_weather_interval_loss_and_certain_probability_control_the_actual_kernel(int numerator, bool cold, int warmth) {
        PlaygroundWorldDefinition definition = Outdoors(new(0, 0, 0, 0, 0), Hour(8), 80);
        definition = definition.Change(weather: new(Enabled: true, IntervalTicks: 4 * PlaygroundTime.Hour,
            ExposureLoss: 5, ColdProbabilityNumerator: numerator, ColdProbabilityDenominator: 1, Seed: 123));
        var initial = PlaygroundGameFactory.Create(definition);
        PlaygroundWeatherObservation observed = PlaygroundEnvironmentRules.Observe(initial.World, "a")!;
        Assert.Equal(Hour(12), observed.NextWindow);
        Assert.Equal(4 * PlaygroundTime.Hour, observed.IntervalTicks);
        Assert.Equal(5, observed.ExposedWarmthLoss);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new WaitAndContinue());
        for (int step = 0; kernel.World.Expedition!.Environment!.Sequence == 0 && step < 30; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(12)));
        }
        PlaygroundEnvironment environment = kernel.World.Expedition!.Environment!;
        Assert.Equal(1, environment.Sequence);
        Assert.Equal(cold, environment.LastCold);
        Assert.Equal(Hour(12), environment.LastAt);
        Assert.Equal(Hour(16), environment.NextDue);
        Assert.Equal(warmth * PlaygroundSurvival.Scale, kernel.World.GetCharacter("a").Survival!.WarmthUnits);
    }

    [Fact]
    public void Supply_and_description_use_the_requested_definition_without_cross_world_static_values() {
        PlaygroundWorldDefinition normal = PlaygroundWorldDefinitions.LivingWorld(characterCount: 1);
        var food = new Dictionary<string, int>(normal.FoodSupplies, StringComparer.Ordinal) { ["item.ration"] = 17 };
        PlaygroundWorldDefinition variant = normal.Change(foodSupplies: food);
        PlaygroundSurvival body = PlaygroundSurvival.Create(variant, Hour(8), 20, 50, 80, sheltered: true);
        Assert.Equal(37 * PlaygroundSurvival.Scale, body.Supply(Hour(8), "item.ration", variant).FoodUnits);
        Assert.Equal(75 * PlaygroundSurvival.Scale, body.Supply(Hour(8), "item.ration", normal).FoodUnits);
        Assert.Contains("+17", PlaygroundSurvivalRules.UseDescription(variant, "item.ration"));
        Assert.Contains("+55", PlaygroundSurvivalRules.UseDescription(normal, "item.ration"));
        Assert.Equal(20 * PlaygroundSurvival.Scale, body.FoodUnits);
    }

    private static ModelTime Hour(long hour) => new(hour * PlaygroundTime.Hour);
    private static PlaygroundWorldDefinition Outdoors(PlaygroundBodyDefinition body, ModelTime at, int warmth) {
        PlaygroundWorldDefinition definition = PlaygroundWorldDefinitions.Named("gentle", characterCount: 1, enableWeather: false);
        return definition.Change(startTime: at, body: body,
            initialCharacters: definition.InitialCharacters.Select(actor => actor.Change(placeId: "camp", food: 100, water: 100, warmth: warmth)));
    }
    private sealed class WaitAndContinue : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation(Guid.NewGuid().ToString("N"),
                context.Observation.Actor.Activity is null ? "wait 24" : "continue", Guid.NewGuid().ToString("N")));
    }
}

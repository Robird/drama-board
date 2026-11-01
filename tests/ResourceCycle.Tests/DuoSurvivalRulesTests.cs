using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class DuoSurvivalRulesTests {
    [Fact]
    public async Task Dead_character_is_frozen_while_partner_continues_until_its_own_death() {
        CycleSurvival Needs(int water, bool sheltered) => new(Hour(8), 100 * CycleSurvival.Scale,
            water * CycleSurvival.Scale, 60 * CycleSurvival.Scale, Rate(0), CycleSurvivalRules.WaterRate,
            CycleSurvivalRules.WarmthRate(sheltered, true), warnedMask: 2);
        CycleWorld initial = World("ravine", "cabin", waitingB: 12, aNeeds: Needs(8, false), bNeeds: Needs(24, true));
        var controller = Commands(CycleCommand.Go("trail-4"));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilState(kernel, world => !world.GetCharacter("a").IsActive);
        CycleCharacter escaped = kernel.World.GetCharacter("a");
        CharacterContext terminal = kernel.World.Business.InteractionStates["a"].CurrentContext!;
        Assert.Equal(CycleExpeditionOutcome.Dead, escaped.Ending!.Outcome);
        Assert.Equal(Hour(9), escaped.Ending.At);
        Assert.Equal(CycleExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        Assert.False(kernel.World.Expedition!.Spatial.TryGetEntity(new("a"), out _));
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions, p => p.OwnerId.StartsWith("actor:a:", StringComparison.Ordinal));

        await UntilState(kernel, world => world.ExpeditionSummary != CycleExpeditionSummary.Exploring);
        Assert.Equal(CycleExpeditionSummary.NoneEscaped, kernel.World.ExpeditionSummary);
        Assert.Equal(Hour(11), kernel.World.Time);
        Assert.Equal("cabin", Assert.IsType<AtPlaceLocation>(kernel.World.GetCharacter("b").Ending!.Location).PlaceId.Value);
        Assert.Same(escaped, kernel.World.GetCharacter("a"));
        Assert.Same(terminal, kernel.World.Business.InteractionStates["a"].CurrentContext);
        Assert.Equal(escaped.Survival!.WaterUnits, escaped.Survival.Project(Hour(100)).WaterUnits);
        Assert.Equal(1, controller.Calls);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
    }

    [Fact]
    public async Task Death_retires_a_traversal_and_partner_passes_its_former_arrival_without_ghost_arrival() {
        CycleWorld initial = World("camp", "cabin", waitingB: 5, aNeeds: new(Hour(8), 100 * CycleSurvival.Scale,
            8 * CycleSurvival.Scale, 100 * CycleSurvival.Scale, Rate(0), CycleSurvivalRules.WaterRate,
            CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2));
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Go("trail-2")));
        await UntilState(kernel, world => !world.GetCharacter("a").IsActive);
        CycleCharacter dead = kernel.World.GetCharacter("a");
        Assert.Equal(Hour(9), dead.Ending!.At);
        Assert.Equal(Hour(10), Assert.IsType<TraversingLocation>(dead.Ending.Location).ArrivalDue);
        await UntilState(kernel, world => world.Time == Hour(13));
        Assert.Same(dead, kernel.World.GetCharacter("a"));
        Assert.Equal(CycleExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        Assert.DoesNotContain(history.CompletedEvents.SelectMany(e => e.Facts), f => f.ActorId == "a" && f.Kind == CycleEventKind.MoveArrived);
        Assert.False(kernel.World.Expedition!.Spatial.TryGetEntity(new("a"), out _));
        Assert.Null(CycleActions.CurrentPlace(kernel.World, "a"));
        Assert.False(CycleActions.NeedsDecision(kernel.World, "a"));
        Assert.NotNull(CycleActions.Check(kernel.World, "a", CycleCommand.Wait(1)));
        Assert.Empty(kernel.World.Spatial!.Contacts.Entries);
    }

    [Fact]
    public async Task One_weather_draw_records_independent_exposure_for_both_characters() {
        CycleWorld initial = World("camp", "cabin", waitingA: 4, waitingB: 4, weather: true);
        var kernel = CycleEngine.CreateKernel(Memory(initial));
        await Commit(kernel);
        CycleFact weather = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.WeatherResolved, weather.Kind);
        Assert.Null(weather.ActorId);
        Assert.Equal(2, weather.ChangedCharacters.Count);
        CycleEnvironment environment = kernel.World.Expedition!.Environment!;
        Assert.Equal(1, environment.Sequence);
        Assert.True(environment.LastCold);
        Assert.Equal(10 * CycleSurvival.Scale, environment.LastEffects.Single(e => e.ActorId == "a").WarmthLossUnits);
        Assert.Equal(0, environment.LastEffects.Single(e => e.ActorId == "b").WarmthLossUnits);
        Assert.False(environment.LastEffects.Single(e => e.ActorId == "a").Sheltered);
        Assert.True(environment.LastEffects.Single(e => e.ActorId == "b").Sheltered);
        Assert.Equal(10m, CycleEnvironmentRules.Observe(kernel.World, "a")!.LastActualWarmthLoss);
        Assert.Equal(0m, CycleEnvironmentRules.Observe(kernel.World, "b")!.LastActualWarmthLoss);
    }

    [Fact]
    public async Task Shared_fatal_weather_ends_both_characters_atomically_without_later_activity_completion() {
        CycleSurvival LowWarmth() => new(Hour(8), 100 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            5 * CycleSurvival.Scale, Rate(0), Rate(0), CycleSurvivalRules.WarmthRate(false, true), warnedMask: 4);
        CycleWorld initial = World("camp", "camp", waitingA: 4, waitingB: 4, weather: true, aNeeds: LowWarmth(), bNeeds: LowWarmth());
        var kernel = CycleEngine.CreateKernel(Memory(initial));
        await Commit(kernel);
        CycleFact weather = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.WeatherResolved, weather.Kind);
        Assert.Equal(2, weather.SpatialFacts.OfType<EntityRemovedFact>().Count());
        Assert.Empty(kernel.World.Expedition!.Spatial.Entities);
        Assert.Equal(CycleExpeditionSummary.NoneEscaped, kernel.World.ExpeditionSummary);
        Assert.All(kernel.World.Characters, actor => {
            Assert.Equal(CycleExpeditionOutcome.Dead, actor.Ending!.Outcome);
            Assert.Null(actor.Activity);
            Assert.Equal(CharacterContextStatus.Terminal, kernel.World.Business.InteractionStates[actor.Id].CurrentContext!.Status);
        });
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions, p => p.OwnerId.StartsWith("actor:", StringComparison.Ordinal));
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continuous_deaths_precede_same_tick_weather_and_only_remaining_participants_receive_it(bool bothDie) {
        CycleSurvival Needs(int water, bool sheltered = false) => new(Hour(8), 100 * CycleSurvival.Scale, water * CycleSurvival.Scale,
            60 * CycleSurvival.Scale, Rate(0), CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(sheltered, true), warnedMask: 2);
        CycleWorld initial = World("camp", "cabin", waitingA: 4, waitingB: 4, weather: true,
            aNeeds: Needs(0), bNeeds: Needs(bothDie ? 0 : 100, sheltered: true));
        var kernel = CycleEngine.CreateKernel(Memory(initial));
        await Commit(kernel);
        Assert.Equal(CycleEventKind.SurvivalFailed, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        await Commit(kernel);
        CycleFact second = Assert.Single(kernel.LastCompletion!.Event.Facts);
        CycleEnvironment environment = kernel.World.Expedition!.Environment!;
        if (bothDie) {
            Assert.Equal(CycleEventKind.SurvivalFailed, second.Kind);
            Assert.Equal(0, environment.Sequence);
            Assert.Empty(environment.LastEffects);
            Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
        } else {
            Assert.Equal(CycleEventKind.WeatherResolved, second.Kind);
            Assert.Equal(1, environment.Sequence);
            Assert.Equal("b", Assert.Single(environment.LastEffects).ActorId);
            Assert.Equal("b", Assert.Single(second.ChangedCharacters).Id);
            Assert.Null(CycleEnvironmentRules.Observe(kernel.World, "a")!.LastAt);
        }
    }

    [Fact]
    public async Task Road_arrival_keeps_character_active_when_distant_partner_dies() {
        CycleWorld initial = World("ravine", "cabin", waitingB: 12,
            bNeeds: new(Hour(8), 100 * CycleSurvival.Scale, 32 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
                Rate(0), CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(true, true)));
        var controller = new StubCharacterController((observation, actor) => actor == "a"
            ? observation.PlaceId == "ravine" ? CycleCommand.Go("trail-4") : CycleCommand.Wait(4)
            : CycleCommand.Continue());
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilState(kernel, world => !world.GetCharacter("b").IsActive);
        Assert.Equal(CycleExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        Assert.Null(kernel.World.GetCharacter("a").Ending);
        Assert.Equal("road", CycleActions.CurrentPlace(kernel.World, "a"));
        Assert.Equal(CycleExpeditionOutcome.Dead, kernel.World.GetCharacter("b").Ending!.Outcome);
        Assert.Equal(Hour(12), kernel.World.Time);
    }

    private static CycleWorld World(string aPlace, string bPlace, int waitingA = 0, int waitingB = 0,
        bool weather = false, CycleSurvival? aNeeds = null, CycleSurvival? bNeeds = null) {
        var (basis, _) = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false, characterCount: 2);
        CycleCharacter Actor(string id, string place, int wait, CycleSurvival? needs) => new(id, id,
            activity: wait == 0 ? null : new(CycleActivityKind.Wait, 1, Hour(8), wait * CycleTime.Hour, Hour(8 + wait)),
            nextActivityId: wait == 0 ? 1 : 2,
            survival: needs ?? new(Hour(8), 100 * CycleSurvival.Scale, 100 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
                Rate(0), Rate(0), CycleSurvivalRules.WarmthRate(CycleExpeditionMap.GetPlace(place).Sheltered, true)));
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId,
            GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new(aPlace)), new(new("b"), new(bPlace))]),
            [new("a", aPlace, Hour(8), []), new("b", bPlace, Hour(8), [])], environment: weather ? new(0, 0, Hour(8)) : null);
        return CycleEngine.Rebuild(new(Hour(8), basis.Light, basis.Resources, basis.Script,
            [Actor("a", aPlace, waitingA, aNeeds), Actor("b", bPlace, waitingB, bNeeds)], expedition: expedition), Hour(8));
    }

    private static async Task UntilState(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> predicate) {
        for (int i = 0; i < 100; i++) {
            if (predicate(kernel.World)) { return; }
            await Commit(kernel);
        }
        Assert.Fail("Expected two-character boundary was not reached.");
    }
    private static async Task Commit(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
    }
}

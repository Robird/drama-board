using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class MulticharacterContextTests {
    [Fact]
    public async Task Personal_death_freezes_its_observation_while_distant_partner_keeps_living() {
        CycleWorld initial = World("ravine", "stream");
        CycleCharacter mortal = initial.GetCharacter("a").WithSurvival(new(Hour(8), 100 * CycleSurvival.Scale,
            24 * CycleSurvival.Scale, 100 * CycleSurvival.Scale, CycleSurvivalRules.FoodRate,
            CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2));
        initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [mortal]), initial.Time);
        var source = new Controller(context => context.Observation.Actor.Id == "a"
            ? CycleCommand.Wait(3) : CycleCommand.Wait(4));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: source);
        await Reach(kernel, world => !world.GetCharacter("a").IsActive);
        Assert.Equal(Hour(11), kernel.World.Time);
        Assert.Equal(CycleExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        CharacterContext ended = kernel.World.Business.InteractionStates["a"].CurrentContext!;
        CycleObservation frozen = ended.LatestObservation;
        string fingerprint = MaterialSnapshot(frozen);
        Assert.Equal(CharacterContextStatus.Terminal, ended.Status);
        Assert.Equal(CycleExpeditionOutcome.Dead, frozen.Outcome);
        Assert.Equal("ravine", frozen.PlaceId);
        Assert.Equal(Hour(11), frozen.Time);
        Assert.Equal(0, frozen.Survival!.FoodPerHour);
        Assert.Null(CycleActions.CurrentPlace(kernel.World, "a"));
        Assert.NotNull(CycleActions.Location(kernel.World, "a"));
        Assert.DoesNotContain(kernel.World.Expedition!.Spatial.Entities, entity => entity.Id.Value == "a");

        CycleObservation partner = CycleDecisionContext.Observe(kernel.World, "b");
        Assert.Equal(CycleExpeditionOutcome.Exploring, partner.Outcome);
        Assert.Null(partner.EndingReason);
        Assert.DoesNotContain(partner.OtherCharacters, actor => actor.Id == "a");
        Assert.DoesNotContain(kernel.World.Business.InteractionStates["b"].UndeliveredExperiences,
            item => item.Event.Kind == CycleObservedEventKind.ExpeditionEnded);
        Assert.DoesNotContain("road", MaterialSnapshot(partner), StringComparison.Ordinal);

        await Reach(kernel, world => world.Time >= Hour(12));
        Assert.Same(ended, kernel.World.Business.InteractionStates["a"].CurrentContext);
        Assert.Same(frozen, CycleDecisionContext.Observe(kernel.World, "a"));
        Assert.Equal(fingerprint, MaterialSnapshot(CycleDecisionContext.Observe(kernel.World, "a")));
        Assert.Equal(Hour(12), CycleDecisionContext.Observe(kernel.World, "b").Time);
        Assert.True(kernel.World.GetCharacter("b").IsActive);
        Assert.Equal(1, source.ActorCalls["a"]);
    }

    [Theory]
    [InlineData("cabin", false)]
    [InlineData("stream", true)]
    public async Task Shared_weather_records_private_effects_and_closes_only_ended_contexts(string partnerPlace, bool bothDie) {
        CycleWorld initial = World("camp", partnerPlace, weatherNow: true, warmth: 9);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        await Advance(kernel);
        CycleFact weather = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.WeatherResolved, weather.Kind);
        Assert.Null(weather.ActorId);
        Assert.Equal(2, weather.Environment!.LastEffects.Count);
        Assert.Equal(1, weather.Environment.Sequence);
        Assert.Equal(CycleExpeditionOutcome.Dead, kernel.World.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(bothDie, !kernel.World.GetCharacter("b").IsActive);
        Assert.Equal(bothDie ? CycleExpeditionSummary.NoneEscaped : CycleExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        Assert.Equal(CharacterContextStatus.Terminal, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
        Assert.Equal(bothDie, kernel.World.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Terminal);

        foreach (string actor in new[] { "a", "b" }) {
            CycleObservedEvent effect = Assert.Single(weather.ObservedEvents, entry => entry.ObserverId == actor && entry.Kind == CycleObservedEventKind.WeatherResolved);
            Assert.Equal(actor == "b" && !bothDie ? "ColdWindSheltered" : "ColdWind", effect.Detail);
            Assert.All(effect.Effects.Where(change => change.Key.Category == "body"), change => Assert.Equal(actor, change.Key.ObjectId));
            CycleObservation observation = CycleDecisionContext.Observe(kernel.World, actor);
            Assert.Equal(actor == "b" && !bothDie ? 0 : 9, observation.Weather!.LastActualWarmthLoss);
            Assert.Equal(actor == "b" && !bothDie, observation.Weather.LastWasSheltered);
            Assert.Equal(actor == "b" && !bothDie ? CycleExpeditionOutcome.Exploring : CycleExpeditionOutcome.Dead, observation.Outcome);
        }
        Assert.DoesNotContain(weather.ObservedEvents, entry => entry.ObserverId == "b" && !bothDie && entry.Kind == CycleObservedEventKind.ExpeditionEnded);
        if (bothDie) {
            Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
        } else {
            // Its low warmth still requires a warning before the first input context opens.
            await Advance(kernel);
            Assert.Equal(CycleEventKind.SurvivalWarning, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
            Assert.Null(kernel.World.Business.InteractionStates["b"].CurrentContext);
            await Reach(kernel, world => world.Business.InteractionStates["b"].CurrentContext is { Status: CharacterContextStatus.Open });
            CharacterContext opening = kernel.World.Business.InteractionStates["b"].CurrentContext!;
            Assert.Equal(CharacterContextStatus.Open, opening.Status);
            Assert.Contains(opening.Opening.Experiences, item => item.Event.Kind == CycleObservedEventKind.WeatherResolved && item.Event.Detail == "ColdWindSheltered");
            Assert.Contains(opening.Opening.Experiences, item => item.Event.Kind == CycleObservedEventKind.SurvivalWarning);
            Assert.DoesNotContain(opening.Opening.Experiences, item => item.Event.Kind == CycleObservedEventKind.ExpeditionEnded);
        }
    }

    private static CycleWorld World(string firstPlace, string secondPlace, bool weatherNow = false, int warmth = 100) {
        ModelTime at = Hour(8);
        CycleWorld template = CycleGameFactory.CreateSurvival(at, enableWeather: false, characterCount: 2).World;
        CycleCharacter[] actors = template.Characters.Select(actor => actor.WithSurvival(CycleSurvival.Create(at,
            food: 100, water: 100, warmth: warmth, warmthRate: CycleSurvivalRules.WarmthRate(
                CycleExpeditionMap.GetPlace(actor.Id == "a" ? firstPlace : secondPlace).Sheltered, true)))).ToArray();
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph,
            [new(new("a"), new(firstPlace)), new(new("b"), new(secondPlace))]);
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            [new("a", firstPlace, at, []), new("b", secondPlace, at, [])],
            environment: weatherNow ? new(0, 0, at) : null);
        return CycleEngine.Rebuild(new(at, template.Light, template.Resources, template.Script, actors, expedition: expedition), at);
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));

    private static async Task Reach(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> target) {
        for (int step = 0; step < 100; step++) {
            if (target(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The requested multiplayer boundary was not reached.");
    }

    private sealed class Controller(Func<CycleDecisionContext, CycleCommand> choose) : ICycleCharacterController {
        public Dictionary<string, int> ActorCalls { get; } = new(StringComparer.Ordinal);
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.Observation.Actor.Id;
            int call = ActorCalls.GetValueOrDefault(actor) + 1;
            ActorCalls[actor] = call;
            return ValueTask.FromResult(new CycleControllerOperation($"call/{actor}/{call}", CycleCommandParser.Format(choose(context)), $"operation/{actor}/{call}"));
        }
    }

    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This boundary must not request input.");
    }
}

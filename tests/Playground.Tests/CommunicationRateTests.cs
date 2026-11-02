using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class CommunicationRateTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Communication_reanchors_bodies_without_replacing_valid_rate_causes(bool gift) {
        var (initial, _) = PlaygroundGameFactory.CreateSurvival(characterCount: 2, enableWeather: false);
        var warmth = new RateBreakdown([new("fixture.sun", "日照", 18), new("fixture.exposure", "暴露", -12)]);
        PlaygroundCharacter[] characters = initial.Characters.Select(actor => actor.WithSurvival(new(initial.Time,
            60 * PlaygroundSurvival.Scale, 60 * PlaygroundSurvival.Scale, 60 * PlaygroundSurvival.Scale,
            PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, warmth))).ToArray();
        PlaygroundWorld world = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: characters), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(gift
            ? PlaygroundCommand.Give("b", "item.water", 1) : PlaygroundCommand.Say("b", "先补给再出发。")));
        await StepWorldEvent(kernel);
        Assert.Equal(gift ? PlaygroundEventKind.ItemGiven : PlaygroundEventKind.SpeechSent, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        foreach (PlaygroundCharacter actor in kernel.World.Characters) {
            Assert.Same(warmth, actor.Survival!.WarmthRate);
            Assert.Equal(6, actor.Survival.WarmthPerHour);
            Assert.Equal(60 * PlaygroundSurvival.Scale, actor.Survival.WarmthUnits);
        }
        Assert.Equal(world.Time, kernel.World.Time);
    }
}

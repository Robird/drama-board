using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CommunicationRateTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Communication_reanchors_bodies_without_replacing_valid_rate_causes(bool gift) {
        var (initial, _) = CycleGameFactory.CreateSurvival(characterCount: 2, enableWeather: false);
        var warmth = new RateBreakdown([new("fixture.sun", "日照", 18), new("fixture.exposure", "暴露", -12)]);
        CycleCharacter[] characters = initial.Characters.Select(actor => actor.WithSurvival(new(initial.Time,
            60 * CycleSurvival.Scale, 60 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
            CycleSurvivalRules.FoodRate, CycleSurvivalRules.WaterRate, warmth))).ToArray();
        CycleWorld world = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: characters), initial.Time);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(gift
            ? CycleCommand.Give("b", "item.water", 1) : CycleCommand.Say("b", "先补给再出发。")));
        await StepWorldEvent(kernel);
        Assert.Equal(gift ? CycleEventKind.ItemGiven : CycleEventKind.SpeechSent, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        foreach (CycleCharacter actor in kernel.World.Characters) {
            Assert.Same(warmth, actor.Survival!.WarmthRate);
            Assert.Equal(6, actor.Survival.WarmthPerHour);
            Assert.Equal(60 * CycleSurvival.Scale, actor.Survival.WarmthUnits);
        }
        Assert.Equal(world.Time, kernel.World.Time);
    }
}

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CycleBackgroundTests {
    [Fact]
    public void Survival_background_includes_goal_and_visible_rules_without_revealing_distant_resources() {
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");

        CycleBackground background = CycleBackground.For(observation);

        Assert.Contains("寻找人类社会", background.Introduction);
        Assert.Contains(background.Rules, rule => rule.Contains("温暖"));
        Assert.Contains(background.Rules, rule => rule.Contains("天气窗口"));
        Assert.Contains(world.Resources, resource => resource.ItemId == "item.wood");
        Assert.DoesNotContain(background.Rules, rule => rule.Contains("干木"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Background_without_survival_omits_need_and_weather_rules(bool expedition) {
        CycleWorld world = expedition ? CycleGameFactory.CreateSurvival(enableNeeds: false).World
            : CycleGameFactory.CreatePlayable().World;
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");

        CycleBackground background = CycleBackground.For(observation);

        Assert.Contains(background.Rules, rule => rule.Contains("采集按每份周期结算"));
        Assert.DoesNotContain(background.Rules, rule => rule.Contains("温暖") || rule.Contains("天气窗口"));
        if (!expedition) {
            Assert.Contains(background.Rules, rule => rule.Contains("别人只公开正在采集哪个点"));
        }
    }
}

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CycleBackgroundTests {
    [Fact]
    public void Living_world_background_describes_continuing_life_without_revealing_distant_content() {
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");

        CycleBackground background = CycleBackground.For(observation);

        Assert.Equal(LivingWorldContent.Name, background.Title);
        Assert.Contains("返回继续生活", background.Introduction);
        Assert.Contains("没有必须完成的主线或固定天数的终局", background.Introduction);
        Assert.Contains(background.Rules, rule => rule.Contains("不会结束生活") && rule.Contains("其他角色仍可继续"));
        Assert.Contains(background.Rules, rule => rule.Contains("温暖"));
        Assert.Contains(background.Rules, rule => rule.Contains("天气窗口"));
        Assert.Contains(world.Resources, resource => resource.ItemId == "item.wood");
        Assert.DoesNotContain(background.Rules, rule => rule.Contains("干木"));
        string material = string.Join("\n", new[] { background.Title, background.Introduction }.Concat(background.Rules));
        foreach (string place in new[] { "stream", "ravine", "cabin", "signal" }) {
            Assert.DoesNotContain(CycleExpeditionMap.GetPlace(place).Name, material, StringComparison.Ordinal);
        }
        foreach (CycleResource resource in world.Resources) {
            Assert.DoesNotContain(resource.Id, material, StringComparison.Ordinal);
        }
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

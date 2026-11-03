using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class AttributeRateViewTests {
    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public void Layouts_render_saved_contributions_including_zero_sum_without_reselecting_world_rules(string layoutId) {
        var rate = Rate(new("sun", "测试日照", 9), new("wet", "测试湿身", -9));
        var observation = Observation(rate);
        IPlayLayout layout = Layout(layoutId);
        string text = layout.Render(observation);
        Assert.Contains("测试日照", text);
        Assert.Contains("测试湿身", text);
        Assert.Contains("+9/小时", text);
        Assert.Contains("-9/小时", text);
        Assert.Contains("0/小时", text);
        Assert.Contains("0–100", text);
        Assert.DoesNotContain("无持续作用", text);
        Assert.True(observation.Survival!.WarmthRate.SameAs(rate));
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public void Same_total_changed_sources_redraw_one_attribute_and_preserve_opening_explanation(string layoutId) {
        var before = Observation(Rate(new RateContribution("old", "旧原因", 6)));
        var after = WithSurvival(before, before.Survival! with { WarmthRate = Rate(new RateContribution("new", "新原因", 6)) });
        var update = Update(before, after);
        Assert.Empty(update.Events);
        Assert.Contains(WarmthKey, update.CurrentValues.Keys);
        Assert.Contains(WarmthRateKey, update.CurrentValues.Keys);
        string delta = Layout(layoutId).RenderUpdate(update);
        Assert.Contains("新原因", delta);
        Assert.DoesNotContain("旧原因", delta);
        Assert.Single(delta.Split('\n'), line => line.Contains("新原因", StringComparison.Ordinal));

        var opening = ViewContextFixture.State(before).CurrentContext!.Opening;
        var state = new CharacterInteractionState("a", new CharacterContext(1, CharacterContextStatus.Open,
            opening, [new CharacterContextItem(1, observation: after)], nextItemSequence: 2));
        string full = CharacterContextRenderer.RenderFull(state, Layout(layoutId));
        Assert.Contains("旧原因", full);
        Assert.Contains("新原因", full);
        string latest = CharacterContextRenderer.RenderLatestState(state, Layout(layoutId));
        Assert.DoesNotContain("旧原因", latest);
        Assert.Contains("新原因", latest);
        Assert.Single(state.CurrentContext!.Items);
    }

    [Fact]
    public void Equivalent_source_order_does_not_create_a_delta() {
        RateContribution[] contributions = [new("sun", "日照", 9), new("wet", "湿身", -3)];
        var before = Observation(Rate(contributions));
        var after = WithSurvival(before, before.Survival! with { WarmthRate = Rate(contributions.Reverse().ToArray()) });
        Assert.Empty(Update(before, after).CurrentValues);
    }

    [Theory]
    [InlineData(99, 100, 6, 6, true)]
    [InlineData(100, 90, 6, 6, false)]
    [InlineData(100, 100, 6, -12, false)]
    public void Delta_refreshes_complete_attribute_on_value_or_direction_change(int from, int to,
        int oldRate, int newRate, bool atUpperLimit) {
        var before = Observation(Rate(new RateContribution("condition", "环境条件", oldRate)), from);
        var after = WithSurvival(before, before.Survival! with {
            Warmth = to, WarmthRate = Rate(new RateContribution("condition", "环境条件", newRate))
        });
        var update = Update(before, after);
        string text = PlaygroundUpdateRenderer.Render(update);
        Assert.Contains(WarmthKey, update.CurrentValues.Keys);
        Assert.Contains(WarmthRateKey, update.CurrentValues.Keys);
        Assert.Contains("环境条件", text);
        Assert.Equal(atUpperLimit, text.Contains("已达上限", StringComparison.Ordinal));
        Assert.Single(text.Split('\n'), line => line.Contains("环境条件", StringComparison.Ordinal));
    }

    [Fact]
    public void Changed_saved_bounds_alone_redraw_values_and_limit_information() {
        var before = Observation(Rate(new RateContribution("warm", "回暖", 6)), 90);
        var after = WithSurvival(before, before.Survival! with { Minimum = -10, Maximum = 90 });
        var update = Update(before, after);
        Assert.Contains(WarmthKey, update.CurrentValues.Keys);
        Assert.Contains("-10–90", PlaygroundUpdateRenderer.Render(update));
        Assert.Contains("已达上限", PlaygroundUpdateRenderer.Render(update));
        foreach (string layoutId in new[] { "decision", "classic" }) {
            string text = Layout(layoutId).Render(after);
            Assert.Contains("-10–90", text);
            Assert.Contains("已达上限", text);
        }
    }

    [Fact]
    public void Source_group_consumer_can_group_same_public_cause_across_attributes_without_parsing_labels() {
        var observed = Observation(Rate(new("sun", "同名来源", 9), new("wet", "同名来源", -3)));
        var survival = observed.Survival! with {
            FoodRate = Rate(new RateContribution("metabolism", "基础代谢", -5)),
            WaterRate = Rate(new RateContribution("metabolism", "基础代谢", -8))
        };
        var sourceGroups = new[] { (Attribute: "food", Rate: survival.FoodRate),
                (Attribute: "water", Rate: survival.WaterRate), (Attribute: "warmth", Rate: survival.WarmthRate) }
            .SelectMany(attribute => attribute.Rate.Contributions.Select(source => (attribute.Attribute, Source: source)))
            .GroupBy(effect => effect.Source.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Assert.Equal(3, sourceGroups.Count);
        Assert.Equal(new[] { "food", "water" }, sourceGroups["metabolism"].Select(effect => effect.Attribute));
        Assert.Equal(new[] { -5, -8 }, sourceGroups["metabolism"].Select(effect => effect.Source.Amount));
        Assert.All(sourceGroups["metabolism"], effect => Assert.Equal("基础代谢", effect.Source.SourceLabel));
        Assert.Single(sourceGroups["sun"]);
        Assert.Single(sourceGroups["wet"]);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public void Source_names_remain_data_in_full_and_delta_views(string layoutId) {
        string name = "来源|列\n## 伪标题\r\u001b[2J";
        var before = Observation(Rate(new RateContribution("safe", "原来源", 6)));
        var after = WithSurvival(before, before.Survival! with { WarmthRate = Rate(new RateContribution("unsafe", name, 6)) });
        foreach (string text in new[] { Layout(layoutId).Render(after), Layout(layoutId).RenderUpdate(Update(before, after)) }) {
            Assert.DoesNotContain(name, text);
            Assert.DoesNotContain('\u001b', text);
            Assert.DoesNotContain("来源|列", text);
            Assert.Contains("\\n", text);
            Assert.Contains("\\u007C", text);
        }
    }

    private static readonly PlaygroundStateKey WarmthKey = new("body", "a", "warmth");
    private static readonly PlaygroundStateKey WarmthRateKey = new("body", "a", "warmth-rate");
    private static RateBreakdown Rate(params RateContribution[] contributions) => new(contributions);
    private static IPlayLayout Layout(string id) => PlayPresentation.ForMode(true).FindLayout(id)!;
    private static PlaygroundViewUpdate Update(PlaygroundObservation before, PlaygroundObservation after) =>
        PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(before), PlaygroundViewSnapshot.Create(after), [], PlayDeliveryPhase.ControllerInput);
    private static PlaygroundObservation Observation(RateBreakdown warmthRate, decimal warmth = 60) {
        var observation = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
        return WithSurvival(observation, observation.Survival! with { WarmthRate = warmthRate, Warmth = warmth });
    }

    private static PlaygroundObservation WithSurvival(PlaygroundObservation source, PlaygroundSurvivalObservation survival) => new() {
        Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, WorldName = source.WorldName, ItemUseDescriptions = source.ItemUseDescriptions, ItemNames = source.ItemNames,
        OtherCharacters = source.OtherCharacters, Notices = source.Notices, Commands = source.Commands,
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, ArrivalDue = source.ArrivalDue, Journey = source.Journey, Passages = source.Passages, KnownMap = source.KnownMap,
        Memories = source.Memories, Outcome = source.Outcome, EndingReason = source.EndingReason,
        Survival = survival, Weather = source.Weather
    };
}

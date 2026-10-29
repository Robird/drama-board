using System.Collections;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class RateBreakdownTests {
    [Fact]
    public void Contributions_are_copied_canonical_and_equal_independently_of_input_order() {
        List<RateContribution> input = [new("sun", "日照", 18), new("exposure", "暴露", -12)];
        var rate = new RateBreakdown(input);
        input.Clear();

        Assert.Equal(6, rate.Total);
        Assert.Equal(new[] { "exposure", "sun" }, rate.Contributions.Select(part => part.SourceKey));
        Assert.True(rate.SameAs(new RateBreakdown(rate.Contributions.Reverse())));
        Assert.False(rate.SameAs(new RateBreakdown([new("sun", "日照", 6)])));
        Assert.False(rate.SameAs(new RateBreakdown([new("sun", "日照", 18), new("exposure", "改名", -12)])));
        Assert.False(rate.SameAs(new RateBreakdown([new("sun", "日照", 19), new("exposure", "暴露", -13)])));
        Assert.Throws<NotSupportedException>(() => ((IList)rate.Contributions).Clear());
    }

    [Fact]
    public void Zero_sum_keeps_both_real_causes_and_is_distinct_from_no_continuous_effect() {
        var zeroSum = new RateBreakdown([new("recovery", "恢复", 12), new("loss", "损失", -12)]);
        var none = new RateBreakdown([]);

        Assert.Equal(0, zeroSum.Total);
        Assert.Equal(2, zeroSum.Contributions.Count);
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Contributions);
        Assert.False(zeroSum.SameAs(none));
    }

    [Fact]
    public void Duplicate_sources_and_out_of_range_totals_are_rejected() {
        Assert.Throws<InvalidOperationException>(() => new RateBreakdown([new("same", "同源", 1), new("same", "同源", 2)]));
        Assert.Throws<OverflowException>(() => new RateBreakdown([new("a", "甲", int.MaxValue), new("b", "乙", 1)]));
        Assert.Throws<OverflowException>(() => new RateBreakdown([new("a", "甲", int.MinValue), new("b", "乙", -1)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Physical_anchor_requires_explicit_rates_in_every_slot(int omitted) {
        RateBreakdown[] rates = [Rate(-5), Rate(-8), Rate(6)];
        rates[omitted] = null!;
        Assert.Throws<ArgumentNullException>(() => new CycleSurvival(Hour(8), 50 * CycleSurvival.Scale,
            50 * CycleSurvival.Scale, 50 * CycleSurvival.Scale, rates[0], rates[1], rates[2]));
    }

    [Fact]
    public void Projection_supply_warning_acknowledgement_and_instant_weather_preserve_explanations() {
        var rate = new RateBreakdown([new("sun", "日照", 18), new("exposure", "暴露", -12)]);
        var original = new CycleSurvival(Hour(8), 25 * CycleSurvival.Scale, 45 * CycleSurvival.Scale,
            60 * CycleSurvival.Scale, CycleSurvivalRules.FoodRate, CycleSurvivalRules.WaterRate, rate);
        CycleSurvival[] changed = [original.Project(Hour(9)), original.Supply(Hour(8), "item.water"),
            original.Warn(Hour(8)), original.Acknowledge(Hour(8)), original.LoseWarmth(Hour(8), 10 * CycleSurvival.Scale)];

        foreach (CycleSurvival body in changed) {
            Assert.True(original.FoodRate.SameAs(body.FoodRate));
            Assert.True(original.WaterRate.SameAs(body.WaterRate));
            Assert.True(rate.SameAs(body.WarmthRate));
        }
        Assert.Equal(66 * CycleSurvival.Scale, changed[0].WarmthUnits);
        Assert.Equal(50 * CycleSurvival.Scale, changed[^1].WarmthUnits);
    }

    [Theory]
    [InlineData(false, true, 6)]
    [InlineData(false, false, -12)]
    [InlineData(true, true, 12)]
    [InlineData(true, false, 12)]
    public void Current_conditional_rules_select_one_actual_cause(bool sheltered, bool day, int expected) {
        RateBreakdown rate = CycleSurvivalRules.WarmthRate(sheltered, day);
        Assert.Equal(expected, rate.Total);
        Assert.Equal(expected, Assert.Single(rate.Contributions).Amount);
        Assert.Equal(-5, CycleSurvivalRules.FoodRate.Total);
        Assert.Equal(-8, CycleSurvivalRules.WaterRate.Total);
        Assert.Equal(Assert.Single(CycleSurvivalRules.FoodRate.Contributions).SourceKey,
            Assert.Single(CycleSurvivalRules.WaterRate.Contributions).SourceKey);
        Assert.Equal(Assert.Single(CycleSurvivalRules.FoodRate.Contributions).SourceLabel,
            Assert.Single(CycleSurvivalRules.WaterRate.Contributions).SourceLabel);
    }
}

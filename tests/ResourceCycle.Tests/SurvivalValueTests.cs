using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class SurvivalValueTests {
    private const long Hour = CycleTime.Hour;
    private static ModelTime At(long ticks) => new(ticks);

    [Fact]
    public void Changing_rates_integrates_the_old_segment_before_starting_the_new_one() {
        CycleSurvival initial = CycleSurvival.Create(At(17 * Hour + Hour / 2));

        CycleSurvival night = initial.WithRates(At(18 * Hour), -5, -8, -12);
        CycleSurvival later = night.Project(At(18 * Hour + Hour / 2));

        // Half an hour in daylight: warmth 60 + 3. Half an hour at night: 63 - 6.
        Assert.Equal(63 * Hour, night.WarmthUnits);
        Assert.Equal(57 * Hour, later.WarmthUnits);
        Assert.Equal(60 * Hour, later.FoodUnits);
        Assert.Equal(57 * Hour, later.WaterUnits);
        Assert.Equal(60 * Hour, initial.WarmthUnits);
        Assert.Equal(6, initial.WarmthPerHour);
        Assert.Equal(-12, night.WarmthPerHour);
    }

    [Fact]
    public void Reanchoring_at_arbitrary_ticks_does_not_discard_fractional_points() {
        CycleSurvival initial = CycleSurvival.Create(At(0));
        CycleSurvival direct = initial.Project(At(123));
        CycleSurvival segmented = initial.Project(At(1)).Project(At(17)).Project(At(61)).Project(At(123));

        Assert.Equal(65 * Hour - 5 * 123, direct.FoodUnits);
        Assert.Equal(65 * Hour - 8 * 123, direct.WaterUnits);
        Assert.Equal(60 * Hour + 6 * 123, direct.WarmthUnits);
        Assert.Equal(direct.FoodUnits, segmented.FoodUnits);
        Assert.Equal(direct.WaterUnits, segmented.WaterUnits);
        Assert.Equal(direct.WarmthUnits, segmented.WarmthUnits);
        Assert.Equal(direct.BaselineAt, segmented.BaselineAt);
    }

    [Fact]
    public void Recovery_saturates_and_excess_warmth_is_not_bankable_after_a_rate_change() {
        CycleSurvival initial = CycleSurvival.Create(At(0), warmth: 99);
        CycleSurvival exposed = initial.WithRates(At(Hour), -5, -8, -12);

        CycleSurvival cooled = exposed.Project(At(Hour + Hour / 2));

        Assert.Equal(100 * Hour, exposed.WarmthUnits);
        Assert.Equal(94 * Hour, cooled.WarmthUnits);
    }

    [Fact]
    public void Projection_uses_wide_intermediates_and_clamps_at_extreme_elapsed_time() {
        CycleSurvival initial = CycleSurvival.Create(At(0));

        CycleSurvival farFuture = initial.Project(At(long.MaxValue));

        Assert.Equal(0, farFuture.FoodUnits);
        Assert.Equal(0, farFuture.WaterUnits);
        Assert.Equal(100 * Hour, farFuture.WarmthUnits);
        Assert.True(farFuture.IsFatal);
        Assert.Equal(At(long.MaxValue), farFuture.BaselineAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => farFuture.Project(At(long.MaxValue - 1)));
    }

    [Fact]
    public void Acknowledgement_clears_attention_but_does_not_repeat_a_low_episode_warning() {
        var initial = new CycleSurvival(At(0), 80 * Hour, 25 * Hour, 80 * Hour, foodPerHour: 0, warmthPerHour: 0);

        CycleSurvival warned = initial.Warn(At(0));
        CycleSurvival acknowledged = warned.Acknowledge(At(0));
        CycleSurvival stillLow = acknowledged.Warn(At(Hour / 8));

        Assert.Equal(2, warned.WarnedMask);
        Assert.Equal(2, warned.PendingMask);
        Assert.Equal(At(0), warned.PendingSince);
        Assert.Equal(2, acknowledged.WarnedMask);
        Assert.Equal(0, acknowledged.PendingMask);
        Assert.Null(acknowledged.PendingSince);
        Assert.Equal(24 * Hour, stillLow.WaterUnits);
        Assert.Equal(2, stillLow.WarnedMask);
        Assert.Equal(0, stillLow.PendingMask);
        Assert.Equal(0, initial.WarnedMask);
    }

    [Fact]
    public void Water_supply_rearms_the_episode_but_preserves_unacknowledged_attention() {
        var initial = new CycleSurvival(At(0), 80 * Hour, 25 * Hour, 80 * Hour, foodPerHour: 0, warmthPerHour: 0);
        CycleSurvival warned = initial.Warn(At(0));

        CycleSurvival supplied = warned.Supply(At(0), "item.water");
        CycleSurvival acknowledged = supplied.Acknowledge(At(0));
        // 70 - 8 * 5.625 hours = 25.
        ModelTime nextCrossing = At(45 * Hour / 8);
        CycleSurvival warnedAgain = acknowledged.Warn(nextCrossing);

        Assert.Equal(70 * Hour, supplied.WaterUnits);
        Assert.Equal(0, supplied.WarnedMask);
        Assert.Equal(2, supplied.PendingMask);
        Assert.Equal(At(0), supplied.PendingSince);
        Assert.Equal(25 * Hour, warnedAgain.WaterUnits);
        Assert.Equal(2, warnedAgain.WarnedMask);
        Assert.Equal(2, warnedAgain.PendingMask);
        Assert.Equal(nextCrossing, warnedAgain.PendingSince);
    }

    [Fact]
    public void Only_recovery_strictly_above_the_threshold_rearms_a_warning() {
        var initial = new CycleSurvival(At(0), 80 * Hour, 80 * Hour, 24 * Hour,
            foodPerHour: 0, waterPerHour: 0, warmthPerHour: 1, warnedMask: 4, pendingMask: 4, pendingSince: At(0));

        CycleSurvival exactlyAtThreshold = initial.Project(At(Hour));
        CycleSurvival aboveThreshold = exactlyAtThreshold.Project(At(Hour + 1));

        Assert.Equal(25 * Hour, exactlyAtThreshold.WarmthUnits);
        Assert.Equal(4, exactlyAtThreshold.WarnedMask);
        Assert.Equal(25 * Hour + 1, aboveThreshold.WarmthUnits);
        Assert.Equal(0, aboveThreshold.WarnedMask);
        Assert.Equal(4, aboveThreshold.PendingMask);
        Assert.Equal(At(0), aboveThreshold.PendingSince);
    }

    [Fact]
    public void Supplying_another_need_does_not_rearm_one_that_remains_exactly_at_the_threshold() {
        var initial = new CycleSurvival(At(0), 50 * Hour, 25 * Hour, 80 * Hour,
            foodPerHour: 0, waterPerHour: 0, warmthPerHour: 0, warnedMask: 2, pendingMask: 2, pendingSince: At(0));

        CycleSurvival supplied = initial.Supply(At(0), "item.food");

        Assert.Equal(85 * Hour, supplied.FoodUnits);
        Assert.Equal(25 * Hour, supplied.WaterUnits);
        Assert.Equal(2, supplied.WarnedMask);
        Assert.Equal(2, supplied.PendingMask);
    }

    [Fact]
    public void Later_warnings_keep_the_first_unacknowledged_perception_time() {
        var initial = new CycleSurvival(At(0), 25 * Hour, 26 * Hour, 80 * Hour,
            foodPerHour: 0, waterPerHour: -1, warmthPerHour: 0);

        CycleSurvival first = initial.Warn(At(0));
        CycleSurvival second = first.Warn(At(Hour));

        Assert.Equal(1, first.PendingMask);
        Assert.Equal(3, second.WarnedMask);
        Assert.Equal(3, second.PendingMask);
        Assert.Equal(At(0), second.PendingSince);
    }

    [Fact]
    public void Supply_cannot_revive_a_need_depleted_at_the_consumption_boundary() {
        var initial = new CycleSurvival(At(0), 80 * Hour, 8 * Hour, 80 * Hour,
            foodPerHour: 0, waterPerHour: -8, warmthPerHour: 0);

        Assert.False(initial.Project(At(Hour - 1)).IsFatal);
        Assert.True(initial.Project(At(Hour)).IsFatal);
        Assert.Throws<InvalidOperationException>(() => initial.Supply(At(Hour), "item.water"));
        Assert.Equal(8 * Hour, initial.WaterUnits);
    }

    [Fact]
    public void Warning_prediction_uses_ceil_so_a_fractional_tick_is_never_announced_early() {
        var initial = new CycleSurvival(At(7), 80 * Hour, 25 * Hour + 1, 80 * Hour,
            foodPerHour: 0, waterPerHour: -8, warmthPerHour: 0);

        var next = Assert.NotNull(CycleSurvivalRules.NextThreshold(initial, At(7)));

        Assert.Equal(At(8), next.Due);
        Assert.False(next.Fatal);
        Assert.True(initial.Project(At(7)).WaterUnits > 25 * Hour);
        Assert.True(initial.Project(At(8)).WaterUnits <= 25 * Hour);
    }

    [Fact]
    public void Fatal_prediction_uses_ceil_and_skips_an_already_warned_low_episode() {
        var initial = new CycleSurvival(At(7), 80 * Hour, 9, 80 * Hour,
            foodPerHour: 0, waterPerHour: -8, warmthPerHour: 0, warnedMask: 2);

        var next = Assert.NotNull(CycleSurvivalRules.NextThreshold(initial, At(7)));

        Assert.Equal(At(9), next.Due);
        Assert.True(next.Fatal);
        Assert.False(initial.Project(At(8)).IsFatal);
        Assert.True(initial.Project(At(9)).IsFatal);
    }

    [Fact]
    public void Prediction_at_the_last_representable_tick_is_kept_but_an_overflowing_due_is_absent() {
        var representable = new CycleSurvival(At(long.MaxValue - 1), 80 * Hour, 25 * Hour + 1, 80 * Hour,
            foodPerHour: 0, waterPerHour: -8, warmthPerHour: 0);
        var beyondRange = new CycleSurvival(At(long.MaxValue), 80 * Hour, 25 * Hour + 1, 80 * Hour,
            foodPerHour: 0, waterPerHour: -8, warmthPerHour: 0);

        var next = Assert.NotNull(CycleSurvivalRules.NextThreshold(representable, representable.BaselineAt));

        Assert.Equal(At(long.MaxValue), next.Due);
        Assert.Null(CycleSurvivalRules.NextThreshold(beyondRange, beyondRange.BaselineAt));
    }

    [Fact]
    public void Fatality_wins_only_at_the_same_due_time_not_over_an_earlier_warning() {
        var alreadyFatal = new CycleSurvival(At(0), 0, 20 * Hour, 80 * Hour,
            foodPerHour: -5, waterPerHour: -8, warmthPerHour: 0);
        var fatalNextTick = new CycleSurvival(At(0), 1, 20 * Hour, 80 * Hour,
            foodPerHour: -5, waterPerHour: -8, warmthPerHour: 0, warnedMask: 1);

        var immediateFatal = Assert.NotNull(CycleSurvivalRules.NextThreshold(alreadyFatal, At(0)));
        var immediateWarning = Assert.NotNull(CycleSurvivalRules.NextThreshold(fatalNextTick, At(0)));

        Assert.Equal(At(0), immediateFatal.Due);
        Assert.True(immediateFatal.Fatal);
        Assert.Equal(At(0), immediateWarning.Due);
        Assert.False(immediateWarning.Fatal);
    }
}

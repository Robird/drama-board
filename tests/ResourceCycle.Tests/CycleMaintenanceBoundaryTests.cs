using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CycleMaintenanceBoundaryTests {
    [Fact]
    public void Batch_that_restores_original_children_still_recomputes_each_affected_owner_once() {
        CycleBusinessState business = Business(new("a", "Alice"));
        var before = new CycleWorld(business, CyclePredictionMaintenance.Rebuild(business));
        CycleResource original = business.GetResource("sunflower");
        // Two recorded light changes within one logical instant pause then resume the resource.
        // The final child references deliberately equal the original references.
        CycleFact[] facts = [
            new(CycleEventKind.LightChanged, business.Time, light: new(false, Hour(18)),
                changedResources: [original.Change(original.Stock, original.AccumulatedTicks, null)],
                observedEvents: [LightChange(business.Time, true, false)]),
            new(CycleEventKind.LightChanged, business.Time, light: business.Light, changedResources: [original],
                observedEvents: [LightChange(business.Time, false, true)])
        ];
        var instant = new LogicalInstant(business.Time, 0);
        CycleWorld folded = facts.Aggregate(before, (world, fact) => CycleEngine.Fold(world, instant, fact));
        var metrics = new CycleMetrics();

        CycleWorld completed = CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental, metrics);

        Assert.Same(business.Light, completed.Light);
        Assert.Same(original, completed.GetResource("sunflower"));
        Assert.Same(before.Forecasts, completed.Forecasts);
        Assert.Equal(1, Recalculations(metrics, "light"));
        Assert.Equal(1, Recalculations(metrics, "resource:sunflower:regen"));
        Assert.Equal(2, metrics.Recalculations);
        Assert.Equal(4, metrics.RoutedNotifications);
        Assert.Equal(4, metrics.Invalidations);
    }

    [Fact]
    public void Recorded_attention_acknowledgement_applies_and_updates_without_a_world_or_controller() {
        var activity = new CycleActivity(CycleActivityKind.Wait, 1, Hour(6), CycleTime.Hour, Hour(7));
        var notice = new CycleNotice(CycleNoticeKind.ResourceDepleted, "sunflower", null, "source", Hour(6), "empty");
        var actor = new CycleCharacter("a", "Alice", activity: activity, notices: [notice], nextActivityId: 2);
        CycleBusinessState before = Business(actor);
        CycleForecasts previous = CyclePredictionMaintenance.Rebuild(before);
        CyclePrediction activityPrediction = Assert.Single(previous.Predictions, p => p.OwnerId == "actor:a:activity");
        var replacement = actor.Change(activity, notices: []);
        var fact = new CycleFact(CycleEventKind.AttentionAcknowledged, Hour(6), "a",
            changedCharacters: [replacement], lastServedActorId: "a", toolCallId: "call_fixture_1", operationRecordId: "fixture/1",
            observedEvents: [new(0, "a", CycleObservedEventKind.AttentionAcknowledged, Hour(6), Hour(6),
                effects: [new(new("notice", "a", "count"), CycleVisibleValue.Numeric(1, "条"), CycleVisibleValue.Numeric(0, "条"))])]);

        CycleBusinessState after = CycleFactApplication.Apply(before, new(Hour(6), 0), fact);
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var metrics = new CycleMetrics();
        CyclePredictionMaintenance.CollectDirtyOwners(before, after, previous, fact, dirty, metrics);
        CycleForecasts next = CyclePredictionMaintenance.Update(after, previous, dirty, Hour(6), metrics);

        Assert.Same(replacement, after.GetCharacter("a"));
        Assert.Same(before.Light, after.Light);
        Assert.Same(before.GetResource("sunflower"), after.GetResource("sunflower"));
        Assert.Single(before.GetCharacter("a").Notices);
        Assert.Empty(after.GetCharacter("a").Notices);
        Assert.Same(activityPrediction, Assert.Single(next.Predictions, p => p.OwnerId == "actor:a:activity"));
        Assert.DoesNotContain(next.Predictions, p => p.OwnerId == "actor:a:attention");
        Assert.Contains(previous.Predictions, p => p.OwnerId == "actor:a:attention");
        Assert.Same(previous.Bindings, next.Bindings);
        Assert.Equal(1, metrics.Recalculations);
        Assert.Equal(0, Recalculations(metrics, "actor:a:activity"));
    }

    [Fact]
    public void Recomputed_equal_output_reuses_the_cache_and_prediction_instances() {
        CycleBusinessState business = Business(new("a", "Alice"));
        CycleForecasts previous = CyclePredictionMaintenance.Rebuild(business);
        var metrics = new CycleMetrics();

        CycleForecasts next = CyclePredictionMaintenance.Update(business, previous,
            new HashSet<string>(StringComparer.Ordinal) { "light" }, business.Time, metrics);

        Assert.Same(previous, next);
        Assert.Equal(1, metrics.Recalculations);
        Assert.Equal(0, metrics.RoutedNotifications);
        Assert.Equal(0, metrics.Invalidations);
    }

    [Fact]
    public void Fact_application_rejects_wrong_time_and_unknown_entities_without_a_runtime_cache() {
        CycleBusinessState before = Business(new("a", "Alice"));
        var unknown = new CycleFact(CycleEventKind.WaitCompleted, before.Time, "absent",
            changedCharacters: [new("absent", "Missing")]);
        var wrongTime = new CycleFact(CycleEventKind.LightChanged, Hour(7), light: new(false, Hour(18)));

        Assert.Throws<InvalidOperationException>(() => CycleFactApplication.Apply(before, new(before.Time, 0), unknown));
        Assert.Throws<InvalidOperationException>(() => CycleFactApplication.Apply(before, new(before.Time, 0), wrongTime));
        Assert.Equal(Hour(6), before.Time);
        Assert.True(before.Light.IsDay);
        Assert.Equal("a", Assert.Single(before.Characters).Id);
    }

    private static CycleBusinessState Business(CycleCharacter actor) =>
        new(Hour(6), new(true, Hour(18)), [Sun(Hour(6))], new([]), [actor]);
    private static CycleObservedEvent LightChange(ModelTime at, bool before, bool after) =>
        new(0, "a", CycleObservedEventKind.LightChanged, at, at,
            effects: [new(new("world", "world", "light"), CycleVisibleValue.Flag(before), CycleVisibleValue.Flag(after))]);
}

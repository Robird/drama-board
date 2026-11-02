using DramaBoard.Kernel.Time;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class PlaygroundMaintenanceBoundaryTests {
    [Fact]
    public void Batch_that_restores_original_children_still_recomputes_each_affected_owner_once() {
        PlaygroundBusinessState business = Business(new("a", "Alice"));
        var before = new PlaygroundWorld(business, PlaygroundPredictionMaintenance.Rebuild(business));
        PlaygroundResource original = business.GetResource("sunflower");
        // Two recorded light changes within one logical instant pause then resume the resource.
        // The final child references deliberately equal the original references.
        PlaygroundFact[] facts = [
            new(PlaygroundEventKind.LightChanged, business.Time, light: new(false, Hour(18)),
                changedResources: [original.Change(original.Stock, original.AccumulatedTicks, null)],
                observedEvents: [LightChange(business.Time, true, false)]),
            new(PlaygroundEventKind.LightChanged, business.Time, light: business.Light, changedResources: [original],
                observedEvents: [LightChange(business.Time, false, true)])
        ];
        facts[0] = facts[0].WithInteractions([new("a", undeliveredExperiences: [new(1, facts[0].ObservedEvents[0])], nextExperienceSequence: 2)]);
        facts[1] = facts[1].WithInteractions([new("a", undeliveredExperiences: [new(1, facts[0].ObservedEvents[0]), new(2, facts[1].ObservedEvents[0])], nextExperienceSequence: 3)]);
        var instant = new LogicalInstant(business.Time, 0);
        PlaygroundWorld folded = facts.Aggregate(before, (world, fact) => PlaygroundEngine.Fold(world, instant, fact));
        var metrics = new PlaygroundMetrics();

        PlaygroundWorld completed = PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental, metrics);

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
        var activity = new PlaygroundActivity(PlaygroundActivityKind.Wait, 1, Hour(6), PlaygroundTime.Hour, Hour(7));
        var notice = new PlaygroundNotice(PlaygroundNoticeKind.ResourceDepleted, "sunflower", null, "source", Hour(6), "empty");
        var actor = new PlaygroundCharacter("a", "Alice", activity: activity, notices: [notice], nextActivityId: 2);
        PlaygroundBusinessState before = Business(actor);
        PlaygroundForecasts previous = PlaygroundPredictionMaintenance.Rebuild(before);
        PlaygroundPrediction activityPrediction = Assert.Single(previous.Predictions, p => p.OwnerId == "actor:a:activity");
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(new(before, previous), "a");
        var opening = new CharacterContextOpening(PlaygroundBackground.For(observation), observation, null, before.Time,
            PlaygroundOutcomeActivityState.Ongoing);
        before = before.Replace(before.Time, interactionChanges: [new("a", new(1, CharacterContextStatus.Open, opening))]);
        var replacement = actor.Change(activity, notices: []);
        var fact = new PlaygroundFact(PlaygroundEventKind.AttentionAcknowledged, Hour(6), "a",
            changedCharacters: [replacement], lastServedActorId: "a", toolCallId: "call_fixture_1", operationRecordId: "fixture/1", rawInput: "continue", operationDisposition: PlaygroundOperationDisposition.Succeeded,
            observedEvents: [new(0, "a", PlaygroundObservedEventKind.AttentionAcknowledged, Hour(6), Hour(6),
                effects: [new(new("notice", "a", "count"), PlaygroundVisibleValue.Numeric(1, "条"), PlaygroundVisibleValue.Numeric(0, "条"))])]);

        var pending = new PendingAdvance("a", 1, 1, PlaygroundCommand.Continue(), "continue", "call_fixture_1", "fixture/1", Hour(6),
            "确认提醒并继续原定活动", activityId: activity.Id);
        fact = fact.WithInteractions([new("a", new(1, CharacterContextStatus.Closed, opening, lastOperationSequence: 1), pending,
            [new(1, fact.ObservedEvents[0])], nextExperienceSequence: 2)]);
        PlaygroundBusinessState after = PlaygroundFactApplication.Apply(before, new(Hour(6), 0), fact);
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var metrics = new PlaygroundMetrics();
        PlaygroundPredictionMaintenance.CollectDirtyOwners(before, after, previous, fact, dirty, metrics);
        PlaygroundForecasts next = PlaygroundPredictionMaintenance.Update(after, previous, dirty, Hour(6), metrics);

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
        PlaygroundBusinessState business = Business(new("a", "Alice"));
        PlaygroundForecasts previous = PlaygroundPredictionMaintenance.Rebuild(business);
        var metrics = new PlaygroundMetrics();

        PlaygroundForecasts next = PlaygroundPredictionMaintenance.Update(business, previous,
            new HashSet<string>(StringComparer.Ordinal) { "light" }, business.Time, metrics);

        Assert.Same(previous, next);
        Assert.Equal(1, metrics.Recalculations);
        Assert.Equal(0, metrics.RoutedNotifications);
        Assert.Equal(0, metrics.Invalidations);
    }

    [Fact]
    public void Fact_application_rejects_wrong_time_and_unknown_entities_without_a_runtime_cache() {
        PlaygroundBusinessState before = Business(new("a", "Alice"));
        var unknown = new PlaygroundFact(PlaygroundEventKind.WaitCompleted, before.Time, "absent",
            changedCharacters: [new("absent", "Missing")]);
        var wrongTime = new PlaygroundFact(PlaygroundEventKind.LightChanged, Hour(7), light: new(false, Hour(18)));

        Assert.Throws<InvalidOperationException>(() => PlaygroundFactApplication.Apply(before, new(before.Time, 0), unknown));
        Assert.Throws<InvalidOperationException>(() => PlaygroundFactApplication.Apply(before, new(before.Time, 0), wrongTime));
        Assert.Equal(Hour(6), before.Time);
        Assert.True(before.Light.IsDay);
        Assert.Equal("a", Assert.Single(before.Characters).Id);
    }

    private static PlaygroundBusinessState Business(PlaygroundCharacter actor) =>
        new(Hour(6), new(true, Hour(18)), [Sun(Hour(6))], new([]), [actor]);
    private static PlaygroundObservedEvent LightChange(ModelTime at, bool before, bool after) =>
        new(0, "a", PlaygroundObservedEventKind.LightChanged, at, at,
            effects: [new(new("world", "world", "light"), PlaygroundVisibleValue.Flag(before), PlaygroundVisibleValue.Flag(after))]);
}

using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class IncrementalProjectionTests {
    [Fact]
    public void Context_history_is_not_flattened_into_current_state_changes() {
        var observation = Observation();
        var state = ViewContextFixture.State(observation);
        var before = CycleViewSnapshot.Create(observation, state);
        var after = CycleViewSnapshot.Create(observation, new CharacterInteractionState("a", state.CurrentContext, draftHandoff: "记下此处"));
        var update = CycleViewUpdate.Create(before, after, [], PlayDeliveryPhase.ControllerInput);
        Assert.Equal("记下此处", update.CurrentValues[new("continuity", "a", "draft")].Text);
        Assert.DoesNotContain(update.CurrentValues.Keys, key => key.Category.StartsWith("incoming", StringComparison.Ordinal));
        Assert.DoesNotContain("食物：无", CycleUpdateRenderer.Render(update));
    }

    [Fact]
    public void Withdrawn_passages_retain_their_distinct_known_object_labels() {
        var previous = CycleViewSnapshot.Create(Observation(passages: [new("trail-a", "通向森林", CycleTime.Hour, null),
            new("trail-b", "通向河岸", CycleTime.Hour / 2, "河岸")]));
        var current = CycleViewSnapshot.Create(Observation());
        var update = CycleViewUpdate.Create(previous, current, [], PlayDeliveryPhase.Advancing);
        foreach (string id in new[] { "trail-a", "trail-b" }) {
            var key = new CycleStateKey("passage", id, "description");
            Assert.Equal(CycleVisibleValue.Unobserved, update.CurrentValues[key]);
            Assert.Contains(id, update.Label(key));
            Assert.Equal(previous.Label(key), update.Label(key));
        }
        string rendered = CycleUpdateRenderer.Render(update);
        Assert.Contains("trail-a", rendered);
        Assert.Contains("trail-b", rendered);
        Assert.Contains("不再可见", rendered);
        Assert.DoesNotContain("- description：", rendered);
    }

    [Fact]
    public void Filtering_preserves_original_commit_and_tool_source_identity() {
        var snapshot = CycleViewSnapshot.Create(Observation());
        var own = Event(9, new(new("body", "a", "water"), CycleVisibleValue.Numeric(1), CycleVisibleValue.Numeric(2)));
        var foreign = new CycleObservedEvent(3, "b", CycleObservedEventKind.ItemUsed, new(0), new(0));
        CycleObservedEventSource[] sources = [new(41, 8, 0, 3, "foreign", "provider-b"), new(41, 8, 1, 9, "own", "provider:a")];
        var update = CycleViewUpdate.Create(snapshot, snapshot, [foreign, own], PlayDeliveryPhase.Advancing, sources: sources);
        Assert.Same(own, Assert.Single(update.Events));
        Assert.Equal(sources[1], Assert.Single(update.Sources));
    }

    [Fact]
    public void Depleted_owned_item_is_zero_once_without_retaining_a_tombstone() {
        var before = CycleViewSnapshot.Create(Observation(items: new Dictionary<string, int> { ["item.food"] = 1 }));
        var after = CycleViewSnapshot.Create(Observation());
        var key = new CycleStateKey("inventory", "item.food", "count");
        var effect = new CycleObservedEffect(key, CycleVisibleValue.Numeric(1, "份"), CycleVisibleValue.Numeric(0, "份"));
        var update = CycleViewUpdate.Create(before, after, [Event(0, effect)], PlayDeliveryPhase.ControllerInput);
        Assert.Equal(CycleVisibleValue.Numeric(0, "份"), update.CurrentValues[key]);
        Assert.DoesNotContain(key, after.Values.Keys);
        Assert.DoesNotContain(key, CycleViewUpdate.Create(after, after, [], PlayDeliveryPhase.ControllerInput).CurrentValues.Keys);
    }

    [Fact]
    public void Leaving_and_reentering_visibility_are_not_resource_destruction_or_creation() {
        var resource = new CycleResource("berries", "野果", "item.food", true, CycleTime.Hour, 5, 3);
        var visible = CycleViewSnapshot.Create(Observation(resources: [resource]));
        var absent = CycleViewSnapshot.Create(Observation());
        var key = new CycleStateKey("resource", resource.Id, "stock");
        Assert.Equal(CycleVisibleValue.Unobserved, CycleViewUpdate.Create(visible, absent, [], PlayDeliveryPhase.Advancing).CurrentValues[key]);
        Assert.Equal(CycleVisibleValue.Numeric(3, "份"), CycleViewUpdate.Create(absent, visible, [], PlayDeliveryPhase.Advancing).CurrentValues[key]);
        Assert.Empty(CycleViewUpdate.Create(absent, absent, [], PlayDeliveryPhase.Advancing).CurrentValues);
    }

    [Fact]
    public void Net_zero_events_keep_their_order_and_select_final_value_once() {
        var snapshot = CycleViewSnapshot.Create(Observation());
        var key = new CycleStateKey("body", "a", "water");
        var n = CycleVisibleValue.Numeric(75 * CycleSurvival.Scale, "点", CycleSurvival.Scale);
        var low = CycleVisibleValue.Numeric(30 * CycleSurvival.Scale, "点", CycleSurvival.Scale);
        var first = Event(7, new(key, n, low));
        var second = Event(9, new(key, low, n));
        var update = CycleViewUpdate.Create(snapshot, snapshot, [first, second], PlayDeliveryPhase.Advancing);
        Assert.Equal([7, 9], update.Events.Select(e => e.EventOrdinal));
        Assert.Equal(n, update.CurrentValues[key]);
        Assert.Single(update.CurrentValues, pair => pair.Key == key);
        string text = CycleUpdateRenderer.Render(update);
        Assert.Contains("-45", text);
        Assert.Contains("+45", text);
    }

    [Fact]
    public void Derived_risk_removal_activity_absence_and_warning_context_are_semantic_changes() {
        var activity = new CycleActivity(CycleActivityKind.Wait, 1, new(0), CycleTime.Hour, new(CycleTime.Hour));
        var before = CycleViewSnapshot.Create(Observation(activity: activity, water: 20, warning: true, commandRisk: "饮水不足"));
        var after = CycleViewSnapshot.Create(Observation(water: 75, warning: true));
        var update = CycleViewUpdate.Create(before, after, [], PlayDeliveryPhase.ControllerInput);
        Assert.Equal(CycleVisibleValue.KnownAbsent, update.CurrentValues[new("activity", "a", "kind")]);
        Assert.Contains(new CycleStateKey("body", "a", "warnings"), update.CurrentValues.Keys);
        Assert.Equal(CycleVisibleValue.KnownAbsent, update.CurrentValues[new("command", CycleViewSnapshot.CommandId(CycleCommand.Wait(1)), "warning")]);
        foreach (var key in before.Values.Keys.Concat(after.Values.Keys).Distinct()) {
            var reconstructed = update.CurrentValues.TryGetValue(key, out var current) ? current : before.Resolve(key);
            Assert.Equal(after.Resolve(key), reconstructed);
        }
    }

    [Fact]
    public void Projection_and_events_do_not_include_another_actors_private_values() {
        var observation = ViewContextFixture.Create(CycleGameFactory.CreateSurvival().World, "a").Observation;
        var snapshot = CycleViewSnapshot.Create(observation);
        var foreign = new CycleObservedEvent(0, "secret", CycleObservedEventKind.ItemUsed, observation.Time, observation.Time,
            effects: [new(new("body", "secret", "water"), CycleVisibleValue.Numeric(0), CycleVisibleValue.Numeric(80))]);
        var update = CycleViewUpdate.Create(snapshot, snapshot, [foreign], PlayDeliveryPhase.ControllerInput);
        Assert.Empty(update.Events);
        Assert.DoesNotContain(snapshot.Values.Keys, key => key.ObjectId == "secret");
        Assert.DoesNotContain("secret", CycleUpdateRenderer.Render(update));
    }

    private static CycleObservedEvent Event(int ordinal, CycleObservedEffect effect) =>
        new(ordinal, "a", CycleObservedEventKind.ItemUsed, new(0), new(0), effects: [effect]);

    private static CycleObservation Observation(IReadOnlyDictionary<string, int>? items = null,
        IReadOnlyList<CycleResource>? resources = null, CycleActivity? activity = null, decimal water = 75,
        bool warning = false, string? commandRisk = null,
        IReadOnlyList<CyclePassageObservation>? passages = null) => new() {
        Time = new(0), IsDay = true, NextLightChange = new(12 * CycleTime.Hour),
        Actor = new("a", "旅人", 0, new ItemInventory(items), activity, [], null, 1),
        Resources = resources ?? [], HarvestDurations = (resources ?? []).ToDictionary(r => r.Id, r => r.HarvestTicks),
        ItemNames = new Dictionary<string, string> { ["item.food"] = "食物" }, OtherCharacters = [], Notices = [],
        Commands = [new(CycleCommand.Wait(1), null, commandRisk)],
        Passages = passages ?? [],
        Survival = new(70, water, 70, TestRate(-5), TestRate(-6), TestRate(5), false, warning ? ["饮水曾触及警戒线"] : [],
            new(null, null), new(null, null), new(null, null),
            PendingWarningMask: warning ? 2 : 0)
    };

    private static RateBreakdown TestRate(int amount) => new([new("test", "测试来源", amount)]);
}

using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class IncrementalProjectionTests {
    [Fact]
    public void Context_history_is_not_flattened_into_current_state_changes() {
        var observation = Observation();
        var state = ViewContextFixture.State(observation);
        var before = PlaygroundViewSnapshot.Create(observation, state);
        var after = PlaygroundViewSnapshot.Create(observation, new CharacterInteractionState("a", state.CurrentContext, draftHandoff: "记下此处"));
        var update = PlaygroundViewUpdate.Create(before, after, [], PlayDeliveryPhase.ControllerInput);
        Assert.Equal("记下此处", update.CurrentValues[new("continuity", "a", "draft")].Text);
        Assert.DoesNotContain(update.CurrentValues.Keys, key => key.Category.StartsWith("incoming", StringComparison.Ordinal));
        Assert.DoesNotContain("食物：无", PlaygroundUpdateRenderer.Render(update));
    }

    [Fact]
    public void Withdrawn_passages_retain_their_distinct_known_object_labels() {
        var previous = PlaygroundViewSnapshot.Create(Observation(passages: [new("trail-a", "通向森林", PlaygroundTime.Hour, null),
            new("trail-b", "通向河岸", PlaygroundTime.Hour / 2, "河岸")]));
        var current = PlaygroundViewSnapshot.Create(Observation());
        var update = PlaygroundViewUpdate.Create(previous, current, [], PlayDeliveryPhase.Advancing);
        foreach (string id in new[] { "trail-a", "trail-b" }) {
            var key = new PlaygroundStateKey("passage", id, "description");
            Assert.Equal(PlaygroundVisibleValue.Unobserved, update.CurrentValues[key]);
            Assert.Contains(id, update.Label(key));
            Assert.Equal(previous.Label(key), update.Label(key));
        }
        string rendered = PlaygroundUpdateRenderer.Render(update);
        Assert.Contains("trail-a", rendered);
        Assert.Contains("trail-b", rendered);
        Assert.Contains("不再可见", rendered);
        Assert.DoesNotContain("- description：", rendered);
    }

    [Fact]
    public void Filtering_preserves_original_commit_and_tool_source_identity() {
        var snapshot = PlaygroundViewSnapshot.Create(Observation());
        var own = Event(9, new(new("body", "a", "water"), PlaygroundVisibleValue.Numeric(1), PlaygroundVisibleValue.Numeric(2)));
        var foreign = new PlaygroundObservedEvent(3, "b", PlaygroundObservedEventKind.ItemUsed, new(0), new(0));
        PlaygroundObservedEventSource[] sources = [new(41, 8, 0, 3, "foreign", "provider-b"), new(41, 8, 1, 9, "own", "provider:a")];
        var update = PlaygroundViewUpdate.Create(snapshot, snapshot, [foreign, own], PlayDeliveryPhase.Advancing, sources: sources);
        Assert.Same(own, Assert.Single(update.Events));
        Assert.Equal(sources[1], Assert.Single(update.Sources));
    }

    [Fact]
    public void Depleted_owned_item_is_zero_once_without_retaining_a_tombstone() {
        var before = PlaygroundViewSnapshot.Create(Observation(items: new Dictionary<string, int> { ["item.food"] = 1 }));
        var after = PlaygroundViewSnapshot.Create(Observation());
        var key = new PlaygroundStateKey("inventory", "item.food", "count");
        var effect = new PlaygroundObservedEffect(key, PlaygroundVisibleValue.Numeric(1, "份"), PlaygroundVisibleValue.Numeric(0, "份"));
        var update = PlaygroundViewUpdate.Create(before, after, [Event(0, effect)], PlayDeliveryPhase.ControllerInput);
        Assert.Equal(PlaygroundVisibleValue.Numeric(0, "份"), update.CurrentValues[key]);
        Assert.DoesNotContain(key, after.Values.Keys);
        Assert.DoesNotContain(key, PlaygroundViewUpdate.Create(after, after, [], PlayDeliveryPhase.ControllerInput).CurrentValues.Keys);
    }

    [Fact]
    public void Leaving_and_reentering_visibility_are_not_resource_destruction_or_creation() {
        var resource = new PlaygroundResource("berries", "野果", "item.food", true, PlaygroundTime.Hour, 5, 3);
        var visible = PlaygroundViewSnapshot.Create(Observation(resources: [resource]));
        var absent = PlaygroundViewSnapshot.Create(Observation());
        var key = new PlaygroundStateKey("resource", resource.Id, "stock");
        Assert.Equal(PlaygroundVisibleValue.Unobserved, PlaygroundViewUpdate.Create(visible, absent, [], PlayDeliveryPhase.Advancing).CurrentValues[key]);
        Assert.Equal(PlaygroundVisibleValue.Numeric(3, "份"), PlaygroundViewUpdate.Create(absent, visible, [], PlayDeliveryPhase.Advancing).CurrentValues[key]);
        Assert.Empty(PlaygroundViewUpdate.Create(absent, absent, [], PlayDeliveryPhase.Advancing).CurrentValues);
    }

    [Fact]
    public void Net_zero_events_keep_their_order_and_select_final_value_once() {
        var snapshot = PlaygroundViewSnapshot.Create(Observation());
        var key = new PlaygroundStateKey("body", "a", "water");
        var n = PlaygroundVisibleValue.Numeric(75 * PlaygroundSurvival.Scale, "点", PlaygroundSurvival.Scale);
        var low = PlaygroundVisibleValue.Numeric(30 * PlaygroundSurvival.Scale, "点", PlaygroundSurvival.Scale);
        var first = Event(7, new(key, n, low));
        var second = Event(9, new(key, low, n));
        var update = PlaygroundViewUpdate.Create(snapshot, snapshot, [first, second], PlayDeliveryPhase.Advancing);
        Assert.Equal([7, 9], update.Events.Select(e => e.EventOrdinal));
        Assert.Equal(n, update.CurrentValues[key]);
        Assert.Single(update.CurrentValues, pair => pair.Key == key);
        string text = PlaygroundUpdateRenderer.Render(update);
        Assert.Contains("-45", text);
        Assert.Contains("+45", text);
    }

    [Fact]
    public void Derived_risk_removal_activity_absence_and_warning_context_are_semantic_changes() {
        var activity = new PlaygroundActivity(PlaygroundActivityKind.Wait, 1, new(0), PlaygroundTime.Hour, new(PlaygroundTime.Hour));
        var before = PlaygroundViewSnapshot.Create(Observation(activity: activity, water: 20, warning: true, commandRisk: "饮水不足"));
        var after = PlaygroundViewSnapshot.Create(Observation(water: 75, warning: true));
        var update = PlaygroundViewUpdate.Create(before, after, [], PlayDeliveryPhase.ControllerInput);
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, update.CurrentValues[new("activity", "a", "kind")]);
        Assert.Contains(new PlaygroundStateKey("body", "a", "warnings"), update.CurrentValues.Keys);
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, update.CurrentValues[new("command", PlaygroundViewSnapshot.CommandId(PlaygroundCommand.Wait(1)), "warning")]);
        foreach (var key in before.Values.Keys.Concat(after.Values.Keys).Distinct()) {
            var reconstructed = update.CurrentValues.TryGetValue(key, out var current) ? current : before.Resolve(key);
            Assert.Equal(after.Resolve(key), reconstructed);
        }
    }

    [Fact]
    public void Projection_and_events_do_not_include_another_actors_private_values() {
        var observation = ViewContextFixture.Create(PlaygroundGameFactory.CreateSurvival().World, "a").Observation;
        var snapshot = PlaygroundViewSnapshot.Create(observation);
        var foreign = new PlaygroundObservedEvent(0, "secret", PlaygroundObservedEventKind.ItemUsed, observation.Time, observation.Time,
            effects: [new(new("body", "secret", "water"), PlaygroundVisibleValue.Numeric(0), PlaygroundVisibleValue.Numeric(80))]);
        var update = PlaygroundViewUpdate.Create(snapshot, snapshot, [foreign], PlayDeliveryPhase.ControllerInput);
        Assert.Empty(update.Events);
        Assert.DoesNotContain(snapshot.Values.Keys, key => key.ObjectId == "secret");
        Assert.DoesNotContain("secret", PlaygroundUpdateRenderer.Render(update));
    }

    private static PlaygroundObservedEvent Event(int ordinal, PlaygroundObservedEffect effect) =>
        new(ordinal, "a", PlaygroundObservedEventKind.ItemUsed, new(0), new(0), effects: [effect]);

    private static PlaygroundObservation Observation(IReadOnlyDictionary<string, int>? items = null,
        IReadOnlyList<PlaygroundResource>? resources = null, PlaygroundActivity? activity = null, decimal water = 75,
        bool warning = false, string? commandRisk = null,
        IReadOnlyList<PlaygroundPassageObservation>? passages = null) => new() {
        Time = new(0), IsDay = true, NextLightChange = new(12 * PlaygroundTime.Hour),
        Actor = new("a", "旅人", 0, new ItemInventory(items), activity, [], null, 1),
        Resources = resources ?? [], HarvestDurations = (resources ?? []).ToDictionary(r => r.Id, r => r.HarvestTicks),
        ItemNames = new Dictionary<string, string> { ["item.food"] = "食物" }, OtherCharacters = [], Notices = [],
        Commands = [new(PlaygroundCommand.Wait(1), null, commandRisk)],
        Passages = passages ?? [],
        Survival = new(70, water, 70, TestRate(-5), TestRate(-6), TestRate(5), false, warning ? ["饮水曾触及警戒线"] : [],
            new(null, null), new(null, null), new(null, null),
            PendingWarningMask: warning ? 2 : 0)
    };

    private static RateBreakdown TestRate(int amount) => new([new("test", "测试来源", amount)]);
}

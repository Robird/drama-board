using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class AttributeForecastViewTests {
    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public void Layouts_render_each_saved_forecast_and_one_shared_assumption_without_action_repetition(string layoutId) {
        var initial = Observation();
        var observation = WithSurvival(initial, initial.Survival! with {
            FoodForecast = new(At(17), At(22)),
            WaterForecast = new(At(18), At(23)),
            WarmthForecast = new(null, null)
        });
        string text = Layout(layoutId).Render(observation);
        foreach (string field in new[] { "food", "water", "warmth" }) {
            PlaygroundAttributeView attribute = PlaygroundAttributeView.FromSurvival(observation.Survival!).Single(a => a.Field == field);
            Assert.Contains(attribute.ForecastDescription, text);
        }
        Assert.Contains("第 0 天 22:00", text);
        Assert.Contains("第 0 天 23:00", text);
        Assert.Single(text.Split('\n'), line => line.Contains("预测条件：", StringComparison.Ordinal));
        Assert.DoesNotContain("预计最早", text);
        Assert.DoesNotContain("风险：", text);
        Assert.DoesNotContain("行动提示（不禁止）", text);
        Assert.DoesNotContain(PlaygroundViewSnapshot.Create(observation).Values.Keys, key => key.Field is "fatal-at" or "warning");
    }

    [Fact]
    public void Forecast_only_change_redraws_only_affected_attribute_without_inventing_events() {
        var before = Observation();
        var after = WithSurvival(before, before.Survival! with { WaterForecast = new(At(19), At(25)) });
        var update = Update(before, after);
        Assert.Empty(update.Events);
        Assert.Contains(new PlaygroundStateKey("body", "a", "water"), update.CurrentValues.Keys);
        Assert.DoesNotContain(update.CurrentValues.Keys, key => key.Field is "food" or "warmth" or "warning");
        string text = PlaygroundUpdateRenderer.Render(update);
        Assert.Contains("预计耗尽至 0：第 1 天 01:00", text);
        Assert.Single(text.Split('\n'), line => line.Contains("预计耗尽至 0", StringComparison.Ordinal));
        Assert.DoesNotContain("预测条件：", text);
        Assert.Empty(Update(after, after).CurrentValues);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public void Context_replay_retains_opening_forecast_while_latest_preview_uses_saved_update(string layoutId) {
        var source = Observation();
        var before = WithSurvival(source, source.Survival! with { WaterForecast = new(At(18), At(23)) });
        var after = WithSurvival(before, before.Survival! with { WaterForecast = new(At(19), At(25)) });
        var opening = ViewContextFixture.State(before).CurrentContext!.Opening;
        var state = new CharacterInteractionState("a", new CharacterContext(1, CharacterContextStatus.Open,
            opening, [new CharacterContextItem(1, observation: after)], nextItemSequence: 2));
        string full = CharacterContextRenderer.RenderFull(state, Layout(layoutId));
        Assert.Contains("预计耗尽至 0：第 0 天 23:00", full);
        Assert.Contains("预计耗尽至 0：第 1 天 01:00", full);
        Assert.Single(full.Split('\n'), line => line.Contains("预测条件：", StringComparison.Ordinal));
        string latest = CharacterContextRenderer.RenderLatestState(state, Layout(layoutId));
        Assert.DoesNotContain("预计耗尽至 0：第 0 天 23:00", latest);
        Assert.Contains("预计耗尽至 0：第 1 天 01:00", latest);
    }

    [Fact]
    public void Numeric_warning_zone_and_pending_historical_warning_remain_independent() {
        var initial = Observation();
        var low = initial.Survival! with { Food = 20, PendingWarningMask = 0 };
        var lowView = PlaygroundAttributeView.FromSurvival(low).Single(a => a.Field == "food");
        Assert.Contains("已处于警戒区（≤25）", lowView.ForecastDescription);
        Assert.DoesNotContain("历史警告待确认", lowView.ForecastDescription);
        var replenished = low with { Food = 60, PendingWarningMask = 1, FoodForecast = new(At(22), At(27)) };
        string text = PlaygroundAttributeView.FromSurvival(replenished).Single(a => a.Field == "food").ForecastDescription;
        Assert.DoesNotContain("已处于警戒区", text);
        Assert.Contains("预计降至警戒线 25：第 0 天 22:00", text);
        Assert.Contains("历史警告待确认", text);
    }

    [Fact]
    public void Changed_threshold_alone_refreshes_attributes() {
        var before = Observation();
        var after = WithSurvival(before, before.Survival! with { WarningThreshold = 35 });
        var update = Update(before, after);
        Assert.Contains(new PlaygroundStateKey("body", "a", "food"), update.CurrentValues.Keys);
        Assert.Contains("已处于警戒区（≤35）", PlaygroundUpdateRenderer.Render(update));
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public void Terminal_material_does_not_present_frozen_forecasts_as_future_gameplay(string layoutId) {
        var before = Observation();
        var ended = WithSurvival(before, before.Survival!, PlaygroundExpeditionOutcome.Dead);
        string text = Layout(layoutId).Render(ended);
        Assert.DoesNotContain("预计降至警戒线", text);
        Assert.DoesNotContain("预计耗尽至", text);
        Assert.DoesNotContain("预测条件：", text);
        Assert.Contains("不再", text);
    }

    [Fact]
    public void Only_removal_of_a_real_action_exception_emits_a_clearing_message() {
        var initial = Observation();
        var before = WithSurvival(initial, initial.Survival!, commands: [new(PlaygroundCommand.Go("trail-1"), null, "离开庇护")]);
        var after = WithSurvival(initial, initial.Survival!, commands: [new(PlaygroundCommand.Go("trail-1"), null, null)]);
        string delta = PlaygroundUpdateRenderer.Render(Update(before, after));
        Assert.Contains("原行动提示已不再适用", delta);
        Assert.DoesNotContain("风险：无", delta);
        Assert.Empty(Update(after, after).CurrentValues);
    }

    private static ModelTime At(int hour) => new(hour * PlaygroundTime.Hour);
    private static PlaygroundObservation Observation() => PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
    private static IPlayLayout Layout(string id) => PlayPresentation.ForMode(true).FindLayout(id)!;
    private static PlaygroundViewUpdate Update(PlaygroundObservation before, PlaygroundObservation after) =>
        PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(before), PlaygroundViewSnapshot.Create(after), [], PlayDeliveryPhase.ControllerInput);

    private static PlaygroundObservation WithSurvival(PlaygroundObservation source, PlaygroundSurvivalObservation survival,
        PlaygroundExpeditionOutcome? outcome = null, IReadOnlyList<PlaygroundCommandCheck>? commands = null) => new() {
        Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, WorldName = source.WorldName, ItemUseDescriptions = source.ItemUseDescriptions, ItemNames = source.ItemNames,
        OtherCharacters = source.OtherCharacters, Notices = source.Notices, Commands = commands ?? source.Commands,
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, ArrivalDue = source.ArrivalDue, Journey = source.Journey, Passages = source.Passages,
        Memories = source.Memories, Outcome = outcome ?? source.Outcome, EndingReason = source.EndingReason,
        Survival = survival, Weather = source.Weather
    };
}

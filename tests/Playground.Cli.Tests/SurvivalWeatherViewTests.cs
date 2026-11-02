using System.Text;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class SurvivalWeatherViewTests {
    [Fact]
    public void Default_game_exposes_weather_opportunity_and_cost_without_random_state_or_future_outcome() {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival().World;
        PlaygroundDecisionContext context = ViewContextFixture.Create(world, "a");
        string before = world.DescribeState();
        PlaygroundWeatherObservation weather = Assert.IsType<PlaygroundWeatherObservation>(context.Observation.Weather);
        Assert.NotNull(weather.NextWindow);
        Assert.Null(weather.LastAt);
        Assert.Null(weather.LastKind);
        string text = PlayView.Render(context.Observation, context.InteractionState);
        Assert.Contains("## 天气与暴露", text);
        Assert.Contains("下次窗口", text);
        Assert.Contains("可能平静，也可能遭遇寒风", text);
        Assert.Contains("寒风使暴露者的温暖最多减少", text);
        Assert.Contains("尚无已发生的天气记录", text);
        Assert.Contains("未计随机寒风", text);
        Assert.Contains("属性预测见身体状态", PlayView.RenderCommands(context.Observation));
        string json = JsonSerializer.Serialize(context.Observation);
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);
        // ContextSequence is a visible decision identity; the weather RNG's Sequence remains private.
        Assert.DoesNotContain("\"Sequence\":", json, StringComparison.OrdinalIgnoreCase);
        string alternate = JsonSerializer.Serialize(ViewContextFixture.Create(
            PlaygroundGameFactory.CreateSurvival(weatherSeed: 987654321UL).World, "a").Observation);
        Assert.Equal(json, alternate);
        Assert.Equal(before, world.DescribeState());
        Assert.Null(ViewContextFixture.Create(PlaygroundGameFactory.CreateSurvival(enableNeeds: false).World, "a").Observation.Weather);
    }

    [Theory]
    [InlineData(false, true, 0, "当前没有庇护保护", "当时受到庇护保护，温暖损失 0 点")]
    [InlineData(true, false, 10, "当前有庇护保护", "当时暴露在外，温暖实际减少 10 点")]
    public void Current_shelter_does_not_rewrite_the_recorded_effect(bool shelteredNow, bool shelteredThen,
        int loss, string current, string history) {
        var weather = new PlaygroundWeatherObservation(new ModelTime(19 * PlaygroundTime.Hour), 2 * PlaygroundTime.Hour, 10,
            shelteredNow, new ModelTime(17 * PlaygroundTime.Hour), PlaygroundWeatherKind.ColdWind, loss, shelteredThen);
        var text = new StringBuilder();
        PlayView.RenderWeatherHistory(text, weather);
        PlayView.RenderCurrentWeather(text, weather);
        Assert.Contains(current, text.ToString());
        Assert.Contains(history, text.ToString());
        Assert.Contains("最近实际天气：第 0 天 17:00，寒风", text.ToString());
        Assert.Contains("不预示下一次结果", text.ToString());
    }

    [Fact]
    public async Task Actual_weather_is_in_the_next_complete_observation_without_needing_a_broadcast() {
        var initial = PlaygroundGameFactory.CreateSurvival();
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new WaitAndAcknowledge());
        for (int i = 0; i < 30 && (PlaygroundDecisionContext.Observe(kernel.World, "a").Weather!.LastAt is null || kernel.World.Business.InteractionStates["a"].CurrentContext?.Status != CharacterContextStatus.Open); i++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(24 * PlaygroundTime.Hour)));
        }
        PlaygroundObservation observation = PlaygroundDecisionContext.Create(kernel.World, "a").Observation;
        Assert.NotNull(observation.Weather!.LastAt);
        Assert.NotNull(observation.Weather.LastKind);
        string text = PlayView.Render(observation);
        Assert.Contains("最近实际天气", text);
        Assert.DoesNotContain("尚无已发生的天气记录", text);
        Assert.Contains(observation.Weather.LastKind == PlaygroundWeatherKind.ColdWind
            ? "当时暴露在外，温暖实际减少" : "没有天气造成的温暖损失", text);
        CharacterContext context = PlaygroundDecisionContext.Create(kernel.World, "a").Context;
        Assert.Contains(context.Opening.Experiences, experience => experience.Event.Kind == PlaygroundObservedEventKind.WeatherResolved);
        string history = ContextHistoryPlayView.RenderHistory(observation, kernel.World.Business.InteractionStates["a"]);
        Assert.Contains("天气结算", history);
    }

    [Fact]
    public void Terminal_weather_shows_only_recorded_history() {
        var weather = new PlaygroundWeatherObservation(new ModelTime(19 * PlaygroundTime.Hour), 2 * PlaygroundTime.Hour, 10,
            false, new ModelTime(17 * PlaygroundTime.Hour), PlaygroundWeatherKind.ColdWind, 7, false);
        var text = new StringBuilder();
        PlayView.RenderWeatherHistory(text, weather);
        Assert.Contains("温暖实际减少 7 点", text.ToString());
        Assert.DoesNotContain("下次窗口", text.ToString());
        Assert.DoesNotContain("当前没有庇护", text.ToString());
        Assert.DoesNotContain("寒风使暴露者", text.ToString());
    }

    private sealed class WaitAndAcknowledge : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("call_" + Guid.NewGuid().ToString("N"),
                PlaygroundCommandParser.Format(context.Check(PlaygroundCommand.Continue()) is null ? PlaygroundCommand.Continue() : PlaygroundCommand.Wait(24)), Guid.NewGuid().ToString("N")));
    }
}

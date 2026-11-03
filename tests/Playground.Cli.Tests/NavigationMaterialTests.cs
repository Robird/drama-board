using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class NavigationMaterialTests {
    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public async Task Frozen_character_material_reveals_connections_only_after_actual_arrival(string layoutId) {
        IPlayLayout layout = layoutId == "classic" ? new ClassicPlayLayout() : new DecisionPlayLayout();
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new MoveOnce());
        PlaygroundObservation before = PlaygroundDecisionContext.Observe(kernel.World, "a");
        string frozenBefore = layout.Render(before);
        Assert.Contains("## 已知地图", frozenBefore);
        Assert.Contains("`camp`", frozenBefore);
        Assert.Contains("go e-c7", frozenBefore);
        Assert.Contains("这条入口去向未确认", frozenBefore);
        AssertHidden(frozenBefore);
        AssertHidden(layout.RenderActionHelp(before));
        Assert.DoesNotContain("e-n4", frozenBefore);
        Assert.DoesNotContain("溪流湾", frozenBefore);

        PlaygroundObservation? travelling = null;
        PlaygroundObservation? arrived = null;
        for (int i = 0; i < 30; i++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour)));
            var observation = PlaygroundDecisionContext.Observe(kernel.World, "a");
            if (observation.Journey is not null) { travelling = observation; }
            if (PlaygroundActions.CurrentPlace(kernel.World, "a") == "stream" &&
                kernel.World.Business.InteractionStates["a"].CurrentContext is { Status: CharacterContextStatus.Open }) {
                arrived = observation;
                break;
            }
        }
        Assert.NotNull(travelling);
        Assert.NotNull(arrived);
        Assert.Contains("入口 `e-c7`", layout.Render(travelling));
        Assert.DoesNotContain("溪流湾", layout.Render(travelling));
        AssertHidden(layout.Render(travelling));
        Assert.Contains("go e-n4", layout.Render(arrived));
        Assert.Contains("`stream`", layout.Render(arrived));
        Assert.Contains("已确认", layout.Render(arrived));
        AssertHidden(layout.Render(arrived));
        Assert.Equal(frozenBefore, layout.Render(before)); // Rendering never refreshes a saved view from the world.

        var update = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(before), PlaygroundViewSnapshot.Create(arrived),
            [], PlayDeliveryPhase.ControllerInput);
        string delta = layout.RenderUpdate(update);
        Assert.Contains("溪流湾", delta);
        Assert.Contains("e-n4", delta);
        Assert.Contains("camp", delta);
        AssertHidden(delta);
        var state = kernel.World.Business.InteractionStates["a"];
        AssertHidden(CharacterContextRenderer.RenderFull(state, layout));
        AssertHidden(CharacterContextRenderer.RenderLatestState(state, layout));
    }

    [Fact]
    public async Task A_guessed_internal_id_is_only_retained_as_raw_input_and_does_not_confirm_a_connection() {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Guess());
        for (int i = 0; i < 10; i++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour)));
            var state = kernel.World.Business.InteractionStates["a"];
            if (state.CurrentContext?.Items.FirstOrDefault(entry => entry.RawInput == "go trail-1") is not { } item) { continue; }
            Assert.True(item.IsRejected);
            Assert.DoesNotContain("trail-1", item.Result!);
            Assert.DoesNotContain("溪流湾", item.Result!);
            Assert.All(state.CurrentContext.LatestObservation.KnownMap.SelectMany(place => place.Entrances),
                entrance => Assert.Null(entrance.KnownDestinationId));
            foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
                string material = CharacterContextRenderer.RenderFull(state, layout);
                Assert.Contains("原始输入，仅为记录", material);
                Assert.Contains("go trail-1", material); // The user's guess is a record, never a world assertion.
                Assert.DoesNotContain("溪流湾", material);
                Assert.DoesNotContain("e-n4", material);
            }
            return;
        }
        Assert.Fail("The guessed command did not produce a retained rejection.");
    }

    private sealed class Guess : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("guess-call", "go trail-1", "guess-record"));
    }

    private static void AssertHidden(string text) {
        foreach (string hidden in new[] { "trail-", "market-home", "market-workshop", "home-workshop", "road-camp", "巡山信号站", "e-l9" }) {
            Assert.DoesNotContain(hidden, text);
        }
    }

    private sealed class MoveOnce : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("navigation-call", "go e-c7", "navigation-record"));
    }
}

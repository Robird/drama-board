using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlayPresentationTests {
    [Theory]
    [InlineData(true, "decision")]
    [InlineData(false, "classic")]
    public void Mode_selects_default_and_unknown_selection_keeps_current_layout(bool survival, string expected) {
        PlayPresentation presentation = PlayPresentation.ForMode(survival);
        Assert.Equal(expected, presentation.Current.Id);
        IPlayLayout original = presentation.Current;
        Assert.False(presentation.TrySelect("unknown"));
        Assert.Same(original, presentation.Current);
        Assert.True(presentation.TrySelect("classic"));
        Assert.Equal("classic", presentation.Current.Id);
        Assert.True(presentation.TrySelect("decision"));
        Assert.Equal("decision", presentation.Current.Id);
        Assert.Equal(expected, PlayPresentation.ForMode(survival).Current.Id);
    }

    [Fact]
    public async Task Switching_redraws_the_same_observation_and_local_queries_do_not_submit_actions() {
        var first = new RecordingLayout("first");
        var second = new RecordingLayout("second");
        var presentation = new PlayPresentation(first, second);
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival().World;
        string before = world.DescribeState();
        PlaygroundDecisionContext context = ViewContextFixture.Create(world, "a");
        using var input = new StringReader(":layout\n:layout second\n:look\n:help\n:layout first once\n:layout missing\n:layout second extra\nwait 1\n");
        using var output = new StringWriter();
        int boundaries = 0;
        var controller = new PlayConsole.ConsoleCharacterController(presentation, ConsoleMetaCommands.HumanDefault, input, output, _ => boundaries++);

        PlaygroundControllerOperation operation = await controller.ChooseCommandAsync(context, default);

        Assert.Equal("wait 1", operation.RawInput);
        Assert.Same(second, presentation.Current);
        Assert.Equal(2, first.Screens.Count);
        Assert.Equal(2, second.Screens.Count);
        Assert.Single(second.Help);
        Assert.All(first.Screens.Concat(second.Screens).Concat(second.Help),
            observation => Assert.Same(context.Observation, observation));
        Assert.All(first.Snapshots.Concat(second.Snapshots), snapshot => Assert.Same(context.Context, snapshot.Context));
        Assert.Equal(before, world.DescribeState());
        Assert.Equal(1, boundaries);
    }

    [Fact]
    public async Task Distinct_saved_contexts_begin_once_and_repeated_input_does_not_redraw() {
        var layout = new RecordingLayout("test");
        var presentation = new PlayPresentation(layout);
        PlaygroundWorld earlier = PlaygroundGameFactory.CreatePlayable(new ModelTime(16 * PlaygroundTime.Hour)).World;
        PlaygroundWorld later = PlaygroundGameFactory.CreatePlayable(new ModelTime(17 * PlaygroundTime.Hour)).World;
        using var input = new StringReader("wait 1\n:help\nwait 1\nwait 1\nwait 1\n");
        using var output = new StringWriter();
        int boundaries = 0;
        var controller = new PlayConsole.ConsoleCharacterController(presentation, ConsoleMetaCommands.HumanDefault, input, output,
            _ => boundaries++);

        await controller.ChooseCommandAsync(ViewContextFixture.Create(earlier, "a"), default);
        await controller.ChooseCommandAsync(ViewContextFixture.Create(earlier, "a"), default);
        Assert.Equal(1, boundaries);

        await controller.ChooseCommandAsync(ViewContextFixture.Create(earlier, "b"), default);
        Assert.Equal(2, boundaries);

        await controller.ChooseCommandAsync(ViewContextFixture.Create(later, "b", 2), default);
        Assert.Equal(3, boundaries);

        // Explicit observation rendering consumes the saved identity, never a model-time heuristic.
        controller.RenderObservation(ViewContextFixture.Create(
            PlaygroundGameFactory.CreatePlayable(new ModelTime(18 * PlaygroundTime.Hour)).World, "b", 3));
        Assert.Equal(4, boundaries);
        Assert.Equal(4, layout.Screens.Count);
    }

    [Fact]
    public async Task Survival_separates_first_delivery_and_new_context_even_without_time_advancing() {
        var layout = new RecordingLayout("test");
        using var input = new StringReader("wait 1\nwait 1\nwait 1\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(new PlayPresentation(layout), ConsoleMetaCommands.HumanDefault, input, output) {
            StartupText = "startup-help"
        };
        int BoundaryCount() => output.ToString().Split("LLM 上下文开始").Length - 1;
        PlaygroundWorld first = SurvivalContext(15, 1);
        PlaygroundWorld sameContextLaterTime = SurvivalContext(16, 1);
        PlaygroundWorld nextContextSameTime = SurvivalContext(16, 2);

        await controller.ChooseCommandAsync(ViewContextFixture.Create(first, "a"), default);
        Assert.Equal(1, BoundaryCount());
        Assert.Contains("startup-help", output.ToString());
        Assert.Null(controller.StartupText);
        await controller.ChooseCommandAsync(ViewContextFixture.Create(sameContextLaterTime, "a"), default);
        Assert.Equal(1, BoundaryCount());
        await controller.ChooseCommandAsync(ViewContextFixture.Create(nextContextSameTime, "a"), default);
        Assert.Equal(2, BoundaryCount());
        controller.RenderObservation(ViewContextFixture.Create(nextContextSameTime, "a"));
        Assert.Equal(2, BoundaryCount());
        Assert.Contains("startup-help", output.ToString()); // Earlier output remains in the transcript.
        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
    }

    private static PlaygroundWorld SurvivalContext(int hour, long sequence) {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival(new ModelTime(hour * PlaygroundTime.Hour)).World;
        var state = ViewContextFixture.State(PlaygroundDecisionContext.Observe(world, "a"), sequence);
        return PlaygroundEngine.Rebuild(world.Business.Replace(world.Time, interactionChanges: [state]), world.Time);
    }

    [Fact]
    public async Task Shared_console_keeps_layout_across_decisions_but_passes_each_actors_own_observation() {
        var first = new RecordingLayout("first");
        var second = new RecordingLayout("second");
        var presentation = new PlayPresentation(first, second);
        PlaygroundWorld world = PlaygroundGameFactory.CreatePlayable().World;
        using var input = new StringReader(":layout second\nwait 1\n:look\nwait 1\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(presentation, ConsoleMetaCommands.HumanDefault, input, output);
        PlaygroundDecisionContext a = ViewContextFixture.Create(world, "a");
        PlaygroundDecisionContext b = ViewContextFixture.Create(world, "b");

        Assert.Equal("wait 1", (await controller.ChooseCommandAsync(a, default)).RawInput);
        Assert.Equal("wait 1", (await controller.ChooseCommandAsync(b, default)).RawInput);

        Assert.Same(second, presentation.Current);
        Assert.Single(first.Screens);
        Assert.Equal(3, second.Screens.Count);
        Assert.Same(a.Observation, second.Screens[0]);
        Assert.All(second.Screens.Skip(1), observation => Assert.Same(b.Observation, observation));
    }

    // Test the presentation boundary, not a concrete layout's wording, ordering or spacing.
    private sealed class RecordingLayout(string id) : IPlayLayout {
        public string Id => id;
        public string Name => id;
        public List<PlaygroundObservation> Screens { get; } = [];
        public List<PlaygroundObservation> Help { get; } = [];
        public List<PlaygroundViewSnapshot> Snapshots { get; } = [];
        public string RenderFull(PlaygroundViewSnapshot snapshot) { Screens.Add(snapshot.Observation); Snapshots.Add(snapshot); return Id; }
        public string Render(PlaygroundObservation observation, CharacterInteractionState? interactionState = null) { Screens.Add(observation); return Id; }
        public string RenderActionHelp(PlaygroundObservation observation) { Help.Add(observation); return Id; }
    }
}

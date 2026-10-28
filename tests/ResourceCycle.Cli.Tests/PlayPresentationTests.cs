using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

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
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        string before = world.DescribeState();
        CycleDecisionContext context = CycleDecisionContext.Create(world, "a");
        using var input = new StringReader("look\nhelp\nquit\n:layout\n:layout second\n:look\n:help\n:details\n:layout missing\n:layout second extra\nwait 1\n");
        using var output = new StringWriter();
        int clears = 0;
        var controller = new PlayConsole.ConsoleCharacterController(presentation, input, output, () => clears++);

        CycleControllerOperation operation = await controller.ChooseCommandAsync(context, default);

        Assert.Equal(CycleCommand.Wait(1), operation.Command);
        Assert.Same(second, presentation.Current);
        Assert.Single(first.Screens);
        Assert.Equal(2, second.Screens.Count);
        Assert.Single(second.Help);
        Assert.All(first.Screens.Concat(second.Screens).Concat(second.Help).Concat(second.Prompts),
            observation => Assert.Same(context.Observation, observation));
        Assert.Equal(before, world.DescribeState());
        Assert.Equal(1, clears);
    }

    [Fact]
    public async Task New_time_or_actor_clears_before_rendering_but_same_time_interactions_remain() {
        var layout = new RecordingLayout("test");
        var presentation = new PlayPresentation(layout);
        CycleWorld earlier = CycleGameFactory.CreatePlayable(new ModelTime(16 * CycleTime.Hour)).World;
        CycleWorld later = CycleGameFactory.CreatePlayable(new ModelTime(17 * CycleTime.Hour)).World;
        using var input = new StringReader("wait 1\n:help\nwait 1\nwait 1\nwait 1\n");
        using var output = new StringWriter();
        var screensAtClear = new List<int>();
        var controller = new PlayConsole.ConsoleCharacterController(presentation, input, output,
            () => screensAtClear.Add(layout.Screens.Count));

        await controller.ChooseCommandAsync(CycleDecisionContext.Create(earlier, "a"), default);
        await controller.ChooseCommandAsync(CycleDecisionContext.Create(earlier, "a"), default);
        Assert.Equal(new[] { 0 }, screensAtClear);

        await controller.ChooseCommandAsync(CycleDecisionContext.Create(earlier, "b"), default);
        Assert.Equal(new[] { 0, 2 }, screensAtClear);

        await controller.ChooseCommandAsync(CycleDecisionContext.Create(later, "b"), default);
        Assert.Equal(new[] { 0, 2, 3 }, screensAtClear);

        // Terminal observations use this same boundary even though no further input is requested.
        controller.RenderObservation(CycleDecisionContext.Create(
            CycleGameFactory.CreatePlayable(new ModelTime(18 * CycleTime.Hour)).World, "b").Observation);
        Assert.Equal(new[] { 0, 2, 3, 4 }, screensAtClear);
        Assert.Equal(5, layout.Screens.Count);
    }

    [Fact]
    public async Task Survival_clears_on_first_delivery_and_context_identity_even_without_time_advancing() {
        var layout = new RecordingLayout("test");
        using var input = new StringReader("wait 1\nwait 1\nwait 1\n");
        using var output = new StringWriter();
        var clearedAfterScreens = new List<int>();
        var controller = new PlayConsole.ConsoleCharacterController(new PlayPresentation(layout), input, output,
            () => clearedAfterScreens.Add(layout.Screens.Count)) { StartupText = "startup-help" };
        CycleWorld first = SurvivalContext(15, 1);
        CycleWorld sameContextLaterTime = SurvivalContext(16, 1);
        CycleWorld nextContextSameTime = SurvivalContext(16, 2);

        await controller.ChooseCommandAsync(CycleDecisionContext.Create(first, "a"), default);
        Assert.Equal(new[] { 0 }, clearedAfterScreens);
        Assert.Contains("startup-help", output.ToString());
        Assert.Null(controller.StartupText);
        await controller.ChooseCommandAsync(CycleDecisionContext.Create(sameContextLaterTime, "a"), default);
        Assert.Equal(new[] { 0 }, clearedAfterScreens);
        await controller.ChooseCommandAsync(CycleDecisionContext.Create(nextContextSameTime, "a"), default);
        Assert.Equal(new[] { 0, 2 }, clearedAfterScreens);
        controller.RenderObservation(CycleDecisionContext.Create(nextContextSameTime, "a").Observation);
        Assert.Equal(new[] { 0, 2 }, clearedAfterScreens);
    }

    private static CycleWorld SurvivalContext(int hour, long sequence) {
        CycleWorld world = CycleGameFactory.CreateSurvival(new ModelTime(hour * CycleTime.Hour)).World;
        CycleCharacter actor = world.GetCharacter("a");
        var replacement = new CycleCharacter(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack,
            survival: actor.Survival, continuity: new CycleDecisionContinuity(sequence));
        return CycleEngine.Rebuild(world.Business.Replace(world.Time, changedCharacters: [replacement]), world.Time);
    }

    [Fact]
    public async Task Shared_console_keeps_layout_across_decisions_but_passes_each_actors_own_observation() {
        var first = new RecordingLayout("first");
        var second = new RecordingLayout("second");
        var presentation = new PlayPresentation(first, second);
        CycleWorld world = CycleGameFactory.CreatePlayable().World;
        using var input = new StringReader(":layout second\nwait 1\n:look\nwait 1\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(presentation, input, output);
        CycleDecisionContext a = CycleDecisionContext.Create(world, "a");
        CycleDecisionContext b = CycleDecisionContext.Create(world, "b");

        Assert.Equal(CycleCommand.Wait(1), (await controller.ChooseCommandAsync(a, default)).Command);
        Assert.Equal(CycleCommand.Wait(1), (await controller.ChooseCommandAsync(b, default)).Command);

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
        public List<CycleObservation> Screens { get; } = [];
        public List<CycleObservation> Help { get; } = [];
        public List<CycleObservation> Prompts { get; } = [];
        public string Render(CycleObservation observation) { Screens.Add(observation); return Id; }
        public string RenderHelp(CycleObservation observation) { Help.Add(observation); return Id; }
        public string RenderPrompt(CycleObservation observation) { Prompts.Add(observation); return Id; }
    }
}

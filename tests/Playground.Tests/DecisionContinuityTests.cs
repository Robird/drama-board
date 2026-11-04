using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class DecisionContinuityTests {
    [Fact]
    public async Task Same_time_wait_completion_and_warning_are_both_frozen_before_idle_continue() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var source = Commands(PlaygroundCommand.Handoff("等一小时"), PlaygroundCommand.Wait(1), PlaygroundCommand.Continue(), PlaygroundCommand.Wait(1));
        var history = Memory(initial.World);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        await UntilIncoming(kernel);
        PlaygroundCharacter actor = kernel.World.GetCharacter("a");
        CharacterContextOpening incoming = Context(kernel.World).Opening;
        Assert.Equal(2, Context(kernel.World).ContextSequence);
        Assert.Equal(Hour(9), incoming.DeliveredAt);
        Assert.Contains(incoming.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.WaitCompleted);
        Assert.Contains(incoming.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.SurvivalWarning);
        Assert.Equal("等一小时", incoming.PreviousAdvance!.Handoff);
        Assert.Null(Interaction(kernel.World).DraftHandoff);
        Assert.Null(Interaction(kernel.World).PendingAdvance);
        Assert.Null(actor.Activity);
        Assert.Equal(2, source.Calls);

        await Advance(kernel);
        actor = kernel.World.GetCharacter("a");
        Assert.Equal(PlaygroundEventKind.AttentionAcknowledged, LastFact(kernel).Kind);
        Assert.Same(incoming, Context(kernel.World).Opening);
        Assert.Null(Interaction(kernel.World).PendingAdvance);
        Assert.Equal(2, Context(kernel.World).ContextSequence);
        Assert.Equal(0, actor.Survival!.PendingMask);

        await Advance(kernel);
        Assert.Equal(CharacterContextStatus.Closed, Context(kernel.World).Status);
        Assert.Null(Interaction(kernel.World).PendingAdvance!.Handoff);
        Assert.Equal(2, Interaction(kernel.World).PendingAdvance!.SourceContextSequence);
        Assert.Equal(2, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == PlaygroundEventKind.DecisionContextOpened));
    }

    [Fact]
    public async Task Travelling_handoff_preserves_warning_and_segment_then_continue_closes_same_context() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        string passage = Assert.Single(PlaygroundExpeditionMap.GetEntrances("camp"), p => p.To == "ridge").EntranceId;
        var source = Commands(PlaygroundCommand.Go(passage), PlaygroundCommand.Handoff("抵达后找庇护"), PlaygroundCommand.Continue());
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial.World), characterController: source);
        await UntilIncoming(kernel);
        PlaygroundCharacter before = kernel.World.GetCharacter("a");
        long sequence = Context(kernel.World).ContextSequence;
        var spatial = kernel.World.Expedition!.Spatial;
        Assert.Null(PlaygroundActions.CurrentPlace(kernel.World, "a"));
        Assert.NotEqual(0, before.Survival!.PendingMask);
        Assert.Null(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Handoff("途中可编辑")));
        await Advance(kernel);
        PlaygroundCharacter edited = kernel.World.GetCharacter("a");
        Assert.Same(before.Survival, edited.Survival);
        Assert.Same(before.Activity, edited.Activity);
        Assert.Same(before.Backpack, edited.Backpack);
        Assert.Same(spatial, kernel.World.Expedition!.Spatial);
        Assert.Equal(before.LastResult, edited.LastResult);
        Assert.Equal(sequence, Context(kernel.World).ContextSequence);
        Assert.Equal("抵达后找庇护", Interaction(kernel.World).DraftHandoff);
        AssertForecasts(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts, kernel.World.Forecasts);

        await Advance(kernel);
        PlaygroundCharacter resumed = kernel.World.GetCharacter("a");
        Assert.Same(spatial, kernel.World.Expedition!.Spatial);
        Assert.Equal(0, resumed.Survival!.PendingMask);
        Assert.Equal(CharacterContextStatus.Closed, Context(kernel.World).Status);
        Assert.Null(Interaction(kernel.World).DraftHandoff);
        Assert.Equal(PlaygroundCommandKind.Continue, Interaction(kernel.World).PendingAdvance!.Command.Kind);
        Assert.Equal("call_fixture_3", Interaction(kernel.World).PendingAdvance!.ToolCallId);
        Assert.Equal("抵达后找庇护", Interaction(kernel.World).PendingAdvance!.Handoff);
        Assert.Equal(2, Context(kernel.World).ContextSequence);
    }

    [Fact]
    public async Task Handoff_unicode_limit_and_clear_are_domain_rules() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        string exactlyLimit = string.Concat(Enumerable.Repeat("😀", 2000));
        var source = Commands(PlaygroundCommand.Handoff("A的计划"), PlaygroundCommand.Handoff(null));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: source);
        await StepWorldEvent(kernel);
        Assert.Null(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Handoff(exactlyLimit)));
        Assert.NotNull(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Handoff(exactlyLimit + "字")));
        Assert.NotNull(PlaygroundActions.Check(PlaygroundWorld.CreatePlayable(), "a", PlaygroundCommand.Handoff("disabled")));
        Assert.Equal("A的计划", Interaction(kernel.World).DraftHandoff);
        await Advance(kernel);
        Assert.Null(Interaction(kernel.World).DraftHandoff);
        Assert.Equal(1, Context(kernel.World).ContextSequence);
    }

    private static CharacterInteractionState Interaction(PlaygroundWorld world) => world.Business.InteractionStates["a"];
    private static CharacterContext Context(PlaygroundWorld world) => Interaction(world).CurrentContext!;

    private static PlaygroundFact LastFact(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        Assert.Single(kernel.LastCompletion!.Event.Facts);
    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        AssertForecasts(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts, kernel.World.Forecasts);
    }
    private static async Task UntilIncoming(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        for (int count = 0; count < 50; count++) {
            if (Interaction(kernel.World).CurrentContext?.Opening.PreviousAdvance is not null) { return; }
            await Advance(kernel);
        }
        Assert.Fail("No next context was opened.");
    }
}

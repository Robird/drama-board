using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class DecisionContinuityTests {
    [Fact]
    public async Task Same_time_wait_completion_and_warning_are_both_frozen_before_idle_continue() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var source = Commands(CycleCommand.Handoff("等一小时"), CycleCommand.Wait(1), CycleCommand.Continue(), CycleCommand.Wait(1));
        var history = Memory(initial.World);
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await UntilIncoming(kernel);
        CycleCharacter actor = kernel.World.GetCharacter("a");
        CycleActionOutcome incoming = actor.Continuity!.Incoming!;
        Assert.Equal(2, actor.Continuity.ContextSequence);
        Assert.Equal(Hour(9), incoming.DeliveredAt);
        Assert.True(incoming.Summary.Reasons.HasFlag(CycleWakeReason.ActivityCompleted));
        Assert.True(incoming.Summary.Reasons.HasFlag(CycleWakeReason.SurvivalWarning));
        Assert.Equal("等一小时", incoming.Handoff);
        Assert.Null(actor.Continuity.DraftHandoff);
        Assert.Null(actor.Continuity.Pending);
        Assert.Null(actor.Activity);
        Assert.Equal(2, source.Calls);

        await Advance(kernel);
        actor = kernel.World.GetCharacter("a");
        Assert.Equal(CycleEventKind.AttentionAcknowledged, LastFact(kernel).Kind);
        Assert.Same(incoming, actor.Continuity!.Incoming);
        Assert.Null(actor.Continuity.Pending);
        Assert.Equal(2, actor.Continuity.ContextSequence);
        Assert.Equal(0, actor.Survival!.PendingMask);

        await Advance(kernel);
        Assert.Null(kernel.World.GetCharacter("a").Continuity!.Incoming);
        Assert.Null(kernel.World.GetCharacter("a").Continuity!.Pending!.Handoff);
        Assert.Equal(2, kernel.World.GetCharacter("a").Continuity!.Pending!.Action.SourceContextSequence);
        Assert.Equal(1, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == CycleEventKind.DecisionContextOpened));
    }

    [Fact]
    public async Task Travelling_handoff_preserves_warning_and_segment_then_continue_closes_same_context() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        string passage = Assert.Single(CycleExpeditionMap.GetPassages("camp"), p => p.To == "ridge").Id;
        var source = Commands(CycleCommand.Go(passage), CycleCommand.Handoff("抵达后找庇护"), CycleCommand.Continue());
        var kernel = CycleEngine.CreateKernel(Memory(initial.World), characterController: source);
        await UntilIncoming(kernel);
        CycleCharacter before = kernel.World.GetCharacter("a");
        var spatial = kernel.World.Expedition!.Spatial;
        Assert.Null(CycleActions.CurrentPlace(kernel.World, "a"));
        Assert.NotEqual(0, before.Survival!.PendingMask);
        Assert.Null(CycleActions.Check(kernel.World, "a", CycleCommand.Handoff("途中可编辑")));
        await Advance(kernel);
        CycleCharacter edited = kernel.World.GetCharacter("a");
        Assert.Same(before.Survival, edited.Survival);
        Assert.Same(before.Activity, edited.Activity);
        Assert.Same(before.Backpack, edited.Backpack);
        Assert.Same(spatial, kernel.World.Expedition!.Spatial);
        Assert.Equal(before.LastResult, edited.LastResult);
        Assert.Equal(before.Continuity!.ContextSequence, edited.Continuity!.ContextSequence);
        Assert.Equal("抵达后找庇护", edited.Continuity.DraftHandoff);
        Assert.Equal(CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());

        await Advance(kernel);
        CycleCharacter resumed = kernel.World.GetCharacter("a");
        Assert.Same(spatial, kernel.World.Expedition!.Spatial);
        Assert.Equal(0, resumed.Survival!.PendingMask);
        Assert.Null(resumed.Continuity!.Incoming);
        Assert.Null(resumed.Continuity.DraftHandoff);
        Assert.Equal(CycleAcceptedActionKind.Continue, resumed.Continuity.Pending!.Action.Kind);
        Assert.Equal("call_fixture_3", resumed.Continuity.Pending.Action.ToolCallId);
        Assert.Equal("抵达后找庇护", resumed.Continuity.Pending.Handoff);
        Assert.Equal(2, resumed.Continuity.ContextSequence);
    }

    [Fact]
    public async Task Handoff_unicode_limit_clear_and_actor_isolation_are_domain_rules() {
        var a = new CycleCharacter("a", "Alice", continuity: new(draftHandoff: "A的计划"));
        var b = new CycleCharacter("b", "Bob", continuity: new(draftHandoff: "B的计划"));
        CycleWorld world = CycleWorld.CreatePlayable(Hour(8), [a, b]);
        string exactlyLimit = string.Concat(Enumerable.Repeat("😀", 2000));
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Handoff(exactlyLimit)));
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Handoff(exactlyLimit + "字")));
        Assert.NotNull(CycleActions.Check(CycleWorld.CreatePlayable(), "a", CycleCommand.Handoff("disabled")));
        Assert.Equal("A的计划", world.GetCharacter("a").Continuity!.DraftHandoff);

        var source = Commands(CycleCommand.Handoff(null));
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: source);
        await Advance(kernel);
        Assert.Null(kernel.World.GetCharacter("a").Continuity!.DraftHandoff);
        Assert.Same(b, kernel.World.GetCharacter("b"));
        Assert.Equal("B的计划", kernel.World.GetCharacter("b").Continuity!.DraftHandoff);
        Assert.Equal(1, kernel.World.GetCharacter("a").Continuity!.ContextSequence);
        Assert.DoesNotContain("B的计划", System.Text.Json.JsonSerializer.Serialize(CycleDecisionContext.Create(kernel.World, "a").Observation,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static CycleFact LastFact(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Single(kernel.LastCompletion!.Event.Facts);
    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        Assert.Equal(CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
    }
    private static async Task UntilIncoming(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        for (int count = 0; count < 50; count++) {
            if (kernel.World.GetCharacter("a").Continuity!.Incoming is not null) { return; }
            await Advance(kernel);
        }
        Assert.Fail("No next context was opened.");
    }
}

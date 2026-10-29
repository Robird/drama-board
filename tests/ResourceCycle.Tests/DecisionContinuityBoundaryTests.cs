using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class DecisionContinuityBoundaryTests {
    [Fact]
    public async Task Joining_collector_opens_a_new_context_without_advancing_time_or_service_cursor() {
        CycleWorld initial = CycleWorld.CreatePlayable(Hour(8), [
            new("a", "Alice"), new("b", "Bob")
        ], [Sun(Hour(8))]);
        var source = Commands(CycleCommand.Gather("sunflower"), CycleCommand.Gather("sunflower"));
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await StepWorldEvent(kernel);
        await StepWorldEvent(kernel);
        Assert.Equal("b", kernel.World.Business.LastServedActorId);
        Assert.NotNull(kernel.World.Business.InteractionStates["a"].PendingAdvance);
        await Step(kernel);
        Assert.Equal(CycleEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal("b", kernel.World.Business.LastServedActorId);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(2, source.Calls);
        CharacterContext context = kernel.World.Business.InteractionStates["a"].CurrentContext!;
        Assert.Equal(2, context.ContextSequence);
        Assert.Equal(Hour(8), context.Opening.PreviousAdvance!.AcceptedAt);
        Assert.Equal(Hour(8), context.Opening.DeliveredAt);
        Assert.Contains(context.Opening.Observation.Notices, notice => notice.Notice.Kind == CycleNoticeKind.CollectorJoined);
        Assert.Equal(CycleOutcomeActivityState.Ongoing, context.Opening.ActivityState);
        Assert.Equal("call_fixture_1", context.Opening.PreviousAdvance.ToolCallId);
        Assert.Equal(CharacterContextStatus.Closed, kernel.World.Business.InteractionStates["b"].CurrentContext!.Status);

    }

    [Fact]
    public async Task Basic_scenario_opens_contexts_for_each_actor_and_preserves_existing_service_sequence() {
        CycleWorld initial = CycleWorld.CreatePlayable(Hour(8), [new("a", "Alice"), new("b", "Bob")], [Sun(Hour(8))]);
        var source = Commands(CycleCommand.Gather("sunflower"), CycleCommand.Gather("sunflower"), CycleCommand.Continue());
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        for (int step = 0; step < 3; step++) { await StepWorldEvent(kernel); }
        Assert.Equal(new[] { CycleEventKind.GatherStarted, CycleEventKind.GatherStarted, CycleEventKind.AttentionAcknowledged },
            history.CompletedEvents.SelectMany(e => e.Facts).Where(f => f.Kind != CycleEventKind.DecisionContextOpened).Select(f => f.Kind));
        Assert.Equal(new[] { "a", "b", "a" }, source.Requests.Select(r => r.Actor));
        Assert.All(kernel.World.Business.InteractionStates.Values, state => Assert.NotNull(state.CurrentContext));
        Assert.Equal(3, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == CycleEventKind.DecisionContextOpened));
    }

    [Fact]
    public async Task Exhausted_context_identity_rejects_release_but_allows_idle_warning_acknowledgement() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        CycleCharacter original = initial.World.GetCharacter("a");
        CycleSurvival body = new(initial.World.Time, 25 * CycleSurvival.Scale, 45 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
            CycleSurvivalRules.FoodRate, CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(false, true), warnedMask: 1, pendingMask: 1, pendingSince: initial.World.Time);
        var actor = new CycleCharacter(original.Id, original.Name, backpack: original.Backpack,
            survival: body);
        CycleWorld world = CycleEngine.Rebuild(initial.World.Business.Replace(initial.World.Time, changedCharacters: [actor]), initial.World.Time);
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");
        var opening = new CharacterContextOpening(CycleBackground.For(observation), observation, null, world.Time, CycleOutcomeActivityState.Completed);
        var interaction = new CharacterInteractionState("a", new CharacterContext(long.MaxValue, CharacterContextStatus.Open, opening));
        world = CycleEngine.Rebuild(world.Business.Replace(world.Time, interactionChanges: [interaction]), world.Time);
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Wait(1)));
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Continue()));
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Handoff("仍可编辑")));
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.Continue()));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        Assert.Equal(CycleEventKind.AttentionAcknowledged, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(long.MaxValue, kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
        Assert.Null(kernel.World.Business.InteractionStates["a"].PendingAdvance);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
    }
}

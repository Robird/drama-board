using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class DecisionContinuityBoundaryTests {
    [Fact]
    public async Task Joining_collector_opens_a_new_context_without_advancing_time_or_service_cursor() {
        CycleWorld initial = CycleWorld.CreatePlayable(Hour(8), [
            new("a", "Alice", continuity: new()), new("b", "Bob")
        ], [Sun(Hour(8))]);
        var source = Commands(CycleCommand.Gather("sunflower"), CycleCommand.Gather("sunflower"));
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await Step(kernel);
        await Step(kernel);
        Assert.Equal("b", kernel.World.Business.LastServedActorId);
        Assert.NotNull(kernel.World.GetCharacter("a").Continuity!.Pending);
        await Step(kernel);
        Assert.Equal(CycleEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal("b", kernel.World.Business.LastServedActorId);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(2, source.Calls);
        CycleDecisionContinuity continuity = kernel.World.GetCharacter("a").Continuity!;
        Assert.Equal(2, continuity.ContextSequence);
        Assert.Equal(Hour(8), continuity.Incoming!.Action.AcceptedAt);
        Assert.Equal(Hour(8), continuity.Incoming.DeliveredAt);
        Assert.Equal(CycleWakeReason.CollectorJoined, continuity.Incoming.Summary.Reasons);
        Assert.Equal(CycleOutcomeActivityState.Ongoing, continuity.Incoming.ActivityState);
        Assert.Equal("call_fixture_1", continuity.Incoming.Action.ToolCallId);
        Assert.Null(kernel.World.GetCharacter("b").Continuity);
    }

    [Fact]
    public async Task Disabled_scenario_keeps_existing_service_sequence_without_context_occurrences() {
        CycleWorld initial = CycleWorld.CreatePlayable(Hour(8), [new("a", "Alice"), new("b", "Bob")], [Sun(Hour(8))]);
        var source = Commands(CycleCommand.Gather("sunflower"), CycleCommand.Gather("sunflower"), CycleCommand.Continue());
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        for (int step = 0; step < 3; step++) { await Step(kernel); }
        Assert.Equal(new[] { CycleEventKind.GatherStarted, CycleEventKind.GatherStarted, CycleEventKind.AttentionAcknowledged },
            history.CompletedEvents.SelectMany(e => e.Facts).Select(f => f.Kind));
        Assert.Equal(new[] { "a", "b", "a" }, source.Requests.Select(r => r.Actor));
        Assert.All(kernel.World.Characters, actor => Assert.Null(actor.Continuity));
    }

    [Fact]
    public async Task Exhausted_context_identity_rejects_release_but_allows_idle_warning_acknowledgement() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        CycleCharacter original = initial.World.GetCharacter("a");
        CycleSurvival body = new(initial.World.Time, 25 * CycleSurvival.Scale, 45 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
            warnedMask: 1, pendingMask: 1, pendingSince: initial.World.Time);
        var actor = new CycleCharacter(original.Id, original.Name, backpack: original.Backpack,
            survival: body, continuity: new(long.MaxValue));
        CycleWorld world = CycleEngine.Rebuild(initial.World.Business.Replace(initial.World.Time, changedCharacters: [actor]), initial.World.Time);
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Wait(1)));
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Continue()));
        Assert.Null(CycleActions.Check(world, "a", CycleCommand.Handoff("仍可编辑")));
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.Continue()));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        Assert.Equal(CycleEventKind.AttentionAcknowledged, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(long.MaxValue, kernel.World.GetCharacter("a").Continuity!.ContextSequence);
        Assert.Null(kernel.World.GetCharacter("a").Continuity!.Pending);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
    }
}

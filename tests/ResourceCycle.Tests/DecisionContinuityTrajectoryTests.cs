using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class DecisionContinuityTrajectoryTests {
    [Fact]
    public async Task Resuming_partially_completed_gather_preserves_activity_and_summarizes_each_segment_once() {
        CycleWorld initial = CycleWorld.CreatePlayable(Hour(8), [
            new("a", "Alice", continuity: new()), new("b", "Bob")
        ], [Sun(Hour(8), stock: 10, capacity: 10)]);
        var source = Commands(CycleCommand.Gather("sunflower", 4), CycleCommand.Wait(1),
            CycleCommand.Gather("sunflower"), CycleCommand.Continue(), CycleCommand.Wait(24));
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await Until(kernel, world => world.GetCharacter("a").Continuity!.Incoming is not null);

        CycleCharacter interrupted = kernel.World.GetCharacter("a");
        CycleActionOutcome first = interrupted.Continuity!.Incoming!;
        CycleActivity activity = interrupted.Activity!;
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(1, first.Summary.GatheredCount);
        Assert.Equal(1, first.After.CompletedCount);
        Assert.Equal(4, first.After.TargetCount);
        Assert.Equal(CycleWakeReason.CollectorJoined, first.Summary.Reasons);
        Assert.Equal(CycleOutcomeActivityState.Ongoing, first.ActivityState);
        Assert.Equal("call_fixture_1", first.Action.ToolCallId);
        Assert.Equal(3, source.Calls);

        await Step(kernel);
        CycleCharacter resumed = kernel.World.GetCharacter("a");
        CyclePendingAction pending = resumed.Continuity!.Pending!;
        Assert.Same(activity, resumed.Activity);
        Assert.Equal(Hour(10), resumed.Activity!.Due);
        Assert.Equal(1, pending.Before.CompletedCount);
        Assert.Equal(activity.Id, pending.Action.ActivityId);
        Assert.Equal(CycleAcceptedActionKind.Continue, pending.Action.Kind);
        Assert.Equal("call_fixture_4", pending.Action.ToolCallId);
        Assert.Equal(0, pending.Summary.GatheredCount);
        Assert.Null(resumed.Continuity.Incoming);

        await Until(kernel, world => world.GetCharacter("a").Continuity!.Incoming is not null);
        CycleCharacter finished = kernel.World.GetCharacter("a");
        CycleActionOutcome second = finished.Continuity!.Incoming!;
        Assert.Null(finished.Activity);
        Assert.Equal(3, finished.Continuity.ContextSequence);
        Assert.Equal(Hour(12), second.DeliveredAt);
        Assert.Equal(3, second.Summary.GatheredCount);
        Assert.Equal(4, second.After.CompletedCount);
        Assert.Equal(4, second.After.TargetCount);
        Assert.Equal(activity.Id, second.After.ActivityId);
        Assert.Equal(CycleGatherStopReason.TargetReached, second.Summary.GatherStopReason);
        Assert.Equal(CycleOutcomeActivityState.Completed, second.ActivityState);
        Assert.Equal(CycleWakeReason.ActivityCompleted, second.Summary.Reasons);
        Assert.Equal("call_fixture_4", second.Action.ToolCallId);
        Assert.Equal(4, finished.Backpack.Count("item.sunflower"));
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.sunflower"));
        Assert.Equal(5, source.Calls);
        Assert.Equal(2, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == CycleEventKind.DecisionContextOpened));
        Assert.Equal(1, first.Summary.GatheredCount);
        Assert.Equal(1, first.After.CompletedCount);
    }

    [Fact]
    public async Task Incremental_and_full_survival_trajectories_match_each_fact_context_and_original_operation_id() {
        var initial = CycleGameFactory.CreateSurvival(weatherSeed: 0);
        var incrementalSource = new SurvivalScript();
        var fullSource = new SurvivalScript();
        var incrementalHistory = Memory(initial.World);
        var fullHistory = Memory(initial.World);
        var incremental = CycleEngine.CreateKernel(incrementalHistory, PredictionMode.Incremental, characterController: incrementalSource);
        var full = CycleEngine.CreateKernel(fullHistory, PredictionMode.Full, characterController: fullSource);

        for (int step = 0; incremental.World.Expedition!.Outcome == CycleExpeditionOutcome.Exploring; step++) {
            Assert.True(step < 100);
            int previousCalls = incrementalSource.Calls;
            Assert.Equal(StepStatus.Committed, await incremental.StepAsync(Hour(240)));
            Assert.Equal(StepStatus.Committed, await full.StepAsync(Hour(240)));
            Assert.Equal(Describe(incremental.LastCompletion!.Event), Describe(full.LastCompletion!.Event));
            Assert.Equal(incremental.World.DescribeState(), full.World.DescribeState());
            Assert.Equal(incremental.Cursor, full.Cursor);
            Assert.Equal(incremental.World.GetCharacter("a").Continuity!.ToString(), full.World.GetCharacter("a").Continuity!.ToString());
            Assert.Equal(incrementalSource.Calls, fullSource.Calls);
            Assert.Equal(CycleEngine.Rebuild(incremental.World.Business, incremental.World.Time).Forecasts.ToString(), incremental.World.Forecasts.ToString());
            CycleFact fact = Assert.Single(incremental.LastCompletion.Event.Facts);
            Assert.Equal(previousCalls + (fact.ToolCallId is null ? 0 : 1), incrementalSource.Calls);
            if (fact.ToolCallId is not null) {
                Assert.Equal($"endpoint.original:{incrementalSource.Calls}", fact.ToolCallId);
                Assert.Equal($"trajectory/batch/operation/{incrementalSource.Calls}", fact.OperationRecordId);
            }
        }

        CycleFact[] facts = incrementalHistory.CompletedEvents.SelectMany(e => e.Facts).ToArray();
        Assert.Contains(facts, f => f.Kind == CycleEventKind.WeatherResolved);
        Assert.Contains(facts, f => f.Kind == CycleEventKind.SurvivalWarning);
        Assert.Contains(facts, f => f.Kind == CycleEventKind.LightChanged);
        Assert.Equal(CycleEventKind.SurvivalFailed, facts[^1].Kind);
        Assert.Equal(CycleExpeditionOutcome.Dead, full.World.Expedition!.Outcome);
        Assert.Equal(4, incrementalSource.Calls);
        Assert.Equal(2, facts.Count(f => f.Kind == CycleEventKind.DecisionContextOpened));
        CycleDecisionContinuity final = incremental.World.GetCharacter("a").Continuity!;
        Assert.Null(final.Pending);
        Assert.Equal(3, final.ContextSequence);
        Assert.Equal(CycleOutcomeActivityState.Terminal, final.Incoming!.ActivityState);
        Assert.True(final.Incoming.Summary.Reasons.HasFlag(CycleWakeReason.Terminal));
        Assert.Equal("endpoint.original:4", final.Incoming.Action.ToolCallId);
        Assert.Equal("trajectory/batch/operation/4", final.Incoming.Action.OperationRecordId);
        Assert.Equal(StepStatus.Exhausted, await incremental.StepAsync(Hour(240)));
        Assert.Equal(StepStatus.Exhausted, await full.StepAsync(Hour(240)));
        Assert.Equal(4, incrementalSource.Calls);
        Assert.Equal(4, fullSource.Calls);
    }

    private sealed class SurvivalScript : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Assert.Null(context.Observation.Continuity!.Pending);
            CycleCommand command = ++Calls switch {
                1 => CycleCommand.Handoff("记录本次等待的身体变化"),
                2 => CycleCommand.Wait(24),
                _ => CycleCommand.Continue()
            };
            Assert.Null(context.Check(command));
            return ValueTask.FromResult(new CycleControllerOperation($"endpoint.original:{Calls}", command,
                $"trajectory/batch/operation/{Calls}"));
        }
    }
}

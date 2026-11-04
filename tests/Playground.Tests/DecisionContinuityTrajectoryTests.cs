using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class DecisionContinuityTrajectoryTests {
    [Fact]
    public async Task Resuming_partially_completed_gather_preserves_activity_and_summarizes_each_segment_once() {
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(Hour(8), [
            new("a", "Alice"), new("b", "Bob")
        ], [Sun(Hour(8), stock: 10, capacity: 10)]);
        var source = Commands(PlaygroundCommand.Gather("sunflower", 4), PlaygroundCommand.Wait(1),
            PlaygroundCommand.Gather("sunflower"), PlaygroundCommand.Continue(), PlaygroundCommand.Wait(24));
        var history = Memory(initial);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        await Until(kernel, world => Context(world)?.ContextSequence == 2 && Context(world)?.Status == CharacterContextStatus.Open);

        PlaygroundCharacter interrupted = kernel.World.GetCharacter("a");
        CharacterContextOpening first = Context(kernel.World)!.Opening;
        PlaygroundActivity activity = interrupted.Activity!;
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(1, ActualGain(first));
        Assert.Equal(1, first.Observation.Actor.Activity!.CompletedCount);
        Assert.Equal(4, first.Observation.Actor.Activity!.TargetCount);
        Assert.Contains(first.Observation.Notices, n => n.Notice.Kind == PlaygroundNoticeKind.CollectorJoined);
        Assert.Equal(PlaygroundOutcomeActivityState.Ongoing, first.ActivityState);
        Assert.Equal("call_fixture_1", first.PreviousAdvance!.ToolCallId);
        Assert.Equal(3, source.Calls);

        await Step(kernel);
        PlaygroundCharacter resumed = kernel.World.GetCharacter("a");
        PendingAdvance pending = kernel.World.Business.InteractionStates["a"].PendingAdvance!;
        Assert.Same(activity, resumed.Activity);
        Assert.Equal(Hour(10), resumed.Activity!.Due);
        Assert.Equal(1, resumed.Activity.CompletedCount);
        Assert.Equal(activity.Id, pending.ActivityId);
        Assert.Equal(PlaygroundCommandKind.Continue, pending.Command.Kind);
        Assert.Equal("call_fixture_4", pending.ToolCallId);
        Assert.DoesNotContain(kernel.World.Business.InteractionStates["a"].UndeliveredExperiences, e => e.Event.Kind == PlaygroundObservedEventKind.GatherYielded);
        Assert.Equal(CharacterContextStatus.Closed, Context(kernel.World)!.Status);

        await Until(kernel, world => Context(world)?.ContextSequence == 3 && Context(world)?.Status == CharacterContextStatus.Open);
        PlaygroundCharacter finished = kernel.World.GetCharacter("a");
        CharacterContextOpening second = Context(kernel.World)!.Opening;
        Assert.Null(finished.Activity);
        Assert.Equal(3, Context(kernel.World)!.ContextSequence);
        Assert.Equal(Hour(12), second.DeliveredAt);
        Assert.Equal(3, ActualGain(second));
        Assert.Equal(activity.Id, second.PreviousAdvance!.ActivityId);
        Assert.Equal(PlaygroundGatherStopReason.TargetReached.ToString(), Assert.Single(second.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.GatherFinished).Event.Detail);
        Assert.Equal(PlaygroundOutcomeActivityState.Completed, second.ActivityState);
        Assert.Equal("call_fixture_4", second.PreviousAdvance!.ToolCallId);
        Assert.Equal(4, finished.Backpack.Count("item.sunflower"));
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.sunflower"));
        Assert.Equal(5, source.Calls);
        Assert.Equal(3, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == PlaygroundEventKind.DecisionContextOpened && f.ActorId == "a"));
        Assert.Equal(1, ActualGain(first));
        Assert.Equal(1, first.Observation.Actor.Activity!.CompletedCount);
    }

    [Fact]
    public async Task Incremental_and_full_survival_trajectories_match_each_fact_context_and_original_operation_id() {
        var initial = PlaygroundGameFactory.CreateSurvival(weatherSeed: 0);
        var incrementalSource = new SurvivalScript();
        var fullSource = new SurvivalScript();
        var incrementalHistory = Memory(initial.World);
        var fullHistory = Memory(initial.World);
        var incremental = PlaygroundEngine.CreateKernel(incrementalHistory, PredictionMode.Incremental, characterController: incrementalSource);
        var full = PlaygroundEngine.CreateKernel(fullHistory, PredictionMode.Full, characterController: fullSource);

        for (int step = 0; incremental.World.GetCharacter("a").IsActive; step++) {
            Assert.True(step < 100);
            int previousCalls = incrementalSource.Calls;
            Assert.Equal(StepStatus.Committed, await incremental.StepAsync(Hour(240)));
            Assert.Equal(StepStatus.Committed, await full.StepAsync(Hour(240)));
            Assert.Equal(Describe(incremental.LastCompletion!.Event), Describe(full.LastCompletion!.Event));
            Assert.Equal(incremental.World.DescribeState(), full.World.DescribeState());
            Assert.Equal(incremental.Cursor, full.Cursor);
            Assert.Equal(MaterialSnapshot(incremental.World.Business.InteractionStates["a"]), MaterialSnapshot(full.World.Business.InteractionStates["a"]));
            Assert.Equal(incrementalSource.Calls, fullSource.Calls);
            AssertForecasts(PlaygroundEngine.Rebuild(incremental.World.Business, incremental.World.Time).Forecasts, incremental.World.Forecasts);
            PlaygroundFact fact = Assert.Single(incremental.LastCompletion.Event.Facts);
            Assert.Equal(previousCalls + (fact.ToolCallId is null ? 0 : 1), incrementalSource.Calls);
            if (fact.ToolCallId is not null) {
                Assert.Equal($"endpoint.original:{incrementalSource.Calls}", fact.ToolCallId);
                Assert.Equal($"trajectory/batch/operation/{incrementalSource.Calls}", fact.OperationRecordId);
            }
        }

        PlaygroundFact[] facts = incrementalHistory.CompletedEvents.SelectMany(e => e.Facts).ToArray();
        Assert.Contains(facts, f => f.Kind == PlaygroundEventKind.WeatherResolved);
        Assert.Contains(facts, f => f.Kind == PlaygroundEventKind.SurvivalWarning);
        Assert.Contains(facts, f => f.Kind == PlaygroundEventKind.LightChanged);
        Assert.Equal(PlaygroundEventKind.SurvivalFailed, facts[^1].Kind);
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, full.World.GetCharacter("a").Ending!.Outcome);
        Assert.Equal(4, incrementalSource.Calls);
        Assert.Equal(3, facts.Count(f => f.Kind == PlaygroundEventKind.DecisionContextOpened));
        CharacterInteractionState final = incremental.World.Business.InteractionStates["a"];
        Assert.Null(final.PendingAdvance);
        Assert.Equal(4, final.CurrentContext!.ContextSequence);
        Assert.Equal(CharacterContextStatus.Terminal, final.CurrentContext.Status);
        Assert.Equal(PlaygroundOutcomeActivityState.Terminal, final.CurrentContext.Opening.ActivityState);
        Assert.Contains(final.CurrentContext.Opening.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.SurvivalFailed);
        Assert.Equal("endpoint.original:4", final.CurrentContext.Opening.PreviousAdvance!.ToolCallId);
        Assert.Equal("trajectory/batch/operation/4", final.CurrentContext.Opening.PreviousAdvance.OperationRecordId);
        Assert.Equal(StepStatus.Exhausted, await incremental.StepAsync(Hour(240)));
        Assert.Equal(StepStatus.Exhausted, await full.StepAsync(Hour(240)));
        Assert.Equal(4, incrementalSource.Calls);
        Assert.Equal(4, fullSource.Calls);
    }

    private static CharacterContext? Context(PlaygroundWorld world) => world.Business.InteractionStates["a"].CurrentContext;
    private static decimal ActualGain(CharacterContextOpening opening) => opening.Experiences
        .Where(e => e.Event.Kind == PlaygroundObservedEventKind.GatherYielded)
        .SelectMany(e => e.Event.Effects).Where(effect => effect.Key == new PlaygroundStateKey("inventory", "item.sunflower", "count"))
        .Sum(effect => effect.After.Number - effect.Before.Number);

    private sealed class SurvivalScript : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Assert.Equal(CharacterContextStatus.Open, context.Context!.Status);
            PlaygroundCommand command = ++Calls switch {
                1 => PlaygroundCommand.Handoff("记录本次等待的身体变化"),
                2 => PlaygroundCommand.Wait(24),
                _ => PlaygroundCommand.Continue()
            };
            Assert.Null(context.Check(command));
            return ValueTask.FromResult(new PlaygroundControllerOperation($"endpoint.original:{Calls}", PlaygroundCommandParser.Format(command),
                $"trajectory/batch/operation/{Calls}"));
        }
    }
}

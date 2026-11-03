using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class ActiveNavigationMaterialTests {
    private static readonly string[] Explore = ["go e-c7", "go e-h5", "go e-q8", "go e-n4"];

    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public async Task Actual_multisegment_navigation_keeps_its_route_when_an_intermediate_query_replaces_the_candidate(string layoutId) {
        var kernel = Create([.. Explore, "path ravine", "navigate", "path camp", "map", "continue"]);
        await Until(kernel, world => PlaygroundDecisionContext.Observe(world, "a").Navigation is not null);
        PlaygroundObservation departed = PlaygroundDecisionContext.Observe(kernel.World, "a");
        string activeId = departed.Navigation!.Path.Id;
        Assert.NotNull(departed.ArrivalDue);
        Assert.Contains("本次 continue 只确认", NavigationView.RenderActive(departed));
        Assert.Contains("本段仍按原期限到达", NavigationView.RenderActive(departed));
        IPlayLayout layout = Layout(layoutId);
        Assert.DoesNotContain("trail-", layout.Render(departed));

        await Until(kernel, world => HasItem(world, "path camp"));
        CharacterInteractionState state = kernel.World.Business.InteractionStates["a"];
        PlaygroundObservation intermediate = state.CurrentContext!.LatestObservation;
        Assert.Equal("stream", intermediate.PlaceId);
        Assert.Equal(activeId, intermediate.Navigation!.Path.Id);
        Assert.Equal("ravine", intermediate.Navigation.Path.TargetPlaceId);
        Assert.Equal(1, intermediate.Navigation.CompletedSteps);
        Assert.Equal("camp", intermediate.CandidatePath!.TargetPlaceId);
        Assert.NotEqual(activeId, intermediate.CandidatePath.Id);
        string active = NavigationView.RenderActive(intermediate);
        Assert.Contains("已完成 1/2 段", active);
        Assert.Contains("等待确认的下一段", active);
        Assert.Contains("e-h5", active);
        Assert.Contains("狭谷旧桥", active);
        Assert.Contains("本次 continue 确认待响应提醒并尝试", active);
        Assert.NotNull(intermediate.Communication);
        var communication = new System.Text.StringBuilder();
        CommunicationView.Render(communication, intermediate);
        Assert.Contains("本次 continue 确认待响应提醒并尝试", communication.ToString());
        Assert.DoesNotContain("保留原活动及截止时刻", communication.ToString());
        string full = CharacterContextRenderer.RenderFull(state, layout);
        string latest = CharacterContextRenderer.RenderLatestState(state, layout);
        Assert.Contains(activeId, latest);
        Assert.Contains(intermediate.CandidatePath.Id, latest);
        Assert.DoesNotContain("继续不会新建活动", full);
        Assert.DoesNotContain("trail-", full);
        var delta = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(departed), PlaygroundViewSnapshot.Create(intermediate), [], PlayDeliveryPhase.ControllerInput);
        Assert.Contains("等待确认的下一段", layout.RenderUpdate(delta));
        Assert.Contains("e-h5", layout.RenderUpdate(delta));

        await Until(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "ravine" &&
            world.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundObservation arrived = kernel.World.Business.InteractionStates["a"].CurrentContext!.LatestObservation;
        Assert.Null(arrived.Navigation);
        Assert.Equal(intermediate.CandidatePath.Id, arrived.CandidatePath!.Id);
        Assert.Contains("当前无活动导航", layout.RenderUpdate(PlaygroundViewUpdate.Create(
            PlaygroundViewSnapshot.Create(intermediate), PlaygroundViewSnapshot.Create(arrived), [], PlayDeliveryPhase.ControllerInput)));
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public async Task Cancelling_at_an_actual_intermediate_stop_removes_only_navigation_and_renders_a_clear_receipt(string layoutId) {
        var kernel = Create([.. Explore, "path ravine", "navigate", "cancel"]);
        await Until(kernel, world => PlaygroundDecisionContext.Observe(world, "a") is { Navigation.CompletedSteps: 1, ArrivalDue: null } &&
            world.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundObservation before = kernel.World.Business.InteractionStates["a"].CurrentContext!.LatestObservation;
        await Until(kernel, world => HasItem(world, "cancel"));
        var state = kernel.World.Business.InteractionStates["a"];
        PlaygroundObservation after = state.CurrentContext!.LatestObservation;
        Assert.Null(after.Navigation);
        Assert.Equal("stream", after.PlaceId);
        Assert.Null(after.ArrivalDue);
        Assert.Equal(before.CandidatePath!.Id, after.CandidatePath!.Id);
        IPlayLayout layout = Layout(layoutId);
        Assert.Contains("当前无活动导航", layout.RenderUpdate(PlaygroundViewUpdate.Create(
            PlaygroundViewSnapshot.Create(before), PlaygroundViewSnapshot.Create(after), [], PlayDeliveryPhase.ControllerInput)));
        Assert.Contains("当前无活动导航", CharacterContextRenderer.RenderLatestState(state, layout));
        string history = ContextHistoryPlayView.RenderHistory(after, state);
        Assert.Contains("交付时导航尚未结束", history);
        Assert.Contains("后续变化见当前活动导航", history);
        Assert.DoesNotContain("本次 continue", history); // A historical stop must not instruct a cancelled route to depart.
        Assert.Contains(state.CurrentContext.Items, item => item.RawInput == "cancel" && !item.IsRejected);
    }

    [Fact]
    public async Task Starting_a_local_zero_segment_candidate_keeps_the_context_open_without_releasing_time() {
        var kernel = Create(["path camp", "navigate"]);
        await Until(kernel, world => HasItem(world, "path camp"));
        PlaygroundObservation before = kernel.World.Business.InteractionStates["a"].CurrentContext!.LatestObservation;
        long sequence = kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence;
        await Until(kernel, world => world.Business.InteractionStates["a"].CurrentContext!.Items.Any(item => item.RawInput?.StartsWith("navigate ", StringComparison.Ordinal) == true));
        var state = kernel.World.Business.InteractionStates["a"];
        Assert.Equal(sequence, state.CurrentContext!.ContextSequence);
        Assert.Equal(CharacterContextStatus.Open, state.CurrentContext.Status);
        Assert.Equal(before.Time, state.CurrentContext.LatestObservation.Time);
        Assert.Null(state.CurrentContext.LatestObservation.Navigation);
        Assert.Null(state.CurrentContext.LatestObservation.ArrivalDue);
        Assert.DoesNotContain(state.CurrentContext.Items, item => item.IsRejected);
    }

    private static SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> Create(string[] commands) {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
        return PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(commands));
    }
    private static IPlayLayout Layout(string id) => id == "classic" ? new ClassicPlayLayout() : new DecisionPlayLayout();
    private static bool HasItem(PlaygroundWorld world, string raw) => world.Business.InteractionStates["a"].CurrentContext?.Items.Any(item => item.RawInput == raw) == true;
    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> done) {
        for (int i = 0; i < 120; i++) {
            if (done(kernel.World)) { return; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        Assert.Fail("Navigation material boundary was not reached.");
    }
    private sealed class Commands(string[] commands) : IPlaygroundCharacterController {
        private readonly Queue<string> _commands = new(commands);
        private int _sequence;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string raw = _commands.Dequeue();
            if (raw == "navigate") { raw += " " + context.Observation.CandidatePath!.Id; }
            string id = "active-material-" + ++_sequence;
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, raw, id));
        }
    }
}

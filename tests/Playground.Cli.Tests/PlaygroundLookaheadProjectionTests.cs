using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadProjectionTests {
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(20);
    private static IReadOnlyDictionary<string, CharacterControlBinding> Bindings(bool human = false) =>
        new Dictionary<string, CharacterControlBinding> { ["a"] = human ? new CharacterControlBinding.HumanConsole() : new CharacterControlBinding.Llm("test"), ["b"] = new CharacterControlBinding.Llm("test") };

    [Fact]
    public async Task Independent_projection_advances_real_weather_and_preserves_source_and_random_address() {
        var history = await Source("cabin", "camp", weather: true);
        var source = history.State;
        var cursor = history.Cursor;
        string state = source.DescribeState();
        var environment = source.Expedition!.Environment!;
        int eventCount = history.CompletedEvents.Count;
        var result = await PlaygroundLookaheadProjection.ProjectAsync(source, cursor, "b", Bindings(), default, preparationLimit: TestLimit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, result.StopReason);
        Assert.Equal("a", result.Target!.InteractionState.ActorId);
        Assert.Equal(Hour(11), result.Target.Observation.Time);
        Assert.NotNull(result.Target.Observation.Weather!.LastKind);
        Assert.Same(source, history.State);
        Assert.Equal(cursor, history.Cursor);
        Assert.Equal(state, source.DescribeState());
        Assert.Equal(eventCount, history.CompletedEvents.Count);
        Assert.Same(environment, source.Expedition.Environment);
        Assert.Equal(0, environment.Sequence);
        Assert.Null(environment.LastAt);
        Assert.True(result.Steps > 1);
    }

    [Theory]
    [InlineData(true, false, "SamePlace")]
    [InlineData(false, true, "Human")]
    public async Task Stops_at_first_ineligible_controller(bool samePlace, bool human, string expected) {
        var history = await Source("camp", samePlace ? "camp" : "cabin");
        var result = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "b", Bindings(human), default, preparationLimit: TestLimit);
        Assert.Equal(expected, result.StopReason.ToString());
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Repeated_source_actor_stops_without_a_second_prediction() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(10), enableNeeds: false, enableWeather: false, characterCount: 1);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(10)));
        var result = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings(), default, preparationLimit: TestLimit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.RepeatedActor, result.StopReason);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Explicit_bounds_stop_without_modifying_source() {
        var history = await Source("camp", "cabin");
        var source = history.State;
        var step = await PlaygroundLookaheadProjection.ProjectAsync(source, history.Cursor, "b", Bindings(), default, maximumSteps: 0, preparationLimit: TestLimit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.StepLimit, step.StopReason);
        Assert.Equal(0, step.Steps);
        var time = await PlaygroundLookaheadProjection.ProjectAsync(source, history.Cursor, "b", Bindings(), default, maximumModelTicks: 0, preparationLimit: TestLimit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.ModelTimeLimit, time.StopReason);
        var wall = await PlaygroundLookaheadProjection.ProjectAsync(source, history.Cursor, "b", Bindings(), default, preparationLimit: TimeSpan.Zero);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.PreparationLimit, wall.StopReason);
        Assert.Same(source, history.State);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved() {
        var history = await Source("camp", "cabin");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "b", Bindings(), cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Missing_target_binding_and_wrong_source_actor_fail_closed() {
        var history = await Source("camp", "cabin");
        var missing = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "b",
            new Dictionary<string, CharacterControlBinding>(), default, preparationLimit: TestLimit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.UnknownControl, missing.StopReason);
        Assert.Null(missing.Target);
        var wrong = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings(), default, preparationLimit: TestLimit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.InvalidSource, wrong.StopReason);
    }
    [Fact]
    public async Task Illegal_default_wait_is_abandoned_without_a_transition() {
        var history = await Source("camp", "cabin", exhaustedActivityIds: true);
        var decision = PlaygroundDecisionContext.Create(history.State, "b");
        Assert.NotNull(decision.Check(PlaygroundCommand.Wait(1)));
        var result = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "b", Bindings(), default, preparationLimit: TestLimit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.CannotPredict, result.StopReason);
        Assert.Equal(0, result.Steps);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Legal_continue_acknowledges_attention_and_preserves_the_existing_activity() {
        var history = await Source("camp", "cabin", activeAttention: true);
        var activity = history.State.GetCharacter("b").Activity;
        Assert.Null(PlaygroundDecisionContext.Create(history.State, "b").Check(PlaygroundCommand.Continue()));
        var result = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "b", Bindings(), default, preparationLimit: TestLimit);
        Assert.Equal("continue", result.PredictedCommand);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, result.StopReason);
        Assert.Equal("a", result.Target!.InteractionState.ActorId);
        Assert.Equal(Hour(11), result.Target.Observation.Time);
        Assert.Same(activity, history.State.GetCharacter("b").Activity);
        Assert.True(history.State.GetCharacter("b").Communication.AttentionPending);
    }
    private static ModelTime Hour(int hour) => new(hour * PlaygroundTime.Hour);

    // A is already waiting until 11; B's committed opening at 10 is the source boundary.
    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Source(string aPlace, string bPlace, bool weather = false, bool activeAttention = false, bool exhaustedActivityIds = false) {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(10), enableNeeds: false, enableWeather: false, characterCount: 2);
        PlaygroundSurvival? Body(string place) => weather ? PlaygroundSurvival.Create(Hour(10), food: 100, water: 100, warmth: 60,
            warmthRate: PlaygroundSurvivalRules.WarmthRate(PlaygroundExpeditionMap.GetPlace(place).Sheltered, true)) : null;
        var actors = new[] { new PlaygroundCharacter("a", "A", survival: Body(aPlace)), new PlaygroundCharacter("b", "B",
            activity: activeAttention ? new PlaygroundActivity(PlaygroundActivityKind.Wait, 1, Hour(10), 2 * PlaygroundTime.Hour, Hour(12)) : null,
            nextActivityId: exhaustedActivityIds ? long.MaxValue : activeAttention ? 2 : 1,
            communication: activeAttention ? new PlaygroundCommunicationState(Hour(10), 0, true) : null, survival: Body(bPlace)) };
        var places = new[] { aPlace, bPlace };
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph,
            actors.Select((actor, i) => new EntityPlacement(new(actor.Id), new(places[i]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((actor, i) => new PlaygroundVisit(actor.Id, places[i], Hour(10),
                initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[i]))),
            environment: weather ? new PlaygroundEnvironment(0, 0, Hour(11)) : null);
        var business = new PlaygroundBusinessState(Hour(10), initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, Hour(10)), initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: new WaitController());
        for (int i = 0; i < 10; i++) {
            if (PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == "b" && history.State.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Open) { return history; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(10)));
        }
        throw new InvalidOperationException("B opening not reached.");
    }
    private sealed class WaitController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("setup", "wait 1", "setup"));
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadTemporalTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture")
    };

    [Fact]
    public async Task Scratch_continue_advances_real_arrival_without_changing_source_spatial_time_or_weather_random_address() {
        var time = Hour(10);
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
        PlaygroundSurvival Body(string place) => PlaygroundSurvival.Create(time, food: 100, water: 100, warmth: 60,
            warmthRate: PlaygroundSurvivalRules.WarmthRate(PlaygroundExpeditionMap.GetPlace(place).Sheltered, true));
        var actors = new[] {
            new PlaygroundCharacter("a", "A", survival: Body("camp")),
            new PlaygroundCharacter("b", "B", survival: Body("cabin"),
                activity: new PlaygroundActivity(PlaygroundActivityKind.Wait, 1, time, 3 * PlaygroundTime.Hour, Hour(13)),
                nextActivityId: 2, communication: new PlaygroundCommunicationState(time, 0, true))
        };
        string[] places = ["camp", "cabin"];
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph,
            actors.Select((actor, i) => new EntityPlacement(new(actor.Id), new(places[i]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((actor, i) => new PlaygroundVisit(actor.Id, places[i], time,
                initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[i]))),
            environment: new PlaygroundEnvironment(123, 0, Hour(20)));
        var business = new PlaygroundBusinessState(time, initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, time), initial.Cursor);
        var setup = PlaygroundEngine.CreateKernel(history, characterController: new Script(_ => Operation("go e-c7")));
        await Until(setup, history, () => IsOpening(history, "b"));
        var source = history.State;
        var cursor = history.Cursor;
        var sourceSpatial = source.Expedition!.Spatial;
        var environment = source.Expedition.Environment!;
        var waiting = source.GetCharacter("b").Activity;
        var travel = Assert.IsType<TraversingLocation>(PlaygroundActions.Location(source, "a"));
        Assert.Equal(Hour(11), travel.ArrivalDue);
        Assert.Equal("stream", travel.TargetPlaceId.Value);
        Assert.True(source.GetCharacter("b").Communication.AttentionPending);
        Assert.Null(PlaygroundDecisionContext.Create(source, "b").Check(PlaygroundCommand.Continue()));
        int events = history.CompletedEvents.Count;
        string description = source.DescribeState();

        var result = await PlaygroundLookaheadProjection.ProjectAsync(source, cursor, "b", Bindings, default, preparationLimit: Limit);

        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, result.StopReason);
        Assert.Equal("continue", result.PredictedCommand);
        Assert.Equal("a", result.Target!.InteractionState.ActorId);
        Assert.Equal(Hour(11), result.Target.Observation.Time);
        Assert.Equal("stream", result.Target.Observation.PlaceId);
        Assert.Null(result.Target.Observation.Journey);
        Assert.True(result.Steps > 1);
        Assert.Same(source, history.State);
        Assert.Equal(Hour(10), history.State.Time);
        Assert.Equal(cursor, history.Cursor);
        Assert.Equal(events, history.CompletedEvents.Count);
        Assert.Equal(description, history.State.DescribeState());
        Assert.Same(sourceSpatial, history.State.Expedition!.Spatial);
        Assert.Same(travel, PlaygroundActions.Location(history.State, "a"));
        Assert.Same(waiting, history.State.GetCharacter("b").Activity);
        Assert.True(history.State.GetCharacter("b").Communication.AttentionPending);
        Assert.Same(environment, history.State.Expedition.Environment);
        Assert.Equal(123UL, environment.Seed);
        Assert.Equal(0, environment.Sequence);
        Assert.Equal(Hour(20), environment.NextDue);
        Assert.Null(environment.LastAt);
    }

    [Fact]
    public async Task Sound_wakes_same_target_before_predicted_sunset_and_invalidates_time_and_shared_light_material() {
        var history = await Source(17, "camp", "stream");
        var setup = PlaygroundEngine.CreateKernel(history, characterController: new Script(_ => Operation("wait 1")));
        await Until(setup, history, () => IsOpening(history, "b"));
        var projected = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "b", Bindings, default, preparationLimit: Limit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, projected.StopReason);
        Assert.Equal("a", projected.Target!.InteractionState.ActorId);
        Assert.Equal(Hour(18), projected.Target.Observation.Time);
        Assert.False(projected.Target.Observation.IsDay);
        var evidence = new Evidence();
        var client = new InputClient("a", "shout earlier-than-sunset");
        await using var coordinator = Coordinator(history, client, evidence);
        var actualContexts = new List<PlaygroundDecisionContext>();
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: new DelegateController(async (context, token) => {
            actualContexts.Add(context);
            return await coordinator.ChooseCommandAsync(context, token);
        }));
        await Until(kernel, history, () => Operations(history).Any(f => f.RawInput == "handoff fresh-target"));
        PlaygroundDecisionContext actual = Assert.Single(actualContexts, context => context.InteractionState.ActorId == "a");
        Assert.Equal(projected.Target.Context.ContextSequence, actual.Context.ContextSequence);
        Assert.Equal(projected.Target.Context.LastOperationSequence, actual.Context.LastOperationSequence);
        Assert.Equal(Hour(17), actual.Observation.Time);
        Assert.True(actual.Observation.IsDay);
        Assert.Equal(Hour(17), history.State.Time);
        Assert.NotEqual(client.TargetInputs[0], client.TargetInputs[1]);
        Assert.Single(evidence.Kind("lookahead-candidate-discarded"));
        Assert.Empty(evidence.Kind("lookahead-candidate-adopted"));
        Assert.Equal(Hour(18).Ticks, evidence.Kind("lookahead-candidate-created").Single().GetProperty("Target").GetProperty("TimeTicks").GetInt64());
        Assert.Equal("handoff fresh-target", Operations(history).Last().RawInput);
        Assert.DoesNotContain(Operations(history), f => f.RawInput == "handoff stale-target");
    }

    [Theory]
    [InlineData("a", 11)]
    [InlineData("b", 12)]
    public async Task Retained_candidate_is_rejected_after_real_movement_when_target_is_replaced_or_reopens(string serviceActor, int hour) {
        var history = await Source(10, "camp", "cabin");
        var projected = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, projected.StopReason);
        Assert.Equal("b", projected.Target!.InteractionState.ActorId);
        Assert.Equal(Hour(10), projected.Target.Observation.Time);
        var evidence = new Evidence();
        var client = new InputClient("b", "wait 1");
        await using var coordinator = Coordinator(history, client, evidence);
        PlaygroundControllerOperation first = await coordinator.ChooseCommandAsync(PlaygroundDecisionContext.Create(history.State, "a"), default);
        Assert.Single(evidence.Kind("lookahead-candidate-created"));

        // Controlled stale-candidate pressure: intervening decisions use a real Kernel and a
        // scripted controller. The production coordinator retains its original B@10 candidate.
        // Ordinary Host operation examines it sooner; this explicitly tests delayed reuse safety.
        bool firstA = true;
        var prelude = PlaygroundEngine.CreateKernel(history, characterController: new Script(context => {
            if (context.InteractionState.ActorId == "b") { return Operation("go e-t4"); }
            if (firstA) { firstA = false; return first; }
            return Operation("wait 3");
        }));
        await Until(prelude, history, () => history.State.Time == Hour(hour) && IsOpening(history, serviceActor));
        if (serviceActor == "a") {
            Assert.IsType<TraversingLocation>(PlaygroundActions.Location(history.State, "b"));
        } else {
            Assert.Equal("ridge", PlaygroundActions.CurrentPlace(history.State, "b"));
            Assert.True(PlaygroundDecisionContext.Create(history.State, "b").Context.ContextSequence > projected.Target.Context.ContextSequence);
            Assert.Contains(history.CompletedEvents.SelectMany(e => e.Facts), fact => fact.Kind == PlaygroundEventKind.MoveArrived && fact.ActorId == "b");
        }
        PlaygroundDecisionContext actual = PlaygroundDecisionContext.Create(history.State, serviceActor);
        PlaygroundControllerOperation fresh = await coordinator.ChooseCommandAsync(actual, default);
        var commit = PlaygroundEngine.CreateKernel(history, characterController: new Script(_ => fresh));
        int before = Operations(history).Count;
        await Until(commit, history, () => Operations(history).Count > before);
        Assert.Single(evidence.Kind("lookahead-candidate-discarded"));
        Assert.Empty(evidence.Kind("lookahead-candidate-adopted"));
        Assert.Equal(serviceActor, Operations(history).Last().ActorId);
        Assert.Equal(PlaygroundOperationDisposition.Succeeded, Operations(history).Last().OperationDisposition);
        Assert.DoesNotContain(Operations(history), f => f.RawInput == "handoff stale-target");
        if (serviceActor == "b") {
            Assert.Equal("handoff fresh-target", fresh.RawInput);
            Assert.NotEqual(client.TargetInputs[0], client.TargetInputs[1]);
        }
    }

    private static PlaygroundLookaheadController Coordinator(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, ICompletionClient client, Evidence evidence) =>
        new(() => (history.State, history.Cursor), Bindings,
            (_, _) => ValueTask.FromResult(new LlmDecisionBinding(client, "fixture-model", "fixture")),
            new LlmControllerOptions { MaxCalls = 3 }, _ => { }, evidence: evidence.Record);
    private static ModelTime Hour(int hour) => new(hour * PlaygroundTime.Hour);
    private static bool IsOpening(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, string actor) =>
        PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == actor
        && history.State.Business.InteractionStates[actor].CurrentContext?.Status == CharacterContextStatus.Open;
    private static List<PlaygroundFact> Operations(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history) =>
        history.CompletedEvents.SelectMany(e => e.Facts).Where(f => f.OperationRecordId is not null).ToList();
    private static PlaygroundControllerOperation Operation(string raw) => new("temporal-call", raw, Guid.NewGuid().ToString("N"));
    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel,
        InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, Func<bool> reached) {
        using var stop = new CancellationTokenSource(Limit);
        for (int i = 0; i < 100 && !reached(); i++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(20), stop.Token));
        }
        Assert.True(reached(), $"Boundary was not reached at {history.State.Time}.");
    }
    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Source(int hour, string aPlace, string bPlace) {
        var time = Hour(hour);
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
        var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
        string[] places = [aPlace, bPlace];
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph,
            actors.Select((actor, i) => new EntityPlacement(new(actor.Id), new(places[i]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((actor, i) => new PlaygroundVisit(actor.Id, places[i], time,
                initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[i]))));
        var business = new PlaygroundBusinessState(time, initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, time), initial.Cursor);
        var setup = PlaygroundEngine.CreateKernel(history);
        await Until(setup, history, () => IsOpening(history, "a"));
        return history;
    }

    private sealed class InputClient(string targetActor, string otherCommand) : ICompletionClient {
        public string Name => "lookahead-temporal-fixture";
        public string ApiSpecId => "fixture";
        public List<string> TargetInputs { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            string material = Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
            string command = otherCommand;
            if (material.Contains($"· \"{targetActor}\" ·", StringComparison.Ordinal)) {
                TargetInputs.Add(material);
                command = material == TargetInputs[0] ? "handoff stale-target" : "handoff fresh-target";
            }
            return Task.FromResult(new CompletionResult(new ActionMessage([
                new ActionBlock.ToolCall(new("act", "temporal-model-call", JsonSerializer.Serialize(new { command })))
            ]), new("fixture", "fixture", request.ModelId)));
        }
    }
    private sealed class Evidence {
        private readonly ConcurrentQueue<(string Kind, JsonElement Data)> _events = new();
        public void Record(string kind, object payload) => _events.Enqueue((kind, JsonSerializer.SerializeToElement(payload).GetProperty("Detail")));
        public List<JsonElement> Kind(string kind) => _events.Where(e => e.Kind == kind).Select(e => e.Data).ToList();
    }
    private sealed class Script(Func<PlaygroundDecisionContext, PlaygroundControllerOperation> choose) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) => ValueTask.FromResult(choose(context));
    }
    private sealed class DelegateController(Func<PlaygroundDecisionContext, CancellationToken, ValueTask<PlaygroundControllerOperation>> choose) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) => choose(context, cancellationToken);
    }
}

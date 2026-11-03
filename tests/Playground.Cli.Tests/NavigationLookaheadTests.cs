using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground.Completion;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Production scratch projection, request comparison and actual command publication; offline model fixtures only.</summary>
public sealed class NavigationLookaheadTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture")
    };
    private static ModelTime Hour(int value) => new(value * PlaygroundTime.Hour);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scratch_arrival_discovers_or_advances_navigation_without_installing_anything_in_the_source(bool navigation) {
        var history = await Departure(navigation);
        PlaygroundWorld source = history.State;
        var cursor = history.Cursor;
        int events = history.CompletedEvents.Count;
        string original = source.DescribeState();
        var result = await PlaygroundLookaheadProjection.ProjectAsync(source, cursor, "b", Bindings, default, preparationLimit: Limit);
        Assert.Equal("wait 1", result.PredictedCommand);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, result.StopReason);
        Assert.Equal("a", result.Target!.Observation.Actor.Id);
        Assert.Equal("stream", result.Target.Observation.PlaceId);
        if (navigation) {
            Assert.Equal(1, result.Target.Observation.Navigation!.CompletedSteps);
            Assert.Equal(0, source.Expedition!.Navigation("a")!.CompletedSteps);
        } else {
            Assert.Contains(result.Target.Observation.KnownMap, place => place.Id == "stream");
            Assert.DoesNotContain(PlaygroundDecisionContext.Observe(source, "a").KnownMap, place => place.Id == "stream");
            Assert.False(source.Expedition!.KnowsPassage("a", "trail-1"));
        }
        Assert.Same(source, history.State);
        Assert.Equal(cursor, history.Cursor);
        Assert.Equal(events, history.CompletedEvents.Count);
        Assert.Equal(original, source.DescribeState());
        Assert.IsType<TraversingLocation>(PlaygroundActions.Location(source, "a"));
        Assert.Null(history.PendingEvent);
    }

    [Fact]
    public async Task Intermediate_navigation_predicts_continue_then_respects_the_unknown_source_place_target_gate() {
        var history = await Intermediate();
        // B's instantaneous map rotates service to A without advancing the clock or navigation.
        await Commit(history, Op("map"));
        Assert.Equal("a", PlaygroundEngine.NextDecisionActor(history.State, history.Cursor));
        PlaygroundWorld source = history.State;
        var cursor = history.Cursor;
        int eventCount = history.CompletedEvents.Count;
        Assert.Equal(1, source.Expedition!.Navigation("a")!.CompletedSteps);
        var result = await PlaygroundLookaheadProjection.ProjectAsync(source, cursor, "a", Bindings, default, preparationLimit: Limit);
        Assert.Equal("continue", result.PredictedCommand);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.UnknownPlace, result.StopReason);
        Assert.True(result.Steps > 0); // Continue was accepted in scratch; prediction did not try the forbidden wait.
        Assert.Null(result.Target);
        Assert.Same(source, history.State);
        Assert.Equal(cursor, history.Cursor);
        Assert.Equal(eventCount, history.CompletedEvents.Count);
        Assert.Equal("stream", PlaygroundActions.CurrentPlace(source, "a"));
        Assert.Equal(1, source.Expedition.Navigation("a")!.CompletedSteps);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("same-move")]
    [InlineData("knowledge")]
    [InlineData("candidate")]
    [InlineData("navigation")]
    public async Task Actual_request_comparison_adopts_only_equal_navigation_material_and_publishes_the_response_once(string variation) {
        bool moving = variation == "same-move";
        bool equal = variation is "same" or "same-move";
        var original = await Intermediate();
        var actual = equal ? original : await Intermediate(variation);
        var warm = await PlaygroundLookaheadProjection.ProjectAsync(original.State, original.Cursor, "b", Bindings, default, preparationLimit: Limit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, warm.StopReason);
        var evidence = new Evidence();
        var client = new ModelClient(moving ? "continue" : "path camp");
        var current = original;
        await using var controller = new PlaygroundLookaheadController(() => (current.State, current.Cursor), Bindings,
            (_, _) => ValueTask.FromResult(new LlmDecisionBinding(client, "fixture-model", "fixture")),
            new LlmControllerOptions { MaxCalls = 4 }, _ => { }, evidence: evidence.Record);
        PlaygroundWorld frozen = original.State;
        var frozenCursor = original.Cursor;
        int operations = Operations(original).Count;
        PlaygroundControllerOperation source = await controller.ChooseCommandAsync(PlaygroundDecisionContext.Create(original.State, "b"), default);
        Assert.Single(evidence.Of("lookahead-candidate-created"));
        Assert.Same(frozen, original.State);
        Assert.Equal(frozenCursor, original.Cursor);
        Assert.Equal(operations, Operations(original).Count); // Capturing a model response never applies its path command.
        current = actual;
        await Commit(actual, source);
        Assert.Equal("a", PlaygroundEngine.NextDecisionActor(actual.State, actual.Cursor));
        PlaygroundControllerOperation answer = await controller.ChooseCommandAsync(PlaygroundDecisionContext.Create(actual.State, "a"), default);
        if (equal) {
            Assert.Single(evidence.Of("lookahead-candidate-adopted"));
            Assert.Empty(evidence.Of("lookahead-candidate-discarded"));
            Assert.Single(client.Requests, IsA);
        } else {
            JsonElement discarded = Assert.Single(evidence.Of("lookahead-candidate-discarded"));
            Assert.Equal(discarded.GetProperty("Target").GetRawText(), discarded.GetProperty("ActualTarget").GetRawText());
            Assert.Equal("input-changed", discarded.GetProperty("Reason").GetString());
            Assert.Empty(evidence.Of("lookahead-candidate-adopted"));
            CompletionRequest[] requests = client.Requests.Where(IsA).ToArray();
            Assert.Equal(2, requests.Length);
            Assert.NotEqual(Key(requests[0]), Key(requests[1]));
            if (variation == "candidate") {
                string candidate = NavigationView.RenderCandidate(PlaygroundDecisionContext.Create(actual.State, "a").Observation);
                Assert.Contains(candidate, Material(requests[1]));
                Assert.DoesNotContain(candidate, Material(requests[0]));
            }
        }
        int before = Operations(actual).Count;
        long generation = actual.State.Expedition!.Spatial.Entities.Single(entity => entity.Id.Value == "a").MovementGeneration;
        await Commit(actual, answer);
        PlaygroundFact published = Assert.Single(Operations(actual).Skip(before));
        Assert.Equal("a", published.ActorId);
        Assert.Equal(moving ? "continue" : "path camp", published.RawInput);
        Assert.Equal(moving ? PlaygroundEventKind.MoveStarted : PlaygroundEventKind.PathQueried, published.Kind);
        if (moving) {
            var entity = actual.State.Expedition!.Spatial.Entities.Single(entity => entity.Id.Value == "a");
            Assert.Equal(generation + 1, entity.MovementGeneration);
            var segment = Assert.IsType<TraversingLocation>(entity.Location);
            Assert.Equal("trail-3", segment.PassageId.Value);
            Assert.Equal("ravine", segment.TargetPlaceId.Value);
            Assert.Equal(actual.State.Time.Ticks + 2 * PlaygroundTime.Hour, segment.ArrivalDue.Ticks);
            Assert.Equal(1, actual.State.Expedition.Navigation("a")!.CompletedSteps);
            Assert.Single(published.SpatialFacts.OfType<TraversalStartedFact>());
        }
        Assert.Equal(answer.OperationRecordId, published.OperationRecordId);
        Assert.Single(Operations(actual), fact => fact.OperationRecordId == answer.OperationRecordId);
    }

    [Fact]
    public async Task Different_unconfirmed_remote_endpoints_produce_identical_actual_completion_material_and_comparison_keys() {
        var first = await Intermediate();
        var second = await Intermediate(hiddenEndpoint: true);
        Assert.NotEqual(first.State.Definition.Fingerprint, second.State.Definition.Fingerprint);
        var capture = new ModelClient();
        foreach (var history in new[] { first, second }) {
            var controller = new LlmCharacterController(capture, "fixture-model", context => CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout()));
            await controller.ChooseCommandAsync(PlaygroundDecisionContext.Create(history.State, "b"), default);
        }
        CompletionRequest[] requests = capture.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(Material(requests[0]), Material(requests[1]));
        Assert.Equal(Key(requests[0]), Key(requests[1]));
        Assert.DoesNotContain("trail-6", Material(requests[0]));
        Assert.DoesNotContain("巡山信号站", Material(requests[0]));
        Assert.DoesNotContain("山脚镇口", Material(requests[0]));
    }

    // B's idle opening is selected just after A departs. Only the disposable projection then reaches A's arrival.
    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Departure(bool navigation) {
        var history = Initial(known: navigation);
        var script = new Setup(navigation ? ["path ravine", "navigate"] : ["go e-c7"], []);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: script);
        if (navigation) {
            // B stays eligible after a harmless map; A can then start its selected route.
            script.B.Enqueue("map");
        }
        await Until(kernel, history, () => PlaygroundActions.IsTravelling(history.State, "a") && IsOpening(history, "b"));
        return history;
    }

    // All alternatives are valid histories produced by real path/navigation/query operations at identical identities and ticks.
    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Intermediate(string variation = "same", bool hiddenEndpoint = false) {
        var history = Initial(known: true, extraKnown: variation == "knowledge", hiddenEndpoint: hiddenEndpoint);
        string selected = variation == "navigation" ? "stream" : "ravine";
        string candidate = variation == "candidate" ? "ravine" : "camp";
        var script = new Setup(["path " + selected, "navigate", "path " + candidate], ["wait 1", "map"]);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: script);
        await Until(kernel, history, () => IsOpening(history, "b") &&
            history.State.Business.InteractionStates["a"].CurrentContext is { Status: CharacterContextStatus.Open } context &&
            context.LatestObservation.PlaceId == "stream" && context.Items.Any(item => item.RawInput == "path " + candidate) &&
            history.State.Time == Hour(11));
        return history;
    }

    private static InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> Initial(bool known, bool extraKnown = false, bool hiddenEndpoint = false) {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(10), enableNeeds: false, enableWeather: false, characterCount: 2);
        PlaygroundWorldDefinition definition = initial.World.Definition;
        if (hiddenEndpoint) { definition = definition.Change(passages: definition.Passages.Select(p => p.Id == "trail-6" ? p with { To = "road" } : p)); }
        var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
        var spatial = GraphSpatialState.Create(definition.Runtime.Graph, [new(new("a"), new("camp")), new(new("b"), new("cabin"))]);
        string[] aPlaces = known ? extraKnown ? ["camp", "stream", "ravine", "ridge"] : ["camp", "stream", "ravine"] : ["camp"];
        var visits = aPlaces.Select(place => new PlaygroundVisit("a", place, Hour(10), initial.World.Resources.Where(r => definition.GetResourcePlace(r.Id) == place)))
            .Append(new PlaygroundVisit("b", "cabin", Hour(10), initial.World.Resources.Where(r => definition.GetResourcePlace(r.Id) == "cabin")));
        var expedition = new PlaygroundExpedition(initial.World.Expedition!.ScenarioId, spatial, visits,
            confirmedPassages: known ? [new("a", "trail-1"), new("a", "trail-3")] : []);
        var business = new PlaygroundBusinessState(Hour(10), initial.World.Light, initial.World.Resources, initial.World.Script, actors,
            expedition: expedition, definition: definition);
        return new(PlaygroundEngine.Rebuild(business, Hour(10)), initial.Cursor);
    }

    private static bool IsOpening(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, string actor) =>
        PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == actor && history.State.Business.InteractionStates[actor].CurrentContext?.Status == CharacterContextStatus.Open;
    private static List<PlaygroundFact> Operations(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history) =>
        history.CompletedEvents.SelectMany(e => e.Facts).Where(fact => fact.OperationRecordId is not null).ToList();
    private static PlaygroundControllerOperation Op(string raw) => new("navigation-lookahead-call", raw, Guid.NewGuid().ToString("N"));
    private static async Task Commit(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, PlaygroundControllerOperation operation) {
        int count = Operations(history).Count;
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: new Script(_ => operation));
        await Until(kernel, history, () => Operations(history).Count > count);
    }
    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel,
        InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, Func<bool> done) {
        using var timeout = new CancellationTokenSource(Limit);
        for (int step = 0; step < 100; step++) {
            if (done()) { return; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24), timeout.Token));
        }
        Assert.Fail("Navigation lookahead setup did not reach the requested boundary.");
    }
    private static string Material(CompletionRequest request) => Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
    private static bool IsA(CompletionRequest request) => Material(request).Contains("· \"a\" ·", StringComparison.Ordinal);
    private static string Key(CompletionRequest request) {
        Assert.True(PlaygroundLookaheadController.TryInput(request, out string? key));
        return key!;
    }
    private sealed class Setup(string[] a, string[] b) : IPlaygroundCharacterController {
        private readonly Queue<string> _a = new(a);
        public Queue<string> B { get; } = new(b);
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken token) {
            string raw = (context.Observation.Actor.Id == "a" ? _a : B).Dequeue();
            if (raw == "navigate") { raw += " " + context.Observation.CandidatePath!.Id; }
            return ValueTask.FromResult(Op(raw));
        }
    }
    private sealed class Script(Func<PlaygroundDecisionContext, PlaygroundControllerOperation> choose) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken token) => ValueTask.FromResult(choose(context));
    }
    private sealed class ModelClient(string targetCommand = "path camp") : ICompletionClient {
        public string Name => "navigation-lookahead-fixture";
        public string ApiSpecId => "fixture";
        public ConcurrentQueue<CompletionRequest> Requests { get; } = new();
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Enqueue(request);
            string command = IsA(request) ? targetCommand : "wait 1";
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.ToolCall(new("act", "navigation-model-call", JsonSerializer.Serialize(new { command })))]), new("fixture", "fixture", request.ModelId)));
        }
    }
    private sealed class Evidence {
        private readonly ConcurrentQueue<(string Kind, JsonElement Detail)> _events = new();
        public void Record(string kind, object value) => _events.Enqueue((kind, JsonSerializer.SerializeToElement(value).GetProperty("Detail")));
        public JsonElement[] Of(string kind) => _events.Where(e => e.Kind == kind).Select(e => e.Detail).ToArray();
    }
}

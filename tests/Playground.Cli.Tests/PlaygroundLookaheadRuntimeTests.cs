using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadRuntimeTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings =
        new Dictionary<string, CharacterControlBinding> { ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture") };

    [Fact]
    public async Task Wrong_wait_prediction_still_adopts_independent_B_and_completed_B_cannot_publish_before_A() {
        using var files = new Files();
        var history = await Source();
        await WarmProjection(history);
        var source = history.State;
        var cursor = history.Cursor;
        int events = history.CompletedEvents.Count;
        var client = new BarrierClient(material => IsA(material) ? "wait 2" : "wait 1");
        using var timeout = new CancellationTokenSource(Limit);
        using var output = new StringWriter();
        Task<int> run = PlaygroundHost.RunAsync(history, Bindings, files.Options(2), timeout.Token, output, client);
        try {
            await client.CandidateCompleted.Task.WaitAsync(timeout.Token);
            Assert.False(run.IsCompleted);
            Assert.Same(source, history.State);
            Assert.Equal(cursor, history.Cursor);
            Assert.Equal(events, history.CompletedEvents.Count);
            Assert.Null(history.PendingEvent);
            Assert.Equal(2, client.Requests.Count);
        } finally { client.ReleaseA.TrySetResult(); }
        Assert.Equal(2, await run.WaitAsync(timeout.Token));
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(new[] { "a", "b" }, Operations(history).Select(f => f.ActorId));
        Assert.Equal(new[] { "wait 2", "wait 1" }, Operations(history).Select(f => f.RawInput));
        Assert.All(Operations(history), f => Assert.Equal(PlaygroundOperationDisposition.Succeeded, f.OperationDisposition));
        Assert.Single(files.Events("lookahead-candidate-adopted"));
        Assert.Empty(files.Events("lookahead-candidate-discarded"));
        Assert.Equal(2, files.Events("lookahead-physical-request").Count);
    }

    [Fact]
    public async Task Two_call_budget_allows_prefetched_B_to_commit_without_a_third_physical_call() {
        using var files = new Files();
        var history = await Source();
        await WarmProjection(history);
        var client = new FixedClient(_ => "wait 1");
        using var output = new StringWriter();
        using var timeout = new CancellationTokenSource(Limit);
        Assert.Equal(2, await PlaygroundHost.RunAsync(history, Bindings, files.Options(2), timeout.Token, output, client));
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(new[] { "a", "b" }, Operations(history).Select(f => f.ActorId));
        Assert.Single(files.Events("lookahead-candidate-created"));
        Assert.Single(files.Events("lookahead-candidate-adopted"));
        Assert.Equal(2, files.Events("lookahead-actual-delivery").Count);
        Assert.Contains("call-budget-exhausted", output.ToString());
    }

    [Fact]
    public async Task Shout_across_sound_edge_discards_prediction_and_real_B_receives_speech() {
        using var files = new Files();
        var history = await Source("camp", "stream");
        await WarmProjection(history);
        const string speech = "runtime-sound-edge-proof";
        var client = new BarrierClient(material => IsA(material) && !material.Contains(speech, StringComparison.Ordinal)
            ? "shout " + speech : "wait 1");
        using var output = new StringWriter();
        using var timeout = new CancellationTokenSource(Limit);
        Task<int> run = PlaygroundHost.RunAsync(history, Bindings, files.Options(6), timeout.Token, output, client);
        try { await client.CandidateCompleted.Task.WaitAsync(timeout.Token); }
        finally { client.ReleaseA.TrySetResult(); }
        Assert.Equal(2, await run.WaitAsync(timeout.Token));
        Assert.NotEmpty(files.Events("lookahead-candidate-discarded"));
        Assert.Contains(files.Events("lookahead-candidate-discarded"), record => {
            JsonElement detail = record.GetProperty("Detail");
            return detail.GetProperty("Reason").GetString() == "input-changed"
                && detail.GetProperty("Target").GetRawText() == detail.GetProperty("ActualTarget").GetRawText();
        });
        string[] bMaterials = client.Requests.Select(Material).Where(material => !IsA(material)).ToArray();
        Assert.Contains(bMaterials, material => !material.Contains(speech, StringComparison.Ordinal));
        Assert.Contains(bMaterials, material => material.Contains(speech, StringComparison.Ordinal));
        Assert.Contains(Operations(history), f => f.ActorId == "a" && f.RawInput == "shout " + speech
            && f.OperationDisposition == PlaygroundOperationDisposition.Succeeded);
        Assert.Contains(Operations(history), f => f.ActorId == "b" && f.OperationDisposition == PlaygroundOperationDisposition.Succeeded);
    }

    [Fact]
    public async Task Continuous_real_host_repeatedly_creates_and_adopts_across_model_ticks() {
        using var files = new Files();
        var history = await Source();
        await WarmProjection(history);
        var client = new FixedClient(_ => "wait 1");
        using var output = new StringWriter();
        using var timeout = new CancellationTokenSource(Limit);
        Assert.Equal(2, await PlaygroundHost.RunAsync(history, Bindings, files.Options(12), timeout.Token, output, client));
        Assert.Equal(12, client.Requests.Count);
        Assert.True(files.Events("lookahead-candidate-created").Count >= 3);
        Assert.True(files.Events("lookahead-candidate-adopted").Count >= 3);
        var operations = Operations(history);
        Assert.True(operations.Select(f => f.At.Ticks).Distinct().Count() >= 3);
        Assert.All(operations, f => Assert.Equal(PlaygroundOperationDisposition.Succeeded, f.OperationDisposition));
        Assert.Equal(operations.Count, operations.Select(f => f.OperationRecordId).Distinct().Count());
    }

    [Fact]
    public async Task Existing_nonzero_target_operation_sequence_is_preserved_and_adopted() {
        using var files = new Files();
        var history = await Source();
        var prelude = PlaygroundEngine.CreateKernel(history, characterController: new PrivateHandoff());
        for (int step = 0; step < 10 && history.State.Business.InteractionStates["b"].CurrentContext?.LastOperationSequence != 1; step++) {
            Assert.Equal(StepStatus.Committed, await prelude.StepAsync(new ModelTime(long.MaxValue)));
        }
        Assert.Equal(1, history.State.Business.InteractionStates["b"].CurrentContext!.LastOperationSequence);
        Assert.Equal("a", PlaygroundEngine.NextDecisionActor(history.State, history.Cursor));
        await WarmProjection(history);
        var client = new FixedClient(_ => "wait 1");
        using var output = new StringWriter();
        using var timeout = new CancellationTokenSource(Limit);
        Assert.Equal(2, await PlaygroundHost.RunAsync(history, Bindings, files.Options(2), timeout.Token, output, client));
        JsonElement adopted = Assert.Single(files.Events("lookahead-candidate-adopted")).GetProperty("Detail");
        Assert.Equal(1, adopted.GetProperty("Target").GetProperty("OperationSequence").GetInt64());
        Assert.Equal(2, client.Requests.Count);
        Assert.Empty(files.Events("lookahead-candidate-discarded"));
    }

    private sealed class PrivateHandoff : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken token) {
            string id = "setup-" + context.InteractionState.ActorId;
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, "handoff private-note", id));
        }
    }

    private static List<PlaygroundFact> Operations(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history) =>
        history.CompletedEvents.SelectMany(e => e.Facts).Where(f => f.OperationRecordId is not null).ToList();
    private static string Material(CompletionRequest request) =>
        Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
    private static bool IsA(string material) => material.Contains("· \"a\" ·", StringComparison.Ordinal);
    private static CompletionResult Response(CompletionRequest request, string command) => new(
        new ActionMessage([new ActionBlock.ToolCall(new("act", "runtime-fixture-call", JsonSerializer.Serialize(new { command })))]),
        new("fixture", "fixture", request.ModelId));

    // Warm only the independent projection; every assertion above uses the actual host/client path.
    private static async Task WarmProjection(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history) {
        var result = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default,
            preparationLimit: Limit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, result.StopReason);
        Assert.Equal("b", result.Target!.InteractionState.ActorId);
    }

    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Source(string aPlace = "camp", string bPlace = "cabin") {
        var time = new ModelTime(10 * PlaygroundTime.Hour);
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
        var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
        var places = new[] { aPlace, bPlace };
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph,
            actors.Select((actor, index) => new EntityPlacement(new(actor.Id), new(places[index]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((actor, index) => new PlaygroundVisit(actor.Id, places[index], time,
                initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[index]))));
        var business = new PlaygroundBusinessState(time, initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, time), initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history);
        for (int i = 0; i < 10; i++) {
            if (PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == "a"
                && history.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open) { return history; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(time));
        }
        throw new InvalidOperationException("A opening not reached.");
    }

    private sealed class FixedClient(Func<string, string> choose) : ICompletionClient {
        public string Name => "lookahead-runtime-fixture";
        public string ApiSpecId => "fixture";
        public ConcurrentQueue<CompletionRequest> Requests { get; } = new();
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Enqueue(request);
            return Task.FromResult(Response(request, choose(Material(request))));
        }
    }

    private sealed class BarrierClient(Func<string, string> choose) : ICompletionClient {
        private int _blocked;
        public string Name => "lookahead-runtime-barrier";
        public string ApiSpecId => "fixture";
        public ConcurrentQueue<CompletionRequest> Requests { get; } = new();
        public TaskCompletionSource ReleaseA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CandidateCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Enqueue(request);
            string material = Material(request);
            if (IsA(material) && Interlocked.Exchange(ref _blocked, 1) == 0) {
                return DelayedA(request, material, cancellationToken);
            }
            Task<CompletionResult> completed = Task.FromResult(Response(request, choose(material)));
            if (!IsA(material)) { CandidateCompleted.TrySetResult(); }
            return completed;
        }
        private async Task<CompletionResult> DelayedA(CompletionRequest request, string material, CancellationToken token) {
            await ReleaseA.Task.WaitAsync(token);
            return Response(request, choose(material));
        }
    }

    private sealed class Files : IDisposable {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-lookahead-runtime-" + Guid.NewGuid().ToString("N"));
        private string LogPath => Path.Combine(Root, "raw.jsonl");
        public PlaygroundHostOptions Options(int calls) => new(MaxCalls: calls, MaxSteps: 1000, LogPath: LogPath, Lookahead: true);
        public List<JsonElement> Events(string kind) => LlmRawAudit.Read(LogPath).Records
            .Where(record => record.Kind == "lookahead-event" && record.Payload.GetProperty("Kind").GetString() == kind)
            .Select(record => record.Payload.GetProperty("Data")).ToList();
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}

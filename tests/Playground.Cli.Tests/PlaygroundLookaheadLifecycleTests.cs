using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadLifecycleTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture")
    };

    [Fact]
    public async Task In_flight_candidate_is_adopted_and_waited_for_with_peak_two() {
        using var files = new Files();
        var history = await Source();
        var a = Signal<CompletionResult>();
        var b = Signal<CompletionResult>();
        var startedB = Signal<CompletionRequest>();
        var client = new Client((request, _) => {
            if (IsA(request)) { return a.Task; }
            startedB.TrySetResult(request);
            return b.Task;
        });
        using var stop = new CancellationTokenSource(Limit);
        using var output = new StringWriter();
        Task<int> run = PlaygroundHost.RunAsync(history, Bindings, files.Options(2), stop.Token, output, client);
        try {
            CompletionRequest bRequest = await startedB.Task.WaitAsync(stop.Token);
            a.TrySetResult(Act(client.Requests.Single(IsA), "wait 1"));
            await files.WaitFor("lookahead-candidate-adopted", 1, stop.Token);
            Assert.False(run.IsCompleted);
            Assert.False(b.Task.IsCompleted);
            Assert.Equal("a", Assert.Single(Operations(history)).ActorId);
            Assert.Equal(2, client.Requests.Count);
            b.TrySetResult(Act(bRequest, "wait 1"));
            Assert.Equal(2, await run.WaitAsync(stop.Token));
            Assert.Equal(new[] { "a", "b" }, Operations(history).Select(f => f.ActorId));
            Assert.Equal(2, files.Events("lookahead-run-summary").Single().GetProperty("PeakInFlight").GetInt32());
        } finally {
            a.TrySetCanceled(); b.TrySetCanceled();
            await run.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Discarded_uncooperative_provider_keeps_extra_slot_and_shutdown_drains_it() {
        using var files = new Files();
        var history = await Source(soundEdge: true);
        var firstA = Signal<CompletionResult>();
        var candidate = Signal<CompletionResult>();
        var candidateStarted = Signal<CompletionRequest>();
        var cancelled = Signal<bool>();
        int aCalls = 0, bCalls = 0;
        var client = new Client((request, token) => {
            if (IsA(request)) {
                return Interlocked.Increment(ref aCalls) == 1 ? firstA.Task : Task.FromResult(Act(request, "wait 1"));
            }
            if (Interlocked.Increment(ref bCalls) == 1) {
                token.Register(() => cancelled.TrySetResult(true));
                candidateStarted.TrySetResult(request);
                return candidate.Task; // Deliberately ignores cancellation until the test releases it.
            }
            return Task.FromResult(Act(request, "wait 1"));
        });
        using var stop = new CancellationTokenSource(Limit);
        using var output = new StringWriter();
        Task<int> run = PlaygroundHost.RunAsync(history, Bindings, files.Options(4), stop.Token, output, client);
        try {
            CompletionRequest candidateRequest = await candidateStarted.Task.WaitAsync(stop.Token);
            firstA.TrySetResult(Act(client.Requests.Single(IsA), "shout discard-me"));
            await cancelled.Task.WaitAsync(stop.Token);
            await files.WaitFor("lookahead-actual-delivery", 3, stop.Token);
            Assert.False(run.IsCompleted);
            Assert.False(candidate.Task.IsCompleted);
            Assert.Equal(4, client.Requests.Count);
            Assert.Single(files.Events("lookahead-candidate-created"));
            Assert.Single(files.Events("lookahead-candidate-discarded"));
            Assert.All(files.Events("lookahead-physical-request"), e => Assert.InRange(e.GetProperty("PeakInFlight").GetInt32(), 0, 2));
            candidate.TrySetResult(Act(candidateRequest, "wait 1"));
            Assert.Equal(2, await run.WaitAsync(stop.Token));
            Assert.Single(files.Events("lookahead-candidate-drained"));
            Assert.Equal(2, files.Events("lookahead-run-summary").Single().GetProperty("PeakInFlight").GetInt32());
            Assert.Equal(3, Operations(history).Count);
            Assert.Equal(2, Operations(history).Count(f => f.ActorId == "a"));
            Assert.Single(Operations(history), f => f.ActorId == "b");
        } finally {
            firstA.TrySetCanceled(); candidate.TrySetCanceled();
            await run.WaitAsync(Limit);
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task Candidate_exception_only_reaches_actual_controller_when_adopted(bool adopt, int expectedExit) {
        using var files = new Files();
        var history = await Source(soundEdge: !adopt);
        var client = new Client((request, _) => Task.FromResult(Act(request,
            Material(request).Contains("discard-fault", StringComparison.Ordinal) ? "wait 1" : adopt ? "wait 1" : "shout discard-fault"))) {
            FaultB = true
        };
        using var stop = new CancellationTokenSource(Limit);
        using var output = new StringWriter();
        Assert.Equal(expectedExit, await PlaygroundHost.RunAsync(history, Bindings, files.Options(adopt ? 2 : 3), stop.Token, output, client));
        Assert.Single(files.Events("lookahead-failure"));
        Assert.Equal(adopt ? 1 : 0, files.Events("lookahead-candidate-adopted").Count);
        Assert.Equal(adopt ? 0 : 1, files.Events("lookahead-candidate-discarded").Count);
        Assert.Equal(adopt ? 1 : 2, Operations(history).Count);
        Assert.Equal(adopt ? new[] { "a" } : new[] { "a", "b" }, Operations(history).Select(f => f.ActorId));
        Assert.All(Operations(history), f => Assert.Equal(PlaygroundOperationDisposition.Succeeded, f.OperationDisposition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_feedback_has_no_effect_until_adoption_then_ordinary_loop_saves_and_continues(bool adopt) {
        using var files = new Files();
        var history = await Source(soundEdge: !adopt);
        var firstA = Signal<CompletionResult>();
        var candidateReturned = Signal<bool>();
        int aCalls = 0;
        var client = new Client((request, _) => {
            if (IsA(request)) { return Interlocked.Increment(ref aCalls) == 1 ? firstA.Task : Task.FromResult(Act(request, "wait 1")); }
            if (Material(request).Contains("discard-feedback", StringComparison.Ordinal)) { return Task.FromResult(Act(request, "wait 1")); }
            if (!request.TailMessages.IsEmpty) { return Task.FromResult(Act(request, "wait 1")); }
            var response = Task.FromResult(Tool(request, "feedback", new { text = "candidate-feedback-proof" }));
            candidateReturned.TrySetResult(true);
            return response;
        });
        using var stop = new CancellationTokenSource(Limit);
        using var output = new StringWriter();
        Task<int> run = PlaygroundHost.RunAsync(history, Bindings, files.Options(3), stop.Token, output, client);
        try {
            await candidateReturned.Task.WaitAsync(stop.Token);
            Assert.Empty(files.Reports());
            Assert.Empty(Operations(history));
            firstA.TrySetResult(Act(client.Requests.Single(IsA), adopt ? "wait 1" : "shout discard-feedback"));
            Assert.Equal(2, await run.WaitAsync(stop.Token));
            if (adopt) {
                FeedbackRecord saved = Assert.Single(files.Reports());
                Assert.Equal("candidate-feedback-proof", saved.Text);
                CompletionRequest continuation = Assert.Single(client.Requests, request => !request.TailMessages.IsEmpty);
                var receipt = Assert.IsType<ToolResultsMessage>(continuation.TailMessages[1]);
                Assert.Contains(saved.FeedbackId, Assert.Single(receipt.Results).GetFlattenedText());
                Assert.Equal(new[] { "a", "b" }, Operations(history).Select(f => f.ActorId));
            } else {
                Assert.Empty(files.Reports());
                Assert.All(client.Requests, request => Assert.True(request.TailMessages.IsEmpty));
                Assert.Single(files.Events("lookahead-candidate-discarded"));
            }
        } finally {
            firstA.TrySetCanceled();
            await run.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Human_next_decision_prevents_candidate_and_EOF_stops_cleanly() {
        using var files = new Files();
        var bindings = new Dictionary<string, CharacterControlBinding>(Bindings) { ["b"] = new CharacterControlBinding.HumanConsole() };
        var history = await Source();
        var client = new Client((request, _) => Task.FromResult(Act(request, "wait 1")));
        using var output = new StringWriter();
        using var input = new StringReader("");
        using var stop = new CancellationTokenSource(Limit);
        Assert.Equal(0, await PlaygroundHost.RunAsync(history, bindings, files.Options(4), stop.Token, output, client, input));
        Assert.Single(client.Requests);
        Assert.Empty(files.Events("lookahead-candidate-created"));
        Assert.Equal("a", Assert.Single(Operations(history)).ActorId);
        Assert.Contains("Human 会话已结束", output.ToString());
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Material(CompletionRequest request) => Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
    private static bool IsA(CompletionRequest request) => Material(request).Contains("· \"a\" ·", StringComparison.Ordinal);
    private static CompletionResult Act(CompletionRequest request, string command) => Tool(request, "act", new { command });
    private static CompletionResult Tool(CompletionRequest request, string name, object arguments) => new(
        new ActionMessage([new ActionBlock.ToolCall(new(name, "lifecycle-" + name, JsonSerializer.Serialize(arguments)))]), new("fixture", "fixture", request.ModelId));
    private static List<PlaygroundFact> Operations(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history) =>
        history.CompletedEvents.SelectMany(e => e.Facts).Where(f => f.OperationRecordId is not null).ToList();

    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Source(bool soundEdge = false) {
        var time = new ModelTime(10 * PlaygroundTime.Hour);
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
        var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
        string[] places = ["camp", soundEdge ? "stream" : "cabin"];
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph,
            actors.Select((actor, i) => new EntityPlacement(new(actor.Id), new(places[i]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((actor, i) => new PlaygroundVisit(actor.Id, places[i], time,
                initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[i]))));
        var business = new PlaygroundBusinessState(time, initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, time), initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history);
        for (int i = 0; i < 10; i++) {
            if (PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == "a"
                && history.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open) {
                var warm = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
                Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, warm.StopReason);
                return history;
            }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(time));
        }
        throw new InvalidOperationException("A opening not reached.");
    }

    private sealed class Client(Func<CompletionRequest, CancellationToken, Task<CompletionResult>> respond) : ICompletionClient {
        private int _bCalls;
        public string Name => "lookahead-lifecycle-fixture";
        public string ApiSpecId => "fixture";
        public bool FaultB { get; init; }
        public ConcurrentQueue<CompletionRequest> Requests { get; } = new();
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Enqueue(request);
            return FaultB && !IsA(request) && Interlocked.Increment(ref _bCalls) == 1
                ? Task.FromException<CompletionResult>(new InvalidOperationException("candidate-provider-fault")) : respond(request, cancellationToken);
        }
    }

    private sealed class Files : IDisposable {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-lookahead-lifecycle-" + Guid.NewGuid().ToString("N"));
        private string LogPath => Path.Combine(Root, "raw.jsonl");
        public PlaygroundHostOptions Options(int calls) => new(MaxCalls: calls, MaxSteps: 1000, HomePath: Root, LogPath: LogPath, Lookahead: true);
        public IReadOnlyList<FeedbackRecord> Reports() => FeedbackJournal.Read(FeedbackSession.JournalPath(Root));
        public List<JsonElement> Events(string kind) => !File.Exists(LogPath) ? [] : LlmRawAudit.Read(LogPath).Records
            .Where(record => record.Kind == "lookahead-event" && record.Payload.GetProperty("Kind").GetString() == kind)
            .Select(record => record.Payload.GetProperty("Data")).ToList();
        public async Task WaitFor(string kind, int count, CancellationToken token) {
            while (Events(kind).Count < count) { token.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}

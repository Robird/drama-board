using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadHumanWaitingTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"),
        ["b"] = new CharacterControlBinding.Llm("fixture"),
        ["c"] = new CharacterControlBinding.HumanConsole()
    };

    [Fact]
    public async Task Human_inserted_before_candidate_blocks_input_without_delivering_candidate_feedback_or_mutating_B() {
        using var files = new Files();
        var history = await Source();
        var projection = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, projection.StopReason);
        Assert.Equal("b", projection.Target!.InteractionState.ActorId);
        Assert.Equal(Hour(11), projection.Target.Observation.Time);
        var sourceB = history.State.Business.InteractionStates["b"];
        var sourceActivityB = history.State.GetCharacter("b").Activity;
        var client = new HeldClient();
        using var input = new HeldReader();
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource(Limit);
        var options = new PlaygroundHostOptions(MaxCalls: 2, MaxSteps: 1000, LogPath: files.RawPath, HomePath: files.Root, Lookahead: true);
        Task<int> run = PlaygroundHost.RunAsync(history, Bindings, options, stop.Token, output, client, input);
        try {
            CompletionRequest candidateRequest = await client.CandidateStarted.Task.WaitAsync(stop.Token);
            client.ReleaseA.TrySetResult(true);
            await input.ReadStarted.Task.WaitAsync(stop.Token);
            Assert.False(input.Release.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.False(client.Candidate.Task.IsCompleted);
            Assert.Equal("c", PlaygroundEngine.NextDecisionActor(history.State, history.Cursor));
            Assert.Equal(Hour(10), history.State.Time);
            Assert.True(history.State.GetCharacter("c").Communication.AttentionPending);
            Assert.Same(sourceB, history.State.Business.InteractionStates["b"]);
            Assert.Same(sourceActivityB, history.State.GetCharacter("b").Activity);
            var waitingWorld = history.State;
            var waitingCursor = history.Cursor;
            int waitingEvents = history.CompletedEvents.Count;
            Assert.Equal("a", Assert.Single(Operations(history)).ActorId);
            Assert.Empty(files.Reports());

            // The candidate completes while real Human input remains blocked. Its feedback tool
            // must remain physical evidence only, with no actual controller/history delivery.
            client.Candidate.TrySetResult(Tool(candidateRequest, "feedback", new { text = "must-not-save-during-human-wait" }));
            await files.WaitForCompleted(2, stop.Token);
            Assert.False(input.Release.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Same(waitingWorld, history.State);
            Assert.Equal(waitingCursor, history.Cursor);
            Assert.Equal(waitingEvents, history.CompletedEvents.Count);
            Assert.Null(history.PendingEvent);
            Assert.Same(sourceB, history.State.Business.InteractionStates["b"]);
            Assert.Empty(files.Reports());
            Assert.DoesNotContain(files.Records(), record => record.Kind == "model-response"
                && record.Payload.GetProperty("CharacterId").GetString() == "b");
            Assert.Empty(files.Events("lookahead-candidate-adopted"));
            Assert.Equal(2, client.Requests.Count);

            input.Release.TrySetResult(null); // EOF is released only after proving actual waiting.
            Assert.Equal(0, await run.WaitAsync(stop.Token));
            Assert.Single(files.Events("lookahead-candidate-drained"));
            Assert.Single(files.Events("lookahead-actual-delivery"));
            Assert.Equal(2, files.Events("lookahead-physical-request").Count);
            Assert.Equal("a", Assert.Single(Operations(history)).ActorId);
            Assert.Empty(files.Reports());
            Assert.Same(sourceB, history.State.Business.InteractionStates["b"]);
            Assert.Contains("Human 会话已结束", output.ToString());
        } finally {
            client.ReleaseA.TrySetResult(true);
            client.Candidate.TrySetCanceled();
            input.Release.TrySetResult(null);
            await run.WaitAsync(Limit);
        }
    }

    private static ModelTime Hour(int hour) => new(hour * PlaygroundTime.Hour);
    private static List<PlaygroundFact> Operations(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history) =>
        history.CompletedEvents.SelectMany(e => e.Facts).Where(f => f.OperationRecordId is not null).ToList();
    private static CompletionResult Tool(CompletionRequest request, string name, object arguments) => new(
        new ActionMessage([new ActionBlock.ToolCall(new(name, "human-wait-" + name, JsonSerializer.Serialize(arguments)))]),
        new("fixture", "fixture", request.ModelId));
    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Source() {
        var time = Hour(10);
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 3);
        var actors = new[] {
            new PlaygroundCharacter("a", "A"),
            new PlaygroundCharacter("b", "B", activity: new PlaygroundActivity(PlaygroundActivityKind.Wait, 1, time, PlaygroundTime.Hour, Hour(11)), nextActivityId: 2),
            new PlaygroundCharacter("c", "C", activity: new PlaygroundActivity(PlaygroundActivityKind.Wait, 1, time, 3 * PlaygroundTime.Hour, Hour(13)), nextActivityId: 2)
        };
        string[] places = ["camp", "cabin", "stream"];
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
                && history.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open) { return history; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(time));
        }
        throw new InvalidOperationException("A opening not reached.");
    }
    private sealed class HeldReader : TextReader {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) {
            ReadStarted.TrySetResult(true);
            return new(Release.Task.WaitAsync(cancellationToken));
        }
    }
    private sealed class HeldClient : ICompletionClient {
        public string Name => "lookahead-human-wait-fixture";
        public string ApiSpecId => "fixture";
        public ConcurrentQueue<CompletionRequest> Requests { get; } = new();
        public TaskCompletionSource<bool> ReleaseA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CompletionRequest> CandidateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CompletionResult> Candidate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Enqueue(request);
            string material = Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
            if (material.Contains("· \"a\" ·", StringComparison.Ordinal)) { return WaitA(request, cancellationToken); }
            CandidateStarted.TrySetResult(request);
            return Candidate.Task.WaitAsync(cancellationToken);
        }
        private async Task<CompletionResult> WaitA(CompletionRequest request, CancellationToken token) {
            await ReleaseA.Task.WaitAsync(token);
            return Tool(request, "act", new { command = "shout wake-human-before-B" });
        }
    }
    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-lookahead-human-wait-" + Guid.NewGuid().ToString("N"));
        public string RawPath => Path.Combine(Root, "raw.jsonl");
        public IReadOnlyList<FeedbackRecord> Reports() => FeedbackJournal.Read(FeedbackSession.JournalPath(Root));
        public IReadOnlyList<LlmRawAuditRecord> Records() => LlmRawAudit.Read(RawPath).Records;
        public List<JsonElement> Events(string kind) => Records().Where(record => record.Kind == "lookahead-event"
            && record.Payload.GetProperty("Kind").GetString() == kind).Select(record => record.Payload.GetProperty("Data").GetProperty("Detail")).ToList();
        public async Task WaitForCompleted(int count, CancellationToken token) {
            while (Events("lookahead-physical-completed").Count < count) { token.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}

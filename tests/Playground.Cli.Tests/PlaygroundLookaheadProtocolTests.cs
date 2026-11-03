using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadProtocolTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture")
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adopted_invalid_act_is_corrected_only_in_actual_loop_and_does_not_start_another_prefetch(bool feedbackEnabled) {
        using var files = new Files();
        var history = await Source();
        var warm = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, warm.StopReason);
        var client = new ProtocolClient();
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource(Limit);
        var options = new PlaygroundHostOptions(MaxCalls: 3, LogPath: files.RawPath, Lookahead: true,
            HomePath: feedbackEnabled ? files.Root : null);
        Task<int> run = PlaygroundHost.RunAsync(history, Bindings, options, stop.Token, output, client);
        try {
            await client.CandidateReturned.Task.WaitAsync(stop.Token);
            Assert.Empty(Operations(history));
            Assert.Equal(2, client.Requests.Count);
            Assert.DoesNotContain(files.Records(), record => record.Kind == "model-response");
            client.ReleaseA.TrySetResult(true);
            Assert.Equal(2, await run.WaitAsync(stop.Token));
        } finally {
            client.ReleaseA.TrySetResult(true);
            await run.WaitAsync(Limit);
        }

        Assert.Equal(3, client.Requests.Count);
        CompletionRequest correction = Assert.Single(client.Requests, request => ProtocolClient.IsCorrection(request));
        if (feedbackEnabled) {
            Assert.Single(correction.PromptPrefix.SharedContextMessages);
            string text = Assert.IsType<ObservationMessage>(Assert.Single(correction.TailMessages)).Content!;
            Assert.Contains("协议错误", text);
            Assert.Contains("每次响应只调用一个已提供的工具", text);
            Assert.Empty(FeedbackJournal.Read(FeedbackSession.JournalPath(files.Root)));
        } else {
            Assert.Empty(correction.TailMessages);
            Assert.Equal(2, correction.PromptPrefix.SharedContextMessages.Length);
            string text = Assert.IsType<ObservationMessage>(correction.PromptPrefix.SharedContextMessages[1]).Content!;
            Assert.Contains("协议错误", text);
            Assert.Contains("请只调用一次 act", text);
        }
        var operations = Operations(history);
        Assert.Equal(new[] { "a", "b" }, operations.Select(f => f.ActorId));
        Assert.All(operations, operation => {
            Assert.Equal("wait 1", operation.RawInput);
            Assert.Equal(PlaygroundOperationDisposition.Succeeded, operation.OperationDisposition);
        });
        Assert.Single(files.Events("lookahead-candidate-created"));
        JsonElement adopted = Assert.Single(files.Events("lookahead-candidate-adopted"));
        Assert.Empty(files.Events("lookahead-candidate-discarded"));
        var physical = files.Events("lookahead-physical-request");
        var dispatches = files.Events("lookahead-actual-dispatch");
        var deliveries = files.Events("lookahead-actual-delivery");
        Assert.Equal(3, physical.Count);
        Assert.Equal(3, dispatches.Count);
        Assert.Equal(3, deliveries.Count);
        Assert.Equal(3, dispatches.Select(e => e.GetProperty("PhysicalId").GetString()).Distinct().Count());
        Assert.All(dispatches, dispatch => {
            string logicalId = dispatch.GetProperty("LogicalRequestId").GetString()!;
            string physicalId = dispatch.GetProperty("PhysicalId").GetString()!;
            Assert.Single(physical, request => request.GetProperty("PhysicalId").GetString() == physicalId);
            Assert.Single(deliveries, delivery => delivery.GetProperty("LogicalRequestId").GetString() == logicalId
                && delivery.GetProperty("PhysicalId").GetString() == physicalId);
            Assert.Single(files.Records(), record => record.Kind == "model-request"
                && record.Payload.GetProperty("RequestId").GetString() == logicalId);
        });
        Assert.Contains(dispatches, dispatch => dispatch.GetProperty("PhysicalId").GetString() == adopted.GetProperty("PhysicalId").GetString());
        Assert.Equal(new[] { 1, 2 }, dispatches.Where(e => e.GetProperty("Target").GetProperty("ActorId").GetString() == "b")
            .Select(e => e.GetProperty("Turn").GetInt32()));
    }

    private static List<PlaygroundFact> Operations(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history) =>
        history.CompletedEvents.SelectMany(e => e.Facts).Where(f => f.OperationRecordId is not null).ToList();
    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Source() {
        var time = new ModelTime(10 * PlaygroundTime.Hour);
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
        var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
        string[] places = ["camp", "cabin"];
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
    private sealed class ProtocolClient : ICompletionClient {
        public string Name => "lookahead-protocol-fixture";
        public string ApiSpecId => "fixture";
        public ConcurrentQueue<CompletionRequest> Requests { get; } = new();
        public TaskCompletionSource<bool> ReleaseA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CandidateReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static bool IsCorrection(CompletionRequest request) => request.PromptPrefix.SharedContextMessages.Length > 1 || !request.TailMessages.IsEmpty;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Enqueue(request);
            string material = Assert.IsType<ObservationMessage>(request.PromptPrefix.SharedContextMessages[0]).Content!;
            if (material.Contains("· \"a\" ·", StringComparison.Ordinal)) { return WaitA(request, cancellationToken); }
            if (IsCorrection(request)) { return Task.FromResult(Result(request, "{\"command\":\"wait 1\"}")); }
            Task<CompletionResult> invalid = Task.FromResult(Result(request, "{\"command\":42}"));
            CandidateReturned.TrySetResult(true);
            return invalid;
        }
        private async Task<CompletionResult> WaitA(CompletionRequest request, CancellationToken token) {
            await ReleaseA.Task.WaitAsync(token);
            return Result(request, "{\"command\":\"wait 1\"}");
        }
        private static CompletionResult Result(CompletionRequest request, string arguments) => new(
            new ActionMessage([new ActionBlock.ToolCall(new("act", "protocol-fixture-call", arguments))]), new("fixture", "fixture", request.ModelId));
    }
    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-lookahead-protocol-" + Guid.NewGuid().ToString("N"));
        public string RawPath => Path.Combine(Root, "raw.jsonl");
        public IReadOnlyList<LlmRawAuditRecord> Records() => LlmRawAudit.Read(RawPath).Records;
        public List<JsonElement> Events(string kind) => Records().Where(record => record.Kind == "lookahead-event"
            && record.Payload.GetProperty("Kind").GetString() == kind).Select(record => record.Payload.GetProperty("Data").GetProperty("Detail")).ToList();
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Coordinator/Kernel diagnostic boundaries only; these tests do not exercise Host cleanup wiring.</summary>
public sealed class PlaygroundLookaheadDiagnosticFailureTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private const string Secret = "credential-do-not-record-123";
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture")
    };

    [Fact]
    public async Task Raw_flush_failure_with_two_requests_in_flight_retires_admission_and_disposal_drains_both() {
        var history = await Source();
        using var stream = new FailNthFlushStream();
        using var raw = LlmRawAudit.CreateForTesting(stream, "diagnostic-run");
        var aRequest = Signal<CompletionRequest>();
        var bRequest = Signal<CompletionRequest>();
        var aResponse = Signal<CompletionResult>();
        var bResponse = Signal<CompletionResult>();
        var aCancelled = Signal<bool>();
        var bCancelled = Signal<bool>();
        int calls = 0;
        var client = new Client((request, token) => {
            Interlocked.Increment(ref calls);
            if (IsA(request)) {
                token.Register(() => aCancelled.TrySetResult(true));
                aRequest.TrySetResult(request);
                return aResponse.Task;
            }
            token.Register(() => bCancelled.TrySetResult(true));
            bRequest.TrySetResult(request);
            return bResponse.Task; // Deliberately non-cooperative: cancellation is observable, not terminal.
        });
        var traces = new List<LlmControllerTrace>();
        var dispatches = new ConcurrentQueue<JsonElement>();
        var coordinator = Controller(history, client, traces.Add, (kind, payload) => {
            if (kind == "lookahead-actual-dispatch") { dispatches.Enqueue(JsonSerializer.SerializeToElement(payload)); }
            if (kind == "lookahead-candidate-created") { stream.FailAtFlush = stream.FlushCount + 1; }
            raw.RecordLookahead(kind, payload);
        });
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: coordinator);
        var before = history.Cursor;
        using var timeout = new CancellationTokenSource(Limit);
        Task<StepStatus> step = kernel.StepAsync(history.State.Time, timeout.Token).AsTask();
        await aRequest.Task.WaitAsync(timeout.Token);
        await bRequest.Task.WaitAsync(timeout.Token);
        IOException primary = await Assert.ThrowsAsync<IOException>(() => step);
        Assert.Same(stream.Failure, primary);
        Assert.True(raw.IsFaulted);
        Assert.Equal(before, history.Cursor);
        Assert.Equal(2, calls);
        Assert.False(aResponse.Task.IsCompleted);
        Assert.False(bResponse.Task.IsCompleted);
        JsonElement dispatch = Assert.Single(dispatches).GetProperty("Detail");
        Assert.Equal(Assert.Single(traces, trace => trace.Kind == LlmControllerTraceKind.Request).RequestId,
            dispatch.GetProperty("LogicalRequestId").GetString());
        Assert.Equal("diagnostic-run-physical-1", dispatch.GetProperty("PhysicalId").GetString());

        InvalidOperationException retired = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ChooseCommandAsync(PlaygroundDecisionContext.Create(history.State, "a"), timeout.Token).AsTask());
        Assert.Same(primary, retired.InnerException);
        Assert.Equal(2, calls);
        Task cleanup = coordinator.DisposeAsync().AsTask();
        try {
            await aCancelled.Task.WaitAsync(timeout.Token);
            await bCancelled.Task.WaitAsync(timeout.Token);
            Assert.False(cleanup.IsCompleted);
            aResponse.TrySetResult(Act(await aRequest.Task));
            Assert.False(cleanup.IsCompleted);
            bResponse.TrySetResult(Act(await bRequest.Task));
            AggregateException cleanupFailure = await Assert.ThrowsAsync<AggregateException>(() => cleanup);
            Assert.Contains(cleanupFailure.Flatten().InnerExceptions, error => ReferenceEquals(error, primary));
            Assert.Same(stream.Failure, primary); // The caller retains the original failure independently of cleanup errors.
            Assert.Equal(2, coordinator.CallsMade);
            Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(stream.ToArray()));
            Assert.All(traces.Where(trace => trace.Kind == LlmControllerTraceKind.Request), trace => Assert.NotNull(trace.RequestId));
        } finally {
            aResponse.TrySetCanceled(); bResponse.TrySetCanceled();
            try { await cleanup.WaitAsync(Limit); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_evidence_keeps_transport_facts_and_only_local_rejection_proves_no_remote_outcome(bool rejected) {
        var history = await Source();
        var facts = new CompletionFailureInfo(CompletionFailureKind.Transport, RetryAfter: TimeSpan.FromSeconds(7));
        Exception failure = rejected
            ? new CompletionRequestRejectedException(CompletionTermination.Failed("local-contract"), innerException: new IOException(Secret))
            : new CompletionFailureException(facts, Secret, new IOException(Secret));
        var client = new Client((_, _) => Task.FromException<CompletionResult>(failure));
        var evidence = new ConcurrentQueue<(string Kind, JsonElement Data)>();
        var coordinator = Controller(history, client, _ => { }, (kind, payload) => evidence.Enqueue((kind, JsonSerializer.SerializeToElement(payload))),
            new Dictionary<string, CharacterControlBinding>(Bindings) { ["b"] = new CharacterControlBinding.HumanConsole() });
        try {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: coordinator);
            var paused = await Assert.ThrowsAsync<LlmControllerPausedException>(() => kernel.StepAsync(history.State.Time).AsTask());
            Assert.Same(failure, paused.InnerException);
            JsonElement detail = Assert.Single(evidence, entry => entry.Kind == "lookahead-failure").Data.GetProperty("Detail");
            Assert.Equal(!rejected, detail.GetProperty("RemoteOutcomeUnknown").GetBoolean());
            if (!rejected) {
                JsonElement recorded = detail.GetProperty("Failure");
                Assert.Equal((int)CompletionFailureKind.Transport, recorded.GetProperty("Kind").GetInt32());
                Assert.Equal("00:00:07", recorded.GetProperty("RetryAfter").GetString());
            }
            Assert.DoesNotContain(Secret, string.Join("\n", evidence.Select(entry => entry.Data.GetRawText())));
            Assert.Equal(1, coordinator.CallsMade);
        } finally { await coordinator.DisposeAsync(); }
    }

    private static PlaygroundLookaheadController Controller(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history,
        ICompletionClient client, Action<LlmControllerTrace> trace, Action<string, object> evidence,
        IReadOnlyDictionary<string, CharacterControlBinding>? bindings = null) => new(() => (history.State, history.Cursor), bindings ?? Bindings,
            (_, _) => ValueTask.FromResult(new LlmDecisionBinding(client, "fixture-model", "fixture")),
            new LlmControllerOptions { MaxCalls = 8 }, trace, evidence: evidence, runId: "diagnostic-run", branchId: "ephemeral");
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool IsA(CompletionRequest request) => ((ObservationMessage)request.PromptPrefix.SharedContextMessages[0]).Content!.Contains("· \"a\" ·", StringComparison.Ordinal);
    private static CompletionResult Act(CompletionRequest request) => new(
        new ActionMessage([new ActionBlock.ToolCall(new("act", "diagnostic-act", "{\"command\":\"wait 1\"}"))]),
        new("fixture", "fixture", request.ModelId));

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
            if (PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == "a" &&
                history.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open) {
                var warm = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
                Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, warm.StopReason);
                return history;
            }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(time));
        }
        throw new InvalidOperationException("A opening not reached.");
    }
    private sealed class Client(Func<CompletionRequest, CancellationToken, Task<CompletionResult>> respond) : ICompletionClient {
        public string Name => "diagnostic-fixture";
        public string ApiSpecId => "fixture";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) => respond(request, cancellationToken);
    }
    private sealed class FailNthFlushStream : MemoryStream {
        public int FlushCount { get; private set; }
        public int FailAtFlush { get; set; } = int.MaxValue;
        public IOException Failure { get; } = new(Secret);
        public override void Flush() {
            if (++FlushCount == FailAtFlush) { throw Failure; }
            base.Flush();
        }
    }
}

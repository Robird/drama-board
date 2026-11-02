using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.CharacterGym;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground.Completion;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Offline probes of real materials, controllers, arbitration and durable publication.
/// Candidate requests are diagnostics, never persisted decision boundaries.</summary>
public sealed class PredictiveTimeParallelismTests {
    [Fact]
    public async Task Independent_first_requests_overlap_but_full_trace_and_operation_identities_follow_serial_kernel() {
        using var serial = new Run();
        await serial.Execute(new ImmediateClient(Act("handoff private-a")), new ImmediateClient(Act("handoff private-b")));

        using var parallel = new Run();
        PlaygroundWorld source = parallel.History.State;
        var providerA = new HeldClient(Act("handoff private-a"));
        var providerB = new HeldClient(Act("handoff private-b"));
        await using var candidateA = new Candidate(Opening(source, "a"), providerA);
        await using var candidateB = new Candidate(Opening(source, "b"), providerB);
        await Task.WhenAll(providerA.Invoked.Task, providerB.Invoked.Task).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(providerA.Release.Task.IsCompleted);
        Assert.False(providerB.Release.Task.IsCompleted); // Both actual Completion calls are outstanding.
        Assert.Same(source, parallel.History.State);
        Assert.All(source.Business.InteractionStates.Values, state => Assert.Null(state.CurrentContext));
        Assert.Throws<InvalidOperationException>(() => parallel.History.CaptureDecisionBoundary("b"));
        providerB.Release.SetResult(); // Faster b cannot advance the real world.
        await candidateB.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(source, parallel.History.State);
        providerA.Release.SetResult();
        await candidateA.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var gateA = new AdoptionClient(candidateA, new ImmediateClient(Act("handoff unexpected")));
        var gateB = new AdoptionClient(candidateB, new ImmediateClient(Act("handoff unexpected")));
        await parallel.Execute(gateA, gateB);
        Assert.Equal(1, gateA.Adopted);
        Assert.Equal(1, gateB.Adopted);
        Assert.Equal(0, gateA.Discarded + gateB.Discarded);
        Assert.Equal(serial.Inputs, parallel.Inputs);
        Assert.Equal(serial.Transitions, parallel.Transitions);
        Assert.Equal(serial.FinalMaterial(), parallel.FinalMaterial());
        Assert.Equal(new[] { "a", "b" }, parallel.Boundaries.Select(boundary => boundary.ActorId));
        parallel.CloseHistory();
        foreach (PlaygroundDecisionBoundary boundary in parallel.Boundaries) {
            Assert.Equal(boundary, PlaygroundDurableHistory.InspectDecisionBoundary(parallel.Store,
                boundary.BranchId, boundary.StateCommitId, boundary.ActorId));
        }
    }

    [Fact]
    public async Task Another_actors_speech_invalidates_full_input_and_falls_back_without_submitting_candidate() {
        using var serial = new Run();
        await serial.Execute(new ImmediateClient(Act("say b changed-information")), new ImmediateClient(Act("handoff heard")));
        using var parallel = new Run();
        var provider = new HeldClient(Act("handoff stale-candidate"));
        await using var candidate = new Candidate(Opening(parallel.History.State, "b"), provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var gate = new AdoptionClient(candidate, new ImmediateClient(Act("handoff heard")));
        await parallel.Execute(new ImmediateClient(Act("say b changed-information")), gate);
        Assert.Equal(0, gate.Adopted);
        Assert.Equal(1, gate.Discarded);
        Assert.NotEqual(RequestValue(candidate.Request!), parallel.Inputs[1]);
        Assert.Contains("changed-information", parallel.Inputs[1]);
        Assert.DoesNotContain("stale-candidate", parallel.FinalMaterial());
        Assert.Equal(serial.Inputs, parallel.Inputs);
        Assert.Equal(serial.Transitions, parallel.Transitions);
        Assert.Equal(serial.FinalMaterial(), parallel.FinalMaterial());
    }

    [Fact]
    public async Task Stopping_an_inflight_candidate_drains_controller_without_opening_real_context() {
        using var run = new Run();
        PlaygroundWorld source = run.History.State;
        KernelCursor cursor = run.History.Cursor;
        var provider = new HeldClient(Act("handoff never-adopted"));
        var candidate = new Candidate(Opening(source, "b"), provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await candidate.DisposeAsync();
        Assert.True(candidate.Ready.Task.IsCanceled);
        Assert.Same(source, run.History.State);
        Assert.Equal(cursor, run.History.Cursor);
        Assert.Null(run.History.PendingEvent);
        Assert.All(source.Business.InteractionStates.Values, state => Assert.Null(state.CurrentContext));
        // Local cancellation proves cleanup, not that a remote provider avoided computation.
    }

    [Fact]
    public async Task Cached_feedback_is_not_saved_until_actual_adoption_and_continuation_uses_real_receipt() {
        using var run = new Run();
        int saves = 0;
        ValueTask<string> Save(LlmFeedbackSubmission submission, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Assert.Equal("a", submission.CharacterId);
            saves++;
            return ValueTask.FromResult("receipt-actual-adoption");
        }
        var provider = new HeldClient(Result(new("feedback", "feedback-native-id", "{\"text\":\"offline-probe\"}")));
        await using var candidate = new Candidate(Opening(run.History.State, "a"), provider, Save);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, saves);
        Assert.Null(run.History.State.Business.InteractionStates["a"].CurrentContext);
        var continuation = new ImmediateClient(Act("handoff after-feedback"));
        var gate = new AdoptionClient(candidate, continuation);
        await run.Execute(gate, new ImmediateClient(Act("handoff b")), Save);
        Assert.Equal(1, saves);
        Assert.Equal(1, gate.Adopted);
        Assert.Equal(0, gate.Discarded);
        CompletionRequest next = Assert.Single(continuation.Requests);
        CompletionResult cached = await candidate.Ready.Task;
        Assert.Same(cached.Message, next.TailMessages[0]);
        ToolResult receipt = Assert.Single(Assert.IsType<ToolResultsMessage>(next.TailMessages[1]).Results);
        Assert.Equal("feedback-native-id", receipt.ToolCallId);
        Assert.Contains("receipt-actual-adoption", RequestValue(next));
        Assert.DoesNotContain("offline-probe", run.FinalMaterial());
    }

    // OpenContext performs trade maintenance as well as projection. Its fact is folded only into
    // an independent immutable scratch graph. No history, cursor or actual boundary is invented.
    private static PlaygroundDecisionContext Opening(PlaygroundWorld source, string actor) {
        PlaygroundFact opened = PlaygroundInteractionRules.OpenContext(source, actor);
        PlaygroundWorld folded = PlaygroundEngine.Fold(source, new(source.Time, 0), opened);
        PlaygroundWorld scratch = PlaygroundEngine.Rebuild(folded.Business, source.Time);
        return PlaygroundDecisionContext.Create(scratch, actor);
    }

    private static LlmCharacterController Controller(ICompletionClient client,
        Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? feedback = null) =>
        new(client, "offline-fixture-model", context => CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout()),
            feedbackSink: feedback);

    // Includes every current request field. The existing explicit polymorphic projection preserves
    // raw tool-call identities and native action blocks; unsupported schema/message shapes fail closed.
    private static string RequestValue(CompletionRequest request) => JsonSerializer.Serialize(new {
        request.ModelId, request.PromptPrefix.SystemPrompt,
        Contract = GymMessageProjection.Contract(request.PromptPrefix.OutputContract),
        Shared = request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
        Tail = request.TailMessages.Select(GymMessageProjection.Message).ToArray()
    });
    private static CompletionResult Act(string command) => Result(new("act", "provider-call-identity", JsonSerializer.Serialize(new { command })));
    private static CompletionResult Result(RawToolCall call) => new(new ActionMessage([new ActionBlock.ToolCall(call)]),
        new("offline-fixture", "offline-fixture", "offline-fixture-model"));

    private sealed class ImmediateClient(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public List<CompletionRequest> Requests { get; } = [];
        public string Name => "offline-fixture";
        public string ApiSpecId => "offline-fixture";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class HeldClient(CompletionResult response) : ICompletionClient {
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "offline-fixture";
        public string ApiSpecId => "offline-fixture";
        public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Invoked.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return response;
        }
    }

    /// <summary>Runs the ordinary renderer/prompt builder, but withholds the first provider result
    /// from that controller. Therefore no speculative operation/feedback or continuation is created.</summary>
    private sealed class Candidate : ICompletionClient, IAsyncDisposable {
        private readonly CancellationTokenSource _stop = new();
        private readonly ICompletionClient _provider;
        private readonly Task<PlaygroundControllerOperation> _prediction;
        public CompletionRequest? Request { get; private set; }
        public TaskCompletionSource<CompletionResult> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => _provider.Name;
        public string ApiSpecId => _provider.ApiSpecId;
        public Candidate(PlaygroundDecisionContext context, ICompletionClient provider,
            Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? feedback = null) {
            _provider = provider;
            _prediction = Controller(this, feedback).ChooseCommandAsync(context, _stop.Token).AsTask();
        }
        public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Request = request;
            try {
                CompletionResult result = await _provider.StreamCompletionAsync(request, observer, cancellationToken);
                Ready.TrySetResult(result);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("Candidate results must only be delivered through actual adoption.");
            } catch (OperationCanceledException) {
                Ready.TrySetCanceled(cancellationToken);
                throw;
            } catch (Exception error) {
                Ready.TrySetException(error);
                throw;
            }
        }
        public async ValueTask DisposeAsync() {
            await _stop.CancelAsync();
            try { await _prediction; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }

    private sealed class AdoptionClient(Candidate candidate, ICompletionClient fallback) : ICompletionClient {
        private bool _examined;
        public int Adopted { get; private set; }
        public int Discarded { get; private set; }
        public string Name => fallback.Name;
        public string ApiSpecId => fallback.ApiSpecId;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            if (!_examined) {
                _examined = true;
                if (candidate.Request is not null && RequestValue(candidate.Request) == RequestValue(request)) {
                    Adopted++;
                    return candidate.Ready.Task.WaitAsync(cancellationToken);
                }
                Discarded++;
            }
            return fallback.StreamCompletionAsync(request, observer, cancellationToken);
        }
    }

    private sealed class Run : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "predictive-playground-" + Guid.NewGuid().ToString("N"));
        private bool _historyClosed;
        public string Store => Path.Combine(_root, "game");
        public PlaygroundDurableHistory History { get; }
        public List<string> Inputs { get; } = [];
        public List<string> Transitions { get; } = [];
        public List<PlaygroundDecisionBoundary> Boundaries { get; } = [];
        public Run() {
            var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false, characterCount: 2);
            History = PlaygroundDurableHistory.Create(Store, initial.World, initial.Cursor);
        }
        public async Task Execute(ICompletionClient a, ICompletionClient b,
            Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? feedback = null) {
            var recording = new RecordingHistory(History, Transitions);
            var controller = new ActualController(this, Controller(new RecordingClient(a, Inputs), feedback), Controller(new RecordingClient(b, Inputs)));
            var kernel = PlaygroundEngine.CreateKernel(recording, characterController: controller);
            for (int i = 0; i < 4; i++) {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            }
            Assert.Equal(2, Boundaries.Count);
        }
        public string FinalMaterial() => string.Join("\n", History.State.Business.InteractionStates.OrderBy(pair => pair.Key)
            .Select(pair => CharacterContextRenderer.RenderFull(pair.Value, new DecisionPlayLayout())));
        public void CloseHistory() {
            if (_historyClosed) { return; }
            History.Dispose();
            _historyClosed = true;
        }
        public void Dispose() {
            CloseHistory();
            if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
        }
    }

    private sealed class ActualController(Run run, LlmCharacterController a, LlmCharacterController b) : IPlaygroundCharacterController {
        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.InteractionState.ActorId;
            PlaygroundDecisionBoundary boundary = run.History.CaptureDecisionBoundary(actor);
            run.Boundaries.Add(boundary);
            PlaygroundControllerOperation operation = await (actor == "a" ? a : b).ChooseCommandAsync(context, cancellationToken);
            // The experiment host allocates its own repeatable operation record identity, before
            // any operation is submitted. Provider ToolCallId and complete request bytes survive.
            return operation with { OperationRecordId = $"probe-operation-{actor}-{boundary.ContextSequence}-{boundary.OperationSequence}" };
        }
    }

    private sealed class RecordingClient(ICompletionClient inner, List<string> inputs) : ICompletionClient {
        public string Name => inner.Name;
        public string ApiSpecId => inner.ApiSpecId;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            inputs.Add(RequestValue(request));
            return inner.StreamCompletionAsync(request, observer, cancellationToken);
        }
    }

    private sealed class RecordingHistory(PlaygroundDurableHistory inner, List<string> transitions) : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        private static readonly JsonSerializerOptions FactOptions = new() {
            Converters = { new VisibleValueConverter() }
        };
        private string? _preparedTrace;
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            // Prepare diagnostics before either E or S publication, so projection errors cannot
            // be mistaken for a failed write after a successfully committed world state.
            string trace = JsonSerializer.Serialize(new {
                occurrence.CauseKey, occurrence.TargetInstant,
                nextCursor = inner.Cursor.Advance(occurrence.CauseKey, occurrence.TargetInstant),
                Facts = occurrence.Facts.Select(fact => JsonSerializer.SerializeToElement(fact, FactOptions)).ToArray()
            });
            inner.CommitEvent(occurrence);
            _preparedTrace = trace;
        }
        public void CommitState(PlaygroundWorld nextState, KernelCursor nextCursor) {
            string trace = _preparedTrace ?? throw new InvalidOperationException("Missing prepublication diagnostic trace.");
            inner.CommitState(nextState, nextCursor);
            transitions.Add(trace);
            _preparedTrace = null;
        }
    }

    // All stored discriminant and payload fields are retained. Number is a derived convenience
    // accessor which throws for nonnumeric variants; it is reconstructed from Units / Scale.
    private sealed class VisibleValueConverter : JsonConverter<PlaygroundVisibleValue> {
        public override PlaygroundVisibleValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("This diagnostic projection is write-only.");
        public override void Write(Utf8JsonWriter writer, PlaygroundVisibleValue value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new { value.Kind, value.Units, value.Scale, value.Unit, value.Text, value.Boolean }, options);
    }
}

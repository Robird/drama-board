using System.Diagnostics;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.CharacterGym;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Playground;
using DramaBoard.Playground.Completion;

namespace DramaBoard.Playground.Cli;

/// <summary>Run-local, one-step response prefetch. Only the ordinary controller produces operations.</summary>
internal sealed class PlaygroundLookaheadController : IPlaygroundCharacterController, IAsyncDisposable {
    private readonly Func<(PlaygroundWorld World, KernelCursor Cursor)> _snapshot;
    private readonly IReadOnlyDictionary<string, CharacterControlBinding> _bindings;
    private readonly Func<string, CancellationToken, ValueTask<LlmDecisionBinding>> _resolver;
    private readonly LlmControllerOptions _options;
    private readonly Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? _feedback;
    private readonly Action<string, object>? _evidence;
    private readonly string _runId, _branchId;
    private readonly LlmCharacterController _ordinary;
    private readonly List<Task<CompletionResult>> _physical = [];
    private readonly List<CancellationTokenSource> _physicalStops = [];
    private readonly Dictionary<string, LlmDecisionBinding> _readyBindings = new(StringComparer.Ordinal);
    private Candidate? _candidate;
    private Service? _service;
    private Exception? _evidenceError;
    private int _calls, _active;
    private int _inFlight, _peak;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _serviceTicks, _demandTicks;
    private long _adopted, _discarded, _projectionTicks, _comparisonTicks, _waitTicks;
    private bool _disposed;

    public PlaygroundLookaheadController(Func<(PlaygroundWorld World, KernelCursor Cursor)> committedSnapshot,
        IReadOnlyDictionary<string, CharacterControlBinding> bindings,
        Func<string, CancellationToken, ValueTask<LlmDecisionBinding>> resolver, LlmControllerOptions options,
        Action<LlmControllerTrace> actualTrace,
        Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>>? feedbackSink = null,
        Action<string, object>? evidence = null, string runId = "ephemeral", string branchId = "ephemeral") {
        _snapshot = committedSnapshot; _bindings = bindings; _resolver = resolver; _options = options;
        _feedback = feedbackSink; _evidence = evidence; _runId = runId; _branchId = branchId;
        _ordinary = new(async (actor, token) => {
            var binding = await _resolver(actor, token);
            if (_bindings.TryGetValue(actor, out var control) && control is CharacterControlBinding.Llm llm) {
                _readyBindings[llm.ConnectionId] = binding;
            }
            return binding with { Client = new DemandClient(this, binding, _service!) };
        }, Render, options with { MaxCalls = int.MaxValue }, trace => {
            if (trace.Kind == LlmControllerTraceKind.Request && _service is { } service) {
                service.LogicalRequestId = trace.RequestId;
            }
            actualTrace(trace);
        }, feedbackSink);
    }

    public int CallsMade => _calls;
    public object Stats => new { PhysicalCalls = _calls, Adopted = _adopted, Discarded = _discarded,
        EvidenceError = _evidenceError?.GetType().FullName, PeakInFlight = _peak,
        ServiceMilliseconds = Milliseconds(_serviceTicks), DemandMilliseconds = Milliseconds(_demandTicks),
        ProjectionMilliseconds = Milliseconds(_projectionTicks), ComparisonMilliseconds = Milliseconds(_comparisonTicks),
        RemainingResponseWaitMilliseconds = Milliseconds(_waitTicks),
        TimingScope = "Cumulative run durations; service includes rendering and all tool turns; demand includes projection/comparison; remaining wait begins after preparation. Nested durations overlap and must not be summed." };
    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    private static string Render(PlaygroundDecisionContext context) =>
        CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout());

    public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) { throw new InvalidOperationException("Lookahead controller requires sequential invocation."); }
        long started = Stopwatch.GetTimestamp();
        try {
            PruneCompletedPhysical();
            var source = _snapshot();
            _service = new(context, source.World, source.Cursor);
            return await _ordinary.ChooseCommandAsync(context, cancellationToken);
        } finally { _serviceTicks += Stopwatch.GetTimestamp() - started; _service = null; Volatile.Write(ref _active, 0); }
    }

    private async Task<CompletionResult> Demand(Service service, LlmDecisionBinding binding, CompletionRequest request, CancellationToken token) {
        long started = Stopwatch.GetTimestamp();
        try { return await DemandCore(service, binding, request, token); }
        finally { _demandTicks += Stopwatch.GetTimestamp() - started; }
    }
    private async Task<CompletionResult> DemandCore(Service service, LlmDecisionBinding binding, CompletionRequest request, CancellationToken token) {
        Healthy();
        bool first = service.Turn++ == 0;
        if (first && _candidate is { } candidate && !candidate.Examined) {
            candidate.Examined = true;
            long compared = Stopwatch.GetTimestamp();
            bool targetMatches = candidate.Target == Identity(service.Context);
            bool bindingMatches = SameBinding(candidate.Binding, binding);
            bool comparable = TryInput(request, out string? actual);
            bool same = targetMatches && bindingMatches && comparable && candidate.Input == actual;
            _comparisonTicks += Stopwatch.GetTimestamp() - compared;
            if (same) {
                _candidate = null;
                _adopted++;
                Emit("candidate-adopted", new { candidate.PhysicalId, Target = candidate.Target });
                return await Deliver(candidate.Response, candidate.PhysicalId, service, token);
            }
            _discarded++;
            Emit("candidate-discarded", new { candidate.PhysicalId, Target = candidate.Target,
                ActualTarget = Identity(service.Context), service.LogicalRequestId,
                Reason = !targetMatches ? "target-changed" : !bindingMatches ? "binding-changed" : !comparable ? "unsupported-input" : "input-changed" });
            if (!candidate.Response.IsCompleted) { candidate.Stop.Cancel(); }
            // An uncooperative cancelled request continues to occupy the extra slot.
        }
        var physical = Invoke(binding, request, "demand", service.Context.InteractionState.ActorId, token);
        return await Deliver(physical.Response, physical.Id, service, token, prepare: first);
    }

    private async Task<CompletionResult> Deliver(Task<CompletionResult> response, string id, Service service, CancellationToken token, bool prepare = false) {
        Emit("actual-dispatch", new { service.LogicalRequestId, PhysicalId = id, Target = Identity(service.Context), Turn = service.Turn });
        if (prepare) { await Prepare(service, token); }
        long started = Stopwatch.GetTimestamp();
        try {
            var result = await response.WaitAsync(token);
            Emit("actual-delivery", new { service.LogicalRequestId, PhysicalId = id, Target = Identity(service.Context), Turn = service.Turn });
            return result;
        } finally { _waitTicks += Stopwatch.GetTimestamp() - started; }
    }

    private async Task Prepare(Service service, CancellationToken token) {
        if (_candidate is { } previous) {
            if (!previous.Response.IsCompleted) { return; }
            await Drain(previous);
            _candidate = null;
        }
        if (_calls >= _options.MaxCalls || _disposed) { return; }
        long started = Stopwatch.GetTimestamp();
        try {
            var projection = await PlaygroundLookaheadProjection.ProjectAsync(service.World, service.Cursor,
                service.Context.InteractionState.ActorId, _bindings, token);
            Emit("projectionstop", new { projection.StopReason, projection.Steps, projection.Elapsed, projection.PredictedCommand,
                SourceCursor = service.Cursor, Target = projection.Target is { } projected ? Identity(projected) : null });
            if (projection.Target is not { } target) { return; }
            if (!_bindings.TryGetValue(target.InteractionState.ActorId, out var targetControl) ||
                targetControl is not CharacterControlBinding.Llm targetLlm ||
                !_readyBindings.TryGetValue(targetLlm.ConnectionId, out var binding)) {
                Emit("projection-skip", new { Reason = "binding-not-ready", Target = Identity(target) });
                return;
            }
            var capture = new CaptureClient(binding.Client);
            var controller = new LlmCharacterController(capture, binding.ModelId, Render,
                _options with { MaxCalls = int.MaxValue }, feedbackSink: _feedback is null ? null : CaptureFeedback);
            try { await controller.ChooseCommandAsync(target, token); }
            catch (CapturedException) { }
            if (capture.Request is not { } request || !TryInput(request, out string? input) || _calls >= _options.MaxCalls) { return; }
            var physical = Invoke(binding, request, "candidate", target.InteractionState.ActorId, token);
            _candidate = new(Identity(target), binding, input!, physical.Id, physical.Response, physical.Stop);
            Emit("candidate-created", new { physical.Id, Target = _candidate.Target, FullInput = input });
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) {
            if (_evidenceError is not null) { throw; }
            Emit("projection-failure", new { Error = error.GetType().FullName });
        } finally { _projectionTicks += Stopwatch.GetTimestamp() - started; }
    }
    private static ValueTask<string> CaptureFeedback(LlmFeedbackSubmission _, CancellationToken __) => throw new InvalidOperationException("Capture cannot execute feedback.");

    private (string Id, Task<CompletionResult> Response, CancellationTokenSource Stop) Invoke(LlmDecisionBinding binding,
        CompletionRequest request, string purpose, string actor, CancellationToken token) {
        PruneCompletedPhysical();
        Healthy(); token.ThrowIfCancellationRequested();
        if (_calls >= _options.MaxCalls) { throw new LlmControllerPausedException("call-budget-exhausted", _calls); }
        string id = $"{_runId}-physical-{_calls + 1}";
        Emit("physical-request", new { PhysicalId = id, Purpose = purpose, ActorId = actor, binding.ConnectionId,
            binding.ModelId, binding.Client.ApiSpecId, FullInput = AuditInput(request) });
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _calls++;
        // Calling this async method starts the provider before projection preparation begins.
        Task<CompletionResult> response = Physical(binding, request, id, stop.Token);
        _physical.Add(response); _physicalStops.Add(stop);
        return (id, response, stop);
    }
    private async Task<CompletionResult> Physical(LlmDecisionBinding binding, CompletionRequest request, string id, CancellationToken token) {
        int inFlight = Interlocked.Increment(ref _inFlight);
        int peak;
        do { peak = _peak; } while (inFlight > peak && Interlocked.CompareExchange(ref _peak, inFlight, peak) != peak);
        try {
            CompletionResult result;
            try { result = await binding.Client.StreamCompletionAsync(request, null, token); }
            catch (Exception error) {
                try { Emit("failure", new { PhysicalId = id, Error = error.GetType().FullName,
                    Failure = (error as CompletionFailureException)?.Failure,
                    RemoteOutcomeUnknown = error is not CompletionRequestRejectedException }); }
                catch (Exception diagnostic) { throw new AggregateException("Provider and evidence failures.", error, diagnostic); }
                throw;
            }
            object outcome;
            try {
                outcome = new { PhysicalId = id, Result = new { Message = GymMessageProjection.Message(result.Message),
                    result.Invocation, result.Termination, result.Usage, result.Failure, result.Errors } };
            } catch (Exception error) {
                // A result arrived, but its evidence could not be represented. Do not silently
                // classify this as a discarded provider failure and admit more requests.
                Interlocked.CompareExchange(ref _evidenceError, error, null);
                throw;
            }
            Emit("physical-completed", outcome);
            return result;
        } finally { Interlocked.Decrement(ref _inFlight); }
    }
    private TargetIdentity Identity(PlaygroundDecisionContext context) => new(_runId, _branchId,
        context.InteractionState.ActorId, context.Context.ContextSequence, context.Context.LastOperationSequence, context.Observation.Time.Ticks);
    private static bool SameBinding(LlmDecisionBinding left, LlmDecisionBinding right) =>
        ReferenceEquals(left.Client, right.Client) && left.ConnectionId == right.ConnectionId && left.ModelId == right.ModelId && left.Client.ApiSpecId == right.Client.ApiSpecId;
    private void Healthy() {
        if (_evidenceError is { } error) { throw new InvalidOperationException("Lookahead evidence failed; physical admission retired.", error); }
    }
    private void Emit(string kind, object payload) {
        Healthy();
        try { _evidence?.Invoke("lookahead-" + kind, new { ElapsedMilliseconds = _clock.Elapsed.TotalMilliseconds,
            InFlight = Volatile.Read(ref _inFlight), PeakInFlight = Volatile.Read(ref _peak), Detail = payload }); }
        catch (Exception error) { Interlocked.CompareExchange(ref _evidenceError, error, null); throw; }
    }
    private async Task Drain(Candidate candidate) {
        string? errorType = null;
        try { await candidate.Response; } catch (Exception error) { errorType = error.GetType().FullName; }
        Emit("candidate-drained", new { candidate.PhysicalId, ErrorType = errorType });
    }
    private void PruneCompletedPhysical() {
        // Only the serialized service thread mutates these paired lists. A candidate retains its
        // response and cancellation source until adoption/discard/drain has released that identity.
        for (int index = _physical.Count - 1; index >= 0; index--) {
            Task<CompletionResult> response = _physical[index];
            if (!response.IsCompleted || ReferenceEquals(response, _candidate?.Response)) { continue; }
            try { response.GetAwaiter().GetResult(); } catch { }
            // Physical already recorded provider failures; diagnostic failures remain in
            // _evidenceError and retire future admission. Observation never refunds _calls.
            _physicalStops[index].Dispose();
            _physicalStops.RemoveAt(index);
            _physical.RemoveAt(index);
        }
    }
    public async ValueTask DisposeAsync() {
        if (_disposed) { return; }
        _disposed = true;
        var errors = new List<Exception>();
        // Completed providers may still have registered callbacks. Cancel only requests whose
        // response has not yet reached a terminal state.
        for (int index = 0; index < _physical.Count; index++) {
            if (_physical[index].IsCompleted) { continue; }
            try { await _physicalStops[index].CancelAsync(); } catch (Exception error) { errors.Add(error); }
        }
        foreach (var task in _physical) { try { await task; } catch { } }
        if (_candidate is { } candidate) { try { await Drain(candidate); } catch (Exception error) { errors.Add(error); } }
        if (_evidenceError is { } evidenceError && !errors.Contains(evidenceError)) { errors.Add(evidenceError); }
        foreach (var stop in _physicalStops) { stop.Dispose(); }
        _physicalStops.Clear();
        _physical.Clear();
        _candidate = null;
        if (errors.Count == 0 && _calls != 0) {
            try { Emit("run-summary", new { Stats, AllPhysicalTasksObserved = true }); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) { throw new AggregateException("Lookahead cleanup failures.", errors); }
    }

    internal static bool TryInput(CompletionRequest request, out string? input) {
        try { return TryInputCore(request, out input); }
        catch (NotSupportedException) { input = null; return false; }
    }
    private static bool TryInputCore(CompletionRequest request, out string? input) {
        input = null;
        if (!request.TailMessages.IsEmpty || request.PromptPrefix.SharedContextMessages.Any(m => m.GetType() != typeof(ObservationMessage))) { return false; }
        foreach (var tool in request.PromptPrefix.OutputContract.Tools) {
            if (tool.InputKind != ToolInputKind.JsonObject || tool.InputSchema is not ToolSchema.Object schema ||
                schema.Properties.Any(p => p.Schema is not ToolSchema.Value)) { return false; }
        }
        var contract = request.PromptPrefix.OutputContract;
        input = JsonSerializer.Serialize(new {
            request.ModelId, request.PromptPrefix.SystemPrompt,
            Contract = new { contract.ToolChoice.Kind, contract.ToolChoice.RequiredToolName,
                contract.AllowParallelToolCalls, contract.SemanticFingerprint, contract.IsProviderDefault,
                Tools = contract.Tools.Select(tool => {
                    var schema = (ToolSchema.Object)tool.InputSchema;
                    return new { tool.Name, tool.Description, tool.InputKind, tool.TextFormat,
                        InputSchema = new { schema.Description, schema.Example, schema.AdditionalProperties,
                            schema.IsNullable, Properties = schema.Properties.Select(p => {
                                var value = (ToolSchema.Value)p.Schema;
                                return new { p.Name, p.IsRequired, Schema = new { value.Description, value.Example,
                                    value.ValueKind, value.IsNullable, DefaultPresent = value.Default.HasValue,
                                    Default = Scalar(value.Default?.Value), value.StringEnumValues, value.MinLength,
                                    value.MaxLength, value.Pattern, Minimum = Scalar(value.Minimum), Maximum = Scalar(value.Maximum) } };
                            }).ToArray() } };
                }).ToArray() },
            Shared = request.PromptPrefix.SharedContextMessages.Cast<ObservationMessage>().Select(m => new { m.Kind, m.Content }).ToArray(),
            Tail = Array.Empty<object>()
        });
        return true;
    }
    private static object? Scalar(object? value) => value is null ? null : new {
        Type = value.GetType().FullName,
        Value = value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            ? value : throw new NotSupportedException("Unknown scalar schema value.")
    };
    private static string AuditInput(CompletionRequest request) {
        try {
            return JsonSerializer.Serialize(new {
                request.ModelId, request.PromptPrefix.SystemPrompt,
                Contract = GymMessageProjection.Contract(request.PromptPrefix.OutputContract),
                Shared = request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
                Tail = request.TailMessages.Select(GymMessageProjection.Message).ToArray()
            });
        } catch (NotSupportedException) {
            // Audit fallback is never used by TryInput or cache admission. A future message/schema
            // shape must remain usable on the ordinary demand path while prefetch fails closed.
            return JsonSerializer.Serialize(new { UnsupportedAuditShape = true, Request = request });
        }
    }

    private sealed record TargetIdentity(string RunId, string BranchId, string ActorId, long ContextSequence, long OperationSequence, long TimeTicks);
    private sealed record Service(PlaygroundDecisionContext Context, PlaygroundWorld World, KernelCursor Cursor) {
        public int Turn;
        public string? LogicalRequestId;
    }
    private sealed record Candidate(TargetIdentity Target, LlmDecisionBinding Binding, string Input, string PhysicalId,
        Task<CompletionResult> Response, CancellationTokenSource Stop) { public bool Examined; }
    private sealed class DemandClient(PlaygroundLookaheadController owner, LlmDecisionBinding binding, Service service) : ICompletionClient {
        public string Name => binding.Client.Name;
        public string ApiSpecId => binding.Client.ApiSpecId;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) =>
            owner.Demand(service, binding, request, cancellationToken);
    }
    private sealed class CapturedException : Exception { }
    private sealed class CaptureClient(ICompletionClient client) : ICompletionClient {
        public string Name => client.Name;
        public string ApiSpecId => client.ApiSpecId;
        public CompletionRequest? Request { get; private set; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Request = request;
            throw new CapturedException();
        }
    }
}

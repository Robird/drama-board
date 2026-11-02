using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;
using DramaBoard.Playground.Completion;

namespace DramaBoard.PlaygroundParallelism;

internal sealed record ConnectionIdentity(string ConnectionId, string ApiSpecId, string ModelId, string ConfigurationHash);

/// <summary>All physical requests, including corrections and feedback continuation, share these slots and admission.</summary>
internal sealed class ProviderDispatcher(ProbeEvidence evidence, OnlineBudget? budget, int maxInFlight) : IDisposable {
    private readonly SemaphoreSlim _slots = new(maxInFlight, maxInFlight);
    private int _calls;
    private int _inFlight;
    private int _peak;
    public int Calls => Volatile.Read(ref _calls);
    public int PeakInFlight => Volatile.Read(ref _peak);
    public async Task<CompletionResult> Invoke(string actor, string purpose, LlmDecisionBinding binding,
        ConnectionIdentity identity, CompletionRequest request, CancellationToken cancellationToken) {
        RequireHealthyWriters();
        string input = ProbeEvidence.Request(request);
        string hash = ProbeEvidence.Hash(input);
        evidence.Write("provider-queued", new { ActorId = actor, Purpose = purpose, InputHash = hash, identity });
        await _slots.WaitAsync(cancellationToken);
        try {
            cancellationToken.ThrowIfCancellationRequested();
            RequireHealthyWriters();
            string requestId = budget?.BeforeInvoke(actor, hash) ?? $"fixture-request-{Interlocked.Increment(ref _calls)}";
            if (budget is not null) { Interlocked.Increment(ref _calls); }
            int inFlight = Interlocked.Increment(ref _inFlight);
            int oldPeak;
            do { oldPeak = _peak; } while (inFlight > oldPeak && Interlocked.CompareExchange(ref _peak, inFlight, oldPeak) != oldPeak);
            try {
                evidence.Write("provider-invoked", new { RequestId = requestId, ActorId = actor, Purpose = purpose, InputHash = hash, Input = input, identity, InFlight = inFlight });
                CompletionResult result;
                try {
                    result = await binding.Client.StreamCompletionAsync(request, observer: null, cancellationToken);
                } catch (Exception error) {
                    string outcome = error is OperationCanceledException ? "cancelled-remote-outcome-unknown" : "failed";
                    try {
                        budget?.Finished(requestId, outcome, usage: null);
                        evidence.Write("provider-finished", new { RequestId = requestId, Outcome = outcome, ErrorType = error.GetType().FullName, Usage = (object?)null });
                    } catch (Exception diagnosticError) {
                        throw new AggregateException("Provider and diagnostic failures are both retained.", error, diagnosticError);
                    }
                    throw;
                }
                // Diagnostic failure after a provider result is not reclassified as another
                // provider failure and cannot produce a duplicate terminal budget record.
                budget?.Finished(requestId, result.Termination.Kind.ToString(), result.Usage);
                evidence.Write("provider-finished", new { RequestId = requestId, Outcome = result.Termination.Kind.ToString(), Result = ProbeEvidence.Result(result) });
                return result;
            } finally { Interlocked.Decrement(ref _inFlight); }
        } finally { _slots.Release(); }
    }
    private void RequireHealthyWriters() {
        if (budget?.IsFaulted == true || evidence.IsFaulted) { throw new InvalidOperationException("Physical request admission retired after diagnostic failure."); }
    }
    public void Dispose() => _slots.Dispose();
}

/// <summary>The projected ordinary controller never receives the physical result. It cannot execute
/// feedback, create an operation or start a second model turn.</summary>
internal sealed class Candidate : ICompletionClient, IAsyncDisposable {
    private readonly CancellationTokenSource _controllerStop;
    private readonly CancellationTokenSource _providerStop;
    private readonly ProviderDispatcher _dispatcher;
    private readonly LlmDecisionBinding _binding;
    private readonly ProbeEvidence _evidence;
    private readonly Task<PlaygroundControllerOperation> _controllerTask;
    public string Actor { get; }
    public string CandidateId { get; }
    public string SourceStateCommitId { get; }
    public string SourceBranchId { get; }
    public long ContextSequence { get; }
    public ConnectionIdentity Identity { get; }
    public TaskCompletionSource<CompletionRequest> Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<CompletionResult>? Response { get; private set; }
    public string Name => _binding.Client.Name;
    public string ApiSpecId => _binding.Client.ApiSpecId;
    public bool Examined { get; set; }

    public Candidate(PlaygroundDecisionContext projected, string sourceStateCommitId, string sourceBranchId, LlmDecisionBinding binding,
        ConnectionIdentity identity, ProviderDispatcher dispatcher, ProbeEvidence evidence, CancellationToken hostToken) {
        Actor = projected.InteractionState.ActorId;
        ContextSequence = projected.Context.ContextSequence;
        CandidateId = $"candidate-{Actor}-{ContextSequence}";
        SourceStateCommitId = sourceStateCommitId;
        SourceBranchId = sourceBranchId;
        Identity = identity;
        _binding = binding;
        _dispatcher = dispatcher;
        _evidence = evidence;
        _controllerStop = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        _providerStop = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        _evidence.Write("candidate-created", new { CandidateId, Actor, SourceStateCommitId, SourceBranchId,
            Target = new { ContextSequence, TimeTicks = projected.Observation.Time.Ticks }, identity });
        var controller = ControllerFactory.Create(this, binding, (_, _) =>
            throw new InvalidOperationException("Speculative feedback sentinel must never execute."));
        _controllerTask = controller.ChooseCommandAsync(projected, _controllerStop.Token).AsTask();
    }
    public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default) {
        Response = _dispatcher.Invoke(Actor, "candidate", _binding, Identity, request, _providerStop.Token);
        Captured.TrySetResult(request);
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Projected controllers may never receive a result.");
    }
    public void CancelPhysicalRequest() => _providerStop.Cancel();
    public async ValueTask DisposeAsync() {
        var cleanupErrors = new List<Exception>();
        try {
            try { await _controllerStop.CancelAsync(); } catch (Exception error) { cleanupErrors.Add(error); }
            try { await _providerStop.CancelAsync(); } catch (Exception error) { cleanupErrors.Add(error); }
            try { await _controllerTask; } catch (OperationCanceledException) when (_controllerStop.IsCancellationRequested) { }
            catch (Exception error) { cleanupErrors.Add(error); }
            if (Response is not null) {
                try { await Response; } catch (Exception error) {
                    // The provider dispatcher recorded the actual failure. An unused candidate error
                    // does not become a failure in the unrelated actual character service.
                    try { _evidence.Write("candidate-drained", new { CandidateId, ErrorType = error.GetType().FullName }); }
                    catch (Exception diagnosticError) { cleanupErrors.Add(diagnosticError); }
                }
            }
        } finally {
            _controllerStop.Dispose();
            _providerStop.Dispose();
        }
        if (cleanupErrors.Count != 0) { throw new AggregateException("Candidate cleanup failures retained after both tasks were drained.", cleanupErrors); }
    }
}

internal sealed class DemandGate(string actor, LlmDecisionBinding binding, ConnectionIdentity identity,
    Candidate? candidate, ProviderDispatcher dispatcher, ProbeEvidence evidence, List<string> actualInputSequence) : ICompletionClient {
    public PlaygroundDecisionBoundary? Boundary { get; set; }
    public List<string> ActualInputs { get; } = [];
    public int Adopted { get; private set; }
    public int Discarded { get; private set; }
    public string Name => binding.Client.Name;
    public string ApiSpecId => binding.Client.ApiSpecId;
    public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default) {
        string actual = ProbeEvidence.Request(request);
        PlaygroundDecisionBoundary boundary = Boundary ?? throw new InvalidOperationException("Actual request requires a real decision boundary.");
        ActualInputs.Add(actual);
        actualInputSequence.Add(actual);
        evidence.Write("actual-request", new { ActorId = actor, Boundary, InputHash = ProbeEvidence.Hash(actual), Input = actual, identity });
        if (candidate is not null && !candidate.Examined) {
            candidate.Examined = true;
            CompletionRequest projected = await candidate.Captured.Task.WaitAsync(cancellationToken);
            string expected = ProbeEvidence.Request(projected);
            bool targetMatches = boundary.ActorId == candidate.Actor && boundary.BranchId == candidate.SourceBranchId &&
                boundary.ContextSequence == candidate.ContextSequence && boundary.OperationSequence == 0;
            bool same = targetMatches && identity == candidate.Identity && expected == actual;
            evidence.Write(same ? "candidate-input-validated" : "candidate-discarded", new {
                candidate.CandidateId, candidate.SourceStateCommitId, Boundary,
                Reason = same ? "complete-input-and-binding-match" : !targetMatches ? "target-changed" : identity != candidate.Identity ? "connection-changed" : "input-changed",
                ExpectedHash = ProbeEvidence.Hash(expected), ActualHash = ProbeEvidence.Hash(actual)
            });
            if (same) {
                CompletionResult result = await candidate.Response!.WaitAsync(cancellationToken);
                Adopted++;
                evidence.Write("candidate-response-delivered", new { candidate.CandidateId, boundary, result.Termination.Kind });
                return result;
            }
            Discarded++;
            candidate.CancelPhysicalRequest();
        }
        return await dispatcher.Invoke(actor, "demand-or-continuation", binding, identity, request, cancellationToken);
    }
}

internal static class ControllerFactory {
    public static LlmCharacterController Create(ICompletionClient client, LlmDecisionBinding binding,
        Func<LlmFeedbackSubmission, CancellationToken, ValueTask<string>> feedback) =>
        new((_, _) => ValueTask.FromResult(new LlmDecisionBinding(client, binding.ModelId, binding.ConnectionId)),
            context => CharacterContextRenderer.RenderFull(context.InteractionState, new DecisionPlayLayout())
                + "\n实验任务：请只调用 act 工具，提交 handoff 研究记录，作为本次短实验的行动。请勿重复已有操作。\n",
            new LlmControllerOptions { MaxCalls = 8 }, feedbackSink: feedback);
}

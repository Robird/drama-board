using DramaBoard.CharacterGym;
using DramaBoard.Playground.Cli;
using System.Security.Cryptography;
using System.Text.Json;
using DramaBoard.Playground;
using DramaBoard.Playground.Completion;

/// <summary>Host evidence only. Neither feedback nor recording advances the game.</summary>
internal sealed class GymSession : IDisposable {
    private readonly GymJournal _journal;
    private readonly PlaygroundDurableHistory _history;
    private readonly Dictionary<string, GymRecord> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CharacterContext?> _contexts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recordedOperations;
    private bool _stopped;
    private string _lastState;
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string BranchId { get; }

    public static string JournalPath(string store) => Path.Combine(store, "gym", "events.jsonl");
    public static string OperationsPath(string store, PlaygroundDurableHistory history) {
        PlaygroundBranchInfo branch = history.CurrentBranch;
        if (branch.ForkedFromCommitId is null) { return Path.Combine(store, "operations.jsonl"); }
        string directory = Path.Combine(store, "branch-operations");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, branch.BranchId + ".jsonl");
    }

    public GymSession(string store, PlaygroundDurableHistory history, PlaygroundHostOptions options, IReadOnlyDictionary<string, CharacterControlBinding> bindings, string logPath, bool recovered) {
        _history = history;
        BranchId = history.CurrentBranch.BranchId;
        _lastState = PlaygroundDurableHistory.FormatCommitId(history.CompletedStateCommitId);
        string path = JournalPath(store);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _journal = GymJournal.Open(path);
        _recordedOperations = _journal.Records.Where(record => record.BranchId == BranchId && record.Kind == "operation")
            .Select(record => record.Payload.GetProperty("OperationRecordId").GetString()!).ToHashSet(StringComparer.Ordinal);
        try {
            Append("run-start", null, new {
                StoreId = _journal.StoreId, StateCommitId = history.CurrentBranch.CompletedStateCommitId,
                history.CurrentBranch.ForkedFromCommitId, GameRevision = PlaygroundGameRevision.Current,
                RecoveredPending = recovered, AuditPath = Path.GetFullPath(logPath),
                options.MaxCalls, options.MaxFailures, options.MaxContextCharacters,
                options.Until, options.MaxSteps,
                Content = new { history.State.Definition.ContentId, history.State.Definition.Fingerprint },
                Scenario = history.State.Expedition!.ScenarioId, CharacterCount = history.State.Characters.Count,
                Actors = history.State.Characters.Select(character => new { ActorId = character.Id,
                    Controller = bindings[character.Id] is CharacterControlBinding.HumanConsole ? "Human" : "LLM",
                    ConnectionId = (bindings[character.Id] as CharacterControlBinding.Llm)?.ConnectionId }).ToArray(),
                FeedbackEnabled = options.Feedback, Presentation = "decision/full/character-context",
                Builds = new[] { typeof(GymSession).Assembly, typeof(PlaygroundWorld).Assembly, typeof(LlmCharacterController).Assembly,
                    typeof(DramaBoard.CharacterControl.Completion.LlmDecisionLoop).Assembly, typeof(GymJournal).Assembly,
                    typeof(DramaBoard.Goals.GoalSet).Assembly }
                    .Select(assembly => new { Name = assembly.GetName().Name, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray(),
                TailGapBytes = _journal.DiscardedTailBytes
            });
        } catch { _journal.Dispose(); throw; }
    }

    private GymRecord Append(string kind, string? actor, object payload) => _journal.Append(kind, RunId, BranchId, actor, payload);

    public void Trace(LlmControllerTrace trace) {
        string requestId = trace.RequestId ?? throw new InvalidOperationException("Gym requires a stable request identity.");
        if (trace.Kind == LlmControllerTraceKind.Request) {
            PlaygroundDecisionBoundary boundary = _history.CaptureDecisionBoundary(trace.CharacterId);
            var record = Append("request", trace.CharacterId, new {
                RequestId = requestId, trace.CallNumber, trace.ContextSequence, Boundary = boundary,
                trace.ConnectionId, trace.ApiSpecId, trace.Request.ModelId, trace.Request.PromptPrefix.SystemPrompt,
                OutputContract = GymMessageProjection.Contract(trace.Request.PromptPrefix.OutputContract),
                Shared = trace.Request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
                Tail = trace.Request.TailMessages.Select(GymMessageProjection.Message).ToArray()
            });
            _requests.Add(requestId, record);
        } else if (trace.Kind == LlmControllerTraceKind.Feedback) {
            Append("feedback-receipt", trace.CharacterId, new { RequestId = requestId, trace.FeedbackId,
                Status = "saved; delivery is evidenced by the next request" });
        } else {
            Append("response", trace.CharacterId, new { RequestId = requestId, trace.CallNumber, trace.ContextSequence,
                trace.ConnectionId, trace.ApiSpecId, trace.ModelId,
                Stage = trace.Kind.ToString(), trace.ProtocolError, trace.Operation,
                Invocation = trace.Result?.Invocation, Termination = trace.Result?.Termination, Usage = trace.Result?.Usage,
                Blocks = trace.Result?.Message.Blocks.Select(block => GymMessageProjection.Runtime(block)).ToArray() });
        }
    }

    public ValueTask<string> SaveFeedback(LlmFeedbackSubmission submission, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_requests.TryGetValue(submission.RequestId, out GymRecord? request) || request.ActorId != submission.CharacterId ||
            request.Payload.GetProperty("ContextSequence").GetInt64() != submission.ContextSequence) {
            throw new InvalidOperationException("Feedback has no matching captured request.");
        }
        var existing = _journal.Records.SingleOrDefault(record => record.Kind == "feedback" &&
            record.Payload.GetProperty("RequestId").GetString() == submission.RequestId &&
            record.Payload.GetProperty("Submission").GetProperty("ToolCallId").GetString() == submission.ToolCallId);
        if (existing is not null) {
            if (existing.Payload.GetProperty("Submission").Deserialize<LlmFeedbackSubmission>() != submission) {
                throw new InvalidOperationException("Feedback identity was reused with different contents.");
            }
            return ValueTask.FromResult(existing.Payload.GetProperty("FeedbackId").GetString()!);
        }
        string id = Guid.NewGuid().ToString("N");
        Append("feedback", submission.CharacterId, new { FeedbackId = id, submission.RequestId,
            RequestSequence = request.Sequence, Boundary = request.Payload.GetProperty("Boundary"), Submission = submission });
        return ValueTask.FromResult(id);
    }

    public void CaptureCommitted(PlaygroundWorld world, PlaygroundOperationJournal operations) {
        string state = PlaygroundDurableHistory.FormatCommitId(_history.CompletedStateCommitId);
        _lastState = state;
        foreach (PlaygroundOperationEntry entry in operations.Entries.Where(entry => entry.Completed is not null)) {
            if (_recordedOperations.Contains(entry.Received.OperationRecordId)) { continue; }
            PlaygroundOperationResult completed = entry.Completed!;
            Append("operation", entry.Received.ActorId, new { entry.Received.OperationRecordId,
                Received = entry.Received, Result = new {
                    completed.ToolCallId, Status = completed.Status.ToString(), completed.Result,
                    completed.WorldVersion, completed.DomainEvent, completed.ExactResult
                }, StateCommitId = state,
                Association = "operation identity is authoritative; state is recording boundary, not necessarily original result commit" });
            _recordedOperations.Add(entry.Received.OperationRecordId);
        }
        foreach (CharacterInteractionState interaction in world.Business.InteractionStates.Values) {
            if (interaction.CurrentContext is not { } context ||
                _contexts.TryGetValue(interaction.ActorId, out var old) && ReferenceEquals(old, context)) { continue; }
            Append(context.Status == CharacterContextStatus.Terminal ? "terminal" : "perception", interaction.ActorId,
                new { StateCommitId = state, context.ContextSequence,
                    Delivery = "retained character material; not evidence of a model request or human screen",
                    Material = CharacterContextRenderer.RenderFull(interaction, new DecisionPlayLayout()) });
            _contexts[interaction.ActorId] = context;
        }
    }

    public void Stop(string reason) {
        if (_stopped || _journal.IsFaulted) { return; }
        Append("run-end", null, new { Reason = reason, StateCommitId = _lastState });
        _stopped = true;
    }

    public void Dispose() {
        try { Stop("host-scope-ended"); }
        finally { _journal.Dispose(); }
    }
}

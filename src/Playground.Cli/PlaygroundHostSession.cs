using System.Security.Cryptography;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;
using DramaBoard.Playground.Completion;

/// <summary>One host run's semantic evidence. The borrowed journal owns all durable writes.</summary>
internal sealed class PlaygroundHostSession {
    private readonly PlaygroundHostJournal _journal;
    private readonly PlaygroundDurableHistory? _history;
    private readonly Dictionary<string, CharacterContext> _contexts = new(StringComparer.Ordinal);
    private bool _stopped;

    public string RunId { get; }
    public string BranchId { get; }
    public bool DetailedRecording { get; }

    public PlaygroundHostSession(PlaygroundHostJournal journal, PlaygroundDurableHistory? history, string runId,
        bool detailedRecording) {
        _journal = journal;
        _history = history;
        RunId = runId;
        BranchId = history?.CurrentBranch.BranchId ?? "ephemeral";
        DetailedRecording = detailedRecording;
    }

    public void Start(PlaygroundWorld world, PlaygroundHostOptions options,
        IReadOnlyDictionary<string, CharacterControlBinding> bindings, string? auditPath, string? store) {
        string? state = StateCommitId();
        Append("run-start", null, null, new {
            JournalId = _journal.JournalId, StateCommitId = state,
            ForkedFromCommitId = _history?.CurrentBranch.ForkedFromCommitId,
            DetailedRecording, AuditPath = LocationHint(auditPath, store),
            Coverage = "Recording starts at this completed State; earlier external operations and detailed history are not inferred.",
            GameRevision = PlaygroundGameRevision.Current,
            options.MaxCalls, options.MaxFailures, options.MaxContextCharacters, options.Until, options.MaxSteps,
            Home = options.HomePath, Store = store,
            Version = new { world.Time.Ticks, LineageId = _history?.Cursor.Version.LineageId,
                TransitionCount = _history?.Cursor.Version.TransitionCount },
            Content = new { world.Definition.ContentId, world.Definition.Fingerprint },
            Scenario = world.Expedition!.ScenarioId, CharacterCount = world.Characters.Count,
            Actors = world.Characters.Select(character => new { ActorId = character.Id,
                Controller = bindings[character.Id] is CharacterControlBinding.HumanConsole ? "Human" : "LLM",
                ConnectionId = (bindings[character.Id] as CharacterControlBinding.Llm)?.ConnectionId }).ToArray(),
            Presentation = "decision/full/character-context",
            Builds = new[] { typeof(PlaygroundHostSession).Assembly, typeof(PlaygroundWorld).Assembly,
                typeof(LlmCharacterController).Assembly, typeof(LlmDecisionLoop).Assembly,
                typeof(DramaBoard.CharacterGym.GymMessageProjection).Assembly,
                typeof(DramaBoard.Goals.GoalSet).Assembly }.Distinct()
                .Select(assembly => new { Name = assembly.GetName().Name,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray(),
            TailGapBytes = _journal.DiscardedTailBytes
        });
    }

    public void CaptureCommitted(PlaygroundWorld world) {
        if (!DetailedRecording) { return; }
        string? state = StateCommitId();
        foreach (CharacterInteractionState interaction in world.Business.InteractionStates.Values) {
            if (interaction.CurrentContext is not { } context ||
                _contexts.TryGetValue(interaction.ActorId, out CharacterContext? previous) && ReferenceEquals(previous, context)) {
                continue;
            }
            Append("character-material", interaction.ActorId, context.ContextSequence,
                CharacterHistoryProjection.Capture(interaction, state, _history?.Cursor.Version.LineageId,
                    _history?.Cursor.Version.TransitionCount, world.Time.Ticks));
            _contexts[interaction.ActorId] = context;
        }
    }

    public void Trace(LlmControllerTrace trace, long? rawRecordSequence) {
        if (!DetailedRecording) { return; }
        if (trace.Kind == LlmControllerTraceKind.Feedback) {
            FeedbackSaved(trace.CharacterId, trace.ContextSequence,
                trace.FeedbackId ?? throw new InvalidOperationException("A saved feedback trace has no receipt identity."), trace.RequestId);
        } else if (trace.Kind == LlmControllerTraceKind.Request) {
            PlaygroundDecisionBoundary? boundary = _history?.CaptureDecisionBoundary(trace.CharacterId);
            if (boundary is not null && boundary.ContextSequence != trace.ContextSequence) {
                throw new InvalidOperationException("The model request does not match the current decision boundary.");
            }
            Append("model-request", trace.CharacterId, trace.ContextSequence,
                ModelHistoryProjection.Request(trace, boundary, rawRecordSequence));
        } else {
            Append(trace.Kind == LlmControllerTraceKind.Response ? "model-response" : "model-failure",
                trace.CharacterId, trace.ContextSequence, ModelHistoryProjection.Outcome(trace, rawRecordSequence));
        }
    }

    public void FeedbackSaved(string actorId, long contextSequence, string feedbackId, string? requestId = null) {
        if (!DetailedRecording) { return; }
        Append("feedback-saved", actorId, contextSequence, new { FeedbackId = feedbackId, RequestId = requestId,
            Status = "saved; inclusion in a subsequent constructed request is separate evidence" });
    }

    public void Stop(string reason) {
        if (_stopped || _journal.IsFaulted) { return; }
        // A failed world publication can retire its adapter. The last completed S is then unknown to
        // this instance; do not ask a faulted adapter to produce a fictitious clean shutdown boundary.
        string? state = _history?.IsFaulted == true ? null : StateCommitId();
        Append("run-end", null, null, new { Reason = reason, StateCommitId = state });
        _stopped = true;
    }

    private string? StateCommitId() => _history is null ? null
        : PlaygroundDurableHistory.FormatCommitId(_history.CompletedStateCommitId);

    private void Append(string kind, string? actorId, long? contextSequence, object payload) =>
        _journal.Append(kind, RunId, BranchId, actorId, contextSequence, payload);

    private static string? LocationHint(string? path, string? store) {
        if (path is null) { return null; }
        string absolute = Path.GetFullPath(path);
        if (store is null) { return absolute; }
        string relative = Path.GetRelativePath(Path.GetFullPath(store), absolute);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative) ? relative : absolute;
    }
}

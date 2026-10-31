using System.Security.Cryptography;
using System.Text.Json;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.CharacterGym;
using DramaBoard.Microtown.Completion;

namespace DramaBoard.Microtown.Cli.Gym;

/// <summary>Town's host evidence adapter. The game E/S history alone decides whether an operation happened.</summary>
/// <remarks>Recover pending E before calling ReconcileCommitted. The first reconciliation closes old uncommitted
/// inputs as interrupted; later calls record newly completed operations. No input is ever replayed from this log.</remarks>
public sealed class TownGymSession : IDisposable {
    private readonly TownDurableHistory _history;
    private readonly GymJournal _journal;
    private readonly Dictionary<string, GymRecord> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GymRecord> _received = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvedAtOpen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _materials = new(StringComparer.Ordinal);
    private bool _coldReconciliation = true;
    private bool _stopped;
    private string _lastState;

    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string BranchId { get; }
    public static string JournalPath(string store) => Path.Combine(store, "gym", "events.jsonl");

    /// <summary>Records the resolved public model/connection identity after lazy binding, never credentials or endpoint configuration.</summary>
    public void RecordBinding(string model, string connectionId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        Append("binding", null, new { Model = model, Connection = connectionId });
    }

    public TownGymSession(string store, TownDurableHistory history, TownGymRunOptions options) {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(options);
        _history = history;
        BranchId = TownDurableHistory.FormatBranchId(history.BranchId);
        _lastState = TownDurableHistory.FormatCommitId(history.CompletedStateCommitId);
        string journalPath = JournalPath(store);
        // Validate foreign evidence before opening the writer: Open may repair an incomplete tail.
        if (File.Exists(journalPath)) { ValidateGameIdentity(GymJournal.Read(journalPath).Records); }
        _journal = GymJournal.Open(journalPath);
        try {
            ValidateGameIdentity(_journal.Records);
            foreach (GymRecord record in _journal.Records) {
                if (record.BranchId != BranchId) { continue; }
                if (record.Kind == "operation-received") {
                    if (!_received.TryAdd(Text(record, "OperationRecordId"), record)) {
                        throw new InvalidDataException("The Gym log contains duplicate received operation identities.");
                    }
                } else if (record.Kind == "operation") { _completed.Add(Text(record, "OperationRecordId")); }
            }
            _unresolvedAtOpen.UnionWith(_received.Keys.Where(id => !_completed.Contains(id)));
            Append("run-start", null, new {
                GameId = TownGameRevision.GameId, GameRevision = TownGameRevision.Current, _journal.StoreId,
                StateCommitId = _lastState,
                ForkedFromCommitId = history.CurrentBranch.ForkedFromCommitId is { } origin ? TownDurableHistory.FormatCommitId(origin) : null,
                HadPendingEvent = history.PendingEvent is not null,
                options.Model, options.Connection, options.MaxCalls, options.MaxOperations, options.FeedbackEnabled,
                options.MaxFailures, options.MaxContextCharacters, options.LlmEnabled, options.ExperimentNote,
                Bindings = history.State.Business.Characters.Select(actor => new {
                    ActorId = actor.Id, Controller = !options.LlmEnabled || options.HumanActor == actor.Id ? "Human" : "LLM"
                }).ToArray(),
                Presentation = "town/full/actor-observation", Schema = "Microtown.Root/1;Microtown.Event/1",
                Builds = new[] { typeof(TownGymSession).Assembly, typeof(TownWorld).Assembly,
                    typeof(TownLlmCharacterController).Assembly, typeof(LlmDecisionLoop).Assembly,
                    typeof(GymJournal).Assembly, typeof(DramaBoard.Goals.GoalSet).Assembly,
                    typeof(DramaBoard.Kernel.Simulation.KernelCursor).Assembly,
                    typeof(DramaBoard.Spatial.GraphSpatialState).Assembly, typeof(DramaBoard.Inventory.ItemInventory).Assembly,
                    typeof(Atelia.DurableGraph.Repository).Assembly }
                    .Select(assembly => new { Name = assembly.GetName().Name,
                        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray(),
                TailGapBytes = _journal.DiscardedTailBytes
            });
        } catch { _journal.Dispose(); throw; }
    }

    public void Trace(LlmControllerTrace trace) {
        string requestId = trace.RequestId ?? throw new InvalidOperationException("A recorded request requires its identity.");
        if (trace.Kind == LlmControllerTraceKind.Request) {
            TownGymBoundary boundary = TownGymBoundary.From(_history.CaptureDecisionBoundary(trace.CharacterId));
            if (boundary.DecisionSequence != trace.ContextSequence) { throw new InvalidOperationException("Model material and decision boundary differ."); }
            GymRecord recorded = Append("request", trace.CharacterId, new {
                RequestId = requestId, trace.CallNumber, trace.ContextSequence, Boundary = boundary,
                trace.Request.ModelId, trace.Request.PromptPrefix.SystemPrompt,
                OutputContract = GymMessageProjection.Contract(trace.Request.PromptPrefix.OutputContract),
                Shared = trace.Request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(),
                Tail = trace.Request.TailMessages.Select(GymMessageProjection.Message).ToArray()
            });
            _requests.Add(requestId, recorded);
        } else {
            GymRecord request = Request(requestId, trace.CharacterId, trace.ContextSequence);
            if (trace.Kind == LlmControllerTraceKind.Feedback) {
                Append("feedback-receipt", trace.CharacterId, new { RequestId = requestId, trace.FeedbackId,
                    Status = "saved; delivery is evidenced by the next request" });
            } else {
                Append("response", trace.CharacterId, new {
                    RequestId = requestId, trace.CallNumber, trace.ContextSequence, Stage = trace.Kind.ToString(),
                    trace.ProtocolError, trace.Operation, Invocation = trace.Result?.Invocation,
                    Termination = trace.Result?.Termination, Usage = trace.Result?.Usage,
                    Blocks = trace.Result?.Message.Blocks.Select(block => GymMessageProjection.Runtime(block)).ToArray()
                });
                if (trace.Operation is { } operation) {
                    RecordOperationReceived(trace.CharacterId,
                        new(operation.RawInput, operation.ToolCallId, operation.OperationRecordId), request);
                }
            }
        }
    }

    public ValueTask<string> SaveFeedback(LlmFeedbackSubmission submission, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        GymRecord request = Request(submission.RequestId, submission.CharacterId, submission.ContextSequence);
        if (request.Payload.GetProperty("CallNumber").GetInt32() != submission.CallNumber) {
            throw new InvalidOperationException("Feedback call identity differs from its request.");
        }
        GymRecord? existing = _journal.Records.SingleOrDefault(record => record.Kind == "feedback" &&
            Text(record, "RequestId") == submission.RequestId &&
            record.Payload.GetProperty("Submission").GetProperty("ToolCallId").GetString() == submission.ToolCallId);
        if (existing is not null) {
            if (existing.Payload.GetProperty("Submission").Deserialize<LlmFeedbackSubmission>() != submission) {
                throw new InvalidOperationException("Feedback identity was reused with different content.");
            }
            return ValueTask.FromResult(Text(existing, "FeedbackId"));
        }
        string id = Guid.NewGuid().ToString("N");
        // GymJournal.Append flushes to disk before this stable receipt is returned to the tool loop.
        Append("feedback", submission.CharacterId, new {
            FeedbackId = id, submission.RequestId, RequestSequence = request.Sequence,
            Boundary = request.Payload.GetProperty("Boundary"), Submission = submission
        });
        return ValueTask.FromResult(id);
    }

    public void RecordOperationReceived(string actorId, TownControllerOperation operation) => RecordOperationReceived(actorId, operation, null);

    private void RecordOperationReceived(string actorId, TownControllerOperation operation, GymRecord? request) {
        operation.Validate();
        TownGymBoundary boundary = TownGymBoundary.From(_history.CaptureDecisionBoundary(actorId));
        if (request is not null && request.Payload.GetProperty("Boundary").Deserialize<TownGymBoundary>() != boundary) {
            throw new InvalidOperationException("The game moved during the recorded model request.");
        }
        if (_received.TryGetValue(operation.OperationRecordId, out GymRecord? existing)) {
            if (existing.ActorId != actorId || existing.Payload.GetProperty("Received").Deserialize<TownControllerOperation>() != operation ||
                existing.Payload.GetProperty("Boundary").Deserialize<TownGymBoundary>() != boundary) {
                throw new InvalidOperationException("Operation identity was reused at another boundary or with different contents.");
            }
            return;
        }
        _received.Add(operation.OperationRecordId, Append("operation-received", actorId, new {
            operation.OperationRecordId, RequestId = request is null ? null : Text(request, "RequestId"),
            Received = operation, Boundary = boundary
        }));
    }

    public void ReconcileCommitted() {
        if (_history.PendingEvent is not null) { throw new InvalidOperationException("Recover pending game work before reconciling Gym results."); }
        _lastState = TownDurableHistory.FormatCommitId(_history.CompletedStateCommitId);
        string[] pending = _received.Keys.Where(id => !_completed.Contains(id)).ToArray();
        foreach (TownCommittedOperation committed in _history.ReadCommittedOperations(pending)) {
            TownOperationReceipt receipt = committed.Receipt;
            string id = receipt.Operation.OperationRecordId;
            GymRecord source = _received[id];
            if (source.ActorId != receipt.ActorId || source.Payload.GetProperty("Received").Deserialize<TownControllerOperation>() != receipt.Operation) {
                throw new InvalidDataException("Committed game receipt disagrees with its recorded controller output.");
            }
            Append("operation", receipt.ActorId, new {
                OperationRecordId = id, RequestId = OptionalText(source, "RequestId"), Received = receipt.Operation,
                Result = new { receipt.Operation.ToolCallId, Status = receipt.Accepted ? "Succeeded" : "Rejected",
                    Result = receipt.Result, DomainEvent = TownDurableHistory.FormatCommitId(committed.EventCommitId),
                    ExactResult = true, Receipt = receipt },
                StateCommitId = TownDurableHistory.FormatCommitId(committed.StateCommitId),
                RecordingStateCommitId = _lastState, Recovered = source.RunId != RunId,
                Delivery = "completed game evidence; not proof that a character or model saw this receipt"
            });
            _completed.Add(id);
        }
        if (_coldReconciliation) {
            foreach (string id in _unresolvedAtOpen.Where(id => !_completed.Contains(id))) {
                GymRecord source = _received[id];
                Append("operation", source.ActorId, new {
                    OperationRecordId = id, RequestId = OptionalText(source, "RequestId"), Received = source.Payload.GetProperty("Received"),
                    Result = new { Status = "Interrupted", Result = "No completed game effect found after pending recovery; input was not replayed.",
                        DomainEvent = (string?)null, ExactResult = false }, StateCommitId = _lastState
                });
                _completed.Add(id);
            }
            _coldReconciliation = false;
        }
        foreach (TownCharacter actor in _history.State.Business.Characters) {
            string material = TownTextRenderer.Render(TownObservation.Capture(_history.State, actor.Id));
            if (_materials.GetValueOrDefault(actor.Id) == material) { continue; }
            Append(actor.Failed || _history.State.Business.Ended ? "terminal" : "perception", actor.Id,
                new { StateCommitId = _lastState, Material = material,
                    Delivery = "actor-visible material at the boundary; not proof of a model request or human display" });
            _materials[actor.Id] = material;
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

    private GymRecord Append(string kind, string? actor, object payload) => _journal.Append(kind, RunId, BranchId, actor, payload);

    internal static void ValidateGameIdentity(IEnumerable<GymRecord> records) {
        foreach (GymRecord record in records.Where(record => record.Kind == "run-start")) {
            if (record.Payload.ValueKind != JsonValueKind.Object ||
                !record.Payload.TryGetProperty("GameId", out JsonElement game) ||
                game.ValueKind != JsonValueKind.String || game.GetString() != TownGameRevision.GameId) {
                throw new InvalidDataException("The selected Gym journal is not a Microtown journal.");
            }
        }
    }
    private GymRecord Request(string id, string actor, long decision) => _requests.TryGetValue(id, out GymRecord? request) &&
        request.ActorId == actor && request.Payload.GetProperty("ContextSequence").GetInt64() == decision
            ? request : throw new InvalidOperationException("No matching recorded model request.");
    private static string Text(GymRecord record, string property) => record.Payload.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"Gym record lacks {property}.");
    private static string? OptionalText(GymRecord record, string property) => record.Payload.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

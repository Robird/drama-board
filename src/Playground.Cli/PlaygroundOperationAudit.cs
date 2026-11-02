using DramaBoard.Playground;

internal enum PlaygroundOperationStatus { Succeeded, Rejected, Interrupted }

internal sealed record PlaygroundOperationReceipt(string OperationRecordId, string BatchId, string ToolCallId,
    string ActorId, long? ContextSequence, string ToolName, string RawInput);

internal sealed record PlaygroundOperationResult(string ToolCallId, PlaygroundOperationStatus Status, string Result,
    string? WorldVersion, string? DomainEvent, bool ExactResult);

internal sealed record PlaygroundOperationEntry(PlaygroundOperationReceipt Received, PlaygroundOperationResult? Completed);

internal sealed record PlaygroundOperationCompletion(string OperationRecordId, PlaygroundOperationResult Completed, bool Recovered = false);

/// <summary>A completed domain E/S boundary, supplied after pending-event recovery. Never an execution request.</summary>
internal sealed record PlaygroundOperationCommit(string OperationRecordId, string ToolCallId, string ActorId,
    string Result, string WorldVersion, string DomainEvent, PlaygroundOperationDisposition Disposition) {
    internal static PlaygroundOperationStatus StatusOf(PlaygroundOperationDisposition disposition) => disposition switch {
        PlaygroundOperationDisposition.Succeeded => PlaygroundOperationStatus.Succeeded,
        PlaygroundOperationDisposition.Rejected => PlaygroundOperationStatus.Rejected,
        _ => throw new InvalidDataException("The game history contains an unknown operation disposition."),
    };
}

/// <summary>Branch-bound operation receipt/result semantics over the shared host journal; owns no file.</summary>
internal sealed class PlaygroundOperationAudit {
    private readonly PlaygroundHostJournal _journal;
    private readonly PlaygroundOperationIndex _index;
    private readonly string _branchId;
    private readonly string _runId;

    public PlaygroundOperationAudit(PlaygroundHostJournal journal, string branchId, string runId) {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _journal = journal;
        _branchId = branchId;
        _runId = runId;
        _index = journal.Operations(branchId);
    }

    public IReadOnlyList<PlaygroundOperationEntry> Entries { get { _journal.RequireUsable(); return _index.Entries; } }
    public IReadOnlyList<string> PendingOperationRecordIds { get { _journal.RequireUsable(); return _index.PendingOperationRecordIds; } }

    /// <summary>Persist before parsing or executing input. Retransmitting an existing ID must preserve every input field.</summary>
    public PlaygroundOperationReceipt Receive(string batchId, string toolCallId, string actorId, long? contextSequence,
        string toolName, string rawInput, string? operationRecordId = null) {
        _journal.RequireUsable();
        var received = new PlaygroundOperationReceipt(operationRecordId ?? Guid.NewGuid().ToString("N"), batchId,
            toolCallId, actorId, contextSequence, toolName, rawInput);
        ValidateReceipt(received);
        if (_index.TryGet(received.OperationRecordId, out var existing)) {
            if (existing!.Received != received) { throw new InvalidOperationException("The operation record was reused with different input or identity."); }
            return existing.Received;
        }
        if (_index.HasCall(batchId, toolCallId)) { throw new InvalidOperationException("A tool-call ID already exists in this invocation batch."); }
        _journal.Append("operation-received", _runId, _branchId, actorId, contextSequence, received);
        return _index.Entries[^1].Received;
    }

    /// <summary>Persist before delivering a result. An identical repeat returns the saved result without appending.</summary>
    public PlaygroundOperationResult Complete(string operationRecordId, PlaygroundOperationStatus status, string result,
        string? worldVersion = null, string? domainEvent = null, bool exactResult = true, bool recovered = false) {
        _journal.RequireUsable();
        if (!_index.TryGet(operationRecordId, out var entry)) { throw new InvalidOperationException("No receipt exists for this operation."); }
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(status)) { throw new ArgumentOutOfRangeException(nameof(status)); }
        var completed = new PlaygroundOperationResult(entry!.Received.ToolCallId, status, result, worldVersion, domainEvent, exactResult);
        if (entry.Completed is not null) {
            if (entry.Completed != completed) { throw new InvalidOperationException("A saved operation result cannot be rewritten."); }
            return entry.Completed;
        }
        _journal.Append("operation-completed", _runId, _branchId, entry.Received.ActorId, entry.Received.ContextSequence,
            new PlaygroundOperationCompletion(operationRecordId, completed, recovered));
        _index.TryGet(operationRecordId, out var installed);
        return installed!.Completed!;
    }

    /// <summary>Call only after authoritative game recovery. Reconciles this branch's own receipts, never ancestors.
    /// Missing query/unpublished results become Interrupted; no original input is executed.</summary>
    public void Reconcile(IEnumerable<PlaygroundOperationCommit> committedOperations) {
        _journal.RequireUsable();
        ArgumentNullException.ThrowIfNull(committedOperations);
        var commits = new Dictionary<string, PlaygroundOperationCommit>(StringComparer.Ordinal);
        foreach (var commit in committedOperations) {
            _ = PlaygroundOperationCommit.StatusOf(commit.Disposition);
            if (!commits.TryAdd(commit.OperationRecordId, commit) && commits[commit.OperationRecordId] != commit) {
                throw new InvalidDataException("The game history contains conflicting operation references.");
            }
            if (_index.TryGet(commit.OperationRecordId, out var entry) &&
                (entry!.Received.ToolCallId != commit.ToolCallId || entry.Received.ActorId != commit.ActorId)) {
                throw new InvalidDataException("The game history does not match the saved operation identity.");
            }
        }
        foreach (string recordId in PendingOperationRecordIds) {
            if (commits.TryGetValue(recordId, out var commit)) {
                Complete(recordId, PlaygroundOperationCommit.StatusOf(commit.Disposition), commit.Result,
                    commit.WorldVersion, commit.DomainEvent, exactResult: false, recovered: true);
            } else {
                Complete(recordId, PlaygroundOperationStatus.Interrupted,
                    "操作结果未保存，且没有已提交的游戏事实；未自动重做。", exactResult: false, recovered: true);
            }
        }
    }

    internal static void ValidateReceipt(PlaygroundOperationReceipt received) {
        ArgumentException.ThrowIfNullOrWhiteSpace(received.OperationRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.BatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.ToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(received.ToolName);
        ArgumentNullException.ThrowIfNull(received.RawInput);
        if (received.ContextSequence is <= 0) { throw new ArgumentOutOfRangeException(nameof(received.ContextSequence)); }
    }
}

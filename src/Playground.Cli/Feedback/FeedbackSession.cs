using DramaBoard.CharacterControl.Completion;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli;

/// <summary>Serial host feedback; neither saving nor reading advances the game.</summary>
internal sealed class FeedbackSession : IDisposable {
    private readonly FeedbackJournal _journal;
    private readonly PlaygroundDurableHistory? _history;
    private readonly string? _storePath;
    public string RunId { get; }

    public static string JournalPath(string directory) => Path.Combine(directory, "feedback.jsonl");

    public FeedbackSession(string path, PlaygroundDurableHistory? history = null, string? runId = null, string? storePath = null) {
        _journal = FeedbackJournal.Open(path);
        _history = history;
        _storePath = storePath is null ? null : Path.GetFullPath(storePath);
        RunId = runId ?? Guid.NewGuid().ToString("N");
    }

    public ValueTask<string> SaveHuman(PlaygroundDecisionContext context, string text, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        return Save(context.InteractionState.ActorId, context.Context.ContextSequence, text, FeedbackSource.HumanConsole, null, cancellationToken);
    }

    public ValueTask<string> SaveLlm(LlmFeedbackSubmission submission, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(submission);
        return Save(submission.CharacterId, submission.ContextSequence, submission.Text, FeedbackSource.Llm,
            new(submission.RequestId, submission.CallNumber, submission.ToolCallId), cancellationToken);
    }

    private ValueTask<string> Save(string actorId, long contextSequence, string text, FeedbackSource source,
        FeedbackModel? model, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > FeedbackRecord.MaxTextCharacters) { throw new ArgumentException($"Feedback text exceeds {FeedbackRecord.MaxTextCharacters} characters.", nameof(text)); }
        PlaygroundDecisionBoundary? boundary = _history?.CaptureDecisionBoundary(actorId);
        if (boundary is not null && boundary.ContextSequence != contextSequence) {
            throw new InvalidOperationException("Feedback context does not match the current decision boundary.");
        }
        var record = new FeedbackRecord(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, RunId,
            actorId, source, contextSequence, text, boundary, model, _storePath);
        return ValueTask.FromResult(_journal.Append(record));
    }

    public void Dispose() => _journal.Dispose();
}

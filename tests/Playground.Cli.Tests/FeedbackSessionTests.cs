using DramaBoard.CharacterControl.Completion;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class FeedbackSessionTests {
    [Fact]
    public async Task Ephemeral_feedback_needs_neither_gym_nor_model_trace() {
        using var files = new Files();
        using (var session = new FeedbackSession(files.Path)) {
            var submission = new LlmFeedbackSubmission("request", 1, "a", 1, "tool", "仅含正文\n并保留原文");
            string receipt = await session.SaveLlm(submission, default);
            FeedbackRecord record = Assert.Single(FeedbackJournal.Read(files.Path));
            Assert.Equal(receipt, record.FeedbackId);
            Assert.Equal(submission.Text, record.Text);
            Assert.Equal(session.RunId, record.RunId);
            Assert.Null(record.Boundary);
            Assert.Equal(new FeedbackModel("request", 1, "tool"), record.Model);
        }
    }

    [Fact]
    public async Task Durable_human_and_model_feedback_attach_exact_boundary_and_reject_wrong_context() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateSurvival();
        var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(memory);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        using var history = PlaygroundDurableHistory.Create(files.Store, kernel.World, kernel.Cursor);
        PlaygroundDecisionBoundary boundary = history.CaptureDecisionBoundary("a");
        using var session = new FeedbackSession(files.Path, history, "run", files.Store);
        PlaygroundDecisionContext context = PlaygroundDecisionContext.Create(history.State, "a");
        await session.SaveHuman(context, "人类体验", default);
        await session.SaveLlm(new("request", 2, "a", boundary.ContextSequence, "tool", "模型体验"), default);
        Assert.All(FeedbackJournal.Read(files.Path), record => {
            Assert.Equal(boundary, record.Boundary);
            Assert.Equal(System.IO.Path.GetFullPath(files.Store), record.StorePath);
        });
        Assert.Throws<InvalidOperationException>(() => session.SaveLlm(
            new("request", 3, "a", boundary.ContextSequence + 1, "stale", "过期"), default));
        Assert.Throws<InvalidOperationException>(() => session.SaveLlm(
            new("request", 3, "b", boundary.ContextSequence, "other", "错误角色"), default));
        Assert.Equal(2, FeedbackJournal.Read(files.Path).Count);
        Assert.Equal(boundary, history.CaptureDecisionBoundary("a"));
    }

    [Fact]
    public void Cancellation_and_oversized_text_create_no_file() {
        using var files = new Files();
        using var session = new FeedbackSession(files.Path);
        var submission = new LlmFeedbackSubmission("request", 1, "a", 1, "tool", "正文");
        Assert.Throws<OperationCanceledException>(() => session.SaveLlm(submission, new CancellationToken(true)));
        Assert.Throws<ArgumentException>(() => session.SaveLlm(submission with { Text = new string('x', 4001) }, default));
        Assert.False(File.Exists(files.Path));
    }

    private sealed class Files : IDisposable {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "feedback-session-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "feedback.jsonl");
        public string Store => System.IO.Path.Combine(_root, "game");
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
